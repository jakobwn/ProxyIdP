# ProxyIdP.SampleApp

A minimal web app for testing and validating a running ProxyIdP. It signs users in through the proxy with OpenID Connect (authorization code + PKCE, no client secret) and shows what the proxy issued.

## Pages

- `/` start page with the configured authority and a **Sign in** button.
- `/profile` (requires sign-in) lists all claims, the raw and decoded ID token and access token, and has a **Sign out** button.

## Prerequisites

1. Trust the dev certificate: `dotnet dev-certs https --trust`
2. Run ProxyIdP in **Development** (the `dev-client` is only seeded there). It must listen on `https://localhost:7103` (the `https` launch profile), the same as `Proxy:Issuer`.
3. Enable at least one upstream IdP in `ProxyIdP/appsettings.json` (set `Enabled` to `true`, with real tenant, client and secret values). All entries are disabled placeholders by default, so sign-in cannot complete until you do. Register the proxy callback `https://localhost:7103/signin-upstream` as a redirect URI at that IdP.

## Run

1. Start `ProxyIdP` (profile `https`).
2. Start `ProxyIdP.SampleApp` (profile `https`). It listens on `https://localhost:5001`, which matches the redirect URIs seeded for `dev-client`.
3. Open `https://localhost:5001`, click **Sign in**, choose an upstream IdP on the proxy login page, and authenticate.

Settings are in `appsettings.json` under `ProxyIdP` (`Authority`, `ClientId`).

## What to verify on the profile page

- `iss` equals the proxy issuer, and `aud` equals the client id (`dev-client`).
- `nonce` is present in the ID token.
- `sub` is a stable hash that stays the same across sign-ins by the same user at the same IdP.
- `idp` is the id of the upstream IdP you used, and `upstream_sub` is the subject from that IdP.
- Upstream claims (for example `name` and `email`) are passed through.
- The `kid` in the token headers matches a key at `https://localhost:7103/.well-known/jwks.json`.
- The access token has `typ` `at+jwt` and a `scope` claim.
- **Sign out** returns to the sample app start page. This works because the app sends `client_id` to the proxy's end-session endpoint.
