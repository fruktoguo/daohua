-- 0059_v58_to_v59.sql
-- v58 -> v59 兼容迁移：
--   1. platform.section_flush_queue 与 platform.integration_outbox 增加 status 死信列及部分就绪/死信索引；
--   2. platform.compute_jobs 增加 dead_count 判死次数计数列与按玩家查死信部分索引；
--   3. 补齐清理/反查索引：auth_sessions(expires_at/revoked_at)、economy.reservations 终态部分索引、
--      market.orders 终态部分索引、market.trades(buy_order_id/sell_order_id)、
--      market.settlement_claims(claimed_at)、social.party_session_inbox 已消费部分索引；
--   4. 为高频写表 player_projection / player_runtime_leases / player_asset_balances 设置 fillfactor/autovacuum 调优；
--   5. 玩家分段载荷结构与存档标签保持 v43 不变，禁止推进 schema_version 标签。
-- 适用对象：platform.schema_version 最新行为 58 的既有库；执行后启动门禁（要求 v59）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v59。

BEGIN;

ALTER TABLE platform.section_flush_queue
    ADD COLUMN IF NOT EXISTS status varchar(16) NOT NULL DEFAULT 'pending';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'platform.section_flush_queue'::regclass
          AND conname = 'section_flush_queue_status_check'
    ) THEN
        ALTER TABLE platform.section_flush_queue
            ADD CONSTRAINT section_flush_queue_status_check CHECK (status IN ('pending', 'dead'));
    END IF;
END $$;

DROP INDEX IF EXISTS platform.ix_section_flush_ready;
CREATE INDEX IF NOT EXISTS ix_section_flush_ready
    ON platform.section_flush_queue(next_attempt_at) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS ix_section_flush_dead
    ON platform.section_flush_queue(updated_at) WHERE status = 'dead';

ALTER TABLE platform.integration_outbox
    ADD COLUMN IF NOT EXISTS status varchar(16) NOT NULL DEFAULT 'pending';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'platform.integration_outbox'::regclass
          AND conname = 'integration_outbox_status_check'
    ) THEN
        ALTER TABLE platform.integration_outbox
            ADD CONSTRAINT integration_outbox_status_check CHECK (status IN ('pending', 'dead'));
    END IF;
END $$;

DROP INDEX IF EXISTS platform.ix_outbox_ready;
CREATE INDEX IF NOT EXISTS ix_outbox_ready
    ON platform.integration_outbox(visible_at, lease_until) WHERE delivered_at IS NULL AND status = 'pending';
CREATE INDEX IF NOT EXISTS ix_outbox_dead
    ON platform.integration_outbox(visible_at) WHERE delivered_at IS NULL AND status = 'dead';

ALTER TABLE platform.compute_jobs
    ADD COLUMN IF NOT EXISTS dead_count integer NOT NULL DEFAULT 0;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'platform.compute_jobs'::regclass
          AND conname = 'compute_jobs_dead_count_check'
    ) THEN
        ALTER TABLE platform.compute_jobs
            ADD CONSTRAINT compute_jobs_dead_count_check CHECK (dead_count >= 0);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_compute_jobs_dead_player
    ON platform.compute_jobs(player_id, updated_at DESC) WHERE status = 'dead';

CREATE INDEX IF NOT EXISTS ix_auth_sessions_expires
    ON identity.auth_sessions(expires_at);
CREATE INDEX IF NOT EXISTS ix_auth_sessions_revoked
    ON identity.auth_sessions(revoked_at) WHERE revoked_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_reservations_terminal_updated
    ON economy.reservations(updated_at) WHERE status <> 'active';

CREATE INDEX IF NOT EXISTS ix_market_orders_terminal_updated
    ON market.orders(updated_at) WHERE status IN ('filled', 'cancelled', 'expired');

CREATE INDEX IF NOT EXISTS ix_market_trades_buy_order
    ON market.trades(buy_order_id);
CREATE INDEX IF NOT EXISTS ix_market_trades_sell_order
    ON market.trades(sell_order_id);

CREATE INDEX IF NOT EXISTS ix_settlement_claims_claimed
    ON market.settlement_claims(claimed_at);

CREATE INDEX IF NOT EXISTS ix_party_session_inbox_consumed_at
    ON social.party_session_inbox(consumed_at) WHERE consumed_at IS NOT NULL;

ALTER TABLE game.player_projection SET (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.05, autovacuum_analyze_scale_factor = 0.02);
ALTER TABLE game.player_runtime_leases SET (fillfactor = 70, autovacuum_vacuum_scale_factor = 0.02, autovacuum_vacuum_cost_limit = 1000);
ALTER TABLE economy.player_asset_balances SET (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.05, autovacuum_analyze_scale_factor = 0.02);

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (59, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
