-- v27 → v28 兼容迁移：新增 market.settlement_cursors 每玩家结算游标。
-- 结算领取原按 market.trades 时间窗谓词计数，与一切同玩家成交插入产生 SSI 冲突；
-- 游标行由成交事务 upsert 累加 pending_count、领取事务 FOR UPDATE 后清零结算，
-- 行锁互斥替代谓词读，领取事务可降级 ReadCommitted、SSI 冲突面归零。
-- 回填：pending_count = 各玩家「最近领取时刻之后」的成交腿数（买方/卖方各计一腿），
-- 保持与旧领取语义一致的未确认口径；迁移完成至新代码生效之间的成交未计入游标，
-- settled_trade_count 仅少报那几笔，成交资产早已入账不受影响。

BEGIN;

CREATE TABLE IF NOT EXISTS market.settlement_cursors (
    player_id uuid PRIMARY KEY REFERENCES identity.players(player_id),
    pending_count integer NOT NULL DEFAULT 0 CHECK (pending_count >= 0),
    settled_count bigint NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL
);

INSERT INTO market.settlement_cursors (player_id, pending_count, settled_count, updated_at)
SELECT legs.pid, count(*)::int, 0, now()
FROM (
    SELECT buyer_id AS pid, traded_at FROM market.trades
    UNION ALL
    SELECT seller_id AS pid, traded_at FROM market.trades
) legs
LEFT JOIN (
    SELECT player_id, max(claimed_at) AS claimed_at
    FROM market.settlement_claims GROUP BY player_id
) c ON c.player_id = legs.pid
WHERE legs.traded_at > COALESCE(c.claimed_at, '-infinity'::timestamptz)
GROUP BY legs.pid
ON CONFLICT (player_id) DO NOTHING;

INSERT INTO platform.schema_version(version, applied_at)
    VALUES (28, now()) ON CONFLICT (version) DO NOTHING;

COMMIT;
