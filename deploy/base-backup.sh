#!/usr/bin/env bash
# PostgreSQL 物理基线备份：为 WAL/PITR 提供可验证的 pg_basebackup 与 manifest。

set -Eeuo pipefail

DEPLOY_DIR="${DEPLOY_DIR:-/opt/daohua}"
COMPOSE_FILE="${COMPOSE_FILE:-${DEPLOY_DIR}/compose.prod.yaml}"
ENV_FILE="${ENV_FILE:-${DEPLOY_DIR}/daohua.env}"
BACKUP_DIR="${BACKUP_DIR:-${DEPLOY_DIR}/backups}"
BASE_BACKUP_RETENTION_COUNT="${BASE_BACKUP_RETENTION_COUNT:-2}"
DB_CONTAINER_NAME="${DB_CONTAINER_NAME:-daohua-db}"
LOCK_FILE="${BACKUP_LOCK_FILE:-/run/lock/daohua-db-backup.lock}"

if [[ "${EUID}" -ne 0 && "${ALLOW_NON_ROOT_BACKUP:-0}" != "1" ]]; then
  printf '该脚本必须以 root 运行。\n' >&2
  exit 1
fi
if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  set -a; source "$ENV_FILE"; set +a
fi

compose() {
  if [[ -f "$ENV_FILE" ]]; then
    docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"
  else
    docker compose -f "$COMPOSE_FILE" "$@"
  fi
}

mkdir -p "$BACKUP_DIR/base"
exec 9>"$LOCK_FILE"
flock 9
state="$(docker inspect --format '{{.State.Running}} {{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$DB_CONTAINER_NAME" 2>/dev/null || true)"
[[ "$state" == true\ healthy ]] || { printf 'PostgreSQL 容器未健康运行: %s\n' "${state:-missing}" >&2; exit 1; }

timestamp="$(date -u +'%Y%m%dT%H%M%SZ')"
archive="$BACKUP_DIR/base/daohua-postgres-base-${timestamp}.tar.gz"
partial="${archive}.partial"
container_tmp="/tmp/daohua-base-${timestamp}"

cleanup() { docker exec "$DB_CONTAINER_NAME" rm -rf -- "$container_tmp" >/dev/null 2>&1 || true; rm -f "$partial"; }
trap cleanup EXIT

docker exec "$DB_CONTAINER_NAME" sh -ceu '
  rm -rf -- "$1"
  mkdir -p "$1"
  PGPASSWORD="${POSTGRES_PASSWORD:?POSTGRES_PASSWORD 未注入}" \
    pg_basebackup -h 127.0.0.1 -U "${POSTGRES_USER:?POSTGRES_USER 未注入}" -D "$1" -Fp -X stream -c fast -P
' sh "$container_tmp"
docker exec "$DB_CONTAINER_NAME" tar -C "$container_tmp" -czf - . >"$partial"
mv "$partial" "$archive"
sha256sum "$archive" >"${archive}.sha256"
docker exec "$DB_CONTAINER_NAME" test -f "$container_tmp/backup_manifest"
printf '物理基线备份完成: %s\n' "$archive"
printf 'WAL/PITR 校验清单: backup_manifest（位于归档内）\n'

mapfile -t old_archives < <(find "$BACKUP_DIR/base" -maxdepth 1 -type f -name '*.tar.gz' -printf '%T@ %p\n' | sort -rn | awk -v keep="$BASE_BACKUP_RETENTION_COUNT" 'NR>keep {print $2}')
for old_archive in "${old_archives[@]:-}"; do
  rm -f -- "$old_archive" "${old_archive}.sha256"
done
