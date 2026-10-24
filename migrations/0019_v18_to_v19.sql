-- 0019_v18_to_v19.sql
-- v18 → v19 兼容迁移（表结构变更，无存档载荷变更）：
--   组队讨伐（总纲 §21.13）社交侧持久化：
--   1. social.parties 增 open_slots 开启槽位（2..5，默认 5；收窄不得低于当前队员数由社交层裁决保证）；
--   2. social.party_members 增 ready 就绪标记（默认 false，成员自开关）；
--   3. 新增 social.party_sessions：持久化活跃/终局讨伐会话（spec jsonb 冻结共享剧本规格），
--      同一队伍同时至多一个活跃会话（ended_at IS NULL 部分唯一索引收敛并发仲裁）。
--   存档标签无需推进：SectionCodec 载荷结构本版本未变。
-- 适用对象：platform.schema_version 最新行为 18 的既有库；执行后启动门禁（要求 v19）即通过。
-- 全部语句幂等（IF NOT EXISTS / DROP CONSTRAINT IF EXISTS + ADD CONSTRAINT），可重复执行。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v19。

BEGIN;

ALTER TABLE social.parties ADD COLUMN IF NOT EXISTS
    open_slots smallint NOT NULL DEFAULT 5;
ALTER TABLE social.parties DROP CONSTRAINT IF EXISTS parties_open_slots_check;
ALTER TABLE social.parties ADD CONSTRAINT parties_open_slots_check
    CHECK (open_slots BETWEEN 2 AND 5);

ALTER TABLE social.party_members ADD COLUMN IF NOT EXISTS
    ready boolean NOT NULL DEFAULT false;

CREATE TABLE IF NOT EXISTS social.party_sessions (
    session_id varchar(64) PRIMARY KEY,
    party_id uuid NOT NULL REFERENCES social.parties(party_id) ON DELETE CASCADE,
    spec jsonb NOT NULL,
    started_at timestamptz NOT NULL,
    ended_at timestamptz NULL,
    end_reason varchar(64) NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_party_sessions_active
    ON social.party_sessions(party_id) WHERE ended_at IS NULL;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (19, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
