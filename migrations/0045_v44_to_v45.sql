-- 0045_v44_to_v45.sql
-- v44 -> v45 兼容迁移（Buff 效果载荷收敛为通道模型，总纲 §7.9.5.3）：
--   buffs/combat/action_queue 分段内的 CombatBuffState/CombatBuffCarry/
--   ActionSustainedBuff/ActiveBuff 平铺效果字段替换为 Effects 通道数组。
--   旧平铺载荷在 Brotli 压缩 section 内无法由 SQL 无损重写，属不可缺省解码变更——
--   本迁移只推进 platform.schema_version 与分段标签；仍携带旧载荷的玩家存档由
--   reset-outdated-players.sh 启动链经控制台交互确认后清零玩家游戏域数据（保留账号/角色名）。
-- 适用对象：platform.schema_version 最新行为 44 的既有库；执行后启动门禁（要求 v45）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v45。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 40 WHERE schema_version <> 40;
UPDATE game.player_state_sections SET schema_version = 40 WHERE schema_version <> 40;
UPDATE platform.section_flush_queue SET schema_version = 40 WHERE schema_version <> 40;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (45, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
