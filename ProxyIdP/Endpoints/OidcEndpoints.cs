using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyIdP.Data;
using ProxyIdP.Options;
using ProxyIdP.Plugins;
using ProxyIdP.Services;

namespace ProxyIdP.Endpoints;

public static class OidcEndpoints
{
    private static readonly string[] SupportedScopes = ["openid", "profile", "email"];

    public static void MapOidc(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration", Discovery);
        app.MapGet("/.well-known/jwks.json", async (ISigningKeyStore keys, CancellationToken ct) => Results.Json(await keys.GetJwksAsync(ct)));
        app.MapGet("/connect/authorize", Authorize);
        app.MapGet("/login", LoginGet);
        app.MapPost("/login", LoginPost);
        app.MapGet("/api/login/config", ApiLoginConfig);
        app.MapPost("/api/login", ApiLogin);
        app.MapGet("/signin-upstream", UpstreamCallback);
        app.MapPost("/connect/token", Token);
        app.MapMethods("/connect/userinfo", ["GET", "POST"], UserInfo);
        app.MapGet("/connect/endsession", EndSession);
    }

    private static IResult Discovery(TokenService tokens)
    {
        var i = tokens.Issuer;
        return Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = i,
            ["authorization_endpoint"] = $"{i}/connect/authorize",
            ["token_endpoint"] = $"{i}/connect/token",
            ["userinfo_endpoint"] = $"{i}/connect/userinfo",
            ["end_session_endpoint"] = $"{i}/connect/endsession",
            ["jwks_uri"] = $"{i}/.well-known/jwks.json",
            ["response_types_supported"] = new[] { "code" },
            ["response_modes_supported"] = new[] { "query" },
            ["grant_types_supported"] = new[] { "authorization_code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["scopes_supported"] = SupportedScopes,
            ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "client_secret_post", "none" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["claims_supported"] = new[] { "sub", "iss", "aud", "exp", "iat", "nonce", "auth_time", "name", "email", "preferred_username", "upstream_sub", "idp" }
        });
    }

    private static async Task<IResult> Authorize(HttpContext ctx, ProxyIdPDbContext db, IOptions<ProxyOptions> opts, TimeProvider time, IPluginHost plugins, CancellationToken ct)
    {
        var q = ctx.Request.Query;
        var clientId = q["client_id"].ToString();
        var redirectUri = q["redirect_uri"].ToString();

        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId);
        if (client is null || !client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
            return Results.BadRequest("Unknown client or invalid redirect_uri.");

        string? state = q["state"];
        IResult Error(string error, string description) => Results.Redirect(QueryHelpers.AddQueryString(redirectUri,
            new Dictionary<string, string?> { ["error"] = error, ["error_description"] = description, ["state"] = state, ["iss"] = opts.Value.Issuer.TrimEnd('/') }));

        if (q["response_type"] != "code") return Error("unsupported_response_type", "Only response_type=code is supported.");
        var scopes = q["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains("openid")) return Error("invalid_scope", "The openid scope is required.");
        if (scopes.Any(s => !client.AllowedScopes.Contains(s))) return Error("invalid_scope", "Scope not allowed for client.");
        var challenge = q["code_challenge"].ToString();
        if (string.IsNullOrEmpty(challenge) || q["code_challenge_method"] != "S256")
            return Error("invalid_request", "PKCE with code_challenge_method=S256 is required.");

        var hookCtx = new AuthorizationRequestContext(clientId, redirectUri, scopes, string.IsNullOrWhiteSpace(q["login_hint"]) ? null : q["login_hint"].ToString().Trim());
        await plugins.AuthorizationRequestAsync(hookCtx, ct);
        if (hookCtx.IsRejected) return Error(hookCtx.Error ?? "access_denied", hookCtx.ErrorDescription ?? "Request rejected.");

        var pending = new PendingAuthorization
        {
            Id = Crypto.RandomToken(),
            ClientId = clientId,
            RedirectUri = redirectUri,
            Scope = string.Join(' ', scopes),
            State = state,
            Nonce = q["nonce"],
            LoginHint = hookCtx.LoginHint,
            CodeChallenge = challenge,
            ExpiresAt = time.GetUtcNow().UtcDateTime.AddMinutes(opts.Value.PendingAuthorizationLifetimeMinutes)
        };
        db.PendingAuthorizations.Add(pending);
        await db.SaveChangesAsync();
        return Results.Redirect($"/login?rid={Uri.EscapeDataString(pending.Id)}");
    }

    private static string Page(string body) =>
        "<!DOCTYPE html><html class=\"login-html\"><head><meta charset=\"utf-8\"><title>Sign in</title>" +
        "<link rel=\"stylesheet\" href=\"/style/login.css\"></head><body class=\"login-page\"><div class=\"login-container\">" + body + "</div></body></html>";

    private static Task<IResult> RenderLogin(HttpContext ctx, IAntiforgery af, UpstreamIdpRegistry reg, string rid, string? error)
    {
        var tokens = af.GetAndStoreTokens(ctx);
        Func<string?, string> enc = s => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder("<h2 class=\"login-title\">Sign in</h2>");
        if (error is not null) sb.Append($"<p class=\"login-error err\">{enc(error)}</p>");
        sb.Append("<form class=\"login-form\" method=\"post\" action=\"/login\">")
          .Append($"<input class=\"login-hidden\" type=\"hidden\" name=\"{enc(tokens.FormFieldName)}\" value=\"{enc(tokens.RequestToken)}\">")
          .Append($"<input class=\"login-hidden\" type=\"hidden\" name=\"rid\" value=\"{enc(rid)}\">")
          .Append("<label class=\"login-label\">Email address<input class=\"login-input login-email\" type=\"email\" name=\"email\" autocomplete=\"email\" autofocus></label>")
          .Append("<button class=\"login-button login-button-primary\" type=\"submit\">Continue</button><hr class=\"login-divider\"><p class=\"login-alt-text\">Or sign in with:</p>");
        foreach (var idp in reg.Enabled)
            sb.Append($"<button class=\"login-button login-idp-button login-idp-{enc(idp.Id)}\" type=\"submit\" name=\"idp\" value=\"{enc(idp.Id)}\" formnovalidate>{enc(idp.DisplayName)}</button>");
        sb.Append("</form>");
        return Task.FromResult(Results.Content(Page(sb.ToString()), "text/html"));
    }

    private static async Task<PendingAuthorization?> FindPending(ProxyIdPDbContext db, string? rid, TimeProvider time)
    {
        if (string.IsNullOrEmpty(rid)) return null;
        var p = await db.PendingAuthorizations.FirstOrDefaultAsync(x => x.Id == rid);
        return p is not null && p.ExpiresAt > time.GetUtcNow().UtcDateTime ? p : null;
    }

    private static async Task<IResult> LoginGet(HttpContext ctx, ProxyIdPDbContext db, IAntiforgery af, UpstreamIdpRegistry reg, TimeProvider time,
        IOptions<UiOptions> ui, IHostEnvironment env)
    {
        var rid = ctx.Request.Query["rid"].ToString();
        if (await FindPending(db, rid, time) is null) return Results.BadRequest("Sign-in request expired or invalid.");
        if (LoginUi.CustomIndexPath(env, ui.Value) is { } index)
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.File(File.OpenRead(index), "text/html");
        }
        return await RenderLogin(ctx, af, reg, rid, null);
    }

    private const string NoIdpMessage = "No identity provider matches that email address. Please choose one below.";

    private static async Task<(string? Url, string? Error)> StartUpstream(ProxyIdPDbContext db, UpstreamIdpRegistry reg, IIdpResolver resolver,
        IUpstreamIdpClient upstream, IOptions<ProxyOptions> opts, IPluginHost plugins, PendingAuthorization pending, string? selected, string email, CancellationToken ct)
    {
        var idp = !string.IsNullOrEmpty(selected) ? reg.Get(selected) : await resolver.ResolveAsync(email, ct) ?? reg.Default;
        if (idp is null) return (null, NoIdpMessage);

        if (!string.IsNullOrEmpty(email)) pending.LoginHint = email;
        pending.IdpId = idp.Id;
        pending.UpstreamState = Crypto.RandomToken();
        pending.UpstreamNonce = Crypto.RandomToken();
        pending.UpstreamCodeVerifier = Crypto.RandomToken(48);
        await db.SaveChangesAsync(ct);

        var redirect = $"{opts.Value.Issuer.TrimEnd('/')}/signin-upstream";
        var url = await upstream.BuildAuthorizeUrlAsync(idp, redirect, pending.UpstreamState, pending.UpstreamNonce, pending.UpstreamCodeVerifier, pending.LoginHint, ct);
        var hookCtx = new UpstreamRedirectContext(pending.ClientId, idp.Id, string.IsNullOrEmpty(email) ? null : email, url);
        await plugins.UpstreamRedirectAsync(hookCtx, ct);
        return (hookCtx.AuthorizeUrl, null);
    }

    private sealed record ApiLoginRequest(string? Rid, string? Email, string? Idp);

    private static async Task<IResult> ApiLoginConfig(HttpContext ctx, ProxyIdPDbContext db, IAntiforgery af, UpstreamIdpRegistry reg, TimeProvider time,
        IOptions<UiOptions> ui, IHostEnvironment env)
    {
        if (LoginUi.CustomIndexPath(env, ui.Value) is null) return Results.NotFound();
        ctx.Response.Headers.CacheControl = "no-store";
        var rid = ctx.Request.Query["rid"].ToString();
        if (await FindPending(db, rid, time) is null) return Results.Json(new { error = "Sign-in request expired or invalid." }, statusCode: 400);
        var t = af.GetAndStoreTokens(ctx);
        return Results.Json(new
        {
            rid,
            antiforgery = new { headerName = t.HeaderName, requestToken = t.RequestToken },
            idps = reg.Enabled.Select(i => new { id = i.Id, displayName = i.DisplayName })
        });
    }

    private static async Task<IResult> ApiLogin(HttpContext ctx, ProxyIdPDbContext db, IAntiforgery af, UpstreamIdpRegistry reg,
        IIdpResolver resolver, IUpstreamIdpClient upstream, IOptions<ProxyOptions> opts, IOptions<UiOptions> ui, IHostEnvironment env,
        TimeProvider time, IPluginHost plugins, ApiLoginRequest body, CancellationToken ct)
    {
        if (LoginUi.CustomIndexPath(env, ui.Value) is null) return Results.NotFound();
        try { await af.ValidateRequestAsync(ctx); }
        catch (AntiforgeryValidationException) { return Results.Json(new { error = "Invalid or missing antiforgery token." }, statusCode: 400); }
        var pending = await FindPending(db, body.Rid, time);
        if (pending is null) return Results.Json(new { error = "Sign-in request expired or invalid." }, statusCode: 400);

        var (url, error) = await StartUpstream(db, reg, resolver, upstream, opts, plugins, pending, body.Idp, (body.Email ?? "").Trim(), ct);
        return error is not null ? Results.Json(new { error }, statusCode: 400) : Results.Json(new { redirectUrl = url });
    }

    private static async Task<IResult> LoginPost(HttpContext ctx, ProxyIdPDbContext db, IAntiforgery af, UpstreamIdpRegistry reg,
        IIdpResolver resolver, IUpstreamIdpClient upstream, IOptions<ProxyOptions> opts, TimeProvider time, IPluginHost plugins, CancellationToken ct)
    {
        try { await af.ValidateRequestAsync(ctx); }
        catch (AntiforgeryValidationException) { return Results.BadRequest("Invalid or missing antiforgery token."); }
        var form = await ctx.Request.ReadFormAsync(ct);
        var rid = form["rid"].ToString();
        var pending = await FindPending(db, rid, time);
        if (pending is null) return Results.BadRequest("Sign-in request expired or invalid.");

        var selected = form["idp"].ToString();
        var email = form["email"].ToString().Trim();
        var (url, error) = await StartUpstream(db, reg, resolver, upstream, opts, plugins, pending, selected, email, ct);
        if (error is not null) return await RenderLogin(ctx, af, reg, rid, error);
        return Results.Redirect(url!);
    }

    private static async Task<IResult> UpstreamCallback(HttpContext ctx, ProxyIdPDbContext db, UpstreamIdpRegistry reg, IUpstreamIdpClient upstream,
        IOptions<ProxyOptions> opts, TimeProvider time, IPluginHost plugins, ILogger<Program> logger, CancellationToken ct)
    {
        var state = ctx.Request.Query["state"].ToString();
        var pending = string.IsNullOrEmpty(state) ? null
            : await db.PendingAuthorizations.FirstOrDefaultAsync(p => p.UpstreamState == state, ct);
        if (pending is null || pending.ExpiresAt <= time.GetUtcNow().UtcDateTime) return Results.BadRequest("Sign-in request expired or invalid.");

        // Single use
        db.PendingAuthorizations.Remove(pending);
        await db.SaveChangesAsync(ct);

        string Back(string error, string? desc = null) => QueryHelpers.AddQueryString(pending.RedirectUri, new Dictionary<string, string?>
        {
            ["error"] = error, ["error_description"] = desc, ["state"] = pending.State, ["iss"] = opts.Value.Issuer.TrimEnd('/')
        });

        if (ctx.Request.Query.TryGetValue("error", out var upstreamError))
            return Results.Redirect(Back(upstreamError == "access_denied" ? "access_denied" : "server_error", "Upstream sign-in failed."));

        var idp = reg.Get(pending.IdpId);
        var code = ctx.Request.Query["code"].ToString();
        if (idp is null || string.IsNullOrEmpty(code)) return Results.Redirect(Back("server_error", "Upstream sign-in failed."));

        UpstreamResult result;
        try
        {
            result = await upstream.RedeemAsync(idp, code, $"{opts.Value.Issuer.TrimEnd('/')}/signin-upstream",
                pending.UpstreamCodeVerifier!, pending.UpstreamNonce!, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upstream sign-in via {Idp} failed", idp.Id);
            await plugins.ErrorAsync(new ErrorContext(pending.ClientId, "upstream-callback", ex), ct);
            return Results.Redirect(Back("access_denied", "Upstream authentication failed."));
        }

        var upstreamClaims = result.Claims.Select(c => new Claim(c.Key, c.Value)).ToList();
        var callbackCtx = new UpstreamCallbackContext(pending.ClientId, idp.Id, result.Subject, upstreamClaims);
        await plugins.UpstreamCallbackAsync(callbackCtx, ct);
        if (callbackCtx.IsRejected) return Results.Redirect(Back(callbackCtx.Error ?? "access_denied", callbackCtx.ErrorDescription));
        result = result with { Claims = [.. upstreamClaims.Select(c => new KeyValuePair<string, string>(c.Type, c.Value))] };

        var proxyCode = Crypto.RandomToken();
        db.Grants.Add(new AuthorizationGrant
        {
            CodeHash = Crypto.Sha256(proxyCode),
            ClientId = pending.ClientId,
            Subject = result.Subject,
            ClaimsJson = JsonSerializer.Serialize(result.Claims.Select(c => new[] { c.Key, c.Value })),
            Nonce = pending.Nonce,
            CodeChallenge = pending.CodeChallenge,
            RedirectUri = pending.RedirectUri,
            Scope = pending.Scope,
            ExpiresAt = time.GetUtcNow().UtcDateTime.AddSeconds(opts.Value.AuthorizationCodeLifetimeSeconds)
        });
        await db.SaveChangesAsync(ct);

        return Results.Redirect(QueryHelpers.AddQueryString(pending.RedirectUri, new Dictionary<string, string?>
        {
            ["code"] = proxyCode, ["state"] = pending.State, ["iss"] = opts.Value.Issuer.TrimEnd('/')
        }));
    }

    private static IResult TokenError(string error, string? desc = null, int status = 400) =>
        Results.Json(new { error, error_description = desc }, statusCode: status);

    private static async Task<IResult> Token(HttpContext ctx, ProxyIdPDbContext db, TokenService tokens, TimeProvider time, IPluginHost plugins, CancellationToken ct)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (!ctx.Request.HasFormContentType) return TokenError("invalid_request");
        var form = await ctx.Request.ReadFormAsync(ct);

        string? clientId = form["client_id"], secret = form["client_secret"];
        if (ctx.Request.Headers.Authorization.ToString() is { } auth && auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..])).Split(':', 2);
                clientId = Uri.UnescapeDataString(parts[0].Replace("+", "%20"));
                secret = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace("+", "%20")) : null;
            }
            catch (FormatException) { return TokenError("invalid_client", null, 401); }
        }

        var client = string.IsNullOrEmpty(clientId) ? null : await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        if (client is null) return TokenError("invalid_client", null, 401);
        if (client.SecretHash is not null && (string.IsNullOrEmpty(secret) || !Crypto.FixedTimeEquals(client.SecretHash, Crypto.Sha256(secret))))
            return TokenError("invalid_client", null, 401);

        if (form["grant_type"] != "authorization_code") return TokenError("unsupported_grant_type");

        var code = form["code"].ToString();
        var now = time.GetUtcNow().UtcDateTime;
        var hash = Crypto.Sha256(code);
        var grant = string.IsNullOrEmpty(code) ? null : await db.Grants.FirstOrDefaultAsync(g => g.CodeHash == hash, ct);
        if (grant is null || grant.ClientId != client.ClientId || grant.ExpiresAt <= now || grant.ConsumedAt is not null)
            return TokenError("invalid_grant");

        grant.ConsumedAt = now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return TokenError("invalid_grant"); }

        if (form["redirect_uri"] != grant.RedirectUri) return TokenError("invalid_grant", "redirect_uri mismatch");
        var verifier = form["code_verifier"].ToString();
        if (string.IsNullOrEmpty(verifier) || !Crypto.FixedTimeEquals(Crypto.Sha256(verifier), grant.CodeChallenge))
            return TokenError("invalid_grant", "PKCE verification failed");

        var pairs = JsonSerializer.Deserialize<List<string[]>>(grant.ClaimsJson) ?? [];
        var claims = new List<Claim> { new("sub", grant.Subject) };
        claims.AddRange(pairs.Select(p => new Claim(p[0], p[1])));

        await plugins.TokenIssuingAsync(new TokenIssuingContext(client.ClientId, grant.Subject, grant.Scope, claims), ct);

        var idToken = await tokens.CreateIdTokenAsync(client.ClientId, claims, grant.Nonce, ct);
        var accessToken = await tokens.CreateAccessTokenAsync(client.ClientId, claims, grant.Scope, ct);
        await plugins.TokenIssuedAsync(new TokenIssuedContext(client.ClientId, grant.Subject, grant.Scope, claims), ct);
        return Results.Json(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = tokens.LifetimeSeconds,
            id_token = idToken,
            scope = grant.Scope
        });
    }

    private static async Task<IResult> UserInfo(HttpContext ctx, TokenService tokens, CancellationToken ct)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            return Results.StatusCode(401);
        }
        var jwt = await tokens.ValidateAccessTokenAsync(auth[7..].Trim(), ct);
        if (jwt is null)
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            return Results.StatusCode(401);
        }

        var skip = new HashSet<string> { "iss", "aud", "exp", "iat", "nbf", "jti", "scope", "client_id", "auth_time", "nonce" };
        var dict = new Dictionary<string, object>();
        foreach (var g in jwt.Claims.Where(c => !skip.Contains(c.Type)).GroupBy(c => c.Type))
            dict[g.Key] = g.Count() == 1 ? g.First().Value : g.Select(c => c.Value).ToArray();
        return Results.Json(dict);
    }

    private static async Task<IResult> EndSession(HttpContext ctx, ProxyIdPDbContext db, CancellationToken ct)
    {
        var target = ctx.Request.Query["post_logout_redirect_uri"].ToString();
        var clientId = ctx.Request.Query["client_id"].ToString();
        if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(clientId))
        {
            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
            if (client is not null && client.PostLogoutRedirectUris.Contains(target, StringComparer.Ordinal))
            {
                var state = ctx.Request.Query["state"].ToString();
                return Results.Redirect(string.IsNullOrEmpty(state) ? target : QueryHelpers.AddQueryString(target, "state", state));
            }
        }
        return Results.Content(Page("<h2>You have been signed out.</h2>"), "text/html");
    }
}
