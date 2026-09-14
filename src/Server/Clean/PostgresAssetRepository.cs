namespace Server.Clean;

/// <summary>资产余额只读投影。它不参与任何结算写入，避免把查询与事务职责混在一起。</summary>
public sealed class PostgresAssetRepository(NpgsqlDataSource dataSource) : IAssetBalanceRepository
{
    /// <summary>
    /// 加载BalancesAsync（异步）。
    /// </summary>
    /// <param name="playerId">玩家唯一标识。</param>
    /// <param name="cancellationToken">取消操作的标记。</param>
    /// <returns>异步操作结果，包含 IReadOnlyList&lt;AssetBalanceRecord。</returns>
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
