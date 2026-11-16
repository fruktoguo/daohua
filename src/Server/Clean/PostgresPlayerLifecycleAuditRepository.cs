namespace Server.Clean;

/// <summary>
/// 玩家上线与离线生命周期审计存储边界（白名单第 3 类）。
/// 仅在连接接入转 Online（<c>player.online</c>）与真实卸载下线（<c>player.offline</c>）时写入，严禁按心跳或模拟 Tick 写入。
/// </summary>
public interface IPlayerLifecycleAuditStore
{
    /// <summary>记录角色上线审计事件。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="accountId">所属账号 ID。</param>
    /// <param name="displayName">角色当前道号。</param>
    /// <param name="nodeId">上线时所在世界节点 ID。</param>
    /// <param name="occurredAt">发生时刻（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask RecordOnlineAsync(Guid playerId, Guid accountId, string displayName, string? nodeId,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default);

    /// <summary>记录角色离线审计事件。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="accountId">所属账号 ID。</param>
    /// <param name="displayName">角色当前道号。</param>
    /// <param name="nodeId">离线时所在世界节点 ID。</param>
    /// <param name="occurredAt">离线时刻（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask RecordOfflineAsync(Guid playerId, Guid accountId, string displayName, string? nodeId,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
}

/// <summary>基于 PostgreSQL <c>identity.audit_events</c> 的玩家上线/离线白名单审计仓储。</summary>
public sealed class PostgresPlayerLifecycleAuditRepository(NpgsqlDataSource dataSource) : IPlayerLifecycleAuditStore
{
    /// <inheritdoc />
    public ValueTask RecordOnlineAsync(Guid playerId, Guid accountId, string displayName, string? nodeId,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
        => InsertAsync(playerId, accountId, "player.online", displayName, nodeId, occurredAt, cancellationToken);

    /// <inheritdoc />
    public ValueTask RecordOfflineAsync(Guid playerId, Guid accountId, string displayName, string? nodeId,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
        => InsertAsync(playerId, accountId, "player.offline", displayName, nodeId, occurredAt, cancellationToken);

    private async ValueTask InsertAsync(Guid playerId, Guid accountId, string eventType,
        string displayName, string? nodeId, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            displayName = displayName ?? string.Empty,
            nodeId = nodeId ?? string.Empty
        });
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            INSERT INTO identity.audit_events (event_id, actor_id, event_type, target_id, payload, occurred_at)
            VALUES (
                $1,
                $2,
                $3,
                COALESCE($4, (SELECT p.account_id FROM identity.players p WHERE p.player_id = $2)),
                $5::jsonb,
                $6)
            ON CONFLICT (event_id) DO NOTHING
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = eventType });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = accountId == Guid.Empty ? DBNull.Value : accountId
        });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = payload });
        command.Parameters.Add(new NpgsqlParameter { Value = occurredAt });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
