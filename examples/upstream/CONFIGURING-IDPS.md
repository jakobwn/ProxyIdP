# Configuring upstream identity providers

ProxyIdP does not authenticate users itself. It forwards them to an **upstream identity provider (IdP)** and issues its own tokens to your applications. This guide explains how to configure those upstream IdPs in `customconfig.json`.

## 1. Overview

The file is read from `/config/customconfig.json` (typically a mounted config volume or baked into an image, see [README.md](README.md)). It is optional. IdPs are defined under `UpstreamIdps` as **named nodes**:

```json
{
  "UpstreamIdps": {
	"workforce": {
	  "Type": "EntraWorkforce",
	  "DisplayName": "Company (Entra)",
	  "TenantId": "00000000-0000-0000-0000-000000000000",
	  "ClientId": "11111111-1111-1111-1111-111111111111",
	  "EmailDomains": [ "company.com" ]
	}
  }
}
```

- The **node name is the IdP id** (`workforce` above). There is no `Id` property. It may only contain letters, digits, `-` and `_`, and must be unique (case-insensitive). It is used internally, in the login page button class (`login-idp-<id>`) and in the `idp` claim of issued tokens.
- Any new node adds an IdP, so the list is dynamic.
- Optional `SortOrder` (integer, lowest first, default 0) controls the order of the IdPs; ties keep configuration order.
- The old array form (`"UpstreamIdps": [ ... ]`, or `UpstreamIdps__0__...` environment variables) is rejected at startup.
- A configuration error stops startup with a list of every problem found.

### Layering and environment variables

Precedence, lowest to highest: `appsettings.json` < `/config/customconfig.json` < environment variables < command line.

Because IdPs are named nodes, a single value can be overridden without touching the file. Use `__` as the separator:

```
UpstreamIdps__workforce__ClientId=22222222-2222-2222-2222-222222222222
UpstreamIdps__workforce__ClientSecret=<secret>
UpstreamIdps__OIDC-IdP__Enabled=true
```

Arrays are indexed: `UpstreamIdps__workforce__EmailDomains__0=company.com`.

## 2. Choosing a type

| | `EntraWorkforce` | `EntraExternalId` | `GenericOidc` |
|---|---|---|---|
| For | Employees and guests of a Microsoft Entra ID (work or school) tenant | Customers and consumers in a Microsoft Entra External ID tenant | Any other OpenID Connect provider |
| Typical users | Staff, partners invited as guests | Public-facing sign-up and sign-in | Okta, Keycloak, Auth0, a partner's IdP, Google and similar |
| Required settings | `TenantId` | `TenantId` (GUID) and `TenantSubdomain` | `Authority` (or `MetadataAddress`) |
| Metadata URL | `login.microsoftonline.com/<TenantId>/v2.0/...` | `<TenantSubdomain>.ciamlogin.com/<TenantId>/v2.0/...` | `<Authority>/.well-known/openid-configuration`, or `MetadataAddress` |
| Multi-tenant | Yes, with `organizations`, `common` or `consumers` | No | No |

**Use `EntraWorkforce`** when the people signing in belong to your organization's Entra tenant, or to many organizations (multi-tenant).

**Use `EntraExternalId`** when you run a customer-facing tenant on Entra External ID. Its sign-in endpoints use the `ciamlogin.com` domain, so it needs its own type.

**Use `GenericOidc`** for any provider that supports OpenID Connect discovery and the authorization code flow, other than the two Entra variants.

For the two Entra types, ProxyIdP derives a stable user identity from the tenant and object id (`tid:oid`) where available. This keeps the subject stable if a user's email or name changes.

## 3. Common properties

These apply to every type.

| Property | Required | Default | Description |
|---|---|---|---|
| `Type` | Yes | | `EntraWorkforce`, `EntraExternalId` or `GenericOidc`. |
| `DisplayName` | Yes | | Text on the IdP's button on the login page. |
| `Enabled` | No | `true` | A disabled IdP is ignored completely: no button, no routing, not usable as default. Handy for keeping a prepared entry in the file. |
| `IsDefault` | No | `false` | Used when no IdP button was chosen and the email did not match an IdP (also for an empty email). At most one IdP may be the default and it must be enabled. Without a default the login page asks the user to choose. |
| `ClientId` | Yes | | The application (client) id registered at the upstream IdP. |
| `ClientSecret` | No | none | For confidential clients. Leave out for public clients that rely on PKCE. Do not store real secrets in a baked-in file; supply them with an environment variable (`UpstreamIdps__<id>__ClientSecret`) or a mounted secret. |
| `UsePkce` | No | `true` | Sends a PKCE challenge to the upstream IdP. Either `ClientSecret` or `UsePkce` must be in effect. Keep it on unless the upstream IdP can't handle it. |
| `Scopes` | No | `openid`, `profile`, `email` | Scopes requested upstream. `openid` is always added if you leave it out. |
| `EmailDomains` | No | empty | Email domains that route straight to this IdP, e.g. `[ "company.com" ]`. Matching is case-insensitive, whitespace is trimmed and a leading `@` is ignored. May be empty or `null`. |

