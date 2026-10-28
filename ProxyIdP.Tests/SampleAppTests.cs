using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ProxyIdP.Tests;

public sealed class SampleAppFactory : WebApplicationFactory<global::Program>
{
    public const string Authority = "https://proxy.test";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("environment", "Development");
        builder.UseSetting("ProxyIdP:Authority", Authority);
        builder.ConfigureServices(services =>
            services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                // Avoid fetching discovery from a running proxy.
                o.Configuration = o.Configuration ?? new OpenIdConnectConfiguration
                {
                    Issuer = Authority,
                    AuthorizationEndpoint = $"{Authority}/connect/authorize",
                    TokenEndpoint = $"{Authority}/connect/token",
                    EndSessionEndpoint = $"{Authority}/connect/endsession"
                };
                o.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(o.Configuration);
            }));
    }
}

public class SampleAppTests : IClassFixture<SampleAppFactory>
{
    private readonly HttpClient _http;

    public SampleAppTests(SampleAppFactory factory) =>
        _http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task StartPage_ShowsSignInAndAuthority()
    {
        var r = await _http.GetAsync("/");
        var html = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("href=\"/signin\"", html);
        Assert.Contains(SampleAppFactory.Authority, html);
    }

    [Fact]
    public async Task Profile_Anonymous_RedirectsToProxyAuthorize_WithPkce()
    {
        var r = await _http.GetAsync("/profile");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var location = r.Headers.Location!.ToString();
        Assert.StartsWith($"{SampleAppFactory.Authority}/connect/authorize", location);
        Assert.Contains("client_id=dev-client", location);
        Assert.Contains("response_type=code", location);
        Assert.Contains("code_challenge_method=S256", location);
    }

    [Fact]
    public async Task SignIn_RedirectsToProxyAuthorize()
    {
        var r = await _http.GetAsync("/signin");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.StartsWith($"{SampleAppFactory.Authority}/connect/authorize", r.Headers.Location!.ToString());
    }
}
