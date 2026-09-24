namespace Domain.Tests;

/// <summary>
/// 撮合结构等价测试：价格有序索引实现与「全量扫描 + 排序」朴素参照模型逐位对照，
/// 覆盖价格优先、Sequence 优先、自成交跳过、到期排除、部分成交链与版本演进；
/// 另含到期索引只读枚举与就地变更撤销日志的回归。
/// </summary>
public sealed class MarketMatchingEquivalenceTests
{
    /// <summary>
    /// 混合随机操作序列下，索引撮合与朴素全扫撮合的逐笔成交、订单终态、
    /// 盘口版本与活动订单集必须逐位一致。
    /// </summary>
    [Fact]
    public void IndexedMatchingMatchesNaiveScanAcrossMixedOperations()
    {
        var instrument = new MarketInstrument(new ItemId("item.match-equiv"));
        var book = new OrderBook(instrument);
        var naive = new NaiveOrderBook();
        var random = new Random(20260924);
        var players = Enumerable.Range(0, 6).Select(_ => PlayerId.New()).ToArray();
        var open = new List<OrderId>();
        var baseTime = new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

        for (var step = 0; step < 400; step++)
        {
            var now = baseTime.AddMinutes(step);
            var roll = random.Next(100);
            if (roll < 70 || open.Count == 0)
            {
                // 挂单：价格集中在小数网格制造同价冲突，方向/数量/属主随机。
                var side = random.Next(2) == 0 ? MarketSide.Buy : MarketSide.Sell;
                var price = Math.Round(1 + random.NextDouble() * 20, 1);
                var quantity = 1 + random.Next(5);
                var owner = players[random.Next(players.Length)];
                var request = new PlaceOrderRequest(OrderId.New(), owner, instrument, side, price, quantity, now, new IdempotencyKey($"k{step}"));
                var expected = naive.Place(request);
                var actual = book.Place(request);
                Assert.Equal(expected.IncomingOrder, actual.IncomingOrder);
                Assert.Equal(expected.Fills.Select(FillKey), actual.Fills.Select(FillKey));
                Assert.Equal(expected.BookVersion, actual.BookVersion);
                if (actual.IncomingOrder.Status is MarketOrderStatus.Active or MarketOrderStatus.PartiallyFilled)
                    open.Add(request.OrderId);
            }
            else if (roll < 90)
            {
                // 撤单：目标可能已作为 maker 成交至终态，双方结局（结果或异常类型）必须一致。
                var orderId = open[random.Next(open.Count)];
                var owner = naive.OwnerOf(orderId);
                CancellationResult? expected = null;
                CancellationResult? actual = null;
                var expectedError = Record.Exception(() => expected = naive.Cancel(orderId, owner));
                var actualError = Record.Exception(() => actual = book.Cancel(orderId, owner));
                Assert.Equal(expectedError?.GetType(), actualError?.GetType());
                Assert.Equal((expectedError as DomainException)?.Code, (actualError as DomainException)?.Code);
                if (expectedError is null && actualError is null)
                {
                    Assert.Equal(expected!.Order, actual!.Order);
                    Assert.Equal(expected.BookVersion, actual.BookVersion);
                }
                open.Remove(orderId);
            }
            else
            {
                // 到期下架：cutoff 覆盖全部订单（created+30d 必然早于 step+31d）。
                var expected = naive.Expire(now.AddDays(31));
                var actual = book.Expire(now.AddDays(31));
                Assert.Equal(expected, actual);
                Assert.Equal(naive.Version, book.Version);
                open.RemoveAll(id => expected.Any(order => order.OrderId == id));
            }
        }

        // 终态对照：活动订单集（Sequence 升序导出）逐位一致。
        Assert.Equal(naive.ActiveOrders(), book.ExportActiveOrders());
        Assert.Equal(naive.Version, book.Version);
    }

