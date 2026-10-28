# Running ProxyIdP in a container

Build (from the solution root):

	docker build -f ProxyIdP/Dockerfile -t proxyidp .

Behind a TLS-terminating proxy (HTTP only in the container):

	docker run -p 8080:8080 -v proxyidp-data:/data `
	  -e Proxy__Issuer=https://idp.example.com `
	  -e ForwardedHeaders__Enabled=true `
	  proxyidp

Direct HTTPS with a mounted certificate:

	docker run -p 8443:8443 -v proxyidp-data:/data -v ./certs:/certs:ro `
	  -e ASPNETCORE_URLS="https://+:8443;http://+:8080" `
	  -e ASPNETCORE_Kestrel__Certificates__Default__Path=/certs/idp.pfx `
	  -e ASPNETCORE_Kestrel__Certificates__Default__Password=<password> `
	  -e Hosting__UseHttpsRedirection=true `
	  -e Proxy__Issuer=https://idp.example.com `
	  proxyidp

Notes:
- `/data` holds the SQLite DB and Data Protection keys; mount a volume to persist them.
- `Proxy__Issuer` must be the public URL. Supply secrets (e.g. `IdpResolver__ApiKey`) via environment variables.
- Health check: `GET /healthz`.
