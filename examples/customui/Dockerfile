ARG BASE_IMAGE=ghcr.io/jakobwn/proxyidp:latest
FROM ${BASE_IMAGE}

COPY --chown=$APP_UID:$APP_UID customui/ /app/customui/

ENV Ui__CustomUi__Enabled=true
