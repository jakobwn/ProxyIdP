using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ProxyIdP.Tests;

public sealed class CustomUiFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"proxyidp-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("environment", "Development");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"Data Source={_db}",
            ["Proxy:Issuer"] = "https://proxy.test",
            ["Ui:CustomUi:Enabled"] = "true",
            ["Ui:CustomUi:Path"] = FindExampleUiPath()
        }));
    }

    private static string FindExampleUiPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ProxyIdP.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "examples", "customui", "customui");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_db); } catch { }
    }
}

public static class LoginTestHelpers
{
    public const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    public static async Task<string> NewRidAsync(HttpClient http)
    {
        var r = await http.GetAsync($"/connect/authorize?client_id=dev-client&redirect_uri=https://localhost:5001/signin-oidc&response_type=code&scope=openid&state=s&code_challenge={Challenge}&code_challenge_method=S256");
        return r.Headers.Location!.ToString()["/login?rid=".Length..];
    }
}

public class LoginStyleTests : IClassFixture<ProxyFactory>
{
    private readonly HttpClient _http;

    public LoginStyleTests(ProxyFactory f) =>
        _http = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task LoginPage_LinksStylesheet_AndHasClasses()
    {
        var rid = await LoginTestHelpers.NewRidAsync(_http);
        var html = await _http.GetStringAsync($"/login?rid={rid}");
        Assert.Contains("href=\"/style/login.css\"", html);
        Assert.Contains("login-form", html);
        Assert.Contains("login-button-primary", html);
    }

    [Fact]
    public async Task StylesheetEndpoint_ReturnsCss()
    {
        var r = await _http.GetAsync("/style/login.css");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/css", r.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task CustomUiDisabled_ApiEndpointsReturn404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/login/config?rid=x")).StatusCode);
        var r = await _http.PostAsJsonAsync("/api/login", new { rid = "x" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}

public class CustomUiTests : IClassFixture<CustomUiFactory>
{
    private readonly HttpClient _http;

    public CustomUiTests(CustomUiFactory f) =>
        _http = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Login_ServesCustomIndexHtml()
    {
        var rid = await LoginTestHelpers.NewRidAsync(_http);
        var html = await _http.GetStringAsync($"/login?rid={rid}");
        Assert.Contains("/customui/login.js", html);
    }

    [Fact]
    public async Task Login_WithInvalidRid_Returns400()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/login?rid=bogus")).StatusCode);
    }

    [Fact]
    public async Task CustomUiStaticFiles_AreServed()
    {
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/customui/login.js")).StatusCode);
    }

    [Fact]
    public async Task ApiConfig_ReturnsAntiforgeryAndIdps()
    {
        var rid = await LoginTestHelpers.NewRidAsync(_http);
        var doc = await _http.GetFromJsonAsync<JsonElement>($"/api/login/config?rid={rid}");
        Assert.Equal(rid, doc.GetProperty("rid").GetString());
        Assert.Equal("X-CSRF-TOKEN", doc.GetProperty("antiforgery").GetProperty("headerName").GetString());
        Assert.Equal(JsonValueKind.Array, doc.GetProperty("idps").ValueKind);
    }

    [Fact]
    public async Task ApiConfig_InvalidRid_Returns400()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/api/login/config?rid=bogus")).StatusCode);
    }

    [Fact]
    public async Task ApiLogin_WithoutAntiforgery_Returns400()
    {
        var r = await _http.PostAsJsonAsync("/api/login", new { rid = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task ApiLogin_WithTokenButInvalidRid_ReturnsError()
    {
        var rid = await LoginTestHelpers.NewRidAsync(_http);
        var cfg = await _http.GetFromJsonAsync<JsonElement>($"/api/login/config?rid={rid}");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/login") { Content = JsonContent.Create(new { rid = "bogus" }) };
        req.Headers.Add("X-CSRF-TOKEN", cfg.GetProperty("antiforgery").GetProperty("requestToken").GetString());
        var r = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("expired or invalid", await r.Content.ReadAsStringAsync());
    }
}
