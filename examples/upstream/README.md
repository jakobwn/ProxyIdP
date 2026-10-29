# Upstream IdPs example

An image that extends ProxyIdP and bakes in a `customconfig.json` with upstream identity providers (`UpstreamIdps`) and a client (`Clients`). See [CONFIGURING-IDPS.md](CONFIGURING-IDPS.md) for a full explanation of the settings.

The base image ships with no IdPs. This example supplies them through `/config/customconfig.json`.

## Build

```
docker build -t proxyidp-upstream examples/upstream
```

Build on a locally built base image instead of the published one:

```
docker build -t proxyidp -f ProxyIdP/Dockerfile .
docker build -t proxyidp-upstream --build-arg BASE_IMAGE=proxyidp examples/upstream
```

## Run

```
docker run -p 8080:8080 -v proxyidp-data:/data -e Proxy__Issuer=https://localhost:8080 proxyidp-upstream
```

## Overriding the configuration

- Mount your own file over the baked-in one: `-v ./my-config:/config`. A volume on `/config` hides the baked-in file, so the mounted directory must contain `customconfig.json`.
- Override single values with environment variables, which win over the file, e.g. `-e UpstreamIdps__workforce__ClientId=...` or `-e Clients__example-app__Secret=...`.

Precedence: appsettings < `/config/customconfig.json` < environment variables < command line.

Replace the placeholder redirect URIs of `example-app` and the tenant and client ids before real use.
