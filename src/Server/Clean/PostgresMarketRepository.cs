using Domain.GameData;
using Domain.Market;
namespace Server.Clean;

/// <summary>
/// 市场订单、冻结、成交和资产转移的 PostgreSQL 事务实现。
/// 订单簿本身是可重建投影；本类只提交权威订单与资产事实。
/// </summary>
public sealed class PostgresMarketRepository(
    NpgsqlDataSource dataSource,
    ServerOptions options,
    ILogger<PostgresMarketRepository> logger,
    GameDataCatalog? gameData = null) : IMarketTransactionStore
{
    private static readonly string CoinsAsset = "currency.spirit-stone";

    /// <summary>
    /// 市场分片单写者语义的进程内闸门：分区内全部写事务串行排队，消除订单行/成交谓词上的
    /// 分区内部 SSI 竞争；不同分区互不影响。与 <see cref="PlayerCommitGates"/> 配合时先取
    /// 分区闸再取玩家条带，与经济仓储的「只取条带」构成一致的全局加锁序。
    /// </summary>
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> PartitionGates = new();

    private static SemaphoreSlim PartitionGateFor(int partitionId)
        => PartitionGates.GetOrAdd(partitionId, _ => new SemaphoreSlim(1, 1));
    /// <summary>底层数据源，供需要在同一连接上组合市场读模型的调用方使用。</summary>
    public NpgsqlDataSource DataSource => dataSource;

    /// <summary>
    /// 结算采用成交即入账，claim 不是再次发钱，而是持久化玩家对已入账结算的确认游标。
    /// 这样客户端重复点击不会重复发放资产，同时保留可审计的领取动作。
    /// </summary>
    public async ValueTask<SettlementClaimResult> ClaimSettlementAsync(Guid playerId, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (playerId == Guid.Empty || requestId == Guid.Empty) throw new ArgumentException("结算领取标识无效。", nameof(playerId));
        var now = DateTimeOffset.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // 游标行锁已与同玩家成交互斥，领取不再需要 Serializable 谓词读；ReadCommitted 下本方法无 SSI 表面。
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await using (var existing = new NpgsqlCommand("""
            SELECT settled_trade_count, claimed_at
            FROM market.settlement_claims
            WHERE player_id = $1 AND request_id = $2
            FOR SHARE
            """, connection, transaction))
        {
            existing.Parameters.Add(new NpgsqlParameter { Value = playerId });
            existing.Parameters.Add(new NpgsqlParameter { Value = requestId });
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var existingCount = reader.GetInt32(0);
                var claimedAt = reader.GetFieldValue<DateTimeOffset>(1);
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return new SettlementClaimResult(playerId, requestId, existingCount, claimedAt, true);
            }
        }

        // 确保游标行存在（首次领取的玩家行尚缺）；随后 FOR UPDATE 锁定——同玩家成交事务
        // 的游标 upsert 会在此行上等待本领取提交，新成交要么计入本次要么留待下次，不丢不重。
        await using (var ensure = new NpgsqlCommand("""
            INSERT INTO market.settlement_cursors (player_id, pending_count, settled_count, updated_at)
            VALUES ($1, 0, 0, $2)
            ON CONFLICT (player_id) DO NOTHING
            """, connection, transaction))
        {
            ensure.Parameters.Add(new NpgsqlParameter { Value = playerId });
            ensure.Parameters.Add(new NpgsqlParameter { Value = now });
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        int settlementCount;
        await using (var cursor = new NpgsqlCommand("""
            SELECT pending_count
            FROM market.settlement_cursors
            WHERE player_id = $1
            FOR UPDATE
            """, connection, transaction))
        {
            cursor.Parameters.Add(new NpgsqlParameter { Value = playerId });
            settlementCount = (int)(await cursor.ExecuteScalarAsync(cancellationToken) ?? 0);
        }

        await using (var settle = new NpgsqlCommand("""
            UPDATE market.settlement_cursors
            SET pending_count = 0, settled_count = settled_count + $2, updated_at = $3
            WHERE player_id = $1
            """, connection, transaction))
        {
            settle.Parameters.Add(new NpgsqlParameter { Value = playerId });
            settle.Parameters.Add(new NpgsqlParameter { Value = settlementCount });
            settle.Parameters.Add(new NpgsqlParameter { Value = now });
            await settle.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO market.settlement_claims(player_id, request_id, settled_trade_count, claimed_at)
            VALUES ($1, $2, $3, $4)
            """, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = playerId });
            insert.Parameters.Add(new NpgsqlParameter { Value = requestId });
            insert.Parameters.Add(new NpgsqlParameter { Value = settlementCount });
            insert.Parameters.Add(new NpgsqlParameter { Value = now });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new SettlementClaimResult(playerId, requestId, settlementCount, now, false);
    }

    /// <summary>加载本进程分片全部活动订单，按 sequence 排序；用于启动时重建订单簿。</summary>
    public async ValueTask<IReadOnlyList<StoredMarketOrder>> LoadActiveOrdersAsync(CancellationToken cancellationToken = default)
    {
        var orders = new List<StoredMarketOrder>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT order_id, owner_player_id, asset_id, variant, side, price, quantity,
                   filled_quantity, status, sequence, created_at, expires_at
            FROM market.orders
            WHERE partition_id = $1 AND status IN ('active', 'partially_filled')
            ORDER BY sequence
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = options.MarketPartitionId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            orders.Add(new StoredMarketOrder(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetString(8), reader.GetInt64(9), reader.GetFieldValue<DateTimeOffset>(10), reader.GetFieldValue<DateTimeOffset>(11)));
        }
        return orders;
    }

    /// <summary>
    /// 读取行情统计表；assetId 为空时返回全部品种行。当日/当周成交量按存储键惰性掩零——
    /// 行内 day_key/week_key 落后当前周期时直接投影为 0，无需为跨日/跨周单独发起写事务。
    /// </summary>
    /// <param name="assetId">可选标的过滤；null/空串返回全表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask<IReadOnlyList<MarketItemStatsSnapshot>> LoadItemStatsAsync(
        string? assetId = null,
        CancellationToken cancellationToken = default)
    {
        var (dayKey, weekKey) = MarketStatsKeys(DateTimeOffset.UtcNow);
        var rows = new List<MarketItemStatsSnapshot>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT asset_id, variant, reference_price, best_ask_price, ask_quantity, best_bid_price, bid_quantity,
                   CASE WHEN day_key = $1 THEN day_volume ELSE 0 END,
                   CASE WHEN week_key = $2 THEN week_volume ELSE 0 END
            FROM market.item_stats
            WHERE ($3::varchar IS NULL OR asset_id = $3)
            ORDER BY asset_id, variant
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date, Value = dayKey });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date, Value = weekKey });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Varchar, Value = string.IsNullOrWhiteSpace(assetId) ? DBNull.Value : assetId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MarketItemStatsSnapshot(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3), reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5), reader.GetDouble(6),
                reader.GetDouble(7), reader.GetDouble(8)));
        }
        return rows;
    }

    /// <summary>加载本分片指定品种的活动订单，按 sequence 排序。</summary>
    public async ValueTask<IReadOnlyList<StoredMarketOrder>> LoadActiveOrdersAsync(
        MarketInstrument instrument,
        CancellationToken cancellationToken = default)
    {
        if (!instrument.ItemId.IsValid || instrument.Variant is null || instrument.Variant.Length > 128)
            throw new ArgumentException("市场品种无效。", nameof(instrument));
        var orders = new List<StoredMarketOrder>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT order_id, owner_player_id, asset_id, variant, side, price, quantity,
                   filled_quantity, status, sequence, created_at, expires_at
            FROM market.orders
            WHERE partition_id = $1 AND asset_id = $2 AND variant = $3
              AND status IN ('active', 'partially_filled')
            ORDER BY sequence
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = options.MarketPartitionId });
        command.Parameters.Add(new NpgsqlParameter { Value = instrument.ItemId.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = instrument.Variant });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            orders.Add(new StoredMarketOrder(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetString(8), reader.GetInt64(9), reader.GetFieldValue<DateTimeOffset>(10), reader.GetFieldValue<DateTimeOffset>(11)));
        }
        return orders;
    }

    /// <summary>按订单 ID 加载本分片订单；跨分片或不存在的订单返回 null。</summary>
    public async ValueTask<StoredMarketOrder?> LoadOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        if (orderId == Guid.Empty) return null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT order_id, owner_player_id, asset_id, variant, side, price, quantity,
                   filled_quantity, status, sequence, created_at, expires_at
            FROM market.orders
            WHERE order_id = $1 AND partition_id = $2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = orderId });
        command.Parameters.Add(new NpgsqlParameter { Value = options.MarketPartitionId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredMarketOrder(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetString(8), reader.GetInt64(9), reader.GetFieldValue<DateTimeOffset>(10), reader.GetFieldValue<DateTimeOffset>(11))
            : null;
    }

    /// 读取与指定玩家存在风控禁止成交关联的玩家标识集合（结义邀请上下级、同设备主体）。
    /// </summary>
    public async ValueTask<HashSet<Guid>> GetProhibitedTradePartnersAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        if (playerId == Guid.Empty) return [];
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var partners = new HashSet<Guid>();

        // 1. 邀请关联（我的邀请者，以及我邀请的小号）
        await using (var inviteCmd = new NpgsqlCommand("""
            SELECT inviter_id FROM social.player_invitations WHERE invitee_id = $1
            UNION ALL
            SELECT invitee_id FROM social.player_invitations WHERE inviter_id = $1
            """, connection))
        {
            inviteCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await using var reader = await inviteCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                partners.Add(reader.GetGuid(0));
        }

        // 2. 同设备 / 同主体关联
        await using (var deviceCmd = new NpgsqlCommand("""
            SELECT p2.player_id
            FROM identity.players p1
            JOIN identity.account_risk_signals s1 ON s1.account_id = p1.account_id
            JOIN identity.account_risk_signals s2 ON s2.signal_kind = s1.signal_kind AND s2.signal_hash = s1.signal_hash
            JOIN identity.players p2 ON p2.account_id = s2.account_id
            WHERE p1.player_id = $1
              AND s1.signal_kind IN ('person', 'device', 'fingerprint')
              AND p2.player_id <> $1
            """, connection))
        {
            deviceCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await using var reader = await deviceCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                partners.Add(reader.GetGuid(0));
        }

        return partners;
    }

    /// <summary>
    /// 抢占本分片写者租约。仅在租约不存在、已过期或本就属于本实例时成功；
    /// 每次成功接管 fence_token 递增，使旧写者未提交的事务因 fence 校验失败而无法再写。
    /// </summary>
    public async ValueTask<bool> AcquireLeaseAsync(Guid ownerInstanceId, CancellationToken cancellationToken = default)
    {
        if (ownerInstanceId == Guid.Empty) throw new ArgumentException("ownerInstanceId 不能为空。", nameof(ownerInstanceId));
        var now = DateTimeOffset.UtcNow;
        var partitionGate = PartitionGateFor(options.MarketPartitionId);
        await partitionGate.WaitAsync(cancellationToken);
        try
        {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
                await using var command = new NpgsqlCommand("""
                    INSERT INTO market.partition_leases
                        (partition_id, owner_instance_id, fence_token, expires_at, renewed_at)
                    VALUES ($1, $2, 1, $3, $4)
                    ON CONFLICT (partition_id) DO UPDATE SET
                        owner_instance_id = EXCLUDED.owner_instance_id,
                        fence_token = market.partition_leases.fence_token + 1,
                        expires_at = EXCLUDED.expires_at,
                        renewed_at = EXCLUDED.renewed_at
                    WHERE market.partition_leases.expires_at <= $4
                       OR market.partition_leases.owner_instance_id = $2
                    RETURNING fence_token
                    """, connection, transaction);
                command.Parameters.Add(new NpgsqlParameter { Value = options.MarketPartitionId });
                command.Parameters.Add(new NpgsqlParameter { Value = ownerInstanceId });
                command.Parameters.Add(new NpgsqlParameter { Value = now.Add(options.SectionFlushLease) });
                command.Parameters.Add(new NpgsqlParameter { Value = now });
                var value = await command.ExecuteScalarAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return value is long;
        }
        finally
        {
            partitionGate.Release();
        }
    }

    /// <summary>
    /// 在 Serializable 事务内提交挂单：校验分片租约 fence → 写订单 → 冻结挂单资产 → 逐笔应用成交。
    /// 幂等键命中已存订单时核对参数一致后直接返回，不重复冻结或成交。
    /// </summary>
    /// <param name="order">挂单输入，分片必须与当前写者一致。</param>
    /// <param name="fills">本次撮合产生的成交列表（可为空）。</param>
    /// <param name="fenceToken">当前持有的分片租约 fence。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>提交结局；AlreadyCommitted=true 表示幂等重放。</returns>
    public async ValueTask<MarketCommitResult> CommitPlacementAsync(
        MarketOrderInput order,
        IReadOnlyList<MarketFillInput> fills,
        long fenceToken,
        CancellationToken cancellationToken = default)
    {
        ValidateOrder(order);
        if (gameData is not null && gameData.Items.TryGetValue(new Domain.Common.ItemId(order.AssetId), out var itemConfig))
        {
            if (string.Equals(itemConfig.Category, "material", StringComparison.OrdinalIgnoreCase))
            {
                var basePrice = itemConfig.SellPrice > 0 ? itemConfig.SellPrice : 1.0;
                var maxPrice = basePrice * 50.0;
                if (order.Price > maxPrice)
                    throw new ArgumentException($"材料挂单单价超出基准价值上限（最高 {maxPrice} 灵石）。", nameof(order));
            }
        }
        if (order.PartitionId != options.MarketPartitionId)
            throw new ArgumentException("订单分片与当前市场写者不一致。", nameof(order));
        if (fills is null || fills.Count > 256)
            throw new ArgumentException("单次成交数量超过上限。", nameof(fills));
        var now = order.CreatedAt ?? DateTimeOffset.UtcNow;
        var total = order.Price * order.Quantity;
        if (double.IsNaN(total) || double.IsInfinity(total) || total <= 0)
            throw new ArgumentException("订单总额无效。", nameof(order));

        var partitionGate = PartitionGateFor(order.PartitionId);
        var stripes = PlayerCommitGates.StripesFor(PlacementPlayerIds(order, fills));
        await partitionGate.WaitAsync(cancellationToken);
        try
        {
            await PlayerCommitGates.AcquireAllAsync(stripes, cancellationToken);
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
                await EnsureLeaseAsync(connection, transaction, order.PartitionId, fenceToken, cancellationToken);

                var inserted = await InsertOrderAsync(connection, transaction, order, now, cancellationToken);
                if (!inserted)
                {
                    await EnsureExistingOrderMatchesAsync(connection, transaction, order, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new MarketCommitResult(order.OrderId, true, true, []);
                }

                // 先冻结挂单资产，再应用成交；所有余额按 player/asset 排序锁定。
                var reservationId = Guid.NewGuid();
                var reserveAsset = order.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? CoinsAsset : order.AssetId;
                var reserveVariant = order.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? string.Empty : order.Variant;
                // 冻结 quantity 始终表示标的数量；买单的灵石金额单独放在 reserved_value。
                var reserveQuantity = order.Quantity;
                var reserveDebit = order.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? total : reserveQuantity;
                await ChangeBalanceAsync(connection, transaction,
                    new AssetDelta(order.OwnerPlayerId, reserveAsset, reserveVariant, -reserveDebit), now, cancellationToken);
                    await using (var reservation = new NpgsqlCommand("""
                        INSERT INTO economy.reservations
                            (reservation_id, order_id, player_id, asset_id, variant, instance_id, side, quantity,
                             reserved_value, status, created_at, updated_at)
                        VALUES ($1, $2, $3, $4, $5, NULL, $6, $7, $8, 'active', $9, $9)
                        """, connection, transaction))
                    {
                        reservation.Parameters.Add(new NpgsqlParameter { Value = reservationId });
                        reservation.Parameters.Add(new NpgsqlParameter { Value = order.OrderId });
                        reservation.Parameters.Add(new NpgsqlParameter { Value = order.OwnerPlayerId });
                        reservation.Parameters.Add(new NpgsqlParameter { Value = reserveAsset });
                        reservation.Parameters.Add(new NpgsqlParameter { Value = reserveVariant });
                        reservation.Parameters.Add(new NpgsqlParameter { Value = order.Side.ToLowerInvariant() });
                        reservation.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = reserveQuantity });
                        reservation.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = order.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? total : 0d });
                        reservation.Parameters.Add(new NpgsqlParameter { Value = now });
                        await reservation.ExecuteNonQueryAsync(cancellationToken);
                    }
                await AppendEconomyTransactionAsync(
                    connection,
                    transaction,
                    order.OrderId,
                    $"market-order:{order.IdempotencyKey}",
                    order.OwnerPlayerId,
                    [new AssetDelta(order.OwnerPlayerId, reserveAsset, reserveVariant, -reserveDebit)],
                    now,
                    cancellationToken);

                var tradeIds = new List<Guid>(fills.Count);
                var tradeIdSet = new HashSet<Guid>();
                var incomingFillQuantity = 0d;
                foreach (var fill in fills)
                {
                    ValidateFill(fill, order.PartitionId);
                    if (!tradeIdSet.Add(fill.TradeId)
                        || (fill.BuyOrderId != order.OrderId && fill.SellOrderId != order.OrderId)
                        || !string.Equals(fill.AssetId, order.AssetId, StringComparison.Ordinal)
                        || !string.Equals(fill.Variant, order.Variant, StringComparison.Ordinal))
                        throw new InvalidOperationException("成交必须唯一且属于当前订单品种。");
                    incomingFillQuantity += (fill.BuyOrderId == order.OrderId || fill.SellOrderId == order.OrderId) ? fill.Quantity : 0d;
                    if (!double.IsFinite(incomingFillQuantity) || incomingFillQuantity > order.Quantity + 1e-9)
                        throw new InvalidOperationException("当前订单成交总量超过订单数量。");
                    await ApplyFillAsync(connection, transaction, fill, order.PartitionId, now, cancellationToken);
                    tradeIds.Add(fill.TradeId);
                }

                // 行情统计与订单事实同事务提交：先按日/周键惰性累加成交量，再整簿重算盘口价量。
                var (dayKey, weekKey) = MarketStatsKeys(now);
                if (fills.Count > 0)
                {
                    await BumpItemStatsVolumeAndReferencePriceAsync(connection, transaction,
                        order.AssetId, order.Variant, fills, dayKey, weekKey, cancellationToken);
                }
                await RefreshItemStatsBookAsync(connection, transaction, order.AssetId, order.Variant, dayKey, weekKey, cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                logger.LogDebug("市场挂单提交 orderId={OrderId} fills={FillCount}", order.OrderId, fills.Count);
                return new MarketCommitResult(order.OrderId, true, false, tradeIds);
            }
            finally
            {
                PlayerCommitGates.ReleaseAll(stripes);
            }
        }
        finally
        {
            partitionGate.Release();
        }
    }

    /// <summary>
    /// 整簿重算单品种盘口统计列：对本分片活动订单按 (partition_id, asset_id, variant, status)
    /// 索引覆盖扫描聚合卖一/买一价与剩余总量；upsert 只覆盖盘口列，日/周成交量由
    /// <see cref="BumpItemStatsVolumeAsync"/> 独立累加保留。无活动订单时聚合仍产出
    /// NULL/0 单行，使已清空品种价格按空盘口回落。
    /// </summary>
    private async Task RefreshItemStatsBookAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string assetId,
        string variant,
        DateOnly dayKey,
        DateOnly weekKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO market.item_stats
                (asset_id, variant, best_ask_price, ask_quantity, best_bid_price, bid_quantity,
                 day_volume, day_key, week_volume, week_key, updated_at)
            SELECT $1, $2,
                   MIN(price) FILTER (WHERE side = 'sell'),
                   COALESCE(SUM(quantity - filled_quantity) FILTER (WHERE side = 'sell'), 0),
                   MAX(price) FILTER (WHERE side = 'buy'),
                   COALESCE(SUM(quantity - filled_quantity) FILTER (WHERE side = 'buy'), 0),
                   0, $3, 0, $4, now()
            FROM market.orders
            WHERE partition_id = $5 AND asset_id = $1 AND variant = $2
              AND status IN ('active', 'partially_filled')
            ON CONFLICT (asset_id, variant) DO UPDATE SET
                best_ask_price = EXCLUDED.best_ask_price,
                ask_quantity = EXCLUDED.ask_quantity,
                best_bid_price = EXCLUDED.best_bid_price,
                bid_quantity = EXCLUDED.bid_quantity,
                updated_at = EXCLUDED.updated_at
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = assetId });
        command.Parameters.Add(new NpgsqlParameter { Value = variant });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date, Value = dayKey });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date, Value = weekKey });
        command.Parameters.Add(new NpgsqlParameter { Value = options.MarketPartitionId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 成交量惰性累加：行不存在时以本周期键建仓；已存在时 day_key/week_key 匹配才累加，
    /// 跨日/跨周首笔成交以新周期重置——写侧即完成换日归位，读侧另有键掩零兜底。
    /// </summary>
    private static async Task BumpItemStatsVolumeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string assetId,
        string variant,
        double quantity,
        DateOnly dayKey,
        DateOnly weekKey,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(quantity) || quantity <= 0) return;
        await using var command = new NpgsqlCommand("""
            INSERT INTO market.item_stats
                (asset_id, variant, best_ask_price, ask_quantity, best_bid_price, bid_quantity,
                 day_volume, day_key, week_volume, week_key, updated_at)
            VALUES ($1, $2, NULL, 0, NULL, 0, $3, $4, $3, $5, now())
            ON CONFLICT (asset_id, variant) DO UPDATE SET
                day_volume = CASE WHEN market.item_stats.day_key = $4
                                  THEN market.item_stats.day_volume + $3 ELSE $3 END,
                day_key = $4,
                week_volume = CASE WHEN market.item_stats.week_key = $5
                                   THEN market.item_stats.week_volume + $3 ELSE $3 END,
                week_key = $5,
                updated_at = now()
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = assetId });
        command.Parameters.Add(new NpgsqlParameter { Value = variant });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = quantity });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date, Value = dayKey });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date, Value = weekKey });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 成交量与滑动平均参考价更新：
    /// 成交量按日/周键累加；参考价按最近 10 次成交单价的单值滑动平均（EMA，α = 0.1；单笔购买多个算作一次，以单价更新）。
    /// </summary>
    private async Task BumpItemStatsVolumeAndReferencePriceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string assetId,
        string variant,
        IReadOnlyList<MarketFillInput> fills,
        DateOnly dayKey,
        DateOnly weekKey,
        CancellationToken cancellationToken)
    {
        if (fills.Count == 0) return;
        var totalQuantity = fills.Sum(fill => fill.Quantity);
        if (!double.IsFinite(totalQuantity) || totalQuantity <= 0) return;

        await BumpItemStatsVolumeAsync(connection, transaction, assetId, variant, totalQuantity, dayKey, weekKey, cancellationToken);

        foreach (var fill in fills)
        {
            if (!double.IsFinite(fill.Price) || fill.Price <= 0) continue;
            var weight = ReferencePriceRules.CalculateTradeWeight(assetId, variant, fill.Quantity, gameData);
            await using var command = new NpgsqlCommand("""
                UPDATE market.item_stats
                SET reference_price = CASE
                    WHEN reference_price IS NULL THEN round($3::numeric)
                    ELSE GREATEST(1.0, round((reference_price * (1.0 - $4) + $3 * $4)::numeric)) END,
                    updated_at = now()
                WHERE asset_id = $1 AND variant = $2
                """, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = assetId });
            command.Parameters.Add(new NpgsqlParameter { Value = variant });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = fill.Price });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = weight });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>行情日/周键：UTC 自然日与 ISO 周一为界的当周首日（date_trunc('week') 同日历）。</summary>
    private static (DateOnly DayKey, DateOnly WeekKey) MarketStatsKeys(DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var week = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        return (day, week);
    }

    /// <summary>关单事务结局：Success 为 false 时调用方整笔回滚；AssetId 非空表示本事务真实关单、需联动刷新行情统计。</summary>
    private sealed record CloseOrderOutcome(bool Success, string? AssetId, string? Variant);

    /// <summary>主动撤单并退还冻结资产；幂等键必须绑定玩家与订单，防止跨玩家重放。</summary>
    public ValueTask<bool> CancelAsync(
        Guid orderId,
        Guid ownerPlayerId,
        int partitionId,
        long fenceToken,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        return CloseOrderAsync(orderId, ownerPlayerId, partitionId, fenceToken, idempotencyKey, null, cancellationToken);
    }

    /// <summary>过期关单：与撤单共用同一路径，仅当 expires_at 不晚于 cutoff 才关闭并退还冻结。</summary>
    public ValueTask<bool> ExpireAsync(Guid orderId, Guid ownerPlayerId, long fenceToken, DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        CloseOrderAsync(orderId, ownerPlayerId, options.MarketPartitionId, fenceToken,
            $"market-expire:{ownerPlayerId:D}:{orderId:D}", cutoff, cancellationToken);

    /// <summary>
    /// 到期关单合批：单个 Serializable 事务内按传入次序逐笔完成幂等检查、订单校验、
    /// 冻结退款、流水追加与关单更新；任一笔不可关闭则整批回滚返回 false。
    /// 逐笔执行顺序与退款顺序和原独立事务路径完全一致，仅事务边界收敛为一次提交。
    /// </summary>
    /// <param name="orders">到期订单目标列表（订单标识与属主玩家）。</param>
    /// <param name="fenceToken">当前持有的分片租约 fence。</param>
    /// <param name="cutoff">到期判定截止时刻；expires_at 晚于该时刻的订单不关闭。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>整批提交成功返回 true，任一订单不可关闭返回 false。</returns>
    public async ValueTask<bool> ExpireBatchAsync(
        IReadOnlyList<MarketExpiryOrder> orders,
        long fenceToken,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        if (orders is null || orders.Count == 0) return true;
        if (fenceToken <= 0) throw new ArgumentOutOfRangeException(nameof(fenceToken));
        var partitionGate = PartitionGateFor(options.MarketPartitionId);
        var stripes = PlayerCommitGates.StripesFor(orders.Select(o => o.OwnerPlayerId));
        await partitionGate.WaitAsync(cancellationToken);
        try
        {
            await PlayerCommitGates.AcquireAllAsync(stripes, cancellationToken);
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
                await EnsureLeaseAsync(connection, transaction, options.MarketPartitionId, fenceToken, cancellationToken);
                var touchedInstruments = new HashSet<(string AssetId, string Variant)>();
                foreach (var target in orders)
                {
                    var canonicalIdempotencyKey = $"market-expire:{target.OwnerPlayerId:D}:{target.OrderId:D}";
                    var outcome = await CloseOrderCoreAsync(connection, transaction, target.OrderId, target.OwnerPlayerId,
                        canonicalIdempotencyKey, cutoff, cancellationToken);
                    if (!outcome.Success)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return false;
                    }
                    if (outcome.AssetId is not null) touchedInstruments.Add((outcome.AssetId, outcome.Variant!));
                }
                // 合批内同品种只整簿重算一次：按 (asset_id, variant) 去重后逐品种刷新行情统计。
                var (dayKey, weekKey) = MarketStatsKeys(cutoff);
                foreach (var (assetId, variant) in touchedInstruments)
                    await RefreshItemStatsBookAsync(connection, transaction, assetId, variant, dayKey, weekKey, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return true;
            }
            finally
            {
                PlayerCommitGates.ReleaseAll(stripes);
            }
        }
        finally
        {
            partitionGate.Release();
        }
    }

    private async ValueTask<bool> CloseOrderAsync(Guid orderId, Guid ownerPlayerId, int partitionId, long fenceToken,
        string idempotencyKey, DateTimeOffset? expirationCutoff, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty || ownerPlayerId == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey)) return false;
        if (partitionId != options.MarketPartitionId)
            throw new ArgumentException("撤单分片与当前写者不一致。", nameof(partitionId));
        var operation = expirationCutoff.HasValue ? "expire" : "cancel";
        var canonicalIdempotencyKey = $"market-{operation}:{ownerPlayerId:D}:{orderId:D}";
        if (!string.Equals(idempotencyKey, canonicalIdempotencyKey, StringComparison.Ordinal))
            throw new ArgumentException("撤单幂等键必须绑定当前玩家和订单。", nameof(idempotencyKey));
        var partitionGate = PartitionGateFor(partitionId);
        var stripes = PlayerCommitGates.StripesFor([ownerPlayerId]);
        await partitionGate.WaitAsync(cancellationToken);
        try
        {
            await PlayerCommitGates.AcquireAllAsync(stripes, cancellationToken);
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
                await EnsureLeaseAsync(connection, transaction, partitionId, fenceToken, cancellationToken);
                var outcome = await CloseOrderCoreAsync(connection, transaction, orderId, ownerPlayerId, canonicalIdempotencyKey, expirationCutoff, cancellationToken);
                if (!outcome.Success)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
                if (outcome.AssetId is not null)
                {
                    var (dayKey, weekKey) = MarketStatsKeys(DateTimeOffset.UtcNow);
                    await RefreshItemStatsBookAsync(connection, transaction, outcome.AssetId, outcome.Variant!, dayKey, weekKey, cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                return true;
            }
            finally
            {
                PlayerCommitGates.ReleaseAll(stripes);
            }
        }
        finally
        {
            partitionGate.Release();
        }
    }

    /// <summary>
    /// 关单事务体（撤单/到期共用）：在已持有 Serializable 事务与租约校验的连接上，
    /// 完成幂等短路、订单行锁校验、冻结退款、流水追加与状态更新。
    /// Success=false 表示订单不可关闭（不存在/属主不符/非活动/未到期），由调用方决定回滚；
    /// Success=true 且 AssetId 非空表示本事务真实关单，调用方须在提交前联动刷新行情统计。
    /// </summary>
    private async Task<CloseOrderOutcome> CloseOrderCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        Guid ownerPlayerId,
        string canonicalIdempotencyKey,
        DateTimeOffset? expirationCutoff,
        CancellationToken cancellationToken)
    {
        if (await HasEconomyTransactionAsync(connection, transaction, canonicalIdempotencyKey, ownerPlayerId, cancellationToken))
            return new CloseOrderOutcome(true, null, null);
        await using var order = new NpgsqlCommand("""
            SELECT owner_player_id, side, asset_id, variant, status, expires_at
            FROM market.orders WHERE order_id = $1 AND partition_id = $2 FOR UPDATE
            """, connection, transaction);
        order.Parameters.Add(new NpgsqlParameter { Value = orderId });
        order.Parameters.Add(new NpgsqlParameter { Value = options.MarketPartitionId });
        var orderOwnerId = Guid.Empty;
        var side = string.Empty;
        var assetId = string.Empty;
        var variant = string.Empty;
        var status = string.Empty;
        var orderFound = false;
        DateTimeOffset expiresAt = default;
        await using (var reader = await order.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                orderFound = true;
                orderOwnerId = reader.GetGuid(0);
                side = reader.GetString(1);
                assetId = reader.GetString(2);
                variant = reader.GetString(3);
                status = reader.GetString(4);
                expiresAt = reader.GetFieldValue<DateTimeOffset>(5);
            }
        }
        if (!orderFound)
            return new CloseOrderOutcome(false, null, null);
        if (orderOwnerId != ownerPlayerId || status is not ("active" or "partially_filled")
            || (expirationCutoff.HasValue && expiresAt > expirationCutoff.Value))
            return new CloseOrderOutcome(false, null, null);

        // 以冻结表中的真实余量退款；买单可能以更低的 maker 价成交，不能用限价反推。
        var releaseAsset = side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? CoinsAsset : assetId;
        var releaseVariant = side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? string.Empty : variant;
        var releaseAmount = await ReadReservationReleaseAsync(connection, transaction, orderId, side.Equals("buy", StringComparison.OrdinalIgnoreCase), cancellationToken)
            ?? throw new InvalidOperationException("活动订单缺少冻结记录，拒绝静默撤单。");
        if (releaseAmount <= 0)
            throw new InvalidOperationException("活动订单冻结余量无效，拒绝静默撤单。");
        await ChangeBalanceAsync(connection, transaction, new AssetDelta(ownerPlayerId, releaseAsset, releaseVariant, releaseAmount), DateTimeOffset.UtcNow, cancellationToken);
        await AppendEconomyTransactionAsync(
            connection,
            transaction,
            Guid.NewGuid(),
            canonicalIdempotencyKey,
            ownerPlayerId,
            [new AssetDelta(ownerPlayerId, releaseAsset, releaseVariant, releaseAmount)],
            DateTimeOffset.UtcNow,
            cancellationToken);
        await using (var update = new NpgsqlCommand("UPDATE market.orders SET status = $2, version = version + 1, updated_at = now() WHERE order_id = $1", connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = orderId });
            update.Parameters.Add(new NpgsqlParameter { Value = expirationCutoff.HasValue ? "expired" : "cancelled" });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var reservation = new NpgsqlCommand("UPDATE economy.reservations SET status = 'released', updated_at = now() WHERE order_id = $1 AND status = 'active'", connection, transaction))
        {
            reservation.Parameters.Add(new NpgsqlParameter { Value = orderId });
            await reservation.ExecuteNonQueryAsync(cancellationToken);
        }
        return new CloseOrderOutcome(true, assetId, variant);
    }

    private static async Task EnsureLeaseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int partitionId, long fenceToken, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM market.partition_leases
            WHERE partition_id = $1 AND fence_token = $2 AND expires_at > now()
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = partitionId });
        command.Parameters.Add(new NpgsqlParameter { Value = fenceToken });
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new MarketLeaseLostException(partitionId, fenceToken);
    }

    private static async Task<bool> InsertOrderAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, MarketOrderInput order, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO market.orders
                (order_id, partition_id, owner_player_id, side, asset_id, variant, price,
                 quantity, filled_quantity, status, sequence, idempotency_key, created_at, updated_at, expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, 0, 'active',
                    (SELECT COALESCE(max(sequence), 0) + 1 FROM market.orders WHERE partition_id = $2), $9, $10, $10, $11)
            ON CONFLICT DO NOTHING
            RETURNING order_id
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = order.OrderId });
        command.Parameters.Add(new NpgsqlParameter { Value = order.PartitionId });
        command.Parameters.Add(new NpgsqlParameter { Value = order.OwnerPlayerId });
        command.Parameters.Add(new NpgsqlParameter { Value = order.Side.ToLowerInvariant() });
        command.Parameters.Add(new NpgsqlParameter { Value = order.AssetId });
        command.Parameters.Add(new NpgsqlParameter { Value = order.Variant });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = order.Price });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = order.Quantity });
        command.Parameters.Add(new NpgsqlParameter { Value = order.IdempotencyKey });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        command.Parameters.Add(new NpgsqlParameter { Value = now.AddDays(30) });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid;
    }

    private static async Task EnsureExistingOrderMatchesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MarketOrderInput order,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT order_id, owner_player_id, partition_id, side, asset_id, variant, price, quantity
            FROM market.orders
            WHERE order_id = $1 OR idempotency_key = $2
            FOR SHARE
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = order.OrderId });
        command.Parameters.Add(new NpgsqlParameter { Value = order.IdempotencyKey });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("订单幂等记录不可读。");
        var matches = reader.GetGuid(0) == order.OrderId
            && reader.GetGuid(1) == order.OwnerPlayerId
            && reader.GetInt32(2) == order.PartitionId
            && string.Equals(reader.GetString(3), order.Side, StringComparison.OrdinalIgnoreCase)
            && string.Equals(reader.GetString(4), order.AssetId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(5), order.Variant, StringComparison.Ordinal)
            && reader.GetDouble(6) == order.Price
            && reader.GetDouble(7) == order.Quantity;
        if (!matches) throw new DomainException(DomainErrorCode.Conflict, "订单幂等键已绑定其他订单参数。");
    }

    private static async Task ApplyFillAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, MarketFillInput fill, int partitionId, DateTimeOffset fallbackTime, CancellationToken cancellationToken)
    {
        if (await TradeExistsAsync(connection, transaction, fill, cancellationToken)) return;
        var gross = fill.Price * fill.Quantity;
        var proceeds = gross - fill.SellerTax;
        if (double.IsNaN(gross) || double.IsInfinity(gross) || gross <= 0 || fill.SellerTax > gross || proceeds < 0)
            throw new ArgumentException("成交金额无效。", nameof(fill));
        await ValidateCrossTradeEligibilityAsync(connection, transaction, fill.BuyerId, fill.SellerId, cancellationToken);
        await ValidateFillOrdersAsync(connection, transaction, fill, partitionId, cancellationToken);
        // 挂单时已经从双方余额扣除冻结额；成交只消费冻结并把结算资产记入对手方，
        // 不再二次扣减余额。这样重试或同一批次多笔成交不会产生双花。
        await ConsumeReservationAsync(connection, transaction, fill.BuyOrderId, gross, fill.Quantity, fill.TradeId, cancellationToken);
        await ConsumeReservationAsync(connection, transaction, fill.SellOrderId, 0d, fill.Quantity, fill.TradeId, cancellationToken);
        await ChangeBalanceAsync(connection, transaction, new AssetDelta(fill.BuyerId, fill.AssetId, fill.Variant, fill.Quantity), fallbackTime, cancellationToken);
        await ChangeBalanceAsync(connection, transaction, new AssetDelta(fill.SellerId, CoinsAsset, string.Empty, proceeds), fallbackTime, cancellationToken);
        await AppendEconomyTransactionAsync(
            connection,
            transaction,
            fill.TradeId,
            $"market-trade:{fill.TradeId:D}",
            fill.BuyerId,
            [
                new AssetDelta(fill.BuyerId, fill.AssetId, fill.Variant, fill.Quantity),
                new AssetDelta(fill.SellerId, CoinsAsset, string.Empty, proceeds)
            ],
            fill.TradedAt ?? fallbackTime,
            cancellationToken);

        await using (var trade = new NpgsqlCommand("""
            INSERT INTO market.trades
                (trade_id, partition_id, buy_order_id, sell_order_id, buyer_id, seller_id,
                 asset_id, variant, price, quantity, seller_tax, traded_at)
            VALUES ($1, (SELECT partition_id FROM market.orders WHERE order_id = $2), $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            ON CONFLICT (trade_id) DO NOTHING
            """, connection, transaction))
        {
            trade.Parameters.Add(new NpgsqlParameter { Value = fill.TradeId });
            trade.Parameters.Add(new NpgsqlParameter { Value = fill.BuyOrderId });
            trade.Parameters.Add(new NpgsqlParameter { Value = fill.SellOrderId });
            trade.Parameters.Add(new NpgsqlParameter { Value = fill.BuyerId });
            trade.Parameters.Add(new NpgsqlParameter { Value = fill.SellerId });
            trade.Parameters.Add(new NpgsqlParameter { Value = fill.AssetId });
            trade.Parameters.Add(new NpgsqlParameter { Value = fill.Variant });
            trade.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = fill.Price });
            trade.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = fill.Quantity });
            trade.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = fill.SellerTax });
            trade.Parameters.Add(new NpgsqlParameter { Value = fill.TradedAt ?? fallbackTime });
            // 仅真实插入的成交才累加结算游标；ON CONFLICT 命中的重试不重复计数。
            if (await trade.ExecuteNonQueryAsync(cancellationToken) == 1)
                await BumpSettlementCursorsAsync(connection, transaction, fill, cancellationToken);
        }

        await using var update = new NpgsqlCommand("""
            UPDATE market.orders
            SET filled_quantity = LEAST(quantity, filled_quantity + $2),
                version = version + 1,
                status = CASE WHEN filled_quantity + $2 >= quantity THEN 'filled' ELSE 'partially_filled' END,
                updated_at = now()
            WHERE order_id = $1 AND status IN ('active', 'partially_filled')
              AND filled_quantity + $2 <= quantity
            """, connection, transaction);
        update.Parameters.Add(new NpgsqlParameter { Value = fill.BuyOrderId });
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = fill.Quantity });
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("买单成交数量超出剩余冻结。");
        await using var updateSell = new NpgsqlCommand("""
            UPDATE market.orders
            SET filled_quantity = LEAST(quantity, filled_quantity + $2),
                version = version + 1,
                status = CASE WHEN filled_quantity + $2 >= quantity THEN 'filled' ELSE 'partially_filled' END,
                updated_at = now()
            WHERE order_id = $1 AND status IN ('active', 'partially_filled')
              AND filled_quantity + $2 <= quantity
            """, connection, transaction);
        updateSell.Parameters.Add(new NpgsqlParameter { Value = fill.SellOrderId });
        updateSell.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = fill.Quantity });
        if (await updateSell.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("卖单成交数量超出剩余冻结。");
    }

    /// <summary>挂单事务触及的玩家集合：属主加上每笔成交的买卖双方（自交腿按集合去重）。</summary>
    private static IEnumerable<Guid> PlacementPlayerIds(MarketOrderInput order, IReadOnlyList<MarketFillInput> fills)
    {
        yield return order.OwnerPlayerId;
        foreach (var fill in fills)
        {
            yield return fill.BuyerId;
            yield return fill.SellerId;
        }
    }

    /// <summary>
    /// 成交落库后给买卖双方各记一腿待领取结算；游标行锁与领取事务的 FOR UPDATE 物理互斥，
    /// 使领取不再依赖 Serializable 谓词读——这是消除领取侧 40001 的结构来源。
    /// </summary>
    private static async Task BumpSettlementCursorsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, MarketFillInput fill, CancellationToken cancellationToken)
    {
        var legs = new[] { fill.BuyerId, fill.SellerId }
            .GroupBy(playerId => playerId)
            .Select(group => (PlayerId: group.Key, Count: group.Count()));
        await using var batch = connection.CreateBatch();
        batch.Transaction = transaction;
        foreach (var leg in legs)
        {
            var command = new NpgsqlBatchCommand("""
                INSERT INTO market.settlement_cursors (player_id, pending_count, settled_count, updated_at)
                VALUES ($1, $2, 0, $3)
                ON CONFLICT (player_id) DO UPDATE SET
                    pending_count = market.settlement_cursors.pending_count + EXCLUDED.pending_count,
                    updated_at = EXCLUDED.updated_at
                """);
            command.Parameters.Add(new NpgsqlParameter { Value = leg.PlayerId });
            command.Parameters.Add(new NpgsqlParameter { Value = leg.Count });
            command.Parameters.Add(new NpgsqlParameter { Value = fill.TradedAt ?? DateTimeOffset.UtcNow });
            batch.BatchCommands.Add(command);
        }
        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ValidateFillOrdersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MarketFillInput fill,
        int partitionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT order_id, owner_player_id, partition_id, side, asset_id, variant,
                   price, quantity, filled_quantity, status
            FROM market.orders
            WHERE order_id = $1 OR order_id = $2
            ORDER BY order_id
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = fill.BuyOrderId });
        command.Parameters.Add(new NpgsqlParameter { Value = fill.SellOrderId });
        var rows = new Dictionary<Guid, (Guid owner, int partition, string side, string asset, string variant, double price, double quantity, double filled, string status)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[reader.GetGuid(0)] = (
                reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8), reader.GetString(9));
        }
        if (!rows.TryGetValue(fill.BuyOrderId, out var buy) || !rows.TryGetValue(fill.SellOrderId, out var sell))
            throw new InvalidOperationException("成交引用的订单不存在。");
        var buyActive = string.Equals(buy.status, "active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(buy.status, "partially_filled", StringComparison.OrdinalIgnoreCase);
        var sellActive = string.Equals(sell.status, "active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sell.status, "partially_filled", StringComparison.OrdinalIgnoreCase);
        if (buy.partition != partitionId || sell.partition != partitionId
            || !string.Equals(buy.side, "buy", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(sell.side, "sell", StringComparison.OrdinalIgnoreCase)
            || buy.owner != fill.BuyerId || sell.owner != fill.SellerId
            || !string.Equals(buy.asset, fill.AssetId, StringComparison.Ordinal)
            || !string.Equals(sell.asset, fill.AssetId, StringComparison.Ordinal)
            || !string.Equals(buy.variant, fill.Variant, StringComparison.Ordinal)
            || !string.Equals(sell.variant, fill.Variant, StringComparison.Ordinal)
            || !buyActive || !sellActive
            || fill.Price > buy.price + 1e-12
            || fill.Price < sell.price - 1e-12
            || buy.filled + fill.Quantity > buy.quantity + 1e-12
            || sell.filled + fill.Quantity > sell.quantity + 1e-12)
            throw new InvalidOperationException("成交与订单约束不一致。");
    }

    private static async Task ConsumeReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        double amount,
        double quantityUnits,
        Guid settlementId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT reservation_id, player_id, side, quantity, reserved_value
            FROM economy.reservations
            WHERE order_id = $1 AND status = 'active'
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = orderId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException($"订单 {orderId} 缺少活动冻结记录。");
        var reservationId = reader.GetGuid(0);
        var ownerPlayerId = reader.GetGuid(1);
        var side = reader.GetString(2);
        var quantity = reader.GetDouble(3);
        var reservedValue = reader.GetDouble(4);
        await reader.DisposeAsync();

        var isBuy = string.Equals(side, "buy", StringComparison.OrdinalIgnoreCase);
        if (!double.IsFinite(amount) || amount < 0 || !double.IsFinite(quantityUnits) || quantityUnits <= 0
            || quantityUnits > quantity + 1e-9
            || (isBuy && amount > reservedValue + 1e-9)
            || (!isBuy && amount != 0d))
            throw new InvalidOperationException($"订单 {orderId} 的冻结余量不足以覆盖成交。");
        var remainingQuantity = Math.Max(0d, quantity - quantityUnits);
        var remainingValue = isBuy ? Math.Max(0d, reservedValue - amount) : 0d;
        var completed = remainingQuantity <= 1e-12;
        var releaseRemainder = completed && remainingValue > 1e-12 ? remainingValue : 0d;
        if (releaseRemainder > 0)
        {
            await ChangeBalanceAsync(connection, transaction, new AssetDelta(ownerPlayerId, CoinsAsset, string.Empty, releaseRemainder), DateTimeOffset.UtcNow, cancellationToken);
            // 买单以低于限价成交时，未使用的冻结金额是实际资产返还，
            // 必须与余额变化一起写入不可变流水，不能只改余额表。
            await AppendEconomyTransactionAsync(
                connection,
                transaction,
                Guid.NewGuid(),
                $"market-refund:{orderId:D}:{settlementId:D}",
                ownerPlayerId,
                [new AssetDelta(ownerPlayerId, CoinsAsset, string.Empty, releaseRemainder)],
                DateTimeOffset.UtcNow,
                cancellationToken);
            remainingValue = 0;
        }
        await using var update = new NpgsqlCommand("""
            UPDATE economy.reservations
            SET quantity = $2, reserved_value = $3,
                status = CASE WHEN $4 THEN 'consumed' ELSE 'active' END,
                updated_at = now()
            WHERE reservation_id = $1
            """, connection, transaction);
        update.Parameters.Add(new NpgsqlParameter { Value = reservationId });
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = remainingQuantity });
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = remainingValue });
        update.Parameters.Add(new NpgsqlParameter { Value = completed });
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<double?> ReadReservationReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        bool isBuy,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT quantity, reserved_value
            FROM economy.reservations
            WHERE order_id = $1 AND status = 'active'
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = orderId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var quantity = reader.GetDouble(0);
        var reservedValue = reader.GetDouble(1);
        await reader.DisposeAsync();
        var release = isBuy ? reservedValue : quantity;
        return double.IsFinite(release) && release >= 0 ? release : null;
    }

    private static async Task<bool> TradeExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MarketFillInput fill,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT buy_order_id, sell_order_id, buyer_id, seller_id, asset_id, variant,
                   price, quantity, seller_tax
            FROM market.trades
            WHERE trade_id = $1
            FOR SHARE
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = fill.TradeId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return false;
        var matches = reader.GetGuid(0) == fill.BuyOrderId
            && reader.GetGuid(1) == fill.SellOrderId
            && reader.GetGuid(2) == fill.BuyerId
            && reader.GetGuid(3) == fill.SellerId
            && string.Equals(reader.GetString(4), fill.AssetId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(5), fill.Variant, StringComparison.Ordinal)
            && reader.GetDouble(6) == fill.Price
            && reader.GetDouble(7) == fill.Quantity
            && reader.GetDouble(8) == fill.SellerTax;
        if (!matches) throw new InvalidOperationException("成交幂等键已绑定不同成交事实。");
        return true;
    }

    private static async Task<bool> HasEconomyTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        Guid expectedActorId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT actor_id, transaction_type
            FROM economy.transactions
            WHERE idempotency_key = $1
            FOR SHARE
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = idempotencyKey });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return false;
        if (reader.GetGuid(0) != expectedActorId || !string.Equals(reader.GetString(1), "market", StringComparison.Ordinal))
            throw new EconomyIdempotencyConflictException(idempotencyKey);
        return true;
    }

    private static async Task AppendEconomyTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid transactionId,
        string idempotencyKey,
        Guid actorId,
        IReadOnlyList<AssetDelta> deltas,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (deltas.Count == 0) return;
        var requestHash = ComputeMarketRequestHash(actorId, deltas);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO economy.transactions
                (transaction_id, idempotency_key, request_hash, transaction_type, actor_id, created_at)
            VALUES ($1, $2, $3, 'market', $4, $5)
            ON CONFLICT (idempotency_key) DO NOTHING
            RETURNING transaction_id
            """, connection, transaction);
        insert.Parameters.Add(new NpgsqlParameter { Value = transactionId });
        insert.Parameters.Add(new NpgsqlParameter { Value = idempotencyKey });
        insert.Parameters.Add(new NpgsqlParameter { Value = requestHash });
        insert.Parameters.Add(new NpgsqlParameter { Value = actorId });
        insert.Parameters.Add(new NpgsqlParameter { Value = occurredAt });
        if (await insert.ExecuteScalarAsync(cancellationToken) is null)
        {
            await using var existing = new NpgsqlCommand("""
                SELECT request_hash, transaction_type, actor_id
                FROM economy.transactions
                WHERE idempotency_key = $1
                FOR SHARE
                """, connection, transaction);
            existing.Parameters.Add(new NpgsqlParameter { Value = idempotencyKey });
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || !string.Equals(reader.GetString(0), requestHash, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(1), "market", StringComparison.Ordinal)
                || reader.GetGuid(2) != actorId)
                throw new EconomyIdempotencyConflictException(idempotencyKey);
            return;
        }

        // 流水行按传入次序一次写入：unnest+ordinality 保证 line_no 与行序一致。
        var linePlayers = new Guid[deltas.Count];
        var lineAssets = new string[deltas.Count];
        var lineVariants = new string[deltas.Count];
        var lineAmounts = new double[deltas.Count];
        for (var i = 0; i < deltas.Count; i++)
        {
            var delta = deltas[i];
            linePlayers[i] = delta.PlayerId;
            lineAssets[i] = delta.AssetId;
            lineVariants[i] = delta.Variant;
            lineAmounts[i] = delta.Delta;
        }
        await using var lines = new NpgsqlCommand("""
            INSERT INTO economy.transaction_lines
                (transaction_id, line_no, player_id, asset_id, variant, delta)
            SELECT $1, (line.ord - 1)::smallint, line.player_id, line.asset_id, line.variant, line.delta
            FROM unnest($2, $3, $4, $5) WITH ORDINALITY AS line(player_id, asset_id, variant, delta, ord)
            """, connection, transaction);
        lines.Parameters.Add(new NpgsqlParameter { Value = transactionId });
        lines.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, Value = linePlayers });
        lines.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Varchar, Value = lineAssets });
        lines.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Varchar, Value = lineVariants });
        lines.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Double, Value = lineAmounts });
        await lines.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ComputeMarketRequestHash(Guid actorId, IReadOnlyList<AssetDelta> deltas)
    {
        var canonical = new StringBuilder().Append(actorId.ToString("D")).Append('\n');
        foreach (var delta in deltas
                     .OrderBy(value => value.PlayerId)
                     .ThenBy(value => value.AssetId, StringComparer.Ordinal)
                     .ThenBy(value => value.Variant, StringComparer.Ordinal))
        {
            canonical.Append(delta.PlayerId.ToString("D")).Append('|')
                .Append(delta.AssetId).Append('|')
                .Append(delta.Variant).Append('|')
                .Append(delta.Delta.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// 单语句余额增减：存在则累加、不存在则按增量插入；结算结果越界（负余额/非有限）时
    /// 两侧 WHERE 静默跳过，随后按当前余额抛出与旧实现一致的余额不足错误。
    /// 与旧的 FOR UPDATE 锁读 + upsert 语义一致，但只有 1 次往返。
    /// </summary>
    private static async Task ChangeBalanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, AssetDelta delta, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // CHECK 约束作用于「候选插入行」本身（先于冲突裁决），因此候选 quantity 不能直接放增量：
        // 行已存在时（EXISTS 放行）候选值必须为合法余额，此处取 0 占位——冲突后由 DO UPDATE
        // 计算真实结果，其 WHERE 守卫在被拒元组上不触发 CHECK（已验证行为）。
        await using var command = new NpgsqlCommand("""
            INSERT INTO economy.player_asset_balances
                (player_id, asset_id, variant, quantity, version, updated_at)
            SELECT $1, $2, $3,
                   CASE WHEN $4 >= 0 AND $4 < 'Infinity'::double precision THEN $4 ELSE 0::double precision END,
                   1, $5
            WHERE ($4 >= 0 AND $4 < 'Infinity'::double precision)
               OR EXISTS (
                   SELECT 1 FROM economy.player_asset_balances b
                   WHERE b.player_id = $1 AND b.asset_id = $2 AND b.variant = $3)
            ON CONFLICT (player_id, asset_id, variant) DO UPDATE SET
                quantity = economy.player_asset_balances.quantity + $4,
                version = economy.player_asset_balances.version + 1,
                updated_at = EXCLUDED.updated_at
            WHERE economy.player_asset_balances.quantity + $4 >= 0
              AND economy.player_asset_balances.quantity + $4 < 'Infinity'::double precision
            RETURNING quantity
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = delta.PlayerId });
        command.Parameters.Add(new NpgsqlParameter { Value = delta.AssetId });
        command.Parameters.Add(new NpgsqlParameter { Value = delta.Variant });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = delta.Delta });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        if (await command.ExecuteScalarAsync(cancellationToken) is double)
            return;
        // 新行值域守卫或既有行余额不足拒绝了写入：读取当前余额构造与旧实现一致的错误证据（事务随即回滚）。
        double current;
        await using (var probe = new NpgsqlCommand("""
            SELECT quantity FROM economy.player_asset_balances
            WHERE player_id = $1 AND asset_id = $2 AND variant = $3
            """, connection, transaction))
        {
            probe.Parameters.Add(new NpgsqlParameter { Value = delta.PlayerId });
            probe.Parameters.Add(new NpgsqlParameter { Value = delta.AssetId });
            probe.Parameters.Add(new NpgsqlParameter { Value = delta.Variant });
            current = await probe.ExecuteScalarAsync(cancellationToken) is double quantity ? quantity : 0;
        }
        throw new EconomyInsufficientBalanceException(delta, current);
    }

    private static void ValidateOrder(MarketOrderInput order)
    {
        if (order.OrderId == Guid.Empty || order.OwnerPlayerId == Guid.Empty) throw new ArgumentException("订单标识无效。", nameof(order));
        if (order.PartitionId < 0 || string.IsNullOrWhiteSpace(order.AssetId) || order.AssetId.Length > 128) throw new ArgumentException("订单品种无效。", nameof(order));
        if (!order.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) && !order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("订单方向无效。", nameof(order));
        if (order.Variant is null || order.Variant.Length > 128 || order.Price <= 0 || order.Quantity <= 0 || double.IsNaN(order.Price) || double.IsInfinity(order.Price) || double.IsNaN(order.Quantity) || double.IsInfinity(order.Quantity) || order.Price != Math.Floor(order.Price) || order.Quantity != Math.Floor(order.Quantity)) throw new ArgumentException("订单数值无效。", nameof(order));
        if (string.IsNullOrWhiteSpace(order.IdempotencyKey) || order.IdempotencyKey.Length > 200) throw new ArgumentException("订单幂等键无效。", nameof(order));
    }

    private static void ValidateFill(MarketFillInput fill, int partitionId)
    {
        if (fill.TradeId == Guid.Empty || fill.BuyOrderId == Guid.Empty || fill.SellOrderId == Guid.Empty || fill.BuyerId == Guid.Empty || fill.SellerId == Guid.Empty || partitionId < 0 || string.IsNullOrWhiteSpace(fill.AssetId) || fill.AssetId.Length > 128 || fill.Variant is null || fill.Variant.Length > 128 || fill.Price <= 0 || fill.Quantity <= 0 || fill.SellerTax < 0 || double.IsNaN(fill.Price) || double.IsInfinity(fill.Price) || double.IsNaN(fill.Quantity) || double.IsInfinity(fill.Quantity) || double.IsNaN(fill.SellerTax) || double.IsInfinity(fill.SellerTax))
            throw new ArgumentException("成交参数无效。", nameof(fill));
    }

    private static async Task ValidateCrossTradeEligibilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid buyerId,
        Guid sellerId,
        CancellationToken cancellationToken)
    {
        if (buyerId == sellerId)
            throw new MarketProhibitedTradeException("不可购买自身订单。");

        // 1. 邀请上下级即时熔断
        await using (var inviteCheck = new NpgsqlCommand("""
            SELECT 1 FROM social.player_invitations
            WHERE (invitee_id = $1 AND inviter_id = $2)
               OR (invitee_id = $2 AND inviter_id = $1)
            LIMIT 1
            """, connection, transaction))
        {
            inviteCheck.Parameters.Add(new NpgsqlParameter { Value = buyerId });
            inviteCheck.Parameters.Add(new NpgsqlParameter { Value = sellerId });
            if (await inviteCheck.ExecuteScalarAsync(cancellationToken) is not null)
                throw new MarketProhibitedTradeException("结义邀请关联同道之间不可定向成交。");
        }

        // 2. 同设备 / 同主体即时熔断 (基于 identity.account_risk_signals)
        await using (var riskCheck = new NpgsqlCommand("""
            SELECT 1
            FROM identity.players pb
            JOIN identity.players ps ON ps.player_id = $2
            JOIN identity.account_risk_signals sb ON sb.account_id = pb.account_id
            JOIN identity.account_risk_signals ss ON ss.account_id = ps.account_id
            WHERE pb.player_id = $1
              AND sb.signal_kind IN ('person', 'device', 'fingerprint')
              AND ss.signal_kind = sb.signal_kind
              AND ss.signal_hash = sb.signal_hash
            LIMIT 1
            """, connection, transaction))
        {
            riskCheck.Parameters.Add(new NpgsqlParameter { Value = buyerId });
            riskCheck.Parameters.Add(new NpgsqlParameter { Value = sellerId });
            if (await riskCheck.ExecuteScalarAsync(cancellationToken) is not null)
                throw new MarketProhibitedTradeException("同设备或关联主体账号之间不可定向成交。");
        }
    }
}

/// <summary>
/// 坊市交易风控即时熔断异常（自买自卖、结义邀请小号向大号接盘、同设备主体互刷倒金）。
/// </summary>
public sealed class MarketProhibitedTradeException(string message) : ArgumentException(message);

/// <summary>到期合批关单的目标订单（由内存盘口到期索引收集）。</summary>
/// <param name="OrderId">订单唯一标识。</param>
/// <param name="OwnerPlayerId">订单属主玩家。</param>
public sealed record MarketExpiryOrder(Guid OrderId, Guid OwnerPlayerId);

/// <summary>分片租约 fence 校验失败时抛出：当前实例已不再是该分片的合法写者，必须中止本次账本写入。</summary>
/// <param name="partitionId">丢失租约的分片。</param>
/// <param name="fenceToken">调用方持有但已失效的 fence。</param>
public sealed class MarketLeaseLostException(int partitionId, long fenceToken)
    : InvalidOperationException($"市场分片租约已失效 partition={partitionId} fence={fenceToken}");
