using Daoxuan.Domain.Market;
using Daoxuan.Domain.Models;
using Xunit;

namespace Daoxuan.Domain.Tests;

public sealed class DaoxuanMarketTests
{
    private static readonly DaoxuanMarketInstrument Herb = new(DaoxuanItemIds.SpiritHerb);

    [Fact]
    public void Order_book_uses_price_time_priority_and_standard_tax()
    {
        var book = new DaoxuanOrderBook(Herb);
        var sellerOne = Guid.NewGuid();
        var sellerTwo = Guid.NewGuid();
        var buyer = Guid.NewGuid();

        Assert.True(book.Place(Sell("s1", sellerOne, 10, 5)).Success);
        Assert.True(book.Place(Sell("s2", sellerTwo, 10, 5)).Success);
        var result = book.Place(Buy("b1", buyer, 12, 7));

        Assert.True(result.Success);
        Assert.Equal(["s1", "s2"], result.Fills.Select(fill => fill.MakerOrderId));
        Assert.Equal([5L, 2L], result.Fills.Select(fill => fill.Quantity));
        Assert.Equal(49, result.Fills[0].SellerProceeds);
        Assert.Equal(1, result.Fills[0].SellerTax);
        Assert.True(book.TryGetOrder("s1", out var filled));
        Assert.Equal(DaoxuanMarketOrderStatus.Filled, filled!.Status);

        Assert.True(book.TryGetOrder("s2", out var remaining));
        Assert.Equal(3, remaining!.RemainingQuantity);
        Assert.Equal(DaoxuanMarketOrderStatus.PartiallyFilled, remaining.Status);
        Assert.Equal(3, book.Snapshot().Asks[0].Quantity);
    }

    [Fact]
    public void Cancellation_is_owner_checked_and_does_not_touch_other_instrument()
    {
        var engine = new DaoxuanMarketEngine(shardCount: 8);
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var order = Sell("cancel-me", owner, 5, 2);
        Assert.True(engine.Place(order).Success);

        var forbidden = engine.Cancel(order.OrderId, other);
        Assert.False(forbidden.Success);
        Assert.Equal("ORDER_FORBIDDEN", forbidden.Code);

        var cancelled = engine.Cancel(order.OrderId, owner);
        Assert.True(cancelled.Success);
        Assert.Empty(engine.GetBook(Herb).Asks);

        var otherInstrument = new DaoxuanMarketInstrument(DaoxuanItemIds.QiBeastCore);
        Assert.True(engine.Place(Sell("other", owner, otherInstrument, 12, 1)).Success);
        Assert.Single(engine.GetBook(otherInstrument).Asks);
    }

    [Fact]
    public void Invalid_and_nontradable_orders_are_rejected_without_creating_books()
    {
        var engine = new DaoxuanMarketEngine();
        var invalid = engine.Place(Sell("bad", Guid.NewGuid(), 0, 1));
        Assert.False(invalid.Success);
        Assert.Equal("INVALID_ORDER", invalid.Code);

        var nontradable = engine.Place(Sell(
            "qi",
            Guid.NewGuid(),
            new DaoxuanMarketInstrument(DaoxuanItemIds.Qi),
            1,
            1));
        Assert.False(nontradable.Success);
        Assert.Equal("ITEM_NOT_TRADABLE", nontradable.Code);
    }

    [Fact]
    public void Invalid_tax_policy_is_rejected_before_order_enters_book()
    {
        var book = new DaoxuanOrderBook(Herb, _ => double.NaN);

        var result = book.Place(Sell("invalid-tax", Guid.NewGuid(), 10, 2));

        Assert.False(result.Success);
        Assert.Equal("INVALID_TAX_POLICY", result.Code);
        Assert.Equal(0, book.ActiveOrderCount);
        Assert.Empty(book.Snapshot().Asks);
        Assert.False(book.TryGetOrder("invalid-tax", out _));
    }

    [Fact]
    public void Sharded_engine_handles_parallel_instrument_orders()
    {
        var engine = new DaoxuanMarketEngine(shardCount: 16);
        var instrument = new DaoxuanMarketInstrument(DaoxuanItemIds.SpiritHerb);
        Parallel.For(0, 1_000, index =>
        {
            engine.Place(Sell($"parallel-{index}", Guid.NewGuid(), instrument, 10 + index % 3, 1));
        });

        var metrics = engine.GetMetrics();
        Assert.Equal(1_000, metrics.PlacementCount);
        Assert.Equal(1_000, metrics.ActiveOrderCount);
    }

