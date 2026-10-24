-- 0018_v17_to_v18.sql
-- v17 → v18 兼容迁移（仅存档 schema_version 标签推进）：
--   战斗属性收敛为纯点数通道：action_queue / duel 载荷内嵌的 CombatantStats 与
--   组队会话规格移除六个独立成长乘区字段（castTimeFactor / cooldownFactor /
--   basicAttackRatio / skillIntensity / criticalMultiplier / searchDelayFactor），
--   等级成长折算为点数注入行动速度/攻击速度/冷却缩减/普攻增幅/技能增幅通道，
--   暴击伤害改为点数 criticalDamagePoints（基础 50 点 = 150% 暴伤），
--   组队规格 searchDelayFactor 更名为 searchSpeedPoints。
--   旧载荷的已删字段按未知属性忽略；新增/更名字段缺省解码为类型默认值——
--   criticalDamagePoints 缺省 50（即基础暴伤初值，在飞战斗快照随境界成长复算），
--   searchSpeedPoints 缺省 0（无加速口径），无载荷改写。
-- 适用对象：platform.schema_version 最新行为 17 的既有库；执行后启动门禁（要求 v18）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v18。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 18 WHERE schema_version = 17;
UPDATE game.player_state_sections SET schema_version = 18 WHERE schema_version = 17;
UPDATE platform.section_flush_queue SET schema_version = 18 WHERE schema_version = 17;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (18, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
