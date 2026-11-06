# ProxyIdP Security Review

## Scope and threat model

ProxyIdP is a sample OIDC proxy that lets **one application** work with several upstream IdPs. A deployment is configured for one application, run by the team that hosts that application, and possibly on the same infrastructure. Several `Clients` exist only because that one application may need several connections.

Consequences:

- Cross-client correlation (pairwise `sub`) and per-client claim policy are low value.
- Plugin and volume compromise, key-ring protection, the browser-facing endpoints and claim minimization toward the application's own logs, caches and sessions are the main concerns.
- Browser-facing endpoints are reachable from the internet. Server-to-server endpoints (`/connect/token`, `/connect/userinfo`, JWKS) only need to be reachable from the application.

Tags: **[code]** means a safe default should be shipped in the code. **[deploy]** means it is configured per installation (see [DEPLOYMENT.md](DEPLOYMENT.md)). **[both]** needs both.

Reviewed: `Program.cs`, `Endpoints/OidcEndpoints.cs`, `Endpoints/LoginUi.cs`, `Data/SigningKeyStore.cs`, `Data/GrantCleanupService.cs`, `Services/IdpResolver.cs`, `Services/UpstreamIdpClient.cs`, `Services/TokenService.cs`, `Services/Crypto.cs`, `Options/UpstreamIdpOptions.cs`, `PluginSystem/PluginHost.cs` (first 100 lines), `PluginSystem/PluginLoadContext.cs`, `Dockerfile`, `appsettings.json`. Not reviewed: the rest of `PluginHost`, `ClientOptions`, `ResolverTokenProvider`, `MapClaims` (tail of `UpstreamIdpClient`), `ProxyIdPDbContext`.

## A. Personal data and credentials exposed needlessly

| # | Severity | Finding | Where | Tag |
|---|---|---|---|---|
| A1 | High | All upstream claims (except a short exclusion list) are stored in the grant and copied into both the id_token and the access token; userinfo returns them all. Scopes are checked at `/authorize` but never used to filter claims. Tokens end up in the application's logs, caches and sessions. | `OidcEndpoints.UpstreamCallback`, `Token`, `UserInfo`; `UpstreamIdpClient.ExcludedClaims` | code |
| A2 | Medium | Identifiers `upstream_sub`, `idp` and the Entra `tid:oid` are exposed to the application by default. | `UpstreamIdpClient.MapClaims`, `claims_supported` in discovery | code |
| A3 | Medium | `ClaimsJson` and `LoginHint` (email) are stored in plaintext in SQLite. Grants are kept up to ~1 hour past expiry (cleanup every 15 minutes). | `GrantCleanupService`, `OidcEndpoints` | both |
| A4 | High | Signing-key private keys are protected by Data Protection, but the key ring is an unencrypted file in `/data/keys` on the same volume as the database. A leak of that volume allows forging tokens. | `Program.cs`, `Dockerfile`, `SigningKeyStore` | deploy (+ code option) |
| A5 | Medium | Secrets (`IdpResolver:ApiKey`, `ClientSecret`, upstream `ClientSecret`, client `Secret`) come from config or environment variables. Environment variables are visible to anyone with host or orchestrator access. | `appsettings.json`, `Program.cs` | both |
| A6 | Low | Client secrets are hashed with unsalted SHA-256. Fine for high-entropy secrets, weak for chosen ones. | `Crypto.Sha256`, `Program.SeedClientsAsync` | code |
| A7 | Low | Upstream exceptions are logged in full and may contain upstream response data. | `OidcEndpoints.UpstreamCallback` | code |
| A8 | Low | Access tokens are validated with `ValidateAudience=false`, so userinfo accepts a token issued to any client. Harmless with a single application, relevant if clients are ever separated. | `TokenService.ValidateAccessTokenAsync` | code |

## B. External attack vectors

