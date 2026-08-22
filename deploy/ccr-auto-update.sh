#!/usr/bin/env bash

set -Eeuo pipefail

DEPLOY_DIR="${DAOHUA_DEPLOY_DIR:-/opt/daohua}"
COMPOSE_FILE="${DEPLOY_DIR}/compose.prod.yaml"
ENV_FILE="${DEPLOY_DIR}/daohua.env"
IMAGE="${DAOHUA_IMAGE:-ccr.ccs.tencentyun.com/yuohira/daohua:prod}"
CONTAINER_NAME="daohua-app"
PREFLIGHT_CONTAINER_NAME="daohua-preflight"
PREFLIGHT_DB_CONTAINER_NAME="daohua-preflight-db"
PREFLIGHT_DB_VOLUME="daohua-preflight-postgres"
PREFLIGHT_NETWORK="daohua-preflight-net"
FAILED_IMAGE_FILE="${DEPLOY_DIR}/failed-image.id"
LOCK_FILE="/run/lock/daohua-ccr-auto-update.lock"


compose() {
  if [[ -f "$ENV_FILE" ]]; then
    docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"
  else
    docker compose -f "$COMPOSE_FILE" "$@"
  fi
}

exec 9>"$LOCK_FILE"
if ! flock -n 9; then
  printf '已有更新任务正在执行，跳过本轮。\n'
  exit 0
fi

wait_healthy() {
  local attempt state
  for attempt in {1..60}; do
    state="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$CONTAINER_NAME" 2>/dev/null || true)"
    if [[ "$state" == "healthy" ]]; then
      return 0
    fi
    if [[ "$state" == "exited" || "$state" == "dead" ]]; then
      return 1
    fi
    sleep 2
  done
  return 1
}

cleanup_preflight() {
  docker rm --force "$PREFLIGHT_CONTAINER_NAME" "$PREFLIGHT_DB_CONTAINER_NAME" >/dev/null 2>&1 || true
  docker network rm "$PREFLIGHT_NETWORK" >/dev/null 2>&1 || true
  docker volume rm --force "$PREFLIGHT_DB_VOLUME" >/dev/null 2>&1 || true
}

preflight_image() {
  local image="$1"
  local attempt state
  cleanup_preflight
  docker network create "$PREFLIGHT_NETWORK" >/dev/null
  docker volume create "$PREFLIGHT_DB_VOLUME" >/dev/null
  docker run --detach \
    --name "$PREFLIGHT_DB_CONTAINER_NAME" \
    --network "$PREFLIGHT_NETWORK" \
    --mount "type=volume,src=$PREFLIGHT_DB_VOLUME,dst=/var/lib/postgresql/data" \
    --shm-size 64m \
    --env POSTGRES_DB=daohua \
    --env POSTGRES_USER=daohua \
    --env POSTGRES_PASSWORD=daohua-preflight \
    --env PGDATA=/var/lib/postgresql/data/pgdata \
    postgres:17-alpine >/dev/null

  for attempt in {1..45}; do
    if docker exec "$PREFLIGHT_DB_CONTAINER_NAME" pg_isready -U daohua -d daohua >/dev/null 2>&1; then
      break
    fi
    sleep 1
  done
  if ! docker exec "$PREFLIGHT_DB_CONTAINER_NAME" pg_isready -U daohua -d daohua >/dev/null 2>&1; then
    docker logs --tail 100 "$PREFLIGHT_DB_CONTAINER_NAME" >&2 2>/dev/null || true
    cleanup_preflight
    return 1
  fi

  docker run --detach \
    --name "$PREFLIGHT_CONTAINER_NAME" \
    --network "$PREFLIGHT_NETWORK" \
    --read-only \
    --tmpfs /tmp:size=32m,mode=1777 \
    --cap-drop ALL \
    --security-opt no-new-privileges:true \
    --pids-limit 128 \
    --memory 512m \
    --cpus 0.50 \
    --env ASPNETCORE_ENVIRONMENT=Production \
    --env Storage__Provider=postgres \
    --env Storage__ConnectionString='Host=daohua-preflight-db;Port=5432;Database=daohua;Username=daohua;Password=daohua-preflight;Maximum Pool Size=4;Minimum Pool Size=1;Timeout=5;Command Timeout=30;Keepalive=30' \
    --env Storage__DurableOutboxEnabled=true \
    --env Storage__EnableSchemaBootstrap=true \
    --env Market__LedgerProvider=postgres \
    --env Market__PartitionCount=1 \
    --env Market__PartitionId=0 \
    "$image" >/dev/null

  for attempt in {1..45}; do
    state="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$PREFLIGHT_CONTAINER_NAME" 2>/dev/null || true)"
    if [[ "$state" == "healthy" ]]; then
      docker exec "$PREFLIGHT_CONTAINER_NAME" curl --fail --silent --show-error http://127.0.0.1:8080/healthz >/dev/null
      docker exec "$PREFLIGHT_CONTAINER_NAME" curl --fail --silent --show-error http://127.0.0.1:8080/api/actions >/dev/null
      cleanup_preflight
      return 0
    fi
    if [[ "$state" == "exited" || "$state" == "dead" ]]; then
      break
    fi
    sleep 2
  done

  docker logs --tail 100 "$PREFLIGHT_CONTAINER_NAME" >&2 2>/dev/null || true
  docker logs --tail 100 "$PREFLIGHT_DB_CONTAINER_NAME" >&2 2>/dev/null || true
  cleanup_preflight
  return 1
}

