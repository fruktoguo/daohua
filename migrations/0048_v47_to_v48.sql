-- v47 -> v48：战斗特质等级、持续伤害来源和真实伤害统计可缺省解码。
-- 仅推进库内容与分段标签；不改玩家载荷，不清空数据。
BEGIN;
UPDATE game.player_state_heads SET schema_version = 41 WHERE schema_version = 40;
UPDATE game.player_state_sections SET schema_version = 41 WHERE schema_version = 40;
UPDATE platform.section_flush_queue SET schema_version = 41 WHERE schema_version = 40;
INSERT INTO platform.schema_version(version, applied_at)
    VALUES (48, now()) ON CONFLICT (version) DO NOTHING;
COMMIT;
