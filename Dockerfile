
ARG NODE_IMAGE=node:22-alpine
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0-alpine
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-alpine

FROM ${NODE_IMAGE} AS frontend-build
WORKDIR /src/frontend
ARG NPM_CONFIG_REGISTRY=https://registry.npmmirror.com
ARG BUILD_VERSION=dev
ARG REQUIRE_PRIVATE_MODULES=0
ENV VITE_APP_VERSION=${BUILD_VERSION}
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --registry="${NPM_CONFIG_REGISTRY}" --ignore-scripts --no-audit --no-fund
COPY frontend/ ./
COPY config/game-data/ /src/config/game-data/
COPY scripts/gen-simulator-catalog.mjs /src/scripts/gen-simulator-catalog.mjs
COPY scripts/export-i18n-csv.mjs /src/scripts/export-i18n-csv.mjs
COPY scripts/gen-asset-manifest.mjs /src/scripts/gen-asset-manifest.mjs
COPY docs/design/战斗演武画廊/vfx/ /src/docs/design/战斗演武画廊/vfx/
RUN if [ "${REQUIRE_PRIVATE_MODULES}" = "1" ]; then \
      test -f /src/frontend/src/private-impl/index.ts \
      && ./node_modules/.bin/tsc -p tsconfig.private-impl.json --noEmit; \
    fi \
    && test -n "$(find /src/docs/design/战斗演武画廊/vfx/abilities /src/docs/design/战斗演武画廊/vfx/consumables -maxdepth 1 -name '*.js' -print -quit)" \
    && npm run build \
    && grep -rq "DaohuaVFX" /src/frontend/dist/assets/

FROM ${DOTNET_SDK_IMAGE} AS dotnet-build
WORKDIR /src
ARG BUILD_VERSION=dev
ARG REQUIRE_PRIVATE_MODULES=0
COPY global.json Directory.Build.props ./
# 直接复制新基线源码；.dockerignore 排除 bin/obj、运行数据、旧文档和 secrets。
COPY src/ ./src/
COPY config/game-data/ ./config/game-data/
COPY vendor/YuoCore/src/ ./vendor/YuoCore/src/
RUN dotnet restore src/Server/Server.csproj --nologo \
    && dotnet restore src/ComputeNode/ComputeNode.csproj --nologo
# DaoHuaPrivateRoot 指向 staging 拼装的私有模块目录（内含 server/**/*.cs）；
# 当 REQUIRE_PRIVATE_MODULES=1（生产镜像）时 MSBuild 强制校验至少编入一份私有服务端源码，禁止静默降级。
RUN dotnet publish src/Server/Server.csproj \
    --configuration Release \
    --no-restore \
    --output /out/server \
    --nologo \
    -p:ContinuousIntegrationBuild=true \
    -p:DaoHuaObservability=false \
    -p:DaoHuaPrivateRoot=/src/src/Private \
    -p:DaoHuaRequirePrivate="${REQUIRE_PRIVATE_MODULES}" \
    -p:IncludeSourceRevisionInInformationalVersion=false \
    -p:InformationalVersion="${BUILD_VERSION}"
RUN dotnet publish src/ComputeNode/ComputeNode.csproj \
    --configuration Release \
    --no-restore \
    --output /out/compute \
    --nologo \
    -p:ContinuousIntegrationBuild=true \
    -p:DaoHuaObservability=false \
    -p:IncludeSourceRevisionInInformationalVersion=false \
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
HEALTHCHECK --interval=10s --timeout=3s --start-period=3s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/healthz >/dev/null || exit 1
ENTRYPOINT ["dotnet", "Server.dll"]

FROM runtime-base AS compute-runtime
COPY --from=dotnet-build --chown=10001:10001 /out/compute/ ./
RUN test ! -d /app/data || ! find /app/data -type f -print -quit | grep -q .
EXPOSE 8090
HEALTHCHECK --interval=10s --timeout=3s --start-period=3s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8090/healthz >/dev/null || exit 1
ENTRYPOINT ["dotnet", "DaoHua.ComputeNode.dll"]

# 默认目标为主服务；Compose 的计算节点显式选择 compute-runtime。
FROM server-runtime
