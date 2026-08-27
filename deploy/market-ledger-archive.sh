#!/usr/bin/env bash
# 将已超过热数据窗口的成交与审计记录按 UTC 月份移动到分区归档表。
# 每个月份在同一事务内完成“删除在线表 + 写入归档分区”，可重复执行且不丢数据。

set -Eeuo pipefail

DEPLOY_DIR="${DEPLOY_DIR:-/opt/daohua}"
COMPOSE_FILE="${COMPOSE_FILE:-${DEPLOY_DIR}/compose.prod.yaml}"
ENV_FILE="${ENV_FILE:-${DEPLOY_DIR}/daohua.env}"
ARCHIVE_BEFORE_DAYS="${ARCHIVE_BEFORE_DAYS:-90}"
DB_CONTAINER_NAME="${DB_CONTAINER_NAME:-daohua-db}"

[[ "$ARCHIVE_BEFORE_DAYS" =~ ^[1-9][0-9]*$ ]] || { printf 'ARCHIVE_BEFORE_DAYS 必须是正整数。\n' >&2; exit 2; }
if [[ -f "$ENV_FILE" ]]; then
  # shellcheck disable=SC1090
  set -a; source "$ENV_FILE"; set +a
fi

compose() {
  if [[ -f "$ENV_FILE" ]]; then docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"; else docker compose -f "$COMPOSE_FILE" "$@"; fi
}

compose exec -T db psql -v ON_ERROR_STOP=1 --username=daohua --dbname=daohua \
  -v before_days="$ARCHIVE_BEFORE_DAYS" <<'SQL'
BEGIN;
CREATE TABLE IF NOT EXISTS market.trades_archive (
    trade_id uuid NOT NULL,
    partition_id integer NOT NULL,
    buy_order_id uuid NOT NULL,
    sell_order_id uuid NOT NULL,
    buyer_id uuid NOT NULL,
    seller_id uuid NOT NULL,
    asset_id varchar(128) NOT NULL,
    variant varchar(128) NOT NULL DEFAULT '',
    price double precision NOT NULL,
    quantity double precision NOT NULL,
    seller_tax double precision NOT NULL,
    traded_at timestamptz NOT NULL
) PARTITION BY RANGE (traded_at);
CREATE INDEX IF NOT EXISTS ix_market_trades_archive_time ON market.trades_archive (traded_at DESC);
CREATE TABLE IF NOT EXISTS identity.audit_events_archive (
    event_id uuid NOT NULL,
    actor_id uuid NULL,
    event_type varchar(96) NOT NULL,
    target_id uuid NULL,
    payload jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at timestamptz NOT NULL
) PARTITION BY RANGE (occurred_at);
CREATE INDEX IF NOT EXISTS ix_audit_events_archive_time ON identity.audit_events_archive (occurred_at DESC);
COMMIT;

SELECT set_config('daohua.archive_before_days', :'before_days', false);

DO $archive$
DECLARE
    cutoff timestamptz := date_trunc('day', now() - make_interval(days => current_setting('daohua.archive_before_days')::integer));
    month_start date;
    month_end date;
    suffix text;
    moved_trades bigint;
    moved_audit bigint;
BEGIN
    FOR month_start IN
        SELECT generate_series(
            date_trunc('month', least(
                COALESCE((SELECT min(traded_at) FROM market.trades), cutoff),
                COALESCE((SELECT min(occurred_at) FROM identity.audit_events), cutoff)
            ))::date,
            date_trunc('month', cutoff)::date - 1,
            interval '1 month'
        )::date
    LOOP
        month_end := (month_start + interval '1 month')::date;
        EXIT WHEN month_start >= cutoff::date;
        suffix := to_char(month_start, 'YYYYMM');
        EXECUTE format('CREATE TABLE IF NOT EXISTS market.trades_archive_%s PARTITION OF market.trades_archive FOR VALUES FROM (%L) TO (%L)', suffix, month_start, month_end);
        EXECUTE format('CREATE TABLE IF NOT EXISTS identity.audit_events_archive_%s PARTITION OF identity.audit_events_archive FOR VALUES FROM (%L) TO (%L)', suffix, month_start, month_end);
        EXECUTE format($sql$
            WITH moved AS (
                DELETE FROM market.trades
                WHERE traded_at >= %L AND traded_at < %L
                RETURNING trade_id, partition_id, buy_order_id, sell_order_id, buyer_id, seller_id, asset_id, variant, price, quantity, seller_tax, traded_at
            )
            INSERT INTO market.trades_archive
            SELECT * FROM moved
        $sql$, month_start, month_end);
        GET DIAGNOSTICS moved_trades = ROW_COUNT;
        EXECUTE format($sql$
            WITH moved AS (
                DELETE FROM identity.audit_events
                WHERE occurred_at >= %L AND occurred_at < %L
                RETURNING event_id, actor_id, event_type, target_id, payload, occurred_at
            )
            INSERT INTO identity.audit_events_archive
            SELECT * FROM moved
        $sql$, month_start, month_end);
        GET DIAGNOSTICS moved_audit = ROW_COUNT;
        RAISE NOTICE '已归档月份 % trades=% audit=%', suffix, moved_trades, moved_audit;
    END LOOP;
END
$archive$;
SQL

printf '市场成交与审计分区归档完成（热数据窗口 %s 天）。\n' "$ARCHIVE_BEFORE_DAYS"
