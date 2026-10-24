-- v33 -> v34：settings 分段载荷新增 unlockedAvatars 已解锁形象集合（可缺省解码为 ["player", "player-male"]）；
-- 表结构未变，库内容版本标签随存档载荷标签推进至 34。
BEGIN;

UPDATE game.player_state_heads SET schema_version = 34 WHERE schema_version <> 34;
UPDATE game.player_state_sections SET schema_version = 34 WHERE schema_version <> 34;
UPDATE platform.section_flush_queue SET schema_version = 34 WHERE schema_version <> 34;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (34, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
