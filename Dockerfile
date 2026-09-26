# syntax=docker/dockerfile:1
# Self-contained: builds from this repo's own tree only. Nothing outside the build context is referenced.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0
WORKDIR /src
COPY . .
# Framework-dependent publish. Not AOT: the OpenAPI merge and configuration binding are
# reflection-based, so trimming would break them.
RUN dotnet publish src/ApiGateway/ApiGateway.csproj -c Release -a $TARGETARCH \
      -p:Version=$VERSION -p:UseAppHost=false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
ARG VERSION=0.0.0
ARG REVISION=unknown
LABEL org.opencontainers.image.title="api-gateway" \
      org.opencontainers.image.source="https://github.com/bklooste/api-gateway" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION"
WORKDIR /app
COPY --from=build /app .
# The chiselled base defaults to the non-root `app` user; stated explicitly so it can't regress.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# The route table is mounted at /config/routes.json and watched for edits. inotify is unreliable on
# mounted filesystems — a Kubernetes ConfigMap updates by swapping the ..data symlink, which the
# default watcher misses — so poll instead. Polling one small file every few seconds costs nothing.
ENV DOTNET_USE_POLLING_FILE_WATCHER=true
# No shell/curl in a chiselled image, so the app probes itself.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["dotnet", "ApiGateway.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "ApiGateway.dll"]
