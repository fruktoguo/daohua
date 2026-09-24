namespace Server.Clean;

/// <summary>冷挂机任务入队结果。</summary>
public enum ComputeEnqueueResult : byte
{
    /// <summary>新任务行已插入。</summary>
    Inserted,
    /// <summary>已存在 pending 行，目标时间与输入载荷已就地刷新。</summary>
    Refreshed,
    /// <summary>已存在 leased/终态同键行，本次入队被跳过。</summary>
    SkippedExisting
}

/// <summary>派发器领取到的一条冷挂机任务。</summary>
/// <param name="JobId">任务唯一标识。</param>
/// <param name="PlayerId">玩家唯一标识。</param>
/// <param name="TargetTime">目标模拟时刻。</param>
/// <param name="InputPayload">序列化的 compute.v1 任务请求。</param>
/// <param name="Attempts">已领取次数（含本次）。</param>
/// <param name="LeaseToken">本批租约令牌。</param>
public sealed record ComputeJobClaim(
    Guid JobId,
    Guid PlayerId,
    DateTimeOffset TargetTime,
    byte[] InputPayload,
    int Attempts,
    Guid LeaseToken);

/// <summary>冷挂机扫描选中的离线玩家候选。</summary>
/// <param name="PlayerId">玩家唯一标识。</param>
/// <param name="LastActiveAt">投影中的最近活跃时刻。</param>
/// <param name="LastSimulatedAt">提交头中的已结算模拟时刻；扫描 SQL 的窗口过滤口径。</param>
public sealed record ColdPlayerCandidate(Guid PlayerId, DateTimeOffset LastActiveAt, DateTimeOffset LastSimulatedAt);

