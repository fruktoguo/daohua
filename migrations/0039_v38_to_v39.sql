-- 0039_v38_to_v39.sql
-- v38 -> v39 兼容迁移（market.item_stats 增 reference_price 参考价格字段）：
--   新增 market.item_stats.reference_price 字段，存储最近 10 次成交的单值滑动平均价格（EMA）。
--   一次购买多个算作一次，以单价更新；未成交时为 NULL（系统回收价兜底）。
--   玩家八分段载荷与存档标签保持不变（仍为 v36）。
-- 适用对象：platform.schema_version 最新行为 38 的既有库；执行后启动门禁（要求 v39）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v39。

BEGIN;

ALTER TABLE market.item_stats
    ADD COLUMN IF NOT EXISTS reference_price double precision NULL
    CHECK (reference_price IS NULL OR (reference_price > 0 AND reference_price = reference_price AND reference_price < 'Infinity'::double precision));

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (39, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
