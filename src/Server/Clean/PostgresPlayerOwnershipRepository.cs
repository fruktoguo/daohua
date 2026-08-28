namespace Server.Clean;

/// <summary>
/// PostgreSQL 玩家运行时租约。路由能力尚未启用时，租约保证同一玩家最多只有一个实例可写；
/// 租约丢失后请求 fail-closed，避免两个实例同时推进同一个聚合。
/// </summary>
public sealed class PostgresPlayerOwnershipRepository(NpgsqlDataSource dataSource) : IPlayerOwnershipStore
{
    public async ValueTask<bool> AcquireAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        Validate(playerId, ownerInstanceId, lease);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO game.player_runtime_leases(player_id, owner_instance_id, lease_until, renewed_at)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (player_id) DO UPDATE
            SET owner_instance_id = EXCLUDED.owner_instance_id,
                lease_until = EXCLUDED.lease_until,
                renewed_at = EXCLUDED.renewed_at
            WHERE game.player_runtime_leases.lease_until <= $4
               OR game.player_runtime_leases.owner_instance_id = $2
            RETURNING owner_instance_id
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = ownerInstanceId });
        command.Parameters.Add(new NpgsqlParameter { Value = now.Add(lease) });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid owner && owner == ownerInstanceId;
    }

    public async ValueTask<bool> RenewAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        // ownerInstanceId 每次进程启动都会重新生成；即使本机时钟跳变导致 lease_until 已过期，
        // 只要期间没有其他实例接管（ownerInstanceId 仍匹配），当前实例可以原子续租恢复。
        Validate(playerId, ownerInstanceId, lease);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE game.player_runtime_leases
            SET lease_until = $1, renewed_at = $2
            WHERE player_id = $3 AND owner_instance_id = $4
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = now.Add(lease) });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = ownerInstanceId });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask ReleaseAsync(Guid playerId, Guid ownerInstanceId, CancellationToken cancellationToken = default)
    {
        if (playerId == Guid.Empty || ownerInstanceId == Guid.Empty) return;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM game.player_runtime_leases
            WHERE player_id = $1 AND owner_instance_id = $2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = ownerInstanceId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Validate(Guid playerId, Guid ownerInstanceId, TimeSpan lease)
    {
        if (playerId == Guid.Empty) throw new ArgumentException("playerId 不能为空。", nameof(playerId));
        if (ownerInstanceId == Guid.Empty) throw new ArgumentException("ownerInstanceId 不能为空。", nameof(ownerInstanceId));
        if (lease < TimeSpan.FromSeconds(5) || lease > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(lease));
    }
}
