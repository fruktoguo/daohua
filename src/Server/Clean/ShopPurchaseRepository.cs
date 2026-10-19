using Npgsql;

namespace Server.Clean;

/// <summary>系统商店限购配额的只读仓库：按 UTC+8 自然日读取玩家当日已购件数。</summary>
/// <remarks>写入路径只有经济事务（<see cref="PostgresEconomyRepository"/>），本仓库不提供任何写方法。</remarks>
/// <param name="dataSource">PostgreSQL 数据源。</param>
public sealed class ShopPurchaseRepository(NpgsqlDataSource dataSource)
{
    /// <summary>读取指定自然日内各商品的已购件数；未购买过的商品不出现在结果中。</summary>
    /// <param name="playerId">玩家标识。</param>
    /// <param name="dayIndex">UTC+8 自然日序号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>商品标识 → 当日已购件数。</returns>
    public async ValueTask<IReadOnlyDictionary<string, int>> LoadDailyAsync(Guid playerId, long dayIndex,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT offer_id, quantity FROM economy.shop_daily_purchases
            WHERE player_id = $1 AND day_index = $2
            """, connection);
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(dayIndex);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }
}
