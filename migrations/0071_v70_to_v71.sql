-- 0071_v70_to_v71.sql
-- v70 -> v71：
--   双阵营完全对称战斗引擎与 1~5v1~5 多方斗法协议重构：
--     - actionQueue 斗法回放 DuelScriptEvent 新增 attackerUnitIndex / targetUnitIndex（缺省 0）；
--     - actionQueue 斗法回放 DuelPlaybackState 新增 selfUnitIndex / selfUnits / opponentUnits（缺省 0 / null）；
--     - 推进三处玩家存档载荷标签 v49 -> v50（无 DDL 变更，不改写存量载荷字节与校验和）。
BEGIN;

DO $$
DECLARE
    current_version integer := (SELECT max(version) FROM platform.schema_version);
BEGIN
    IF current_version IS DISTINCT FROM 70 AND current_version IS DISTINCT FROM 71 THEN
        RAISE EXCEPTION '0071 requires database schema v70 (or v71 on retry), found %', current_version;
    END IF;
END $$;

UPDATE game.player_state_heads
   SET schema_version = 50
 WHERE schema_version = 49;

UPDATE game.player_state_sections
   SET schema_version = 50
 WHERE schema_version = 49;

UPDATE platform.section_flush_queue
   SET schema_version = 50
 WHERE schema_version = 49;

INSERT INTO platform.schema_version(version, applied_at)
VALUES (71, now())
ON CONFLICT (version) DO NOTHING;

COMMIT;
