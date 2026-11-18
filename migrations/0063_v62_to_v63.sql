-- v62 -> v63：传音引用快照、点赞状态及版本；玩家分段载荷仍为 v44。
BEGIN;

DO $$
DECLARE
    current_version integer := (SELECT max(version) FROM platform.schema_version);
BEGIN
    IF current_version IS DISTINCT FROM 62 AND current_version IS DISTINCT FROM 63 THEN
        RAISE EXCEPTION '0063 需要库 v62 或重试时的 v63，当前版本：%', current_version;
    END IF;
END $$;

ALTER TABLE social.chat_messages
    ADD COLUMN IF NOT EXISTS quote_message_id uuid,
    ADD COLUMN IF NOT EXISTS quote_sender_name varchar(64),
    ADD COLUMN IF NOT EXISTS quote_body varchar(200),
    ADD COLUMN IF NOT EXISTS like_version bigint NOT NULL DEFAULT 0
        CONSTRAINT chat_messages_like_version_check CHECK (like_version >= 0);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT FROM pg_catalog.pg_constraint
        WHERE conrelid = 'social.chat_messages'::regclass AND conname = 'chat_messages_quote_snapshot_check'
    ) THEN
        ALTER TABLE social.chat_messages ADD CONSTRAINT chat_messages_quote_snapshot_check CHECK (
            (quote_message_id IS NULL) = (quote_sender_name IS NULL) AND
            (quote_message_id IS NULL) = (quote_body IS NULL)
        );
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS social.chat_message_likes (
    message_id uuid NOT NULL REFERENCES social.chat_messages(message_id) ON DELETE CASCADE,
    player_id uuid NOT NULL REFERENCES identity.players(player_id),
    PRIMARY KEY (message_id, player_id)
);

DO $$
DECLARE
    social_owner name := (
        SELECT pg_catalog.pg_get_userbyid(nspowner)
        FROM pg_catalog.pg_namespace WHERE nspname = 'social'
    );
BEGIN
    IF social_owner <> current_user THEN
        EXECUTE pg_catalog.format('ALTER TABLE social.chat_message_likes OWNER TO %I', social_owner);
    ELSIF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'daohua_bootstrap') THEN
        ALTER TABLE social.chat_message_likes OWNER TO daohua_bootstrap;
    END IF;
    IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'daohua_api') THEN
        GRANT SELECT, INSERT, DELETE ON social.chat_message_likes TO daohua_api;
    END IF;
END $$;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (63, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
