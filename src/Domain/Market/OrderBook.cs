namespace Domain.Market;

/// <summary>单品种内存订单簿；数据库是权威账本，本类只负责确定性撮合投影。</summary>
public sealed class OrderBook
{
    private readonly Dictionary<OrderId, MarketOrder> _orders = [];
    private long _sequence;
    private long _version;

    public OrderBook(MarketInstrument instrument)
    {
        if (!instrument.ItemId.IsValid) throw new DomainException(DomainErrorCode.InvalidArgument, "市场品种无效。");
        Instrument = instrument;
    }

    public MarketInstrument Instrument { get; }
    public long Version => _version;
    public IReadOnlyCollection<MarketOrder> ActiveOrders => _orders.Values.Where(IsActive).ToArray();
    public bool TryGet(OrderId orderId, out MarketOrder order) => _orders.TryGetValue(orderId, out order!);

    /// <summary>从数据库当前活动订单重建内存投影；只导入，不触发撮合或资产变更。</summary>
    public void Restore(IEnumerable<MarketOrder> orders, long version = 0)
    {
        ArgumentNullException.ThrowIfNull(orders);
        if (version < 0) throw new DomainException(DomainErrorCode.InvalidArgument, "盘口版本不能为负数。");
        var imported = orders.OrderBy(order => order.Sequence).ToArray();
        var ids = new HashSet<OrderId>();
        var sequences = new HashSet<long>();
        _orders.Clear();
        _sequence = 0;
        foreach (var order in imported)
        {
            if (order.Instrument != Instrument || order.OrderId.IsEmpty || order.OwnerId.IsEmpty ||
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
        foreach (var makerSnapshot in MatchingCandidates(incoming).ToArray())
        {
            if (incoming.RemainingQuantity <= 0) break;
            var maker = makerSnapshot;
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
        var expired = _orders.Values
            .Where(order => IsActive(order) && order.ExpiresAt is not null && order.ExpiresAt <= timestamp)
            .OrderBy(order => order.Sequence)
            .ToArray();
        foreach (var order in expired) _orders[order.OrderId] = order with { Status = MarketOrderStatus.Expired };
        if (expired.Length > 0) _version = checked(_version + 1);
        return expired;
    }

    public OrderBookSnapshot Snapshot(int depth = 20, DateTimeOffset? now = null)
    {
        if (depth is < 1 or > 200) throw new DomainException(DomainErrorCode.InvalidArgument, "盘口深度必须在 1 到 200 之间。");
        static IReadOnlyList<MarketLevel> Levels(IEnumerable<MarketOrder> orders, int depth) => orders
            .GroupBy(order => order.Price)
            .OrderBy(group => group.Key)
            .Take(depth)
            .Select(group => new MarketLevel(group.Key, group.Sum(order => order.RemainingQuantity), group.Count()))
            .ToArray();
        var asks = Levels(_orders.Values.Where(order => IsActive(order) && order.Side == MarketSide.Sell), depth);
        var bids = _orders.Values.Where(order => IsActive(order) && order.Side == MarketSide.Buy)
            .GroupBy(order => order.Price)
            .OrderByDescending(group => group.Key)
            .Take(depth)
            .Select(group => new MarketLevel(group.Key, group.Sum(order => order.RemainingQuantity), group.Count()))
            .ToArray();
        return new OrderBookSnapshot(Instrument, asks, bids, _version, (now ?? DateTimeOffset.UtcNow).ToUniversalTime());
    }

    public IReadOnlyList<MarketOrder> OrdersByOwner(PlayerId ownerId, int limit = 100)
    {
        if (ownerId.IsEmpty) throw new DomainException(DomainErrorCode.InvalidArgument, "玩家标识不能为空。");
        if (limit is < 1 or > 1000) throw new DomainException(DomainErrorCode.InvalidArgument, "订单查询上限无效。");
        return _orders.Values.Where(order => order.OwnerId == ownerId && IsActive(order)).OrderByDescending(order => order.Sequence).Take(limit).ToArray();
    }

    public IReadOnlyList<MarketOrder> ExportActiveOrders() => _orders.Values.Where(IsActive).OrderBy(order => order.Sequence).ToArray();

    private IEnumerable<MarketOrder> MatchingCandidates(MarketOrder incoming)
    {
        var opposite = incoming.Side == MarketSide.Buy ? MarketSide.Sell : MarketSide.Buy;
        var candidates = _orders.Values.Where(order => IsActive(order) && order.Side == opposite && Crosses(incoming, order));
        return incoming.Side == MarketSide.Buy
            ? candidates.OrderBy(order => order.Price).ThenBy(order => order.Sequence)
            : candidates.OrderByDescending(order => order.Price).ThenBy(order => order.Sequence);
    }

    private static bool Crosses(MarketOrder incoming, MarketOrder maker) => incoming.Side == MarketSide.Buy
        ? maker.Price <= incoming.Price
        : maker.Price >= incoming.Price;

    private static bool IsActive(MarketOrder order) =>
        (order.Status is MarketOrderStatus.Active or MarketOrderStatus.PartiallyFilled)
        && order.RemainingQuantity > 1e-12;
}
