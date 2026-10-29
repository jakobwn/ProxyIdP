# Custom UI example

An image that extends ProxyIdP and bakes in a custom login UI (`customui/index.html`, `login.js`, `login.css`, `img/`). The sample looks like the default login page.

## Build

```
docker build -t proxyidp-customui examples/customui
```

Build on a locally built base image instead of the published one:

```
docker build -t proxyidp -f ProxyIdP/Dockerfile .
docker build -t proxyidp-customui --build-arg BASE_IMAGE=proxyidp examples/customui
```

## Run

```
docker run -p 8080:8080 -v proxyidp-data:/data proxyidp-customui
```

The image sets `Ui__CustomUi__Enabled=true`; the files are served from `/app/customui`.
Mounting a volume on `/app/customui` hides the baked-in files.