/// <summary>
/// compute_jobs.output_payload 的持久化口径：完结任务的摘要证据。
/// 不再整存节点 result JSON（其中 sections 含全量 base64 载荷，单任务存储放大 ~2.3 倍）；
/// 只保留 jobId/玩家/版本对/应用结局/推进指标与错误信息，诊断口径不变。
/// </summary>
/// <param name="JobId">任务唯一标识。</param>
/// <param name="PlayerId">玩家唯一标识。</param>
/// <param name="SourceStateVersion">任务发起时的提交头版本。</param>
/// <param name="TargetStateVersion">推进后的目标版本。</param>
/// <param name="Outcome">结果落库结局（Applied/StaleVersion/MissingPlayer）。</param>
/// <param name="AssetDeltaCount">随结果入账的资产增量行数（compute.v2；空推进或被丢弃时为 0）。</param>
/// <param name="Metrics">节点回传的推进计算指标。</param>
/// <param name="ErrorCode">节点回传的错误码（成功结果通常为 null）。</param>
/// <param name="ErrorMessage">节点回传的错误消息（成功结果通常为 null）。</param>
public sealed record ComputeJobOutputSummary(
    Guid JobId,
    Guid PlayerId,
    long SourceStateVersion,
    long TargetStateVersion,
    string Outcome,
    int AssetDeltaCount,
    ComputeAdvanceMetrics? Metrics,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// platform.compute_jobs 的 PostgreSQL 实现：冷挂机任务的生产、租约领取与终态回写。
/// 单开任务约束由部分唯一索引 ux_compute_jobs_open 保证；终态行不参与唯一约束，
/// 保留诊断窗口后由 retention worker 按 status+updated_at 清理。
/// </summary>
public sealed class PostgresComputeJobRepository(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// 由玩家与目标时刻派生确定性 job_id。
    /// 同一玩家同一目标时刻的重复入队在主键上自然去重；目标时刻变化后派生新 id，不与历史终态行冲突。
    /// </summary>
    public static Guid DeriveJobId(Guid playerId, long targetUnixMilliseconds)
    {
        Span<byte> buffer = stackalloc byte[24];
        playerId.TryWriteBytes(buffer);
        BitConverter.TryWriteBytes(buffer[16..], targetUnixMilliseconds);
        var bytes = SHA256.HashData(buffer).AsSpan(0, 16).ToArray();
        // 打上 v5 风格 version/variant 位，仅作语义标识，不影响唯一性。
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    /// <summary>
    /// 入队一个冷挂机任务。同一玩家已有 pending 行时就地刷新目标时间与载荷；
    /// 已有 leased（在飞或失联租约）行时不打扰，等待派发器重租或完成；
    /// job_id 撞上终态历史行时视为重复入队直接跳过。
    /// </summary>
    public async ValueTask<ComputeEnqueueResult> EnqueueAsync(
        Guid playerId,
        DateTimeOffset targetTime,
        ReadOnlyMemory<byte> inputPayload,
        CancellationToken cancellationToken = default)
    {
        if (playerId == Guid.Empty || inputPayload.IsEmpty || inputPayload.Length > 4 * 1024 * 1024)
            throw new ArgumentException("冷挂机任务入队参数无效。");
        var jobId = DeriveJobId(playerId, targetTime.ToUnixTimeMilliseconds());
        var now = DateTimeOffset.UtcNow;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 同一确定性 job_id 若已存在（终态历史行或他人并发插入），视为等价任务已登记，直接跳过；
        // 必须先于 pending 刷新检查，否则刷新 job_id 时会与终态行撞主键。
        await using (var exists = new NpgsqlCommand("""
            SELECT 1 FROM platform.compute_jobs WHERE job_id = $1
            """, connection, transaction))
        {
            exists.Parameters.Add(new NpgsqlParameter { Value = jobId });
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return ComputeEnqueueResult.SkippedExisting;
            }
        }

        // 已存在的 pending 行直接刷新为最新输入；job_id 同步替换保持列与载荷一致。
        await using (var refresh = new NpgsqlCommand("""
            UPDATE platform.compute_jobs
            SET job_id = $2, target_time = $3, input_payload = $4, updated_at = $5
            WHERE player_id = $1 AND status = 'pending'
            """, connection, transaction))
        {
            refresh.Parameters.Add(new NpgsqlParameter { Value = playerId });
            refresh.Parameters.Add(new NpgsqlParameter { Value = jobId });
            refresh.Parameters.Add(new NpgsqlParameter { Value = targetTime });
            refresh.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = inputPayload });
            refresh.Parameters.Add(new NpgsqlParameter { Value = now });
            if (await refresh.ExecuteNonQueryAsync(cancellationToken) > 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return ComputeEnqueueResult.Refreshed;
            }
        }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO platform.compute_jobs
                (job_id, player_id, target_time, status, input_payload, output_payload,
                 lease_until, lease_token, next_attempt_at, last_error, attempts, updated_at, created_at)
            VALUES ($1, $2, $3, 'pending', $4, NULL, NULL, NULL, NULL, NULL, 0, $5, $5)
            ON CONFLICT (player_id) WHERE status IN ('pending', 'leased') DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = jobId });
            insert.Parameters.Add(new NpgsqlParameter { Value = playerId });
            insert.Parameters.Add(new NpgsqlParameter { Value = targetTime });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = inputPayload });
            insert.Parameters.Add(new NpgsqlParameter { Value = now });
            int inserted;
            try
            {
                inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // 并发事务在同一快照检查后插入了相同 job_id/玩家未完结行：按已存在处理。
                await transaction.RollbackAsync(CancellationToken.None);
                return ComputeEnqueueResult.SkippedExisting;
            }
            await transaction.CommitAsync(cancellationToken);
            // 命中部分唯一索引（该玩家已有 pending/leased 行）时 DO NOTHING 返回 0。
            return inserted > 0 ? ComputeEnqueueResult.Inserted : ComputeEnqueueResult.SkippedExisting;
        }
    }

    /// <summary>
    /// 原子领取到期任务：pending 且到点且过重试闸门的行，或租约已过期的 leased 孤儿行。
    /// </summary>
    public async ValueTask<IReadOnlyList<ComputeJobClaim>> ClaimDueAsync(
        int maxRows,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        if (maxRows <= 0) return [];
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(lease);
        var leaseToken = Guid.NewGuid();
        var claimed = new List<ComputeJobClaim>(maxRows);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH candidates AS (
                SELECT job_id
                FROM platform.compute_jobs
                WHERE (status = 'pending' AND target_time <= $1
                       AND (next_attempt_at IS NULL OR next_attempt_at <= $1))
                   OR (status = 'leased' AND lease_until < $1)
                ORDER BY target_time
                FOR UPDATE SKIP LOCKED
                LIMIT $2
            )
            UPDATE platform.compute_jobs AS j
            SET status = 'leased', lease_until = $3, lease_token = $4,
                attempts = j.attempts + 1, updated_at = $1
            FROM candidates AS c
            WHERE j.job_id = c.job_id
            RETURNING j.job_id, j.player_id, j.target_time, j.input_payload, j.attempts
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        command.Parameters.Add(new NpgsqlParameter { Value = maxRows });
        command.Parameters.Add(new NpgsqlParameter { Value = leaseUntil });
        command.Parameters.Add(new NpgsqlParameter { Value = leaseToken });
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(new ComputeJobClaim(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetFieldValue<DateTimeOffset>(2),
                    reader.GetFieldValue<byte[]>(3),
                    reader.GetInt32(4),
                    leaseToken));
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    /// <summary>任务完结 UPDATE 的唯一口径：自动提交与并入调用方事务两条路径共用。</summary>
    private const string CompleteSql = """
        UPDATE platform.compute_jobs
        SET status = 'succeeded', output_payload = $3,
            lease_until = NULL, lease_token = NULL, last_error = NULL, updated_at = now()
        WHERE job_id = $1 AND lease_token = $2 AND status = 'leased'
        """;

    /// <summary>标记任务成功并保存节点输出摘要；租约令牌不匹配时返回 false。</summary>
    public async ValueTask<bool> CompleteAsync(
        Guid jobId,
        Guid leaseToken,
        ReadOnlyMemory<byte> outputPayload,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(CompleteSql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = jobId });
        command.Parameters.Add(new NpgsqlParameter { Value = leaseToken });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = outputPayload });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// 在调用方事务内标记任务成功；供 <see cref="ComputeDispatcherWorker"/> 把
    /// 「section 落库 + 任务完结」并入同一事务提交。租约令牌不匹配返回 false。
    /// </summary>
    internal static async ValueTask<bool> CompleteInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        Guid leaseToken,
        ReadOnlyMemory<byte> outputPayload,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CompleteSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = jobId });
        command.Parameters.Add(new NpgsqlParameter { Value = leaseToken });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = outputPayload });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// 标记任务失败。可重试时退回 pending 并按 retryAfter 设置重试闸门；
    /// 不可重试或领取次数已达上限时进入 dead 终态保留诊断窗口。
    /// </summary>
    public async ValueTask<bool> FailAsync(
        Guid jobId,
        Guid leaseToken,
        string error,
        bool retryable,
        TimeSpan retryAfter,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        var nextAttempt = DateTimeOffset.UtcNow.Add(retryAfter);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE platform.compute_jobs
            SET status = CASE WHEN $4 OR attempts >= $5 THEN 'dead' ELSE 'pending' END,
                next_attempt_at = CASE WHEN $4 OR attempts >= $5 THEN next_attempt_at ELSE $6 END,
                lease_until = NULL, lease_token = NULL,
                last_error = left($3, 512), updated_at = now()
            WHERE job_id = $1 AND lease_token = $2 AND status = 'leased'
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = jobId });
        command.Parameters.Add(new NpgsqlParameter { Value = leaseToken });
        command.Parameters.Add(new NpgsqlParameter { Value = error ?? "unknown" });
        command.Parameters.Add(new NpgsqlParameter { Value = !retryable });
        command.Parameters.Add(new NpgsqlParameter { Value = maxAttempts });
        command.Parameters.Add(new NpgsqlParameter { Value = nextAttempt });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// 按租约令牌释放一批指定任务：链路层失败时令牌下这些 leased 行退回 pending；
    /// 领取次数已达上限的行直接进 dead，避免坏任务被无限重取。
    /// </summary>
    /// <param name="leaseToken">本批租约令牌。</param>
    /// <param name="jobIds">待释放的任务集合；谓词仍带 lease_token 约束，防止误放他人行。</param>
    /// <param name="error">诊断错误信息。</param>
    /// <param name="retryAfter">本组任务的统一退避时长；不同 attempts 的任务由调用方分组后分别调用。</param>
    /// <param name="maxAttempts">最大领取次数。</param>
    public async ValueTask<int> ReleaseLeaseAsync(
        Guid leaseToken,
        IReadOnlyCollection<Guid> jobIds,
        string error,
        TimeSpan retryAfter,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        var nextAttempt = DateTimeOffset.UtcNow.Add(retryAfter);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE platform.compute_jobs
            SET status = CASE WHEN attempts >= $3 THEN 'dead' ELSE 'pending' END,
                next_attempt_at = CASE WHEN attempts >= $3 THEN next_attempt_at ELSE $4 END,
                lease_until = NULL, lease_token = NULL,
                last_error = left($2, 512), updated_at = now()
            WHERE lease_token = $1 AND status = 'leased' AND job_id = ANY($5)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = leaseToken });
        command.Parameters.Add(new NpgsqlParameter { Value = error ?? "unknown" });
        command.Parameters.Add(new NpgsqlParameter { Value = maxAttempts });
        command.Parameters.Add(new NpgsqlParameter { Value = nextAttempt });
        command.Parameters.Add(new NpgsqlParameter { Value = jobIds.ToArray() });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 从读模型投影筛选冷挂机候选：离线、最近活跃在封顶窗口内、已过断线宽限、
    /// 未被任何运行时实例持有、且当前没有未完结任务。
    /// JOIN player_state_heads 把 last_simulated_at 窗口过滤前移到扫描 SQL：
    /// 只有提交头模拟欠债落在 [now-window, now-minIdle] 内的玩家才需要全量状态加载，
    /// 已在收益封顶窗外或不足最小步长的玩家在扫描侧直接排除，不再为丢弃做全量 Load。
    /// </summary>
    public async ValueTask<IReadOnlyList<ColdPlayerCandidate>> ScanColdPlayersAsync(
        TimeSpan minIdle,
        TimeSpan window,
        int maxRows,
        CancellationToken cancellationToken = default)
    {
        if (maxRows <= 0) return [];
        var now = DateTimeOffset.UtcNow;
        var result = new List<ColdPlayerCandidate>(maxRows);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT p.player_id, p.last_active_at, h.last_simulated_at
            FROM game.player_projection p
            JOIN game.player_state_heads h ON h.player_id = p.player_id
            WHERE p.presence = 'offline'
              AND p.last_active_at >= $2
              AND p.last_active_at <= $3
              AND h.last_simulated_at >= $2
              AND h.last_simulated_at <= $3
              AND NOT EXISTS (
                  SELECT 1 FROM game.player_runtime_leases l
                  WHERE l.player_id = p.player_id AND l.lease_until > $1)
              AND NOT EXISTS (
                  SELECT 1 FROM platform.compute_jobs j
                  WHERE j.player_id = p.player_id AND j.status IN ('pending', 'leased'))
            ORDER BY h.last_simulated_at
            LIMIT $4
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        command.Parameters.Add(new NpgsqlParameter { Value = now - window });
        command.Parameters.Add(new NpgsqlParameter { Value = now - minIdle });
        command.Parameters.Add(new NpgsqlParameter { Value = maxRows });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ColdPlayerCandidate(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetFieldValue<DateTimeOffset>(2)));
        }
        return result;
    }
}
