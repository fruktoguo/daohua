-- 0010_v9_to_v10.sql
-- v9 → v10 兼容迁移（表结构新增 + 存档载荷标签推进）：
--   新增 identity.account_risk_signals / identity.account_risk_events 账号注册风控表——
--   只保存服务端 HMAC 摘要，不保存原始设备指纹、IP 或设备 ID。
--   存档载荷 v9 → v10：载荷字段无变化，按统一内容版本约定仅推进 schema_version
--   标签，payload/codec/raw_bytes/checksum 原样保留（schema_version 不参与校验和计算）。
-- 适用对象：platform.schema_version 最新行为 9 的既有库；执行后启动门禁（要求 v10）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v10。

BEGIN;

UPDATE game.player_state_heads SET schema_version = 10 WHERE schema_version = 9;
UPDATE game.player_state_sections SET schema_version = 10 WHERE schema_version = 9;
UPDATE platform.section_flush_queue SET schema_version = 10 WHERE schema_version = 9;

CREATE TABLE IF NOT EXISTS identity.account_risk_signals (
    signal_id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES identity.accounts(account_id) ON DELETE CASCADE,
    signal_kind varchar(32) NOT NULL,
    signal_hash char(64) NOT NULL,
    first_seen_at timestamptz NOT NULL,
    last_seen_at timestamptz NOT NULL,
    hit_count bigint NOT NULL DEFAULT 1 CHECK (hit_count > 0),
    UNIQUE (account_id, signal_kind, signal_hash)
);
CREATE INDEX IF NOT EXISTS ix_account_risk_signal_lookup
    ON identity.account_risk_signals(signal_kind, signal_hash, last_seen_at DESC);
CREATE INDEX IF NOT EXISTS ix_account_risk_signal_account
    ON identity.account_risk_signals(account_id, last_seen_at DESC);

CREATE TABLE IF NOT EXISTS identity.account_risk_events (
    event_id uuid PRIMARY KEY,
    account_id uuid NULL REFERENCES identity.accounts(account_id) ON DELETE SET NULL,
    decision varchar(16) NOT NULL CHECK (decision IN ('allow', 'review', 'deny')),
    error_code varchar(64) NULL,
    signals jsonb NOT NULL DEFAULT '[]'::jsonb,
    risk_score integer NOT NULL DEFAULT 0 CHECK (risk_score >= 0 AND risk_score <= 100),
    reasons jsonb NOT NULL DEFAULT '[]'::jsonb,
    event_kind varchar(16) NOT NULL DEFAULT 'register' CHECK (event_kind IN ('register', 'login', 'handshake')),
    occurred_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_account_risk_events_occurred
    ON identity.account_risk_events(occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_account_risk_events_account
    ON identity.account_risk_events(account_id, occurred_at DESC);

CREATE TABLE IF NOT EXISTS platform.account_risk_policy (
    policy_id smallint PRIMARY KEY CHECK (policy_id = 1),
    payload jsonb NOT NULL,
    updated_at timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS platform.account_risk_key (
    key_id smallint PRIMARY KEY CHECK (key_id = 1),
    key_bytes bytea NOT NULL CHECK (octet_length(key_bytes) = 32)
);

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (10, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
