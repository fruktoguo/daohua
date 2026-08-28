namespace Domain.Tests;

public sealed class MarketTests
{
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
}
