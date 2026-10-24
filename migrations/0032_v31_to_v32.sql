-- v31 -> v32：world 分段建筑效果 BuildingEffectSet 新增 monsterWeakenRatio（封魔阵怪物削弱，
-- 可缺省解码为 0）；组队会话规格新增 MonsterWeakenRatio 冻结字段（spec jsonb 可缺省解码），
-- 存档/队列 schema_version 标签随库内容版本同步推进至 32，表结构未变。
BEGIN;

UPDATE game.player_state_heads SET schema_version = 32 WHERE schema_version <> 32;
UPDATE game.player_state_sections SET schema_version = 32 WHERE schema_version <> 32;
UPDATE platform.section_flush_queue SET schema_version = 32 WHERE schema_version <> 32;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (32, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
