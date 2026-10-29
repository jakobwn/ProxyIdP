ARG BASE_IMAGE=ghcr.io/jakobwn/proxyidp:latest
FROM ${BASE_IMAGE}

COPY --chown=$APP_UID:$APP_UID customconfig.json /config/customconfig.json
