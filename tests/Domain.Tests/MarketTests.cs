namespace Domain.Tests;

/// <summary>
/// 市场测试：价格时间优先撮合、所有权撤单、恢复投影与版本锁定不变量。
/// </summary>
public sealed class MarketTests
{
    /// <summary>
    /// 价格-时间优先撮合：高价买单按更优的挂单价成交，剩余量留在盘口。
    /// </summary>
    [Fact]
    public void PriceTimePriorityProducesDeterministicFill()
    {
        var instrument = new MarketInstrument(new ItemId("item.ningxue-grass"));
        var book = new OrderBook(instrument);
        var seller = PlayerId.New();
        var buyer = PlayerId.New();
        var now = DateTimeOffset.UtcNow;
        book.Place(new PlaceOrderRequest(OrderId.New(), seller, instrument, MarketSide.Sell, 5, 3, now, new IdempotencyKey("sell-1")));
        var result = book.Place(new PlaceOrderRequest(OrderId.New(), buyer, instrument, MarketSide.Buy, 6, 2, now.AddMilliseconds(1), new IdempotencyKey("buy-1")));

        var fill = Assert.Single(result.Fills);
        Assert.Equal(5, fill.Price);
        Assert.Equal(2, fill.Quantity);
        var active = Assert.Single(book.ExportActiveOrders());
        Assert.Equal(1, active.RemainingQuantity);
    }

    /// <summary>
    /// 撤单校验所有者身份：非订单所属玩家撤单必须抛错。
    /// </summary>
    [Fact]
    public void OwnerCannotCancelAnotherPlayersOrder()
    {
        var instrument = new MarketInstrument(new ItemId("item.ningxue-grass"));
        var book = new OrderBook(instrument);
        var owner = PlayerId.New();
        var order = new PlaceOrderRequest(OrderId.New(), owner, instrument, MarketSide.Sell, 5, 1, DateTimeOffset.UtcNow, new IdempotencyKey("sell-1"));
        book.Place(order);
        Assert.Throws<DomainException>(() => book.Cancel(order.OrderId, PlayerId.New()));
    }

    /// <summary>
    /// Restore 只重建盘口投影与版本号，不得触发撮合产生意外成交。
    /// </summary>
    [Fact]
    public void RestoreRebuildsProjectionWithoutMatching()
    {
        var instrument = new MarketInstrument(new ItemId("item.ningxue-grass"));
        var seller = PlayerId.New();
        var now = DateTimeOffset.UtcNow;
        var source = new OrderBook(instrument);
        var placed = source.Place(new PlaceOrderRequest(OrderId.New(), seller, instrument, MarketSide.Sell, 5, 3, now, new IdempotencyKey("sell-1")));
        var restored = new OrderBook(instrument);
        restored.Restore(new[] { placed.IncomingOrder }, version: placed.BookVersion);

        Assert.Single(restored.ActiveOrders);
        Assert.Equal(placed.IncomingOrder.OrderId, restored.ActiveOrders.Single().OrderId);
        Assert.Equal(placed.BookVersion, restored.Version);
    }

    /// <summary>
    /// 恢复时未定义的订单状态码、以及低于订单 Sequence 的盘口版本均必须拒绝。
    /// </summary>
    [Fact]
    public void RestoreRejectsInvalidStatusAndSequence()
    {
        var instrument = new MarketInstrument(new ItemId("item.ningxue-grass"));
        var restored = new OrderBook(instrument);
        var invalid = new MarketOrder(OrderId.New(), PlayerId.New(), instrument, MarketSide.Sell, 1, 1, 1, 1, DateTimeOffset.UtcNow, null, (MarketOrderStatus)99);

        var error = Assert.Throws<DomainException>(() => restored.Restore([invalid], version: 1));

        Assert.Equal(DomainErrorCode.InvalidArgument, error.Code);
    }

    /// <summary>锁定不变量：盘口 version 与订单 sequence 是独立计数空间，但每次挂单同步递增、
    /// 撤单/过期只递增 version，且数据库恢复路径以 max(Sequence) 播种 version，因此恢复时恒有 version ≥ max(sequence)。</summary>
    [Fact]
    public void RestoreLocksVersionNotBelowOrderSequence()
    {
        var instrument = new MarketInstrument(new ItemId("item.ningxue-grass"));
        var book = new OrderBook(instrument);
        var placed = book.Place(new PlaceOrderRequest(OrderId.New(), PlayerId.New(), instrument, MarketSide.Sell, 5, 1, DateTimeOffset.UtcNow, new IdempotencyKey("sell-1")));
        var restored = new OrderBook(instrument);

        var error = Assert.Throws<DomainException>(() => restored.Restore([placed.IncomingOrder], version: placed.IncomingOrder.Sequence - 1));

        Assert.Equal(DomainErrorCode.InvalidArgument, error.Code);
        restored.Restore([placed.IncomingOrder], version: placed.IncomingOrder.Sequence);
        Assert.Equal(placed.IncomingOrder.Sequence, restored.Version);
    }

    [Fact]
    public void PlaceRejectsDecimalQuantity()
    {
        var instrument = new MarketInstrument(new ItemId("item.ningxue-grass"));
        var book = new OrderBook(instrument);
        var error = Assert.Throws<DomainException>(() =>
            book.Place(new PlaceOrderRequest(OrderId.New(), PlayerId.New(), instrument, MarketSide.Sell, 5, 1.5, DateTimeOffset.UtcNow, new IdempotencyKey("sell-decimal"))));
        Assert.Equal(DomainErrorCode.InvalidArgument, error.Code);
    }

