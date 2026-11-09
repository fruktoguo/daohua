-- 0050_v49_to_v50.sql
-- v49 -> v50 兼容迁移：
--   1. social.chat_messages 增 sender_realm 发送者境界列（默认 1：凡俗）；
--   2. social.parties 修改 open_slots 默认开启槽位为 3（总纲 §21.13，三人组队标准规模）。

BEGIN;

ALTER TABLE social.chat_messages ADD COLUMN IF NOT EXISTS sender_realm integer NOT NULL DEFAULT 1;

ALTER TABLE social.parties ALTER COLUMN open_slots SET DEFAULT 3;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (50, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
