-- 0042_v41_to_v42.sql
-- v41 -> v42 挂机推演流水截断与租约索引 HOT 优化：
--   1. 放宽 party_session_inbox 的 consumed_transaction_id 强绑定与外键约束：
--      高频组队挂机推演不再强制向不可变金融账本（economy.transactions）落空流水，
--      consumed_transaction_id 允许为 NULL（此时由 consumed_at IS NOT NULL 证明消费）；
--      外键补充 ON DELETE SET NULL；
--      解除历史结算 inbox 绑定的 transaction_id，释放被死锁无法清理的 40+ 万历史积压；
--   2. platform.section_flush_queue 重建 ix_section_flush_ready 索引为单列 (next_attempt_at)，
--      剔除 lease_until 列，使租约认领更新可以走 HOT（Heap-Only Tuple）原地更新，消除整页 FPI 入 WAL。
--   存档载荷与标签未变（仍为 v38）。
-- 适用对象：platform.schema_version 最新行为 41 的既有库；执行后启动门禁（要求 v42）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v42。

BEGIN;

-- 1. 放宽 party_session_inbox 约束并解绑历史引用
ALTER TABLE social.party_session_inbox
    DROP CONSTRAINT IF EXISTS ck_party_session_inbox_consumed,
    DROP CONSTRAINT IF EXISTS party_session_inbox_check;

ALTER TABLE social.party_session_inbox
    ADD CONSTRAINT ck_party_session_inbox_consumed
    CHECK (consumed_at IS NOT NULL OR consumed_transaction_id IS NULL);

ALTER TABLE social.party_session_inbox
    DROP CONSTRAINT IF EXISTS party_session_inbox_consumed_transaction_id_fkey;

ALTER TABLE social.party_session_inbox
    ADD CONSTRAINT party_session_inbox_consumed_transaction_id_fkey
    FOREIGN KEY (consumed_transaction_id)
    REFERENCES economy.transactions(transaction_id)
    ON DELETE SET NULL;

UPDATE social.party_session_inbox
SET consumed_transaction_id = NULL
WHERE consumed_at IS NOT NULL
  AND consumed_transaction_id IN (
      SELECT transaction_id FROM economy.transactions
      WHERE transaction_type IN ('simulation.reward', 'participation.settlement')
  );

-- 2. 优化 section_flush_queue 租约索引恢复 HOT
DROP INDEX IF EXISTS platform.ix_section_flush_ready;
CREATE INDEX IF NOT EXISTS ix_section_flush_ready ON platform.section_flush_queue(next_attempt_at);

-- 3. 推进版本
INSERT INTO platform.schema_version(version, applied_at)
    VALUES (42, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
