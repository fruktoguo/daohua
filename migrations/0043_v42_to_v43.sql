-- 0043_v42_to_v43.sql
-- v42 -> v43 transaction_lines 外键补齐 ON DELETE CASCADE 并清理 2 小时前短期流水与 14 天前长期流水：
--   1. transaction_lines 关联 transactions 外键补充 ON DELETE CASCADE，支持级联删除明细行；
--   2. 一次性彻底清理 2 小时前的所有非长期流水（战斗掉落、物品使用、商店回收、技能参悟、建筑捐献、组队结算、教程跳过等）；
--   3. 一次性彻底清理 14 天前的长期流水（market、shop.buy、player.unlock_avatar）；
--   4. 推进 platform.schema_version 至 43，存档标签未变（仍为 v38）。
-- 适用对象：platform.schema_version 最新行为 42 的既有库；执行后启动门禁（要求 v43）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v43。

BEGIN;

ALTER TABLE economy.transaction_lines
    DROP CONSTRAINT IF EXISTS transaction_lines_transaction_id_fkey;

ALTER TABLE economy.transaction_lines
    ADD CONSTRAINT transaction_lines_transaction_id_fkey
    FOREIGN KEY (transaction_id)
    REFERENCES economy.transactions(transaction_id)
    ON DELETE CASCADE;

-- 1. 一次性清扫 2 小时前所有的短期易失性流水（此时 transaction_lines 会通过外键级联同步删除）
DELETE FROM economy.transactions
WHERE created_at < now() - interval '2 hours'
  AND transaction_type NOT IN ('market', 'shop.buy', 'player.unlock_avatar')
  AND NOT EXISTS (SELECT 1 FROM social.party_session_inbox i WHERE i.consumed_transaction_id = transactions.transaction_id);

-- 2. 一次性清扫 14 天前的长期金融流水
DELETE FROM economy.transactions
WHERE created_at < now() - interval '14 days'
  AND transaction_type IN ('market', 'shop.buy', 'player.unlock_avatar');

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (43, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
