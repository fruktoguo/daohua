-- 0067_v66_to_v67.sql
-- v66 -> v67：
--   1. 追忆副本组队表结构升级：
--      - social.parties 扩展 activity_kind 允许 'dungeon'、放宽 difficulty 范围为 0..10、新增 dungeon_id 与 dungeon_repeat_count 列；
--      - social.party_members 新增 assist_mode 助战标记列；
--      - social.party_seekers 扩展 activity_kind 允许 'dungeon'；
--   2. 玩家八分段 progression 新增可缺省体力锚点字段（tiliValue/tiliUpdatedAtUnixSeconds），
--      actionQueue 远征载荷新增可缺省追忆副本标记字段（dungeonId/dungeonTotalWaves/dungeonEnergyCharged）；
--      推进三处存档载荷标签 v46 -> v47（不改写存量载荷字节与校验和）。
BEGIN;

DO $$
DECLARE
    current_version integer := (SELECT max(version) FROM platform.schema_version);
BEGIN
    IF current_version IS DISTINCT FROM 66 AND current_version IS DISTINCT FROM 67 THEN
        RAISE EXCEPTION '0067 requires database schema v66 (or v67 on retry), found %', current_version;
    END IF;
END $$;

-- 1. 重建 social.parties 的 activity_kind 与 difficulty CHECK 约束，并补齐追忆副本列。
DO $$
DECLARE
    con record;
BEGIN
    FOR con IN
        SELECT conname
        FROM pg_constraint
        WHERE conrelid = 'social.parties'::regclass
          AND contype = 'c'
          AND (pg_get_constraintdef(oid) ILIKE '%activity_kind%'
               OR pg_get_constraintdef(oid) ILIKE '%difficulty%')
    LOOP
        EXECUTE pg_catalog.format('ALTER TABLE social.parties DROP CONSTRAINT IF EXISTS %I', con.conname);
    END LOOP;
END $$;

ALTER TABLE social.parties
    ADD CONSTRAINT ck_parties_activity_kind
        CHECK (activity_kind IN ('any', 'combat', 'dungeon', 'exploration', 'cultivation', 'alchemy', 'forging', 'talisman', 'formation', 'enhancement')),
    ADD CONSTRAINT ck_parties_difficulty
        CHECK (difficulty BETWEEN 0 AND 10);

ALTER TABLE social.parties
    ADD COLUMN IF NOT EXISTS dungeon_id varchar(128) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS dungeon_repeat_count integer NOT NULL DEFAULT 1;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'social.parties'::regclass
          AND conname = 'ck_parties_dungeon_repeat_count'
    ) THEN
        ALTER TABLE social.parties
            ADD CONSTRAINT ck_parties_dungeon_repeat_count
                CHECK (dungeon_repeat_count = -1 OR (dungeon_repeat_count >= 1 AND dungeon_repeat_count <= 99));
    END IF;
END $$;

-- 2. social.party_members 新增助战标记列。
ALTER TABLE social.party_members
    ADD COLUMN IF NOT EXISTS assist_mode boolean NOT NULL DEFAULT false;

-- 3. 重建 social.party_seekers 的 activity_kind CHECK 约束。
DO $$
DECLARE
    con record;
BEGIN
    FOR con IN
        SELECT conname
        FROM pg_constraint
        WHERE conrelid = 'social.party_seekers'::regclass
          AND contype = 'c'
          AND pg_get_constraintdef(oid) ILIKE '%activity_kind%'
    LOOP
        EXECUTE pg_catalog.format('ALTER TABLE social.party_seekers DROP CONSTRAINT IF EXISTS %I', con.conname);
    END LOOP;
END $$;

ALTER TABLE social.party_seekers
    ADD CONSTRAINT ck_party_seekers_activity_kind
        CHECK (activity_kind IN ('any', 'combat', 'dungeon', 'exploration', 'cultivation', 'alchemy', 'forging', 'talisman', 'formation', 'enhancement'));

-- 4. 推进三处玩家存档载荷标签 46 -> 47（additive 可缺省解码，无需重写 JSON 载荷与校验和）。
UPDATE game.player_state_heads
   SET schema_version = 47
 WHERE schema_version = 46;

UPDATE game.player_state_sections
   SET schema_version = 47
 WHERE schema_version = 46;

UPDATE platform.section_flush_queue
   SET schema_version = 47
 WHERE schema_version = 46;

INSERT INTO platform.schema_version(version, applied_at)
VALUES (67, now())
ON CONFLICT (version) DO NOTHING;

COMMIT;
