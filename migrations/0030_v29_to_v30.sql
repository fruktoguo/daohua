-- v29 → v30 兼容迁移：combat 分段新增战内 Buff 跨波携带态（Combat.Buffs），
-- 参与成员快照新增 CombatBuffs 冻结字段——全部可缺省解码为空携带态，
-- 存档载荷无损兼容，仅推进 schema_version 标签；表结构未变。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 30 WHERE schema_version <> 30;
UPDATE game.player_state_sections SET schema_version = 30 WHERE schema_version <> 30;
UPDATE platform.section_flush_queue SET schema_version = 30 WHERE schema_version <> 30;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (30, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
