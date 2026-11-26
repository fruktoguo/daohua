-- 0069_v68_to_v69.sql
-- v68 -> v69：
--   符箓与阵法技艺每级提升 1% 对应伤害：
--     - CombatantStats（actionQueue / duel 载荷内冻结战斗属性）新增 talismanDamageBonusPoints 与 formationDamageBonusPoints（均可缺省解码为 0）；
--     - 推进三处玩家存档载荷标签 v48 -> v49（无 DDL 变更，不改写存量载荷字节与校验和）。
BEGIN;

DO $$
DECLARE
    current_version integer := (SELECT max(version) FROM platform.schema_version);
BEGIN
    IF current_version IS DISTINCT FROM 68 AND current_version IS DISTINCT FROM 69 THEN
        RAISE EXCEPTION '0069 requires database schema v68 (or v69 on retry), found %', current_version;
    END IF;
END $$;

UPDATE game.player_state_heads
   SET schema_version = 49
 WHERE schema_version = 48;

UPDATE game.player_state_sections
   SET schema_version = 49
 WHERE schema_version = 48;

UPDATE platform.section_flush_queue
   SET schema_version = 49
 WHERE schema_version = 48;

INSERT INTO platform.schema_version(version, applied_at)
VALUES (69, now())
ON CONFLICT (version) DO NOTHING;

COMMIT;