## 4. Type-specific properties

### EntraWorkforce

| Property | Required | Description |
|---|---|---|
| `TenantId` | Yes | Tenant GUID, a tenant domain such as `contoso.onmicrosoft.com`, or one of `organizations` (any work or school account), `common` (work, school and personal accounts) or `consumers` (personal accounts only). |

With `organizations`, `common` or `consumers`, tokens come from many tenants, so the issuer is checked against the Entra issuer pattern instead of a single fixed issuer. Use a specific tenant GUID when only one organization should sign in.

```json
"workforce": {
  "Type": "EntraWorkforce",
  "DisplayName": "Contoso (Entra)",
  "TenantId": "00000000-0000-0000-0000-000000000000",
  "ClientId": "11111111-1111-1111-1111-111111111111",
  "EmailDomains": [ "contoso.com" ],
  "UsePkce": true
}
```

### EntraExternalId

| Property | Required | Description |
|---|---|---|
| `TenantId` | Yes | The External ID tenant's GUID. A domain name is not accepted. |
| `TenantSubdomain` | Yes | The tenant's subdomain, the part before `.ciamlogin.com`. |

```json
"customers": {
  "Type": "EntraExternalId",
  "DisplayName": "Customers",
  "TenantSubdomain": "contosocustomers",
  "TenantId": "00000000-0000-0000-0000-000000000000",
  "ClientId": "11111111-1111-1111-1111-111111111111",
  "IsDefault": true,
  "UsePkce": true
}
```

Customers typically have arbitrary email domains, so this IdP is usually the default and has no `EmailDomains`.

### GenericOidc

| Property | Required | Description |
|---|---|---|
| `Authority` | Yes, unless `MetadataAddress` is set | The provider's issuer URL. Must be an absolute **HTTPS** URL. The discovery document is read from `<Authority>/.well-known/openid-configuration`. |
| `MetadataAddress` | No | Full URL of the discovery document. Use it when it isn't at the standard location. When set, it takes precedence over `Authority` for discovery and must also be HTTPS. |

```json
"partner": {
  "Type": "GenericOidc",
  "DisplayName": "Partner",
  "Authority": "https://idp.partner.example",
  "ClientId": "partner-client",
  "ClientSecret": "set-me-through-an-environment-variable",
  "EmailDomains": [ "partner.example" ],
  "UsePkce": true
}
```

## 5. How a user is routed to an IdP

1. The user clicks an **IdP button**. That IdP is used, and it must be enabled.
2. Otherwise the typed **email** is matched against each enabled IdP's `EmailDomains`.
3. If nothing matches and the optional **resolver API** (`IdpResolver` section) is enabled, it is asked which IdP the email belongs to.
4. If that gives no answer, the **default IdP** (`IsDefault`) is used.
5. With no default, the login page shows an error and asks the user to choose.

The email is also passed to the upstream IdP as `login_hint`, so it does not have to be typed twice. Each button has the CSS class `login-idp-<id>`, which you can use to style it.

## 6. Registering ProxyIdP at the upstream IdP

At every upstream IdP, register ProxyIdP as an application with:

- **Redirect URI**: `<Proxy:Issuer>/signin-upstream`, for example `https://login.example.com/signin-upstream`. It must match `Proxy:Issuer` exactly (a trailing slash is ignored).
- **Flow**: authorization code. ProxyIdP redeems the code on the server.
- **Public or confidential client**: for a public client set `UsePkce` to `true` and leave out `ClientSecret`. For a confidential client create a client secret upstream and supply it as `ClientSecret`.

## 7. Clients

The same file can define the applications that sign in through ProxyIdP, under `Clients`. See `examples/upstream/customconfig.json` and the main [README](../../ProxyIdP/README.md#configuring-clients).

## 8. Common mistakes

| Startup error | Cause and fix |
|---|---|
| Duplicate Id | Two nodes with names that differ only in case. Ids are case-insensitive. |
| Id may only contain letters, digits, `-` and `_` | Rename the node. |
| Only one IdP may have `IsDefault=true` | Set it on one IdP only. |
| The default IdP must be enabled | Enable it, or move `IsDefault` to an enabled IdP. |
| `TenantId` must be a GUID for EntraExternalId | Use the tenant GUID, not its domain name. |
| `TenantSubdomain` is required | Add the part before `.ciamlogin.com`. |
| `TenantId` is required for EntraWorkforce | Add a GUID, a domain, or `organizations`, `common` or `consumers`. |
| Authority (or MetadataAddress) must be an absolute HTTPS URL | Use `https://`. Plain `http://` is rejected. |
| Either ClientSecret or UsePkce must be configured | Turn `UsePkce` on, or add a `ClientSecret`. |
| `UpstreamIdps` must be an object keyed by IdP id | You used the old array form. Convert it to named nodes. |
