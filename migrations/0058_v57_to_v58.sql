-- 0058_v57_to_v58.sql
-- v57 -> v58 兼容迁移：
--   1. 新增每日签到旁路状态表 game.player_daily_checkins（每角色至多 1 行，含周/月统计与 7 日签到位图）；
--   2. 为 daohua_bootstrap / daohua_api / daohua_worker 补齐 game.player_daily_checkins 属主与 DML 权限；
--   3. 玩家分段载荷结构与存档标签保持 v43 不变，禁止推进 schema_version 标签。
-- 适用对象：platform.schema_version 最新行为 57 的既有库；执行后启动门禁（要求 v58）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v58。

BEGIN;

CREATE TABLE IF NOT EXISTS game.player_daily_checkins (
    player_id uuid PRIMARY KEY REFERENCES identity.players(player_id) ON DELETE CASCADE,
    last_claim_day_index bigint NOT NULL,
    last_claim_date char(10) NOT NULL,
    streak_days integer NOT NULL CHECK (streak_days >= 1),
    total_days integer NOT NULL CHECK (total_days >= 1),
    last_reward_merit integer NOT NULL CHECK (last_reward_merit BETWEEN 10 AND 999),
    week_start_day_index bigint NOT NULL DEFAULT 0,
    week_days integer NOT NULL DEFAULT 0 CHECK (week_days >= 0),
    week_merit integer NOT NULL DEFAULT 0 CHECK (week_merit >= 0),
    week_days_mask smallint NOT NULL DEFAULT 0,
    month_key integer NOT NULL DEFAULT 0,
    month_days integer NOT NULL DEFAULT 0 CHECK (month_days >= 0),
    month_merit integer NOT NULL DEFAULT 0 CHECK (month_merit >= 0),
    last_claimed_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL
);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_bootstrap') THEN
        EXECUTE 'ALTER TABLE game.player_daily_checkins OWNER TO daohua_bootstrap';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_api') THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON game.player_daily_checkins TO daohua_api';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'daohua_worker') THEN
        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON game.player_daily_checkins TO daohua_worker';
    END IF;
END $$;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (58, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
