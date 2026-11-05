using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ProxyIdP.Data;
using ProxyIdP.Options;
using ProxyIdP.Services;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace ProxyIdP.Tests;

public class ResolverAuthTests
{
    private sealed class Handler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeKeys : ISigningKeyStore
    {
        public RsaSecurityKey Key { get; } = new(RSA.Create(2048)) { KeyId = "test-key" };
        public Task EnsureKeysAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<SigningCredentials> GetSigningCredentialsAsync(CancellationToken ct = default) =>
            Task.FromResult(new SigningCredentials(Key, SecurityAlgorithms.RsaSha256));
        public Task<IReadOnlyList<SecurityKey>> GetValidationKeysAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SecurityKey>>([Key]);
        public Task<object> GetJwksAsync(CancellationToken ct = default) => Task.FromResult<object>(new { });
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static IdpResolverOptions Options(Action<IdpResolverOptions>? cfg = null)
    {
        var o = new IdpResolverOptions
        {
            Endpoint = "https://resolver.example/resolve",
            Authentication =
            {
                Mode = ResolverAuthMode.FederatedCredential,
                TokenEndpoint = "https://login.example/token",
                ClientId = "proxy",
                Subject = "proxy-subject"
            }
        };
        cfg?.Invoke(o);
        return o;
    }

    private static (ResolverTokenProvider Provider, FakeKeys Keys, Clock Clock) Provider(Handler handler, IdpResolverOptions opts)
    {
        var keys = new FakeKeys();
        var clock = new Clock(DateTimeOffset.UtcNow);
        var proxy = MsOptions.Create(new ProxyOptions { Issuer = "https://proxy.example/" });
        return (new ResolverTokenProvider(new Factory(handler), keys, MsOptions.Create(opts), proxy, clock, NullLogger<ResolverTokenProvider>.Instance), keys, clock);
    }

    private static Dictionary<string, string> Form(string? body) =>
        body!.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    [Fact]
    public async Task Federated_SendsSignedAssertionWithExpectedClaims()
    {
        var handler = new Handler((_, _) => Json("""{"access_token":"tok","expires_in":3600}"""));
        var (provider, keys, _) = Provider(handler, Options());

        var token = await provider.GetTokenAsync(false, default);

        Assert.Equal("tok", token);
        var form = Form(handler.Calls.Single().Body);
        Assert.Equal("client_credentials", form["grant_type"]);
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form["client_assertion_type"]);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(form["client_assertion"], new TokenValidationParameters
        {
            ValidIssuer = "https://proxy.example",
            ValidAudience = "api://AzureADTokenExchange",
            IssuerSigningKey = keys.Key
        });
        Assert.True(result.IsValid);
        var jwt = (JsonWebToken)result.SecurityToken;
        Assert.Equal("proxy-subject", jwt.Subject);
        Assert.False(string.IsNullOrEmpty(jwt.Id));
    }

    [Fact]
    public async Task ClientSecret_SendsSecret()
    {
        var handler = new Handler((_, _) => Json("""{"access_token":"tok","expires_in":3600}"""));
        var (provider, _, _) = Provider(handler, Options(o => { o.Authentication.Mode = ResolverAuthMode.ClientSecret; o.Authentication.ClientSecret = "s3cret"; }));

        await provider.GetTokenAsync(false, default);

        var form = Form(handler.Calls.Single().Body);
        Assert.Equal("s3cret", form["client_secret"]);
        Assert.False(form.ContainsKey("client_assertion"));
    }

