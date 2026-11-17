namespace Server.Clean;

/// <summary>
/// PostgreSQL 玩家运行时租约。路由能力尚未启用时，租约保证同一玩家最多只有一个实例可写；
/// 租约丢失后请求 fail-closed，避免两个实例同时推进同一个聚合。
/// </summary>
public sealed class PostgresPlayerOwnershipRepository(NpgsqlDataSource dataSource) : IPlayerOwnershipStore
{
    /// <summary>抢占玩家租约：UPSERT 仅在租约过期或已属本实例时生效；被其他实例持有时返回 false。</summary>
    public async ValueTask<bool> AcquireAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        Validate(playerId, ownerInstanceId, lease);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockPlayerIdentityAsync(connection, transaction, playerId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH stamp AS MATERIALIZED (SELECT clock_timestamp() AS checked_at)
            INSERT INTO game.player_runtime_leases(player_id, owner_instance_id, lease_until, renewed_at)
            SELECT $1, $2, checked_at + $3, checked_at FROM stamp
            ON CONFLICT (player_id) DO UPDATE
            SET owner_instance_id = EXCLUDED.owner_instance_id,
                lease_until = EXCLUDED.lease_until,
                renewed_at = EXCLUDED.renewed_at
            WHERE game.player_runtime_leases.lease_until <= EXCLUDED.renewed_at
               OR game.player_runtime_leases.owner_instance_id = $2
            RETURNING owner_instance_id
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = ownerInstanceId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Interval, Value = lease });
        var acquired = await command.ExecuteScalarAsync(cancellationToken) is Guid owner && owner == ownerInstanceId;
        await transaction.CommitAsync(cancellationToken);
        return acquired;
    }

    /// <summary>持有者续租；返回 false 表示租约已过期或已被其他实例接管，调用方必须按租约丢失处理并淘汰内存陈旧状态。</summary>
    public async ValueTask<bool> RenewAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        Validate(playerId, ownerInstanceId, lease);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockPlayerIdentityAsync(connection, transaction, playerId, cancellationToken);
        // 只有拿到身份和租约行锁之后才取数据库墙钟；排队期间到期的旧实体不能被续活。
        await using var command = new NpgsqlCommand("""
            WITH locked AS MATERIALIZED (
                SELECT player_id, lease_until FROM game.player_runtime_leases
                WHERE player_id = $1 AND owner_instance_id = $2 FOR UPDATE
            ), stamp AS MATERIALIZED (
                SELECT player_id, lease_until, clock_timestamp() AS checked_at FROM locked
            )
            UPDATE game.player_runtime_leases lease
            SET lease_until = stamp.checked_at + $3, renewed_at = stamp.checked_at
            FROM stamp
            WHERE lease.player_id = stamp.player_id AND lease.owner_instance_id = $2
              AND stamp.lease_until > stamp.checked_at
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = ownerInstanceId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Interval, Value = lease });
        var renewed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return renewed;
    }

    /// <summary>主动释放租约；ownerInstanceId 谓词防止误删已被其他实例接管的新租约。</summary>
    public async ValueTask ReleaseAsync(Guid playerId, Guid ownerInstanceId, CancellationToken cancellationToken = default)
    {
        if (playerId == Guid.Empty || ownerInstanceId == Guid.Empty) return;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockPlayerIdentityAsync(connection, transaction, playerId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM game.player_runtime_leases
            WHERE player_id = $1 AND owner_instance_id = $2
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = ownerInstanceId });
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 所有权操作与 Compute 共用身份行 fence。等值 UPDATE 不改业务身份，却推进 MVCC 行版本：
    /// Compute 的 RepeatableRead 快照若在此更新前建立且等待该行，必须触发 40001 重演，不能读取旧的不存在租约谓词。
    /// </summary>
    internal static async ValueTask LockPlayerIdentityAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid playerId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE identity.players SET status = status
            WHERE player_id = $1 AND status = 1
            RETURNING player_id
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        if (await command.ExecuteScalarAsync(cancellationToken) is not Guid)
            throw new InvalidDataException("玩家身份不存在或已停用，不能更改运行时所有权。");
    }

    private static void Validate(Guid playerId, Guid ownerInstanceId, TimeSpan lease)
    {
        if (playerId == Guid.Empty) throw new ArgumentException("playerId 不能为空。", nameof(playerId));
        if (ownerInstanceId == Guid.Empty) throw new ArgumentException("ownerInstanceId 不能为空。", nameof(ownerInstanceId));
        if (lease < TimeSpan.FromSeconds(5) || lease > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(lease));
    }
}
