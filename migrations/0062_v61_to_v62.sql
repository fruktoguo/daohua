-- 0062_v61_to_v62.sql
-- v61 -> v62：兑换码系统旁路表（economy.redeem_batches / redeem_codes / player_redeem_claims）；玩家存档载荷标签保持 v44 不变。
BEGIN;

DO $$
DECLARE
    current_version integer := (SELECT max(version) FROM platform.schema_version);
BEGIN
    IF current_version IS DISTINCT FROM 61 AND current_version IS DISTINCT FROM 62 THEN
        RAISE EXCEPTION '0062 requires database schema v61 (or v62 on retry), found %', current_version;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS economy.redeem_batches (
    batch_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title varchar(120) NOT NULL,
    mode varchar(16) NOT NULL CHECK (mode IN ('universal', 'single_use')),
    rewards jsonb NOT NULL,
    max_claims integer NULL CHECK (max_claims IS NULL OR max_claims > 0),
    claimed_count integer NOT NULL DEFAULT 0 CHECK (claimed_count >= 0),
    created_at timestamptz NOT NULL,
    expires_at timestamptz NULL,
    disabled_at timestamptz NULL
) WITH (fillfactor = 90);

CREATE INDEX IF NOT EXISTS ix_redeem_batches_created_at
    ON economy.redeem_batches(created_at DESC, batch_id DESC);

CREATE TABLE IF NOT EXISTS economy.redeem_codes (
    code_normalized varchar(32) PRIMARY KEY CHECK (char_length(code_normalized) BETWEEN 4 AND 32),
    batch_id bigint NOT NULL REFERENCES economy.redeem_batches(batch_id) ON DELETE CASCADE,
    redeemed_by uuid NULL REFERENCES identity.players(player_id),
    redeemed_at timestamptz NULL
) WITH (fillfactor = 90);

CREATE INDEX IF NOT EXISTS ix_redeem_codes_batch
    ON economy.redeem_codes(batch_id, redeemed_at NULLS FIRST, code_normalized);

CREATE TABLE IF NOT EXISTS economy.player_redeem_claims (
    player_id uuid NOT NULL REFERENCES identity.players(player_id),
    batch_id bigint NOT NULL REFERENCES economy.redeem_batches(batch_id) ON DELETE CASCADE,
    code_normalized varchar(32) NOT NULL,
    claimed_at timestamptz NOT NULL,
    PRIMARY KEY (player_id, batch_id)
);

CREATE INDEX IF NOT EXISTS ix_player_redeem_claims_batch
    ON economy.player_redeem_claims(batch_id, claimed_at DESC);

DO $$
DECLARE
    economy_owner name := (
        SELECT pg_catalog.pg_get_userbyid(nspowner)
        FROM pg_catalog.pg_namespace
        WHERE nspname = 'economy'
    );
BEGIN
    IF economy_owner IS NOT NULL AND economy_owner <> current_user THEN
        EXECUTE pg_catalog.format('ALTER TABLE economy.redeem_batches OWNER TO %I', economy_owner);
        EXECUTE pg_catalog.format('ALTER TABLE economy.redeem_codes OWNER TO %I', economy_owner);
        EXECUTE pg_catalog.format('ALTER TABLE economy.player_redeem_claims OWNER TO %I', economy_owner);
    ELSIF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'daohua_bootstrap') THEN
        ALTER TABLE economy.redeem_batches OWNER TO daohua_bootstrap;
        ALTER TABLE economy.redeem_codes OWNER TO daohua_bootstrap;
        ALTER TABLE economy.player_redeem_claims OWNER TO daohua_bootstrap;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'daohua_api') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE
            ON economy.redeem_batches, economy.redeem_codes, economy.player_redeem_claims
            TO daohua_api;
        GRANT USAGE, SELECT ON SEQUENCE economy.redeem_batches_batch_id_seq TO daohua_api;
    END IF;
END $$;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (62, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
