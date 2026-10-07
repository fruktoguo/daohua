-- 0017_v16_to_v17.sql
-- v16 → v17 兼容迁移（仅存档 schema_version 标签推进）：
--   world 分段载荷新增 nodeBuildingUsages 节点建筑拥挤使用快照（buildingId →
--   使用人数/承载上限/效率系数）：随节点建筑投影同节拍下发并持久化，
--   供建筑 tab「使用中 n/cap」与超载衰减展示；效果衰减随投影内 NodeBuildingEffects
--   一并承载，运行时结算语义不变。
--   新字段可缺省解码——旧存档缺失时按 null 恢复为「未知拥挤」，等价零人数满效口径，
--   无需载荷改写。
-- 适用对象：platform.schema_version 最新行为 16 的既有库；执行后启动门禁（要求 v17）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v17。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 17 WHERE schema_version = 16;
UPDATE game.player_state_sections SET schema_version = 17 WHERE schema_version = 16;
UPDATE platform.section_flush_queue SET schema_version = 17 WHERE schema_version = 16;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (17, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
