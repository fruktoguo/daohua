#!/usr/bin/env bash
# 备份门禁：校验 custom dump、物理 base backup manifest 与 WAL 归档完整性。

set -Eeuo pipefail

DEPLOY_DIR="${DEPLOY_DIR:-/opt/daohua}"
BACKUP_DIR="${BACKUP_DIR:-${DEPLOY_DIR}/backups}"
WAL_ARCHIVE_DIR="${WAL_ARCHIVE_DIR:-${DEPLOY_DIR}/wal-archive}"
COMPOSE_FILE="${COMPOSE_FILE:-${DEPLOY_DIR}/compose.prod.yaml}"
ENV_FILE="${ENV_FILE:-${DEPLOY_DIR}/daohua.env}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/backup-checks.sh"
ISOLATED_NAME="daohua-backup-verify-$$"
if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  set -a; source "$ENV_FILE"; set +a
fi
ISOLATED_RESTORE="${VERIFY_BACKUP_ISOLATED_RESTORE:-0}"
ISOLATED_PORT="${VERIFY_BACKUP_PORT:-15433}"
if [[ "$ISOLATED_RESTORE" == "1" ]]; then
  [[ -n "${DB_BOOTSTRAP_USER:-}" ]] || { printf '隔离恢复需要 DB_BOOTSTRAP_USER。
' >&2; exit 1; }
  [[ -n "${DB_BOOTSTRAP_PASSWORD:-}" ]] || { printf '隔离恢复需要 DB_BOOTSTRAP_PASSWORD。
' >&2; exit 1; }
fi

compose() {
  if [[ -f "$ENV_FILE" ]]; then docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"; else docker compose -f "$COMPOSE_FILE" "$@"; fi
}

# 与生成及轮转互斥；复制或 touch 不能刷新源备份的恢复时间。
exec 9>"${BACKUP_LOCK_FILE:-/run/lock/daohua-db-backup.lock}"
flock 9
shopt -s nullglob
latest_dump=""
latest_stamp=""
for candidate in "$BACKUP_DIR"/{hourly,daily,weekly}/daohua-postgres-*.dump; do
  name="${candidate##*/}"
  stamp="${name%.dump}"
  stamp="${stamp##*-}"
  if [[ "$stamp" > "$latest_stamp" ]]; then latest_dump="$candidate"; latest_stamp="$stamp"; fi
done
backup_is_fresh "$latest_dump" "${VERIFY_DUMP_MAX_AGE_SECONDS:-10800}" || {
  printf '没有新鲜的逻辑 dump（或 VERIFY_DUMP_MAX_AGE_SECONDS 非法）。\n' >&2; exit 1;
}
backup_check_sha "$latest_dump" || { printf '逻辑 dump 缺少校验对或 SHA-256 不符。\n' >&2; exit 1; }
compose exec -T db pg_restore --list <"$latest_dump" >/dev/null
printf 'custom dump 校验通过: %s\n' "$latest_dump"

bases=("$BACKUP_DIR/base"/daohua-postgres-base-*.tar.gz)
latest_base=""
if ((${#bases[@]} > 0)); then latest_base="${bases[-1]}"; fi
if backup_is_fresh "$latest_base" "${VERIFY_BASE_MAX_AGE_SECONDS:-129600}"; then
  tmp_dir="$(mktemp -d /tmp/daohua-verify-base.XXXXXX)"
  trap 'rm -rf -- "$tmp_dir"' EXIT
  backup_check_sha "$latest_base" || { printf '物理基线缺少校验对或 SHA-256 不符。\n' >&2; exit 1; }
  tar -xzf "$latest_base" -C "$tmp_dir"
  [[ -f "$tmp_dir/backup_manifest" ]] || { printf 'base backup 缺少 backup_manifest。\n' >&2; exit 1; }
  docker run --rm --user 0 -v "$tmp_dir:/var/lib/postgresql/data" postgres:18-alpine \
    chown -R postgres:postgres /var/lib/postgresql/data
  docker run --rm --user postgres -v "$tmp_dir:/var/lib/postgresql/data:ro" postgres:18-alpine \
    pg_verifybackup /var/lib/postgresql/data
  printf '物理 base backup 校验通过: %s\n' "$latest_base"
  if [[ "$ISOLATED_RESTORE" == "1" ]]; then
    trap 'docker rm -f "$ISOLATED_NAME" >/dev/null 2>&1 || true; rm -rf -- "$tmp_dir"' EXIT
    # 只传环境变量名称，不把口令展开进 docker argv；库名取运行中 Compose 契约。
    export POSTGRES_USER="$DB_BOOTSTRAP_USER"
    export POSTGRES_PASSWORD="$DB_BOOTSTRAP_PASSWORD"
    export POSTGRES_DB
    POSTGRES_DB="$(compose exec -T db sh -ceu 'printf "%s" "${POSTGRES_DB:?POSTGRES_DB 未注入}"')"
    docker run -d --name "$ISOLATED_NAME" -p "127.0.0.1:${ISOLATED_PORT}:5432" \
      --env POSTGRES_USER --env POSTGRES_PASSWORD --env POSTGRES_DB \
      --env PGDATA=/var/lib/postgresql/data \
      -v "$tmp_dir:/var/lib/postgresql/data" postgres:18-alpine postgres -c archive_mode=off >/dev/null
    ready=0
    for _ in {1..30}; do
      if docker exec "$ISOLATED_NAME" sh -ceu 'pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB"' >/dev/null 2>&1; then ready=1; break; fi
      sleep 2
    done
    [[ "$ready" == "1" ]] || { printf '隔离恢复实例未就绪。\n' >&2; exit 1; }
    docker exec "$ISOLATED_NAME" sh -ceu '
      export PGPASSWORD="$POSTGRES_PASSWORD"
      psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 -c "SELECT count(*) FROM identity.accounts; SELECT count(*) FROM game.player_state_heads;" >/dev/null
    '
    printf '隔离恢复实例 ready/最小 SQL 校验通过（端口 127.0.0.1:%s）。\n' "$ISOLATED_PORT"
  else
    printf '物理 base 仅完成格式与 manifest 校验；未启动隔离恢复实例。设置 VERIFY_BACKUP_ISOLATED_RESTORE=1 可执行 ready/最小 SQL 验证。\n'
  fi
else
  printf '缺少新鲜的物理 base（或 VERIFY_BASE_MAX_AGE_SECONDS 非法）。\n' >&2
  exit 1
fi

archive_mode="$(compose exec -T db sh -ceu '
  psql -XAt -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "SHOW archive_mode;"
')"
case "$archive_mode" in
  off) printf '连续 WAL 已明确关闭；仅验证自带 WAL 的物理基线，不宣称 PITR 可用。\n' ;;
  on|always) WAL_ARCHIVE_DIR="$WAL_ARCHIVE_DIR" "$SCRIPT_DIR/wal-archive.sh" verify ;;
  *) printf '无法判定 PostgreSQL archive_mode，拒绝备份放行。\n' >&2; exit 1 ;;
esac
printf '备份门禁通过。\n'
