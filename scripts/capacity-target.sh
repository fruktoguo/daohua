#!/usr/bin/env bash
# 仅执行真实 PostgreSQL/WAL/latest-only Worker 容量门禁；不删除数据库卷。

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

if [[ "${SKIP_DB_START:-0}" != "1" ]]; then
  if ! (echo >/dev/tcp/"${DB_HOST:-127.0.0.1}"/"${DB_PORT:-5432}") 2>/dev/null; then
    docker compose -f compose.yaml up -d db
  fi
fi

dotnet run --project src/LoadTests/LoadTests.csproj --configuration Release -- \
  --scenario capacity-target \
  --require-database \
  --database "${DAOHUA_DATABASE_URL:-Host=${DB_HOST:-127.0.0.1};Port=${DB_PORT:-5432};Database=daohua;Username=daohua;Password=${DB_PASSWORD:-daohua-local-dev};Maximum Pool Size=16;Timeout=5;Command Timeout=30}" \
  ${@+"$@"}
