-- v35 -> v36：progression 分段载荷新增 unlockedTraits 特质等级经验养成集合（可缺省解码为 null/[]）；
-- 表结构未变，库内容版本标签随存档载荷标签推进至 36。
BEGIN;

UPDATE game.player_state_heads SET schema_version = 36 WHERE schema_version <> 36;
UPDATE game.player_state_sections SET schema_version = 36 WHERE schema_version <> 36;
UPDATE platform.section_flush_queue SET schema_version = 36 WHERE schema_version <> 36;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (36, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
