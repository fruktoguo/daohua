-- v23 → v24：组队讨伐改为会话唯一权威计算，增加检查点与成员结算收件箱。
-- 保留所有玩家、队伍、就绪标记与资产；旧执行模型的活跃会话在迁移时按墙钟散场，
-- 禁止把已经各自结算过的旧成员剧本初始化为新会话，避免重复发放历史收益。
-- 本文件不参与编译、运行时装配或空库初始化；可随 migrations/ 整体删除。

BEGIN;

-- 与手工载荷转换共用同一事务；阻止单独执行 SQL 把旧参与行动伪装成 v24。
LOCK TABLE game.player_runtime_leases, game.player_state_heads,
    game.player_state_sections, platform.section_flush_queue, game.player_action_queue
    IN SHARE ROW EXCLUSIVE MODE;
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM game.player_runtime_leases WHERE lease_until > clock_timestamp()) THEN
        RAISE EXCEPTION 'v24 迁移拒绝有效玩家运行时租约；请停止全部服务并等待租约释放/过期后手工运行 node migrations/0024_v23_to_v24.mjs';
    END IF;
    IF EXISTS (SELECT 1 FROM game.player_state_sections WHERE schema_version = 23 AND section_kind = 'action_queue')
        OR EXISTS (SELECT 1 FROM platform.section_flush_queue WHERE schema_version = 23 AND section_kind = 'action_queue') THEN
        RAISE EXCEPTION 'v23 action_queue 尚未离线转换；请手工运行 node migrations/0024_v23_to_v24.mjs，禁止仅推进 schema 标签';
    END IF;
END $$;

DO $$
DECLARE party_reference record;
BEGIN
    FOR party_reference IN
        SELECT conname FROM pg_constraint
        WHERE conrelid = 'social.party_sessions'::regclass
          AND confrelid = 'social.parties'::regclass AND contype = 'f'
    LOOP
        EXECUTE format('ALTER TABLE social.party_sessions DROP CONSTRAINT %I', party_reference.conname);
    END LOOP;
END $$;
ALTER TABLE social.party_sessions
    ADD COLUMN IF NOT EXISTS runtime_state jsonb NULL,
    ADD COLUMN IF NOT EXISTS state_version bigint NOT NULL DEFAULT 0 CHECK (state_version >= 0),
    ADD COLUMN IF NOT EXISTS last_simulated_at timestamptz NULL;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
        WHERE conrelid = 'social.party_sessions'::regclass AND conname = 'ck_party_sessions_checkpoint_presence') THEN
        ALTER TABLE social.party_sessions ADD CONSTRAINT ck_party_sessions_checkpoint_presence
            CHECK ((runtime_state IS NULL) = (last_simulated_at IS NULL));
    END IF;
END $$;

UPDATE social.party_sessions
SET ended_at = now(), end_reason = 'authority-model-upgrade'
WHERE ended_at IS NULL AND runtime_state IS NULL;

CREATE TABLE IF NOT EXISTS social.party_session_inbox (
    session_id varchar(64) NOT NULL REFERENCES social.party_sessions(session_id),
    player_id uuid NOT NULL REFERENCES identity.players(player_id),
    sequence bigint NOT NULL CHECK (sequence > 0),
    payload jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    consumed_at timestamptz NULL,
    consumed_transaction_id uuid NULL REFERENCES economy.transactions(transaction_id),
    PRIMARY KEY (session_id, player_id, sequence),
    CHECK ((consumed_at IS NULL) = (consumed_transaction_id IS NULL))
);
CREATE INDEX IF NOT EXISTS ix_party_session_inbox_pending
    ON social.party_session_inbox(player_id, created_at, session_id, sequence) WHERE consumed_at IS NULL;

UPDATE game.player_state_heads SET schema_version = 24 WHERE schema_version = 23;
UPDATE game.player_state_sections SET schema_version = 24 WHERE schema_version = 23;
UPDATE platform.section_flush_queue SET schema_version = 24 WHERE schema_version = 23;

INSERT INTO platform.schema_version(version, applied_at)
VALUES (24, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
