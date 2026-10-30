using System.Net.Sockets;
using Domain.Common;
using Domain.GameData;
using Domain.Market;
using Domain.Player;
using Server.Clean.Social;

namespace Server.Clean;

/// <summary>
/// 市场分片运行时：内存 OrderBook 是盘口投影，PostgreSQL 事务是订单与资产权威。
/// 启动时抢占分片写者租约并以 fence token 对每笔写事务做 fencing；挂单/撤单/到期退款
/// 经 _gate 单写者串行，内存盘口只随已提交事务更新。
/// 持久化边界失败后不再永久停摆：先标记未就绪 fail-closed 拒绝一切写入，
/// 监管循环按指数退避重获租约并从数据库重建盘口，恢复一致后自动回到就绪。
/// </summary>
public sealed class MarketRuntime : IHostedService, IAsyncDisposable
{
    /// <summary>瞬态数据库错误的有界重试：3 次，50-200ms 线性退避。</summary>
    private const int TransientRetryAttempts = 3;
    private static readonly int[] TransientRetryBackoffMilliseconds = [50, 120, 200];
    /// <summary>正常态续租周期。</summary>
    private static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(10);
    /// <summary>写权重获的指数退避窗口：1s 起步、逐次翻倍、封顶 30s。</summary>
    private static readonly TimeSpan RecoveryBackoffInitial = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecoveryBackoffMax = TimeSpan.FromSeconds(30);

    private readonly PostgresMarketRepository _store;
    private readonly ServerOptions _options;
    private readonly ILogger<MarketRuntime> _logger;
    private readonly IParticipationAssetBoundary? _participationAssets;
    private readonly GameDataCatalog? _gameData;
    private readonly ConcurrentDictionary<MarketInstrument, OrderBook> _books = new();
    private readonly ConcurrentDictionary<MarketInstrument, double> _referencePrices = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _booksGate = new();
    private readonly Guid _ownerInstanceId = Guid.NewGuid();
    private CancellationTokenSource? _lifetime;
    private Task? _renewTask;
    private long _fenceToken;
    private volatile bool _ready;
    /// <summary>监管循环唤醒信号：置位后下一次等待立即返回，随即被 CompareExchange 换新。</summary>
    private TaskCompletionSource _supervisorSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _placementCount;
    private long _matchCount;
    private long _cancelCount;
    private long _rejectedCount;
    private double _tradedQuantity;
    private double _tradedGrossAmount;
    private readonly LogThrottle _persistenceErrorLog = new(TimeSpan.FromMinutes(1));
    private readonly LogThrottle _recoveryLog = new(TimeSpan.FromMinutes(1));

    /// <summary>创建分片运行时实例；不触库，租约抢占与盘口恢复延迟到 <see cref="StartAsync"/>。</summary>
    /// <param name="store">市场账本仓储：订单、成交与分片租约的 PostgreSQL 适配。</param>
    /// <param name="options">服务端配置选项（分片数与本进程分片 ID 由此读取）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="participationAssets">资产提交前结清在线消耗品实耗的会话门；独立仓储测试可省略。</param>
    public MarketRuntime(PostgresMarketRepository store, ServerOptions options, ILogger<MarketRuntime> logger,
        IParticipationAssetBoundary? participationAssets = null,
        GameDataCatalog? gameData = null)
    {
        _store = store;
        _options = options;
        _logger = logger;
        _participationAssets = participationAssets;
        _gameData = gameData;
    }

