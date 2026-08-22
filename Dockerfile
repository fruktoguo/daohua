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
COPY src/Daoxuan.Domain/Daoxuan.Domain.csproj src/Daoxuan.Domain/
COPY src/Daoxuan.Protocol/Daoxuan.Protocol.csproj src/Daoxuan.Protocol/
COPY src/Daoxuan.Server/Daoxuan.Server.csproj src/Daoxuan.Server/
COPY src/Daoxuan.ComputeNode/Daoxuan.ComputeNode.csproj src/Daoxuan.ComputeNode/
COPY config/game-data/ config/game-data/
COPY vendor/YuoCore/README.md vendor/YuoCore/README.md
COPY vendor/YuoCore/src/Directory.Build.props vendor/YuoCore/src/Directory.Build.props
COPY vendor/YuoCore/src/YuoCore.Abstractions/YuoCore.Abstractions.csproj vendor/YuoCore/src/YuoCore.Abstractions/
COPY vendor/YuoCore/src/YuoCore.Analyzers/YuoCore.Analyzers.csproj vendor/YuoCore/src/YuoCore.Analyzers/
COPY vendor/YuoCore/src/YuoCore.Ecs/YuoCore.Ecs.csproj vendor/YuoCore/src/YuoCore.Ecs/
COPY vendor/YuoCore/src/YuoCore.Persistence/YuoCore.Persistence.csproj vendor/YuoCore/src/YuoCore.Persistence/
COPY vendor/YuoCore/src/YuoCore.Runtime/YuoCore.Runtime.csproj vendor/YuoCore/src/YuoCore.Runtime/
RUN dotnet restore src/Daoxuan.Server/Daoxuan.Server.csproj --nologo \
    && dotnet restore src/Daoxuan.ComputeNode/Daoxuan.ComputeNode.csproj --nologo
COPY src/Daoxuan.Domain/ src/Daoxuan.Domain/
COPY src/Daoxuan.Protocol/ src/Daoxuan.Protocol/
COPY src/Daoxuan.ComputeNode/ src/Daoxuan.ComputeNode/
COPY src/Daoxuan.Server/ src/Daoxuan.Server/
COPY config/game-data/ config/game-data/
COPY vendor/YuoCore/src/ vendor/YuoCore/src/
RUN dotnet publish src/Daoxuan.Server/Daoxuan.Server.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    --nologo \
    -p:ContinuousIntegrationBuild=true \
    -p:InformationalVersion="${BUILD_VERSION}"
RUN dotnet publish src/Daoxuan.ComputeNode/Daoxuan.ComputeNode.csproj \
    --configuration Release \
    --no-restore \
    --output /app/compute \
    --nologo \
    -p:ContinuousIntegrationBuild=true \
    -p:InformationalVersion="${BUILD_VERSION}"
COPY --from=frontend-build /src/frontend/dist/ /app/publish/wwwroot/
RUN test ! -d /app/publish/data \
    || ! find /app/publish/data -type f -print -quit | grep -q .

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
RUN apk add --no-cache curl icu-libs krb5-libs tzdata \
    && addgroup -S -g 10001 daoxuan \
    && adduser -S -D -H -u 10001 -G daoxuan daoxuan
WORKDIR /app
ARG BUILD_DATE=unknown
ARG BUILD_VERSION=dev
ARG VCS_REF=unknown
LABEL org.opencontainers.image.title="道化太初" \
      org.opencontainers.image.description="道化太初 Web 与游戏服务端" \
      org.opencontainers.image.created="${BUILD_DATE}" \
      org.opencontainers.image.version="${BUILD_VERSION}" \
      org.opencontainers.image.revision="${VCS_REF}"
COPY --from=server-build --chown=10001:10001 /app/publish/ ./
COPY --from=server-build --chown=10001:10001 /app/compute/ /app/compute/
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
USER 10001:10001
EXPOSE 8080
STOPSIGNAL SIGTERM
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/readyz >/dev/null || exit 1
ENTRYPOINT ["dotnet", "Daoxuan.Server.dll"]
