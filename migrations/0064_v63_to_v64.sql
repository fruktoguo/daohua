-- 0064_v63_to_v64.sql
-- v63 -> v64：筑基期根基系统（progression 分段新增 genjiPoints / genjiUnlocked，可缺省解码为 0 / false），
-- 推进三处玩家存档载荷标签 v44 -> v45；无表结构或权限变更，不重写 JSON/Brotli 字节与 SHA-256 校验和。
BEGIN;

DO $$
DECLARE
    current_version integer := (SELECT max(version) FROM platform.schema_version);
BEGIN
    IF current_version IS DISTINCT FROM 63 AND current_version IS DISTINCT FROM 64 THEN
        RAISE EXCEPTION '0064 requires database schema v63 (or v64 on retry), found %', current_version;
    END IF;
END $$;

UPDATE game.player_state_heads SET schema_version = 45 WHERE schema_version = 44;
UPDATE game.player_state_sections SET schema_version = 45 WHERE schema_version = 44;
UPDATE platform.section_flush_queue SET schema_version = 45 WHERE schema_version = 44;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM game.player_state_heads WHERE schema_version <> 45)
        OR EXISTS (SELECT 1 FROM game.player_state_sections WHERE schema_version <> 45)
        OR EXISTS (SELECT 1 FROM platform.section_flush_queue WHERE schema_version <> 45) THEN
        RAISE EXCEPTION '0064 found unexpected player save schema labels; inspect before retrying';
    END IF;
END $$;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (64, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
