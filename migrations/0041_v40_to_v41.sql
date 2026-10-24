-- 0041_v40_to_v41.sql
-- v40 -> v41 兼容迁移（progression 分段载荷新增 heavenGate 五行灵根状态）：
--   玩家存档 progression 分段新增 heavenGateRolled / heavenGateConfirmed /
--   heavenGateEntered / heavenGateRerollCount 字段，全部可缺省解码
--   （旧存档按「未开天门、改命 0 次」处理，叩天门境界需要重新开天门取得灵根）。
--   存档标签由 v37 推进至 v38；表结构未变，仅推进 platform.schema_version 与分段标签。
-- 适用对象：platform.schema_version 最新行为 40 的既有库；执行后启动门禁（要求 v41）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v41。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 38 WHERE schema_version <> 38;
UPDATE game.player_state_sections SET schema_version = 38 WHERE schema_version <> 38;
UPDATE platform.section_flush_queue SET schema_version = 38 WHERE schema_version <> 38;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (41, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
