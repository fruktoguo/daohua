-- 0065_v64_to_v65.sql
-- v64 -> v65：补齐两处高频扫描部分索引：
--   1. platform.integration_outbox(delivered_at) WHERE delivered_at IS NOT NULL（PostgresRetentionWorker 5 分钟清理防全表扫描排序）；
--   2. identity.auth_sessions(account_id) WHERE revoked_at IS NULL（改密批量吊销按账号直查）；
-- 玩家存档载荷结构与标签保持 v45 不变。
BEGIN;

DO $$
DECLARE
    current_version integer := (SELECT max(version) FROM platform.schema_version);
BEGIN
    IF current_version IS DISTINCT FROM 64 AND current_version IS DISTINCT FROM 65 THEN
        RAISE EXCEPTION '0065 requires database schema v64 (or v65 on retry), found %', current_version;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_outbox_delivered
    ON platform.integration_outbox(delivered_at)
    WHERE delivered_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_auth_sessions_account_active
    ON identity.auth_sessions(account_id)
    WHERE revoked_at IS NULL;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (65, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
