-- 0047_v46_to_v47.sql
-- v46 -> v47 兼容迁移：
--   identity.accounts 追加 taptap_openid 列与部分唯一索引（支持 TapTap OAuth 登录与一人一号防多开）。

BEGIN;

ALTER TABLE identity.accounts ADD COLUMN IF NOT EXISTS taptap_openid varchar(128) NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_accounts_taptap_openid ON identity.accounts(taptap_openid) WHERE taptap_openid IS NOT NULL;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (47, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
