-- 0072_v71_to_v72.sql
-- v71 -> v72：
--   新战斗流派特质、护盾系统、三门神通与六符矩阵：
--     - actionQueue 战斗单位快照 CombatantSnapshot 新增 shield / lastShanshenbuTriggerAtSeconds / lastDirectDamageAtSeconds（缺省 0 / null）；
--     - combat 分段 PlayerCombatStateDocument 新增 talismanCooldowns（主动术法槽符箓冷却时间戳字典，缺省 null）；
--     - 推进三处玩家存档载荷标签 v50 -> v51（无 DDL 变更，不改写存量载荷字节与校验和）。
BEGIN;

DO $$
BEGIN
    IF (SELECT max(version) FROM platform.schema_version) IS DISTINCT FROM 71
       AND (SELECT max(version) FROM platform.schema_version) IS DISTINCT FROM 72 THEN
        RAISE EXCEPTION '0072 requires database schema v71 (or v72 on retry), found %',
            (SELECT max(version) FROM platform.schema_version);
    END IF;
END $$;

UPDATE game.player_state_heads
   SET schema_version = 51
 WHERE schema_version = 50;

UPDATE game.player_state_sections
   SET schema_version = 51
 WHERE schema_version = 50;

UPDATE platform.section_flush_queue
   SET schema_version = 51
 WHERE schema_version = 50;

INSERT INTO platform.schema_version(version, applied_at)
VALUES (72, now())
ON CONFLICT (version) DO NOTHING;

COMMIT;
