-- 0007_v7_to_v8.sql
-- v7 → v8 兼容迁移（仅存档载荷标签推进，无表结构变更）：
--   存档载荷 v7 → v8：settings 分段新增 queuePaused（bool，队列暂停开关，缺省 false）；
--      PersistenceJson 反序列化对缺失属性按类型默认值处理，故 v7 载荷可被 v8 解码器
--      直接接受——本迁移只推进 schema_version 标签，payload/codec/raw_bytes/checksum
--      原样保留（schema_version 不参与 section/head 校验和计算）。
-- 适用对象：platform.schema_version 最新行为 7 的既有库；执行后启动门禁（要求 v8）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v8。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 8 WHERE schema_version = 7;
UPDATE game.player_state_sections SET schema_version = 8 WHERE schema_version = 7;
UPDATE platform.section_flush_queue SET schema_version = 8 WHERE schema_version = 7;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (8, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