    /// <summary>
    /// 每标的单笔约束：同一玩家在本簿已有活动挂单时，同向第二笔与反向买卖都被拒绝；
    /// 撤单把订单移出属主索引后立即可换向重挂，其他玩家与其他标的/变体不受影响。
    /// </summary>
    [Fact]
    public void PlaceRejectsSecondActiveOrderOfSameOwnerOnly()
    {
        var instrument = new MarketInstrument(new ItemId("item.single-order"));
        var book = new OrderBook(instrument);
        var owner = PlayerId.New();
        var other = PlayerId.New();
        var now = DateTimeOffset.UtcNow;
        var sell = book.Place(new PlaceOrderRequest(OrderId.New(), owner, instrument, MarketSide.Sell, 5, 2, now, new IdempotencyKey("sell-1")));

        var sameSide = Assert.Throws<DomainException>(() =>
            book.Place(new PlaceOrderRequest(OrderId.New(), owner, instrument, MarketSide.Sell, 7, 1, now, new IdempotencyKey("sell-2"))));
        Assert.Equal(DomainErrorCode.OwnerOrderExists, sameSide.Code);
        var opposite = Assert.Throws<DomainException>(() =>
            book.Place(new PlaceOrderRequest(OrderId.New(), owner, instrument, MarketSide.Buy, 9, 1, now, new IdempotencyKey("buy-1"))));
        Assert.Equal(DomainErrorCode.OwnerOrderExists, opposite.Code);

        // 被拒绝的请求不留痕：盘口仍只有原卖单，且没有任何资产冻结类副作用由簿侧产生。
        var active = Assert.Single(book.ExportActiveOrders());
        Assert.Equal(sell.IncomingOrder.OrderId, active.OrderId);
        Assert.Equal(1, book.Version);

        // 其他玩家可正常挂同标的反向单（并撮合），不受他人活动单约束。
        Assert.Single(book.Place(new PlaceOrderRequest(OrderId.New(), other, instrument, MarketSide.Buy, 5, 1, now, new IdempotencyKey("other-buy"))).Fills);

        // 撤单后立即可换向重挂；换向单不再被拒。
        book.Cancel(sell.IncomingOrder.OrderId, owner);
        var rebid = book.Place(new PlaceOrderRequest(OrderId.New(), owner, instrument, MarketSide.Buy, 4, 1, now, new IdempotencyKey("buy-after-cancel")));
        Assert.Equal(MarketOrderStatus.Active, rebid.IncomingOrder.Status);
        Assert.Empty(rebid.Fills);
        Assert.Null(book.ActiveOrderOf(PlayerId.New()));
    }

    /// <summary>
    /// 每标的约束的作用域是单本订单簿：同一玩家在基础簿与 "+3" 强化簿可各挂一笔，
    /// 与钱包按 (itemId, variant) 行冻结互不挤占的口径一致。
    /// </summary>
    [Fact]
    public void PlaceAllowsOneOrderPerInstrumentAcrossVariants()
    {
        var baseInstrument = new MarketInstrument(new ItemId("equipment.qingfeng-sword"));
        var enhanced = new MarketInstrument(new ItemId("equipment.qingfeng-sword"), "+3");
        var owner = PlayerId.New();
        var now = DateTimeOffset.UtcNow;
        var baseBook = new OrderBook(baseInstrument);
        var enhancedBook = new OrderBook(enhanced);

        baseBook.Place(new PlaceOrderRequest(OrderId.New(), owner, baseInstrument, MarketSide.Sell, 10, 1, now, new IdempotencyKey("base-sell")));
        enhancedBook.Place(new PlaceOrderRequest(OrderId.New(), owner, enhanced, MarketSide.Sell, 100, 1, now, new IdempotencyKey("plus3-sell")));

        Assert.NotNull(baseBook.ActiveOrderOf(owner));
        Assert.NotNull(enhancedBook.ActiveOrderOf(owner));
        Assert.Equal(enhanced, enhancedBook.ActiveOrderOf(owner)!.Instrument);
    }

    /// <summary>
    /// 恢复重建后约束依旧生效：Restore 按活动订单重造属主索引，
    /// 重启前已存在的单在重启后继续阻止同标的新增挂单。
    /// </summary>
    [Fact]
    public void RestoreRebuildsOwnerIndexSoConstraintSurvivesRestart()
    {
        var instrument = new MarketInstrument(new ItemId("item.restored-owner"));
        var owner = PlayerId.New();
        var now = DateTimeOffset.UtcNow;
        var source = new OrderBook(instrument);
        var placed = source.Place(new PlaceOrderRequest(OrderId.New(), owner, instrument, MarketSide.Sell, 5, 3, now, new IdempotencyKey("sell")));

        var restored = new OrderBook(instrument);
        restored.Restore(source.ExportActiveOrders(), source.Version);

        Assert.Equal(placed.IncomingOrder.OrderId, restored.ActiveOrderOf(owner)?.OrderId);
        var error = Assert.Throws<DomainException>(() =>
            restored.Place(new PlaceOrderRequest(OrderId.New(), owner, instrument, MarketSide.Buy, 6, 1, now, new IdempotencyKey("buy"))));
        Assert.Equal(DomainErrorCode.OwnerOrderExists, error.Code);
    }
}
