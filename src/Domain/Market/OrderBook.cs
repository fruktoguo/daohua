namespace Domain.Market;

/// <summary>单品种内存订单簿；数据库是权威账本，本类只负责确定性撮合投影。</summary>
public sealed class OrderBook
{
    private readonly Dictionary<OrderId, MarketOrder> _orders = [];
    private long _sequence;
    private long _version;

    public OrderBook(MarketInstrument instrument)
    {
        if (!instrument.ItemId.IsValid || instrument.Variant is null || instrument.Variant.Length > 128)
            throw new DomainException(DomainErrorCode.InvalidArgument, "市场品种无效。");
        Instrument = instrument;
    }

    public MarketInstrument Instrument { get; }
    public long Version => _version;
    public IReadOnlyCollection<MarketOrder> ActiveOrders
    {
        get
        {
            var active = new List<MarketOrder>(_orders.Count);
            foreach (var order in _orders.Values)
            {
                if (IsActive(order)) active.Add(order);
            }
            return active;
        }
    }
    public bool TryGet(OrderId orderId, out MarketOrder order) => _orders.TryGetValue(orderId, out order!);

    /// <summary>从数据库当前活动订单重建内存投影；只导入，不触发撮合或资产变更。</summary>
    public void Restore(IEnumerable<MarketOrder> orders, long version = 0)
    {
        ArgumentNullException.ThrowIfNull(orders);
        if (version < 0) throw new DomainException(DomainErrorCode.InvalidArgument, "盘口版本不能为负数。");
        var imported = orders as IList<MarketOrder> ?? orders.ToList();
        var sorted = new MarketOrder[imported.Count];
        for (var i = 0; i < imported.Count; i++) sorted[i] = imported[i];
        Array.Sort(sorted, static (a, b) => a.Sequence.CompareTo(b.Sequence));
        var ids = new HashSet<OrderId>();
        var sequences = new HashSet<long>();
        _orders.Clear();
        _sequence = 0;
        foreach (var order in sorted)
        {
            if (order.Instrument != Instrument || order.OrderId.IsEmpty || order.OwnerId.IsEmpty ||
                !Enum.IsDefined(order.Side) || !Enum.IsDefined(order.Status) ||
                !double.IsFinite(order.Price) || order.Price <= 0 || !double.IsFinite(order.OriginalQuantity) || order.OriginalQuantity <= 0 ||
                !double.IsFinite(order.RemainingQuantity) || order.RemainingQuantity <= 0 || order.RemainingQuantity > order.OriginalQuantity ||
                order.Sequence <= 0 || !ids.Add(order.OrderId) || !sequences.Add(order.Sequence) || !IsActive(order))
                throw new DomainException(DomainErrorCode.InvalidArgument, "活动订单快照无效。");
            _orders.Add(order.OrderId, order);
            _sequence = Math.Max(_sequence, order.Sequence);
        }
        if (version < _sequence) throw new DomainException(DomainErrorCode.InvalidArgument, "盘口版本不能小于订单序列。");
        _version = version;
    }

