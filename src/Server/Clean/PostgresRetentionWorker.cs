namespace Server.Clean;

/// <summary>
/// 只清理可重建或已过期的运行记录，避免日志、回执和冷挂机结果在小硬盘上无界增长。
/// 资产余额、订单、成交和未投递 outbox 永不由该 worker 删除；权威流水归档由备份/归档流程负责。
/// </summary>
public sealed class PostgresRetentionRepository(
    NpgsqlDataSource dataSource,
    ServerOptions options,
    ILogger<PostgresRetentionRepository> logger)
{
    public async ValueTask<RetentionResult> PruneAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var outboxBefore = now - options.DeliveredOutboxRetention;
        var sessionsBefore = now - options.SessionRetention;
        var auditBefore = now - options.AuditRetention;
        var computeBefore = now - options.ComputeJobRetention;
        var keysBefore = now - options.DataProtectionKeyRetention;
        var claimsBefore = now - options.SettlementClaimRetention;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var deletedOutbox = await DeleteBatchAsync(connection, transaction, """
            SELECT outbox_id
            FROM platform.integration_outbox
            WHERE delivered_at IS NOT NULL AND delivered_at < $1
            ORDER BY delivered_at
            LIMIT $2
            """, "DELETE FROM platform.integration_outbox o USING ({0}) doomed WHERE o.outbox_id = doomed.outbox_id", outboxBefore, cancellationToken);
        var deletedSessions = await DeleteBatchAsync(connection, transaction, """
            SELECT session_id
            FROM identity.auth_sessions
            WHERE (revoked_at IS NOT NULL AND revoked_at < $1)
               OR expires_at < $1
            ORDER BY expires_at
            LIMIT $2
            """, "DELETE FROM identity.auth_sessions s USING ({0}) doomed WHERE s.session_id = doomed.session_id", sessionsBefore, cancellationToken);
        var deletedAudit = await DeleteBatchAsync(connection, transaction, """
            SELECT event_id
            FROM identity.audit_events
            WHERE occurred_at < $1
            ORDER BY occurred_at
            LIMIT $2
            """, "DELETE FROM identity.audit_events e USING ({0}) doomed WHERE e.event_id = doomed.event_id", auditBefore, cancellationToken);
        var deletedCompute = await DeleteBatchAsync(connection, transaction, """
            SELECT job_id
            FROM platform.compute_jobs
            WHERE updated_at < $1
              AND status IN ('succeeded', 'failed', 'cancelled')
            ORDER BY updated_at
            LIMIT $2
            """, "DELETE FROM platform.compute_jobs j USING ({0}) doomed WHERE j.job_id = doomed.job_id", computeBefore, cancellationToken);
        var deletedKeys = await DeleteBatchAsync(connection, transaction, """
            SELECT key_id
            FROM identity.data_protection_keys
            WHERE expires_at IS NOT NULL AND expires_at < $1
            ORDER BY expires_at
            LIMIT $2
            """, "DELETE FROM identity.data_protection_keys k USING ({0}) doomed WHERE k.key_id = doomed.key_id", keysBefore, cancellationToken);
        var deletedClaims = await DeleteBatchAsync(connection, transaction, """
            SELECT player_id, request_id
            FROM market.settlement_claims
            WHERE claimed_at < $1
            ORDER BY claimed_at
            LIMIT $2
            """, "DELETE FROM market.settlement_claims c USING ({0}) doomed WHERE c.player_id = doomed.player_id AND c.request_id = doomed.request_id", claimsBefore, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var result = new RetentionResult(deletedOutbox, deletedSessions, deletedAudit, deletedCompute, deletedKeys, deletedClaims);
        if (result.Total > 0)
            logger.LogInformation("已完成低频运行数据清理 outbox={Outbox} sessions={Sessions} audit={Audit} compute={Compute} dataProtectionKeys={DataProtectionKeys} settlementClaims={SettlementClaims}",
                result.Outbox, result.Sessions, result.Audit, result.ComputeJobs, result.DataProtectionKeys, result.SettlementClaims);
        return result;
    }

    private async Task<int> DeleteBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string selectorSql,
        string deleteTemplate,
        DateTimeOffset before,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            deleteTemplate,
            selectorSql), connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = before });
        command.Parameters.Add(new NpgsqlParameter { Value = options.RetentionBatchSize });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed record RetentionResult(
    int Outbox,
    int Sessions,
    int Audit,
    int ComputeJobs,
    int DataProtectionKeys,
    int SettlementClaims)
{
    public int Total => Outbox + Sessions + Audit + ComputeJobs + DataProtectionKeys + SettlementClaims;
}

public sealed class PostgresRetentionWorker(
    PostgresRetentionRepository repository,
    ILogger<PostgresRetentionWorker> logger) : BackgroundService
{
    private readonly LogThrottle _errorLog = new(TimeSpan.FromMinutes(1));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 避开刚启动时的 schema/key-ring 热路径；之后每小时最多做一次小批量删除。
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await repository.PruneAsync(DateTimeOffset.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                if (_errorLog.TryAcquire()) logger.LogError(exception, "低频运行数据清理失败，将在下个周期重试。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
