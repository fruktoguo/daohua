-- 0051_v50_to_v51.sql
-- v50 -> v51：队伍斗地主底注、独立准备状态与权威牌局检查点。

BEGIN;

ALTER TABLE social.parties
    ADD COLUMN doudizhu_stake double precision NOT NULL DEFAULT 10
        CHECK (doudizhu_stake >= 0 AND doudizhu_stake < 'Infinity'::double precision);

ALTER TABLE social.party_members
    ADD COLUMN doudizhu_ready boolean NOT NULL DEFAULT false;

CREATE TABLE social.doudizhu_games (
    party_id uuid PRIMARY KEY,
    game_id uuid NOT NULL UNIQUE,
    state jsonb NOT NULL,
    state_version bigint NOT NULL DEFAULT 0 CHECK (state_version >= 0),
    status smallint NOT NULL CHECK (status BETWEEN 0 AND 2),
    updated_at timestamptz NOT NULL
);

ALTER TABLE social.doudizhu_games OWNER TO daohua_bootstrap;
GRANT SELECT, INSERT, UPDATE, DELETE ON social.doudizhu_games TO daohua_api;

INSERT INTO platform.schema_version(version, applied_at) VALUES (51, now());

COMMIT;
