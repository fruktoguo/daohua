-- 0040_v39_to_v40.sql
-- v39 -> v40 兼容迁移（loadout 分段载荷新增 consumableTriggers 消耗品触发条件映射）：
--   玩家存档 loadout 分段新增 consumableTriggers 字段（物品 ID -> 触发条件列表），
--   可缺省解码为 null/[]（旧存档按「未配置」处理，条件回退配表 consumable.triggers 出厂默认）。
--   存档标签由 v36 推进至 v37；表结构未变，仅推进 platform.schema_version 与分段标签。
-- 适用对象：platform.schema_version 最新行为 39 的既有库；执行后启动门禁（要求 v40）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v40。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 37 WHERE schema_version <> 37;
UPDATE game.player_state_sections SET schema_version = 37 WHERE schema_version <> 37;
UPDATE platform.section_flush_queue SET schema_version = 37 WHERE schema_version <> 37;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (40, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
