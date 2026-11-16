-- 0059_v58_to_v59.sql
-- v58 -> v59 兼容迁移：
--   1. 新增传音自定义表情包多端同步旁路表 social.player_stickers（含活跃与墓碑清理索引）；
--   2. 为 daohua_bootstrap / daohua_api / daohua_worker 补齐 social.player_stickers 属主与 DML 权限；
--   3. 玩家分段载荷结构与存档标签保持 v43 不变，禁止推进 schema_version 标签。
-- 适用对象：platform.schema_version 最新行为 58 的既有库；执行后启动门禁（要求 v59）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v59。

BEGIN;

CREATE TABLE IF NOT EXISTS social.player_stickers (
    player_id uuid NOT NULL REFERENCES identity.players(player_id) ON DELETE CASCADE,
    sticker_id varchar(64) NOT NULL,
    url varchar(200) NOT NULL CHECK (char_length(url) > 0),
    sha256 char(64) NULL,
    mime varchar(32) NOT NULL DEFAULT '',
    use_count integer NOT NULL DEFAULT 0 CHECK (use_count >= 0),
    last_used_at bigint NOT NULL DEFAULT 0 CHECK (last_used_at >= 0),
    added_at bigint NOT NULL CHECK (added_at >= 0),
    updated_at bigint NOT NULL CHECK (updated_at >= 0),
    needs_reupload boolean NOT NULL DEFAULT false,
    deleted_at bigint NULL CHECK (deleted_at IS NULL OR deleted_at >= 0),
    PRIMARY KEY (player_id, sticker_id)
);

CREATE INDEX IF NOT EXISTS ix_player_stickers_player_active
    ON social.player_stickers(player_id, use_count DESC, last_used_at DESC, added_at DESC)
    WHERE deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_player_stickers_deleted_cleanup
    ON social.player_stickers(deleted_at)
    WHERE deleted_at IS NOT NULL;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_bootstrap') THEN
        EXECUTE 'ALTER TABLE social.player_stickers OWNER TO daohua_bootstrap';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_api') THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON social.player_stickers TO daohua_api';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_worker') THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON social.player_stickers TO daohua_worker';
    END IF;
END $$;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (59, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
