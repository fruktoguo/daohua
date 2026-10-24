namespace Server.Clean;

/// <summary>
/// 默认的集成事件下游：把 outbox 事件归档进 identity.audit_events。
/// 安全/业务审计事件（auth.*）统一在此落库，
/// 返回 true 表示已投递，由 OutboxWorker 标记 delivered。
/// </summary>
public sealed class AuditIntegrationEventDispatcher(
    NpgsqlDataSource dataSource,
    ILogger<AuditIntegrationEventDispatcher> logger) : IIntegrationEventDispatcher
{
    /// <summary>
    /// 把一条 outbox 消息归档进审计表；event_id 冲突视为已归档（幂等）。
    /// 返回 true 表示本通道已处理、OutboxWorker 可标记 delivered；
    /// 返回 false 表示事件类型不属于审计通道，消息保持 pending 等待其他下游。
    /// </summary>
    /// <param name="message">durable outbox 中待投递的一行事件</param>
    /// <param name="cancellationToken">取消标记</param>
    public async ValueTask<bool> DispatchAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var eventType = message.EventType;
        // 审计通道只认领 auth.*：outbox 当前唯一生产者是账号仓储（auth.account.*），
        // player.* 与 market.* 无生产者，不为假想事件预留归档分支。
        var known = eventType.StartsWith("auth.", StringComparison.Ordinal);
        if (!known)
        {
            // 未归档类型不属于审计通道，保持 pending 等待未来下游接管。
            logger.LogDebug("outbox 事件类型无审计归档通道 type={EventType}", eventType);
            return false;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO identity.audit_events (event_id, actor_id, event_type, target_id, payload, occurred_at)
            VALUES ($1, $2, $3, NULL, $4::jsonb, $5)
            ON CONFLICT (event_id) DO NOTHING
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = message.OutboxId });
        command.Parameters.Add(new NpgsqlParameter { Value = message.AggregateId });
        command.Parameters.Add(new NpgsqlParameter { Value = eventType });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb, Value = Encoding.UTF8.GetString(message.Payload) });
        command.Parameters.Add(new NpgsqlParameter { Value = message.OccurredAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }
}
