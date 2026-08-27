#!/usr/bin/env bash
# 新基线最小充分门禁：代码、领域/服务测试、真实 PostgreSQL/WAL 容量、前端和 Compose 配置。

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

if [[ ! -f vendor/YuoCore/src/YuoCore.Runtime/YuoCore.Runtime.csproj ]]; then
  printf 'YuoCore submodule 未初始化，请执行 git submodule update --init --recursive。\n' >&2
  exit 1
fi

printf '%s\n' '==> 检查真实工具链'
dotnet --version
node --version
npm --version

printf '%s\n' '==> 安装前端锁定依赖'
npm --prefix frontend ci --ignore-scripts --no-audit --no-fund

printf '%s\n' '==> Release 构建'
dotnet build Game.slnx --configuration Release --disable-build-servers --nologo

if [[ -f tests/Domain.Tests/Domain.Tests.csproj ]]; then
  printf '%s\n' '==> 领域测试'
  dotnet test tests/Domain.Tests/Domain.Tests.csproj --configuration Release --no-build --disable-build-servers --nologo
fi
if [[ -f tests/Server.Tests/Server.Tests.csproj ]]; then
  printf '%s\n' '==> 服务端测试'
  dotnet test tests/Server.Tests/Server.Tests.csproj --configuration Release --no-build --disable-build-servers --nologo
fi
if [[ -f src/DataTool/DataTool.csproj ]]; then
  printf '%s\n' '==> 游戏数据校验'
  dotnet run --project src/DataTool/DataTool.csproj --configuration Release --no-build -- validate config/game-data
fi

printf '%s\n' '==> 校验 Compose 配置'
if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  docker compose -f compose.yaml config >/dev/null
  DB_PASSWORD=verify-db-password \
  APP_IMAGE=daohua:verify \
  COMPUTE_IMAGE=daohua-compute:verify \
  COMPUTE_TOKEN=verify-compute-token \
    docker compose -f deploy/compose.prod.yaml config --quiet
else
  printf '%s\n' 'Docker 不可用，跳过 Compose 运行时检查。'
fi

printf '%s\n' '==> 启动本地 PostgreSQL（若尚未运行）'
DB_HOST="${DB_HOST:-127.0.0.1}"
DB_PORT="${DB_PORT:-5432}"
if ! (echo >/dev/tcp/"$DB_HOST"/"$DB_PORT") 2>/dev/null; then
  if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    docker compose -f compose.yaml up -d db
    for _ in {1..60}; do
      if (echo >/dev/tcp/"$DB_HOST"/"$DB_PORT") 2>/dev/null; then break; fi
      sleep 1
    done
  fi
fi

if [[ -f src/LoadTests/LoadTests.csproj ]]; then
  printf '%s\n' '==> 2c4g/40g 容量目标门禁（真实 PostgreSQL/WAL）'
  dotnet run --project src/LoadTests/LoadTests.csproj --configuration Release --no-build -- \
    --scenario capacity-target \
    --require-database \
    --database "${DAOHUA_DATABASE_URL:-Host=${DB_HOST};Port=${DB_PORT};Database=daohua;Username=daohua;Password=${DB_PASSWORD:-daohua-local-dev};Maximum Pool Size=16;Timeout=5;Command Timeout=30}"
fi

printf '%s\n' '==> 前端类型检查与生产构建'
npm --prefix frontend run build

if [[ "${VERIFY_DOCKER:-0}" == "1" ]]; then
  printf '%s\n' '==> 生产镜像构建（VERIFY_DOCKER=1）'
  scripts/docker-build-prod.sh --allow-dirty --no-pull --release-tag verify
fi

printf '%s\n' '全部验证完成。'