    [Fact]
    public void Terminal_order_route_is_released_after_match()
    {
        var engine = new DaoxuanMarketEngine(shardCount: 2);
        var seller = Sell("terminal-sell", Guid.NewGuid(), 3, 1);
        Assert.True(engine.Place(seller).Success);
        var buyer = Buy("terminal-buy", Guid.NewGuid(), 3, 1);
        Assert.True(engine.Place(buyer).Success);

        Assert.False(engine.TryGetOrder(seller.OrderId, out _));
        Assert.False(engine.TryGetOrder(buyer.OrderId, out _));
        Assert.Equal(0, engine.GetMetrics().ActiveOrderCount);
    }

    [Fact]
    public void Order_book_snapshot_restores_price_time_priority()
    {
        var firstOwner = Guid.NewGuid();
        var secondOwner = Guid.NewGuid();
        var book = new DaoxuanOrderBook(Herb);
        Assert.True(book.Place(Sell("restore-first", firstOwner, 10, 2)).Success);
        Assert.True(book.Place(Sell("restore-second", secondOwner, 10, 2)).Success);

        var restored = new DaoxuanOrderBook(Herb);
        restored.RestoreState(book.ExportState());
        var result = restored.Place(Buy("restore-buy", Guid.NewGuid(), 10, 3));

        Assert.Equal(["restore-first", "restore-second"], result.Fills.Select(fill => fill.MakerOrderId));
        Assert.Equal([2L, 1L], result.Fills.Select(fill => fill.Quantity));
        Assert.Equal(3, restored.ExportState().Sequence);
    }

    [Fact]
    public void Owner_order_index_tracks_active_and_restored_orders()
    {
        var owner = Guid.NewGuid();
        var engine = new DaoxuanMarketEngine(shardCount: 4);
        Assert.True(engine.Place(Sell("owner-index", owner, 10, 2)).Success);
        Assert.Equal("owner-index", Assert.Single(engine.GetActiveOrders(owner)).OrderId);

        var restored = new DaoxuanMarketEngine(shardCount: 4);
        restored.RestoreState(engine.ExportState());
        Assert.Equal("owner-index", Assert.Single(restored.GetActiveOrders(owner)).OrderId);

        Assert.True(restored.Cancel("owner-index", owner).Success);
        Assert.Empty(restored.GetActiveOrders(owner));
    }

    [Fact]
    public void Market_partition_hash_is_stable_and_includes_enhancement_level()
    {
        var baseInstrument = new DaoxuanMarketInstrument(DaoxuanItemIds.SpiritHerb, 0);
        var enhancedInstrument = new DaoxuanMarketInstrument(DaoxuanItemIds.SpiritHerb, 7);

        Assert.Equal("fnv1a64-utf16-v1", DaoxuanMarketPartitioner.Algorithm);
        Assert.Equal(60, DaoxuanMarketPartitioner.GetPartition(baseInstrument, 64));
        Assert.Equal(43, DaoxuanMarketPartitioner.GetPartition(enhancedInstrument, 64));
        Assert.True(DaoxuanMarketPartitioner.IsOwnedBy(baseInstrument, 60, 64));
        Assert.False(DaoxuanMarketPartitioner.IsOwnedBy(baseInstrument, 59, 64));
    }

