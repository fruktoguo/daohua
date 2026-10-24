-- 0021_v20_to_v21.sql
-- v20 → v21：行动队列参与载荷切换为组织无关 participation 绑定。
-- partySession/party.expedition 不能无损还原为真实 ActionId 与 participation；禁止仅推进玩家行标签。
-- 本脚本只标记库级版本，旧玩家存档继续保持 v20，由 reset-outdated-players.sh 检出后要求
-- 操作者交互确认清零；无 TTY 必须中止。全新空库由 PostgresSchema.Sql 直建 v21。

BEGIN;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (21, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
