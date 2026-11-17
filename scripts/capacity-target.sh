#!/usr/bin/env bash
# 容量链路回归；不是固定规格验收。真实 PostgreSQL/WAL/ComputeNode，合成结果不可单独通过。

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

if [[ "${1:-}" == --help || "${1:-}" == -h ]]; then
  printf '%s\n' '用法：scripts/capacity-target.sh [--link-regression] [LoadTests 调优参数]' \
    '  --target-acceptance  当前缺少可校验的固定规格证据，拒绝通过；不会启动工作负载。' \
    '  仅使用本机 Docker 与缓存的 postgres:18-alpine，每次新建随机回环、独立 tmpfs PostgreSQL；忽略外部目标环境变量。'
  exit 0
fi
if [[ "${1:-}" == --target-acceptance ]]; then
  printf '%s\n' '未执行固定规格验收：需 2c4g-40g 整机资源限制、生产近似持久盘、5000 份代表性完整存档及实测证据。链路回归不能替代。' >&2
  exit 1
fi
if [[ "${1:-}" == --link-regression ]]; then shift; fi
for arg in "$@"; do
  case "${arg,,}" in
    --scenario|--database|--require-database|--repo-root|--url|--target-acceptance)
      printf '容量链路入口不允许覆盖场景、目标库或强制探测口径：%s\n' "$arg" >&2
      exit 2 ;;
  esac
done

# 在容器创建前丢弃外部地址和代理；整条链路只访问本机。
unset CAPACITY_REAL_DATABASE_URL CAPACITY_REAL_SERVER_URL CAPACITY_POSTGRES_CONTAINER \
  DAOHUA_DATABASE_URL DAOHUA_COMPUTE_TEST_CONNECTION DAOHUA_MARKET_TEST_CONNECTION \
  DOCKER_HOST DOCKER_CONTEXT HTTP_PROXY HTTPS_PROXY ALL_PROXY http_proxy https_proxy all_proxy
context="$(docker context show | tr -d '\r')"
endpoint="$(docker context inspect "$context" --format '{{(index .Endpoints "docker").Host}}' | tr -d '\r')"
case "$endpoint" in
  unix:///*|npipe:////./pipe/*) export DOCKER_CONTEXT="$context" ;;
  *) printf '容量链路仅允许本机 Docker socket 或命名管道。\n' >&2; exit 1 ;;
esac

isolated_container=""
cleanup() {
  if [[ -n "$isolated_container" ]]; then
    docker rm -f -v "$isolated_container" >/dev/null || printf '无法回收本轮容量容器。\n' >&2
  fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
run_id="$(openssl rand -hex 16 | tr -d '\r')"
password="$(openssl rand -hex 24 | tr -d '\r')"
[[ "$run_id" =~ ^[0-9a-f]{32}$ && "$password" =~ ^[0-9a-f]{48}$ ]] || exit 1
name="daohua_capacity_$run_id"
isolated_container="$(POSTGRES_USER="$name" POSTGRES_DB="$name" POSTGRES_PASSWORD="$password" \
  MSYS_NO_PATHCONV=1 docker create --rm --pull never --label daohua.capacity.postgres=true \
  --label "daohua.capacity.run=$run_id" --cpus 0.75 --memory 1g \
  --publish 127.0.0.1::5432 --tmpfs=/var/lib/postgresql:rw,size=2147483648 \
  --env POSTGRES_USER --env POSTGRES_DB --env POSTGRES_PASSWORD \
  postgres:18-alpine postgres -c max_connections=100 -c shared_buffers=128MB | tr -d '\r')"
[[ "$isolated_container" =~ ^[0-9a-f]{64}$ ]] || { isolated_container=""; exit 1; }
docker start "$isolated_container" >/dev/null
ready=0
for ((attempt=0; attempt<60; attempt++)); do
  if docker exec "$isolated_container" sh -ceu 'pg_isready -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' >/dev/null 2>&1; then ready=1; break; fi
  sleep 1
done
[[ "$ready" == 1 ]] || { printf '隔离容量 PostgreSQL 未就绪。\n' >&2; exit 1; }
address="$(docker port "$isolated_container" 5432/tcp | tr -d '\r')"
[[ "$address" =~ ^127\.0\.0\.1:[0-9]+$ ]] || { printf '容量 PostgreSQL 未绑定回环端口。\n' >&2; exit 1; }
connection="Host=127.0.0.1;Port=${address##*:};Database=$name;Username=$name;Password=$password;Maximum Pool Size=16;Timeout=5;Command Timeout=30"
export CAPACITY_POSTGRES_CONTAINER="$isolated_container"
printf '本轮独立 PostgreSQL：run=%s，回环端口=%s。\n' "$run_id" "${address##*:}" >&2
export CAPACITY_REAL_NETWORK_DURATION_SECONDS="${CAPACITY_REAL_NETWORK_DURATION_SECONDS:-60}"
private_constants="$ROOT_DIR/../DaoHua-Private/server/AntiMultiOpen/AntiMultiOpenConstants.cs"
if [[ -z "${DAOHUA_LOADTEST_MODULE_DECLARATION:-}" && -f "$private_constants" ]]; then
  DAOHUA_LOADTEST_MODULE_DECLARATION="$(sed -nE 's/.*ModuleDeclarationId = "([^"]+)".*/\1/p' "$private_constants" | head -1)"
  [[ -n "$DAOHUA_LOADTEST_MODULE_DECLARATION" ]] || {
    printf '无法从私有模块真源读取 LoadTest Hello 声明。\n' >&2
    exit 1
  }
  export DAOHUA_LOADTEST_MODULE_DECLARATION
fi
printf '容量链路回归：真实 PostgreSQL、100/500 认证业务收发与 2000 一小时冷挂机；阈值或资源证据缺失均失败。不是固定规格验收。\n' >&2
DAOHUA_DATABASE_URL="$connection" \
CAPACITY_REAL_DATABASE_URL="$connection" \
CAPACITY_REAL_COMPUTE_LINK=1 \
dotnet run --project src/LoadTests/LoadTests.csproj --configuration Release -- \
  --scenario capacity-link \
  --require-database \
  "$@"
