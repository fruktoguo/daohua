-- 0052_v51_to_v52.sql
-- v51 -> v52 兼容迁移（战斗快照新增入战定格特质）：
--   战斗单位快照新增 Traits（入战定格特质 ID/等级），可缺省解码为 Empty——
--   发布瞬间处于战斗中的玩家本场按无定格特质继续，下一场入战自动按新装配定格；
--   非战斗玩家零影响。存档标签由 v41 推进至 v42；表结构未变，仅推进
--   platform.schema_version 与分段标签。
-- 适用对象：platform.schema_version 最新行为 51 的既有库；执行后启动门禁（要求 v52）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v52。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 42 WHERE schema_version <> 42;
UPDATE game.player_state_sections SET schema_version = 42 WHERE schema_version <> 42;
UPDATE platform.section_flush_queue SET schema_version = 42 WHERE schema_version <> 42;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (52, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
