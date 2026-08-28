#!/usr/bin/env bash
# daohua 生产镜像自动更新：Server 与 ComputeNode 成对校验、健康检查失败自动回滚。
# 只替换容器，不触碰 PostgreSQL 数据卷；不会安装 Redis 或写入业务数据。

set -Eeuo pipefail

DEPLOY_DIR="${DEPLOY_DIR:-/opt/daohua}"
COMPOSE_FILE="${COMPOSE_FILE:-${DEPLOY_DIR}/compose.prod.yaml}"
ENV_FILE="${ENV_FILE:-${DEPLOY_DIR}/daohua.env}"
APP_IMAGE="${APP_IMAGE:-}"
COMPUTE_IMAGE="${COMPUTE_IMAGE:-}"
CONTAINER_NAME="daohua-app"
COMPUTE_CONTAINER_NAME="daohua-compute"
FAILED_IMAGE_FILE="${FAILED_IMAGE_FILE:-${DEPLOY_DIR}/failed-image.id}"
LOCK_FILE="${LOCK_FILE:-/run/lock/daohua-ccr-auto-update.lock}"

compose() {
  if [[ -f "$ENV_FILE" ]]; then
    docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"
  else
    docker compose -f "$COMPOSE_FILE" "$@"
  fi
}

derive_compute_image() {
  local image="$1"
  if [[ "$image" == *"/daohua:"* ]]; then
    printf '%s\n' "${image/daohua:/daohua-compute:}"
  elif [[ "$image" == daohua:* ]]; then
    printf '%s\n' "${image/daohua:/daohua-compute:}"
  else
    return 1
  fi
}

requested_app_image="${APP_IMAGE:-}"
requested_compute_image="${COMPUTE_IMAGE:-}"
if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  set -a
  source "$ENV_FILE"
  set +a
fi
APP_IMAGE="${requested_app_image:-${APP_IMAGE:-ccr.ccs.tencentyun.com/yuohira/daohua:prod}}"
COMPUTE_IMAGE="${requested_compute_image:-${COMPUTE_IMAGE:-}}"
if [[ -z "$COMPUTE_IMAGE" ]]; then
  COMPUTE_IMAGE="$(derive_compute_image "$APP_IMAGE")"
fi

[[ -f "$COMPOSE_FILE" ]] || { printf '缺少生产 Compose 文件: %s\n' "$COMPOSE_FILE" >&2; exit 1; }
[[ "$APP_IMAGE" =~ ^[A-Za-z0-9._/:@-]+$ && "$COMPUTE_IMAGE" =~ ^[A-Za-z0-9._/:@-]+$ ]] || {
  printf '镜像引用包含非法字符。\n' >&2
  exit 1
}

mkdir -p "$(dirname "$LOCK_FILE")"
exec 9>"$LOCK_FILE"
if ! flock -n 9; then
  printf '已有更新任务正在执行，跳过本轮。\n'
  exit 0
fi

wait_healthy() {
  local container="$1"
  local attempt state
  for attempt in {1..60}; do
    state="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container" 2>/dev/null || true)"
    if [[ "$state" == healthy ]]; then return 0; fi
    if [[ "$state" == exited || "$state" == dead ]]; then return 1; fi
    sleep 2
  done
  return 1
}

wait_ready() {
  local attempt
  for attempt in {1..30}; do
    if curl --fail --silent --show-error http://127.0.0.1:15128/readyz >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  return 1
}

deploy_pair() {
  local app_image="$1" compute_image="$2"
  APP_IMAGE="$app_image" COMPUTE_IMAGE="$compute_image" compose up \
    --detach \
    --force-recreate \
    app compute-node
  wait_healthy "$COMPUTE_CONTAINER_NAME"
  wait_healthy "$CONTAINER_NAME"
  wait_ready
}

docker pull "$APP_IMAGE" >/dev/null
docker pull "$COMPUTE_IMAGE" >/dev/null
candidate_app_id="$(docker image inspect --format '{{.Id}}' "$APP_IMAGE")"
candidate_compute_id="$(docker image inspect --format '{{.Id}}' "$COMPUTE_IMAGE")"
running_app_id="$(docker inspect --format '{{.Image}}' "$CONTAINER_NAME" 2>/dev/null || true)"
running_compute_id="$(docker inspect --format '{{.Image}}' "$COMPUTE_CONTAINER_NAME" 2>/dev/null || true)"
candidate_marker="${candidate_app_id}|${candidate_compute_id}"

if [[ "$candidate_app_id" == "$running_app_id" && "$candidate_compute_id" == "$running_compute_id" ]]; then
  printf 'Server 与 ComputeNode 均已是最新版本: %s\n' "$candidate_marker"
  exit 0
fi
if [[ -f "$FAILED_IMAGE_FILE" && "$(<"$FAILED_IMAGE_FILE")" == "$candidate_marker" ]]; then
  printf '候选镜像此前健康检查失败，等待 CCR 出现新版本: %s\n' "$candidate_marker" >&2
  exit 0
fi

rollback_app_image=""
rollback_compute_image=""
if [[ -n "$running_app_id" ]]; then
  rollback_app_image="daohua-rollback:app-previous"
  docker tag "$running_app_id" "$rollback_app_image"
fi
if [[ -n "$running_compute_id" ]]; then
  rollback_compute_image="daohua-rollback:compute-previous"
  docker tag "$running_compute_id" "$rollback_compute_image"
fi

printf '发现新镜像，开始成对更新: %s\n' "$candidate_marker"
if deploy_pair "$APP_IMAGE" "$COMPUTE_IMAGE"; then
  rm -f "$FAILED_IMAGE_FILE"
  printf '镜像更新成功: %s\n' "$candidate_marker"
  exit 0
fi

printf '%s\n' "$candidate_marker" >"$FAILED_IMAGE_FILE"
printf '新镜像健康检查失败，开始回滚。\n' >&2
docker logs --tail 100 "$CONTAINER_NAME" >&2 2>/dev/null || true
docker logs --tail 100 "$COMPUTE_CONTAINER_NAME" >&2 2>/dev/null || true
if [[ -z "$rollback_app_image" || -z "$rollback_compute_image" ]]; then
  printf '没有完整的上一版本 Server/ComputeNode 镜像，无法自动回滚。\n' >&2
  exit 1
fi

if ! deploy_pair "$rollback_app_image" "$rollback_compute_image"; then
  printf '自动回滚失败，需要立即人工处理。\n' >&2
  exit 1
fi
printf '已回滚到上一版本；失败版本在 CCR 变化前不会再次尝试。\n' >&2
exit 1
