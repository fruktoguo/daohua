
ARG NODE_IMAGE=node:22-alpine
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-alpine
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-alpine

FROM ${NODE_IMAGE} AS frontend-build
WORKDIR /src/frontend
ARG NPM_CONFIG_REGISTRY=https://registry.npmjs.org
ARG BUILD_VERSION=dev
ENV VITE_APP_VERSION=${BUILD_VERSION}
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --registry="${NPM_CONFIG_REGISTRY}" --ignore-scripts --no-audit --no-fund
COPY frontend/ ./
COPY scripts/export-i18n-csv.mjs /src/scripts/export-i18n-csv.mjs
RUN npm run build

FROM ${DOTNET_SDK_IMAGE} AS dotnet-build
WORKDIR /src
ARG BUILD_VERSION=dev
COPY global.json Directory.Build.props ./
# 直接复制新基线源码；.dockerignore 排除 bin/obj、运行数据、旧文档和 secrets。
COPY src/ ./src/
COPY config/game-data/ ./config/game-data/
COPY vendor/YuoCore/src/ ./vendor/YuoCore/src/
RUN dotnet restore src/Server/Server.csproj --nologo \
    && dotnet restore src/ComputeNode/ComputeNode.csproj --nologo
# DaoHuaPrivateRoot 指向 staging 拼装的私有模块目录：存在则源码注入 Server.Clean（完整口径），
# 开源自建上下文无该目录，MSBuild 通配零匹配自动降级为开源版（单一 Dockerfile 双口径）。
RUN dotnet publish src/Server/Server.csproj \
    --configuration Release \
    --no-restore \
    --output /out/server \
    --nologo \
    -p:ContinuousIntegrationBuild=true \
    -p:DaoHuaObservability=false \
    -p:DaoHuaPrivateRoot=/src/src/Private \
    -p:InformationalVersion="${BUILD_VERSION}"
RUN dotnet publish src/ComputeNode/ComputeNode.csproj \
    --configuration Release \
    --no-restore \
    --output /out/compute \
    --nologo \
    -p:ContinuousIntegrationBuild=true \
    -p:DaoHuaObservability=false \
    -p:InformationalVersion="${BUILD_VERSION}"

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime-base
RUN apk add --no-cache curl icu-libs tzdata \
    && addgroup -S -g 10001 daohua \
    && adduser -S -D -H -u 10001 -G daohua daohua
WORKDIR /app
ARG BUILD_DATE=unknown
ARG BUILD_VERSION=dev
ARG VCS_REF=unknown
LABEL org.opencontainers.image.title="道化太初" \
      org.opencontainers.image.description="道化太初商业级游戏服务" \
      org.opencontainers.image.created="${BUILD_DATE}" \
      org.opencontainers.image.version="${BUILD_VERSION}" \
      org.opencontainers.image.revision="${VCS_REF}"
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_ENVIRONMENT=Production
USER 10001:10001
STOPSIGNAL SIGTERM

FROM runtime-base AS server-runtime
COPY --from=dotnet-build --chown=10001:10001 /out/server/ ./
COPY --from=frontend-build --chown=10001:10001 /src/frontend/dist/ ./wwwroot/
RUN test ! -d /app/data || ! find /app/data -type f -print -quit | grep -q .
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/healthz >/dev/null || exit 1
ENTRYPOINT ["dotnet", "Server.dll"]

FROM runtime-base AS compute-runtime
COPY --from=dotnet-build --chown=10001:10001 /out/compute/ ./
RUN test ! -d /app/data || ! find /app/data -type f -print -quit | grep -q .
EXPOSE 8090
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8090/healthz >/dev/null || exit 1
ENTRYPOINT ["dotnet", "DaoHua.ComputeNode.dll"]

# 默认目标为主服务；Compose 的计算节点显式选择 compute-runtime。
FROM server-runtime
