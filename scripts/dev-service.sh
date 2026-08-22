#!/usr/bin/env bash

set -Eeuo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COMPOSE_FILE="${ROOT_DIR}/compose.yaml"
SERVICE_URL="http://127.0.0.1:5128"
DB_VOLUME="daohua_daohua-postgres"
COMMAND="${1:-up}"

cd "$ROOT_DIR"

require_docker() {
  if ! docker info >/dev/null 2>&1; then
    printf 'Docker 当前不可用。\n' >&2
    exit 1
  fi
}

compose_container_id() {
  docker compose -f "$COMPOSE_FILE" ps -q app 2>/dev/null || true
}

assert_fixed_port_available() {
  if [[ -n "$(compose_container_id)" ]]; then
    return 0
  fi
  if [[ -n "$(ss -H -ltn 'sport = :5128' 2>/dev/null || true)" ]]; then
    printf '固定端口 127.0.0.1:5128 已被其他进程占用，请先停止旧服务。\n' >&2
    exit 1
  fi
}

wait_healthy() {
  local attempt state container_id
  container_id="$(compose_container_id)"
  if [[ -z "$container_id" ]]; then
    printf '未找到本地 app 容器。\n' >&2
    return 1
  fi

  for attempt in {1..60}; do
    state="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container_id" 2>/dev/null || true)"
    if [[ "$state" == "healthy" ]]; then
      return 0
    fi
    if [[ "$state" == "exited" || "$state" == "dead" ]]; then
      docker compose -f "$COMPOSE_FILE" logs --tail 120 app >&2
      return 1
    fi
    sleep 1
  done

  docker compose -f "$COMPOSE_FILE" logs --tail 120 app >&2
  printf '等待本地服务健康超时。\n' >&2
  return 1
}

case "$COMMAND" in
  up)
    require_docker
    assert_fixed_port_available
    docker volume create "$DB_VOLUME" >/dev/null
    docker compose -f "$COMPOSE_FILE" up --detach --build app
    wait_healthy
    node scripts/smoke-production.mjs "$SERVICE_URL"
    ;;
  restart)
    require_docker
    assert_fixed_port_available
    docker volume create "$DB_VOLUME" >/dev/null
    docker compose -f "$COMPOSE_FILE" up --detach --build --force-recreate app
    wait_healthy
    node scripts/smoke-production.mjs "$SERVICE_URL"
    ;;
  down)
    require_docker
    docker compose -f "$COMPOSE_FILE" down
    ;;
  status)
    require_docker
    docker compose -f "$COMPOSE_FILE" ps --all
    ;;
  logs)
    require_docker
    docker compose -f "$COMPOSE_FILE" logs --follow --tail 200 app
    ;;
  smoke)
    node scripts/smoke-production.mjs "$SERVICE_URL"
    ;;
  *)
    printf '用法: scripts/dev-service.sh {up|restart|down|status|logs|smoke}\n' >&2
    exit 2
    ;;
esac
