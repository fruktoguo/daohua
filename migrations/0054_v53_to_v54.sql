-- 0054_v53_to_v54.sql
-- v53 -> v54 兼容迁移：
--   1. game.player_projection 新增排行榜所需的 experience/lifetime_experience/duel_wins/duel_losses/avatar 五列；
--   2. 新增全服排行榜整点快照派生读模型表 game.leaderboard_snapshots 与 game.leaderboard_ranks；
--   3. 对未压缩（codec='json'）存量存档行顺带回填新增投影列（Brotli 行由服务端首次物化时补齐）；
--   4. 玩家载荷标签由 42 推进至 43（新增 progression.duelWins/duelLosses 与 settings.publicLoadoutVisible 均可缺省解码）；
--   5. 授予 daohua_api 对两张排行榜派生表的读写权限并置表归属为 daohua_bootstrap。
-- 适用对象：platform.schema_version 最新行为 53 的既有库；执行后启动门禁（要求 v54）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v54。

BEGIN;

ALTER TABLE game.player_projection
    ADD COLUMN IF NOT EXISTS experience double precision NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS lifetime_experience double precision NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS duel_wins double precision NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS duel_losses double precision NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS avatar varchar(64) NOT NULL DEFAULT 'player';

UPDATE game.player_projection AS p
SET experience = COALESCE((convert_from(s.payload, 'UTF8')::jsonb ->> 'experience')::double precision, p.experience),
    lifetime_experience = COALESCE((convert_from(s.payload, 'UTF8')::jsonb ->> 'lifetimeExperience')::double precision, p.lifetime_experience),
    duel_wins = COALESCE((convert_from(s.payload, 'UTF8')::jsonb ->> 'duelWins')::double precision, p.duel_wins),
    duel_losses = COALESCE((convert_from(s.payload, 'UTF8')::jsonb ->> 'duelLosses')::double precision, p.duel_losses)
FROM game.player_state_sections AS s
WHERE s.player_id = p.player_id
  AND s.section_kind = 'progression'
  AND s.codec = 'json';

UPDATE game.player_projection AS p
SET avatar = LEFT(convert_from(s.payload, 'UTF8')::jsonb ->> 'avatar', 64)
FROM game.player_state_sections AS s
WHERE s.player_id = p.player_id
  AND s.section_kind = 'settings'
  AND s.codec = 'json'
  AND NULLIF(convert_from(s.payload, 'UTF8')::jsonb ->> 'avatar', '') IS NOT NULL;

CREATE TABLE IF NOT EXISTS game.leaderboard_snapshots (
    board_id varchar(64) PRIMARY KEY,
    computed_at timestamptz NOT NULL,
    participant_count integer NOT NULL CHECK (participant_count >= 0),
    payload jsonb NOT NULL
);

CREATE TABLE IF NOT EXISTS game.leaderboard_ranks (
    player_id uuid NOT NULL REFERENCES identity.players(player_id),
    board_id varchar(64) NOT NULL,
    rank integer NOT NULL CHECK (rank > 0),
    score double precision NOT NULL CHECK (score > 0),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (player_id, board_id)
);
CREATE INDEX IF NOT EXISTS ix_leaderboard_ranks_board ON game.leaderboard_ranks(board_id, rank);

ALTER TABLE game.leaderboard_snapshots OWNER TO daohua_bootstrap;
ALTER TABLE game.leaderboard_ranks OWNER TO daohua_bootstrap;

GRANT SELECT, INSERT, UPDATE, DELETE ON
    game.leaderboard_snapshots,
    game.leaderboard_ranks
    TO daohua_api;

UPDATE game.player_state_heads SET schema_version = 43 WHERE schema_version <> 43;
UPDATE game.player_state_sections SET schema_version = 43 WHERE schema_version <> 43;
UPDATE platform.section_flush_queue SET schema_version = 43 WHERE schema_version <> 43;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (54, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
