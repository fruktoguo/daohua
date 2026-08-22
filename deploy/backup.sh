#!/usr/bin/env bash

set -Eeuo pipefail

DEPLOY_DIR="${DAOHUA_DEPLOY_DIR:-/opt/daohua}"
COMPOSE_FILE="${DAOHUA_COMPOSE_FILE:-${DEPLOY_DIR}/compose.prod.yaml}"
ENV_FILE="${DEPLOY_DIR}/daohua.env"
BACKUP_DIR="${DAOHUA_BACKUP_DIR:-${DEPLOY_DIR}/backups}"
DB_CONTAINER_NAME="${DAOHUA_DB_CONTAINER_NAME:-daohua-db}"
LOCK_FILE="${DAOHUA_BACKUP_LOCK_FILE:-/run/lock/daohua-ccr-auto-update.lock}"

if [[ "${EUID}" -ne 0 && "${DAOHUA_ALLOW_NON_ROOT_BACKUP:-0}" != "1" ]]; then
  printf '该脚本必须以 root 运行。\n' >&2
  exit 1
fi


compose() {
  if [[ -f "$ENV_FILE" ]]; then
    docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"
  else
    docker compose -f "$COMPOSE_FILE" "$@"
  fi
}

exec 9>"$LOCK_FILE"
flock 9

mkdir -p "$BACKUP_DIR/hourly" "$BACKUP_DIR/daily" "$BACKUP_DIR/weekly"
timestamp="$(date -u +'%Y%m%dT%H%M%SZ')"
hour_archive="${BACKUP_DIR}/hourly/daohua-postgres-hourly-${timestamp}.dump"
partial="${hour_archive}.partial"
sha_file="${hour_archive}.sha256"

db_state="$(docker inspect --format '{{.State.Running}} {{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$DB_CONTAINER_NAME" 2>/dev/null || true)"
if [[ -z "$db_state" || "$db_state" != true\ healthy ]]; then
  printf 'PostgreSQL 容器未健康运行: %s\n' "${db_state:-missing}" >&2
  exit 1
fi

if ! compose exec -T db pg_dump \
  --username=daohua \
  --dbname=daohua \
  --format=custom \
  --compress=gzip:9 \
  --no-owner \
  --no-privileges \
  >"$partial"; then
  rm -f "$partial"
  printf '数据库备份失败。\n' >&2
  exit 1
fi

mv "$partial" "$hour_archive"
compose exec -T db pg_restore --list <"$hour_archive" >/dev/null
sha256sum "$hour_archive" >"$sha_file"

utc_hour="$(date -u +'%H')"
utc_day="$(date -u +'%u')"
if [[ "$utc_hour" == "00" ]]; then
  daily_archive="${BACKUP_DIR}/daily/daohua-postgres-daily-${timestamp}.dump"
  cp -p "$hour_archive" "$daily_archive"
  sha256sum "$daily_archive" >"${daily_archive}.sha256"
  if [[ "$utc_day" == "1" ]]; then
    weekly_archive="${BACKUP_DIR}/weekly/daohua-postgres-weekly-${timestamp}.dump"
    cp -p "$hour_archive" "$weekly_archive"
    sha256sum "$weekly_archive" >"${weekly_archive}.sha256"
  fi
fi

prune_backups() {
  local dir="$1"
  local keep="$2"
  mapfile -t files < <(find "$dir" -maxdepth 1 -type f -name '*.dump' -printf '%T@ %p\n' | sort -rn | awk -v keep="$keep" 'NR>keep {print $2}')
  for file in "${files[@]:-}"; do
    rm -f -- "$file" "${file}.sha256"
  done
}

prune_backups "${BACKUP_DIR}/hourly" 24
prune_backups "${BACKUP_DIR}/daily" 7
prune_backups "${BACKUP_DIR}/weekly" 4

printf '数据库备份完成: %s\n' "$hour_archive"
printf '校验文件: %s\n' "$sha_file"