    public PlacementResult Place(PlaceOrderRequest request)
    {
        if (request.OrderId.IsEmpty || request.OwnerId.IsEmpty || !request.IdempotencyKey.IsValid ||
            !double.IsFinite(request.Price) || request.Price <= 0 || !double.IsFinite(request.Quantity) || request.Quantity <= 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "订单参数无效。");
        if (!Enum.IsDefined(request.Side))
            throw new DomainException(DomainErrorCode.InvalidArgument, "订单方向无效。");
        if (request.Instrument != Instrument)
            throw new DomainException(DomainErrorCode.InvalidArgument, "订单品种与订单簿不一致。");
        if (!_orders.TryAdd(request.OrderId, new MarketOrder(
                request.OrderId,
                request.OwnerId,
                request.Instrument,
                request.Side,
                request.Price,
                request.Quantity,
                request.Quantity,
                checked(++_sequence),
                request.CreatedAt.ToUniversalTime(),
                null,
                MarketOrderStatus.Active)))
            throw new DomainException(DomainErrorCode.Duplicate, "订单标识已存在。");

        var incoming = _orders[request.OrderId];
        var fills = new List<MarketFill>();
        var candidates = MatchingCandidates(incoming);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (incoming.RemainingQuantity <= 0) break;
            var maker = candidates[i];
            if (maker.OwnerId == incoming.OwnerId) continue;
            var quantity = Math.Min(incoming.RemainingQuantity, maker.RemainingQuantity);
            var price = maker.Price;
            var buyer = incoming.Side == MarketSide.Buy ? incoming : maker;
            var seller = incoming.Side == MarketSide.Sell ? incoming : maker;
            fills.Add(new MarketFill(
                TransactionId.New(),
                buyer.OrderId,
                seller.OrderId,
                buyer.OwnerId,
                seller.OwnerId,
                Instrument,
                price,
                quantity,
                request.CreatedAt.ToUniversalTime()));
            incoming = incoming with
            {
                RemainingQuantity = Math.Max(0, incoming.RemainingQuantity - quantity),
                Status = incoming.RemainingQuantity - quantity <= 1e-12 ? MarketOrderStatus.Filled : MarketOrderStatus.PartiallyFilled
            };
            maker = maker with
            {
                RemainingQuantity = Math.Max(0, maker.RemainingQuantity - quantity),
                Status = maker.RemainingQuantity - quantity <= 1e-12 ? MarketOrderStatus.Filled : MarketOrderStatus.PartiallyFilled
            };
            _orders[incoming.OrderId] = incoming;
            _orders[maker.OrderId] = maker;
        }

