using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyIdP.Options;
using ProxyIdP.Services;

namespace ProxyIdP.Tests;

public class IdpResolverAndPkceTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            return await respond(request).WaitAsync(ct);
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static UpstreamIdpOptions Idp(string id, Action<UpstreamIdpOptions>? cfg = null)
    {
        var o = new UpstreamIdpOptions { Id = id, Type = UpstreamIdpType.GenericOidc, DisplayName = id, ClientId = "c", Authority = "https://idp.example" };
        cfg?.Invoke(o);
        return o;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static (IdpResolver, FakeHandler) Resolver(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond,
        Action<IdpResolverOptions>? cfg = null, params UpstreamIdpOptions[] idps)
    {
        var handler = new FakeHandler(respond);
        var opts = new IdpResolverOptions { Endpoint = "https://resolver.example/lookup" };
        cfg?.Invoke(opts);
        var registry = new UpstreamIdpRegistry(idps.Length > 0 ? idps : [Idp("corp", o => o.EmailDomains = ["contoso.com"]), Idp("other")]);
        return (new IdpResolver(registry, new FakeFactory(handler), Microsoft.Extensions.Options.Options.Create(opts), NullLogger<IdpResolver>.Instance), handler);
    }

    // --- Validation ---

    [Fact]
    public void NoEmailDomains_IsValid() =>
        Assert.Empty(Idp("a", o => o.EmailDomains = []).Validate());

    [Fact]
    public void NoSecret_WithPkce_IsValid() =>
        Assert.Empty(Idp("a", o => { o.ClientSecret = null; o.UsePkce = true; }).Validate());

    [Fact]
    public void NoSecret_WithoutPkce_IsRejected() =>
        Assert.Throws<InvalidOperationException>(() => new UpstreamIdpRegistry([Idp("a", o => { o.ClientSecret = null; o.UsePkce = false; })]));

    [Fact]
    public void Secret_WithoutPkce_IsValid() =>
        Assert.Empty(Idp("a", o => { o.ClientSecret = "s"; o.UsePkce = false; }).Validate());

    // --- Default IdP and login_hint ---

    [Fact]
    public void Default_ReturnsFlaggedIdp()
    {
        var reg = new UpstreamIdpRegistry([Idp("a"), Idp("b", o => o.IsDefault = true)]);
        Assert.Equal("b", reg.Default?.Id);
    }

    [Fact]
    public void Default_IsNull_WhenNoneFlagged() =>
        Assert.Null(new UpstreamIdpRegistry([Idp("a"), Idp("b")]).Default);

    [Fact]
    public void TwoDefaults_AreRejected() =>
        Assert.Throws<InvalidOperationException>(() =>
            new UpstreamIdpRegistry([Idp("a", o => o.IsDefault = true), Idp("b", o => o.IsDefault = true)]));

    [Fact]
    public void DisabledDefault_IsRejected() =>
        Assert.Throws<InvalidOperationException>(() =>
            new UpstreamIdpRegistry([Idp("a", o => { o.IsDefault = true; o.Enabled = false; })]));

    [Fact]
    public void AuthorizeUrl_ContainsLoginHint_WhenGiven()
    {
        var url = UpstreamIdpClient.BuildAuthorizeUrl(Idp("a"), "https://idp.example/authorize", "https://proxy/cb", "st", "no", "verifier", "jane@contoso.com");
        Assert.Equal("jane@contoso.com", Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(url).Query)["login_hint"].ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AuthorizeUrl_OmitsLoginHint_WhenEmpty(string? hint)
    {
        var url = UpstreamIdpClient.BuildAuthorizeUrl(Idp("a"), "https://idp.example/authorize", "https://proxy/cb", "st", "no", "verifier", hint);
        Assert.DoesNotContain("login_hint", url);
    }

    // --- Authorize URL PKCE ---

    [Fact]
    public void AuthorizeUrl_ContainsPkce_WhenEnabled()
    {
        var url = UpstreamIdpClient.BuildAuthorizeUrl(Idp("a"), "https://idp.example/authorize", "https://proxy/cb", "st", "no", "verifier");
        Assert.Contains("code_challenge=", url);
        Assert.Contains("code_challenge_method=S256", url);
    }

    [Fact]
    public void AuthorizeUrl_OmitsPkce_WhenDisabled()
    {
        var url = UpstreamIdpClient.BuildAuthorizeUrl(Idp("a", o => { o.UsePkce = false; o.ClientSecret = "s"; }),
            "https://idp.example/authorize", "https://proxy/cb", "st", "no", "verifier");
        Assert.DoesNotContain("code_challenge", url);
    }

    // --- Resolver ---

    [Fact]
    public async Task DomainMatch_WinsWithoutCallingApi()
    {
        var (resolver, handler) = Resolver(_ => Task.FromResult(Json("{\"idpId\":\"other\"}")));
        var result = await resolver.ResolveAsync("bob@contoso.com", default);
        Assert.Equal("corp", result?.Id);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ApiMatch_ReturnsConfiguredIdp_ViaPost()
    {
        var (resolver, handler) = Resolver(_ => Task.FromResult(Json("{\"idpId\":\"other\"}")));
        var result = await resolver.ResolveAsync("bob@unknown.com", default);
        Assert.Equal("other", result?.Id);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("bob@unknown.com", JsonDocument.Parse(handler.Bodies[0]!).RootElement.GetProperty("email").GetString());
    }

    [Fact]
    public async Task ApiMatch_WorksViaGet_WithCustomParameter()
    {
        var (resolver, handler) = Resolver(_ => Task.FromResult(Json("{\"idpId\":\"other\"}")), o => { o.Method = "GET"; o.EmailParameter = "mail"; });
        var result = await resolver.ResolveAsync("bob@unknown.com", default);
        Assert.Equal("other", result?.Id);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("bob@unknown.com", System.Web.HttpUtility.ParseQueryString(handler.Requests[0].RequestUri!.Query)["mail"]);
    }

    [Fact]
    public async Task ApiKey_IsSentInConfiguredHeader()
    {
        var (resolver, handler) = Resolver(_ => Task.FromResult(Json("{\"idpId\":\"other\"}")), o => { o.ApiKey = "secret"; o.ApiKeyHeader = "X-Key"; });
        await resolver.ResolveAsync("bob@unknown.com", default);
        Assert.Equal("secret", handler.Requests[0].Headers.GetValues("X-Key").Single());
    }

    [Fact]
    public async Task UnknownIdpId_IsIgnored()
    {
        var (resolver, _) = Resolver(_ => Task.FromResult(Json("{\"idpId\":\"nope\"}")));
        Assert.Null(await resolver.ResolveAsync("bob@unknown.com", default));
    }

    [Fact]
    public async Task DisabledIdp_IsIgnored()
    {
        var (resolver, _) = Resolver(_ => Task.FromResult(Json("{\"idpId\":\"off\"}")), null, Idp("off", o => o.Enabled = false), Idp("on"));
        Assert.Null(await resolver.ResolveAsync("bob@unknown.com", default));
    }

    [Theory]
    [InlineData("{\"idpId\":null}")]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public async Task EmptyNullOrBadBody_ReturnsNull(string body)
    {
        var (resolver, _) = Resolver(_ => Task.FromResult(Json(body)));
        Assert.Null(await resolver.ResolveAsync("bob@unknown.com", default));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NonSuccessStatus_ReturnsNull(HttpStatusCode code)
    {
        var (resolver, _) = Resolver(_ => Task.FromResult(Json("{\"idpId\":\"other\"}", code)));
        Assert.Null(await resolver.ResolveAsync("bob@unknown.com", default));
    }

    [Fact]
    public async Task Timeout_ReturnsNull()
    {
        var (resolver, _) = Resolver(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return Json("{\"idpId\":\"other\"}");
        }, o => o.TimeoutSeconds = 1);
        Assert.Null(await resolver.ResolveAsync("bob@unknown.com", default));
    }

    [Fact]
    public async Task HttpException_ReturnsNull()
    {
        var (resolver, _) = Resolver(_ => throw new HttpRequestException("boom"));
        Assert.Null(await resolver.ResolveAsync("bob@unknown.com", default));
    }

    [Fact]
    public async Task ApiDisabled_WhenNoEndpoint()
    {
        var (resolver, handler) = Resolver(_ => Task.FromResult(Json("{\"idpId\":\"other\"}")), o => o.Endpoint = "");
        Assert.Null(await resolver.ResolveAsync("bob@unknown.com", default));
        Assert.Empty(handler.Requests);
    }
}
