using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ProxyIdP.Data;
using ProxyIdP.Options;

namespace ProxyIdP.Services;

public interface IResolverTokenProvider
{
    Task<string?> GetTokenAsync(bool forceRefresh, CancellationToken ct);
}

/// <summary>Acquires and caches the bearer token used to call the IdP resolver API.</summary>
public sealed class ResolverTokenProvider(
    IHttpClientFactory httpFactory,
    ISigningKeyStore keys,
    IOptions<IdpResolverOptions> options,
    IOptions<ProxyOptions> proxy,
    TimeProvider time,
    ILogger<ResolverTokenProvider> logger) : IResolverTokenProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IdpResolverAuthenticationOptions _auth = options.Value.Authentication;
    private string? _token;
    private DateTimeOffset _expires;

    public async Task<string?> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!_auth.UsesBearerToken) return null;
        if (!forceRefresh && IsValid()) return _token;

        await _gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && IsValid()) return _token;
            (_token, _expires) = await RequestAsync(ct);
            return _token;
        }
        finally { _gate.Release(); }
    }

    private bool IsValid() => _token is not null && time.GetUtcNow() < _expires - TimeSpan.FromSeconds(30);

    private async Task<(string Token, DateTimeOffset Expires)> RequestAsync(CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _auth.ClientId!
        };
        if (!string.IsNullOrEmpty(_auth.Scope)) form["scope"] = _auth.Scope;

        if (_auth.Mode == ResolverAuthMode.ClientSecret)
        {
            form["client_secret"] = _auth.ClientSecret!;
        }
        else
        {
            form["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
            form["client_assertion"] = _auth.Mode == ResolverAuthMode.FederatedCredential
                ? await CreateAssertionAsync(ct)
                : (await File.ReadAllTextAsync(_auth.AssertionFilePath!, ct)).Trim();
        }

        var http = httpFactory.CreateClient(IdpResolver.HttpClientName);
        using var response = await http.PostAsync(_auth.TokenEndpoint, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Resolver token endpoint returned {Status}", (int)response.StatusCode);
            throw new InvalidOperationException($"Resolver token request failed with status {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var token = root.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Token response had no access_token.");
        var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 300;
        return (token, time.GetUtcNow().AddSeconds(seconds));
    }

    /// <summary>Signs a short-lived client assertion with ProxyIdP's current signing key.</summary>
    public async Task<string> CreateAssertionAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = proxy.Value.Issuer.TrimEnd('/'),
            Audience = _auth.Audience,
            Subject = new ClaimsIdentity([new Claim("sub", _auth.Subject!), new Claim("role", "resolver"), new Claim("jti", Guid.NewGuid().ToString("N"))]),
            NotBefore = now,
            IssuedAt = now,
            Expires = now.AddSeconds(Math.Max(30, _auth.AssertionLifetimeSeconds)),
            SigningCredentials = await keys.GetSigningCredentialsAsync(ct)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
