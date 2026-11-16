#!/usr/bin/env bash
# 清理超过热数据留存窗口（默认 30 天）的历史分区归档表（*_archive_YYYYMM）。
# 热表数据统一由 PostgresRetentionWorker 按 30 天直接回收；长期留档由整点备份与异地同步承担，
# 避免本地分区归档表在 2c4g/40g 机器上长期累积耗尽磁盘。

set -Eeuo pipefail

DEPLOY_DIR="${DEPLOY_DIR:-/opt/daohua}"
COMPOSE_FILE="${COMPOSE_FILE:-${DEPLOY_DIR}/compose.prod.yaml}"
ENV_FILE="${ENV_FILE:-${DEPLOY_DIR}/daohua.env}"
ARCHIVE_BEFORE_DAYS="${ARCHIVE_BEFORE_DAYS:-30}"
DB_CONTAINER_NAME="${DB_CONTAINER_NAME:-daohua-db}"

[[ "$ARCHIVE_BEFORE_DAYS" =~ ^[1-9][0-9]*$ ]] || { printf 'ARCHIVE_BEFORE_DAYS 必须是正整数。\n' >&2; exit 2; }
if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  set -a; source "$ENV_FILE"; set +a
fi

compose() {
  if [[ -f "$ENV_FILE" ]]; then docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"; else docker compose -f "$COMPOSE_FILE" "$@"; fi
}

compose exec -T db sh -ceu '
  export PGPASSWORD="${POSTGRES_PASSWORD:?POSTGRES_PASSWORD 未注入}"
  exec psql -v ON_ERROR_STOP=1 --username="${POSTGRES_USER:?POSTGRES_USER 未注入}" \
    --dbname="${POSTGRES_DB:?POSTGRES_DB 未注入}" -v before_days="$1"
' sh "$ARCHIVE_BEFORE_DAYS" <<'SQL'
SELECT set_config('daohua.archive_before_days', :'before_days', false);

DO $prune_partitions$
DECLARE
    cutoff date := (now() - make_interval(days => current_setting('daohua.archive_before_days')::integer))::date;
    rec record;
    part_end date;
BEGIN
    FOR rec IN
        SELECT schemaname, tablename
        FROM pg_tables
        WHERE (schemaname = 'market' AND tablename ~ '^trades_archive_[0-9]{6}$')
           OR (schemaname = 'identity' AND tablename ~ '^audit_events_archive_[0-9]{6}$')
        ORDER BY schemaname, tablename
    LOOP
        part_end := (to_date(right(rec.tablename, 6), 'YYYYMM') + interval '1 month')::date;
        IF part_end <= cutoff THEN
            EXECUTE format('DROP TABLE IF EXISTS %I.%I', rec.schemaname, rec.tablename);
            RAISE NOTICE '已删除过期历史分区 %.%', rec.schemaname, rec.tablename;
        END IF;
    END LOOP;
END
$prune_partitions$;
SQL

printf '超过 %s 天的历史归档分区清理完成。\n' "$ARCHIVE_BEFORE_DAYS"
