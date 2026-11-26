-- 0066_v65_to_v66.sql
-- v65 -> v66：
--   1. 道友攻略板块与角色名片配装付费查阅表结构（social.guide_posts / guide_daily_quotas / guide_post_views /
--      guide_post_likes / guide_post_unlocks / guide_post_tips / loadout_inspect_grants）及 daohua_api 角色授权；
--   2. 玩家八分段 settings 新增可选字段 loadoutInspectPrice（缺省解码为 0），推进三处存档载荷标签 v45 -> v46。
BEGIN;

DO $$
DECLARE
    current_version integer := (SELECT max(version) FROM platform.schema_version);
BEGIN
    IF current_version IS DISTINCT FROM 65 AND current_version IS DISTINCT FROM 66 THEN
        RAISE EXCEPTION '0066 requires database schema v65 (or v66 on retry), found %', current_version;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS social.guide_posts (
    post_id uuid PRIMARY KEY,
    author_id uuid NOT NULL REFERENCES identity.players(player_id),
    title varchar(120) NOT NULL CHECK (char_length(btrim(title)) > 0),
    summary varchar(400) NOT NULL DEFAULT '',
    body text NOT NULL CHECK (char_length(btrim(body)) > 0),
    unlock_price double precision NOT NULL DEFAULT 0 CHECK (unlock_price >= 0 AND unlock_price <= 100000),
    view_count integer NOT NULL DEFAULT 0 CHECK (view_count >= 0),
    like_count integer NOT NULL DEFAULT 0 CHECK (like_count >= 0),
    tipper_count integer NOT NULL DEFAULT 0 CHECK (tipper_count >= 0),
    total_reward_stones double precision NOT NULL DEFAULT 0 CHECK (total_reward_stones >= 0),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL
) WITH (fillfactor = 85);

CREATE INDEX IF NOT EXISTS ix_guide_posts_active_reward
    ON social.guide_posts(total_reward_stones DESC, tipper_count DESC, created_at DESC, post_id DESC)
    WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_guide_posts_active_created
    ON social.guide_posts(created_at DESC, post_id DESC)
    WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_guide_posts_author
    ON social.guide_posts(author_id, created_at DESC)
    WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_guide_posts_deleted
    ON social.guide_posts(deleted_at)
    WHERE deleted_at IS NOT NULL;

CREATE TABLE IF NOT EXISTS social.guide_daily_quotas (
    player_id uuid NOT NULL REFERENCES identity.players(player_id),
    post_date date NOT NULL,
    post_count integer NOT NULL DEFAULT 0 CHECK (post_count >= 0),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (player_id, post_date)
);
CREATE INDEX IF NOT EXISTS ix_guide_daily_quotas_updated
    ON social.guide_daily_quotas(updated_at);

CREATE TABLE IF NOT EXISTS social.guide_post_views (
    post_id uuid NOT NULL REFERENCES social.guide_posts(post_id) ON DELETE CASCADE,
    player_id uuid NOT NULL REFERENCES identity.players(player_id),
    viewed_at timestamptz NOT NULL,
    PRIMARY KEY (post_id, player_id)
);

CREATE TABLE IF NOT EXISTS social.guide_post_likes (
    post_id uuid NOT NULL REFERENCES social.guide_posts(post_id) ON DELETE CASCADE,
    player_id uuid NOT NULL REFERENCES identity.players(player_id),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (post_id, player_id)
);

CREATE TABLE IF NOT EXISTS social.guide_post_unlocks (
    post_id uuid NOT NULL REFERENCES social.guide_posts(post_id) ON DELETE CASCADE,
    viewer_id uuid NOT NULL REFERENCES identity.players(player_id),
    paid_stones double precision NOT NULL CHECK (paid_stones > 0),
    author_received_stones double precision NOT NULL CHECK (author_received_stones >= 0),
    unlocked_at timestamptz NOT NULL,
    PRIMARY KEY (post_id, viewer_id)
);

CREATE TABLE IF NOT EXISTS social.guide_post_tips (
    post_id uuid NOT NULL REFERENCES social.guide_posts(post_id) ON DELETE CASCADE,
    tipper_id uuid NOT NULL REFERENCES identity.players(player_id),
    total_tipped_stones double precision NOT NULL CHECK (total_tipped_stones > 0),
    tip_count integer NOT NULL DEFAULT 1 CHECK (tip_count > 0),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (post_id, tipper_id)
);

CREATE TABLE IF NOT EXISTS social.loadout_inspect_grants (
    viewer_id uuid NOT NULL REFERENCES identity.players(player_id),
    target_id uuid NOT NULL REFERENCES identity.players(player_id),
    access_date date NOT NULL,
    paid_stones double precision NOT NULL CHECK (paid_stones > 0),
    target_received_stones double precision NOT NULL CHECK (target_received_stones >= 0),
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (viewer_id, target_id, access_date)
);
CREATE INDEX IF NOT EXISTS ix_loadout_inspect_grants_expires
    ON social.loadout_inspect_grants(expires_at);

DO $$
DECLARE
    social_owner name := (
        SELECT pg_catalog.pg_get_userbyid(nspowner)
        FROM pg_catalog.pg_namespace
        WHERE nspname = 'social'
    );
    tbl text;
BEGIN
    FOREACH tbl IN ARRAY ARRAY[
        'guide_posts', 'guide_daily_quotas', 'guide_post_views', 'guide_post_likes',
        'guide_post_unlocks', 'guide_post_tips', 'loadout_inspect_grants'
    ] LOOP
        IF social_owner IS NOT NULL AND social_owner <> current_user THEN
            EXECUTE pg_catalog.format('ALTER TABLE social.%I OWNER TO %I', tbl, social_owner);
        ELSIF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'daohua_bootstrap') THEN
            EXECUTE pg_catalog.format('ALTER TABLE social.%I OWNER TO daohua_bootstrap', tbl);
        END IF;
    END LOOP;

    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'daohua_api') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON
            social.guide_posts,
            social.guide_daily_quotas,
            social.guide_post_views,
            social.guide_post_likes,
            social.guide_post_unlocks,
            social.guide_post_tips,
            social.loadout_inspect_grants
            TO daohua_api;
    END IF;
END $$;

UPDATE game.player_state_heads SET schema_version = 46 WHERE schema_version = 45;
UPDATE game.player_state_sections SET schema_version = 46 WHERE schema_version = 45;
UPDATE platform.section_flush_queue SET schema_version = 46 WHERE schema_version = 45;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM game.player_state_heads WHERE schema_version <> 46)
        OR EXISTS (SELECT 1 FROM game.player_state_sections WHERE schema_version <> 46)
        OR EXISTS (SELECT 1 FROM platform.section_flush_queue WHERE schema_version <> 46) THEN
        RAISE EXCEPTION '0066 found unexpected player save schema labels; inspect before retrying';
    END IF;
END $$;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (66, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
