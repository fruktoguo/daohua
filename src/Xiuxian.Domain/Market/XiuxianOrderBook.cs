namespace Xiuxian.Domain.Market;

/// <summary>
/// 单一品种订单簿。实例本身不加锁，必须由所属市场分片串行调用，以保证撮合路径无全局锁。
/// </summary>
public sealed class XiuxianOrderBook
{
    private readonly XiuxianMarketInstrument _instrument;
    private readonly Func<string, double> _taxRateResolver;
    private readonly Func<string, long> _vendorPriceResolver;
    private readonly Action<string>? _terminalOrderCallback;
    private readonly int _maxTerminalHistory;
    private readonly SortedDictionary<long, LinkedList<MutableOrder>> _asks = new();
    private readonly SortedDictionary<long, LinkedList<MutableOrder>> _bids =
        new(Comparer<long>.Create((left, right) => right.CompareTo(left)));
    private readonly SortedDictionary<long, HashSet<MutableOrder>> _expirationSchedule = [];
    private readonly Dictionary<string, MutableOrder> _orders = new(StringComparer.Ordinal);
    private readonly Queue<string> _terminalOrderIds = new();
    private long _sequence;
    private long _activeOrderCount;
    private long _placementCount;
    private long _matchCount;
    private long _cancelCount;
    private long _rejectedCount;
    private long _tradedQuantity;
    private long _tradedGrossAmount;

