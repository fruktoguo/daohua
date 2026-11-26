-- 0068_v67_to_v68.sql
-- v67 -> v68：
--   战斗统计新增紧凑 PVP 斗法记录：
--     - duel 分段 PlayerBattleReportDocument 新增 duelRecords（可缺省解码）；
--     - actionQueue 分段 DuelPlaybackState 新增 viewerRecord（可缺省解码）；
--     - 推进三处玩家存档载荷标签 v47 -> v48（无 DDL 变更，不改写存量载荷字节与校验和）。
BEGIN;

DO $$
DECLARE
    current_version integer := (SELECT max(version) FROM platform.schema_version);
BEGIN
    IF current_version IS DISTINCT FROM 67 AND current_version IS DISTINCT FROM 68 THEN
        RAISE EXCEPTION '0068 requires database schema v67 (or v68 on retry), found %', current_version;
    END IF;
END $$;

UPDATE game.player_state_heads
   SET schema_version = 48
 WHERE schema_version = 47;

UPDATE game.player_state_sections
   SET schema_version = 48
 WHERE schema_version = 47;

UPDATE platform.section_flush_queue
   SET schema_version = 48
 WHERE schema_version = 47;

INSERT INTO platform.schema_version(version, applied_at)
VALUES (68, now())
ON CONFLICT (version) DO NOTHING;

COMMIT;
