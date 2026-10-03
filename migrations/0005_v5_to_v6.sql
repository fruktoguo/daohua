-- 0005_v5_to_v6.sql
-- v5 → v6 兼容迁移（仅存档载荷标签推进，无表结构变更）：
--   存档载荷 v5 → v6：loadout 分段新增 techniqueExperiences（Record<string,double>，缺省空）、
--      ActionIntent 新增 targetItemId/auxItemId（string?，缺省 null）、
--      LoadoutDocument 新增 TechniqueExperiences；PersistenceJson 反序列化
--      对缺失属性按类型默认值处理，故 v5 载荷可被 v6 解码器直接接受——本迁移只推进
--      schema_version 标签，payload/codec/raw_bytes/checksum 原样保留
--      （schema_version 不参与 section/head 校验和计算）。
-- 适用对象：platform.schema_version 最新行为 5 的既有库；执行后启动门禁（要求 v6）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v6。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 6 WHERE schema_version = 5;
UPDATE game.player_state_sections SET schema_version = 6 WHERE schema_version = 5;
UPDATE platform.section_flush_queue SET schema_version = 6 WHERE schema_version = 5;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (6, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
