#!/usr/bin/env bash
# PostgreSQL 物理基线备份：为 WAL/PITR 提供可验证的 pg_basebackup 与 manifest。

set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/backup-access.sh"

DEPLOY_DIR="${DEPLOY_DIR:-/opt/daohua}"
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

[[ "$BASE_BACKUP_RETENTION_COUNT" =~ ^[1-9][0-9]*$ ]] || { printf 'BASE_BACKUP_RETENTION_COUNT 必须为正整数。\n' >&2; exit 2; }
umask 077
exec 9>"$LOCK_FILE"
flock 9
backup_prepare_access "$BACKUP_DIR"
state="$(docker inspect --format '{{.State.Running}} {{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$DB_CONTAINER_NAME" 2>/dev/null || true)"
[[ "$state" == true\ healthy ]] || { printf 'PostgreSQL 容器未健康运行: %s\n' "${state:-missing}" >&2; exit 1; }

timestamp="$(date -u +'%Y%m%dT%H%M%SZ')"
archive="$BACKUP_DIR/base/daohua-postgres-base-${timestamp}.tar.gz"
stage="$(mktemp -d "$BACKUP_DIR/.publish.XXXXXX")"
partial="$stage/base.tar.gz"
trap 'rm -rf -- "$stage"' EXIT

# PG18 stdout tar 只支持单表空间、非 stream；fetch 将所需 WAL 收进 base.tar。
# 保留 PG 原生 manifest，仅在宿主 gzip。高负载需按备份耗时设置足够的 wal_keep_size。
docker exec "$DB_CONTAINER_NAME" sh -ceu '
  PGPASSWORD="${POSTGRES_PASSWORD:?POSTGRES_PASSWORD 未注入}" \
    exec pg_basebackup -w -h 127.0.0.1 -U "${POSTGRES_USER:?POSTGRES_USER 未注入}" -D - -Ft -X fetch -c fast -P
' | gzip -"${BACKUP_COMPRESS_LEVEL:-6}" >"$partial"
# grep 消费完整列表，避免 -q 提前退出使 tar 遭遇 SIGPIPE。
if ! gzip -t "$partial" || ! tar -tzf "$partial" | grep -Ex '(\./)?backup_manifest' >/dev/null; then
  printf '物理基线缺少 manifest 或 gzip/tar 损坏，未发布归档。\n' >&2
  exit 1
fi
backup_publish_pair "$partial" "$archive"
shopt -s nullglob
archives=("$BACKUP_DIR/base"/daohua-postgres-base-*.tar.gz)
for (( i=0; i<${#archives[@]}-BASE_BACKUP_RETENTION_COUNT; i++ )); do
  rm -f -- "${archives[i]}" "${archives[i]}.sha256"
done
printf '物理基线备份完成: %s（恢复校验由 verify-backup.sh 执行）\n' "$archive"
