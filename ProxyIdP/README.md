# ProxyIdP

## Upstream sign-in behavior

- **login_hint**: when the user enters an email on the login page, that email is forwarded to the upstream IdP as `login_hint`, so it does not have to be typed twice. A `login_hint` sent by the client to `/connect/authorize` is used when no email was typed. Choosing an IdP button without an email sends no hint.
- **Default IdP**: set `"IsDefault": true` on one enabled entry in `UpstreamIdps`. It is used when no IdP button was chosen and the email did not match an IdP (by `EmailDomains` or the optional resolver API), including an empty email. Without a default, the login page shows an error and asks the user to choose. At most one IdP may be the default and it must be enabled; otherwise startup fails.

## Configuring upstream IdPs

`UpstreamIdps` is an object of named nodes; the node name is the IdP id (no `Id` property). Any new name adds an IdP. Individual values can be overridden with environment variables, e.g. `UpstreamIdps__workforce__ClientId=...`. The old array form (`UpstreamIdps__0__...`) is no longer supported and fails at startup.

The list can also live in an optional file, `/config/customconfig.json` (e.g. a mounted config volume). It can hold any configuration section, including `UpstreamIdps` and `Clients`: `{ "UpstreamIdps": { "workforce": { ... } }, "Clients": { ... } }`. Precedence: appsettings < `/config/customconfig.json` < environment variables < command line. This file replaces the earlier `/config/upstreamidps.json`; rename existing files.

## Configuring clients

`Clients` is an object of named nodes; the node name is the client id. Each entry supports `Secret` (optional, stored hashed), `RedirectUris`, `PostLogoutRedirectUris` and `AllowedScopes` (defaults to `openid profile email`). At least one redirect URI is required. Single values can be overridden with environment variables, e.g. `Clients__dev-client__Secret=...`. The array form is rejected at startup.

On startup every configured client is upserted (created, or updated to match the configuration). Clients that exist in the database but not in the configuration are left untouched. The base `appsettings.json` defines no clients; `appsettings.Development.json` defines `dev-client`.

## Login page customization

### Stylesheet

The `/login` page links `/style/login.css`. Every element carries a class (`login-page`, `login-container`, `login-title`, `login-error`, `login-form`, `login-label`, `login-input`, `login-button`, `login-button-primary`, `login-divider`, `login-alt-text`, `login-idp-button`, and `login-idp-<id>` per identity provider).

The file is read from the folder set by `Ui:StylePath` (default `style`; `/app/style` in the Docker image, declared as a volume). If `login.css` is missing there, a built-in default is served.

```
docker run -v ./mystyle:/app/style ... proxyidp
```

### Custom UI

Set `Ui:CustomUi:Enabled` to `true` to replace the built-in `/login` page. `Ui:CustomUi:Path` is the folder (default `customui`; `/app/customui` in Docker, declared as a volume). `index.html` there is served at `/login?rid=...` (after the `rid` is validated) and all files in the folder are served under `/customui/`. If `index.html` is missing, the built-in page is used.

| Docker environment variable | Meaning |
| --- | --- |
| `Ui__StylePath` | Stylesheet folder |
| `Ui__CustomUi__Enabled` | `true` to use the custom UI |
| `Ui__CustomUi__Path` | Custom UI folder |

```
docker run -e Ui__CustomUi__Enabled=true -v ./mycustomui:/app/customui ... proxyidp
```

The custom UI uses these same-origin JSON endpoints (404 when the custom UI is disabled):

- `GET /api/login/config?rid=<rid>` returns `{ "rid", "antiforgery": { "headerName", "requestToken" }, "idps": [ { "id", "displayName" } ] }` and sets the antiforgery cookie. Returns 400 `{ "error" }` if the `rid` is expired or invalid.
- `POST /api/login` with JSON `{ "rid", "email", "idp" }` and the antiforgery token in the `X-CSRF-TOKEN` header. Returns `{ "redirectUrl" }` (navigate the browser there) or 400 `{ "error" }`. Provider selection is the same as the default page: an explicit `idp`, else email domain/resolver match, else the default IdP.

is in `examples/customui` (`index.html`, `login.js`, `login.css`), together with a Dockerfile that extends the ProxyIdP image.

