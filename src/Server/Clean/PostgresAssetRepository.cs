namespace Server.Clean;

/// <summary>资产余额只读投影。它不参与任何结算写入，避免把查询与事务职责混在一起。</summary>
public sealed class PostgresAssetRepository(NpgsqlDataSource dataSource) : IAssetBalanceRepository
{
    /// <summary>读取玩家全部正余额资产行，按 asset_id/variant 排序；零余额行不回传以压缩快照。</summary>
    public async ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(
        Guid playerId,
        CancellationToken cancellationToken = default)
    {
        if (playerId == Guid.Empty) throw new ArgumentException("PlayerId 不能为空。", nameof(playerId));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT player_id, asset_id, variant, quantity, version
            FROM economy.player_asset_balances
            WHERE player_id = $1 AND quantity > 0
            ORDER BY asset_id, variant
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var balances = new List<AssetBalanceRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            balances.Add(new AssetBalanceRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3),
                reader.GetInt64(4)));
        }

        return balances;
    }
}
