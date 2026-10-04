-- 0008_v8_to_v9.sql
-- v8 → v9 兼容迁移（仅存档载荷标签推进，无表结构变更）：
--   存档载荷 v8 → v9：progression 分段新增 traitIds（特质 ID 集合，缺省 null）；
--      PersistenceJson 反序列化对缺失属性按类型默认值处理，故 v8 载荷可被 v9 解码器
--      直接接受——本迁移只推进 schema_version 标签，payload/codec/raw_bytes/checksum
--      原样保留（schema_version 不参与 section/head 校验和计算）。
-- 适用对象：platform.schema_version 最新行为 8 的既有库；执行后启动门禁（要求 v9）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v9。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 9 WHERE schema_version = 8;
UPDATE game.player_state_sections SET schema_version = 9 WHERE schema_version = 8;
UPDATE platform.section_flush_queue SET schema_version = 9 WHERE schema_version = 8;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (9, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
