-- 0056_v55_to_v56.sql
-- v55 -> v56 兼容迁移：
--   1. 新增双轨邮件系统三张表与索引（social.broadcast_mails / social.player_mail_states / social.player_mails），
--      其中 expires_at 允许为 NULL 以支持「永不过期」邮件；
--   2. 为 daohua_bootstrap / daohua_api / daohua_worker 补齐表属主、DML 与 social 序列权限；
--   3. 玩家分段载荷结构与存档标签保持 v43 不变。
-- 适用对象：platform.schema_version 最新行为 55 的既有库；执行后启动门禁（要求 v56）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v56。

BEGIN;

CREATE TABLE IF NOT EXISTS social.broadcast_mails (
    broadcast_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title text NOT NULL,
    body text NOT NULL,
    sender_name text NOT NULL,
    attachments jsonb NOT NULL DEFAULT '[]'::jsonb,
    only_existing_players boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NULL,
    revoked_at timestamptz NULL
);
ALTER TABLE social.broadcast_mails
    ALTER COLUMN expires_at DROP NOT NULL;

CREATE INDEX IF NOT EXISTS ix_broadcast_mails_active
    ON social.broadcast_mails(expires_at, created_at DESC)
    WHERE revoked_at IS NULL;

CREATE TABLE IF NOT EXISTS social.player_mail_states (
    player_id uuid PRIMARY KEY,
    read_ids bigint[] NOT NULL DEFAULT '{}'::bigint[],
    claimed_ids bigint[] NOT NULL DEFAULT '{}'::bigint[],
    deleted_ids bigint[] NOT NULL DEFAULT '{}'::bigint[],
    updated_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS social.player_mails (
    mail_id uuid PRIMARY KEY,
    player_id uuid NOT NULL,
    title text NOT NULL,
    body text NOT NULL,
    sender_name text NOT NULL,
    attachments jsonb NOT NULL DEFAULT '[]'::jsonb,
    has_attachments boolean NOT NULL DEFAULT false,
    is_read boolean NOT NULL DEFAULT false,
    is_claimed boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NULL
);
ALTER TABLE social.player_mails
    ALTER COLUMN expires_at DROP NOT NULL;

CREATE INDEX IF NOT EXISTS ix_player_mails_player_created
    ON social.player_mails(player_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_player_mails_expires
    ON social.player_mails(expires_at);

ALTER TABLE social.broadcast_mails OWNER TO daohua_bootstrap;
ALTER TABLE social.player_mail_states OWNER TO daohua_bootstrap;
ALTER TABLE social.player_mails OWNER TO daohua_bootstrap;

GRANT SELECT, INSERT, UPDATE, DELETE ON
    social.broadcast_mails,
    social.player_mail_states,
    social.player_mails
    TO daohua_api, daohua_worker;

GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA social
    TO daohua_api, daohua_worker;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (56, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
