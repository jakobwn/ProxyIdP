using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ProxyIdP.Options;

namespace ProxyIdP.Services;

public sealed record UpstreamResult(string Subject, List<KeyValuePair<string, string>> Claims);

public interface IUpstreamIdpClient
{
    Task<string> BuildAuthorizeUrlAsync(UpstreamIdpOptions idp, string redirectUri, string state, string nonce, string codeVerifier, string? loginHint, CancellationToken ct);
    Task<UpstreamResult> RedeemAsync(UpstreamIdpOptions idp, string code, string redirectUri, string codeVerifier, string expectedNonce, CancellationToken ct);
}

public sealed partial class UpstreamIdpClient(IHttpClientFactory httpFactory) : IUpstreamIdpClient
{
    private static readonly HashSet<string> ExcludedClaims = new(StringComparer.Ordinal)
    {
        "iss", "aud", "exp", "iat", "nbf", "nonce", "at_hash", "c_hash", "s_hash", "auth_time", "sub", "idp", "azp", "azpacr", "ver"
    };

    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new();

    private ConfigurationManager<OpenIdConnectConfiguration> Manager(UpstreamIdpOptions idp) =>
        _managers.GetOrAdd(idp.Id, _ => new ConfigurationManager<OpenIdConnectConfiguration>(
            idp.GetMetadataAddress(),
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(httpFactory.CreateClient()) { RequireHttps = true }));

    public async Task<string> BuildAuthorizeUrlAsync(UpstreamIdpOptions idp, string redirectUri, string state, string nonce, string codeVerifier, string? loginHint, CancellationToken ct)
    {
        var cfg = await Manager(idp).GetConfigurationAsync(ct);
        return BuildAuthorizeUrl(idp, cfg.AuthorizationEndpoint, redirectUri, state, nonce, codeVerifier, loginHint);
    }

    public static string BuildAuthorizeUrl(UpstreamIdpOptions idp, string authorizationEndpoint, string redirectUri, string state, string nonce, string codeVerifier, string? loginHint = null)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = idp.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = string.Join(' ', idp.Scopes),
            ["state"] = state,
            ["nonce"] = nonce
        };
        if (!string.IsNullOrWhiteSpace(loginHint)) query["login_hint"] = loginHint;
        if (idp.UsePkce)
        {
            query["code_challenge"] = Crypto.Sha256(codeVerifier);
            query["code_challenge_method"] = "S256";
        }
        return QueryHelpers.AddQueryString(authorizationEndpoint, query);
    }

    public async Task<UpstreamResult> RedeemAsync(UpstreamIdpOptions idp, string code, string redirectUri, string codeVerifier, string expectedNonce, CancellationToken ct)
    {
        var manager = Manager(idp);
        var cfg = await manager.GetConfigurationAsync(ct);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = idp.ClientId
        };
        if (idp.UsePkce) form["code_verifier"] = codeVerifier;
        if (!string.IsNullOrEmpty(idp.ClientSecret)) form["client_secret"] = idp.ClientSecret;

        var http = httpFactory.CreateClient();
        using var response = await http.PostAsync(cfg.TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Upstream token endpoint for '{idp.Id}' returned {(int)response.StatusCode}.");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("id_token", out var idTokenEl) || idTokenEl.GetString() is not { Length: > 0 } idToken)
            throw new InvalidOperationException("Upstream response did not contain an id_token.");

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(idToken, BuildParameters(idp, cfg));
        if (!result.IsValid)
        {
            // Possible signing key rollover: refresh metadata and retry once.
            manager.RequestRefresh();
            cfg = await manager.GetConfigurationAsync(ct);
            result = await handler.ValidateTokenAsync(idToken, BuildParameters(idp, cfg));
            if (!result.IsValid) throw new SecurityTokenException("Upstream id_token validation failed.", result.Exception);
        }

        var jwt = (JsonWebToken)result.SecurityToken;
        if (!jwt.TryGetPayloadValue<string>("nonce", out var nonce) || nonce != expectedNonce)
            throw new SecurityTokenException("Upstream id_token nonce mismatch.");

        return MapClaims(idp, jwt);
    }

    private static TokenValidationParameters BuildParameters(UpstreamIdpOptions idp, OpenIdConnectConfiguration cfg)
    {
        var p = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidAudience = idp.ClientId,
            ValidateIssuer = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = cfg.SigningKeys
        };

        var multiTenant = idp.Type == UpstreamIdpType.EntraWorkforce &&
            idp.TenantId is "common" or "organizations" or "consumers";
        if (multiTenant)
        {
            p.IssuerValidator = (issuer, _, _) =>
                EntraIssuerRegex().IsMatch(issuer) ? issuer : throw new SecurityTokenInvalidIssuerException($"Invalid issuer {issuer}");
        }
        else
        {
            p.ValidIssuer = cfg.Issuer;
        }
        return p;
    }

    public static UpstreamResult MapClaims(UpstreamIdpOptions idp, JsonWebToken jwt)
    {
        var claims = jwt.Claims.ToList();
        string? Get(string type) => claims.FirstOrDefault(c => c.Type == type)?.Value;

        var upstreamSub = Get("sub") ?? throw new SecurityTokenException("Upstream id_token has no sub claim.");
        var stable = upstreamSub;
        if (idp.IsEntra && Get("oid") is { } oid && Get("tid") is { } tid) stable = $"{tid}:{oid}";

        var subject = Crypto.Sha256($"{idp.Id}:{stable}");

        var result = claims
            .Where(c => !ExcludedClaims.Contains(c.Type))
            .Select(c => new KeyValuePair<string, string>(c.Type, c.Value))
            .ToList();
        result.Add(new("upstream_sub", upstreamSub));
        result.Add(new("idp", idp.Id));
        return new UpstreamResult(subject, result);
    }

    [GeneratedRegex(@"^https://login\.microsoftonline\.com/[0-9a-fA-F\-]{36}/v2\.0$")]
    private static partial Regex EntraIssuerRegex();
}
