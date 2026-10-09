#!/usr/bin/env bash
# 合并必跑最小充分门禁：工具链、Release 构建、Domain/Ecs/Server 三套测试（自动供给
# daohua_market_test 集成测试库）、DataTool 校验、Compose 配置与前端构建。
# 容量门禁不在合并必跑内：容量相关变更与发布前，在本地或受控测试库执行
# scripts/capacity-target.sh（AGENTS.md §9）；本脚本不执行远程发布或生产恢复验证。

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
npm --prefix frontend ci --include=dev --ignore-scripts --no-audit --no-fund

printf '%s\n' '==> Release 构建'
dotnet build Game.slnx --configuration Release --disable-build-servers --nologo

if [[ -f tests/Domain.Tests/Domain.Tests.csproj ]]; then
  printf '%s\n' '==> 领域测试'
  dotnet test tests/Domain.Tests/Domain.Tests.csproj --configuration Release --no-build --disable-build-servers --nologo
fi
if [[ -f tests/Ecs.Tests/Ecs.Tests.csproj ]]; then
  printf '%s\n' '==> ECS 运行时与适配边界测试'
  dotnet test tests/Ecs.Tests/Ecs.Tests.csproj --configuration Release --no-build --disable-build-servers --nologo
fi
if [[ -f tests/Server.Tests/Server.Tests.csproj ]]; then
  # PostgresIntegration 集成测试需要专用库：本地 PG 可达时幂等创建 daohua_market_test
  # 并导出 DAOHUA_MARKET_TEST_CONNECTION；未供给成功时相关测试失败并报明所需环境变量。
  printf '%s\n' '==> 供给 PostgreSQL 集成测试库'
  DB_HOST="${DB_HOST:-127.0.0.1}"
  DB_PORT="${DB_PORT:-5432}"
  DB_USER="${DB_USER:-daohua}"
  DB_PASSWORD="${DB_PASSWORD:-daohua-local-dev}"
  DB_CONTAINER="${DB_CONTAINER:-daohua-local-db}"
  pg_admin() {
    if command -v docker >/dev/null 2>&1 \
        && docker ps --format '{{.Names}}' 2>/dev/null | grep -qx "$DB_CONTAINER"; then
      docker exec -i "$DB_CONTAINER" psql -X -t -A -v ON_ERROR_STOP=1 -U "$DB_USER" -d postgres "$@"
    elif command -v psql >/dev/null 2>&1; then
      PGPASSWORD="$DB_PASSWORD" psql -h "$DB_HOST" -p "$DB_PORT" -X -t -A -v ON_ERROR_STOP=1 -U "$DB_USER" -d postgres "$@"
    else
      return 127
    fi
  }

  if ! (echo >/dev/tcp/"$DB_HOST"/"$DB_PORT") 2>/dev/null; then
    if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
      docker compose -f compose.yaml up -d db
      for _ in {1..60}; do
        if (echo >/dev/tcp/"$DB_HOST"/"$DB_PORT") 2>/dev/null; then break; fi
        sleep 1
      done
    fi
  fi

  if (echo >/dev/tcp/"$DB_HOST"/"$DB_PORT") 2>/dev/null \
      && [[ -z "${DAOHUA_MARKET_TEST_CONNECTION:-}" ]]; then
    TEST_DB_NAME="${TEST_DB_NAME:-daohua_market_test}"
    TEST_DB_EXISTS="$(pg_admin -c "SELECT 1 FROM pg_database WHERE datname='${TEST_DB_NAME}';" 2>/dev/null || true)"
    if [[ "$TEST_DB_EXISTS" != "1" ]]; then
      pg_admin -c "CREATE DATABASE ${TEST_DB_NAME};" >/dev/null 2>&1 || true
      TEST_DB_EXISTS="$(pg_admin -c "SELECT 1 FROM pg_database WHERE datname='${TEST_DB_NAME}';" 2>/dev/null || true)"
    fi
    if [[ "$TEST_DB_EXISTS" == "1" ]]; then
      export DAOHUA_MARKET_TEST_CONNECTION="Host=${DB_HOST};Port=${DB_PORT};Database=${TEST_DB_NAME};Username=${DB_USER};Password=${DB_PASSWORD};Maximum Pool Size=16;Timeout=5;Command Timeout=30"
      printf '已导出 DAOHUA_MARKET_TEST_CONNECTION（库 %s）。\n' "$TEST_DB_NAME"
    else
      printf '警告: 无法供给集成测试库 %s；PostgresIntegration 测试将失败。\n' "$TEST_DB_NAME" >&2
      printf '      请手动创建该库，或预设 DAOHUA_MARKET_TEST_CONNECTION。\n' >&2
    fi
  elif (echo >/dev/tcp/"$DB_HOST"/"$DB_PORT") 2>/dev/null; then
    printf '检测到预设 DAOHUA_MARKET_TEST_CONNECTION，直接使用。\n'
  else
    printf '警告: PostgreSQL 不可达且未设 DAOHUA_MARKET_TEST_CONNECTION；PostgresIntegration 测试将失败。\n' >&2
  fi

  printf '%s\n' '==> 服务端测试'
  dotnet test tests/Server.Tests/Server.Tests.csproj --configuration Release --no-build --disable-build-servers --nologo
fi
if [[ -f src/DataTool/DataTool.csproj ]]; then
  printf '%s\n' '==> 游戏数据校验'
  dotnet run --project src/DataTool/DataTool.csproj --configuration Release --no-build -- validate config/game-data --strict
fi

printf '%s\n' '==> 校验 Compose 配置'
if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  docker compose -f compose.yaml config >/dev/null
  DB_BOOTSTRAP_USER=daohua_bootstrap \
  DB_BOOTSTRAP_PASSWORD=verify-bootstrap-password \
  DB_APP_USER=daohua_api_login \
  DB_APP_PASSWORD=verify-app-password \
  DB_WORKER_USER=daohua_worker_login \
  DB_WORKER_PASSWORD=verify-worker-password \
  APP_IMAGE=daohua:verify \
  COMPUTE_IMAGE=daohua-compute:verify \
  COMPUTE_TOKEN=verify-compute-token \
    docker compose -f deploy/compose.prod.yaml config --quiet
else
  printf '%s\n' 'Docker 不可用，跳过 Compose 运行时检查。'
fi

printf '%s\n' '==> 前端类型检查与生产构建'
npm --prefix frontend run build

if [[ "${VERIFY_DOCKER:-0}" == "1" ]]; then
  printf '%s\n' '==> 生产镜像构建（VERIFY_DOCKER=1）'
  scripts/docker-build-prod.sh --allow-dirty --no-pull --release-tag verify
fi

printf '%s\n' '全部验证完成。'
