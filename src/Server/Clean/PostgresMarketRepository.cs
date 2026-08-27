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
    private static readonly string CoinsAsset = "spirit";
    public NpgsqlDataSource DataSource => dataSource;

    public async ValueTask<IReadOnlyList<StoredMarketOrder>> LoadActiveOrdersAsync(CancellationToken cancellationToken = default)
    {
        var orders = new List<StoredMarketOrder>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT order_id, owner_player_id, asset_id, variant, side, price, quantity,
                   filled_quantity, status, sequence, created_at
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
                reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetString(8), reader.GetInt64(9), reader.GetFieldValue<DateTimeOffset>(10)));
        }
        return orders;
    }

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
                   filled_quantity, status, sequence, created_at
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
                reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetString(8), reader.GetInt64(9), reader.GetFieldValue<DateTimeOffset>(10)));
        }
        return orders;
    }

    public async ValueTask<StoredMarketOrder?> LoadOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        if (orderId == Guid.Empty) return null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT order_id, owner_player_id, asset_id, variant, side, price, quantity,
                   filled_quantity, status, sequence, created_at
            FROM market.orders
            WHERE order_id = $1 AND partition_id = $2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = orderId });
        command.Parameters.Add(new NpgsqlParameter { Value = options.MarketPartitionId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredMarketOrder(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetString(8), reader.GetInt64(9), reader.GetFieldValue<DateTimeOffset>(10))
            : null;
    }

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

    public async ValueTask<MarketCommitResult> CommitPlacementAsync(
        MarketOrderInput order,
        IReadOnlyList<MarketFillInput> fills,
        long fenceToken,
        CancellationToken cancellationToken = default)
    {
        ValidateOrder(order);
        if (order.PartitionId != options.MarketPartitionId)
            throw new ArgumentException("订单分片与当前市场写者不一致。", nameof(order));
        if (order.PartitionId != options.MarketPartitionId)
            throw new ArgumentException("订单分片与当前写者不一致。", nameof(order));
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

    public async ValueTask<bool> CancelAsync(
        Guid orderId,
        Guid ownerPlayerId,
        int partitionId,
        long fenceToken,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (orderId == Guid.Empty || ownerPlayerId == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey)) return false;
        if (partitionId != options.MarketPartitionId)
            throw new ArgumentException("撤单分片与当前写者不一致。", nameof(partitionId));
        var canonicalIdempotencyKey = $"market-cancel:{ownerPlayerId:D}:{orderId:D}";
        if (!string.Equals(idempotencyKey, canonicalIdempotencyKey, StringComparison.Ordinal))
            throw new ArgumentException("撤单幂等键必须绑定当前玩家和订单。", nameof(idempotencyKey));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        await EnsureLeaseAsync(connection, transaction, partitionId, fenceToken, cancellationToken);
        if (await HasEconomyTransactionAsync(connection, transaction, canonicalIdempotencyKey, ownerPlayerId, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        await using var order = new NpgsqlCommand("""
            SELECT owner_player_id, side, asset_id, variant, status
            FROM market.orders WHERE order_id = $1 AND partition_id = $2 FOR UPDATE
            """, connection, transaction);
        order.Parameters.Add(new NpgsqlParameter { Value = orderId });
        order.Parameters.Add(new NpgsqlParameter { Value = partitionId });
        var orderOwnerId = Guid.Empty;
        var side = string.Empty;
        var assetId = string.Empty;
        var variant = string.Empty;
        var status = string.Empty;
        var orderFound = false;
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
            }
        }
        if (!orderFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        if (orderOwnerId != ownerPlayerId || status is not ("active" or "partially_filled"))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

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
        await using (var update = new NpgsqlCommand("UPDATE market.orders SET status = 'cancelled', version = version + 1, updated_at = now() WHERE order_id = $1", connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = orderId });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var reservation = new NpgsqlCommand("UPDATE economy.reservations SET status = 'released', updated_at = now() WHERE order_id = $1 AND status = 'active'", connection, transaction))
        {
            reservation.Parameters.Add(new NpgsqlParameter { Value = orderId });
            await reservation.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
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
                 quantity, filled_quantity, status, sequence, idempotency_key, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, 0, 'active',
                    (SELECT COALESCE(max(sequence), 0) + 1 FROM market.orders WHERE partition_id = $2), $9, $10, $10)
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

        for (var lineNo = 0; lineNo < deltas.Count; lineNo++)
        {
            var delta = deltas[lineNo];
            await using var line = new NpgsqlCommand("""
                INSERT INTO economy.transaction_lines
                    (transaction_id, line_no, player_id, asset_id, variant, delta)
                VALUES ($1, $2, $3, $4, $5, $6)
                """, connection, transaction);
            line.Parameters.Add(new NpgsqlParameter { Value = transactionId });
            line.Parameters.Add(new NpgsqlParameter { Value = lineNo });
            line.Parameters.Add(new NpgsqlParameter { Value = delta.PlayerId });
            line.Parameters.Add(new NpgsqlParameter { Value = delta.AssetId });
            line.Parameters.Add(new NpgsqlParameter { Value = delta.Variant });
            line.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = delta.Delta });
            await line.ExecuteNonQueryAsync(cancellationToken);
        }
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

    private static async Task ChangeBalanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, AssetDelta delta, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT quantity, version FROM economy.player_asset_balances
            WHERE player_id = $1 AND asset_id = $2 AND variant = $3 FOR UPDATE
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = delta.PlayerId });
        command.Parameters.Add(new NpgsqlParameter { Value = delta.AssetId });
        command.Parameters.Add(new NpgsqlParameter { Value = delta.Variant });
        double current = 0;
        long version = 0;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                current = reader.GetDouble(0);
                version = reader.GetInt64(1);
            }
        }
        var next = current + delta.Delta;
        if (double.IsNaN(next) || double.IsInfinity(next) || next < 0) throw new EconomyInsufficientBalanceException(delta, current);
        await using var update = new NpgsqlCommand("""
            INSERT INTO economy.player_asset_balances
                (player_id, asset_id, variant, quantity, version, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (player_id, asset_id, variant) DO UPDATE SET
                quantity = EXCLUDED.quantity, version = EXCLUDED.version, updated_at = EXCLUDED.updated_at
            """, connection, transaction);
        update.Parameters.Add(new NpgsqlParameter { Value = delta.PlayerId });
        update.Parameters.Add(new NpgsqlParameter { Value = delta.AssetId });
        update.Parameters.Add(new NpgsqlParameter { Value = delta.Variant });
        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = next });
        update.Parameters.Add(new NpgsqlParameter { Value = version + 1 });
        update.Parameters.Add(new NpgsqlParameter { Value = now });
        await update.ExecuteNonQueryAsync(cancellationToken);
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

public sealed class MarketLeaseLostException(int partitionId, long fenceToken)
    : InvalidOperationException($"市场分片租约已失效 partition={partitionId} fence={fenceToken}");
