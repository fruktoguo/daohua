-- v30 -> v31：game.player_projection 新增 offline_since 离线锚点投影列（NULL=在线），
-- 存量离线行以提交头 last_simulated_at 补锚；settings 分段新增 offlineSinceAt（可缺省解码），
-- 存档/队列 schema_version 标签随库内容版本同步推进至 31。
BEGIN;

ALTER TABLE game.player_projection ADD COLUMN IF NOT EXISTS offline_since timestamptz NULL;
UPDATE game.player_projection p SET offline_since = h.last_simulated_at
FROM game.player_state_heads h
WHERE h.player_id = p.player_id AND p.presence <> 'online' AND p.offline_since IS NULL;

UPDATE game.player_state_heads SET schema_version = 31 WHERE schema_version <> 31;
UPDATE game.player_state_sections SET schema_version = 31 WHERE schema_version <> 31;
UPDATE platform.section_flush_queue SET schema_version = 31 WHERE schema_version <> 31;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (31, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
