-- 0011_v10_to_v11.sql
-- v10 → v11 兼容迁移（表结构修复 + 存档 schema_version 标签推进）：
--   重建 identity.account_risk_events.event_kind 的 CHECK 约束，纳入 'handshake'。
--   背景：部分 v10 库在开发中间态建表，残留只含 ('register','login') 的旧约束，
--   WebSocket 握手风险观测写入 event_kind='handshake' 时触发 23514 违约。
--   存档载荷 v10 → v11：载荷字段无变化，按统一内容版本约定仅推进 schema_version
--   标签，payload/codec/raw_bytes/checksum 原样保留（schema_version 不参与校验和计算）。
-- 适用对象：platform.schema_version 最新行为 10 的既有库；执行后启动门禁（要求 v11）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v11。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 11 WHERE schema_version = 10;
UPDATE game.player_state_sections SET schema_version = 11 WHERE schema_version = 10;
UPDATE platform.section_flush_queue SET schema_version = 11 WHERE schema_version = 10;

ALTER TABLE identity.account_risk_events DROP CONSTRAINT IF EXISTS account_risk_events_event_kind_check;
ALTER TABLE identity.account_risk_events ADD CONSTRAINT account_risk_events_event_kind_check
    CHECK (event_kind IN ('register', 'login', 'handshake'));

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (11, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
