using System.Collections.Concurrent;
using Daoxuan.Domain.Models;
using Daoxuan.Domain.Simulation;

namespace Daoxuan.Domain.Market;

/// <summary>
/// 分片市场入口。每个分片只在自己的锁内访问订单簿，撮合路径不会持有全局锁。
/// </summary>
public sealed class DaoxuanMarketEngine
{
    private readonly MarketShard[] _shards;
    private readonly ConcurrentDictionary<string, OrderRoute> _routes = new(StringComparer.Ordinal);
    private readonly Func<string, bool> _isTradable;
    private readonly Func<string, double> _taxRateResolver;
    private readonly Func<string, long> _vendorPriceResolver;

    public DaoxuanMarketEngine(
        int shardCount = 16,
        Func<string, bool>? isTradable = null,
        Func<string, double>? taxRateResolver = null,
        Func<string, long>? vendorPriceResolver = null)
    {
        if (shardCount is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(shardCount), "分片数量必须在 1 到 256 之间。");
        }

        _shards = Enumerable.Range(0, shardCount).Select(_ => new MarketShard()).ToArray();
        _isTradable = isTradable ?? IsCatalogTradable;
        _taxRateResolver = taxRateResolver ?? DaoxuanMarketTaxPolicy.GetSellerTaxRate;
        _vendorPriceResolver = vendorPriceResolver ?? GetCatalogVendorPrice;
    }

    /// <summary>获取当前引擎分片总数。</summary>
    public int ShardCount => _shards.Length;

    /// <summary>
    /// 导出所有分片下全部交易品种订单簿的全量快照。
    /// </summary>
    /// <returns>市场引擎快照对象。</returns>
    public DaoxuanMarketEngineState ExportState()
    {
        var books = new List<DaoxuanMarketBookState>();
        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                books.AddRange(shard.Books.Values.Select(book => book.ExportState()));
            }
        }

        return new DaoxuanMarketEngineState(
            books.OrderBy(book => book.Instrument.ItemId, StringComparer.Ordinal)
                .ThenBy(book => book.Instrument.EnhancementLevel)
                .ToArray());
    }

    /// <summary>
    /// 在服务启动阶段恢复订单簿和全局订单路由。调用期间不应接收市场写请求。
    /// </summary>
    /// <param name="state">待恢复的市场引擎快照。</param>
    public void RestoreState(DaoxuanMarketEngineState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var books = state.Books ?? throw new InvalidDataException("市场订单簿快照不能为空。");
        var seenInstruments = new HashSet<DaoxuanMarketInstrument>();
        var seenOrders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var book in books)
        {
            if (book is null
                || !book.Instrument.IsValid
                || !_isTradable(book.Instrument.ItemId)
                || !seenInstruments.Add(book.Instrument))
            {
                throw new InvalidDataException("市场订单簿快照包含无效或重复品种。");
            }

            foreach (var order in book.ActiveOrders ?? throw new InvalidDataException("活动订单列表不能为空。"))
            {
                if (order is null || !seenOrders.Add(order.OrderId))
                {
                    throw new InvalidDataException("市场活动订单 ID 重复。");
                }
            }
        }

        _routes.Clear();
        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                shard.Books.Clear();
            }
        }

        foreach (var bookState in books)
        {
            var shardIndex = GetShardIndex(bookState.Instrument);
            var shard = _shards[shardIndex];
            lock (shard.Gate)
            {
                var book = shard.GetBook(
                    bookState.Instrument,
                    _taxRateResolver,
                    _vendorPriceResolver,
                    RemoveRoute);
                book.RestoreState(bookState);
                foreach (var order in bookState.ActiveOrders)
                {
                    if (!_routes.TryAdd(
                            order.OrderId,
                            new OrderRoute(shardIndex, bookState.Instrument, order.OwnerId)))
                    {
                        throw new InvalidDataException("市场订单路由恢复失败。");
                    }

                }
            }
        }
    }

    /// <summary>
    /// 向对应分片提交挂单请求并执行价格/时间优先撮合。
    /// </summary>
    /// <param name="request">订单挂单请求。</param>
    /// <returns>挂单与撮合成交执行结果。</returns>
    public DaoxuanMarketPlacementResult Place(DaoxuanMarketOrderRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.OrderId))
        {
            return DaoxuanMarketPlacementResult.Fail("INVALID_ORDER_ID", "订单 ID 不能为空。");
        }

        if (!request.Instrument.IsValid)
        {
            return DaoxuanMarketPlacementResult.Fail("INVALID_INSTRUMENT", "市场品种无效。");
        }

        if (!_isTradable(request.Instrument.ItemId))
        {
            return DaoxuanMarketPlacementResult.Fail("ITEM_NOT_TRADABLE", "该物品不能挂牌交易。");
        }

        var shardIndex = GetShardIndex(request.Instrument);
        var route = new OrderRoute(shardIndex, request.Instrument, request.OwnerId);
        if (!_routes.TryAdd(request.OrderId, route))
        {
            return DaoxuanMarketPlacementResult.Fail("DUPLICATE_ORDER_ID", "订单 ID 已存在。");
        }

        var shard = _shards[shardIndex];
        DaoxuanMarketPlacementResult result;
        try
        {
            lock (shard.Gate)
            {
                result = shard.GetBook(request.Instrument, _taxRateResolver, _vendorPriceResolver, RemoveRoute).Place(request);
            }
        }
        catch
        {
            // 订单簿出现未预期异常时不能留下可撤销路由。
            RemoveRoute(request.OrderId);
            throw;
        }

        if (!result.Success)
        {
            RemoveRoute(request.OrderId);
        }
        else if (result.Order?.Status == DaoxuanMarketOrderStatus.Filled)
        {
            // 已完全成交的订单不再占用全局路由表；订单簿保留有界终态历史。
            RemoveRoute(request.OrderId);
        }

        return result;
    }

    /// <summary>
    /// 取消指定活动订单。
    /// </summary>
    /// <param name="orderId">订单 ID。</param>
    /// <param name="ownerId">撤单发起人玩家 GUID。</param>
    /// <returns>撤单结果。</returns>
    public DaoxuanMarketCancellationResult Cancel(string orderId, Guid ownerId)
    {
        if (string.IsNullOrWhiteSpace(orderId) || !_routes.TryGetValue(orderId, out var route))
        {
            return DaoxuanMarketCancellationResult.Fail("ORDER_NOT_FOUND", "订单不存在。");
        }

        var shard = _shards[route.ShardIndex];
        lock (shard.Gate)
        {
            var result = shard.GetBook(route.Instrument, _taxRateResolver, _vendorPriceResolver, RemoveRoute).Cancel(orderId, ownerId);
            if (result.Success)
            {
                RemoveRoute(orderId);
            }

            return result;
        }
    }

    /// <summary>
    /// 尝试根据订单 ID 查找活动或终态订单快照。
    /// </summary>
    /// <param name="orderId">订单 ID。</param>
    /// <param name="order">输出订单视图。</param>
    /// <returns>找到返回 true，否则返回 false。</returns>
    public bool TryGetOrder(string orderId, out DaoxuanMarketOrderView? order)
    {
        if (!_routes.TryGetValue(orderId, out var route))
        {
            order = null;
            return false;
        }

        var shard = _shards[route.ShardIndex];
        lock (shard.Gate)
        {
            return shard.GetBook(route.Instrument, _taxRateResolver, _vendorPriceResolver, RemoveRoute).TryGetOrder(orderId, out order);
        }
    }

    /// <summary>
    /// 查询指定玩家当前所有的活动委托订单列表。
    /// </summary>
    /// <param name="ownerId">玩家 GUID。</param>
    /// <param name="limit">最大返回条数（默认 100，最大 500）。</param>
    /// <returns>活动订单列表。</returns>
    public IReadOnlyList<DaoxuanMarketOrderView> GetActiveOrders(Guid ownerId, int limit = 100)
    {
        if (ownerId == Guid.Empty)
        {
            return [];
        }

        // 该查询不是撮合热路径；复用全局路由避免为大量一次性账号创建嵌套字典。
        var normalizedLimit = Math.Clamp(limit, 1, 500);
        var orders = new List<DaoxuanMarketOrderView>(normalizedLimit);
        foreach (var route in _routes)
        {
            if (route.Value.OwnerId != ownerId)
            {
                continue;
            }

            if (TryGetOrder(route.Key, out var order) && order is not null)
            {
                orders.Add(order);
            }
        }

        return orders
            .OrderByDescending(order => order.Sequence)
            .Take(normalizedLimit)
            .ToArray();
    }

    /// <summary>
    /// 获取特定交易品种的当前盘口深度快照。
    /// </summary>
    /// <param name="instrument">交易品种。</param>
    /// <param name="depth">档位深度限制。</param>
    /// <returns>盘口深度快照。</returns>
    public DaoxuanMarketBookSnapshot GetBook(DaoxuanMarketInstrument instrument, int depth = 20)
    {
        return GetBookWithVersion(instrument, depth).Snapshot;
    }

    /// <summary>
    /// 在同一个分片锁内生成盘口和对应版本，防止 HTTP 缓存标签领先于响应正文。
    /// </summary>
    /// <param name="instrument">交易品种。</param>
    /// <param name="depth">深度限制。</param>
    /// <returns>盘口深度快照与版本标签元组。</returns>
    public (DaoxuanMarketBookSnapshot Snapshot, DaoxuanMarketBookVersion Version) GetBookWithVersion(
        DaoxuanMarketInstrument instrument,
        int depth = 20)
    {
        if (!instrument.IsValid) throw new ArgumentException("市场品种无效。", nameof(instrument));
        var shard = _shards[GetShardIndex(instrument)];
        lock (shard.Gate)
        {
            var book = shard.GetBook(instrument, _taxRateResolver, _vendorPriceResolver, RemoveRoute);
            return (book.Snapshot(depth), CreateBookVersion(book));
        }
    }

    /// <summary>
    /// 读取盘口版本而不生成档位数组。HTTP 层可据此在未变化时直接返回 304。
    /// </summary>
    /// <param name="instrument">交易品种。</param>
    /// <returns>盘口当前版本特征结构。</returns>
    public DaoxuanMarketBookVersion GetBookVersion(DaoxuanMarketInstrument instrument)
    {
        if (!instrument.IsValid) throw new ArgumentException("市场品种无效。", nameof(instrument));
        var shard = _shards[GetShardIndex(instrument)];
        lock (shard.Gate)
        {
            if (!shard.Books.TryGetValue(instrument, out var book))
            {
                return default;
            }

            return CreateBookVersion(book);
        }
    }

    /// <summary>
    /// 触发所有分片订单簿的订单过期清理。
    /// </summary>
    /// <param name="now">当前时间戳。</param>
    /// <returns>本次过期的订单列表。</returns>
    public IReadOnlyList<DaoxuanMarketOrderView> Expire(DateTimeOffset now)
    {
        var expired = new List<DaoxuanMarketOrderView>();
        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                foreach (var book in shard.Books.Values)
                {
                    expired.AddRange(book.Expire(now));
                }
            }
        }

        return expired;
    }

    /// <summary>
    /// 汇总收集全部分片的实时统计指标（活跃单数、撮合数、挂单数、成交量等）。
    /// </summary>
    /// <returns>引擎监控指标快照。</returns>
    public DaoxuanMarketEngineMetrics GetMetrics()
    {
        long active = 0;
        long placements = 0;
        long matches = 0;
        long cancels = 0;
        long rejected = 0;
        long quantity = 0;
        long gross = 0;
        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                foreach (var book in shard.Books.Values)
                {
                    active = checked(active + book.ActiveOrderCount);
                    placements = checked(placements + book.PlacementCount);
                    matches = checked(matches + book.MatchCount);
                    cancels = checked(cancels + book.CancelCount);
                    rejected = checked(rejected + book.RejectedCount);
                    quantity = checked(quantity + book.TradedQuantity);
                    gross = checked(gross + book.TradedGrossAmount);
                }
            }
        }

        return new DaoxuanMarketEngineMetrics(
            _shards.Length,
            active,
            placements,
            matches,
            cancels,
            rejected,
            quantity,
            gross);
    }


    private int GetShardIndex(DaoxuanMarketInstrument instrument)
    {
        return DaoxuanMarketPartitioner.GetPartition(instrument, _shards.Length);
    }

    private static bool IsCatalogTradable(string itemId)
    {
        return DaoxuanItemCatalog.TryGet(itemId, out var definition) && definition.IsTradable;
    }

    private static long GetCatalogVendorPrice(string itemId)
    {
        return DaoxuanItemCatalog.TryGet(itemId, out var definition) ? definition.SellPrice : 0;
    }

    private static DaoxuanMarketBookVersion CreateBookVersion(DaoxuanOrderBook book)
    {
        return new DaoxuanMarketBookVersion(
            book.Sequence,
            book.ActiveOrderCount,
            book.PlacementCount,
            book.MatchCount,
            book.CancelCount,
            book.RejectedCount,
            book.TradedQuantity,
            book.TradedGrossAmount);
    }

    private void RemoveRoute(string orderId)
    {
        _routes.TryRemove(orderId, out _);
    }

    private readonly record struct OrderRoute(
        int ShardIndex,
        DaoxuanMarketInstrument Instrument,
        Guid OwnerId);

    private sealed class MarketShard
    {
        public object Gate { get; } = new();
        public Dictionary<DaoxuanMarketInstrument, DaoxuanOrderBook> Books { get; } = [];

        public DaoxuanOrderBook GetBook(
            DaoxuanMarketInstrument instrument,
            Func<string, double> taxRateResolver,
            Func<string, long> vendorPriceResolver,
            Action<string> terminalOrderCallback)
        {
            if (!Books.TryGetValue(instrument, out var book))
            {
                book = new DaoxuanOrderBook(
                    instrument,
                    taxRateResolver,
                    terminalOrderCallback: terminalOrderCallback,
                    vendorPriceResolver: vendorPriceResolver);
                Books.Add(instrument, book);
            }

            return book;
        }
    }
}
