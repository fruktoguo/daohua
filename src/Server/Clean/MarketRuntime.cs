using System.Net.Sockets;
using Domain.Common;
using Domain.Market;

namespace Server.Clean;

/// <summary>
/// 市场分片运行时：内存 OrderBook 是盘口投影，PostgreSQL 事务是订单与资产权威。
/// 启动时抢占分片写者租约并以 fence token 对每笔写事务做 fencing；挂单/撤单/到期退款
/// 经 _gate 单写者串行，内存盘口只随已提交事务更新；持久化边界失败后标记未就绪
/// fail-fast，等待进程重启从数据库重建。
/// </summary>
public sealed class MarketRuntime : IHostedService, IAsyncDisposable
{
    /// <summary>瞬态数据库错误的有界重试：3 次，50-200ms 线性退避。</summary>
    private const int TransientRetryAttempts = 3;
    private static readonly int[] TransientRetryBackoffMilliseconds = [50, 120, 200];

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
    private double _tradedQuantity;
    private double _tradedGrossAmount;
    private readonly LogThrottle _persistenceErrorLog = new(TimeSpan.FromMinutes(1));

    /// <summary>
    /// 初始化 MarketRuntime 的新实例。
    /// </summary>
    /// <param name="store">市场账本仓储：订单、成交与分片租约的 PostgreSQL 适配。</param>
    /// <param name="options">服务端配置选项（分片数与本进程分片 ID 由此读取）。</param>
    /// <param name="logger">日志记录器。</param>
    public MarketRuntime(PostgresMarketRepository store, ServerOptions options, ILogger<MarketRuntime> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    /// <summary>分片就绪标记：完成租约获取与盘口恢复后为 true；持久化失败或租约丢失时回落 false 拒绝写入。</summary>
    public bool IsReady => _ready;
    /// <summary>本进程持有的市场分片序号。</summary>
    public int PartitionId => _options.MarketPartitionId;
    /// <summary>市场分片总数。</summary>
    public int PartitionCount => _options.MarketPartitionCount;
    /// <summary>品种到分片的路由算法标识。</summary>
    public string Algorithm => MarketPartitioner.Algorithm;

    /// <summary>
    /// 启动分片运行时：等待并抢占写者租约 → 读取 fence token → 恢复活动订单重建内存盘口 →
    /// 结算已到期订单 → 标记就绪并启动 10s 周期续租循环。
    /// 租约被其他实例持续持有超过约 40 秒时启动失败。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
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
                // 热重启期间旧实例租约未过期属于预期等待：只保留首条告警，后续重试降级为 Debug，
                // 避免每次启动刷出数十行同义日志。租约最终仍未取得时由下方异常统一暴露。
                _logger.Log(
                    attempt == 1 ? LogLevel.Warning : LogLevel.Debug,
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
            await ExpireOrdersCoreAsync(DateTimeOffset.UtcNow, cancellationToken);
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

    /// <summary>
    /// 停止分片运行时：取消续租循环并尽力归还租约（把 expires_at 提前到现在，加速新实例接管）。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
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

    /// <summary>判断品种按路由规则是否归属本进程分片。</summary>
    /// <param name="instrument">市场品种。</param>
    public bool Owns(MarketInstrument instrument) => MarketPartitioner.GetPartition(instrument, PartitionCount) == PartitionId;

    /// <summary>
    /// 返回品种盘口快照；分片未就绪或品种不属于本进程分片时抛异常。
    /// </summary>
    /// <param name="instrument">市场品种。</param>
    /// <param name="depth">每侧最大档位数。</param>
    public OrderBookSnapshot GetBook(MarketInstrument instrument, int depth = 20)
    {
        if (!_ready) throw new InvalidOperationException("市场分片尚未就绪。");
        if (!Owns(instrument)) throw new InvalidOperationException("该品种不属于当前市场分片。");
        lock (_booksGate)
        {
            return _books.GetOrAdd(instrument, static key => new OrderBook(key)).Snapshot(depth);
        }
    }

    /// <summary>
    /// 返回玩家在内存盘口中的活动订单，按挂入顺序倒序截断。
    /// Sequence 是单个订单簿内的局部序号，不能作为跨品种排序主键：
    /// 不同簿的同序号订单并列时只能依靠字典枚举序，重启或新增标的簿后顺序会漂移。
    /// 因此跨簿主排序用 CreatedAt 降序，再以 OrderId 兜底保证整体次序确定。
    /// </summary>
    /// <param name="ownerId">订单属主玩家。</param>
    /// <param name="limit">最大返回条数。</param>
    public IReadOnlyList<MarketOrder> GetOrders(PlayerId ownerId, int limit = 100)
    {
        if (!_ready) throw new InvalidOperationException("市场分片尚未就绪。");
        lock (_booksGate)
        {
            return _books.Values
                .SelectMany(book => book.OrdersByOwner(ownerId, limit))
                .OrderByDescending(order => order.CreatedAt)
                .ThenBy(order => order.OrderId.Value)
                .Take(limit)
                .ToArray();
        }
    }

    /// <summary>
    /// 查询单笔订单：先查内存盘口，未命中再回源持久化订单。
    /// 订单不属于 ownerId 时一律返回 null，不泄露他人订单存在性。
    /// </summary>
    /// <param name="orderId">订单标识。</param>
    /// <param name="ownerId">订单属主玩家。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>订单存在且属于该玩家时返回订单，否则 null。</returns>
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

    /// <summary>
    /// 领取市场结算产物（成交所得/撤单退款等已挂账资产）；requestId 兼作幂等键。
    /// 多分片部署尚无跨分片领取路由，PartitionCount > 1 时直接拒绝。
    /// </summary>
    /// <param name="playerId">结算属主玩家。</param>
    /// <param name="requestId">客户端请求标识，用于领取幂等去重。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public ValueTask<SettlementClaimResult> ClaimSettlementAsync(PlayerId playerId, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (!_ready) throw new InvalidOperationException("市场分片尚未就绪。");
        if (PartitionCount != 1)
            throw new InvalidOperationException("多市场分片尚未提供跨分片结算领取路由，当前请求已拒绝。");
        return WithTransientRetryAsync(token => _store.ClaimSettlementAsync(playerId.Value, requestId, token), cancellationToken);
    }

    /// <summary>
    /// 采集分片运行计数快照（活动订单与累计挂单/成交/撤单/拒绝统计）。
    /// </summary>
    public MarketRuntimeMetrics GetMetrics() => new(
        PartitionCount,
        ActiveOrderCount,
        Interlocked.Read(ref _placementCount),
        Interlocked.Read(ref _matchCount),
        Interlocked.Read(ref _cancelCount),
        Interlocked.Read(ref _rejectedCount),
        Volatile.Read(ref _tradedQuantity),
        Volatile.Read(ref _tradedGrossAmount));

    /// <summary>
    /// 挂单入口：撮合在内存 working book 上预演，订单与成交经单事务落库后才替换盘口。
    /// 相同 OrderId 重复提交按幂等返回既有订单；OrderId 绑定参数不一致时拒绝。
    /// 外层包裹瞬态数据库错误的有界重试。
    /// </summary>
    /// <param name="request">挂单请求（订单标识、品种、方向、价格、数量与幂等键）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public ValueTask<PlacementResult> PlaceAsync(PlaceOrderRequest request, CancellationToken cancellationToken = default) =>
        WithTransientRetryAsync(token => PlaceCoreAsync(request, token), cancellationToken);

    private async ValueTask<PlacementResult> PlaceCoreAsync(PlaceOrderRequest request, CancellationToken cancellationToken)
    {
        EnsureReady(request.Instrument);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ExpireOrdersCoreAsync(DateTimeOffset.UtcNow, cancellationToken);
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
                fill.Price * fill.Quantity * 0.02d,
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
            if (fills.Length > 0)
            {
                // 成交统计为 double（无 Interlocked.Add 重载），此段已在 _booksGate 锁内串行，读侧用 Volatile.Read。
                _tradedQuantity += fills.Sum(fill => fill.Quantity);
                _tradedGrossAmount += fills.Sum(fill => fill.Quantity * fill.Price);
            }
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

    /// <summary>
    /// 撤单入口：仅允许撤销自己的活动订单，终态订单按 InvalidTransition 拒绝；
    /// 已取消订单重复撤单按幂等返回。外层包裹瞬态数据库错误的有界重试。
    /// </summary>
    /// <param name="orderId">待撤销订单标识。</param>
    /// <param name="ownerId">订单属主玩家。</param>
    /// <param name="instrument">市场品种。</param>
    /// <param name="idempotencyKey">撤单幂等键。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public ValueTask<CancellationResult> CancelAsync(OrderId orderId, PlayerId ownerId, MarketInstrument instrument, string idempotencyKey, CancellationToken cancellationToken = default) =>
        WithTransientRetryAsync(token => CancelCoreAsync(orderId, ownerId, instrument, idempotencyKey, token), cancellationToken);

    private async ValueTask<CancellationResult> CancelCoreAsync(OrderId orderId, PlayerId ownerId, MarketInstrument instrument, string idempotencyKey, CancellationToken cancellationToken)
    {
        EnsureReady(instrument);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ExpireOrdersCoreAsync(DateTimeOffset.UtcNow, cancellationToken);
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

    /// <summary>瞬态数据库错误的有界重试；挂单/撤单/结算均按幂等键设计，重放安全。</summary>
    private async ValueTask<T> WithTransientRetryAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception exception) when (IsTransientDatabaseError(exception) && !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= TransientRetryAttempts) throw;
                _logger.LogWarning(exception, "市场分片瞬态数据库错误 partition={PartitionId} attempt={Attempt}/{MaxAttempts}，退避后重试",
                    PartitionId, attempt, TransientRetryAttempts);
                await Task.Delay(TransientRetryBackoffMilliseconds[attempt - 1], cancellationToken);
            }
        }
    }

    /// <summary>可重试的瞬态数据库错误：序列化冲突、死锁、连接与超时类；schema/约束错误不在其列。</summary>
    private static bool IsTransientDatabaseError(Exception exception) => exception switch
    {
        OperationCanceledException => false,
        PostgresException { SqlState: "40001" or "40P01" or "57P01" or "57P02" or "57P03" or "53300" or "57014" } => true,
        NpgsqlException { InnerException: TimeoutException or SocketException or IOException } => true,
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

    // 调用方持有写门闩；先提交退款事实，再删除盘口，失败时保留数据库可恢复状态。
    private async Task ExpireOrdersCoreAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var book in _books.Values)
        {
            MarketOrder[] due;
            lock (_booksGate) due = book.ActiveOrders.Where(order => order.ExpiresAt <= now).ToArray();
            if (due.Length == 0) continue;
            foreach (var order in due)
                if (!await _store.ExpireAsync(order.OrderId.Value, order.OwnerId.Value, _fenceToken, now, cancellationToken))
                    throw new InvalidOperationException("市场到期退款事务未提交。");
            lock (_booksGate) book.Expire(now);
        }
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
                await _gate.WaitAsync(cancellationToken);
                try { await ExpireOrdersCoreAsync(now, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _ready = false;
                    _logger.LogCritical(exception, "市场到期退款失败 partition={PartitionId}，停止写入并等待重启恢复", PartitionId);
                    return;
                }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703" or "3F000")
            {
                // schema/配置级错误无法靠重试恢复：保持 fail-fast，停止市场写入。
                _ready = false;
                _logger.LogCritical(ex, "市场租约续租遇到不可恢复的数据库错误 partition={PartitionId}", PartitionId);
                return;
            }
            catch (Exception ex)
            {
                // 瞬态数据库故障不应让分片永久停摆：记录后下一轮继续续租；若租约真正过期，
                // 交易路径的 EnsureLeaseAsync 会以 MarketLeaseLostException fail-fast。
                _logger.LogError(ex, "市场租约续租失败 partition={PartitionId}，等待下一轮重试", PartitionId);
            }
        }
    }

    /// <summary>释放写门闩与生命周期令牌；租约归还由 StopAsync 负责。</summary>
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
        stored.ExpiresAt,
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

/// <summary>
/// 市场分片运行计数快照。
/// </summary>
/// <param name="PartitionCount">市场分片总数。</param>
/// <param name="ActiveOrderCount">内存盘口中当前活动订单数。</param>
/// <param name="PlacementCount">累计成功挂单数。</param>
/// <param name="MatchCount">累计成交笔数。</param>
/// <param name="CancelCount">累计撤单数。</param>
/// <param name="RejectedCount">累计被拒绝的请求数（含业务拒绝与持久化失败）。</param>
/// <param name="TradedQuantity">累计成交数量。</param>
/// <param name="TradedGrossAmount">累计成交金额（未扣费）。</param>
public sealed record MarketRuntimeMetrics(
    int PartitionCount,
    int ActiveOrderCount,
    long PlacementCount,
    long MatchCount,
    long CancelCount,
    long RejectedCount,
    double TradedQuantity,
    double TradedGrossAmount);
