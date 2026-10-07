-- 0013_v12_to_v13.sql
-- v12 → v13 兼容迁移：邀请系统落地。
--   1. identity.players 增 invite_code（专属邀请码）；存量玩家按 player_id 派生
--      确定性的 10 位大写编码回填，随后建唯一索引。
--   2. 新增 social.player_invitations 邀请关系表（里程碑领取标记见表内布尔列）。
-- 存档载荷无结构变化（邀请状态不入分段存档），仅推进 schema_version 标签。
-- 适用对象：platform.schema_version 最新行为 12 的既有库；执行后启动门禁（要求 v13）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v13。

BEGIN;

-- 邀请码默认值兼作存量回填：volatile DEFAULT 对既有行逐行求值，一步到位；
-- 若极小概率撞码则唯一索引创建失败，迁移整体回滚并需人工处理。
ALTER TABLE identity.players ADD COLUMN invite_code varchar(16)
    NOT NULL DEFAULT upper(substr(md5(random()::text || clock_timestamp()::text), 1, 10));
CREATE UNIQUE INDEX ux_players_invite_code ON identity.players(invite_code);

CREATE TABLE social.player_invitations (
    invitee_id uuid PRIMARY KEY REFERENCES identity.players(player_id),
    inviter_id uuid NOT NULL REFERENCES identity.players(player_id),
    invitation_code varchar(16) NOT NULL,
    highest_realm_level integer NOT NULL DEFAULT 1,
    qi_rewarded boolean NOT NULL DEFAULT false,
    foundation_rewarded boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CHECK (inviter_id <> invitee_id)
);
CREATE INDEX ix_player_invitations_inviter ON social.player_invitations(inviter_id);

-- 统一内容版本口径：表结构变更同步推进存档载荷标签（载荷本身无变化，可缺省解码）。
UPDATE game.player_state_heads SET schema_version = 13 WHERE schema_version = 12;
UPDATE game.player_state_sections SET schema_version = 13 WHERE schema_version = 12;
UPDATE platform.section_flush_queue SET schema_version = 13 WHERE schema_version = 12;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (13, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
