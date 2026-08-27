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
}
