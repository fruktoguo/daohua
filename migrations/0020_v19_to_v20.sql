-- 0020_v19_to_v20.sql
-- v19 → v20 兼容迁移（无存档表结构变更，仅内容标签推进）：
--   actionQueue 分段组队讨伐载荷落地（总纲 §21.13）：
--   1. 行动项新增 partySession 会话绑定（PartyExpeditionSpec：成员冻结面板/战外 Buff/持续消耗品定格）；
--   2. 远征状态新增 selfIndex/allies 盟友镜像；
--   3. 战斗单位快照新增 respawnAtSeconds 倒地截止点；
--   4. 盟友镜像携带跨战技能态 CombatSkills。
--   全部字段可缺省解码，既有存档零改写；仅推进内容版本标签。
-- 适用对象：platform.schema_version 最新行为 19 的既有库；执行后启动门禁（要求 v20）即通过。
-- 全部语句幂等，可重复执行。本文件与 migrations/ 目录可随时删除，不影响编译与启动；
-- 空库由 PostgresSchema.Sql 直建 v20。

BEGIN;

-- 存档载荷标签推进：v20 全部新增字段可缺省解码，既有载荷零改写，仅把行级
-- schema_version 推至 20（先例 0017/0018 同款；v19 未推过行标签故用 < 20 兜底）。
UPDATE game.player_state_heads SET schema_version = 20 WHERE schema_version < 20;
UPDATE game.player_state_sections SET schema_version = 20 WHERE schema_version < 20;
UPDATE platform.section_flush_queue SET schema_version = 20 WHERE schema_version < 20;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (20, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
