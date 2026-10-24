-- 0004_v4_to_v5.sql
-- v4 → v5 兼容迁移（表结构 + 存档载荷标签一并推进）：
--   1. identity.players 增加 player_seq 自增序号列，作为设置页「角色账号#序号」
--      等场景的面向玩家编号；存量行由 identity 序列自动回填；
--   2. 存档载荷 v4 → v5：settings 分段新增 autoTravelEnabled（bool，缺省 false）、
--      ActionIntent 新增 anchorNodeId（string?，缺省 null），PersistenceJson 反序列化
--      对缺失属性按类型默认值处理，故 v4 载荷可被 v5 解码器直接接受——本迁移只推进
--      schema_version 标签，payload/codec/raw_bytes/checksum 原样保留
--      （schema_version 不参与 section/head 校验和计算）。
-- 适用对象：platform.schema_version 最新行为 4 的既有库；执行后启动门禁（要求 v5）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v5。

BEGIN;

ALTER TABLE identity.players
    ADD COLUMN IF NOT EXISTS player_seq bigint GENERATED ALWAYS AS IDENTITY;

UPDATE game.player_state_heads SET schema_version = 5 WHERE schema_version = 4;
UPDATE game.player_state_sections SET schema_version = 5 WHERE schema_version = 4;
UPDATE platform.section_flush_queue SET schema_version = 5 WHERE schema_version = 4;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (5, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
