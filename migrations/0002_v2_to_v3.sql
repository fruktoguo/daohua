-- 0002_v2_to_v3.sql
-- v2 → v3 兼容迁移：platform.compute_jobs 补齐派发链所需列（lease_token/next_attempt_at/last_error），
-- 并为同一玩家的未完结任务加部分唯一索引（单开任务约束）。
-- 适用对象：platform.schema_version 最新行为 2 的既有库；执行后启动门禁（要求 v3）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v3。

BEGIN;

ALTER TABLE platform.compute_jobs
    ADD COLUMN IF NOT EXISTS lease_token uuid NULL,
    ADD COLUMN IF NOT EXISTS next_attempt_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS last_error varchar(512) NULL;

-- 同一玩家最多一个未完结冷挂机任务；终态行不参与唯一约束，可保留诊断窗口。
CREATE UNIQUE INDEX IF NOT EXISTS ux_compute_jobs_open
    ON platform.compute_jobs(player_id) WHERE status IN ('pending', 'leased');

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (3, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
