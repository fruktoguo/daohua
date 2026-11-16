namespace Server.Clean;

/// <summary>
/// 只清理可重建或超期运行/流水记录，确保热库体积在高负载 2c4g/40g 上保持严格有界：
/// - 瞬时幂等防重表 <c>economy.transient_idempotency</c> 保留 2 小时；
/// - 非白名单历史 <c>economy.transactions</c> 无延迟分批排空；
/// - 5 类白名单账本与日志（坊市/商店功德/登入上下线审计/PVP 斗法/邮件与跨玩家社交）统一热表保留 30 天（期满删除，依赖定期备份留档）；
/// - 余额表、活跃挂单与未投递 outbox 永不删除；
/// - 每 5 分钟触发一轮，单轮最多连续执行 8 个短事务批次以快速追平历史积压。
/// </summary>
public sealed class PostgresRetentionRepository(
    NpgsqlDataSource dataSource,
    ServerOptions options,
    ILogger<PostgresRetentionRepository> logger)
{
    private const int MaxPassesPerCycle = 8;

    /// <summary>按外键安全顺序多批排空可清理数据；每轮独立短事务，避免长事务持锁。</summary>
    public async ValueTask<RetentionResult> PruneAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var outboxBefore = now - options.DeliveredOutboxRetention;
        var sessionsBefore = now - options.SessionRetention;
        var auditBefore = now - options.AuditRetention;
        var ledgerBefore = now - options.LedgerRetention;
        var transientBefore = now - options.TransientIdempotencyRetention;
        var riskBefore = now - options.AccountRiskEventRetention;
        var computeBefore = now - options.ComputeJobRetention;
        var keysBefore = now - options.DataProtectionKeyRetention;
        var claimsBefore = now - options.SettlementClaimRetention;
        var normalBatch = options.RetentionBatchSize;
        var largeBatch = Math.Min(10_000, normalBatch * 16);

        var outboxTotal = 0;
        var sessionsTotal = 0;
        var auditTotal = 0;
        var computeTotal = 0;
        var keysTotal = 0;
        var claimsTotal = 0;
        var riskTotal = 0;
        var inboxTotal = 0;
        var transactionsTotal = 0;
        var transientTotal = 0;
        var expiredMailsTotal = 0;
        var duelLogsTotal = 0;
        var marketTradesTotal = 0;
        var terminalOrdersTotal = 0;
        var terminalReservationsTotal = 0;
        var shopDailyPurchasesTotal = 0;
        var stickerTombstonesTotal = 0;
        var stickerTombstonesBeforeMs = ledgerBefore.ToUnixTimeMilliseconds();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        for (var pass = 0; pass < MaxPassesPerCycle; pass++)
        {
            var tracker = new BatchCapTracker();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var deletedOutbox = await DeleteBatchAsync(connection, transaction, """
                SELECT outbox_id
                FROM platform.integration_outbox
                WHERE delivered_at IS NOT NULL AND delivered_at < $1
                ORDER BY delivered_at
                LIMIT $2
                """, "DELETE FROM platform.integration_outbox o USING ({0}) doomed WHERE o.outbox_id = doomed.outbox_id",
                outboxBefore, normalBatch, cancellationToken, tracker);

            var deletedSessions = await DeleteBatchAsync(connection, transaction, """
                SELECT session_id
                FROM identity.auth_sessions
                WHERE (revoked_at IS NOT NULL AND revoked_at < $1)
                   OR expires_at < $1
                ORDER BY expires_at
                LIMIT $2
                """, "DELETE FROM identity.auth_sessions s USING ({0}) doomed WHERE s.session_id = doomed.session_id",
                sessionsBefore, normalBatch, cancellationToken, tracker);

            var deletedAudit = await DeleteBatchAsync(connection, transaction, """
                SELECT event_id
                FROM identity.audit_events
                WHERE occurred_at < $1
                ORDER BY occurred_at
                LIMIT $2
                """, "DELETE FROM identity.audit_events e USING ({0}) doomed WHERE e.event_id = doomed.event_id",
                auditBefore, largeBatch, cancellationToken, tracker);

            var deletedRisk = await DeleteBatchAsync(connection, transaction, """
                SELECT event_id FROM identity.account_risk_events WHERE occurred_at < $1 ORDER BY occurred_at LIMIT $2
                """, "DELETE FROM identity.account_risk_events e USING ({0}) doomed WHERE e.event_id = doomed.event_id",
                riskBefore, largeBatch, cancellationToken, tracker);

            var deletedCompute = await DeleteBatchAsync(connection, transaction, """
                SELECT job_id
                FROM platform.compute_jobs
                WHERE updated_at < $1
                  AND status IN ('succeeded', 'failed', 'cancelled', 'dead')
                ORDER BY updated_at
                LIMIT $2
                """, "DELETE FROM platform.compute_jobs j USING ({0}) doomed WHERE j.job_id = doomed.job_id",
                computeBefore, normalBatch, cancellationToken, tracker);

            var deletedKeys = await DeleteBatchAsync(connection, transaction, """
                SELECT key_id
                FROM identity.data_protection_keys
                WHERE expires_at IS NOT NULL AND expires_at < $1
                ORDER BY expires_at
                LIMIT $2
                """, "DELETE FROM identity.data_protection_keys k USING ({0}) doomed WHERE k.key_id = doomed.key_id",
                keysBefore, normalBatch, cancellationToken, tracker);

            var deletedClaims = await DeleteBatchAsync(connection, transaction, """
                SELECT player_id, request_id
                FROM market.settlement_claims
                WHERE claimed_at < $1
                ORDER BY claimed_at
                LIMIT $2
                """, "DELETE FROM market.settlement_claims c USING ({0}) doomed WHERE c.player_id = doomed.player_id AND c.request_id = doomed.request_id",
                claimsBefore, normalBatch, cancellationToken, tracker);

            // 终局已消费组队结算载荷回收必须在清理关联事务前执行，以释放 consumed_transaction_id 外键占用。
            var deletedInboxes = await DeleteBatchAsync(connection, transaction, """
                SELECT inbox.session_id, inbox.player_id, inbox.sequence
                FROM social.party_session_inbox inbox
                JOIN social.party_sessions session ON session.session_id = inbox.session_id
                WHERE session.ended_at IS NOT NULL AND inbox.consumed_at <= $1
                ORDER BY inbox.consumed_at
                LIMIT $2
                """, "DELETE FROM social.party_session_inbox inbox USING ({0}) doomed WHERE inbox.session_id = doomed.session_id AND inbox.player_id = doomed.player_id AND inbox.sequence = doomed.sequence",
                now, normalBatch, cancellationToken, tracker);

            // 1. 瞬时经济幂等防重表（保留 2 小时）：承载 PersistLedger=false 的短期防重窗口。
            var deletedTransient = await DeleteBatchAsync(connection, transaction, """
                SELECT player_id, idempotency_key
                FROM economy.transient_idempotency
                WHERE created_at < $1
                ORDER BY created_at
                LIMIT $2
                """, """
                DELETE FROM economy.transient_idempotency t
                USING ({0}) doomed
                WHERE t.player_id = doomed.player_id AND t.idempotency_key = doomed.idempotency_key
                """,
                transientBefore, largeBatch, cancellationToken, tracker);

            // 2. 非白名单历史流水（单人模拟/战斗掉落/养成消耗/participation.settlement 等）立即排空，级联删除 economy.transaction_lines。
            var deletedNonWhitelistTx = await DeleteBatchAsync(connection, transaction, """
                SELECT t.transaction_id
                FROM economy.transactions t
                WHERE t.created_at <= $1
                  AND NOT (
                      t.transaction_type IN ('market', 'shop.buy', 'player.unlock_avatar', 'mail.claim', 'participation.assets', 'building.contribute', 'doudizhu.settlement', 'system-grant')
                      OR t.transaction_type LIKE 'market.%'
                      OR t.transaction_type LIKE 'invitation.%'
                      OR t.transaction_type LIKE 'zongmen.%'
                      OR t.transaction_type LIKE 'ops.%'
                  )
                  AND NOT EXISTS (SELECT 1 FROM social.party_session_inbox i WHERE i.consumed_transaction_id = t.transaction_id)
                ORDER BY t.created_at
                LIMIT $2
                """, "DELETE FROM economy.transactions t USING ({0}) doomed WHERE t.transaction_id = doomed.transaction_id",
                now, largeBatch, cancellationToken, tracker);

            // 3. 白名单不可变账本（坊市/商店功德/邮件与跨玩家社交消耗品交互）统一热表保留 30 天。
            var deletedWhitelistTx = await DeleteBatchAsync(connection, transaction, """
                SELECT t.transaction_id
                FROM economy.transactions t
                WHERE t.created_at < $1
                  AND (
                      t.transaction_type IN ('market', 'shop.buy', 'player.unlock_avatar', 'mail.claim', 'participation.assets', 'building.contribute', 'doudizhu.settlement', 'system-grant')
                      OR t.transaction_type LIKE 'market.%'
                      OR t.transaction_type LIKE 'invitation.%'
                      OR t.transaction_type LIKE 'zongmen.%'
                      OR t.transaction_type LIKE 'ops.%'
                  )
                  AND NOT EXISTS (SELECT 1 FROM social.party_session_inbox i WHERE i.consumed_transaction_id = t.transaction_id)
                ORDER BY t.created_at
                LIMIT $2
                """, "DELETE FROM economy.transactions t USING ({0}) doomed WHERE t.transaction_id = doomed.transaction_id",
                ledgerBefore, largeBatch, cancellationToken, tracker);

            // 4. PVP 斗法完整收益日志：统一 30 天滚动清理。
            var deletedDuelLogs = await DeleteBatchAsync(connection, transaction, """
                SELECT duel_log_id
                FROM game.duel_logs
                WHERE occurred_at < $1
                ORDER BY occurred_at
                LIMIT $2
                """, "DELETE FROM game.duel_logs d USING ({0}) doomed WHERE d.duel_log_id = doomed.duel_log_id",
                ledgerBefore, largeBatch, cancellationToken, tracker);

            // 5. 坊市成交明细：必须在终态 orders 之前删除，释放 buy_order_id/sell_order_id 外键约束。
            var deletedMarketTrades = await DeleteBatchAsync(connection, transaction, """
                SELECT trade_id
                FROM market.trades
                WHERE traded_at < $1
                ORDER BY traded_at
                LIMIT $2
                """, "DELETE FROM market.trades t USING ({0}) doomed WHERE t.trade_id = doomed.trade_id",
                ledgerBefore, largeBatch, cancellationToken, tracker);

            // 6. 无活跃成交外键引用的超期终态挂单：活跃单（active / partially_filled）与 30 天内完结单永不删除。
            var deletedTerminalOrders = await DeleteBatchAsync(connection, transaction, """
                SELECT o.order_id
                FROM market.orders o
                WHERE o.status IN ('filled', 'cancelled', 'expired')
                  AND o.updated_at < $1
                  AND NOT EXISTS (
                      SELECT 1 FROM market.trades tr
                      WHERE tr.buy_order_id = o.order_id OR tr.sell_order_id = o.order_id
                  )
                ORDER BY o.updated_at
                LIMIT $2
                """, "DELETE FROM market.orders o USING ({0}) doomed WHERE o.order_id = doomed.order_id",
                ledgerBefore, largeBatch, cancellationToken, tracker);

            // 7. 终态冻结预占行（consumed / released）：活跃冻结行永不删除。
            var deletedTerminalReservations = await DeleteBatchAsync(connection, transaction, """
                SELECT reservation_id
                FROM economy.reservations
                WHERE status <> 'active' AND updated_at < $1
                ORDER BY updated_at
                LIMIT $2
                """, "DELETE FROM economy.reservations r USING ({0}) doomed WHERE r.reservation_id = doomed.reservation_id",
                ledgerBefore, largeBatch, cancellationToken, tracker);

            // 8. 过期系统商店每日限购记录。
            var deletedShopPurchases = await DeleteBatchAsync(connection, transaction, """
                SELECT player_id, offer_id, day_index
                FROM economy.shop_daily_purchases
                WHERE updated_at < $1
                ORDER BY updated_at
                LIMIT $2
                """, """
                DELETE FROM economy.shop_daily_purchases p
                USING ({0}) doomed
                WHERE p.player_id = doomed.player_id AND p.offer_id = doomed.offer_id AND p.day_index = doomed.day_index
                """,
                ledgerBefore, largeBatch, cancellationToken, tracker);

            var deletedExpiredPlayerMails = await DeleteBatchAsync(connection, transaction, """
                SELECT mail_id
                FROM social.player_mails
                WHERE expires_at IS NOT NULL AND expires_at < $1
                ORDER BY expires_at
                LIMIT $2
                """, "DELETE FROM social.player_mails m USING ({0}) doomed WHERE m.mail_id = doomed.mail_id",
                now, normalBatch, cancellationToken, tracker);

            var deletedExpiredBroadcastMails = await DeleteBatchAsync(connection, transaction, """
                SELECT broadcast_id
                FROM social.broadcast_mails
                WHERE expires_at IS NOT NULL AND expires_at < $1
                ORDER BY expires_at
                LIMIT $2
                """, "DELETE FROM social.broadcast_mails m USING ({0}) doomed WHERE m.broadcast_id = doomed.broadcast_id",
                now, normalBatch, cancellationToken, tracker);

            var deletedStickerTombstones = await DeleteBatchUnixMsAsync(connection, transaction, """
                SELECT player_id, sticker_id
                FROM social.player_stickers
                WHERE deleted_at IS NOT NULL AND deleted_at < $1
                ORDER BY deleted_at
                LIMIT $2
                """, """
                DELETE FROM social.player_stickers s
                USING ({0}) doomed
                WHERE s.player_id = doomed.player_id AND s.sticker_id = doomed.sticker_id
                """,
                stickerTombstonesBeforeMs, normalBatch, cancellationToken, tracker);

            await transaction.CommitAsync(cancellationToken);

            outboxTotal += deletedOutbox;
            sessionsTotal += deletedSessions;
            auditTotal += deletedAudit;
            computeTotal += deletedCompute;
            keysTotal += deletedKeys;
            claimsTotal += deletedClaims;
            riskTotal += deletedRisk;
            inboxTotal += deletedInboxes;
            transactionsTotal += deletedNonWhitelistTx + deletedWhitelistTx;
            transientTotal += deletedTransient;
            expiredMailsTotal += deletedExpiredPlayerMails + deletedExpiredBroadcastMails;
            duelLogsTotal += deletedDuelLogs;
            marketTradesTotal += deletedMarketTrades;
            terminalOrdersTotal += deletedTerminalOrders;
            terminalReservationsTotal += deletedTerminalReservations;
            shopDailyPurchasesTotal += deletedShopPurchases;
            stickerTombstonesTotal += deletedStickerTombstones;

            if (!tracker.HitCap) break;
        }

        var result = new RetentionResult(
            outboxTotal,
            sessionsTotal,
            auditTotal,
            computeTotal,
            keysTotal,
            claimsTotal,
            riskTotal,
            inboxTotal,
            transactionsTotal,
            transientTotal,
            expiredMailsTotal,
            duelLogsTotal,
            marketTradesTotal,
            terminalOrdersTotal,
            terminalReservationsTotal,
            shopDailyPurchasesTotal,
            stickerTombstonesTotal);

        if (result.Total > 0)
            logger.LogInformation(
                "已完成运行与白名单流水清理 outbox={Outbox} sessions={Sessions} audit={Audit} compute={Compute} dataProtectionKeys={DataProtectionKeys} settlementClaims={SettlementClaims} accountRiskEvents={AccountRiskEvents} participationInboxes={ParticipationInboxes} transactions={Transactions} transientIdempotency={TransientIdempotency} expiredMails={ExpiredMails} duelLogs={DuelLogs} marketTrades={MarketTrades} terminalOrders={TerminalOrders} terminalReservations={TerminalReservations} shopDailyPurchases={ShopDailyPurchases} stickerTombstones={StickerTombstones}",
                result.Outbox, result.Sessions, result.Audit, result.ComputeJobs, result.DataProtectionKeys,
                result.SettlementClaims, result.AccountRiskEvents, result.ParticipationInboxes, result.EmptyTransactions,
                result.TransientIdempotency, result.ExpiredMails, result.DuelLogs, result.MarketTrades,
                result.TerminalOrders, result.TerminalReservations, result.ShopDailyPurchases, result.StickerTombstones);
        return result;
    }

    private sealed class BatchCapTracker
    {
        public bool HitCap { get; set; }
    }

    private static async Task<int> DeleteBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string selectorSql,
        string deleteTemplate,
        DateTimeOffset before,
        int batchSize,
        CancellationToken cancellationToken,
        BatchCapTracker tracker)
    {
        await using var command = new NpgsqlCommand(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            deleteTemplate,
            selectorSql), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = before });
        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected >= batchSize) tracker.HitCap = true;
        return affected;
    }

    private static async Task<int> DeleteBatchUnixMsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string selectorSql,
        string deleteTemplate,
        long beforeUnixMs,
        int batchSize,
        CancellationToken cancellationToken,
        BatchCapTracker tracker)
    {
        await using var command = new NpgsqlCommand(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            deleteTemplate,
            selectorSql), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = beforeUnixMs });
        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected >= batchSize) tracker.HitCap = true;
        return affected;
    }
}