    public XiuxianOrderBook(
        XiuxianMarketInstrument instrument,
        Func<string, double>? taxRateResolver = null,
        int maxTerminalHistory = 10_000,
        Action<string>? terminalOrderCallback = null,
        Func<string, long>? vendorPriceResolver = null)
    {
        if (!instrument.IsValid) throw new ArgumentException("市场品种无效。", nameof(instrument));
        if (maxTerminalHistory is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTerminalHistory));
        }
        _instrument = instrument;
        _taxRateResolver = taxRateResolver ?? XiuxianMarketTaxPolicy.GetSellerTaxRate;
        _vendorPriceResolver = vendorPriceResolver ?? GetCatalogVendorPrice;
        _maxTerminalHistory = maxTerminalHistory;
        _terminalOrderCallback = terminalOrderCallback;
    }

    public XiuxianMarketInstrument Instrument => _instrument;
    public long Sequence => _sequence;
    public long ActiveOrderCount => _activeOrderCount;
    public long PlacementCount => _placementCount;
    public long MatchCount => _matchCount;
    public long CancelCount => _cancelCount;
    public long RejectedCount => _rejectedCount;
    public long TradedQuantity => _tradedQuantity;
    public long TradedGrossAmount => _tradedGrossAmount;

    public XiuxianMarketBookState ExportState()
    {
        return new XiuxianMarketBookState(
            _instrument,
            _orders.Values
                .Where(order => order.Node is not null)
                .OrderBy(order => order.Sequence)
                .Select(order => new XiuxianMarketOrderSnapshot(
                    order.OrderId,
                    order.OwnerId,
                    order.Instrument,
                    order.Side,
                    order.Price,
                    order.OriginalQuantity,
                    order.RemainingQuantity,
                    order.Status,
                    order.Sequence,
                    order.CreatedAt,
                    order.ExpiresAt))
                .ToArray(),
            _sequence,
            _placementCount,
            _matchCount,
            _cancelCount,
            _rejectedCount,
            _tradedQuantity,
            _tradedGrossAmount);
    }

    /// <summary>
    /// 从账本恢复活动订单。恢复只接受非终态订单，并按原始序列重建同价 FIFO。
    /// </summary>
    public void RestoreState(XiuxianMarketBookState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Instrument != _instrument)
        {
            throw new InvalidDataException("订单簿品种与市场快照不一致。");
        }

        _asks.Clear();
        _bids.Clear();
        _expirationSchedule.Clear();
        _orders.Clear();
        _terminalOrderIds.Clear();
        _sequence = 0;
        _activeOrderCount = 0;
        _placementCount = state.PlacementCount;
        _matchCount = state.MatchCount;
        _cancelCount = state.CancelCount;
        _rejectedCount = state.RejectedCount;
        _tradedQuantity = state.TradedQuantity;
        _tradedGrossAmount = state.TradedGrossAmount;

        if (_placementCount < 0 || _matchCount < 0 || _cancelCount < 0 || _rejectedCount < 0
            || _tradedQuantity < 0 || _tradedGrossAmount < 0 || state.Sequence < 0)
        {
            throw new InvalidDataException("市场累计指标不能为负数。");
        }

        var orders = state.ActiveOrders ?? throw new InvalidDataException("市场活动订单不能为空。");
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenSequences = new HashSet<long>();
        foreach (var snapshot in orders.OrderBy(order => order.Sequence))
        {
            if (snapshot.Instrument != _instrument
                || string.IsNullOrWhiteSpace(snapshot.OrderId)
                || snapshot.OwnerId == Guid.Empty
                || snapshot.Price <= 0
                || snapshot.OriginalQuantity <= 0
                || snapshot.RemainingQuantity <= 0
                || snapshot.RemainingQuantity > snapshot.OriginalQuantity
                || snapshot.Sequence <= 0
                || !Enum.IsDefined(snapshot.Side)
                || snapshot.Status is XiuxianMarketOrderStatus.Filled
                    or XiuxianMarketOrderStatus.Cancelled
                    or XiuxianMarketOrderStatus.Expired
                || !seenIds.Add(snapshot.OrderId)
                || !seenSequences.Add(snapshot.Sequence))
            {
                throw new InvalidDataException("市场活动订单快照无效。");
            }

            var expectedStatus = snapshot.RemainingQuantity == snapshot.OriginalQuantity
                ? XiuxianMarketOrderStatus.Active
                : XiuxianMarketOrderStatus.PartiallyFilled;
            if (snapshot.Status != expectedStatus)
            {
                throw new InvalidDataException("市场订单状态与剩余数量不一致。");
            }

            DateTimeOffset createdAt;
            DateTimeOffset expiresAt;
            try
            {
                createdAt = NormalizeCreatedAt(snapshot.CreatedAt, snapshot.ExpiresAt);
                expiresAt = NormalizeExpiresAt(createdAt, snapshot.ExpiresAt);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new InvalidDataException("市场订单时间范围无效。", exception);
            }

            if (expiresAt <= createdAt)
            {
                throw new InvalidDataException("市场订单到期时间必须晚于创建时间。");
            }

            var order = new MutableOrder(
                snapshot.OrderId,
                snapshot.OwnerId,
                snapshot.Instrument,
                snapshot.Side,
                snapshot.Price,
                snapshot.OriginalQuantity,
                snapshot.Sequence,
                createdAt)
            {
                RemainingQuantity = snapshot.RemainingQuantity,
                Status = snapshot.Status,
                ExpiresAt = expiresAt
            };
            _orders.Add(order.OrderId, order);
            AddRestingOrder(order);
            _sequence = Math.Max(_sequence, order.Sequence);
        }

        if (state.Sequence < _sequence)
        {
            throw new InvalidDataException("市场订单序列不能小于活动订单序列。");
        }

        _sequence = state.Sequence;
    }

    public XiuxianMarketPlacementResult Place(XiuxianMarketOrderRequest request)
    {
        _placementCount++;
        if (!Validate(request, out var errorCode, out var errorMessage))
        {
            _rejectedCount++;
            return XiuxianMarketPlacementResult.Fail(errorCode, errorMessage);
        }

        double sellerTaxRate;
        try
        {
            sellerTaxRate = _taxRateResolver(_instrument.ItemId);
        }
        catch (Exception exception) when (exception is ArgumentException or ArithmeticException or InvalidOperationException)
        {
            _rejectedCount++;
            return XiuxianMarketPlacementResult.Fail("INVALID_TAX_POLICY", "市场税率配置无效。");
        }

        if (!double.IsFinite(sellerTaxRate) || sellerTaxRate is < 0 or >= 1)
        {
            _rejectedCount++;
            return XiuxianMarketPlacementResult.Fail("INVALID_TAX_POLICY", "市场税率配置无效。");
        }

        var order = new MutableOrder(
            request.OrderId,
            request.OwnerId,
            request.Instrument,
            request.Side,
            request.Price,
            request.Quantity,
            checked(++_sequence),
            request.CreatedAt ?? DateTimeOffset.UtcNow);
        _orders.Add(order.OrderId, order);
        var fills = new List<XiuxianMarketFill>();
        var opposite = request.Side == XiuxianMarketSide.Buy ? _asks : _bids;

        while (order.RemainingQuantity > 0 && opposite.Count > 0)
        {
            var bestLevel = opposite.First();
            var bestPrice = bestLevel.Key;
            if (!Crosses(request.Side, request.Price, bestPrice)) break;

            var makerNode = bestLevel.Value.First!;
            var maker = makerNode.Value;
            var quantity = Math.Min(order.RemainingQuantity, maker.RemainingQuantity);
            var grossAmount = checked(bestPrice * quantity);
            var sellerProceeds = XiuxianMarketTaxPolicy.CalculateSellerProceeds(
                grossAmount,
                sellerTaxRate);
            var buyer = request.Side == XiuxianMarketSide.Buy ? order : maker;
            var seller = request.Side == XiuxianMarketSide.Sell ? order : maker;
            fills.Add(new XiuxianMarketFill(
                order.OrderId,
                maker.OrderId,
                buyer.OwnerId,
                seller.OwnerId,
                _instrument,
                bestPrice,
                quantity,
                grossAmount,
                sellerProceeds,
                checked(grossAmount - sellerProceeds)));

            order.RemainingQuantity -= quantity;
            maker.RemainingQuantity -= quantity;
            maker.Status = maker.RemainingQuantity == 0
                ? XiuxianMarketOrderStatus.Filled
                : XiuxianMarketOrderStatus.PartiallyFilled;
            if (maker.RemainingQuantity == 0)
            {
                bestLevel.Value.Remove(makerNode);
                maker.Node = null;
                UnscheduleExpiration(maker);
                _activeOrderCount--;
                RecordTerminal(maker);
                if (bestLevel.Value.Count == 0) opposite.Remove(bestPrice);
            }

            _matchCount++;
            _tradedQuantity = checked(_tradedQuantity + quantity);
            _tradedGrossAmount = checked(_tradedGrossAmount + grossAmount);
        }

        order.Status = order.RemainingQuantity == 0
            ? XiuxianMarketOrderStatus.Filled
            : order.FilledQuantity > 0
                ? XiuxianMarketOrderStatus.PartiallyFilled
                : XiuxianMarketOrderStatus.Active;
        if (order.RemainingQuantity > 0)
        {
            AddRestingOrder(order);
        }
        else
        {
            RecordTerminal(order);
        }
        TrimTerminalHistory();

        return new XiuxianMarketPlacementResult(
            true,
            "OK",
            fills.Count == 0 ? "订单已进入订单簿。" : "订单已撮合。",
            ToView(order),
            fills);
    }

    /// <summary>
    /// 低频清理已过期挂牌。调用方应在市场分片锁内调用，避免把时间判断放入撮合循环。
    /// </summary>
    public IReadOnlyList<XiuxianMarketOrderView> Expire(DateTimeOffset now)
    {
        var expired = new List<MutableOrder>();
        var nowTicks = now.ToUniversalTime().UtcTicks;
        while (_expirationSchedule.Count > 0)
        {
            var next = _expirationSchedule.First();
            if (next.Key > nowTicks) break;
            _expirationSchedule.Remove(next.Key);
            expired.AddRange(next.Value.Where(order => order.Node is not null));
        }
        if (expired.Count == 0) return [];

        foreach (var order in expired.OrderBy(order => order.Sequence))
        {
            var levels = order.Side == XiuxianMarketSide.Buy ? _bids : _asks;
            if (order.Node is not null && levels.TryGetValue(order.Price, out var level))
            {
                level.Remove(order.Node);
                if (level.Count == 0) levels.Remove(order.Price);
            }

            order.Node = null;
            order.Status = XiuxianMarketOrderStatus.Expired;
            _activeOrderCount--;
            RecordTerminal(order);
        }

        TrimTerminalHistory();
        return expired.OrderBy(order => order.Sequence).Select(ToView).ToArray();
    }

    public XiuxianMarketCancellationResult Cancel(string orderId, Guid ownerId)
    {
        if (string.IsNullOrWhiteSpace(orderId) || !_orders.TryGetValue(orderId, out var order))
        {
            return XiuxianMarketCancellationResult.Fail("ORDER_NOT_FOUND", "订单不存在。");
        }

        if (order.OwnerId != ownerId)
        {
            return XiuxianMarketCancellationResult.Fail("ORDER_FORBIDDEN", "只能撤销自己的订单。");
        }

        if (order.Status is XiuxianMarketOrderStatus.Filled
            or XiuxianMarketOrderStatus.Cancelled
            or XiuxianMarketOrderStatus.Expired)
        {
            return XiuxianMarketCancellationResult.Fail("ORDER_NOT_ACTIVE", "订单已经结束。");
        }

        if (order.Node is not null)
        {
            var levels = order.Side == XiuxianMarketSide.Buy ? _bids : _asks;
            var level = levels[order.Price];
            level.Remove(order.Node);
            if (level.Count == 0) levels.Remove(order.Price);
            order.Node = null;
            UnscheduleExpiration(order);
        }

        order.Status = XiuxianMarketOrderStatus.Cancelled;
        _activeOrderCount--;
        _cancelCount++;
        RecordTerminal(order);
        TrimTerminalHistory();
        return new XiuxianMarketCancellationResult(true, "OK", "订单已撤销。", ToView(order));
    }

    public bool TryGetOrder(string orderId, out XiuxianMarketOrderView? order)
    {
        if (_orders.TryGetValue(orderId, out var found))
        {
            order = ToView(found);
            return true;
        }

        order = null;
        return false;
    }

    public XiuxianMarketBookSnapshot Snapshot(int depth = 20)
    {
        var normalizedDepth = Math.Clamp(depth, 1, 100);
        return new XiuxianMarketBookSnapshot(
            _instrument,
            ToLevels(_asks, normalizedDepth),
            ToLevels(_bids, normalizedDepth),
            _activeOrderCount,
            _sequence);
    }

    private void AddRestingOrder(MutableOrder order)
    {
        var levels = order.Side == XiuxianMarketSide.Buy ? _bids : _asks;
        if (!levels.TryGetValue(order.Price, out var level))
        {
            level = new LinkedList<MutableOrder>();
            levels.Add(order.Price, level);
        }

        order.Node = level.AddLast(order);
        ScheduleExpiration(order);
        _activeOrderCount++;
    }

    private void ScheduleExpiration(MutableOrder order)
    {
        var expirationTicks = order.ExpiresAt.UtcTicks;
        if (!_expirationSchedule.TryGetValue(expirationTicks, out var orders))
        {
            orders = [];
            _expirationSchedule.Add(expirationTicks, orders);
        }

        orders.Add(order);
    }

    private void UnscheduleExpiration(MutableOrder order)
    {
        var expirationTicks = order.ExpiresAt.UtcTicks;
        if (!_expirationSchedule.TryGetValue(expirationTicks, out var orders)) return;
        orders.Remove(order);
        if (orders.Count == 0) _expirationSchedule.Remove(expirationTicks);
    }

    private void RecordTerminal(MutableOrder order)
    {
        if (order.Node is not null) return;
        if (_maxTerminalHistory > 0)
        {
            _terminalOrderIds.Enqueue(order.OrderId);
        }
        _terminalOrderCallback?.Invoke(order.OrderId);
    }

    private void TrimTerminalHistory()
    {
        while (_terminalOrderIds.Count > _maxTerminalHistory)
        {
            var orderId = _terminalOrderIds.Dequeue();
            if (_orders.TryGetValue(orderId, out var order)
                && order.Node is null
                && order.Status is XiuxianMarketOrderStatus.Filled
                    or XiuxianMarketOrderStatus.Cancelled
                    or XiuxianMarketOrderStatus.Expired)
            {
                _orders.Remove(orderId);
            }
        }
    }

    private bool Validate(
        XiuxianMarketOrderRequest request,
        out string errorCode,
        out string errorMessage)
    {
        if (request is null)
        {
            errorCode = "INVALID_REQUEST";
            errorMessage = "订单参数不能为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.OrderId))
        {
            errorCode = "INVALID_ORDER_ID";
            errorMessage = "订单 ID 不能为空。";
            return false;
        }

        if (_orders.ContainsKey(request.OrderId))
        {
            errorCode = "DUPLICATE_ORDER_ID";
            errorMessage = "订单 ID 已存在。";
            return false;
        }

        if (request.OwnerId == Guid.Empty)
        {
            errorCode = "INVALID_OWNER";
            errorMessage = "订单所有者无效。";
            return false;
        }

        if (request.Instrument != _instrument)
        {
            errorCode = "INSTRUMENT_MISMATCH";
            errorMessage = "订单品种与当前订单簿不匹配。";
            return false;
        }

        if (!Enum.IsDefined(request.Side) || request.Price <= 0 || request.Quantity <= 0)
        {
            errorCode = "INVALID_ORDER";
            errorMessage = "订单方向、价格和数量必须有效。";
            return false;
        }

        if (request.CreatedAt is { } createdAt
            && createdAt.ToUniversalTime() > DateTimeOffset.MaxValue - XiuxianMarketPricePolicy.ListingLifetime)
        {
            errorCode = "INVALID_CREATED_AT";
            errorMessage = "订单创建时间超出支持范围。";
            return false;
        }

        long vendorPrice;
        try
        {
            vendorPrice = _vendorPriceResolver(request.Instrument.ItemId);
        }
        catch (Exception exception) when (exception is ArgumentException or ArithmeticException or InvalidOperationException)
        {
            errorCode = "INVALID_VENDOR_PRICE";
            errorMessage = "物品商店回收价配置无效。";
            return false;
        }

        if (vendorPrice < 0)
        {
            errorCode = "INVALID_VENDOR_PRICE";
            errorMessage = "物品商店回收价配置无效。";
            return false;
        }

        if (request.Price < Math.Max(XiuxianMarketPricePolicy.AbsoluteMinimumPrice, vendorPrice))
        {
            errorCode = "PRICE_BELOW_VENDOR";
            errorMessage = "挂牌价格不能低于物品商店回收价。";
            return false;
        }

        if (!XiuxianMarketPricePolicy.IsValid(request.Price, vendorPrice))
        {
            errorCode = "INVALID_PRICE_INCREMENT";
            errorMessage = "挂牌价格不符合当前价格档位。";
            return false;
        }

        try
        {
            _ = checked(request.Price * request.Quantity);
        }
        catch (OverflowException)
        {
            errorCode = "ORDER_TOO_LARGE";
            errorMessage = "订单金额超出支持范围。";
            return false;
        }

        errorCode = string.Empty;
        errorMessage = string.Empty;
        return true;
    }

    private static long GetCatalogVendorPrice(string itemId)
    {
        return Xiuxian.Domain.Simulation.XiuxianItemCatalog.TryGet(itemId, out var definition)
            ? definition.SellPrice
            : 0;
    }

    private static DateTimeOffset NormalizeCreatedAt(
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (createdAt != default) return createdAt.ToUniversalTime();
        if (expiresAt != default)
        {
            return expiresAt.ToUniversalTime() - XiuxianMarketPricePolicy.ListingLifetime;
        }

        // 兼容没有时间字段的旧账本：从恢复时刻开始计算新的有效期，避免旧订单因
        // 缺少元数据被立即退回或永久占用库存。
        return DateTimeOffset.UtcNow;
    }

    private static DateTimeOffset NormalizeExpiresAt(
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (expiresAt != default) return expiresAt.ToUniversalTime();
        return createdAt.ToUniversalTime() + XiuxianMarketPricePolicy.ListingLifetime;
    }

    private static bool Crosses(XiuxianMarketSide incomingSide, long incomingPrice, long makerPrice)
    {
        return incomingSide == XiuxianMarketSide.Buy
            ? makerPrice <= incomingPrice
            : makerPrice >= incomingPrice;
    }

    private static IReadOnlyList<XiuxianMarketLevel> ToLevels(
        SortedDictionary<long, LinkedList<MutableOrder>> levels,
        int depth)
    {
        return levels
            .Take(depth)
            .Select(pair => new XiuxianMarketLevel(
                pair.Key,
                pair.Value.Sum(order => order.RemainingQuantity),
                pair.Value.Count))
            .ToArray();
    }

    private static XiuxianMarketOrderView ToView(MutableOrder order)
    {
        return new XiuxianMarketOrderView(
            order.OrderId,
            order.OwnerId,
            order.Instrument,
            order.Side,
            order.Price,
            order.OriginalQuantity,
            order.RemainingQuantity,
            order.FilledQuantity,
            order.Status,
            order.Sequence,
            order.CreatedAt,
            order.ExpiresAt);
    }

    private sealed class MutableOrder
    {
        public MutableOrder(
            string orderId,
            Guid ownerId,
            XiuxianMarketInstrument instrument,
            XiuxianMarketSide side,
            long price,
            long quantity,
            long sequence,
            DateTimeOffset createdAt)
        {
            OrderId = orderId;
            OwnerId = ownerId;
            Instrument = instrument;
            Side = side;
            Price = price;
            OriginalQuantity = quantity;
            RemainingQuantity = quantity;
            Sequence = sequence;
            CreatedAt = createdAt.ToUniversalTime();
            ExpiresAt = CreatedAt + XiuxianMarketPricePolicy.ListingLifetime;
        }

        public string OrderId { get; }
        public Guid OwnerId { get; }
        public XiuxianMarketInstrument Instrument { get; }
        public XiuxianMarketSide Side { get; }
        public long Price { get; }
        public long OriginalQuantity { get; }
        public long RemainingQuantity { get; set; }
        public long FilledQuantity => OriginalQuantity - RemainingQuantity;
        public XiuxianMarketOrderStatus Status { get; set; }
        public long Sequence { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset ExpiresAt { get; set; }
        public LinkedListNode<MutableOrder>? Node { get; set; }
    }
}
