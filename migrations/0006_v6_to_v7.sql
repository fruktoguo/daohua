-- 0006_v6_to_v7.sql
-- v6 → v7 兼容迁移（表结构变更 + 存档 schema_version 标签推进）：
--   social.parties 追加队伍档案列（name/activity_kind/node_id/min_valuation/is_public/updated_at）；
--   新增 social.party_seekers「匹配中」散人池；
--   social.chat_messages 增 party_ref 队伍链接外键（ON DELETE SET NULL）；
--   存档载荷字段无变化，仅把既有 schema_version=6 行重贴为 7，
--   payload/codec/raw_bytes/checksum 原样保留（schema_version 不参与校验和计算）。
-- 适用对象：platform.schema_version 最新行为 6 的既有库；执行后启动门禁（要求 v7）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v7。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 7 WHERE schema_version = 6;
UPDATE game.player_state_sections SET schema_version = 7 WHERE schema_version = 6;
UPDATE platform.section_flush_queue SET schema_version = 7 WHERE schema_version = 6;

ALTER TABLE social.parties
    ADD COLUMN IF NOT EXISTS name varchar(32) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS activity_kind varchar(16) NOT NULL DEFAULT 'any',
    ADD COLUMN IF NOT EXISTS node_id varchar(128) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS min_valuation double precision NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS is_public boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now(),
    ADD CONSTRAINT parties_activity_kind_check CHECK (activity_kind IN ('any', 'combat', 'exploration', 'cultivation', 'alchemy', 'forging', 'talisman', 'formation', 'enhancement')),
    ADD CONSTRAINT parties_min_valuation_check CHECK (min_valuation >= 0);
CREATE INDEX IF NOT EXISTS ix_parties_lobby ON social.parties(is_public, activity_kind, node_id)
    WHERE is_public;

CREATE TABLE IF NOT EXISTS social.party_seekers (
    player_id uuid PRIMARY KEY REFERENCES identity.players(player_id),
    activity_kind varchar(16) NOT NULL DEFAULT 'any'
        CHECK (activity_kind IN ('any', 'combat', 'exploration', 'cultivation', 'alchemy', 'forging', 'talisman', 'formation', 'enhancement')),
    node_id varchar(128) NOT NULL DEFAULT '',
    min_valuation double precision NOT NULL DEFAULT 0 CHECK (min_valuation >= 0),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_party_seekers_goal ON social.party_seekers(activity_kind, node_id);

ALTER TABLE social.chat_messages
    ADD COLUMN IF NOT EXISTS party_ref uuid REFERENCES social.parties(party_id) ON DELETE SET NULL;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (7, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
