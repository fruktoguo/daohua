-- 0044_v43_to_v44.sql
-- v43 -> v44 兼容迁移（settings 分段载荷新增组队复活时机策略）：
--   玩家设置分段新增 respawnPolicy（immediate / all-allies-ready / target-ally-alive）
--   与 respawnTargetPlayerId（指定队友存活策略的观察对象玩家 ID），二者均可缺省解码
--   （旧存档按「直接复活、未指定」处理）；战斗快照新增的 respawnPolicy 系列字段同样
--   可缺省解码为直接复活。表结构未变，仅推进 platform.schema_version 与分段标签。
-- 适用对象：platform.schema_version 最新行为 43 的既有库；执行后启动门禁（要求 v44）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v44。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 39 WHERE schema_version <> 39;
UPDATE game.player_state_sections SET schema_version = 39 WHERE schema_version <> 39;
UPDATE platform.section_flush_queue SET schema_version = 39 WHERE schema_version <> 39;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (44, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
