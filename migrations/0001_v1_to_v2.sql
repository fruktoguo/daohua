-- 0001_v1_to_v2.sql
-- v1 → v2 兼容迁移：market.orders 增加 expires_at 过期约束与过期索引（对应提交 4e22179 的 schema v2）。
-- 适用对象：platform.schema_version 最新行为 1 的既有库；执行后启动门禁（要求 v2）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v2。

BEGIN;

-- 存量订单回填到期时间：市场订单有效期 30 天（对齐当前市场规则口径）。
ALTER TABLE market.orders
    ADD COLUMN IF NOT EXISTS expires_at timestamptz;
UPDATE market.orders
    SET expires_at = created_at + interval '30 days'
    WHERE expires_at IS NULL;
ALTER TABLE market.orders
    ALTER COLUMN expires_at SET NOT NULL,
    ADD CONSTRAINT orders_expires_at_check CHECK (expires_at > created_at);

CREATE INDEX IF NOT EXISTS ix_market_orders_expiration
    ON market.orders(partition_id, expires_at)
    WHERE status IN ('active', 'partially_filled');

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (2, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
