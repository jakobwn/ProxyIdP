using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProxyIdP.Plugins;

namespace ProxyIdP.ExamplePlugin;

/// <summary>Example plugin that implements every hook and enriches the token from the local API.</summary>
public sealed class ExamplePlugin : ProxyIdPPlugin
{
    public override string Name => "ExamplePlugin";
    public override int Order => 100;

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // The identity token issued by ProxyIdP for this plugin is sent directly; the local API accepts it (role "plugin").
        services.AddTransient(sp => new ProxyIdPTokenHandler(AcquireIdentityTokenAsync));
        services.AddHttpClient<LocalApiClient>(http =>
        {
            http.BaseAddress = new Uri(configuration["ApiBaseUrl"] ?? "http://localhost:5100");
            http.Timeout = TimeSpan.FromSeconds(5);
        }).AddHttpMessageHandler<ProxyIdPTokenHandler>();

        // Example: exchange the identity token for an Entra token via a federated identity credential instead.
        // services.AddTransient(sp => new EntraTokenHandler(
        //     sp.GetRequiredService<IHttpClientFactory>(),
        //     configuration["TenantId"]!, configuration["ClientId"]!, configuration["Scope"]!,
        //     AcquireIdentityTokenAsync));
        // ...and use .AddHttpMessageHandler<EntraTokenHandler>() on the client above.
    }

    private LocalApiClient Api => Services.GetRequiredService<LocalApiClient>();

    public override Task OnAuthorizationRequestAsync(AuthorizationRequestContext context, CancellationToken ct)
    {
        Logger.LogInformation("Authorization request from {Client} for scopes {Scopes}", context.ClientId, string.Join(' ', context.Scopes));
        return Task.CompletedTask;
    }

    public override Task OnUpstreamRedirectAsync(UpstreamRedirectContext context, CancellationToken ct)
    {
        context.AddQueryParameter("x-example", "1");
        Logger.LogInformation("Redirecting {Client} to upstream {Idp}", context.ClientId, context.IdpId);
        return Task.CompletedTask;
    }

    public override async Task OnUpstreamCallbackAsync(UpstreamCallbackContext context, CancellationToken ct)
    {
        Logger.LogInformation("Upstream {Idp} returned claims: {Types}", context.IdpId, string.Join(", ", context.Claims.Select(c => c.Type)));
        var email = context.Claims.FirstOrDefault(c => c.Type == "email")?.Value;
        if (email is null) return;
        var profile = await Api.GetProfileAsync(email, ct);
        if (profile is not null) Logger.LogInformation("User {Email} is in department {Department}", email, profile.Department);
    }

    public override async Task OnTokenIssuingAsync(TokenIssuingContext context, CancellationToken ct)
    {
        context.Claims.Add(new Claim("example_plugin", "true"));

        var email = context.Claims.FirstOrDefault(c => c.Type is "email" or "preferred_username")?.Value;
        if (email is null) return;
        var roles = await Api.GetRolesAsync(email, ct);
        if (roles is null) return;
        foreach (var role in roles) context.Claims.Add(new Claim("example_roles", role));
    }

    public override async Task OnTokenIssuedAsync(TokenIssuedContext context, CancellationToken ct)
    {
        var idp = context.Claims.FirstOrDefault(c => c.Type == "idp")?.Value;
        await Api.AuditAsync(context.ClientId, context.Subject, idp, ct);
    }

    public override Task OnErrorAsync(ErrorContext context, CancellationToken ct)
    {
        Logger.LogError(context.Exception, "Proxy flow failed at {Stage} for {Client}", context.Stage, context.ClientId);
        return Task.CompletedTask;
    }
}

public sealed class LocalApiClient(HttpClient http, ILogger<LocalApiClient> logger)
{
    public sealed record Profile(string Email, string DisplayName, string Department, string EmployeeId);
    private sealed record RolesResponse(string Email, string[] Roles);

    public async Task<Profile?> GetProfileAsync(string email, CancellationToken ct)
    {
        try { return await http.GetFromJsonAsync<Profile>($"users/{Uri.EscapeDataString(email)}/profile", ct); }
        catch (Exception ex) { logger.LogWarning("Profile lookup failed: {Error}", ex.Message); return null; }
    }

    public async Task<string[]?> GetRolesAsync(string email, CancellationToken ct)
    {
        try { return (await http.GetFromJsonAsync<RolesResponse>($"users/{Uri.EscapeDataString(email)}/roles", ct))?.Roles; }
        catch (Exception ex) { logger.LogWarning("Roles lookup failed: {Error}", ex.Message); return null; }
    }

    public async Task AuditAsync(string clientId, string subject, string? idp, CancellationToken ct)
    {
        try { (await http.PostAsJsonAsync("audit/token-issued", new { clientId, subject, idp }, ct)).EnsureSuccessStatusCode(); }
        catch (Exception ex) { logger.LogWarning("Audit call failed: {Error}", ex.Message); }
    }
}
