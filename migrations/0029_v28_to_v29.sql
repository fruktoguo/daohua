-- v28 → v29 兼容迁移：social.parties 增 pvp_enabled 队伍斗法开关（总纲 §7.10.4）。
-- 队伍斗法开关表达整队作为一个匹配单位参与 PVP 撮合：仅 activity_kind='combat'
-- 可为 true、仅队长可改、会话创建时冻结进参与规格。存量队伍一律归位 false，
-- 非讨伐目标本就不允许置位，无需额外校正。

BEGIN;

ALTER TABLE social.parties
    ADD COLUMN IF NOT EXISTS pvp_enabled boolean NOT NULL DEFAULT false;

-- 存档载荷 v25-v29 均无字段变化，仅标签随库内容版本同步推进；
-- v25-v28 迁移未触及存档标签，本版本一次性把存量标签收敛到 29。
UPDATE game.player_state_heads SET schema_version = 29 WHERE schema_version <> 29;
UPDATE game.player_state_sections SET schema_version = 29 WHERE schema_version <> 29;
UPDATE platform.section_flush_queue SET schema_version = 29 WHERE schema_version <> 29;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (29, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
