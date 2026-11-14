-- 0055_v54_to_v55.sql
-- v54 -> v55 兼容迁移：
--   1. identity.accounts 新增 is_bot boolean NOT NULL DEFAULT false，标识机器人账号（常规玩家默认 false）；
--   2. 为既有通过 DaoHua-MarketBots 创建的 bot_% 前缀账号补齐 is_bot = true；
--   3. 玩家分段载荷结构与存档标签保持 v43 不变。
-- 适用对象：platform.schema_version 最新行为 54 的既有库；执行后启动门禁（要求 v55）即通过。
-- 本文件与 migrations/ 目录可随时删除，不影响编译与启动；空库由 PostgresSchema.Sql 直建 v55。

BEGIN;

ALTER TABLE identity.accounts
    ADD COLUMN IF NOT EXISTS is_bot boolean NOT NULL DEFAULT false;

UPDATE identity.accounts
SET is_bot = true,
    updated_at = now()
WHERE NOT is_bot
  AND login_normalized LIKE 'bot\_%' ESCAPE '\';

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (55, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
