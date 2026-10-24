-- 0038_v37_to_v38.sql
-- v37 -> v38 兼容迁移（仅新增系统商店每日限购表）：
--   新增 economy.shop_daily_purchases(player_id, offer_id, day_index) 认领行，
--   用于系统商店每日限购的原子累加与超限拒绝（与经济事务同事务写入）。
--   玩家八分段载荷与存档标签保持不变（仍为 v36），不得用库版本重贴分段标签。
-- 适用对象：platform.schema_version 最新行为 37 的既有库；执行后启动门禁（要求 v38）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v38。

BEGIN;

CREATE TABLE IF NOT EXISTS economy.shop_daily_purchases (
    player_id uuid NOT NULL REFERENCES identity.players(player_id),
    offer_id varchar(64) NOT NULL,
    day_index bigint NOT NULL,
    quantity integer NOT NULL CHECK (quantity >= 0),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (player_id, offer_id, day_index)
);

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (38, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
