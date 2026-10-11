-- v25 → v26：市场成交按买家/卖家建时间复合索引。
-- 结算领取 ClaimSettlement 按 (buyer_id OR seller_id) 统计最近成交：无索引时谓词读落在
-- (traded_at) 尾部分区索引上，任何新成交插入都会与领取的 SIREAD 重叠并制造 40001；
-- 复合索引把谓词读收窄到该玩家自身的索引页，冲突概率降为「同玩家并发成交」量级。
-- trades 表持续增长，CONCURRENTLY 在线创建避免阻塞写入。

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_market_trades_buyer
    ON market.trades (buyer_id, traded_at);

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_market_trades_seller
    ON market.trades (seller_id, traded_at);
