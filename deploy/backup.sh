#!/usr/bin/env bash

set -Eeuo pipefail

DEPLOY_DIR="${XIUXIAN_DEPLOY_DIR:-/opt/xiuxian}"
COMPOSE_FILE="${DEPLOY_DIR}/compose.prod.yaml"
BACKUP_DIR="${DEPLOY_DIR}/backups"
CONTAINER_NAME="xiuxian-app"
VOLUME_NAME="xiuxian_xiuxian-data"
LOCK_FILE="/run/lock/xiuxian-ccr-auto-update.lock"

if [[ "${EUID}" -ne 0 ]]; then
  printf '该脚本必须以 root 运行。\n' >&2
  exit 1
fi

exec 9>"$LOCK_FILE"
flock 9

mkdir -p "$BACKUP_DIR"
timestamp="$(date -u +'%Y%m%dT%H%M%SZ')"
archive="${BACKUP_DIR}/xiuxian-data-${timestamp}.tar.gz"
partial="${archive}.partial"
volume_path="$(docker volume inspect --format '{{.Mountpoint}}' "$VOLUME_NAME")"
was_running="$(docker inspect --format '{{.State.Running}}' "$CONTAINER_NAME" 2>/dev/null || printf 'false')"
restart_required=0

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

restart_app() {
  if [[ "$restart_required" == "1" ]]; then
    docker compose -f "$COMPOSE_FILE" start app >/dev/null
    wait_healthy
  fi
}
trap restart_app EXIT

if [[ "$was_running" == "true" ]]; then
  restart_required=1
  docker compose -f "$COMPOSE_FILE" stop --timeout 90 app
fi

tar --numeric-owner --xattrs --acls --one-file-system \
  --directory "$volume_path" \
  --create \
  --gzip \
  --file "$partial" \
  .
mv "$partial" "$archive"
tar --list --gzip --file "$archive" >/dev/null
sha256sum "$archive" >"${archive}.sha256"

if [[ "$restart_required" == "1" ]]; then
  docker compose -f "$COMPOSE_FILE" start app >/dev/null
  wait_healthy
  restart_required=0
fi

printf '备份完成: %s\n' "$archive"
printf '校验文件: %s\n' "${archive}.sha256"
