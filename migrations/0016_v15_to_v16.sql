-- 0016_v15_to_v16.sql
-- v15 → v16 兼容迁移（仅存档 schema_version 标签推进）：
--   actionQueue 分段远征载荷新增 isPinned 遭遇供给标记：true=显式钉死（CreateEncounter
--   手动入战，与地点无关）；false=池选远征，迁地时按驻留节点遭遇池判定重锚。
--   新字段可缺省解码——旧存档缺失时按 false 恢复；历史远征全部出自节点池，
--   缺省 false 即为正确语义，无需载荷改写。
-- 适用对象：platform.schema_version 最新行为 15 的既有库；执行后启动门禁（要求 v16）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v16。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 16 WHERE schema_version = 15;
UPDATE game.player_state_sections SET schema_version = 16 WHERE schema_version = 15;
UPDATE platform.section_flush_queue SET schema_version = 16 WHERE schema_version = 15;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (16, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
