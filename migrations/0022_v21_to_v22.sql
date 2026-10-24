-- 0022_v21_to_v22.sql
-- v21 → v22 兼容迁移（无存档字段语义变化）：
--   duel 分段行动账本载荷新增 combatants 战斗单位统计（物理/术法输出承伤减免、
--   自动回复/其他治疗；缺省解码为 null，与内存初始空簿等价）。
--   表结构未变；仅推进既有玩家行的 schema_version 标签，
--   payload/codec/raw_bytes/checksum 原样保留（schema_version 不参与 section/head 校验和计算）。
-- 适用对象：platform.schema_version 最新行为 21 的既有库；执行后启动门禁（要求 v22）即通过。
-- 全部语句幂等，可重复执行。本文件与 migrations/ 目录可随时删除，不影响编译与启动；
-- 空库由 PostgresSchema.Sql 直建 v22。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 22 WHERE schema_version = 21;
UPDATE game.player_state_sections SET schema_version = 22 WHERE schema_version = 21;
UPDATE platform.section_flush_queue SET schema_version = 22 WHERE schema_version = 21;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (22, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
