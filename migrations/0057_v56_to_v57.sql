-- 0057_v56_to_v57.sql
-- v56 -> v57 兼容迁移：
--   1. 新增 PVP 斗法完整收益日志表 game.duel_logs 及 4 个索引（30 天滚动留存）；
--   2. 新增系统商店每日限购时间清理索引 economy.ix_shop_daily_purchases_updated；
--   3. 为高频状态/队列/瞬时幂等四张热表调优 fillfactor 与 autovacuum 阈值，抑制页分裂与写放大；
--   4. 为 daohua_bootstrap / daohua_api / daohua_worker 补齐 game.duel_logs 属主与 DML 权限；
--   5. 玩家分段载荷结构与存档标签保持 v43 不变，禁止推进 schema_version 标签。
-- 适用对象：platform.schema_version 最新行为 56 的既有库；执行后启动门禁（要求 v57）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v57。

BEGIN;

CREATE TABLE IF NOT EXISTS game.duel_logs (
    duel_log_id uuid PRIMARY KEY,
    duel_id varchar(96) NOT NULL,
    initiator_player_id uuid NOT NULL REFERENCES identity.players(player_id) ON DELETE CASCADE,
    initiator_name varchar(64) NOT NULL,
    initiator_power double precision NOT NULL DEFAULT 0,
    target_player_id uuid NOT NULL REFERENCES identity.players(player_id) ON DELETE CASCADE,
    target_name varchar(64) NOT NULL,
    target_power double precision NOT NULL DEFAULT 0,
    node_id varchar(128) NOT NULL,
    winner_player_id uuid NULL,
    outcome varchar(24) NOT NULL CHECK (outcome IN ('initiator_win', 'target_win', 'draw')),
    forfeited boolean NOT NULL DEFAULT false,
    initiator_gains jsonb NOT NULL DEFAULT '[]'::jsonb,
    initiator_losses jsonb NOT NULL DEFAULT '[]'::jsonb,
    target_gains jsonb NOT NULL DEFAULT '[]'::jsonb,
    target_losses jsonb NOT NULL DEFAULT '[]'::jsonb,
    duration_seconds double precision NOT NULL DEFAULT 0,
    occurred_at timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_duel_logs_duel_id ON game.duel_logs(duel_id);
CREATE INDEX IF NOT EXISTS ix_duel_logs_occurred ON game.duel_logs(occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_duel_logs_initiator ON game.duel_logs(initiator_player_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_duel_logs_target ON game.duel_logs(target_player_id, occurred_at DESC);

CREATE INDEX IF NOT EXISTS ix_shop_daily_purchases_updated
    ON economy.shop_daily_purchases(updated_at);

ALTER TABLE game.player_state_heads SET (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.05, autovacuum_analyze_scale_factor = 0.02);
ALTER TABLE game.player_state_sections SET (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.05, autovacuum_analyze_scale_factor = 0.02);
ALTER TABLE platform.section_flush_queue SET (fillfactor = 70, autovacuum_vacuum_scale_factor = 0.02, autovacuum_vacuum_cost_limit = 1000);
ALTER TABLE economy.transient_idempotency SET (fillfactor = 70, autovacuum_vacuum_scale_factor = 0.05, autovacuum_vacuum_cost_limit = 1000);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_bootstrap') THEN
        EXECUTE 'ALTER TABLE game.duel_logs OWNER TO daohua_bootstrap';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_api') THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON game.duel_logs TO daohua_api';
        EXECUTE 'GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA game TO daohua_api';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_worker') THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON game.duel_logs TO daohua_worker';
        EXECUTE 'GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA game TO daohua_worker';
    END IF;
END $$;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (57, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
