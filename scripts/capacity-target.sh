#!/usr/bin/env bash
# 容量门禁：真实 PostgreSQL/WAL/latest-only 探测 + 显式配置的真实 ComputeNode compute-link；synthetic 仅告警，不可单独通过。

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

# 缺省供给全新 tmpfs PostgreSQL，不依赖也不修改用户已有容器、卷或开发数据库。
: "${CAPACITY_REAL_DATABASE_URL:=${DAOHUA_DATABASE_URL:-}}"
: "${CAPACITY_REAL_SERVER_URL:=}"
isolated_container=""
cleanup() {
  if [[ -n "$isolated_container" ]]; then docker rm -f "$isolated_container" >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT
if [[ -z "$CAPACITY_REAL_DATABASE_URL" ]]; then
  isolated_container="daohua-capacity-$(date +%s)-$$"
  export POSTGRES_USER=capacity_gate
  export POSTGRES_DB=capacity_gate
  export POSTGRES_PASSWORD
  POSTGRES_PASSWORD="$(openssl rand -hex 24)"
  docker run --detach --name "$isolated_container" --cpus 0.75 --memory 1g \
    --publish 127.0.0.1::5432 --tmpfs /var/lib/postgresql:rw,size=2147483648 \
    --env POSTGRES_USER --env POSTGRES_DB --env POSTGRES_PASSWORD \
    postgres:18-alpine postgres -c max_connections=100 -c shared_buffers=128MB >/dev/null
  ready=0
  for ((attempt=0; attempt<60; attempt++)); do
    if docker exec "$isolated_container" sh -ceu 'pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB"' >/dev/null 2>&1; then ready=1; break; fi
    sleep 1
  done
  [[ "$ready" == 1 ]] || { printf '隔离容量 PostgreSQL 未就绪。\n' >&2; exit 1; }
  address="$(docker port "$isolated_container" 5432/tcp)"
  CAPACITY_REAL_DATABASE_URL="Host=127.0.0.1;Port=${address##*:};Database=$POSTGRES_DB;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD;Maximum Pool Size=16;Timeout=5;Command Timeout=30"
  export CAPACITY_POSTGRES_CONTAINER="$isolated_container"
fi
export CAPACITY_REAL_NETWORK_DURATION_SECONDS="${CAPACITY_REAL_NETWORK_DURATION_SECONDS:-60}"
printf '容量门禁：真实 PostgreSQL、隔离主服 100/500 认证业务收发与 2000 一小时冷挂机；未完成目标或资源证据缺失均失败。\n' >&2
DAOHUA_DATABASE_URL="$CAPACITY_REAL_DATABASE_URL" \
CAPACITY_REAL_DATABASE_URL="$CAPACITY_REAL_DATABASE_URL" \
CAPACITY_REAL_SERVER_URL="$CAPACITY_REAL_SERVER_URL" \
CAPACITY_REAL_COMPUTE_LINK=1 \
dotnet run --project src/LoadTests/LoadTests.csproj --configuration Release -- \
  --scenario capacity-target \
  --require-database \
  ${@+"$@"}
