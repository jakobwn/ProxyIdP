using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ProxyIdP.Tests;

public class EndpointTests : IClassFixture<ProxyFactory>
{
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private readonly HttpClient _http;

    public EndpointTests(ProxyFactory factory) =>
        _http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Discovery_ExposesCoreEndpoints()
    {
        var doc = await _http.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");
        Assert.Equal("https://proxy.test", doc.GetProperty("issuer").GetString());
        Assert.EndsWith("/.well-known/jwks.json", doc.GetProperty("jwks_uri").GetString());
        Assert.Equal("S256", doc.GetProperty("code_challenge_methods_supported")[0].GetString());
    }

    [Fact]
    public async Task Jwks_ContainsAtLeastTwoKeys_OnFreshDatabase()
    {
        var doc = await _http.GetFromJsonAsync<JsonElement>("/.well-known/jwks.json");
        var keys = doc.GetProperty("keys").EnumerateArray().ToList();
        Assert.True(keys.Count >= 2);
        Assert.Equal(keys.Count, keys.Select(k => k.GetProperty("kid").GetString()).Distinct().Count());
        Assert.All(keys, k => Assert.False(k.TryGetProperty("d", out _)));
    }

    [Fact]
    public async Task Authorize_UnknownClient_Returns400()
    {
        var r = await _http.GetAsync("/connect/authorize?client_id=nope&redirect_uri=https://x/cb&response_type=code");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Authorize_UnregisteredRedirectUri_Returns400()
    {
        var r = await _http.GetAsync($"/connect/authorize?client_id=dev-client&redirect_uri=https://evil/cb&response_type=code&scope=openid&code_challenge={Challenge}&code_challenge_method=S256");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Authorize_MissingPkce_RedirectsWithError()
    {
        var r = await _http.GetAsync("/connect/authorize?client_id=dev-client&redirect_uri=https://localhost:5001/signin-oidc&response_type=code&scope=openid&state=s");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("error=invalid_request", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Authorize_Valid_RedirectsToLogin()
    {
        var r = await _http.GetAsync($"/connect/authorize?client_id=dev-client&redirect_uri=https://localhost:5001/signin-oidc&response_type=code&scope=openid&state=s&code_challenge={Challenge}&code_challenge_method=S256");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.StartsWith("/login?rid=", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Token_InvalidCode_ReturnsInvalidGrant()
    {
        var r = await _http.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = "dev-client", ["code"] = "bogus",
            ["redirect_uri"] = "https://localhost:5001/signin-oidc", ["code_verifier"] = "x"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("invalid_grant", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UserInfo_WithoutToken_Returns401()
    {
        var r = await _http.GetAsync("/connect/userinfo");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Login_WithoutAntiforgery_Returns400()
    {
        var r = await _http.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["rid"] = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
