namespace Server.Clean;

/// 冷挂机任务结果的统一写回边界：节点候选结果经独立重演验证后的可信产物，
/// 与主服本地执行器（§14.4）的自产可信结果走同一个 RepeatableRead 事务——
/// 租约校验、冻结源基线复核、冷玩家状态、钱包版本、section CAS、资产增量入账
/// 与任务完结同生同灭。
/// 业务正确性由显式行锁、MVCC fence 与 CAS 保证；可重放的事务并发冲突/死锁
/// 不烧任务领取次数，最终仍交给可重试失败路径。
/// </summary>
public sealed class ComputeResultCommitter(
    NpgsqlDataSource dataSource,
    PostgresSectionRepository sections,
    PostgresComputeJobRepository jobs,
    GameDataCatalog gameData,
    ServerOptions options,
    ILogger<ComputeResultCommitter> logger)
{
    // PostgreSQL 瞬态并发冲突/死锁只做有限次原地重放；不把其他持久化异常吞掉。
    private const int MaxTransactionReplayAttempts = 8;
    private readonly LogThrottle _transactionConflictLog = new(TimeSpan.FromMinutes(1));

    /// <summary>
    /// 提交一份可信推进结果：内部对串行化冲突原地重放（短退避），返回事务结局。
    /// 可信结果指主服独立重演产物：节点候选经 VerifyAsync 校验一致，或本地执行器
    /// ExecuteAsync 直接产出。节点自报的 metrics/错误字段仅作 output_payload 摘要证据，
    /// 不参与落库决策；本地执行路径三个字段均可空。
    /// </summary>
    public async ValueTask<ComputeApplyOutcome> CommitAsync(
        ComputeJobClaim claim,
        ComputeAdvanceJobRequest request,
        ComputeVerificationResult trusted,
        ComputeAdvanceMetrics? nodeMetrics,
        string? nodeErrorCode,
        string? nodeErrorMessage,
        CancellationToken cancellationToken)
    {
        var deltas = trusted.AssetDeltas.ToArray();
        var now = DateTimeOffset.UtcNow;
        var writes = trusted.Writes.Select(write => write with { UpdatedAt = now }).ToArray();

        // RR 固定快照配合显式 fence/CAS 维护逻辑一致性；事务级并发冲突仍原地重放（不烧领取次数）。
        // section 与 flush_queue 页级读写相交不构成业务冲突，故不依赖 SSI predicate-lock。
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await CommitResultInTransactionAsync(
                    claim, request, trusted, writes, deltas, nodeMetrics, nodeErrorCode, nodeErrorMessage,
                    cancellationToken);
            }
            catch (PostgresException exception) when (IsSerializationConflict(exception)
                && attempt < MaxTransactionReplayAttempts - 1)
            {
                if (_transactionConflictLog.TryAcquire())
                    logger.LogWarning("冷挂机结果落库瞬态并发冲突，重放事务 job={JobId} attempt={Attempt}/{MaxAttempts} sqlstate={SqlState}",
                        claim.JobId, attempt + 1, MaxTransactionReplayAttempts, exception.SqlState);
                var delayMilliseconds = Math.Min(1_000, 40 * (attempt + 1))
                    + Random.Shared.Next(0, 80);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), cancellationToken);
            }
        }
    }

    /// <summary>
    /// 单任务结果回写事务体：在 RepeatableRead 固定快照内将 section CAS、资产增量入账与任务完结并为一个原子提交。
    /// 事务内任何幂等冲突都以异常上交，由 <see cref="CommitAsync"/> 决定是否原地重放。
    /// </summary>
    private async Task<ComputeApplyOutcome> CommitResultInTransactionAsync(
        ComputeJobClaim claim, ComputeAdvanceJobRequest request, ComputeVerificationResult trusted,
        SectionWrite[] writes, Server.Clean.AssetDelta[] deltas,
        ComputeAdvanceMetrics? nodeMetrics, string? nodeErrorCode, string? nodeErrorMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        ComputeApplyOutcome applied;
        try
        {
            var walletReadSet = await ValidateFrozenBaselineInTransactionAsync(connection, transaction, claim, request, trusted, cancellationToken);
            applied = await sections.ApplyComputeResultInTransactionAsync(
                connection, transaction,
                claim.PlayerId, trusted.SourceStateVersion, trusted.TargetStateVersion,
                writes, trusted.LastSimulatedAt, gameData.Fingerprint, walletReadSet, cancellationToken);
        }
        catch (ComputeSourceDriftException)
        {
            // 合法源漂移只归档丢弃，不写 section 或钱包，也不把诚实执行方判死。
            applied = ComputeApplyOutcome.StaleVersion;
        }
        // output_payload 只存摘要：结果 JSON 内含全量 base64 section，整存会把单任务存储放大 ~2.3 倍。
        var output = JsonSerializer.SerializeToUtf8Bytes(new ComputeJobOutputSummary(
            claim.JobId, claim.PlayerId, trusted.SourceStateVersion, trusted.TargetStateVersion,
            applied.ToString(), deltas.Length, nodeMetrics, nodeErrorCode, nodeErrorMessage), ServerJson.Options);
        if (applied == ComputeApplyOutcome.Applied && deltas.Length > 0)
        {
            // 资产增量随本事务原子入账：幂等键绑定 job_id，重派重放不重复记账。
            // 节点不持有数据库权限、不创造第二套资产协议，本事务是补算收益的唯一入账点。
            var committed = await PostgresEconomyRepository.CommitCoreAsync(
                connection, transaction,
                new EconomyTransactionRequest(
                    claim.JobId,
                    $"simulation.compute:{claim.JobId:D}",
                    "simulation.reward",
                    claim.PlayerId,
                    deltas,
                    trusted.LastSimulatedAt,
                    output,
                    PersistLedger: false),
                cancellationToken);
            if (!committed.Applied)
                throw new InvalidOperationException("冷挂机资产事务未提交。");
        }
        if (!await PostgresComputeJobRepository.CompleteInTransactionAsync(
                connection, transaction, claim.JobId, claim.LeaseToken, output, cancellationToken))
            throw new InvalidOperationException("冷挂机任务租约已失效，拒绝提交 section 与资产事务。");
        await transaction.CommitAsync(cancellationToken);
        return applied;
    }

    /// <summary>
    /// 在同一 RepeatableRead 快照中重新核对身份、头/校验和与完整 section，并返回经格式及键唯一性校验的钱包读集。
    /// 下一阶段按原锁序锁定并比较全部余额行的数量与版本；合法源漂移通过专用结局丢弃结果并归档任务。
    /// </summary>
    private static async ValueTask<IReadOnlyDictionary<(string AssetId, string Variant), ComputeAssetBalance>> ValidateFrozenBaselineInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ComputeJobClaim claim,
        ComputeAdvanceJobRequest request,
        ComputeVerificationResult trusted,
        CancellationToken cancellationToken)
    {
        // 任务租约锁、身份/投影行锁与会话名册、未消费收件箱 fence 合并为单批次往返。
        await using (var batch = new NpgsqlBatch(connection) { Transaction = transaction })
        {
            var lease = new NpgsqlBatchCommand("""
                SELECT job_id FROM platform.compute_jobs
                WHERE job_id = $1 AND player_id = $2 AND lease_token = $3
                  AND status = 'leased' AND lease_until > now()
                FOR UPDATE
                """);
            lease.Parameters.Add(new NpgsqlParameter { Value = claim.JobId });
            lease.Parameters.Add(new NpgsqlParameter { Value = claim.PlayerId });
            lease.Parameters.Add(new NpgsqlParameter { Value = claim.LeaseToken });
            batch.BatchCommands.Add(lease);

            var player = new NpgsqlBatchCommand("""
                SELECT i.player_id, i.account_id, i.display_name, i.created_at, p.presence,
                       EXISTS (SELECT 1 FROM game.player_runtime_leases l
                               WHERE l.player_id = i.player_id AND l.lease_until > now()),
                       (EXISTS (
                           SELECT 1 FROM social.party_sessions s
                           WHERE s.ended_at IS NULL AND s.spec @> jsonb_build_object(
                               'members', jsonb_build_array(jsonb_build_object('playerId', i.player_id::text))))
                        OR EXISTS (
                           SELECT 1 FROM social.party_session_inbox inbox
                           WHERE inbox.player_id = i.player_id AND inbox.consumed_at IS NULL))
                FROM identity.players i
                JOIN game.player_projection p ON p.player_id = i.player_id
                WHERE i.player_id = $1 AND i.status = 1
                FOR UPDATE OF i, p
                """);
            player.Parameters.Add(new NpgsqlParameter { Value = claim.PlayerId });
            batch.BatchCommands.Add(player);

            await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetGuid(0) != claim.JobId)
                throw new InvalidOperationException("冷挂机任务租约已失效，拒绝结果写回。");
            await reader.NextResultAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new ComputeSourceDriftException("Compute 玩家源身份已不存在。");
            if (reader.GetGuid(0) != claim.PlayerId
                || reader.GetGuid(1) != request.AccountId
                || !string.Equals(reader.GetString(2), request.DisplayName, StringComparison.Ordinal)
                || reader.GetFieldValue<DateTimeOffset>(3).ToUnixTimeMilliseconds() != request.CreatedAtUnixMilliseconds)
                throw new ComputeSourceDriftException("Compute 玩家身份已漂移。");
            if (!string.Equals(reader.GetString(4), "offline", StringComparison.OrdinalIgnoreCase)
                || reader.GetBoolean(5))
                throw new ComputeSourceDriftException("玩家已上线或被其他运行时实例持有。");
            if (reader.GetBoolean(6))
                throw new ComputeSourceDriftException("玩家已由行动参与会话推进或仍有待消费结算。");
        }
        var bundle = await PostgresSectionRepository.LoadInTransactionAsync(
            connection, transaction, claim.PlayerId, cancellationToken, includeActionQueue: false)
            ?? throw new ComputeSourceDriftException("Compute 玩家完整源基线不存在。");
        if (bundle.Head.StateVersion != trusted.SourceStateVersion
            || bundle.Head.StateVersion <= 0
            || !string.Equals(bundle.Head.Checksum, request.HeadChecksum, StringComparison.Ordinal))
            throw new ComputeSourceDriftException("玩家提交头版本或校验和已漂移。");

        if (request.PlayerId != claim.PlayerId
            || request.SourceStateVersion != bundle.Head.StateVersion
            || request.AccountId != bundle.Identity.AccountId
            || !string.Equals(request.DisplayName, bundle.Identity.DisplayName, StringComparison.Ordinal)
            || request.CreatedAtUnixMilliseconds != bundle.Identity.CreatedAt.ToUnixTimeMilliseconds()
            || request.FromUnixMilliseconds != bundle.Head.LastSimulatedAt.ToUnixTimeMilliseconds()
            || !string.Equals(request.HeadChecksum, bundle.Head.Checksum, StringComparison.Ordinal)
            || !string.Equals(request.HeadGameDataFingerprint, bundle.Head.GameDataFingerprint, StringComparison.Ordinal)
            || request.Sections.Count != bundle.Sections.Count)
            throw new ComputeSourceDriftException("Compute 完整源基线与任务冻结输入不一致。");

        var expected = bundle.Sections.Values
            .OrderBy(section => section.SectionKind, StringComparer.Ordinal)
            .Select(ComputeSectionCodec.ToWire)
            .ToArray();
        if (request.AssetBalances is { Count: > 0 } requestBalances)
        {
            if (requestBalances.Any(balance => balance.Version < 0 || !double.IsFinite(balance.Quantity) || balance.Quantity <= 0))
                throw new InvalidDataException("Compute 冻结钱包字段无效。");
        }
        var actual = request.Sections
            .OrderBy(section => section.Kind, StringComparer.Ordinal)
            .ToArray();
        if (expected.Length != actual.Length)
            throw new ComputeSourceDriftException("Compute 冻结 section 数量已漂移。");
        for (var i = 0; i < expected.Length; i++)
        {
            if (!string.Equals(expected[i].Kind, actual[i].Kind, StringComparison.Ordinal)
                || expected[i].Version != actual[i].Version
                || !string.Equals(expected[i].Codec, actual[i].Codec, StringComparison.Ordinal)
                || !string.Equals(expected[i].PayloadBase64, actual[i].PayloadBase64, StringComparison.Ordinal)
                || !string.Equals(expected[i].Checksum, actual[i].Checksum, StringComparison.OrdinalIgnoreCase))
                throw new ComputeSourceDriftException($"Compute 冻结 section {actual[i].Kind} 已漂移。");
        }

        var balances = request.AssetBalances ?? [];
        var balancesByKey = new Dictionary<(string AssetId, string Variant), ComputeAssetBalance>(balances.Count);
        foreach (var balance in balances)
        {
            if (!balancesByKey.TryAdd((balance.AssetId, balance.Variant), balance))
                throw new InvalidDataException("Compute 冻结钱包存在重复资产键。");
        }
        return balancesByKey;
    }

    /// <summary>失败状态真实落库后记录终态证据；预期串行化重试与重复故障节流不得压掉判死事件。</summary>
    internal async Task<bool> FailWithDiagnosticsAsync(
        ComputeJobClaim claim, string category, string error, bool retryable,
        TimeSpan retryAfter, CancellationToken cancellationToken)
    {
        var updated = await jobs.FailAsync(claim.JobId, claim.LeaseToken, error,
            retryable, retryAfter, options.ComputeMaxAttempts, cancellationToken);
        if (updated && (!retryable || claim.Attempts >= options.ComputeMaxAttempts))
            LogTerminalFailure(claim, category);
        return updated;
    }

    /// <summary>只记录固定错误类别与任务元信息，不输出节点载荷、异常正文或凭据。</summary>
    internal void LogTerminalFailure(ComputeJobClaim claim, string category) =>
        logger.LogWarning("冷挂机任务判死 job={JobId} category={Category} attempts={Attempts} maxAttempts={MaxAttempts}",
            claim.JobId, category, claim.Attempts, options.ComputeMaxAttempts);

    /// <summary>PostgreSQL 40001 与死锁 40P01 判定：两类都是可原地重放的事务并发失败。</summary>
    internal static bool IsSerializationConflict(PostgresException exception) =>
        exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;

    /// <summary>可重试失败的指数退避：1s 起翻倍，封顶 300s；派发链与本地执行器共用同一口径。</summary>
    internal static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(Math.Max(attempts - 1, 0), 8))));

    /// <summary>独立重演已通过，但权威源状态在任务冻结之后合法变化。</summary>
    private sealed class ComputeSourceDriftException(string message) : Exception(message);
}
