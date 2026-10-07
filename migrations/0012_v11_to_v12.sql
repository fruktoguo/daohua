-- 0012_v11_to_v12.sql
-- v11 → v12 兼容迁移（仅存档 schema_version 标签推进）：
--   loadout 分段载荷结构调整：功法自定义触发条件从 abilitySlots[].triggers 槽位字段
--   迁移为顶层 abilityTriggers（按功法 ID 映射）。新字段可缺省解码——旧存档缺失
--   abilityTriggers 时按空表恢复，槽位上遗留的 triggers 字段在反序列化时被忽略丢弃
--   （内测口径允许丢弃历史触发条件配置）。
-- 适用对象：platform.schema_version 最新行为 11 的既有库；执行后启动门禁（要求 v12）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v12。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 12 WHERE schema_version = 11;
UPDATE game.player_state_sections SET schema_version = 12 WHERE schema_version = 11;
UPDATE platform.section_flush_queue SET schema_version = 12 WHERE schema_version = 11;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (12, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
