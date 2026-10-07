-- 0014_v13_to_v14.sql
-- v13 → v14 兼容迁移（仅存档 schema_version 标签推进）：
--   settings 分段载荷新增 tutorialStep 新手引导里程碑。新字段可缺省解码——
--   旧存档缺失 tutorialStep 时按 0 恢复（既有角色首次登录进入引导，可跳过）。
-- 适用对象：platform.schema_version 最新行为 13 的既有库；执行后启动门禁（要求 v14）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v14。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 14 WHERE schema_version = 13;
UPDATE game.player_state_sections SET schema_version = 14 WHERE schema_version = 13;
UPDATE platform.section_flush_queue SET schema_version = 14 WHERE schema_version = 13;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (14, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
