namespace Server.Clean;

/// <summary>outbox 事件的下游投递边界；返回 false 表示暂不可达，事件保留待重试。</summary>
public interface IIntegrationEventDispatcher
{
    /// <summary>投递单条事件；true 已送达可标记，false 由 worker 稍后重试。</summary>
    ValueTask<bool> DispatchAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}

/// <summary>默认不向外部系统投递；事件保留在 outbox，避免在未配置下游时静默丢失。</summary>
public sealed class UnconfiguredIntegrationEventDispatcher(ILogger<UnconfiguredIntegrationEventDispatcher> logger) : IIntegrationEventDispatcher
{
    private readonly ConcurrentDictionary<string, byte> _warnedEventTypes = new(StringComparer.Ordinal);

    /// <summary>始终返回 false 使事件保持 pending；同类事件只告警一次，避免日志风暴。</summary>
    public ValueTask<bool> DispatchAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        // outbox 是可靠队列，不应因为未配置下游而每轮 poll 重复写 warning；
        // 同一事件类型只告警一次，后续只保留低成本 debug 证据。
        if (_warnedEventTypes.TryAdd(message.EventType, 0))
            logger.LogWarning("未配置 integration event dispatcher，事件保持 pending type={EventType}", message.EventType);
        else
            logger.LogDebug("integration event dispatcher 未配置，事件仍 pending type={EventType}", message.EventType);
        return ValueTask.FromResult(false);
    }
}

/// <summary>platform.integration_outbox 的 PostgreSQL 实现：同事务追加、租约认领与回执。</summary>
public sealed class PostgresOutboxRepository(NpgsqlDataSource dataSource) : IOutboxRepository
{
    /// <summary>在调用方事务内插入 outbox 行；事件随业务提交一起可见，保证不丢。</summary>
    public async ValueTask AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid aggregateId,
        string aggregateType,
        string eventType,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        if (aggregateId == Guid.Empty || string.IsNullOrWhiteSpace(aggregateType) || string.IsNullOrWhiteSpace(eventType) || payload.IsEmpty)
            throw new ArgumentException("Outbox 事件参数无效。");
        await using var command = new NpgsqlCommand("""
            INSERT INTO platform.integration_outbox
                (outbox_id, aggregate_type, aggregate_id, event_type, payload,
                 occurred_at, attempts, visible_at)
            VALUES ($1, $2, $3, $4, $5, $6, 0, $6)
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter { Value = aggregateType });
        command.Parameters.Add(new NpgsqlParameter { Value = aggregateId });
        command.Parameters.Add(new NpgsqlParameter { Value = eventType });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = payload });
        command.Parameters.Add(new NpgsqlParameter { Value = occurredAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 原子认领到期未投递事件：UPDATE ... FROM CTE 加租约并递增 attempts。
    /// SKIP LOCKED 让多个 worker 实例互不抢占；按 occurred_at 排序保证近似 FIFO。
    /// </summary>
    public async ValueTask<IReadOnlyList<OutboxMessage>> ClaimAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        if (maxRows <= 0) return [];
        var now = DateTimeOffset.UtcNow;
        var token = Guid.NewGuid();
        var until = now.Add(lease);
        var result = new List<OutboxMessage>(maxRows);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH candidates AS (
                SELECT outbox_id FROM platform.integration_outbox
                WHERE delivered_at IS NULL AND visible_at <= $1
                  AND (lease_until IS NULL OR lease_until < $1)
                ORDER BY occurred_at
                FOR UPDATE SKIP LOCKED LIMIT $2
            )
            UPDATE platform.integration_outbox o
            SET lease_until = $3, lease_token = $4, attempts = o.attempts + 1
            FROM candidates c
            WHERE o.outbox_id = c.outbox_id
            RETURNING o.outbox_id, o.aggregate_type, o.aggregate_id, o.event_type,
                      o.payload, o.occurred_at, o.attempts, o.visible_at
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        command.Parameters.Add(new NpgsqlParameter { Value = maxRows });
        command.Parameters.Add(new NpgsqlParameter { Value = until });
        command.Parameters.Add(new NpgsqlParameter { Value = token });
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new OutboxMessage(
                    reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3), reader.GetFieldValue<byte[]>(4),
                    reader.GetFieldValue<DateTimeOffset>(5), reader.GetInt32(6), reader.GetFieldValue<DateTimeOffset>(7), token));
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>标记已送达并清租约；lease_token 谓词保证只认本批认领的写者。</summary>
    public async ValueTask MarkDeliveredAsync(Guid outboxId, Guid leaseToken, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE platform.integration_outbox
            SET delivered_at = now(), lease_until = NULL, lease_token = NULL, last_error = NULL
            WHERE outbox_id = $1 AND lease_token = $2 AND delivered_at IS NULL
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = outboxId });
        command.Parameters.Add(new NpgsqlParameter { Value = leaseToken });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>投递失败回租：清租约、记 last_error，并按 retryAfter 推迟 visible_at 形成退避。</summary>
    public async ValueTask ReleaseAsync(Guid outboxId, Guid leaseToken, string error, TimeSpan retryAfter, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE platform.integration_outbox
            SET visible_at = $3, lease_until = NULL, lease_token = NULL, last_error = left($4, 512)
            WHERE outbox_id = $1 AND lease_token = $2 AND delivered_at IS NULL
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = outboxId });
        command.Parameters.Add(new NpgsqlParameter { Value = leaseToken });
        command.Parameters.Add(new NpgsqlParameter { Value = DateTimeOffset.UtcNow.Add(retryAfter) });
        command.Parameters.Add(new NpgsqlParameter { Value = error ?? "unknown" });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// outbox 轮询派发后台任务：租约认领 → 逐条投递 → 成功标记/失败退避回租。
/// 单条投递失败指数退避（封顶 5 分钟），循环级异常经节流后记录，不让故障静默。
/// </summary>
public sealed class OutboxWorker(
    IOutboxRepository repository,
    IIntegrationEventDispatcher dispatcher,
    ServerOptions options,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private readonly LogThrottle _failureLog = new(TimeSpan.FromMinutes(1));

    /// <summary>
    /// 派发主循环：按 OutboxPollInterval 轮询租约认领一批事件并逐条投递——成功标记 delivered，
    /// 调度器不可用按固定 1 分钟回租，单条异常按 attempts 指数退避（封顶 5 分钟）回租；
    /// 循环级异常经节流记录后进入下一轮，停机令牌取消即退出。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxWorker started batch={Batch} interval={Interval}.", options.OutboxBatchSize, options.OutboxPollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await repository.ClaimAsync(options.OutboxBatchSize, Lease, stoppingToken);
                foreach (var message in messages)
                {
                    try
                    {
                        if (await dispatcher.DispatchAsync(message, stoppingToken))
                            await repository.MarkDeliveredAsync(message.OutboxId, message.LeaseToken, stoppingToken);
                        else
                            await repository.ReleaseAsync(message.OutboxId, message.LeaseToken, "dispatcher unavailable", TimeSpan.FromMinutes(1), stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (_failureLog.TryAcquire())
                            logger.LogError(ex, "Outbox 事件投递失败 outboxId={OutboxId}", message.OutboxId);
                        await repository.ReleaseAsync(message.OutboxId, message.LeaseToken, ex.Message, TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(message.Attempts, 8)))), stoppingToken);
                    }
                }
                await Task.Delay(options.OutboxPollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_failureLog.TryAcquire()) logger.LogError(ex, "OutboxWorker 循环失败。");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
