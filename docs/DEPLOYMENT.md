# ProxyIdP Deployment Best Practices

ProxyIdP is a generic sample. Many hardening measures depend on where and how it is deployed, so they belong in the installation rather than in the code. This guide lists what to decide and configure when you deploy the container. Background and findings: [SECURITY-REVIEW.md](SECURITY-REVIEW.md).

> All snippets are **samples**. Adapt names, paths, networks and hosts to your environment.

## 1. Threat model

- One application, one team, one ProxyIdP instance, possibly on the same infrastructure as the application.
- Browser-facing endpoints are reachable from the internet. Server-to-server endpoints are only needed by the application.
- A compromised neighbouring workload, a writable shared volume or leaked environment variables are realistic threats. Treat ProxyIdP as holding signing keys and upstream IdP secrets.

## 2. Endpoint exposure

| Exposure | Endpoints |
| --- | --- |
| Public (user browsers) | `/connect/authorize`, `/login`, `/api/login`, `/api/login/config`, `/signin-upstream`, `/connect/endsession`, `/style/*`, `/customui/*` |
| Internal only (application) | `/connect/token`, `/connect/userinfo`, `/.well-known/openid-configuration`, `/.well-known/jwks.json` |
| Internal only (orchestrator) | `/healthz`, `/` |

Enforce this at the reverse proxy or ingress. If the application validates tokens via discovery/JWKS, give it an internal route to those two documents. Note that the `Issuer` value appears in discovery and tokens and must match what the application is configured with.

Sample (nginx, public server block):

```nginx
server {
	listen 443 ssl;
	server_name idp.example.com;

	location ~ ^/(connect/authorize|login|api/login|api/login/config|signin-upstream|connect/endsession|style/|customui/) {
		proxy_pass http://proxyidp:8080;
		proxy_set_header Host $host;
		proxy_set_header X-Forwarded-For $remote_addr;
		proxy_set_header X-Forwarded-Proto $scheme;
		limit_req zone=login burst=20 nodelay;
	}
	location / { return 404; }
}
```

The application reaches `http://proxyidp:8080` directly on the internal network for token, userinfo and JWKS.

## 3. TLS, issuer, hosts and proxies

- Terminate TLS at the proxy. Add HSTS and security headers there (see section 9) until the application emits them itself.
- Set `Proxy__Issuer` to the public HTTPS URL. Never leave `https://localhost:7103`.
- Set `AllowedHosts` to the issuer host name, not `*`.
- Enable `ForwardedHeaders__Enabled=true` only behind a proxy, and restrict the trusted proxy address or network. The sample code clears the trusted lists, so until that is changed make sure the container port is reachable **only** from the proxy.
- Keep `Hosting__UseHttpsRedirection=false` in the container when the proxy terminates TLS.

## 4. Secrets

- Do not bake secrets into the image or commit them to `customconfig.json`.
- Prefer, in order: workload identity / federated credentials (the resolver supports `AssertionFilePath`), a secret store (Key Vault, Kubernetes or Docker secrets mounted as files), then environment variables.
- Environment variables are visible through `docker inspect` and to anyone with orchestrator access. If you must use them, restrict who can read the runtime configuration.
- Use a long random client `Secret` (32+ bytes) for the application's client. Use separate secrets per environment and rotate on a schedule and after any suspected exposure.
- Upstream `ClientSecret` values: prefer public client + PKCE, or certificate/federated credentials, where the upstream IdP allows.

## 5. Signing keys and the Data Protection key ring

- The signing private keys are encrypted with ASP.NET Data Protection. The key ring (`DataProtection__KeyPath`, default `/data/keys`) is what decrypts them.
- Put the key ring on a volume that **only** ProxyIdP mounts, separate from the SQLite database volume and from anything the application can read.
- Protect the key ring at rest (certificate or Key Vault/KMS wrapping) so a copy of the volume alone is not enough. This requires a code or configuration extension point in the sample.
- Back up the key ring (and its protecting certificate) with the database. Losing the key ring makes the current code delete and regenerate signing keys, which invalidates every issued token. Monitor for that.
- Rotation is automatic (`SigningKeys` section). To force-rotate after a suspected compromise, remove the keys and restart (all sessions end).

## 6. Data and volumes

| Path | Purpose | Recommended |
| --- | --- | --- |
| `/data` | SQLite DB, key ring | Writable by ProxyIdP only. Separate volumes for DB and `/data/keys`. |
| `/config` | `customconfig.json` | Read-only mount, or not mounted. Config is hot-reloaded. |
| `/app/plugins` | Plugin DLLs | Not mounted (bake into image) or read-only. |
| `/app/style`, `/app/customui` | UI assets | Read-only mount, or bake into the image. |

- Run **one replica**. SQLite and the in-process key and plugin state assume it.
- The database holds short-lived authorization state and claims (including email). It can be rebuilt: users sign in again. Back it up only if you need continuity. Cover it in your privacy documentation.

## 7. Plugins and custom UI

- Leave `Plugins__Enabled=false` unless you need a plugin.
- A plugin is arbitrary code running inside the IdP with access to all claims and secrets. Review it like application code, build it in CI, and ship it **inside the image**, not through a writable volume. The sample loads plugins from memory and reloads on change, so anyone who can write the plugin folder can run code in the IdP.
- Custom UI HTML runs on your IdP origin. Review it like any front-end code, avoid third-party scripts, and serve it with a strict CSP.

