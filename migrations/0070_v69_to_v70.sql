-- 0070_v69_to_v70.sql
-- v69 -> v70：对弈（斗地主）剥离队伍耦合，升级为同道独立八阶全局匹配与十倍底注验资托管。
-- 玩家八分段载荷与标签保持 v49 不变。

BEGIN;

-- 若旧版 social.doudizhu_games 表仍含 party_id 列且存在未结算的活跃托管牌局，先无损退还已扣底注再重建表。
DO $$
DECLARE
    rec RECORD;
    pid uuid;
    refund_stake double precision;
    tx_id uuid;
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'social' AND table_name = 'doudizhu_games' AND column_name = 'party_id'
    ) THEN
        FOR rec IN
            SELECT game_id, state
            FROM social.doudizhu_games
            WHERE status = 0
              AND COALESCE((state->>'stakeEscrowed')::boolean, false)
              AND COALESCE((state->>'baseStake')::double precision, 0) > 0
        LOOP
            refund_stake := (rec.state->>'baseStake')::double precision;
            FOR pid IN
                SELECT (elem)::uuid
                FROM jsonb_array_elements_text(rec.state->'playerIds') AS elem
            LOOP
                INSERT INTO economy.player_asset_balances(player_id, asset_id, variant, quantity, updated_at)
                VALUES (pid, 'currency.spirit-stone', '', refund_stake, now())
                ON CONFLICT (player_id, asset_id, variant)
                DO UPDATE SET quantity = economy.player_asset_balances.quantity + EXCLUDED.quantity,
                              updated_at = EXCLUDED.updated_at;
            END LOOP;
        END LOOP;

        DROP TABLE social.doudizhu_games;
    END IF;
END $$;

ALTER TABLE social.parties
    DROP COLUMN IF EXISTS doudizhu_stake;

ALTER TABLE social.party_members
    DROP COLUMN IF EXISTS doudizhu_ready;

CREATE TABLE IF NOT EXISTS social.doudizhu_games (
    game_id uuid PRIMARY KEY,
    tier smallint NOT NULL CHECK (tier BETWEEN 1 AND 8),
    player_ids uuid[] NOT NULL CHECK (cardinality(player_ids) = 3),
    dismissed_by uuid[] NOT NULL DEFAULT '{}'::uuid[],
    state jsonb NOT NULL,
    state_version bigint NOT NULL DEFAULT 0 CHECK (state_version >= 0),
    status smallint NOT NULL CHECK (status BETWEEN 0 AND 2),
    updated_at timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_doudizhu_games_players
    ON social.doudizhu_games USING gin (player_ids) WHERE status IN (0, 1);
CREATE INDEX IF NOT EXISTS ix_doudizhu_games_active_updated
    ON social.doudizhu_games(updated_at) WHERE status = 0;
CREATE INDEX IF NOT EXISTS ix_doudizhu_games_finished_updated
    ON social.doudizhu_games(updated_at) WHERE status <> 0;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_bootstrap') THEN
        EXECUTE 'ALTER TABLE social.doudizhu_games OWNER TO daohua_bootstrap';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_api') THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON social.doudizhu_games TO daohua_api';
    END IF;
END $$;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (70, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