    [Fact]
    public void Market_partition_hash_rejects_invalid_topology()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DaoxuanMarketPartitioner.GetPartition(Herb, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DaoxuanMarketPartitioner.IsOwnedBy(Herb, 2, 2));
    }

    [Theory]
    [InlineData(49, 1)]
    [InlineData(50, 2)]
    [InlineData(100, 5)]
    [InlineData(300, 10)]
    [InlineData(500, 20)]
    [InlineData(1_000, 50)]
    [InlineData(5_000_000, 200_000)]
    public void Market_price_increment_uses_stable_one_two_five_bands(long price, long increment)
    {
        Assert.Equal(increment, DaoxuanMarketPricePolicy.GetIncrement(price));
    }

    [Fact]
    public void Market_price_policy_rounds_to_band_and_enforces_vendor_floor()
    {
        Assert.Equal(74, DaoxuanMarketPricePolicy.Normalize(75, 3, roundUp: false));
        Assert.Equal(76, DaoxuanMarketPricePolicy.Normalize(75, 3, roundUp: true));
        Assert.True(DaoxuanMarketPricePolicy.IsValid(76, 3));
        Assert.False(DaoxuanMarketPricePolicy.IsValid(75, 3));
        Assert.False(DaoxuanMarketPricePolicy.IsValid(2, 3));

        var engine = new DaoxuanMarketEngine();
        var invalidIncrement = engine.Place(Sell("invalid-increment", Guid.NewGuid(), 75, 1));
        Assert.False(invalidIncrement.Success);
        Assert.Equal("INVALID_PRICE_INCREMENT", invalidIncrement.Code);

        var belowVendor = engine.Place(Sell("below-vendor", Guid.NewGuid(), 2, 1));
        Assert.False(belowVendor.Success);
        Assert.Equal("PRICE_BELOW_VENDOR", belowVendor.Code);
    }

    [Fact]
    public void Market_orders_expire_at_thirty_days_and_restore_time_metadata()
    {
        var createdAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var expiresAt = createdAt + DaoxuanMarketPricePolicy.ListingLifetime;
        var owner = Guid.NewGuid();
        var engine = new DaoxuanMarketEngine(shardCount: 4);
        var placement = engine.Place(new DaoxuanMarketOrderRequest(
            "expires-after-thirty-days",
            owner,
            Herb,
            DaoxuanMarketSide.Sell,
            10,
            2,
            createdAt));

        Assert.True(placement.Success);
        Assert.Equal(createdAt, placement.Order!.CreatedAt);
        Assert.Equal(expiresAt, placement.Order.ExpiresAt);

        var restored = new DaoxuanMarketEngine(shardCount: 4);
        restored.RestoreState(engine.ExportState());
        var restoredOrder = Assert.Single(restored.GetActiveOrders(owner));
        Assert.Equal(createdAt, restoredOrder.CreatedAt);
        Assert.Equal(expiresAt, restoredOrder.ExpiresAt);
        Assert.Empty(restored.Expire(expiresAt.AddTicks(-1)));

        var expired = Assert.Single(restored.Expire(expiresAt));
        Assert.Equal(DaoxuanMarketOrderStatus.Expired, expired.Status);
        Assert.Empty(restored.GetActiveOrders(owner));
        Assert.Empty(restored.GetBook(Herb).Asks);
        Assert.False(restored.TryGetOrder(expired.OrderId, out _));
    }

    [Fact]
    public void Market_book_read_version_tracks_partial_fill_cancel_and_expiry()
    {
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var engine = new DaoxuanMarketEngine(shardCount: 4);
        var initial = engine.GetBookVersion(Herb);
        var seller = Guid.NewGuid();
        var placement = engine.Place(new DaoxuanMarketOrderRequest(
            "version-sell",
            seller,
            Herb,
            DaoxuanMarketSide.Sell,
            10,
            3,
            now));
        Assert.True(placement.Success);

        var afterPlacement = engine.GetBookVersion(Herb);
        Assert.NotEqual(initial, afterPlacement);
        Assert.Equal(1, afterPlacement.ActiveOrderCount);
        var read = engine.GetBookWithVersion(Herb);
        Assert.Equal(read.Snapshot.Sequence, read.Version.Sequence);
        Assert.Equal(read.Snapshot.ActiveOrderCount, read.Version.ActiveOrderCount);

        var fill = engine.Place(new DaoxuanMarketOrderRequest(
            "version-buy",
            Guid.NewGuid(),
            Herb,
            DaoxuanMarketSide.Buy,
            10,
            1,
            now));
        Assert.True(fill.Success);
        var afterPartialFill = engine.GetBookVersion(Herb);
        Assert.NotEqual(afterPlacement, afterPartialFill);
        Assert.Equal(afterPlacement.ActiveOrderCount, afterPartialFill.ActiveOrderCount);
        Assert.Equal(1, afterPartialFill.MatchCount);
        Assert.Equal(1, afterPartialFill.TradedQuantity);

        Assert.True(engine.Cancel("version-sell", seller).Success);
        var afterCancellation = engine.GetBookVersion(Herb);
        Assert.NotEqual(afterPartialFill, afterCancellation);
        Assert.Equal(0, afterCancellation.ActiveOrderCount);
        Assert.Equal(1, afterCancellation.CancelCount);

        var expiring = engine.Place(new DaoxuanMarketOrderRequest(
            "version-expire",
            Guid.NewGuid(),
            Herb,
            DaoxuanMarketSide.Sell,
            10,
            1,
            now));
        Assert.True(expiring.Success);
        var beforeExpiration = engine.GetBookVersion(Herb);
        Assert.Single(engine.Expire(now + DaoxuanMarketPricePolicy.ListingLifetime));
        var afterExpiration = engine.GetBookVersion(Herb);
        Assert.NotEqual(beforeExpiration, afterExpiration);
        Assert.Equal(0, afterExpiration.ActiveOrderCount);
    }

    private static DaoxuanMarketOrderRequest Sell(string id, Guid owner, long price, long quantity) =>
        Sell(id, owner, Herb, price, quantity);

    private static DaoxuanMarketOrderRequest Sell(
        string id,
        Guid owner,
        DaoxuanMarketInstrument instrument,
        long price,
        long quantity) =>
        new(id, owner, instrument, DaoxuanMarketSide.Sell, price, quantity);

    private static DaoxuanMarketOrderRequest Buy(string id, Guid owner, long price, long quantity) =>
        new(id, owner, Herb, DaoxuanMarketSide.Buy, price, quantity);
}
