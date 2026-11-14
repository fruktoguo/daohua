-- 0053_v52_to_v53.sql
-- v52 -> v53 兼容迁移：
--   1. 新增瞬时经济事务短期幂等防重表 economy.transient_idempotency 及按 created_at 清理索引
--      （防止 PersistLedger=false 事务在结局未知重放时双倍入账/扣减）；
--   2. 按最小权限模型补全 daohua_api 对既有遗漏业务表及全部业务 schema 序列（IDENTITY/SERIAL）的必要权限；
--   3. 玩家八分段载荷与存档标签保持不变（仍为 v42）。
-- 适用对象：platform.schema_version 最新行为 52 的既有库；执行后启动门禁（要求 v53）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v53。

BEGIN;

CREATE TABLE IF NOT EXISTS economy.transient_idempotency (
    player_id uuid NOT NULL REFERENCES identity.players(player_id),
    idempotency_key varchar(200) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (player_id, idempotency_key)
);

CREATE INDEX IF NOT EXISTS ix_transient_idempotency_created_at
    ON economy.transient_idempotency(created_at);

ALTER TABLE economy.transient_idempotency OWNER TO daohua_bootstrap;
ALTER TABLE economy.shop_daily_purchases OWNER TO daohua_bootstrap;
ALTER TABLE market.item_stats OWNER TO daohua_bootstrap;
ALTER TABLE market.settlement_cursors OWNER TO daohua_bootstrap;
ALTER TABLE social.party_session_inbox OWNER TO daohua_bootstrap;
ALTER TABLE social.zongmen OWNER TO daohua_bootstrap;
ALTER TABLE social.zongmen_members OWNER TO daohua_bootstrap;
ALTER TABLE social.zongmen_requests OWNER TO daohua_bootstrap;
ALTER TABLE social.zongmen_departures OWNER TO daohua_bootstrap;

GRANT SELECT, INSERT, UPDATE, DELETE ON
    economy.shop_daily_purchases,
    economy.transient_idempotency,
    market.item_stats,
    market.settlement_cursors,
    social.party_session_inbox,
    social.zongmen,
    social.zongmen_members,
    social.zongmen_requests,
    social.zongmen_departures
    TO daohua_api;

GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA identity, game, economy, market, platform, social
    TO daohua_api, daohua_worker;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (53, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
