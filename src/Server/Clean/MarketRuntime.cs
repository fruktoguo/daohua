using Domain.Common;
using Domain.Market;

namespace Server.Clean;

/// <summary>市场分片运行时：内存 OrderBook 是盘口投影，PostgreSQL 事务是订单与资产权威。</summary>
public sealed class MarketRuntime : IHostedService, IAsyncDisposable
{
    private readonly PostgresMarketRepository _store;
    private readonly ServerOptions _options;
    private readonly ILogger<MarketRuntime> _logger;
    private readonly ConcurrentDictionary<MarketInstrument, OrderBook> _books = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _booksGate = new();
    private readonly Guid _ownerInstanceId = Guid.NewGuid();
    private CancellationTokenSource? _lifetime;
    private Task? _renewTask;
    private long _fenceToken;
    private volatile bool _ready;
    private long _placementCount;
    private long _matchCount;
    private long _cancelCount;
    private long _rejectedCount;
    private readonly LogThrottle _persistenceErrorLog = new(TimeSpan.FromMinutes(1));

    public MarketRuntime(PostgresMarketRepository store, ServerOptions options, ILogger<MarketRuntime> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    public bool IsReady => _ready;
    public int PartitionId => _options.MarketPartitionId;
    public int PartitionCount => _options.MarketPartitionCount;
    public string Algorithm => MarketPartitioner.Algorithm;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        const int maxAcquireAttempts = 40;
        var acquired = false;
        for (var attempt = 1; attempt <= maxAcquireAttempts; attempt++)
        {
            if (await _store.AcquireLeaseAsync(_ownerInstanceId, cancellationToken))
            {
                acquired = true;
                break;
            }

            if (attempt < maxAcquireAttempts)
            {
                _logger.LogWarning(
                    "市场分片租约暂不可用 partition={PartitionId} attempt={Attempt}/{MaxAttempts}，等待旧实例租约自然过期",
                    _options.MarketPartitionId,
                    attempt,
                    maxAcquireAttempts);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        if (!acquired)
            throw new InvalidOperationException($"无法取得市场分片 {_options.MarketPartitionId} 写者租约：已有实例持续持有租约。");
        try
        {
            // 当前存储实现返回 fence 的 API 在租约表中单调递增；读取最新值用于每次事务 fencing。
            _fenceToken = await ReadFenceTokenAsync(cancellationToken);
            var orders = await _store.LoadActiveOrdersAsync(cancellationToken);
            foreach (var group in orders.GroupBy(order => new MarketInstrument(new ItemId(order.AssetId), order.Variant)))
            {
                var book = _books.GetOrAdd(group.Key, static key => new OrderBook(key));
                var restored = group.Select(stored => ToDomainOrder(stored, group.Key)).ToArray();
                book.Restore(restored, restored.Max(order => order.Sequence));
            }
            _ready = true;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _renewTask = RenewLeaseLoopAsync(_lifetime.Token);
            _logger.LogInformation("MarketRuntime ready partition={PartitionId}/{PartitionCount} orders={OrderCount}", PartitionId, PartitionCount, orders.Count);
        }
        catch
        {
            _ready = false;
            await ReleaseLeaseBestEffortAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ready = false;
        _lifetime?.Cancel();
        if (_renewTask is not null)
        {
            try { await _renewTask; } catch (OperationCanceledException) { }
        }
        await ReleaseLeaseBestEffortAsync(cancellationToken);
        _lifetime?.Dispose();
    }

    private async Task ReleaseLeaseBestEffortAsync(CancellationToken cancellationToken)
    {
        if (_fenceToken <= 0) return;
        try
        {
            await using var connection = await _store.DataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                UPDATE market.partition_leases
                SET expires_at = now(), renewed_at = now()
                WHERE partition_id = $1 AND owner_instance_id = $2 AND fence_token = $3
                """, connection);
            command.Parameters.Add(new NpgsqlParameter { Value = PartitionId });
            command.Parameters.Add(new NpgsqlParameter { Value = _ownerInstanceId });
            command.Parameters.Add(new NpgsqlParameter { Value = _fenceToken });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "市场分片租约释放失败 partition={PartitionId}", PartitionId);
        }
    }

    public bool Owns(MarketInstrument instrument) => MarketPartitioner.GetPartition(instrument, PartitionCount) == PartitionId;

    public OrderBookSnapshot GetBook(MarketInstrument instrument, int depth = 20)
    {
        if (!_ready) throw new InvalidOperationException("市场分片尚未就绪。");
        if (!Owns(instrument)) throw new InvalidOperationException("该品种不属于当前市场分片。");
        lock (_booksGate)
        {
            return _books.GetOrAdd(instrument, static key => new OrderBook(key)).Snapshot(depth);
        }
    }

    public IReadOnlyList<MarketOrder> GetOrders(PlayerId ownerId, int limit = 100)
    {
        if (!_ready) throw new InvalidOperationException("市场分片尚未就绪。");
        lock (_booksGate)
        {
            return _books.Values.SelectMany(book => book.OrdersByOwner(ownerId, limit)).OrderByDescending(order => order.Sequence).Take(limit).ToArray();
        }
    }

    public async ValueTask<MarketOrder?> GetOrderAsync(Guid orderId, PlayerId ownerId, CancellationToken cancellationToken = default)
    {
        if (orderId == Guid.Empty || ownerId.IsEmpty) return null;
        lock (_booksGate)
        {
            foreach (var book in _books.Values)
                if (book.TryGet(new OrderId(orderId), out var inMemory))
                    return inMemory.OwnerId == ownerId ? inMemory : null;
        }
        var stored = await _store.LoadOrderAsync(orderId, cancellationToken);
        if (stored is null || stored.OwnerPlayerId != ownerId.Value) return null;
        var instrument = new MarketInstrument(new ItemId(stored.AssetId), stored.Variant);
        return ToDomainOrder(stored, instrument);
    }

    public ValueTask<SettlementClaimResult> ClaimSettlementAsync(PlayerId playerId, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (!_ready) throw new InvalidOperationException("市场分片尚未就绪。");
        if (PartitionCount != 1)
            throw new InvalidOperationException("多市场分片尚未提供跨分片结算领取路由，当前请求已拒绝。");
        return _store.ClaimSettlementAsync(playerId.Value, requestId, cancellationToken);
    }

    public MarketRuntimeMetrics GetMetrics() => new(
        PartitionCount,
        ActiveOrderCount,
        Interlocked.Read(ref _placementCount),
        Interlocked.Read(ref _matchCount),
        Interlocked.Read(ref _cancelCount),
        Interlocked.Read(ref _rejectedCount));

    public async ValueTask<PlacementResult> PlaceAsync(PlaceOrderRequest request, CancellationToken cancellationToken = default)
    {
        EnsureReady(request.Instrument);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            OrderBook book;
            MarketOrder? existingOrder = null;
            lock (_booksGate)
            {
                book = _books.GetOrAdd(request.Instrument, static key => new OrderBook(key));
                if (book.TryGet(request.OrderId, out var found)) existingOrder = found;
            }
            if (existingOrder is not null)
            {
                if (existingOrder.OwnerId != request.OwnerId || existingOrder.Instrument != request.Instrument || existingOrder.Side != request.Side
                    || existingOrder.Price != request.Price || existingOrder.OriginalQuantity != request.Quantity)
                    throw new DomainException(DomainErrorCode.Conflict, "订单标识已绑定其他订单参数。");
                return new PlacementResult(existingOrder, [], book.Version);
            }
            var persisted = await _store.LoadOrderAsync(request.OrderId.Value, cancellationToken);
            if (persisted is not null)
            {
                var persistedOrder = ToDomainOrder(persisted, request.Instrument);
                if (persistedOrder.OwnerId != request.OwnerId || persistedOrder.Instrument != request.Instrument || persistedOrder.Side != request.Side
                    || persistedOrder.Price != request.Price || persistedOrder.OriginalQuantity != request.Quantity)
                    throw new DomainException(DomainErrorCode.Conflict, "订单标识已绑定其他订单参数。");
                return new PlacementResult(persistedOrder, [], book.Version);
            }

            var working = new OrderBook(request.Instrument);
            lock (_booksGate) working.Restore(book.ExportActiveOrders(), book.Version);
            var result = working.Place(request);
            var input = new MarketOrderInput(
                request.OrderId.Value,
                PartitionId,
                request.OwnerId.Value,
                request.Side == MarketSide.Buy ? "buy" : "sell",
                request.Instrument.ItemId.Value,
                request.Instrument.Variant,
                request.Price,
                request.Quantity,
                request.IdempotencyKey.Value,
                request.CreatedAt);
            var fills = result.Fills.Select(fill => new MarketFillInput(
                fill.TradeId.Value,
                fill.BuyOrderId.Value,
                fill.SellOrderId.Value,
                fill.BuyerId.Value,
                fill.SellerId.Value,
                fill.Instrument.ItemId.Value,
                fill.Instrument.Variant,
                fill.Price,
                fill.Quantity,
                0d,
                fill.TradedAt)).ToArray();
            var committed = await _store.CommitPlacementAsync(input, fills, _fenceToken, cancellationToken);
            if (!committed.Applied)
                throw new InvalidOperationException("市场挂单事务未提交。");
            if (committed.AlreadyCommitted)
            {
                // 发生跨请求重试或进程内存与数据库短暂分叉时，数据库事实优先；
                // 重新读取当前品种的活动订单，绝不把本地 working book 当成权威结果。
                var durableOrders = await _store.LoadActiveOrdersAsync(request.Instrument, cancellationToken);
                var durableBook = new OrderBook(request.Instrument);
                var durableDomain = durableOrders
                    .Select(stored => ToDomainOrder(stored, request.Instrument))
                    .ToArray();
                durableBook.Restore(durableDomain, durableDomain.Length == 0 ? 0 : durableDomain.Max(order => order.Sequence));
                lock (_booksGate) book.Restore(durableBook.ExportActiveOrders(), durableBook.Version);
                var durableIncoming = durableDomain.FirstOrDefault(order => order.OrderId == request.OrderId)
                    ?? throw new InvalidOperationException("市场幂等订单已提交但无法读取持久化结果。");
                return new PlacementResult(durableIncoming, [], durableBook.Version);
            }
            lock (_booksGate) book.Restore(working.ExportActiveOrders(), working.Version);
            Interlocked.Increment(ref _placementCount);
            Interlocked.Add(ref _matchCount, fills.Length);
            return result;
        }
        catch (Exception exception) when (IsExpectedRequestFailure(exception))
        {
            Interlocked.Increment(ref _rejectedCount);
            throw;
        }
        catch (Exception exception)
        {
            // DB 事务失败后内存盘口不能继续作为真相；标记未就绪，阻止后续写入，待进程重启恢复。
            _ready = false;
            if (_persistenceErrorLog.TryAcquire())
                _logger.LogCritical(exception, "市场下单持久化边界失败 partition={PartitionId}", PartitionId);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<CancellationResult> CancelAsync(OrderId orderId, PlayerId ownerId, MarketInstrument instrument, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        EnsureReady(instrument);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            OrderBook book;
            bool hasInMemory;
            lock (_booksGate)
            {
                book = _books.GetOrAdd(instrument, static key => new OrderBook(key));
                hasInMemory = book.TryGet(orderId, out _);
            }
            if (!hasInMemory)
            {
                var persisted = await _store.LoadOrderAsync(orderId.Value, cancellationToken)
                    ?? throw new DomainException(DomainErrorCode.NotFound, "订单不存在。");
                var persistedOrder = ToDomainOrder(persisted, instrument);
                if (persistedOrder.OwnerId != ownerId) throw new DomainException(DomainErrorCode.Conflict, "只能撤销自己的订单。");
                if (persistedOrder.Status == MarketOrderStatus.Cancelled) return new CancellationResult(persistedOrder, book.Version);
                throw new DomainException(DomainErrorCode.InvalidTransition, "订单已处于终态。");
            }
            var working = new OrderBook(instrument);
            lock (_booksGate) working.Restore(book.ExportActiveOrders(), book.Version);
            var result = working.Cancel(orderId, ownerId);
            if (!await _store.CancelAsync(orderId.Value, ownerId.Value, PartitionId, _fenceToken, idempotencyKey, cancellationToken))
                throw new InvalidOperationException("市场撤单事务未提交。");
            lock (_booksGate) book.Restore(working.ExportActiveOrders(), working.Version);
            Interlocked.Increment(ref _cancelCount);
            return result;
        }
        catch (Exception exception) when (IsExpectedRequestFailure(exception))
        {
            Interlocked.Increment(ref _rejectedCount);
            throw;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _rejectedCount);
            _ready = false;
            if (_persistenceErrorLog.TryAcquire())
                _logger.LogCritical(exception, "市场撤单持久化边界失败 partition={PartitionId}", PartitionId);
            throw;
        }
        finally { _gate.Release(); }
    }

    private static bool IsExpectedRequestFailure(Exception exception) => exception switch
    {
        OperationCanceledException => true,
        DomainException => true,
        EconomyInsufficientBalanceException => true,
        EconomyIdempotencyConflictException => true,
        ArgumentException => true,
        _ => false
    };

    private void EnsureReady(MarketInstrument instrument)
    {
        if (!_ready) throw new InvalidOperationException("市场分片尚未就绪。");
        if (!Owns(instrument)) throw new InvalidOperationException("市场品种不属于当前分片。");
    }

    private async Task<long> ReadFenceTokenAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _store.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT fence_token
            FROM market.partition_leases
            WHERE partition_id = $1 AND owner_instance_id = $2 AND expires_at > now()
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = PartitionId });
        command.Parameters.Add(new NpgsqlParameter { Value = _ownerInstanceId });
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long token && token > 0 ? token : throw new InvalidOperationException("市场租约 fence token 无效。");
    }

    private async Task RenewLeaseLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                await using var connection = await _store.DataSource.OpenConnectionAsync(cancellationToken);
                await using var command = new NpgsqlCommand("""
                    UPDATE market.partition_leases
                    SET expires_at = $1, renewed_at = $2
                    WHERE partition_id = $3 AND owner_instance_id = $4 AND fence_token = $5
                    """, connection);
                var now = DateTimeOffset.UtcNow;
                command.Parameters.Add(new NpgsqlParameter { Value = now.Add(_options.SectionFlushLease) });
                command.Parameters.Add(new NpgsqlParameter { Value = now });
                command.Parameters.Add(new NpgsqlParameter { Value = PartitionId });
                command.Parameters.Add(new NpgsqlParameter { Value = _ownerInstanceId });
                command.Parameters.Add(new NpgsqlParameter { Value = _fenceToken });
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    _ready = false;
                    _logger.LogCritical("市场分片租约丢失 partition={PartitionId} fence={FenceToken}", PartitionId, _fenceToken);
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogError(ex, "市场租约续租失败 partition={PartitionId}", PartitionId); _ready = false; return; }
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        _lifetime?.Dispose();
        return ValueTask.CompletedTask;
    }

    private int ActiveOrderCount
    {
        get
        {
            lock (_booksGate) return _books.Values.Sum(book => book.ActiveOrders.Count);
        }
    }

    private static MarketOrder ToDomainOrder(StoredMarketOrder stored, MarketInstrument instrument) => new(
        new OrderId(stored.OrderId),
        new PlayerId(stored.OwnerPlayerId),
        instrument,
        stored.Side.Equals("buy", StringComparison.OrdinalIgnoreCase)
            ? MarketSide.Buy
            : stored.Side.Equals("sell", StringComparison.OrdinalIgnoreCase)
                ? MarketSide.Sell
                : throw new InvalidDataException("数据库市场订单方向无效。"),
        stored.Price,
        stored.Quantity,
        Math.Max(0d, stored.Quantity - stored.FilledQuantity),
        stored.Sequence,
        stored.CreatedAt,
        null,
        stored.Status switch
        {
            "active" => MarketOrderStatus.Active,
            "partially_filled" => MarketOrderStatus.PartiallyFilled,
            "filled" => MarketOrderStatus.Filled,
            "cancelled" => MarketOrderStatus.Cancelled,
            "expired" => MarketOrderStatus.Expired,
            _ => throw new InvalidDataException("数据库市场订单状态无效。")
        });
}

public sealed record MarketRuntimeMetrics(
    int PartitionCount,
    int ActiveOrderCount,
    long PlacementCount,
    long MatchCount,
    long CancelCount,
    long RejectedCount);
