using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ProxyIdP.Data;
using ProxyIdP.Options;

namespace ProxyIdP.Services;

public sealed class TokenService(ISigningKeyStore keys, IOptions<ProxyOptions> options, TimeProvider time)
{
    private readonly ProxyOptions _opt = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public string Issuer => _opt.Issuer.TrimEnd('/');
    public int LifetimeSeconds => _opt.TokenLifetimeMinutes * 60;

    public Task<string> CreateIdTokenAsync(string clientId, IEnumerable<Claim> claims, string? nonce, CancellationToken ct) =>
        CreateAsync(clientId, claims, nonce, "JWT", ct);

    public Task<string> CreateAccessTokenAsync(string clientId, IEnumerable<Claim> claims, string scope, CancellationToken ct) =>
        CreateAsync(clientId, claims.Append(new Claim("scope", scope)).Append(new Claim("client_id", clientId)), null, "at+jwt", ct);

    private async Task<string> CreateAsync(string clientId, IEnumerable<Claim> claims, string? nonce, string type, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var identity = new ClaimsIdentity(claims);
        if (nonce is not null) identity.AddClaim(new Claim("nonce", nonce));
        identity.AddClaim(new Claim("auth_time", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = clientId,
            Subject = identity,
            NotBefore = now,
            IssuedAt = now,
            Expires = now.AddMinutes(_opt.TokenLifetimeMinutes),
            SigningCredentials = await keys.GetSigningCredentialsAsync(ct),
            TokenType = type
        };
        return _handler.CreateToken(descriptor);
    }

    public async Task<JsonWebToken?> ValidateAccessTokenAsync(string token, CancellationToken ct)
    {
        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKeys = await keys.GetValidationKeysAsync(ct),
            ValidTypes = ["at+jwt"]
        });
        return result.IsValid ? (JsonWebToken)result.SecurityToken : null;
    }
}
