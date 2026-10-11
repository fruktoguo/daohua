-- v26 → v27 兼容迁移：section_flush_queue 由 (player_id, section_kind) upsert 共享行
-- 改为追加式收件箱（queue_id 自增主键）。upsert 共享行被资产事务与 flush worker 的
-- 并发 upsert/lease/delete 反复争夺，在 Serializable 下持续产生 40001 与同元组死锁；
-- 纯 INSERT 入队 + 按 queue_id 租约认领后，写入方与清理方不再接触同一元组。
-- 现有挂起行的 (player_id, section_kind) 各不相同，分配 queue_id 后语义等价。
-- 存档载荷未变，仅推进版本标签。
-- 全部语句幂等，可重复执行。本文件与 migrations/ 目录可随时删除，不影响编译与启动；
-- 空库由 PostgresSchema.Sql 直建 v27。

BEGIN;

ALTER TABLE platform.section_flush_queue
    DROP CONSTRAINT IF EXISTS section_flush_queue_pkey;

ALTER TABLE platform.section_flush_queue
    ADD COLUMN IF NOT EXISTS queue_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY;

CREATE INDEX IF NOT EXISTS ix_section_flush_player
    ON platform.section_flush_queue(player_id, section_kind, state_version DESC);

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (27, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
