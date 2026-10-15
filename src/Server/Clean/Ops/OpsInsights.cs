using Npgsql;

namespace Server.Clean.Ops;

/// <summary>玩家存档分段按 schema 版本的分布桶。</summary>
/// <param name="SchemaVersion">存档分段 schema 版本。</param>
/// <param name="SectionCount">该版本的分段行数。</param>
public sealed record OpsSchemaVersionBucket(int SchemaVersion, long SectionCount);

/// <summary>核心表体量条目。</summary>
/// <param name="Table">限定表名（schema.table）。</param>
/// <param name="TotalBytes">含索引与 TOAST 的总占用字节。</param>
public sealed record OpsTableSize(string Table, long TotalBytes);

/// <summary>玩家认证与身份只读信息（供运维诊断展示）。</summary>
/// <param name="LoginName">玩家注册登录名（login_normalized）。</param>
/// <param name="PlayerSeq">角色自增编号（player_seq）。</param>
public sealed record OpsPlayerIdentity(string LoginName, long PlayerSeq);

/// <summary>市场成交流水行（只读诊断视图）。</summary>
/// <param name="TradeId">成交唯一标识。</param>
/// <param name="AssetId">品种资产 ID。</param>
/// <param name="Variant">品种变体。</param>
/// <param name="Price">成交单价。</param>
/// <param name="Quantity">成交数量。</param>
/// <param name="BuyerId">买方玩家 ID。</param>
/// <param name="SellerId">卖方玩家 ID。</param>
/// <param name="TradedAt">成交时刻。</param>
public sealed record OpsTradeRow(
    Guid TradeId,
    string AssetId,
    string Variant,
    double Price,
    double Quantity,
    Guid BuyerId,
    Guid SellerId,
    DateTimeOffset TradedAt);

/// <summary>
/// 运维面板存储侧快照：实体计数、durable 投递与落盘积压、存档分段规模与版本分布、核心表体量。
/// </summary>
/// <param name="AccountCount">账号数。</param>
/// <param name="PlayerCount">角色数。</param>
/// <param name="StateHeadCount">已有状态头的角色数。</param>
/// <param name="ActiveLeaseCount">未过期的玩家运行时租约数。</param>
/// <param name="OutboxPending">未投递且未租约的 outbox 行数。</param>
/// <param name="OutboxLeased">未投递但已被租约占用的 outbox 行数。</param>
/// <param name="OutboxDelivered">已投递的 outbox 行数。</param>
/// <param name="OutboxRetrying">未投递且已重试过的 outbox 行数。</param>
/// <param name="OutboxOldestPendingSeconds">最老未投递行的等待秒数；无积压为 null。</param>
/// <param name="FlushQueueDepth">待落盘 section 队列深度。</param>
/// <param name="FlushQueueRetrying">待落盘中已重试过的行数。</param>
/// <param name="FlushQueueOldestDueSeconds">最早到期行已等待的秒数；无到期积压为 null。</param>
/// <param name="SectionRowCount">存档分段行数。</param>
/// <param name="SectionRawBytes">存档分段原始字节合计。</param>
/// <param name="SectionStoredBytes">存档分段落盘字节合计。</param>
/// <param name="SectionSchemaVersions">存档分段 schema 版本分布。</param>
/// <param name="TableSizes">核心表体量。</param>
public sealed record OpsStorageSnapshot(
    long AccountCount,
    long PlayerCount,
    long StateHeadCount,
    long ActiveLeaseCount,
    long OutboxPending,
    long OutboxLeased,
    long OutboxDelivered,
    long OutboxRetrying,
    double? OutboxOldestPendingSeconds,
    long FlushQueueDepth,
    long FlushQueueRetrying,
    double? FlushQueueOldestDueSeconds,
    long SectionRowCount,
    long SectionRawBytes,
    long SectionStoredBytes,
    IReadOnlyList<OpsSchemaVersionBucket> SectionSchemaVersions,
    IReadOnlyList<OpsTableSize> TableSizes);