deploy_image() {
  local image="$1"
  DAOHUA_IMAGE="$image" compose up \
    --detach \
    --force-recreate \
    app
  wait_healthy
}

if [[ ! -f "$COMPOSE_FILE" ]]; then
  printf '缺少生产 Compose 文件: %s\n' "$COMPOSE_FILE" >&2
  exit 1
fi

docker pull "$IMAGE"
candidate_id="$(docker image inspect --format '{{.Id}}' "$IMAGE")"
running_id="$(docker inspect --format '{{.Image}}' "$CONTAINER_NAME" 2>/dev/null || true)"

if [[ "$candidate_id" == "$running_id" ]]; then
  printf '运行镜像已是最新版本: %s\n' "$candidate_id"
  exit 0
fi
if [[ -f "$FAILED_IMAGE_FILE" && "$(<"$FAILED_IMAGE_FILE")" == "$candidate_id" ]]; then
  printf '候选镜像此前健康检查失败，等待 CCR 出现新镜像: %s\n' "$candidate_id" >&2
  exit 0
fi

printf '对候选镜像执行隔离预热与配置校验: %s\n' "$candidate_id"
if ! preflight_image "$IMAGE"; then
  printf '%s\n' "$candidate_id" >"$FAILED_IMAGE_FILE"
  printf '候选镜像预热失败，当前正式容器保持不变。\n' >&2
  exit 1
fi

rollback_image=""
if [[ -n "$running_id" ]]; then
  rollback_image="daohua-rollback:previous"
  docker tag "$running_id" "$rollback_image"
fi

printf '发现新镜像，开始更新: %s -> %s\n' "${running_id:-none}" "$candidate_id"
if deploy_image "$IMAGE"; then
  rm -f "$FAILED_IMAGE_FILE"
  printf '镜像更新成功: %s\n' "$candidate_id"
  exit 0
fi

printf '%s\n' "$candidate_id" >"$FAILED_IMAGE_FILE"
printf '新镜像健康检查失败。\n' >&2
docker logs --tail 100 "$CONTAINER_NAME" >&2 2>/dev/null || true
if [[ -z "$rollback_image" ]]; then
  printf '没有可用的上一镜像，无法自动回滚。\n' >&2
  exit 1
fi

printf '开始回滚到上一镜像: %s\n' "$running_id" >&2
if ! deploy_image "$rollback_image"; then
  printf '自动回滚失败，需要立即人工处理。\n' >&2
  exit 1
fi
printf '已回滚到上一镜像；失败版本在 CCR 变化前不会再次尝试。\n' >&2
exit 1