/// <summary>一轮清理的删除行数统计。</summary>
/// <param name="Outbox">已删除的已投递 outbox 行数。</param>
/// <param name="Sessions">已删除的过期/已撤销会话行数。</param>
/// <param name="Audit">已删除的审计事件行数。</param>
/// <param name="ComputeJobs">已删除的终态冷挂机任务行数。</param>
/// <param name="DataProtectionKeys">已删除的过期 Data Protection key 行数。</param>
/// <param name="SettlementClaims">已删除的过期结算确认游标行数。</param>
/// <param name="AccountRiskEvents">已删除的过期账号风险事件行数。</param>
/// <param name="ParticipationInboxes">已删除的终态会话已消费结算载荷行数。</param>
/// <param name="EmptyTransactions">已删除的非白名单/超期白名单经济流水事务行数。</param>
/// <param name="TransientIdempotency">已删除的超期瞬时经济幂等防重行数。</param>
/// <param name="ExpiredMails">已删除的过期个人邮件与全服群发公告行数。</param>
/// <param name="DuelLogs">已删除的超期 PVP 斗法日志行数。</param>
/// <param name="MarketTrades">已删除的超期坊市成交明细行数。</param>
/// <param name="TerminalOrders">已删除的超期无引用终态挂单行数。</param>
/// <param name="TerminalReservations">已删除的超期终态冻结记录行数。</param>
/// <param name="ShopDailyPurchases">已删除的超期系统商店每日限购记录行数。</param>
/// <param name="StickerTombstones">已删除的超期表情包软删除墓碑行数。</param>
public sealed record RetentionResult(
    int Outbox,
    int Sessions,
    int Audit,
    int ComputeJobs,
    int DataProtectionKeys,
    int SettlementClaims,
    int AccountRiskEvents,
    int ParticipationInboxes = 0,
    int EmptyTransactions = 0,
    int TransientIdempotency = 0,
    int ExpiredMails = 0,
    int DuelLogs = 0,
    int MarketTrades = 0,
    int TerminalOrders = 0,
    int TerminalReservations = 0,
    int ShopDailyPurchases = 0,
    int StickerTombstones = 0)
{
    /// <summary>本轮删除总行数。</summary>
    public int Total => Outbox + Sessions + Audit + ComputeJobs + DataProtectionKeys + SettlementClaims
        + AccountRiskEvents + ParticipationInboxes + EmptyTransactions + TransientIdempotency + ExpiredMails
        + DuelLogs + MarketTrades + TerminalOrders + TerminalReservations + ShopDailyPurchases + StickerTombstones;
}

/// <summary>定期清理后台任务：启动 5 分钟后首次执行，之后按配置周期（默认 5 分钟）多批排空超期流水与日志。</summary>
public sealed class PostgresRetentionWorker(
    PostgresRetentionRepository repository,
    ServerOptions options,
    ILogger<PostgresRetentionWorker> logger) : BackgroundService
{
    private readonly LogThrottle _errorLog = new(TimeSpan.FromMinutes(1));

    /// <summary>
    /// 清理主循环：启动延迟 5 分钟（避开建库与 key-ring 热路径）后首跑，
    /// 之后经 PeriodicTimer 按 <see cref="ServerOptions.RetentionInterval"/> 触发一轮
    /// <see cref="PostgresRetentionRepository.PruneAsync"/>；单轮失败经节流记日志，下个周期重试。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        using var timer = new PeriodicTimer(options.RetentionInterval);
        do
        {
            try { await repository.PruneAsync(DateTimeOffset.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                if (_errorLog.TryAcquire()) logger.LogError(exception, "运行与流水留存清理失败，将在下个周期重试。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
