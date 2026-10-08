#!/usr/bin/env bash
# 容量门禁：真实 PostgreSQL/WAL/latest-only 探测 + 显式配置的真实 ComputeNode compute-link；synthetic 仅告警，不可单独通过。

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

if [[ "${SKIP_DB_START:-0}" != "1" ]]; then
  if ! (echo >/dev/tcp/"${DB_HOST:-127.0.0.1}"/"${DB_PORT:-5432}") 2>/dev/null; then
    docker compose -f compose.yaml up -d db
  fi
fi
printf '容量门禁：synthetic 估算仅作 warning；真实 ComputeNode 路径需显式设置 CAPACITY_REAL_COMPUTE_LINK=1 和 CAPACITY_REAL_DATABASE_URL。\n' >&2
dotnet run --project src/LoadTests/LoadTests.csproj --configuration Release -- \
  --scenario capacity-target \
  --require-database \
  --database "${DAOHUA_DATABASE_URL:-${CAPACITY_REAL_DATABASE_URL:-Host=${DB_HOST:-127.0.0.1};Port=${DB_PORT:-5432};Database=daohua;Username=daohua;Password=${DB_PASSWORD:-daohua-local-dev};Maximum Pool Size=16;Timeout=5;Command Timeout=30}}" \
  ${@+"$@"}
