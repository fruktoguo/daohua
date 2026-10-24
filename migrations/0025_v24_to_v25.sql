-- v24 → v25 兼容迁移：economy.transactions 增 created_at 时间窗索引。
-- 资产事务流水持续增长至千万级，运维/监控按时间窗统计只能全表扫描；
-- 新增 (created_at, transaction_type) 索引支撑时间窗增量计数与类型过滤。
-- 存档载荷未变，仅推进版本标签。
-- 全部语句幂等，可重复执行。本文件与 migrations/ 目录可随时删除，不影响编译与启动；
-- 空库由 PostgresSchema.Sql 直建 v25。

BEGIN;

CREATE INDEX IF NOT EXISTS ix_transactions_created_at
    ON economy.transactions(created_at, transaction_type);

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (25, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
