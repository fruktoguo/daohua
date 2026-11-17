namespace Server.Clean;

/// <summary>
/// 只记录需要跨重连去重的低频写命令。普通读取、心跳和实时帧不进入该表，
/// 过期记录由清理 worker 删除，避免把幂等机制变成高频日志系统。
/// </summary>
public sealed class PostgresCommandReceiptRepository(NpgsqlDataSource dataSource) : ICommandReceiptStore
{
    /// <summary>查找未过期回执；命中说明该 RequestId 已执行过，调用方应走幂等重放路径而非再执行。</summary>
    public async ValueTask<CommandReceipt?> FindAsync(
        Guid playerId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        if (playerId == Guid.Empty || requestId == Guid.Empty) return null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT player_id, request_id, command, payload_hash, state_version, created_at, expires_at
            FROM platform.command_receipts
            WHERE player_id = $1 AND request_id = $2 AND expires_at > now()
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = requestId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CommandReceipt(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6))
            : null;
    }

    /// <summary>分批删除 expires_at 已过的回执行（单批至多 5000 行，单轮最多 20 批），避免积压期长时间持表锁。</summary>
    public async ValueTask<int> PruneExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        const int batchSize = 5_000;
        const int maxPasses = 20;
        var totalDeleted = 0;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        for (var pass = 0; pass < maxPasses; pass++)
        {
            await using var command = new NpgsqlCommand("""
                WITH doomed AS (
                    SELECT player_id, request_id
                    FROM platform.command_receipts
                    WHERE expires_at <= $1
                    ORDER BY expires_at
                    LIMIT $2
                )
                DELETE FROM platform.command_receipts r
                USING doomed
                WHERE r.player_id = doomed.player_id AND r.request_id = doomed.request_id
                """, connection);
            command.Parameters.Add(new NpgsqlParameter { Value = now });
            command.Parameters.Add(new NpgsqlParameter { Value = batchSize });
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            totalDeleted += deleted;
            if (deleted < batchSize) break;
        }
        return totalDeleted;
    }
}

/// <summary>周期清理过期命令回执的后台任务（每 10 分钟一批）；单轮失败只记日志，下轮重试。</summary>
public sealed class CommandReceiptCleanupWorker(
    ICommandReceiptStore receipts,
    ILogger<CommandReceiptCleanupWorker> logger,
    ServerOptions? options = null) : BackgroundService
{
    /// <summary>清理主循环：每 10 分钟删除一次过期回执；单轮失败只记日志，下个周期重试，停机令牌取消即退出。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 独立持久化 worker 不读写命令凭证；API 按相同周期负责其完整留存。
        if (options?.ProcessRole == ServerProcessRole.PersistenceWorker) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await HostedLoopSupport.WaitNextTickQuietlyAsync(timer, stoppingToken))
        {
            try
            {
                var deleted = await receipts.PruneExpiredAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (deleted > 0) logger.LogInformation("已清理过期命令回执 count={Count}", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "命令回执清理失败。"); }
        }
    }
}