    [Fact]
    public async Task AssertionFile_SendsFileContent()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "projected-token\n");
        try
        {
            var handler = new Handler((_, _) => Json("""{"access_token":"tok","expires_in":3600}"""));
            var (provider, _, _) = Provider(handler, Options(o => { o.Authentication.Mode = ResolverAuthMode.AssertionFile; o.Authentication.AssertionFilePath = path; }));

            await provider.GetTokenAsync(false, default);

            Assert.Equal("projected-token", Form(handler.Calls.Single().Body)["client_assertion"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Token_IsCached_UntilNearExpiry_ThenRefreshed()
    {
        var n = 0;
        var handler = new Handler((_, _) => Json($$"""{"access_token":"tok{{++n}}","expires_in":100}"""));
        var (provider, _, clock) = Provider(handler, Options());

        Assert.Equal("tok1", await provider.GetTokenAsync(false, default));
        Assert.Equal("tok1", await provider.GetTokenAsync(false, default));
        Assert.Single(handler.Calls);

        clock.Now = clock.Now.AddSeconds(80);
        Assert.Equal("tok2", await provider.GetTokenAsync(false, default));
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task ForceRefresh_RequestsNewToken()
    {
        var n = 0;
        var handler = new Handler((_, _) => Json($$"""{"access_token":"tok{{++n}}","expires_in":3600}"""));
        var (provider, _, _) = Provider(handler, Options());

        await provider.GetTokenAsync(false, default);
        Assert.Equal("tok2", await provider.GetTokenAsync(true, default));
    }

    [Fact]
    public async Task Resolver_SendsBearer_AndRefreshesOnce_On401()
    {
        var tokens = 0;
        var calls = new List<string?>();
        var handler = new Handler((req, _) =>
        {
            if (req.RequestUri!.AbsoluteUri.StartsWith("https://login.example"))
                return Json($$"""{"access_token":"tok{{++tokens}}","expires_in":3600}""");
            calls.Add(req.Headers.Authorization?.Parameter);
            return req.Headers.Authorization?.Parameter == "tok2" ? Json("""{"idpId":"corp"}""") : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        var opts = Options();
        var (provider, _, _) = Provider(handler, opts);
        var registry = new UpstreamIdpRegistry([new UpstreamIdpOptions { Id = "corp", Type = UpstreamIdpType.GenericOidc, DisplayName = "corp", ClientId = "c", Authority = "https://idp.example" }]);
        var resolver = new IdpResolver(registry, new Factory(handler), MsOptions.Create(opts), NullLogger<IdpResolver>.Instance, provider);

        var idp = await resolver.ResolveAsync("user@unknown.example", default);

        Assert.Equal("corp", idp?.Id);
        Assert.Equal(["tok1", "tok2"], calls);
    }

    [Fact]
    public async Task Resolver_ApiKeyMode_StillSendsApiKeyHeader()
    {
        var handler = new Handler((_, _) => Json("""{"idpId":"corp"}"""));
        var opts = new IdpResolverOptions { Endpoint = "https://resolver.example/resolve", ApiKey = "key" };
        var registry = new UpstreamIdpRegistry([new UpstreamIdpOptions { Id = "corp", Type = UpstreamIdpType.GenericOidc, DisplayName = "corp", ClientId = "c", Authority = "https://idp.example" }]);
        var resolver = new IdpResolver(registry, new Factory(handler), MsOptions.Create(opts), NullLogger<IdpResolver>.Instance);

        await resolver.ResolveAsync("user@unknown.example", default);

        Assert.Equal("key", handler.Calls.Single().Request.Headers.GetValues("X-Api-Key").Single());
    }

    [Theory]
    [InlineData(ResolverAuthMode.ClientSecret)]
    [InlineData(ResolverAuthMode.AssertionFile)]
    [InlineData(ResolverAuthMode.ApiKey)]
    public void Validate_RejectsMissingModeSettings(ResolverAuthMode mode) =>
        Assert.NotNull(Options(o => o.Authentication.Mode = mode).Validate(isDevelopment: true));

    [Fact]
    public void Validate_RequiresHttpsOutsideDevelopment()
    {
        var o = Options(x => x.Authentication.TokenEndpoint = "http://login.example/token");
        Assert.NotNull(o.Validate(isDevelopment: false));
        Assert.Null(o.Validate(isDevelopment: true));
    }

    [Fact]
    public void Validate_DisabledResolver_IsAlwaysValid() =>
        Assert.Null(new IdpResolverOptions { Authentication = { Mode = ResolverAuthMode.ClientSecret } }.Validate(false));
}
