# syntax=docker/dockerfile:1.7

ARG NODE_IMAGE=node:22-alpine
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-alpine
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-alpine

FROM ${NODE_IMAGE} AS frontend-build
WORKDIR /src/frontend
ARG NPM_CONFIG_REGISTRY=https://registry.npmjs.org
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --registry="${NPM_CONFIG_REGISTRY}"
COPY frontend/ ./
RUN npm run build

FROM ${DOTNET_SDK_IMAGE} AS server-build
WORKDIR /src
ARG BUILD_VERSION=dev
COPY global.json ./
COPY src/Xiuxian.Domain/Xiuxian.Domain.csproj src/Xiuxian.Domain/
COPY src/Xiuxian.Protocol/Xiuxian.Protocol.csproj src/Xiuxian.Protocol/
COPY src/Xiuxian.Server/Xiuxian.Server.csproj src/Xiuxian.Server/
COPY config/game-data/ config/game-data/
COPY vendor/YuoCore/README.md vendor/YuoCore/README.md
COPY vendor/YuoCore/src/Directory.Build.props vendor/YuoCore/src/Directory.Build.props
COPY vendor/YuoCore/src/YuoCore.Abstractions/YuoCore.Abstractions.csproj vendor/YuoCore/src/YuoCore.Abstractions/
COPY vendor/YuoCore/src/YuoCore.Analyzers/YuoCore.Analyzers.csproj vendor/YuoCore/src/YuoCore.Analyzers/
COPY vendor/YuoCore/src/YuoCore.Ecs/YuoCore.Ecs.csproj vendor/YuoCore/src/YuoCore.Ecs/
COPY vendor/YuoCore/src/YuoCore.Persistence/YuoCore.Persistence.csproj vendor/YuoCore/src/YuoCore.Persistence/
COPY vendor/YuoCore/src/YuoCore.Runtime/YuoCore.Runtime.csproj vendor/YuoCore/src/YuoCore.Runtime/
RUN dotnet restore src/Xiuxian.Server/Xiuxian.Server.csproj --nologo
COPY src/Xiuxian.Domain/ src/Xiuxian.Domain/
COPY src/Xiuxian.Protocol/ src/Xiuxian.Protocol/
COPY src/Xiuxian.Server/ src/Xiuxian.Server/
COPY config/game-data/ config/game-data/
COPY vendor/YuoCore/src/ vendor/YuoCore/src/
RUN dotnet publish src/Xiuxian.Server/Xiuxian.Server.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    --nologo \
    -p:ContinuousIntegrationBuild=true \
    -p:InformationalVersion="${BUILD_VERSION}"
COPY --from=frontend-build /src/frontend/dist/ /app/publish/wwwroot/
RUN test ! -d /app/publish/data \
    || ! find /app/publish/data -type f -print -quit | grep -q .

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
RUN apk add --no-cache curl icu-libs tzdata \
    && addgroup -S -g 10001 xiuxian \
    && adduser -S -D -H -u 10001 -G xiuxian xiuxian
WORKDIR /app
ARG BUILD_DATE=unknown
ARG BUILD_VERSION=dev
ARG VCS_REF=unknown
LABEL org.opencontainers.image.title="太虚仙途" \
      org.opencontainers.image.description="太虚仙途 Web 与游戏服务端" \
      org.opencontainers.image.created="${BUILD_DATE}" \
      org.opencontainers.image.version="${BUILD_VERSION}" \
      org.opencontainers.image.revision="${VCS_REF}"
COPY --from=server-build --chown=10001:10001 /app/publish/ ./
RUN mkdir -p /app/data \
    && chown -R 10001:10001 /app/data
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
USER 10001:10001
EXPOSE 8080
VOLUME ["/app/data"]
STOPSIGNAL SIGTERM
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/readyz >/dev/null || exit 1
ENTRYPOINT ["dotnet", "Xiuxian.Server.dll"]
