# ProxyIdP

## Upstream sign-in behavior

- **login_hint**: when the user enters an email on the login page, that email is forwarded to the upstream IdP as `login_hint`, so it does not have to be typed twice. A `login_hint` sent by the client to `/connect/authorize` is used when no email was typed. Choosing an IdP button without an email sends no hint.
- **Default IdP**: set `"IsDefault": true` on one enabled entry in `UpstreamIdps`. It is used when no IdP button was chosen and the email did not match an IdP (by `EmailDomains` or the optional resolver API), including an empty email. Without a default, the login page shows an error and asks the user to choose. At most one IdP may be the default and it must be enabled; otherwise startup fails.

## Database note

The schema is created with `EnsureCreated`, which does not alter existing tables. The `PendingAuthorizations` table gained a `LoginHint` column, so delete an existing `proxyidp.db` (or add the column manually) after upgrading.