## 8. Container runtime hardening

Sample (docker compose):

```yaml
services:
  proxyidp:
	image: proxyidp@sha256:<pinned-digest>
	read_only: true
	tmpfs: [/tmp]
	cap_drop: [ALL]
	security_opt: ["no-new-privileges:true"]
	user: "${APP_UID}:${APP_UID}"
	mem_limit: 512m
	cpus: 1
	environment:
	  Proxy__Issuer: https://idp.example.com
	  AllowedHosts: idp.example.com
	  Plugins__Enabled: "false"
	  ConnectionStrings__Default: Data Source=/data/db/proxyidp.db
	  DataProtection__KeyPath: /data/keys
	secrets: [upstream_client_secret]
	volumes:
	  - idp-db:/data/db
	  - idp-keys:/data/keys
	  - ./config:/config:ro
	networks: [idp-internal, edge]
	expose: ["8080"]
secrets:
  upstream_client_secret:
	file: ./secrets/upstream_client_secret
volumes: { idp-db: {}, idp-keys: {} }
networks:
  idp-internal: { internal: true }
  edge: {}
```

- Pin base and application images by digest and scan them in CI. Rebuild when the base image is updated.
- Do not publish port 8080 on the host. Expose it only to the proxy and the application network.
- Kubernetes equivalents: `readOnlyRootFilesystem`, `runAsNonRoot`, `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`, resource limits, one replica.

## 9. Network policy

- Ingress: only from the reverse proxy (public endpoints) and from the application (internal endpoints).
- Egress: only to the configured upstream metadata/token endpoints (for example `login.microsoftonline.com`) and the resolver endpoint, if used.
- Block the cloud instance-metadata address and private ranges from the container.
- Headers to add at the proxy for the public endpoints:

```nginx
add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;
add_header X-Content-Type-Options "nosniff" always;
add_header Referrer-Policy "no-referrer" always;
add_header Content-Security-Policy "default-src 'self'; frame-ancestors 'none'; form-action 'self' https://login.microsoftonline.com" always;
add_header Cache-Control "no-store" always;
```

Adjust `form-action` to your upstream IdPs.

## 10. Client configuration

- Define only the clients the application needs. Use exact redirect and post-logout URIs. No wildcards.
- Keep `AllowedScopes` minimal.
- Use a confidential client with a strong secret where the application can keep one. Otherwise use a public client with PKCE (always required).
- Keep `Proxy__TokenLifetimeMinutes` short (there is no revocation).
- In the application, validate `iss`, `aud`, signature, expiry and `nonce`, and request only the scopes and claims it uses. Avoid logging tokens and claims.

## 11. Upstream IdP configuration

- For Entra, prefer a specific tenant. Avoid `common`/`organizations` unless required, and then restrict allowed tenants (`tid`) at the application or via a plugin until the proxy supports an allow-list.
- Register exactly `https://<issuer>/signin-upstream` at each IdP.
- Limit upstream scopes to what the application needs.
- If you use the resolver API, give it a dedicated least-privilege credential, HTTPS only, and a public address.

## 12. Logging, monitoring and privacy

- Keep `Microsoft.AspNetCore` at `Warning`. Do not enable debug logging in production. Ship logs to a store with access control.
- Alert on: repeated failed client authentication or `invalid_grant`, rejected redirect URIs, signing-key regeneration, plugin load failures, spikes on `/connect/authorize` or `/login`, and container restarts.
- Document that email, name and upstream identifiers pass through ProxyIdP and are stored briefly in its database.

## 13. Updates and incident response

- Rebuild and redeploy on base-image and dependency updates.
- Suspected key-ring or volume leak: rotate signing keys (forces sign-out), rotate the client secret, the upstream client secrets and the resolver credential, and review access to the volumes.
- Suspected plugin or config tampering: redeploy from a known image, wipe writable plugin/config volumes, and rotate all secrets.

## 14. Pre-go-live checklist

- [ ] `Proxy__Issuer` is the public HTTPS URL, and `AllowedHosts` is set to that host.
- [ ] TLS terminates at the proxy with HSTS. Security headers are set.
- [ ] Only the public endpoints are reachable from the internet. Token, userinfo, JWKS, discovery and `/healthz` are internal.
- [ ] Rate limiting is configured at the proxy for authorize, login, api/login and token.
- [ ] ForwardedHeaders is enabled only behind the proxy. The container port is not reachable from anywhere else.
- [ ] Plugins are disabled, or shipped in the image and reviewed. Plugin, config and UI volumes are read-only or absent.
- [ ] The key ring is on a separate ProxyIdP-only volume, protected at rest, and backed up. Alerting on key regeneration exists.
- [ ] Secrets come from files, a secret store or workload identity. None are in the image, git, or `customconfig.json`.
- [ ] Client has exact redirect URIs, minimal scopes, a strong secret (or PKCE public client).
- [ ] Entra tenant is specific, or tenant restrictions are enforced elsewhere.
- [ ] Container runs non-root, read-only root filesystem, capabilities dropped, resource limits, single replica, image pinned and scanned.
- [ ] Ingress and egress network policy is applied. The metadata address is blocked.
- [ ] Logs contain no tokens, claims or secrets. Alerts are set up.
- [ ] Backup and restore, and the key and secret rotation runbook, have been tested.
