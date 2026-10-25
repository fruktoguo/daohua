#!/usr/bin/env bash
# ==============================================================================
# 脚本名称: backup.sh
# 主要职责: 生产环境 PostgreSQL 数据库定时热备份、SHA256 校验与分层归档轮转
#
# 核心备份流程:
#   1. 权限与排他锁: 校验 root 执行权限，获取 /run/lock/daohua-ccr-auto-update.lock 文件锁（避免备份与更新并发）；
#   2. 数据库状态检查: 验证 PostgreSQL 容器健康运行；
#   3. 热备份与压缩导出: 调用 pg_dump 以 gzip:9 压缩导出 custom dump 格式归档；
#   4. 完整性校验: 执行 pg_restore --list 校验归档完整性，并生成 sha256 校验文件；
#   5. 分层归档轮转:
#      - 小时归档 (hourly): 保留最近 24 份；
#      - 每日归档 (daily, UTC 00:00): 保留最近 7 份；
#      - 每周归档 (weekly, 周一 UTC 00:00): 保留最近 4 份；
#   6. 自动老化清理: 超出保留配额的历史归档及其 sha256 校验文件将被自动删除。
#
# 运行方式:
#   - 通常由 systemd 定时器 (daohua-db-backup.timer) 每小时自动调用。
# ==============================================================================

set -Eeuo pipefail

DEPLOY_DIR="${DEPLOY_DIR:-/opt/daohua}"
COMPOSE_FILE="${COMPOSE_FILE:-${DEPLOY_DIR}/compose.prod.yaml}"
ENV_FILE="${ENV_FILE:-${DEPLOY_DIR}/daohua.env}"
BACKUP_DIR="${BACKUP_DIR:-${DEPLOY_DIR}/backups}"
BACKUP_HOURLY_KEEP="${BACKUP_HOURLY_KEEP:-24}"
BACKUP_DAILY_KEEP="${BACKUP_DAILY_KEEP:-7}"
BACKUP_WEEKLY_KEEP="${BACKUP_WEEKLY_KEEP:-4}"
DB_CONTAINER_NAME="${DB_CONTAINER_NAME:-daohua-db}"
BACKUP_COMPRESS_LEVEL="${BACKUP_COMPRESS_LEVEL:-3}"
LOCK_FILE="${BACKUP_LOCK_FILE:-/run/lock/daohua-ccr-auto-update.lock}"

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

if ! compose exec -T db sh -ceu '
  exec pg_dump \
    --username="${POSTGRES_USER:?POSTGRES_USER 未注入}" \
    --dbname="${POSTGRES_DB:?POSTGRES_DB 未注入}" \
    --format=custom --compress="gzip:${BACKUP_COMPRESS_LEVEL}" --no-owner --no-privileges
' >"$partial"; then
  rm -f "$partial"
  printf '数据库备份失败。\n' >&2
  exit 1
fi

mv "$partial" "$hour_archive"
compose exec -T db pg_restore --list <"$hour_archive" >/dev/null
(cd "$(dirname "$hour_archive")" && sha256sum "$(basename "$hour_archive")" >"$sha_file")

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

prune_backups "${BACKUP_DIR}/hourly" "$BACKUP_HOURLY_KEEP"
prune_backups "${BACKUP_DIR}/daily" "$BACKUP_DAILY_KEEP"
prune_backups "${BACKUP_DIR}/weekly" "$BACKUP_WEEKLY_KEEP"

printf '数据库备份完成: %s\n' "$hour_archive"
printf '校验文件: %s\n' "$sha_file"
