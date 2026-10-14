-- v32 -> v33：新增 market.item_stats 每品种行情统计表（卖一/买一价量聚合 +
-- 当日/当周成交量，随市场写事务同事务维护，读侧按 day_key/week_key 惰性掩零）；
-- 存档载荷未变，库内容版本标签随表结构变更推进至 33。
BEGIN;

CREATE TABLE IF NOT EXISTS market.item_stats (
    asset_id varchar(128) NOT NULL,
    variant varchar(128) NOT NULL DEFAULT '',
    best_ask_price double precision NULL CHECK (best_ask_price IS NULL OR (best_ask_price > 0 AND best_ask_price = best_ask_price AND best_ask_price < 'Infinity'::double precision)),
    ask_quantity double precision NOT NULL DEFAULT 0 CHECK (ask_quantity >= 0 AND ask_quantity = ask_quantity AND ask_quantity < 'Infinity'::double precision),
    best_bid_price double precision NULL CHECK (best_bid_price IS NULL OR (best_bid_price > 0 AND best_bid_price = best_bid_price AND best_bid_price < 'Infinity'::double precision)),
    bid_quantity double precision NOT NULL DEFAULT 0 CHECK (bid_quantity >= 0 AND bid_quantity = bid_quantity AND bid_quantity < 'Infinity'::double precision),
    day_volume double precision NOT NULL DEFAULT 0 CHECK (day_volume >= 0 AND day_volume = day_volume AND day_volume < 'Infinity'::double precision),
    day_key date NOT NULL,
    week_volume double precision NOT NULL DEFAULT 0 CHECK (week_volume >= 0 AND week_volume = week_volume AND week_volume < 'Infinity'::double precision),
    week_key date NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (asset_id, variant)
);

-- 回填已有活动挂单的盘口价量与当周/当日成交量，确保迁移后立刻提供行情查询。
INSERT INTO market.item_stats
    (asset_id, variant, best_ask_price, ask_quantity, best_bid_price, bid_quantity,
     day_volume, day_key, week_volume, week_key, updated_at)
SELECT
    COALESCE(o.asset_id, t.asset_id) AS asset_id,
    COALESCE(o.variant, t.variant) AS variant,
    o.best_ask_price,
    COALESCE(o.ask_quantity, 0),
    o.best_bid_price,
    COALESCE(o.bid_quantity, 0),
    COALESCE(t.day_volume, 0),
    (now() AT TIME ZONE 'UTC')::date AS day_key,
    COALESCE(t.week_volume, 0),
    (date_trunc('week', now() AT TIME ZONE 'UTC'))::date AS week_key,
    now() AS updated_at
FROM (
    SELECT
        asset_id,
        variant,
        MIN(price) FILTER (WHERE side = 'sell') AS best_ask_price,
        COALESCE(SUM(quantity - filled_quantity) FILTER (WHERE side = 'sell'), 0) AS ask_quantity,
        MAX(price) FILTER (WHERE side = 'buy') AS best_bid_price,
        COALESCE(SUM(quantity - filled_quantity) FILTER (WHERE side = 'buy'), 0) AS bid_quantity
    FROM market.orders
    WHERE status IN ('active', 'partially_filled')
    GROUP BY asset_id, variant
) o
FULL OUTER JOIN (
    SELECT
        asset_id,
        variant,
        COALESCE(SUM(quantity) FILTER (WHERE traded_at >= (now() AT TIME ZONE 'UTC')::date), 0) AS day_volume,
        COALESCE(SUM(quantity) FILTER (WHERE traded_at >= (date_trunc('week', now() AT TIME ZONE 'UTC'))::date), 0) AS week_volume
    FROM market.trades
    WHERE traded_at >= (date_trunc('week', now() AT TIME ZONE 'UTC'))::date
    GROUP BY asset_id, variant
) t ON o.asset_id = t.asset_id AND o.variant = t.variant
ON CONFLICT (asset_id, variant) DO NOTHING;

UPDATE game.player_state_heads SET schema_version = 33 WHERE schema_version <> 33;
UPDATE game.player_state_sections SET schema_version = 33 WHERE schema_version <> 33;
UPDATE platform.section_flush_queue SET schema_version = 33 WHERE schema_version <> 33;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (33, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
