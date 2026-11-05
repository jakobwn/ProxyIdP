using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ProxyIdP.Data;
using ProxyIdP.Options;

namespace ProxyIdP.Plugins;

public static class PluginSubjectResolver
{
    /// <summary>The host-assigned subject: a configured override, otherwise derived from assembly and type.</summary>
    public static string Resolve(string configuredEntry, string assemblyName, string typeFullName, IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.TryGetValue(configuredEntry, out var subject) && !string.IsNullOrWhiteSpace(subject)) return subject;
        return $"plugin:{assemblyName}/{typeFullName}";
    }
}

public interface IPluginIdentityIssuer
{
    Task<(string Token, DateTimeOffset Expires)> IssueAsync(string subject, CancellationToken ct);
}

/// <summary>Issues short-lived JWTs, signed with ProxyIdP's signing key, that identify a plugin.</summary>
public sealed class PluginIdentityIssuer(
    ISigningKeyStore keys,
    IOptions<ProxyOptions> proxy,
    IOptionsMonitor<PluginOptions> plugins,
    TimeProvider time) : IPluginIdentityIssuer
{
    public async Task<(string Token, DateTimeOffset Expires)> IssueAsync(string subject, CancellationToken ct)
    {
        var opt = plugins.CurrentValue;
        var now = time.GetUtcNow();
        var expires = now.AddSeconds(Math.Max(30, opt.IdentityLifetimeSeconds));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = proxy.Value.Issuer.TrimEnd('/'),
            Audience = opt.IdentityAudience,
            Subject = new ClaimsIdentity([new Claim("sub", subject), new Claim("role", "plugin"), new Claim("jti", Guid.NewGuid().ToString("N"))]),
            NotBefore = now.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = await keys.GetSigningCredentialsAsync(ct)
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}

/// <summary>The identity of one plugin. Caches the issued token until shortly before it expires.</summary>
public sealed class PluginIdentity(string subject, IPluginIdentityIssuer issuer, TimeProvider time) : IPluginIdentity
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    public string Subject => subject;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (IsValid()) return _token!;
        await _gate.WaitAsync(ct);
        try
        {
            if (!IsValid()) (_token, _expires) = await issuer.IssueAsync(subject, ct);
            return _token!;
        }
        finally { _gate.Release(); }
    }

    private bool IsValid() => _token is not null && time.GetUtcNow() < _expires - TimeSpan.FromSeconds(30);
}