    /// <summary>
    /// 到期索引回归：GetExpiredOrders 只回到期队首且按 Sequence 升序、不修改簿内状态；
    /// Expire 下架后索引同步清空，重复调用返回空。
    /// </summary>
    [Fact]
    public void ExpiryIndexReturnsDueOrdersInSequenceOrderWithoutMutating()
    {
        var instrument = new MarketInstrument(new ItemId("item.expiry-index"));
        var book = new OrderBook(instrument);
        var baseTime = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var orders = new MarketOrder[4];
        for (var i = 0; i < orders.Length; i++)
        {
            // 交错创建时刻使 ExpiresAt（created+30d）各不相同。
            orders[i] = book.Place(new PlaceOrderRequest(
                OrderId.New(), PlayerId.New(), instrument, MarketSide.Sell, 10 + i, 1,
                baseTime.AddDays(i), new IdempotencyKey($"sell-{i}"))).IncomingOrder;
        }

        var versionBefore = book.Version;
        var cutoff = baseTime.AddDays(31);   // 仅前两笔到期
        var due = book.GetExpiredOrders(cutoff);
        Assert.Equal(new[] { orders[0].OrderId, orders[1].OrderId }, due.Select(order => order.OrderId).ToArray());
        // 只读枚举不得改变簿内状态：版本不变、订单仍挂牌。
        Assert.Equal(versionBefore, book.Version);
        Assert.Equal(4, book.ExportActiveOrders().Count);
        Assert.Equal(due.Select(order => order.OrderId), book.GetExpiredOrders(cutoff).Select(order => order.OrderId));

        var expired = book.Expire(cutoff);
        Assert.Equal(new[] { orders[0].OrderId, orders[1].OrderId }, expired.Select(order => order.OrderId).ToArray());
        Assert.Equal(versionBefore + 1, book.Version);
        Assert.Empty(book.GetExpiredOrders(cutoff));
        Assert.Empty(book.Expire(cutoff));
        Assert.Equal(2, book.ExportActiveOrders().Count);
    }

    /// <summary>
    /// 撤销日志回归：就地撮合后 Undo 必须逐位还原订单集、版本号与撮合序号；
    /// 继续挂单得到的序号与版本与未发生变更的路径一致。
    /// </summary>
    [Fact]
    public void JournalUndoRestoresExactPriorBookState()
    {
        var instrument = new MarketInstrument(new ItemId("item.journal-undo"));
        var book = new OrderBook(instrument);
        var now = DateTimeOffset.UtcNow;
        var seller = PlayerId.New();
        var sell = book.Place(new PlaceOrderRequest(OrderId.New(), seller, instrument, MarketSide.Sell, 10, 5, now, new IdempotencyKey("sell"))).IncomingOrder;
        var versionBefore = book.Version;
        var activeBefore = book.ExportActiveOrders();

        var journal = new OrderBookJournal();
        var filled = book.Place(new PlaceOrderRequest(OrderId.New(), PlayerId.New(), instrument, MarketSide.Buy, 12, 5, now.AddSeconds(1), new IdempotencyKey("buy")), journal);
        Assert.Single(filled.Fills);
        Assert.Empty(book.ExportActiveOrders());   // 全部成交，盘口清空

        book.Undo(journal);
        Assert.Equal(versionBefore, book.Version);
        Assert.Equal(activeBefore, book.ExportActiveOrders());
        Assert.True(book.TryGet(sell.OrderId, out var restored));
        Assert.Equal(sell, restored);

        // 回滚后重新挂单：新订单应拿到与未回滚分支相同的 Sequence。
        var replay = book.Place(new PlaceOrderRequest(OrderId.New(), PlayerId.New(), instrument, MarketSide.Buy, 12, 5, now.AddSeconds(1), new IdempotencyKey("buy-2")), journal);
        Assert.Equal(filled.IncomingOrder.Sequence, replay.IncomingOrder.Sequence);
    }

    private static (Guid Buy, Guid Sell, double Price, double Quantity, DateTimeOffset TradedAt) FillKey(MarketFill fill) =>
        (fill.BuyOrderId.Value, fill.SellOrderId.Value, fill.Price, fill.Quantity, fill.TradedAt);

    /// <summary>
    /// 朴素参照订单簿：忠实复刻「全量扫描候选 + 显式排序 + 逐笔成交」规格，
    /// 不含任何索引，只作为等价对照真源。
    /// </summary>
    private sealed class NaiveOrderBook
    {
        private readonly List<MarketOrder> _orders = [];
        private long _sequence;

        public long Version { get; private set; }

        public PlayerId OwnerOf(OrderId orderId) => _orders.First(order => order.OrderId == orderId).OwnerId;

