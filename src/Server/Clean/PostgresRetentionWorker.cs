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

    private enum CleanupStepKind
    {
        Outbox,
        DeadOutbox,
        DeadFlushQueue,
        StaleRuntimeLeases,
        Sessions,
        Audit,
        RiskEvents,
        ComputeJobs,
        DataProtectionKeys,
        SettlementClaims,
        ParticipationInboxes,
        TransientIdempotency,
        NonWhitelistTransactions,
        WhitelistTransactions,
        DuelLogs,
        MarketTrades,
        TerminalOrders,
        TerminalReservations,
        ShopDailyPurchases,
        ExpiredPlayerMails,
        ExpiredBroadcastMails,
        StickerTombstones,
        Count
    }

    private sealed record CleanupStepSpec(
        CleanupStepKind Kind,
        object Cutoff,
        int BatchSize,
        string SelectorSql,
        string DeleteTemplate,
        CleanupStepKind? TriggeredBy = null);

    /// <summary>按外键安全顺序逐表独立短事务多批排空可清理数据；单表故障或锁超时不回滚已提交批，且跳过已清空表以节省 IOPS。</summary>
    public async ValueTask<RetentionResult> PruneAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var outboxBefore = now - options.DeliveredOutboxRetention;
        var deadBefore = now - TimeSpan.FromDays(7);
        var staleLeaseBefore = now - TimeSpan.FromHours(1);
        var sessionsBefore = now - options.SessionRetention;
        var auditBefore = now - options.AuditRetention;
        var ledgerBefore = now - options.LedgerRetention;
        var stickerTombstonesBeforeMs = ledgerBefore.ToUnixTimeMilliseconds();
        var transientBefore = now - options.TransientIdempotencyRetention;
        var riskBefore = now - options.AccountRiskEventRetention;
        var computeBefore = now - options.ComputeJobRetention;
        var keysBefore = now - options.DataProtectionKeyRetention;
        var claimsBefore = now - options.SettlementClaimRetention;
        var normalBatch = options.RetentionBatchSize;
        var largeBatch = Math.Min(10_000, normalBatch * 16);

        CleanupStepSpec[] steps =
        [
            new(CleanupStepKind.Outbox, outboxBefore, normalBatch, """
                SELECT outbox_id
                FROM platform.integration_outbox
                WHERE delivered_at IS NOT NULL AND delivered_at < $1
                ORDER BY delivered_at
                LIMIT $2
                """, "DELETE FROM platform.integration_outbox o USING ({0}) doomed WHERE o.outbox_id = doomed.outbox_id"),

            new(CleanupStepKind.DeadOutbox, deadBefore, normalBatch, """
                SELECT outbox_id
                FROM platform.integration_outbox
                WHERE delivered_at IS NULL AND status = 'dead' AND visible_at < $1
                ORDER BY visible_at
                LIMIT $2
                """, "DELETE FROM platform.integration_outbox o USING ({0}) doomed WHERE o.outbox_id = doomed.outbox_id"),

            new(CleanupStepKind.DeadFlushQueue, deadBefore, normalBatch, """
                SELECT queue_id
                FROM platform.section_flush_queue
                WHERE status = 'dead' AND updated_at < $1
                ORDER BY updated_at
                LIMIT $2
                """, "DELETE FROM platform.section_flush_queue q USING ({0}) doomed WHERE q.queue_id = doomed.queue_id"),

            new(CleanupStepKind.StaleRuntimeLeases, staleLeaseBefore, normalBatch, """
                SELECT player_id
                FROM game.player_runtime_leases
                WHERE lease_until < $1
                ORDER BY lease_until
                LIMIT $2
                """, "DELETE FROM game.player_runtime_leases l USING ({0}) doomed WHERE l.player_id = doomed.player_id"),

            new(CleanupStepKind.Sessions, sessionsBefore, normalBatch, """
                SELECT session_id
                FROM identity.auth_sessions
                WHERE (revoked_at IS NOT NULL AND revoked_at < $1)
                   OR expires_at < $1
                ORDER BY expires_at
                LIMIT $2
                """, "DELETE FROM identity.auth_sessions s USING ({0}) doomed WHERE s.session_id = doomed.session_id"),

            new(CleanupStepKind.Audit, auditBefore, largeBatch, """
                SELECT event_id
                FROM identity.audit_events
                WHERE occurred_at < $1
                ORDER BY occurred_at
                LIMIT $2
                """, "DELETE FROM identity.audit_events e USING ({0}) doomed WHERE e.event_id = doomed.event_id"),

            new(CleanupStepKind.RiskEvents, riskBefore, largeBatch, """
                SELECT event_id FROM identity.account_risk_events WHERE occurred_at < $1 ORDER BY occurred_at LIMIT $2
                """, "DELETE FROM identity.account_risk_events e USING ({0}) doomed WHERE e.event_id = doomed.event_id"),

            new(CleanupStepKind.ComputeJobs, computeBefore, normalBatch, """
                SELECT job_id
                FROM platform.compute_jobs
                WHERE updated_at < $1
                  AND status IN ('succeeded', 'failed', 'cancelled', 'dead')
                ORDER BY updated_at
                LIMIT $2
                """, "DELETE FROM platform.compute_jobs j USING ({0}) doomed WHERE j.job_id = doomed.job_id"),

            new(CleanupStepKind.DataProtectionKeys, keysBefore, normalBatch, """
                SELECT key_id
                FROM identity.data_protection_keys
                WHERE expires_at IS NOT NULL AND expires_at < $1
                ORDER BY expires_at
                LIMIT $2
                """, "DELETE FROM identity.data_protection_keys k USING ({0}) doomed WHERE k.key_id = doomed.key_id"),

            new(CleanupStepKind.SettlementClaims, claimsBefore, normalBatch, """
                SELECT player_id, request_id
                FROM market.settlement_claims
                WHERE claimed_at < $1
                ORDER BY claimed_at
                LIMIT $2
                """, "DELETE FROM market.settlement_claims c USING ({0}) doomed WHERE c.player_id = doomed.player_id AND c.request_id = doomed.request_id"),

            // 终局已消费组队结算载荷回收必须在清理关联事务前执行，以释放 consumed_transaction_id 外键占用。
            new(CleanupStepKind.ParticipationInboxes, now, normalBatch, """
                SELECT inbox.session_id, inbox.player_id, inbox.sequence
                FROM social.party_session_inbox inbox
                JOIN social.party_sessions session ON session.session_id = inbox.session_id
                WHERE session.ended_at IS NOT NULL AND inbox.consumed_at <= $1
                ORDER BY inbox.consumed_at
                LIMIT $2
                """, "DELETE FROM social.party_session_inbox inbox USING ({0}) doomed WHERE inbox.session_id = doomed.session_id AND inbox.player_id = doomed.player_id AND inbox.sequence = doomed.sequence"),

            // 1. 瞬时经济幂等防重表（保留 2 小时）：承载 PersistLedger=false 的短期防重窗口。
            new(CleanupStepKind.TransientIdempotency, transientBefore, largeBatch, """
                SELECT player_id, idempotency_key
                FROM economy.transient_idempotency
                WHERE created_at < $1
                ORDER BY created_at
                LIMIT $2
                """, """
                DELETE FROM economy.transient_idempotency t
                USING ({0}) doomed
                WHERE t.player_id = doomed.player_id AND t.idempotency_key = doomed.idempotency_key
                """),

            // 2. 非白名单历史流水（单人模拟/战斗掉落/养成消耗/participation.settlement 等）立即排空，级联删除 economy.transaction_lines。
            new(CleanupStepKind.NonWhitelistTransactions, now, largeBatch, """
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
                TriggeredBy: CleanupStepKind.ParticipationInboxes),

            // 3. 白名单不可变账本（坊市/商店功德/邮件与跨玩家社交消耗品交互）统一热表保留 30 天。
            new(CleanupStepKind.WhitelistTransactions, ledgerBefore, largeBatch, """
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
                TriggeredBy: CleanupStepKind.ParticipationInboxes),

            // 4. PVP 斗法完整收益日志：统一 30 天滚动清理。
            new(CleanupStepKind.DuelLogs, ledgerBefore, largeBatch, """
                SELECT duel_log_id
                FROM game.duel_logs
                WHERE occurred_at < $1
                ORDER BY occurred_at
                LIMIT $2
                """, "DELETE FROM game.duel_logs d USING ({0}) doomed WHERE d.duel_log_id = doomed.duel_log_id"),

            // 5. 坊市成交明细：必须在终态 orders 之前删除，释放 buy_order_id/sell_order_id 外键约束。
            new(CleanupStepKind.MarketTrades, ledgerBefore, largeBatch, """
                SELECT trade_id
                FROM market.trades
                WHERE traded_at < $1
                ORDER BY traded_at
                LIMIT $2
                """, "DELETE FROM market.trades t USING ({0}) doomed WHERE t.trade_id = doomed.trade_id"),

            // 6. 无活跃成交外键引用的超期终态挂单：活跃单（active / partially_filled）与 30 天内完结单永不删除。
            new(CleanupStepKind.TerminalOrders, ledgerBefore, largeBatch, """
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
                TriggeredBy: CleanupStepKind.MarketTrades),

            // 7. 终态冻结预占行（consumed / released）：活跃冻结行永不删除。
            new(CleanupStepKind.TerminalReservations, ledgerBefore, largeBatch, """
                SELECT reservation_id
                FROM economy.reservations
                WHERE status <> 'active' AND updated_at < $1
                ORDER BY updated_at
                LIMIT $2
                """, "DELETE FROM economy.reservations r USING ({0}) doomed WHERE r.reservation_id = doomed.reservation_id"),

            // 8. 过期系统商店每日限购记录。
            new(CleanupStepKind.ShopDailyPurchases, ledgerBefore, largeBatch, """
                SELECT player_id, offer_id, day_index
                FROM economy.shop_daily_purchases
                WHERE updated_at < $1
                ORDER BY updated_at
                LIMIT $2
                """, """
                DELETE FROM economy.shop_daily_purchases p
                USING ({0}) doomed
                WHERE p.player_id = doomed.player_id AND p.offer_id = doomed.offer_id AND p.day_index = doomed.day_index
                """),

            new(CleanupStepKind.ExpiredPlayerMails, now, normalBatch, """
                SELECT mail_id
                FROM social.player_mails
                WHERE expires_at IS NOT NULL AND expires_at < $1
                ORDER BY expires_at
                LIMIT $2
                """, "DELETE FROM social.player_mails m USING ({0}) doomed WHERE m.mail_id = doomed.mail_id"),

            new(CleanupStepKind.ExpiredBroadcastMails, now, normalBatch, """
                SELECT broadcast_id
                FROM social.broadcast_mails
                WHERE expires_at IS NOT NULL AND expires_at < $1
                ORDER BY expires_at
                LIMIT $2
                """, "DELETE FROM social.broadcast_mails m USING ({0}) doomed WHERE m.broadcast_id = doomed.broadcast_id"),

            // 9. 传音表情包软删除墓碑：超过 30 天的 deleted_at 记录彻底清除（deleted_at 为 Unix 毫秒时间戳）。
            new(CleanupStepKind.StickerTombstones, stickerTombstonesBeforeMs, normalBatch, """
                SELECT player_id, sticker_id
                FROM social.player_stickers
                WHERE deleted_at IS NOT NULL AND deleted_at < $1
                ORDER BY deleted_at
                LIMIT $2
                """, """
                DELETE FROM social.player_stickers s
                USING ({0}) doomed
                WHERE s.player_id = doomed.player_id AND s.sticker_id = doomed.sticker_id
                """),
        ];

        var totals = new int[(int)CleanupStepKind.Count];
        var hasBacklog = new bool[(int)CleanupStepKind.Count];
        Array.Fill(hasBacklog, true);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        for (var pass = 0; pass < MaxPassesPerCycle; pass++)
        {
            var anyHitCap = false;
            foreach (var step in steps)
            {
                var idx = (int)step.Kind;
                var shouldRun = hasBacklog[idx] || (step.TriggeredBy is { } dep && hasBacklog[(int)dep]);
                if (!shouldRun) continue;

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                var deleted = await DeleteBatchAsync(
                    connection, transaction, step.SelectorSql, step.DeleteTemplate,
                    step.Cutoff, step.BatchSize, cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                totals[idx] += deleted;
                hasBacklog[idx] = deleted >= step.BatchSize;
                if (hasBacklog[idx]) anyHitCap = true;
            }
            if (!anyHitCap) break;
        }

        var result = new RetentionResult(
            totals[(int)CleanupStepKind.Outbox],
            totals[(int)CleanupStepKind.Sessions],
            totals[(int)CleanupStepKind.Audit],
            totals[(int)CleanupStepKind.ComputeJobs],
            totals[(int)CleanupStepKind.DataProtectionKeys],
            totals[(int)CleanupStepKind.SettlementClaims],
            totals[(int)CleanupStepKind.RiskEvents],
            totals[(int)CleanupStepKind.ParticipationInboxes],
            totals[(int)CleanupStepKind.NonWhitelistTransactions] + totals[(int)CleanupStepKind.WhitelistTransactions],
            totals[(int)CleanupStepKind.TransientIdempotency],
            totals[(int)CleanupStepKind.ExpiredPlayerMails] + totals[(int)CleanupStepKind.ExpiredBroadcastMails],
            totals[(int)CleanupStepKind.DuelLogs],
            totals[(int)CleanupStepKind.MarketTrades],
            totals[(int)CleanupStepKind.TerminalOrders],
            totals[(int)CleanupStepKind.TerminalReservations],
            totals[(int)CleanupStepKind.ShopDailyPurchases],
            totals[(int)CleanupStepKind.StaleRuntimeLeases],
            totals[(int)CleanupStepKind.DeadFlushQueue],
            totals[(int)CleanupStepKind.DeadOutbox],
            totals[(int)CleanupStepKind.StickerTombstones]);

        if (result.Total > 0)
            logger.LogInformation(
                "已完成运行与白名单流水清理 outbox={Outbox} deadOutbox={DeadOutbox} deadQueue={DeadQueue} staleLeases={StaleLeases} sessions={Sessions} audit={Audit} compute={Compute} dataProtectionKeys={DataProtectionKeys} settlementClaims={SettlementClaims} accountRiskEvents={AccountRiskEvents} participationInboxes={ParticipationInboxes} transactions={Transactions} transientIdempotency={TransientIdempotency} expiredMails={ExpiredMails} duelLogs={DuelLogs} marketTrades={MarketTrades} terminalOrders={TerminalOrders} terminalReservations={TerminalReservations} shopDailyPurchases={ShopDailyPurchases} stickerTombstones={StickerTombstones}",
                result.Outbox, result.DeadOutboxRows, result.DeadQueueRows, result.StaleRuntimeLeases,
                result.Sessions, result.Audit, result.ComputeJobs, result.DataProtectionKeys,
                result.SettlementClaims, result.AccountRiskEvents, result.ParticipationInboxes, result.EmptyTransactions,
                result.TransientIdempotency, result.ExpiredMails, result.DuelLogs, result.MarketTrades,
                result.TerminalOrders, result.TerminalReservations, result.ShopDailyPurchases, result.StickerTombstones);
        return result;
    }

    private static async Task<int> DeleteBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string selectorSql,
        string deleteTemplate,
        object before,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            deleteTemplate,
            selectorSql), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = before });
        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });
        return await command.ExecuteNonQueryAsync(cancellationToken);
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
/// <param name="StaleRuntimeLeases">已删除的超期玩家运行时租约行数。</param>
/// <param name="DeadQueueRows">已删除的超期 section 死信队列行数。</param>
/// <param name="DeadOutboxRows">已删除的超期 outbox 死信行数。</param>
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
    int StaleRuntimeLeases = 0,
    int DeadQueueRows = 0,
    int DeadOutboxRows = 0,
    int StickerTombstones = 0)
{
    /// <summary>本轮删除总行数。</summary>
    public int Total => Outbox + Sessions + Audit + ComputeJobs + DataProtectionKeys + SettlementClaims
        + AccountRiskEvents + ParticipationInboxes + EmptyTransactions + TransientIdempotency + ExpiredMails
        + DuelLogs + MarketTrades + TerminalOrders + TerminalReservations + ShopDailyPurchases
        + StaleRuntimeLeases + DeadQueueRows + DeadOutboxRows + StickerTombstones;
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
        while (await HostedLoopSupport.WaitNextTickQuietlyAsync(timer, stoppingToken));
    }
}
