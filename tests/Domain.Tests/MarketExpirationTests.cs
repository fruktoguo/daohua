namespace Domain.Tests;

/// <summary>
/// 市场过期测试：30 天边界与恢复后剩余量的处理。
/// </summary>
public sealed class MarketExpirationTests
{
    /// <summary>
    /// 验证 <c>ThirtyDayBoundaryExcludesExpiredMakerAndPreservesRestoredRemainder</c>（Thirty Day Boundary Excludes Expired Maker And Preserves Restored Remainder）所描述的领域行为。
    /// </summary>
    [Fact]
    public void ThirtyDayBoundaryExcludesExpiredMakerAndPreservesRestoredRemainder()
    {
        var instrument = new MarketInstrument(new ItemId("item.ningxue-grass"));
        var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var book = new OrderBook(instrument);
        var sell = book.Place(new PlaceOrderRequest(OrderId.New(), PlayerId.New(), instrument, MarketSide.Sell, 5, 3, now, new IdempotencyKey("sell")));
        Assert.Equal(now.AddDays(30), sell.IncomingOrder.ExpiresAt);
        book.Place(new PlaceOrderRequest(OrderId.New(), PlayerId.New(), instrument, MarketSide.Buy, 5, 1, now.AddDays(29), new IdempotencyKey("buy-before")));
        var restored = new OrderBook(instrument);
        restored.Restore(book.ExportActiveOrders(), book.Version);
        var boundary = restored.Place(new PlaceOrderRequest(OrderId.New(), PlayerId.New(), instrument, MarketSide.Buy, 5, 1, now.AddDays(30), new IdempotencyKey("buy-at")));
        Assert.Empty(boundary.Fills);
        var expired = Assert.Single(restored.Expire(now.AddDays(30)));
        Assert.Equal(2, expired.RemainingQuantity);
        Assert.True(restored.TryGet(sell.IncomingOrder.OrderId, out var closed));
        Assert.Equal(MarketOrderStatus.Expired, closed.Status);
        Assert.Empty(restored.Expire(now.AddDays(30)));
    }
}