        public PlacementResult Place(PlaceOrderRequest request)
        {
            var incoming = new MarketOrder(
                request.OrderId, request.OwnerId, request.Instrument, request.Side,
                request.Price, request.Quantity, request.Quantity, checked(++_sequence),
                request.CreatedAt.ToUniversalTime(), request.CreatedAt.ToUniversalTime().AddDays(30),
                MarketOrderStatus.Active);
            _orders.Add(incoming);

            var opposite = incoming.Side == MarketSide.Buy ? MarketSide.Sell : MarketSide.Buy;
            var candidates = new List<MarketOrder>();
            foreach (var order in _orders)
            {
                if (order.OrderId == incoming.OrderId || !IsActive(order) || order.Side != opposite
                    || (order.ExpiresAt is not null && order.ExpiresAt <= incoming.CreatedAt)
                    || !Crosses(incoming, order))
                    continue;
                candidates.Add(order);
            }
            candidates.Sort(incoming.Side == MarketSide.Buy
                ? static (a, b) => { var cmp = a.Price.CompareTo(b.Price); return cmp != 0 ? cmp : a.Sequence.CompareTo(b.Sequence); }
                : static (a, b) => { var cmp = b.Price.CompareTo(a.Price); return cmp != 0 ? cmp : a.Sequence.CompareTo(b.Sequence); });

            var fills = new List<MarketFill>();
            foreach (var maker in candidates)
            {
                if (incoming.RemainingQuantity <= 0) break;
                if (maker.OwnerId == incoming.OwnerId) continue;
                var quantity = Math.Min(incoming.RemainingQuantity, maker.RemainingQuantity);
                var price = maker.Price;
                var buyer = incoming.Side == MarketSide.Buy ? incoming : maker;
                var seller = incoming.Side == MarketSide.Sell ? incoming : maker;
                fills.Add(new MarketFill(
                    TransactionId.New(), buyer.OrderId, seller.OrderId, buyer.OwnerId, seller.OwnerId,
                    incoming.Instrument, price, quantity, request.CreatedAt.ToUniversalTime()));
                incoming = incoming with
                {
                    RemainingQuantity = Math.Max(0, incoming.RemainingQuantity - quantity),
                    Status = incoming.RemainingQuantity - quantity <= 1e-12 ? MarketOrderStatus.Filled : MarketOrderStatus.PartiallyFilled
                };
                var updatedMaker = maker with
                {
                    RemainingQuantity = Math.Max(0, maker.RemainingQuantity - quantity),
                    Status = maker.RemainingQuantity - quantity <= 1e-12 ? MarketOrderStatus.Filled : MarketOrderStatus.PartiallyFilled
                };
                Replace(incoming);
                Replace(updatedMaker);
            }
            if (incoming.RemainingQuantity <= 1e-12)
                incoming = incoming with { RemainingQuantity = 0, Status = MarketOrderStatus.Filled };
            Replace(incoming);
            Version = checked(Version + 1);
            return new PlacementResult(incoming, fills, Version);
        }

        public CancellationResult Cancel(OrderId orderId, PlayerId ownerId)
        {
            var order = _orders.First(order => order.OrderId == orderId);
            if (order.OwnerId != ownerId) throw new DomainException(DomainErrorCode.Conflict, "只能撤销自己的订单。");
            if (!IsActive(order)) throw new DomainException(DomainErrorCode.InvalidTransition, "订单已处于终态。");
            order = order with { Status = MarketOrderStatus.Cancelled };
            Replace(order);
            Version = checked(Version + 1);
            return new CancellationResult(order, Version);
        }

        public IReadOnlyList<MarketOrder> Expire(DateTimeOffset now)
        {
            var timestamp = now.ToUniversalTime();
            var expired = _orders
                .Where(order => IsActive(order) && order.ExpiresAt is not null && order.ExpiresAt <= timestamp)
                .OrderBy(order => order.Sequence)
                .ToList();
            if (expired.Count == 0) return [];
            foreach (var order in expired) Replace(order with { Status = MarketOrderStatus.Expired });
            Version = checked(Version + 1);
            return expired;
        }

        public IReadOnlyList<MarketOrder> ActiveOrders() => _orders
            .Where(IsActive)
            .OrderBy(order => order.Sequence)
            .ToArray();

        private void Replace(MarketOrder order)
        {
            var index = _orders.FindIndex(existing => existing.OrderId == order.OrderId);
            _orders[index] = order;
        }

        private static bool Crosses(MarketOrder incoming, MarketOrder maker) => incoming.Side == MarketSide.Buy
            ? maker.Price <= incoming.Price
            : maker.Price >= incoming.Price;

        private static bool IsActive(MarketOrder order) =>
            (order.Status is MarketOrderStatus.Active or MarketOrderStatus.PartiallyFilled)
            && order.RemainingQuantity > 1e-12;
    }
}
