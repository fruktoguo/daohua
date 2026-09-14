namespace Server.Clean;

/// <summary>
/// 只记录需要跨重连去重的低频写命令。普通读取、心跳和实时帧不进入该表，
/// 过期记录由清理 worker 删除，避免把幂等机制变成高频日志系统。
/// </summary>
public sealed class PostgresCommandReceiptRepository(NpgsqlDataSource dataSource) : ICommandReceiptStore
{
    /// <summary>
    /// 执行FindAsync（异步）。
    /// </summary>
    /// <param name="playerId">玩家唯一标识。</param>
    /// <param name="requestId">请求唯一标识。</param>
    /// <param name="cancellationToken">取消操作的标记。</param>
    /// <returns>异步操作结果，包含 CommandReceipt?。</returns>
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

    /// <summary>
    /// 执行PruneExpiredAsync（异步）。
    /// </summary>
    /// <param name="now">参数 now。</param>
    /// <param name="cancellationToken">取消操作的标记。</param>
    /// <returns>异步操作结果，包含 int。</returns>
    public async ValueTask<int> PruneExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM platform.command_receipts WHERE expires_at <= $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// 表示 CommandReceiptCleanupWorker 类。
/// </summary>
/// <param name="receipts">参数 receipts。</param>
/// <param name="logger">日志记录器。</param>
public sealed class CommandReceiptCleanupWorker(
    ICommandReceiptStore receipts,
    ILogger<CommandReceiptCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
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