    /// <summary>
    /// 市场结算把资产计入某玩家余额时触发（成交双方/撤单与到期退款属主/结算领取人）。
    /// 订阅者（玩家运行时）据此标记在线玩家的缓存钱包失效：余额由下一次玩家门内操作重读，
    /// 经推进推送以新版本差量送达客户端；订阅者回调必须在不抛异常、不阻塞市场写门闩的前提下完成。
    /// </summary>
    public event Action<Guid>? WalletCredited;

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
            RestoreBooks(orders);
            try
            {
                var stats = await _store.LoadItemStatsAsync(cancellationToken: cancellationToken);
                foreach (var s in stats)
                {
                    if (s.ReferencePrice is { } refPrice && refPrice > 0)
                        _referencePrices[new MarketInstrument(new ItemId(s.AssetId), s.Variant)] = refPrice;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载历史行情参考价失败，将按需建立");
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
            await ReleaseLeaseBestEffortAsync();
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
        // 停机归还租约脱离停机 token：宿主停机超时触发取消时，Npgsql 会抛 OperationCanceledException，
        // 逃出 StopAsync 会让进程以未处理异常崩溃（exit 134）；租约本就有 expires_at 兜底过期，
        // 归还动作本身尽力而为，不应被停机取消。
        await ReleaseLeaseBestEffortAsync();
        _lifetime?.Dispose();
    }

    private async Task ReleaseLeaseBestEffortAsync()
    {
        if (_fenceToken <= 0) return;
        try
        {
            await using var connection = await _store.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                UPDATE market.partition_leases
                SET expires_at = now(), renewed_at = now()
                WHERE partition_id = $1 AND owner_instance_id = $2 AND fence_token = $3
                """, connection);
            command.Parameters.Add(new NpgsqlParameter { Value = PartitionId });
            command.Parameters.Add(new NpgsqlParameter { Value = _ownerInstanceId });
            command.Parameters.Add(new NpgsqlParameter { Value = _fenceToken });
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception exception)
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

    /// <summary>读取品种当前最低挂售（卖一档）价；分片未就绪、品种不属本进程分片或无卖单时返回 null。</summary>
    /// <param name="instrument">市场品种。</param>
    public double? TryGetBestAskPrice(MarketInstrument instrument)
    {
        if (!_ready || !Owns(instrument)) return null;
        lock (_booksGate)
        {
            return _books.TryGetValue(instrument, out var book) ? book.BestAskPrice : null;
        }
    }

    /// <summary>读取品种当前市场参考价格（最近 10 次成交 EMA 单值平均）；无成交价返回 null。</summary>
    /// <param name="instrument">市场品种。</param>
    public double? TryGetReferencePrice(MarketInstrument instrument)
    {
        return _referencePrices.TryGetValue(instrument, out var price) && price > 0 ? price : null;
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
    /// 汇总指定标的一阶盘口：扫描内存簿中同 ItemId 的全部变体簿，各取 Snapshot(1)
    /// 得出卖一/买一价；两侧均无挂单的空簿不列入。分片未就绪时抛异常。
    /// </summary>
    /// <param name="itemId">标的道具 ID。</param>
    public IReadOnlyList<MarketVariantSummary> GetVariantSummaries(ItemId itemId)
    {
        if (!_ready) throw new InvalidOperationException("市场分片尚未就绪。");
        lock (_booksGate)
        {
            var summaries = new List<MarketVariantSummary>();
            foreach (var book in _books.Values)
            {
                if (book.Instrument.ItemId != itemId) continue;
                var snapshot = book.Snapshot(1);
                var bestAsk = snapshot.Asks.Count > 0 ? snapshot.Asks[0].Price : (double?)null;
                var bestBid = snapshot.Bids.Count > 0 ? snapshot.Bids[0].Price : (double?)null;
                if (bestAsk is null && bestBid is null) continue;
                summaries.Add(new MarketVariantSummary(book.Instrument.Variant, bestAsk, bestBid));
            }
            // 展示序固定：基础簿居前，强化簿按等级升序，非强化变体兜底排尾并按字典序定序。
            return summaries
                .OrderBy(summary => EnhancementRules.ParseLevel(summary.Variant) ?? int.MaxValue)
                .ThenBy(summary => summary.Variant, StringComparer.Ordinal)
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
    /// 查询玩家在指定标的上的活动挂单（每标的至多一笔，命中即唯一）。
    /// 只读内存盘口投影，不触库：活动单必然在投影内，重启由 <see cref="RestoreBooks"/> 全量重建。
    /// </summary>
    /// <param name="ownerId">订单属主玩家。</param>
    /// <param name="instrument">市场品种。</param>
    /// <returns>活动挂单；该玩家在此标的无活动单或分片未就绪时返回 null。</returns>
    public MarketOrder? GetActiveOrder(PlayerId ownerId, MarketInstrument instrument)
    {
        if (!_ready || ownerId.IsEmpty || !Owns(instrument)) return null;
        lock (_booksGate)
            return _books.TryGetValue(instrument, out var book) ? book.ActiveOrderOf(ownerId) : null;
    }

    /// <summary>
    /// 领取市场结算产物（成交所得/撤单退款等已挂账资产）；requestId 兼作幂等键。
    /// 多分片部署尚无跨分片领取路由，PartitionCount > 1 时直接拒绝。
    /// </summary>
    /// <param name="playerId">结算属主玩家。</param>
    /// <param name="requestId">客户端请求标识，用于领取幂等去重。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public async ValueTask<SettlementClaimResult> ClaimSettlementAsync(PlayerId playerId, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (!_ready) throw new InvalidOperationException("市场分片尚未就绪。");
        if (PartitionCount != 1)
            throw new InvalidOperationException("多市场分片尚未提供跨分片结算领取路由，当前请求已拒绝。");
        var result = await WithTransientRetryAsync(token => _store.ClaimSettlementAsync(playerId.Value, requestId, token), cancellationToken);
        // 挂账结算已领取入账属主钱包：通知在线玩家运行时重读余额并推送差额。
        NotifyWalletsCredited([playerId.Value]);
        return result;
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
        if (_gameData is not null && _gameData.Items.TryGetValue(request.Instrument.ItemId, out var itemConfig))
        {
            if (string.Equals(itemConfig.Category, "material", StringComparison.OrdinalIgnoreCase))
            {
                var basePrice = itemConfig.SellPrice > 0 ? itemConfig.SellPrice : 1.0;
                var maxPrice = basePrice * 50.0;
                if (request.Price > maxPrice)
                    throw new DomainException(DomainErrorCode.InvalidArgument, $"材料挂单单价超出基准价值上限（最高 {maxPrice} 灵石）。");
            }
        }
        EnsureReady(request.Instrument);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // 等门闩期间可能已被其他失败降级：门闩内必须复检，防止写者持旧盘口跨过恢复点。
            if (!_ready) throw new InvalidOperationException("市场分片尚未就绪。");
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

            // 就地撮合：变更全程登记撤销日志；事务失败时逆序回放恢复干净盘口，
            // 不再为正演/回写各克隆一次整簿。撮合语义与克隆预演逐位等价。
            var journal = new OrderBookJournal();
            var prohibitedPartners = await _store.GetProhibitedTradePartnersAsync(request.OwnerId.Value, cancellationToken);
            Func<PlayerId, PlayerId, bool>? isProhibitedCrossTrade = prohibitedPartners.Count > 0
                ? (_, makerId) => prohibitedPartners.Contains(makerId.Value)
                : null;
            try
            {
                PlacementResult result;
                lock (_booksGate) result = book.Place(request, journal, isProhibitedCrossTrade);
                // 撮合后才知道全部 maker/taker；不持盘口读锁等待会话，失败由同一撤销日志恢复。
                using var participationScope = _participationAssets is null ? null
                    : await _participationAssets.AcquireAsync(CollectPlacementAssetKeys(request, result), cancellationToken);
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
                    Math.Ceiling(fill.Price * fill.Quantity * 0.02d),
                    fill.TradedAt)).ToArray();
                var committed = await _store.CommitPlacementAsync(input, fills, _fenceToken, cancellationToken);
                if (!committed.Applied)
                    throw new InvalidOperationException("市场挂单事务未提交。");
                if (committed.AlreadyCommitted)
                {
                    // 发生跨请求重试或进程内存与数据库短暂分叉时，数据库事实优先；
                    // 重新读取当前品种的活动订单，绝不把本地撮合结果当成权威事实。
                    var durableOrders = await _store.LoadActiveOrdersAsync(request.Instrument, cancellationToken);
                    var durableBook = new OrderBook(request.Instrument);
                    var durableDomain = durableOrders
                        .Select(stored => ToDomainOrder(stored, request.Instrument))
                        .ToArray();
                    durableBook.Restore(durableDomain, durableDomain.Length == 0 ? 0 : durableDomain.Max(order => order.Sequence));
                    lock (_booksGate) book.Restore(durableBook.ExportActiveOrders(), durableBook.Version);
                    var durableIncoming = durableDomain.FirstOrDefault(order => order.OrderId == request.OrderId)
                        ?? (await _store.LoadOrderAsync(request.OrderId.Value, cancellationToken) is { } filled
                            ? ToDomainOrder(filled, request.Instrument)
                            : null)
                        ?? throw new InvalidOperationException("市场幂等订单已提交但无法读取持久化结果。");
                    return new PlacementResult(durableIncoming, [], durableBook.Version);
                }
                Interlocked.Increment(ref _placementCount);
                Interlocked.Add(ref _matchCount, fills.Length);
                if (fills.Length > 0)
                {
                    // 成交统计为 double（无 Interlocked.Add 重载），此段在写门闩内串行，读侧用 Volatile.Read。
                    _tradedQuantity += fills.Sum(fill => fill.Quantity);
                    _tradedGrossAmount += fills.Sum(fill => fill.Quantity * fill.Price);
                    foreach (var fill in result.Fills)
                    {
                        if (double.IsFinite(fill.Price) && fill.Price > 0)
                        {
                            var weight = ReferencePriceRules.CalculateTradeWeight(
                                request.Instrument.ItemId.Value,
                                request.Instrument.Variant,
                                fill.Quantity,
                                _gameData);
                            _referencePrices.AddOrUpdate(
                                request.Instrument,
                                fill.Price,
                                (_, old) => ReferencePriceRules.CalculateNextReferencePrice(old, fill.Price, weight));
                        }
                    }
                    // 买卖双方钱包均已随成交事务入账：通知在线玩家运行时重读余额并推送差额。
                    NotifyWalletsCredited(result.Fills.SelectMany(fill => new[] { fill.BuyerId.Value, fill.SellerId.Value }));
                }
                return result;
            }
            catch
            {
                // 事务未成交：回放撤销日志把盘口还原到变更前状态，保证瞬态重试读到干净簿；
                // 若异常属于非瞬态，外层分类器会再降级并触发重获重建。
                lock (_booksGate) book.Undo(journal);
                throw;
            }
        }
        catch (Exception exception) when (IsExpectedRequestFailure(exception))
        {
            Interlocked.Increment(ref _rejectedCount);
            throw;
        }
        catch (Exception exception) when (IsTransientDatabaseError(exception))
        {
            // 瞬态事务失败（40001 序列化冲突等）：事务已整体回滚、写权与盘口均未受损，
            // 交由 WithTransientRetryAsync 有界重试自愈，不触发降级。
            throw;
        }
        catch (Exception exception)
        {
            // 非预期失败（含租约 fence 失效、账本约束违例）：内存盘口与账本可能已分叉，
            // 降级 fail-closed 并唤醒监管循环重获写权；已降级时静默透出，不重复告警。
            if (_ready)
            {
                EnterRecovery();
                if (_persistenceErrorLog.TryAcquire())
                    _logger.LogCritical(exception, "市场下单持久化边界失败 partition={PartitionId}，转入租约重获恢复", PartitionId);
            }
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
            // 与挂单一致：门闩内复检就绪，防止写者持旧盘口跨过恢复点。
            if (!_ready) throw new InvalidOperationException("市场分片尚未就绪。");
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
            // 就地撤单：撤销日志兜底，事务失败即回放，消除整簿双向克隆。
            var journal = new OrderBookJournal();
            CancellationResult result;
            try
            {
                lock (_booksGate) result = book.Cancel(orderId, ownerId, journal);
                using var participationScope = _participationAssets is null ? null
                    : await _participationAssets.AcquireAsync(
                        [new ParticipationAssetKey(ownerId.Value, instrument.ItemId.Value, instrument.Variant)], cancellationToken);
                if (!await _store.CancelAsync(orderId.Value, ownerId.Value, PartitionId, _fenceToken, idempotencyKey, cancellationToken))
                    throw new InvalidOperationException("市场撤单事务未提交。");
                Interlocked.Increment(ref _cancelCount);
                // 撤单退款已入账属主钱包：在线玩家的缓存余额需要失效重读。
                NotifyWalletsCredited([ownerId.Value]);
                return result;
            }
            catch
            {
                lock (_booksGate) book.Undo(journal);
                throw;
            }
        }
        catch (Exception exception) when (IsExpectedRequestFailure(exception))
        {
            Interlocked.Increment(ref _rejectedCount);
            throw;
        }
        catch (Exception exception) when (IsTransientDatabaseError(exception))
        {
            // 瞬态失败事务已回滚：交有界重试，不降级。
            throw;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _rejectedCount);
            // 非预期失败：盘口与账本可能分叉，降级 fail-closed 并唤醒重获；已降级时静默透出。
            if (_ready)
            {
                EnterRecovery();
                if (_persistenceErrorLog.TryAcquire())
                    _logger.LogCritical(exception, "市场撤单持久化边界失败 partition={PartitionId}，转入租约重获恢复", PartitionId);
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    private static IReadOnlyList<ParticipationAssetKey> CollectPlacementAssetKeys(
        PlaceOrderRequest request, PlacementResult result)
    {
        var keys = new List<ParticipationAssetKey>(1 + result.Fills.Count * 2)
        {
            new(request.OwnerId.Value, request.Instrument.ItemId.Value, request.Instrument.Variant)
        };
        foreach (var fill in result.Fills)
        {
            keys.Add(new ParticipationAssetKey(fill.BuyerId.Value, fill.Instrument.ItemId.Value, fill.Instrument.Variant));
            keys.Add(new ParticipationAssetKey(fill.SellerId.Value, fill.Instrument.ItemId.Value, fill.Instrument.Variant));
        }
        return keys;
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

    // 调用方持有写门闩；先提交退款事实，再下架内存盘口，失败时内存与账本保持一致。
    // 到期索引只处理队首：未到期簿首个索引项即终止枚举，不再全簿扫描；
    // 逐笔退款合批为单个 Serializable 事务，集合顺序即原逐笔落库顺序（簿枚举序 + 簿内 Sequence 序）。
    private async Task ExpireOrdersCoreAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<OrderBook>? dueBooks = null;
        List<MarketExpiryOrder>? targets = null;
        List<ParticipationAssetKey>? assetKeys = null;
        lock (_booksGate)
        {
            foreach (var book in _books.Values)
            {
                var due = book.GetExpiredOrders(now);
                if (due.Count == 0) continue;
                (dueBooks ??= []).Add(book);
                targets ??= new List<MarketExpiryOrder>(due.Count);
                foreach (var order in due)
                {
                    targets.Add(new MarketExpiryOrder(order.OrderId.Value, order.OwnerId.Value));
                    if (_participationAssets is not null)
                        (assetKeys ??= []).Add(new ParticipationAssetKey(order.OwnerId.Value,
                            order.Instrument.ItemId.Value, order.Instrument.Variant));
                }
            }
        }
        if (targets is null || dueBooks is null) return;
        using var participationScope = _participationAssets is null ? null
            : await _participationAssets.AcquireAsync(assetKeys!, cancellationToken);
        if (!await _store.ExpireBatchAsync(targets, _fenceToken, now, cancellationToken))
            throw new InvalidOperationException("市场到期退款事务未提交。");
        lock (_booksGate)
        {
            foreach (var book in dueBooks) book.Expire(now);
        }
        // 到期退款已合批入账各属主钱包：通知在线玩家运行时重读余额并推送差额。
        NotifyWalletsCredited(targets.Select(target => target.OwnerPlayerId));
    }

    /// <summary>
    /// 分片监管循环：正常态按固定周期续租并结算到期订单；
    /// 一旦失 fence、续租落空或写路径持久化失败，即转入降级——停止一切写入，
    /// 按指数退避重获写权（抢占租约 → 刷新 fence → 重建盘口 → 结算到期 → 恢复就绪），
    /// 恢复期间所有写操作继续被 EnsureReady fail-closed 拒绝。
    /// </summary>
    private async Task RenewLeaseLoopAsync(CancellationToken cancellationToken)
    {
        var recoveryBackoff = RecoveryBackoffInitial;
        while (!cancellationToken.IsCancellationRequested)
        {
            // 正常态固定周期；降级态按当前退避窗口等待；降级信号到达立即醒来尝试重获。
            var signal = Volatile.Read(ref _supervisorSignal);
            await Task.WhenAny(
                Task.Delay(_ready ? RenewInterval : recoveryBackoff, cancellationToken),
                signal.Task);
            Interlocked.CompareExchange(
                ref _supervisorSignal,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                signal);
            try
            {
                if (!_ready)
                {
                    recoveryBackoff = await TryRecoverWriteAccessAsync(cancellationToken)
                        ? RecoveryBackoffInitial
                        : NextRecoveryBackoff(recoveryBackoff);
                    continue;
                }
                recoveryBackoff = RecoveryBackoffInitial;
                var now = DateTimeOffset.UtcNow;
                await using var connection = await _store.DataSource.OpenConnectionAsync(cancellationToken);
                await using var command = new NpgsqlCommand("""
                    UPDATE market.partition_leases
                    SET expires_at = $1, renewed_at = $2
                    WHERE partition_id = $3 AND owner_instance_id = $4 AND fence_token = $5
                    """, connection);
                command.Parameters.Add(new NpgsqlParameter { Value = now.Add(_options.SectionFlushLease) });
                command.Parameters.Add(new NpgsqlParameter { Value = now });
                command.Parameters.Add(new NpgsqlParameter { Value = PartitionId });
                command.Parameters.Add(new NpgsqlParameter { Value = _ownerInstanceId });
                command.Parameters.Add(new NpgsqlParameter { Value = _fenceToken });
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    // 租约行 owner/fence 已变：写权被其他实例接管，保持降级直到其租约过期。
                    _logger.LogCritical("市场分片租约丢失 partition={PartitionId} fence={FenceToken}，转入重获恢复", PartitionId, _fenceToken);
                    EnterRecovery();
                    continue;
                }
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    // 等门闩期间可能已被写路径降级：跳过本轮结算，下一跳直接进入重获。
                    if (_ready) await ExpireOrdersCoreAsync(now, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (IsTransientDatabaseError(exception))
                    {
                        // 合批事务整体回滚，内存盘口与账本仍一致：告警后保持就绪，下轮或下次写操作再试。
                        _logger.LogWarning(exception, "市场到期退款瞬态失败 partition={PartitionId}，等待下一轮重试", PartitionId);
                    }
                    else
                    {
                        _logger.LogCritical(exception, "市场到期退款失败 partition={PartitionId}，转入重获恢复", PartitionId);
                        EnterRecovery();
                    }
                }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703" or "3F000")
            {
                // schema/配置级错误无法靠重试立即恢复：保持降级 fail-closed，
                // 监管循环继续按退避重获；schema 修复后无需重启即可自愈。
                _logger.LogCritical(ex, "市场租约续租遇到不可恢复的数据库错误 partition={PartitionId}，转入重获恢复", PartitionId);
                EnterRecovery();
            }
            catch (Exception ex)
            {
                // 续租瞬态失败不应让分片停摆：记录后下一轮继续续租；若租约真正过期或被夺，
                // 交易路径的 EnsureLeaseAsync 会以 MarketLeaseLostException fail-fast 并触发重获。
                _logger.LogError(ex, "市场租约续租失败 partition={PartitionId}，等待下一轮重试", PartitionId);
            }
        }
    }

    /// <summary>
    /// 单次重获尝试：抢占/确认租约 → 刷新 fence → 写门闩内重建盘口并结算到期订单 → 恢复就绪。
    /// 返回 false 表示仍处降级，由监管循环按退避再次尝试；整个恢复期间写操作 fail-closed。
    /// </summary>
    private async Task<bool> TryRecoverWriteAccessAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await _store.AcquireLeaseAsync(_ownerInstanceId, cancellationToken))
            {
                if (_recoveryLog.TryAcquire())
                    _logger.LogWarning("市场分片租约仍被其他实例持有 partition={PartitionId}，等待其自然过期后接管", PartitionId);
                return false;
            }
            var fence = await ReadFenceTokenAsync(cancellationToken);
            var orders = await _store.LoadActiveOrdersAsync(cancellationToken);
            // 重建必须持有写门闩：排队写者在门闩内复检 _ready，保证没有任何写能持旧盘口跨过恢复点。
            await _gate.WaitAsync(cancellationToken);
            try
            {
                RestoreBooks(orders);
                _fenceToken = fence;
                // 与启动顺序一致：先结算恢复期间到期的订单，再放行写请求。
                await ExpireOrdersCoreAsync(DateTimeOffset.UtcNow, cancellationToken);
                _ready = true;
            }
            finally { _gate.Release(); }
            _logger.LogInformation("市场分片写权已恢复 partition={PartitionId} fence={FenceToken} orders={OrderCount}", PartitionId, fence, orders.Count);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (_recoveryLog.TryAcquire())
                _logger.LogError(exception, "市场分片写权恢复失败 partition={PartitionId}，退避后重试", PartitionId);
            return false;
        }
    }

    /// <summary>
    /// 触发钱包入账事件：订阅者回调不抛异常、不阻塞市场写门闩——隔离失败只记录日志，
    /// 绝不让通知缺陷反向拖垮市场事务路径。
    /// </summary>
    private void NotifyWalletsCredited(IEnumerable<Guid> playerIds)
    {
        if (WalletCredited is null) return;
        foreach (var playerId in playerIds.Distinct())
        {
            try { WalletCredited.Invoke(playerId); }
            catch (Exception exception) { _logger.LogError(exception, "市场入账通知处理失败 player={PlayerId}", playerId); }
        }
    }

    /// <summary>按数据库活动订单全量重建内存盘口；调用方保证无并发写入（启动期单线程或持有写门闩）。</summary>
    private void RestoreBooks(IReadOnlyList<StoredMarketOrder> orders)
    {
        var rebuilt = new List<OrderBook>();
        foreach (var group in orders.GroupBy(order => new MarketInstrument(new ItemId(order.AssetId), order.Variant)))
        {
            var book = new OrderBook(group.Key);
            var restored = group.Select(stored => ToDomainOrder(stored, group.Key)).ToArray();
            book.Restore(restored, restored.Max(order => order.Sequence));
            rebuilt.Add(book);
        }
        lock (_booksGate)
        {
            _books.Clear();
            foreach (var book in rebuilt) _books[book.Instrument] = book;
        }
    }

    /// <summary>进入降级：置未就绪完成 fail-closed，并唤醒监管循环立即开始重获流程。</summary>
    private void EnterRecovery()
    {
        _ready = false;
        SignalSupervisor();
    }

    /// <summary>唤醒监管循环：将当前信号源置为已完成，下一次等待立即返回。</summary>
    private void SignalSupervisor() => Volatile.Read(ref _supervisorSignal).TrySetResult();

    /// <summary>重获退避窗口翻倍并封顶。</summary>
    private static TimeSpan NextRecoveryBackoff(TimeSpan current) =>
        TimeSpan.FromTicks(Math.Min(current.Ticks * 2, RecoveryBackoffMax.Ticks));

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

/// <summary>
/// 单变体盘口的一阶价摘要（经 GET /api/v1/market/variants 透出）。
/// 口径：_books 仅收录本进程分片 Owns 的品种，多分片部署下该摘要是分片局部视图，
/// 其他分片持有的变体簿需向对应实例查询。
/// </summary>
/// <param name="Variant">盘口变体："" 为基础簿，"+N" 为强化等级簿。</param>
/// <param name="BestAskPrice">当前最低挂售价（卖一档）；无卖单为 null。</param>
/// <param name="BestBidPrice">当前最高收购价（买一档）；无买单为 null。</param>
public sealed record MarketVariantSummary(string Variant, double? BestAskPrice, double? BestBidPrice);
