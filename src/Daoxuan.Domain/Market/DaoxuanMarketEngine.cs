using System.Collections.Concurrent;
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

    public int ShardCount => _shards.Length;

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

    public DaoxuanMarketBookSnapshot GetBook(DaoxuanMarketInstrument instrument, int depth = 20)
    {
        return GetBookWithVersion(instrument, depth).Snapshot;
    }

    /// <summary>
    /// 在同一个分片锁内生成盘口和对应版本，防止 HTTP 缓存标签领先于响应正文。
    /// </summary>
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
