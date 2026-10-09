-- 0023_v22_to_v23.sql
-- v22 → v23 兼容迁移（无存档字段语义变化）：
--   actionQueue 分段装配展示载荷落地——行动参与成员快照（participation.spec.members[]）
--   新增 equipment 开打定格装备、斗法回放状态（expedition.duel）新增
--   opponentSkills/opponentEquipment 镜像装配；缺省解码为 null，仅详情弹窗
--   「装配」Tab 按需投影消费，不改写结算语义。
--   表结构未变；仅推进既有玩家行的 schema_version 标签，
--   payload/codec/raw_bytes/checksum 原样保留（schema_version 不参与 section/head 校验和计算）。
-- 适用对象：platform.schema_version 最新行为 22 的既有库；执行后启动门禁（要求 v23）即通过。
-- 全部语句幂等，可重复执行。本文件与 migrations/ 目录可随时删除，不影响编译与启动；
-- 空库由 PostgresSchema.Sql 直建 v23。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 23 WHERE schema_version = 22;
UPDATE game.player_state_sections SET schema_version = 23 WHERE schema_version = 22;
UPDATE platform.section_flush_queue SET schema_version = 23 WHERE schema_version = 22;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (23, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