/// <summary>
/// 运维面板存储与账本只读查询。
/// 只做聚合统计与最近流水抽样，绝不返回玩家载荷、连接串或令牌；
/// 与业务仓储分离，避免把诊断读模型混进权威写路径。
/// </summary>
public sealed class OpsStorageInsights(NpgsqlDataSource dataSource)
{
    /// <summary>采集存储侧快照：实体计数、积压、存档规模与核心表体量。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask<OpsStorageSnapshot> GetStorageAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var counters = await ReadCountersAsync(connection, cancellationToken);
        var versions = await ReadSchemaVersionsAsync(connection, cancellationToken);
        var sizes = await ReadTableSizesAsync(connection, cancellationToken);
        return counters with { SectionSchemaVersions = versions, TableSizes = sizes };
    }

    /// <summary>读取最近市场成交流水（按成交时刻倒序）。</summary>
    /// <param name="limit">返回条数上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask<IReadOnlyList<OpsTradeRow>> GetRecentTradesAsync(int limit, CancellationToken cancellationToken = default)
    {
        var bounded = Math.Clamp(limit, 1, 500);
        var rows = new List<OpsTradeRow>(bounded);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT trade_id, asset_id, variant, price, quantity, buyer_id, seller_id, traded_at
            FROM market.trades
            ORDER BY traded_at DESC
            LIMIT $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = bounded });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OpsTradeRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetGuid(5),
                reader.GetGuid(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }
        return rows;
    }
    /// <summary>批量查询已加载玩家的登录名与角色自增序号；无连接或异常时静默返回空集合，不阻塞运维主流程。</summary>
    public async ValueTask<IReadOnlyDictionary<Guid, OpsPlayerIdentity>> GetPlayerIdentitiesAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        if (playerIds.Count == 0) return new Dictionary<Guid, OpsPlayerIdentity>();
        try
        {
            var map = new Dictionary<Guid, OpsPlayerIdentity>(playerIds.Count);
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT p.player_id, a.login_normalized, p.player_seq
                FROM identity.players p
                JOIN identity.accounts a ON p.account_id = a.account_id
                WHERE p.player_id = ANY($1)
                """, connection);
            command.Parameters.Add(new NpgsqlParameter { Value = playerIds.ToArray() });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                map[reader.GetGuid(0)] = new OpsPlayerIdentity(reader.GetString(1), reader.GetInt64(2));
            }
            return map;
        }
        catch
        {
            return new Dictionary<Guid, OpsPlayerIdentity>();
        }
    }


    private static async ValueTask<OpsStorageSnapshot> ReadCountersAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT count(*) FROM identity.accounts),
                (SELECT count(*) FROM identity.players),
                (SELECT count(*) FROM game.player_state_heads),
                (SELECT count(*) FROM game.player_runtime_leases WHERE lease_until > now()),
                (SELECT count(*) FROM platform.integration_outbox WHERE delivered_at IS NULL AND lease_until IS NULL),
                (SELECT count(*) FROM platform.integration_outbox WHERE delivered_at IS NULL AND lease_until IS NOT NULL AND lease_until > now()),
                (SELECT count(*) FROM platform.integration_outbox WHERE delivered_at IS NOT NULL),
                (SELECT count(*) FROM platform.integration_outbox WHERE delivered_at IS NULL AND attempts > 0),
                (SELECT EXTRACT(EPOCH FROM (now() - min(occurred_at))) FROM platform.integration_outbox WHERE delivered_at IS NULL),
                (SELECT count(*) FROM platform.section_flush_queue),
                (SELECT count(*) FROM platform.section_flush_queue WHERE attempts > 0),
                (SELECT EXTRACT(EPOCH FROM (now() - min(next_attempt_at))) FROM platform.section_flush_queue WHERE next_attempt_at <= now()),
                (SELECT count(*) FROM game.player_state_sections),
                (SELECT coalesce(sum(raw_bytes), 0) FROM game.player_state_sections),
                (SELECT coalesce(sum(stored_bytes), 0) FROM game.player_state_sections)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new OpsStorageSnapshot(0, 0, 0, 0, 0, 0, 0, 0, null, 0, 0, null, 0, 0, 0, [], []);
        return new OpsStorageSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetDouble(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            await reader.IsDBNullAsync(11, cancellationToken) ? null : reader.GetDouble(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetInt64(14),
            [],
            []);
    }

    private static async ValueTask<IReadOnlyList<OpsSchemaVersionBucket>> ReadSchemaVersionsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var buckets = new List<OpsSchemaVersionBucket>();
        await using var command = new NpgsqlCommand("""
            SELECT schema_version, count(*)
            FROM game.player_state_sections
            GROUP BY schema_version
            ORDER BY schema_version
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            buckets.Add(new OpsSchemaVersionBucket(reader.GetInt32(0), reader.GetInt64(1)));
        return buckets;
    }

    private static async ValueTask<IReadOnlyList<OpsTableSize>> ReadTableSizesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var sizes = new List<OpsTableSize>();
        await using var command = new NpgsqlCommand("""
            SELECT n.nspname || '.' || c.relname, pg_total_relation_size(c.oid)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'r'
              AND n.nspname IN ('identity', 'game', 'economy', 'market', 'platform')
            ORDER BY pg_total_relation_size(c.oid) DESC
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            sizes.Add(new OpsTableSize(reader.GetString(0), reader.GetInt64(1)));
        return sizes;
    }
}