        _orders[request.OrderId] = incoming;
        if (incoming.RemainingQuantity <= 1e-12) _orders[request.OrderId] = incoming with { RemainingQuantity = 0, Status = MarketOrderStatus.Filled };
        _version = checked(_version + 1);
        return new PlacementResult(_orders[request.OrderId], fills, _version);
    }

    public CancellationResult Cancel(OrderId orderId, PlayerId ownerId)
    {
        if (!_orders.TryGetValue(orderId, out var order))
            throw new DomainException(DomainErrorCode.NotFound, "订单不存在。");
        if (order.OwnerId != ownerId)
            throw new DomainException(DomainErrorCode.Conflict, "只能撤销自己的订单。");
        if (!IsActive(order))
        {
            if (order.Status == MarketOrderStatus.Cancelled) return new CancellationResult(order, _version);
            throw new DomainException(DomainErrorCode.InvalidTransition, "订单已处于终态。");
        }
        order = order with { Status = MarketOrderStatus.Cancelled };
        _orders[orderId] = order;
        _version = checked(_version + 1);
        return new CancellationResult(order, _version);
    }

    public IReadOnlyList<MarketOrder> Expire(DateTimeOffset now)
    {
        var timestamp = now.ToUniversalTime();
        var expired = new List<MarketOrder>();
        foreach (var order in _orders.Values)
        {
            if (IsActive(order) && order.ExpiresAt is not null && order.ExpiresAt <= timestamp)
            {
                expired.Add(order);
            }
        }
        if (expired.Count == 0) return [];
        expired.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
        for (var i = 0; i < expired.Count; i++)
        {
            var order = expired[i];
            _orders[order.OrderId] = order with { Status = MarketOrderStatus.Expired };
        }
        _version = checked(_version + 1);
        return expired;
    }

    public OrderBookSnapshot Snapshot(int depth = 20, DateTimeOffset? now = null)
    {
        if (depth is < 1 or > 200) throw new DomainException(DomainErrorCode.InvalidArgument, "盘口深度必须在 1 到 200 之间。");
        var askMap = new Dictionary<double, (double Quantity, int Count)>();
        var bidMap = new Dictionary<double, (double Quantity, int Count)>();

        foreach (var order in _orders.Values)
        {
            if (!IsActive(order)) continue;
            var targetMap = order.Side == MarketSide.Sell ? askMap : bidMap;
            if (targetMap.TryGetValue(order.Price, out var current))
            {
                targetMap[order.Price] = (current.Quantity + order.RemainingQuantity, current.Count + 1);
            }
            else
            {
                targetMap[order.Price] = (order.RemainingQuantity, 1);
            }
        }

        var askPrices = new double[askMap.Count];
        askMap.Keys.CopyTo(askPrices, 0);
        Array.Sort(askPrices);
        var askCount = Math.Min(depth, askPrices.Length);
        var asks = new MarketLevel[askCount];
        for (var i = 0; i < askCount; i++)
        {
            var price = askPrices[i];
            var (qty, count) = askMap[price];
            asks[i] = new MarketLevel(price, qty, count);
        }

        var bidPrices = new double[bidMap.Count];
        bidMap.Keys.CopyTo(bidPrices, 0);
        Array.Sort(bidPrices);
        Array.Reverse(bidPrices);
        var bidCount = Math.Min(depth, bidPrices.Length);
        var bids = new MarketLevel[bidCount];
        for (var i = 0; i < bidCount; i++)
        {
            var price = bidPrices[i];
            var (qty, count) = bidMap[price];
            bids[i] = new MarketLevel(price, qty, count);
        }

        return new OrderBookSnapshot(Instrument, asks, bids, _version, (now ?? DateTimeOffset.UtcNow).ToUniversalTime());
    }

    public IReadOnlyList<MarketOrder> OrdersByOwner(PlayerId ownerId, int limit = 100)
    {
        if (ownerId.IsEmpty) throw new DomainException(DomainErrorCode.InvalidArgument, "玩家标识不能为空。");
        if (limit is < 1 or > 1000) throw new DomainException(DomainErrorCode.InvalidArgument, "订单查询上限无效。");
        var list = new List<MarketOrder>();
        foreach (var order in _orders.Values)
        {
            if (order.OwnerId == ownerId && IsActive(order))
            {
                list.Add(order);
            }
        }
        list.Sort(static (a, b) => b.Sequence.CompareTo(a.Sequence));
        if (list.Count > limit) list.RemoveRange(limit, list.Count - limit);
        return list;
    }

    public IReadOnlyList<MarketOrder> ExportActiveOrders()
    {
        var active = new List<MarketOrder>(_orders.Count);
        foreach (var order in _orders.Values)
        {
            if (IsActive(order)) active.Add(order);
        }
        active.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
        return active;
    }

    private List<MarketOrder> MatchingCandidates(MarketOrder incoming)
    {
        var opposite = incoming.Side == MarketSide.Buy ? MarketSide.Sell : MarketSide.Buy;
        var candidates = new List<MarketOrder>();
        foreach (var order in _orders.Values)
        {
            if (IsActive(order) && order.Side == opposite && Crosses(incoming, order))
            {
                candidates.Add(order);
            }
        }

        if (incoming.Side == MarketSide.Buy)
        {
            candidates.Sort(static (a, b) =>
            {
                var cmp = a.Price.CompareTo(b.Price);
                return cmp != 0 ? cmp : a.Sequence.CompareTo(b.Sequence);
            });
        }
        else
        {
            candidates.Sort(static (a, b) =>
            {
                var cmp = b.Price.CompareTo(a.Price);
                return cmp != 0 ? cmp : a.Sequence.CompareTo(b.Sequence);
            });
        }

        return candidates;
    }

    private static bool Crosses(MarketOrder incoming, MarketOrder maker) => incoming.Side == MarketSide.Buy
        ? maker.Price <= incoming.Price
        : maker.Price >= incoming.Price;

    private static bool IsActive(MarketOrder order) =>
        (order.Status is MarketOrderStatus.Active or MarketOrderStatus.PartiallyFilled)
        && order.RemainingQuantity > 1e-12;
}