| # | Severity | Finding | Where | Tag |
|---|---|---|---|---|
| B1 | High | `Plugins:Enabled` defaults to `true`. DLLs in the plugin folder are loaded into the process and reloaded on change, and `/config/customconfig.json` is reloaded on change. Write access to either volume gives code execution inside the IdP with access to every claim and secret. | `PluginHost`, `PluginLoadContext`, `Program.cs`, `appsettings.json` | both |
| B2 | High | `AllowedHosts` is `*`. When forwarded headers are enabled, `KnownProxies` and `KnownIPNetworks` are cleared, so any client can spoof `X-Forwarded-*`. | `Program.cs`, `appsettings.json` | both |
| B3 | Medium | No rate limiting on `/connect/authorize`, `/login`, `/api/login`, `/signin-upstream` or `/connect/token`. | `OidcEndpoints` | both |
| B4 | Medium | Every unauthenticated `/connect/authorize` call with valid client parameters inserts a `PendingAuthorization` row. No cap and no length limits on `state`, `nonce`, `login_hint`. Storage exhaustion DoS. | `OidcEndpoints.Authorize` | code |
| B5 | Medium | `/login` and `/api/login` trigger outbound resolver calls per request, enabling domain enumeration (timing/response differences) and request amplification. | `OidcEndpoints.StartUpstream`, `IdpResolver` | code |
| B6 | Medium | The token endpoint marks the code consumed **before** checking `redirect_uri` and PKCE, so anyone holding a code can burn it. No throttling of failed client authentication. Replay of a code does not revoke tokens already issued. | `OidcEndpoints.Token` | code |
| B7 | Medium | `EnsureKeysAsync` silently deletes undecryptable signing keys and creates new ones. A lost or tampered key ring silently invalidates all tokens and resets trust. | `SigningKeyStore.EnsureKeysAsync` | code |
| B8 | Medium | No security headers (CSP, `frame-ancestors`, `X-Content-Type-Options`, `Referrer-Policy`, HSTS). The login page is frameable (clickjacking). Custom UI is served as-is. | `OidcEndpoints.Page`, `LoginUi` | both |
| B9 | Medium | The resolver `Endpoint` is not restricted to public HTTPS addresses (SSRF to metadata or internal services if configuration is influenced). | `IdpResolverOptions`, `IdpResolver` | both |
| B10 | Medium | Multi-tenant Entra (`common`, `organizations`, `consumers`) validates the issuer by regex only. Without a `tid` allow-list any tenant's user can sign in. | `UpstreamIdpClient.BuildParameters` | both |
| B11 | Low | `/healthz` is unauthenticated and touches the database. `/` reveals the product. | `Program.cs` | both |
| B12 | Low | Access tokens are self-contained JWTs with no revocation (30 minutes by default). Acceptable for one trusted application. | `TokenService` | deploy |
| B13 | Low | `EnsureCreatedAsync` with no migrations. SQLite on a single volume limits the service to one replica and has no upgrade path. | `Program.cs` | deploy |
| B14 | Info | Positive findings: PKCE `S256` required, exact-match redirect and post-logout URIs, single-use upstream state, nonce and PKCE to upstream, fixed-time secret comparison, antiforgery on login POSTs, HTML encoding on the built-in login page, container runs non-root, `iss` returned on authorization responses. | various | n/a |

## Code default versus deployment

Defaults the code should provide (generic sample, secure by default): plugins off, no hot reload; minimal claims by a global allow-list; token endpoint validation order; fail-fast on undecryptable keys; production startup validation (HTTPS issuer, no `localhost`, `AllowedHosts` not `*`, forwarded headers require known proxies, Entra multi-tenant requires a `tid` allow-list); rate limiting; length caps; pending cap and short retention; security headers; no PII in logs; minimal `/healthz`; `*_FILE` secrets and an optional certificate-protected key ring; resolver SSRF guard.

and whether plugins or a custom UI are used.

## Code hardening backlog (not implemented)

Secure-by-default breaking changes are acceptable. Ordered by priority.

1. **Plugins off by default, no hot reload (B1).** Set `Plugins:Enabled=false`. Reload only on restart. Optionally require a hash allow-list per plugin DLL. Stop hot-reloading `/config/customconfig.json` (`reloadOnChange: false`).
2. **Signing key handling (A4, B7).** Fail startup when stored keys cannot be decrypted instead of deleting and regenerating them (add an explicit opt-in to regenerate). Add optional `DataProtection` key protection by certificate or Key Vault, and document a separate key volume.
3. **Token endpoint (B6).** Validate `redirect_uri` and PKCE before marking the code consumed. Throttle failed client authentication. Log and, if desired, revoke on code replay.
4. **Claim minimization (A1, A2).** Add a global `Claims:Allowed` allow-list, default `sub`, `email`, `name`, `preferred_username`. Filter by granted scope (`email`, `profile`). Drop `upstream_sub` and `idp` unless enabled. Store only the filtered claims in the grant.
5. **Production startup validation (B2, B10).** Refuse to start outside Development when `Issuer` is not HTTPS or contains `localhost`, `AllowedHosts` is `*`, forwarded headers are enabled without `KnownProxies` or `KnownIPNetworks`, or a multi-tenant Entra IdP has no `AllowedTenantIds`.
6. **Rate limiting and input caps (B3, B4, B5).** Built-in rate limiter with configurable limits on authorize, login, api/login, signin-upstream and token. Length caps on `state`, `nonce`, `login_hint`, `email`. Cap pending authorizations. Cache negative resolver results briefly and equalize response behavior.
7. **Security headers (B8).** Middleware setting CSP with `frame-ancestors 'none'`, `X-Content-Type-Options`, `Referrer-Policy`, optional HSTS, and `Cache-Control: no-store` on login pages and authorization responses.
8. **Secrets from files (A5).** Support `*_FILE` or a `Secrets:Path` provider for `ClientSecret`, `ApiKey` and client secrets (`AddKeyPerFile`). Never log option values.
9. **Resolver SSRF guard (B9).** Require HTTPS, resolve the host and reject loopback, link-local, private and metadata addresses (also on redirects). Disable automatic redirects on the resolver client.
10. **Retention (A3).** Delete grants shortly after expiry or consumption (minutes, not 1 hour), run cleanup more often, and encrypt `ClaimsJson` and `LoginHint` with Data Protection.
11. **Logging (A7).** Log exception type and correlation id only for upstream failures, never response bodies, claims or emails.
12. **Smaller items.** Salted or keyed hash for client secrets (A6). Audience check in `ValidateAccessTokenAsync` (A8). Minimal `/healthz` and no product banner on `/` (B11). Hardened `Dockerfile` (pinned digest, no sample secrets, documented read-only filesystem support). Migrations instead of `EnsureCreatedAsync` (B13).
