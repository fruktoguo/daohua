namespace Server.Clean;

/// <summary>
/// 市场订单、冻结、成交和资产转移的 PostgreSQL 事务实现。
/// 订单簿本身是可重建投影；本类只提交权威订单与资产事实。
/// </summary>
public sealed class PostgresMarketRepository(
    NpgsqlDataSource dataSource,
    ServerOptions options,
    ILogger<PostgresMarketRepository> logger) : IMarketTransactionStore
{
    private static readonly string CoinsAsset = "currency.spirit-stone";
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
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
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

        var previous = DateTimeOffset.MinValue;
        await using (var cursor = new NpgsqlCommand("""
            SELECT claimed_at
            FROM market.settlement_claims
            WHERE player_id = $1
            ORDER BY claimed_at DESC
            LIMIT 1
            FOR UPDATE
            """, connection, transaction))
        {
            cursor.Parameters.Add(new NpgsqlParameter { Value = playerId });
            var value = await cursor.ExecuteScalarAsync(cancellationToken);
            if (value is DateTimeOffset timestamp) previous = timestamp;
        }

        int settlementCount;
        await using (var countCommand = new NpgsqlCommand("""
            SELECT count(*)::int
            FROM market.trades
            WHERE traded_at > $2 AND (buyer_id = $1 OR seller_id = $1)
            """, connection, transaction))
        {
            countCommand.Parameters.Add(new NpgsqlParameter { Value = playerId });
            countCommand.Parameters.Add(new NpgsqlParameter { Value = previous });
            settlementCount = (int)(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0);
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

    /// <summary>
    /// 抢占本分片写者租约。仅在租约不存在、已过期或本就属于本实例时成功；
    /// 每次成功接管 fence_token 递增，使旧写者未提交的事务因 fence 校验失败而无法再写。
    /// </summary>
    public async ValueTask<bool> AcquireLeaseAsync(Guid ownerInstanceId, CancellationToken cancellationToken = default)
    {
        if (ownerInstanceId == Guid.Empty) throw new ArgumentException("ownerInstanceId 不能为空。", nameof(ownerInstanceId));
        var now = DateTimeOffset.UtcNow;
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

    /// <summary>
    /// 在 Serializable 事务内提交挂单：校验分片租约 fence → 写订单 → 冻结挂单资产 → 逐笔应用成交。
    /// 幂等键命中已存订单时核对参数一致后直接返回，不重复冻结或成交。
    /// </summary>
    /// <param name="order">挂单输入，分片必须与当前写者一致。</param>
    /// <param name="fills">本次撮合产生的成交列表（可为空）。</param>
    /// <param name="fenceToken">当前持有的分片租约 fence。</param>
    /// <returns>提交结局；AlreadyCommitted=true 表示幂等重放。</returns>
    public async ValueTask<MarketCommitResult> CommitPlacementAsync(
        MarketOrderInput order,
        IReadOnlyList<MarketFillInput> fills,
        long fenceToken,
        CancellationToken cancellationToken = default)
    {
        ValidateOrder(order);
        if (order.PartitionId != options.MarketPartitionId)
            throw new ArgumentException("订单分片与当前市场写者不一致。", nameof(order));
        if (fenceToken <= 0) throw new ArgumentOutOfRangeException(nameof(fenceToken));
        if (fills is null || fills.Count > 256)
            throw new ArgumentException("单次成交数量超过上限。", nameof(fills));
        var now = order.CreatedAt ?? DateTimeOffset.UtcNow;
        var total = order.Price * order.Quantity;
        if (double.IsNaN(total) || double.IsInfinity(total) || total <= 0)
            throw new ArgumentException("订单总额无效。", nameof(order));

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

        await transaction.CommitAsync(cancellationToken);
        logger.LogDebug("市场挂单提交 orderId={OrderId} fills={FillCount}", order.OrderId, fills.Count);
        return new MarketCommitResult(order.OrderId, true, false, tradeIds);
    }

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
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        await EnsureLeaseAsync(connection, transaction, options.MarketPartitionId, fenceToken, cancellationToken);
        foreach (var target in orders)
        {
            var canonicalIdempotencyKey = $"market-expire:{target.OwnerPlayerId:D}:{target.OrderId:D}";
            if (!await CloseOrderCoreAsync(connection, transaction, target.OrderId, target.OwnerPlayerId,
                    canonicalIdempotencyKey, cutoff, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
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
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        await EnsureLeaseAsync(connection, transaction, partitionId, fenceToken, cancellationToken);
        if (!await CloseOrderCoreAsync(connection, transaction, orderId, ownerPlayerId, canonicalIdempotencyKey, expirationCutoff, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// 关单事务体（撤单/到期共用）：在已持有 Serializable 事务与租约校验的连接上，
    /// 完成幂等短路、订单行锁校验、冻结退款、流水追加与状态更新。
    /// 返回 false 表示订单不可关闭（不存在/属主不符/非活动/未到期），由调用方决定回滚。
    /// </summary>
    private async Task<bool> CloseOrderCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid orderId,
        Guid ownerPlayerId,
        string canonicalIdempotencyKey,
        DateTimeOffset? expirationCutoff,
        CancellationToken cancellationToken)
    {
        if (await HasEconomyTransactionAsync(connection, transaction, canonicalIdempotencyKey, ownerPlayerId, cancellationToken))
            return true;
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
            return false;
        if (orderOwnerId != ownerPlayerId || status is not ("active" or "partially_filled")
            || (expirationCutoff.HasValue && expiresAt > expirationCutoff.Value))
            return false;

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
        return true;
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
            await trade.ExecuteNonQueryAsync(cancellationToken);
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
        if (order.Variant is null || order.Variant.Length > 128 || order.Price <= 0 || order.Quantity <= 0 || double.IsNaN(order.Price) || double.IsInfinity(order.Price) || double.IsNaN(order.Quantity) || double.IsInfinity(order.Quantity)) throw new ArgumentException("订单数值无效。", nameof(order));
        if (string.IsNullOrWhiteSpace(order.IdempotencyKey) || order.IdempotencyKey.Length > 200) throw new ArgumentException("订单幂等键无效。", nameof(order));
    }

    private static void ValidateFill(MarketFillInput fill, int partitionId)
    {
        if (fill.TradeId == Guid.Empty || fill.BuyOrderId == Guid.Empty || fill.SellOrderId == Guid.Empty || fill.BuyerId == Guid.Empty || fill.SellerId == Guid.Empty || partitionId < 0 || string.IsNullOrWhiteSpace(fill.AssetId) || fill.AssetId.Length > 128 || fill.Variant is null || fill.Variant.Length > 128 || fill.Price <= 0 || fill.Quantity <= 0 || fill.SellerTax < 0 || double.IsNaN(fill.Price) || double.IsInfinity(fill.Price) || double.IsNaN(fill.Quantity) || double.IsInfinity(fill.Quantity) || double.IsNaN(fill.SellerTax) || double.IsInfinity(fill.SellerTax))
            throw new ArgumentException("成交参数无效。", nameof(fill));
    }
}

/// <summary>到期合批关单的目标订单（由内存盘口到期索引收集）。</summary>
/// <param name="OrderId">订单唯一标识。</param>
/// <param name="OwnerPlayerId">订单属主玩家。</param>
public sealed record MarketExpiryOrder(Guid OrderId, Guid OwnerPlayerId);

/// <summary>分片租约 fence 校验失败时抛出：当前实例已不再是该分片的合法写者，必须中止本次账本写入。</summary>
/// <param name="partitionId">丢失租约的分片。</param>
/// <param name="fenceToken">调用方持有但已失效的 fence。</param>
public sealed class MarketLeaseLostException(int partitionId, long fenceToken)
    : InvalidOperationException($"市场分片租约已失效 partition={partitionId} fence={fenceToken}");
