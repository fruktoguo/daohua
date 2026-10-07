-- 0015_v14_to_v15.sql
-- v14 → v15 兼容迁移（仅存档 schema_version 标签推进）：
--   settings 分段载荷新增 tutorialRewardsStep 新手引导奖励游标（已发放至第几里程碑）。
--   新字段可缺省解码——旧存档缺失时按 0 恢复；v14 已发过阶段奖励的账号游标虽为 0，
--   但 tutorialStep 已到对应里程碑，推进判定 nextStep > 游标 时补发的语义与 v14 一致
--   （重学不回退已领奖励：skip/restart 均不重置游标）。
-- 适用对象：platform.schema_version 最新行为 14 的既有库；执行后启动门禁（要求 v15）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v15。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 15 WHERE schema_version = 14;
UPDATE game.player_state_sections SET schema_version = 15 WHERE schema_version = 14;
UPDATE platform.section_flush_queue SET schema_version = 15 WHERE schema_version = 14;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (15, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
