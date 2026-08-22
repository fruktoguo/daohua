// ==============================================================================
// 文件名称: DaoHuaMarketTests.cs
// 主要职责: 灵市撮合引擎（DaoHuaMarketEngine）与订单簿（DaoHuaOrderBook）核心规则单元测试。
// 核心逻辑 / 定义内容:
//   1. 验证价格优先、时间优先（Price-Time Priority）撮合原则与部分成交状态流转。
//   2. 验证多交易品类分片路由、限价单价格偏离限制与撤单权限控制。
//   3. 验证订单簿快照生成、增量版本控制与超期订单批量扫描回收逻辑。
// ==============================================================================

using DaoHua.Domain.Market;
using DaoHua.Domain.Models;
using Xunit;

namespace DaoHua.Domain.Tests;

/// <summary>
/// 验证 DaoHuaMarket（灵市撮合引擎与订单簿）的核心算法、分区哈希、价格档位规则、撤单权限与快照版本控制。
/// </summary>
public sealed class DaoHuaMarketTests
{
    private static readonly DaoHuaMarketInstrument Herb = new(DaoHuaItemIds.SpiritHerb);

    /// <summary>
    /// 验证订单簿的价格优先与时间优先（Price-Time Priority）撮合原则，以及标准成交税扣除。
    /// </summary>
    [Fact]
    public void Order_book_uses_price_time_priority_and_standard_tax()
    {
        var book = new DaoHuaOrderBook(Herb);
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
        Assert.Equal(DaoHuaMarketOrderStatus.Filled, filled!.Status);

        Assert.True(book.TryGetOrder("s2", out var remaining));
        Assert.Equal(3, remaining!.RemainingQuantity);
        Assert.Equal(DaoHuaMarketOrderStatus.PartiallyFilled, remaining.Status);
        Assert.Equal(3, book.Snapshot().Asks[0].Quantity);
    }

    /// <summary>
    /// 验证撤单权限所有权校验（非本人禁止撤单），且撤销指定品类挂单不影响其他交易品种。
    /// </summary>
    [Fact]
    public void Cancellation_is_owner_checked_and_does_not_touch_other_instrument()
    {
        var engine = new DaoHuaMarketEngine(shardCount: 8);
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

        var otherInstrument = new DaoHuaMarketInstrument(DaoHuaItemIds.QiBeastCore);
        Assert.True(engine.Place(Sell("other", owner, otherInstrument, 12, 1)).Success);
        Assert.Single(engine.GetBook(otherInstrument).Asks);
    }

    /// <summary>
    /// 验证非法价格（<=0）与不可交易物品（如真气等系统专属货币）在挂单入口处被立即拦截。
    /// </summary>
    [Fact]
    public void Invalid_and_nontradable_orders_are_rejected_without_creating_books()
    {
        var engine = new DaoHuaMarketEngine();
        var invalid = engine.Place(Sell("bad", Guid.NewGuid(), 0, 1));
        Assert.False(invalid.Success);
        Assert.Equal("INVALID_ORDER", invalid.Code);

        var nontradable = engine.Place(Sell(
            "qi",
            Guid.NewGuid(),
            new DaoHuaMarketInstrument(DaoHuaItemIds.Qi),
            1,
            1));
        Assert.False(nontradable.Success);
        Assert.Equal("ITEM_NOT_TRADABLE", nontradable.Code);
    }

    /// <summary>
    /// 验证异常税率策略（如产生 NaN 浮点计算错误）在挂单入簿前被安全拦截并返回错误代码。
    /// </summary>
    [Fact]
    public void Invalid_tax_policy_is_rejected_before_order_enters_book()
    {
        var book = new DaoHuaOrderBook(Herb, _ => double.NaN);

        var result = book.Place(Sell("invalid-tax", Guid.NewGuid(), 10, 2));

        Assert.False(result.Success);
        Assert.Equal("INVALID_TAX_POLICY", result.Code);
        Assert.Equal(0, book.ActiveOrderCount);
        Assert.Empty(book.Snapshot().Asks);
        Assert.False(book.TryGetOrder("invalid-tax", out _));
    }

    /// <summary>
    /// 验证多分片引擎支持多线程并发挂单，分片隔离锁机制保证计数与指标严格准确。
    /// </summary>
    [Fact]
    public void Sharded_engine_handles_parallel_instrument_orders()
    {
        var engine = new DaoHuaMarketEngine(shardCount: 16);
        var instrument = new DaoHuaMarketInstrument(DaoHuaItemIds.SpiritHerb);
        Parallel.For(0, 1_000, index =>
        {
            engine.Place(Sell($"parallel-{index}", Guid.NewGuid(), instrument, 10 + index % 3, 1));
        });

        var metrics = engine.GetMetrics();
        Assert.Equal(1_000, metrics.PlacementCount);
        Assert.Equal(1_000, metrics.ActiveOrderCount);
    }

    /// <summary>
    /// 验证完全成交的买卖单会自动从活跃订单索引中清理释放，避免内存无界泄露。
    /// </summary>
    [Fact]
    public void Terminal_order_route_is_released_after_match()
    {
        var engine = new DaoHuaMarketEngine(shardCount: 2);
        var seller = Sell("terminal-sell", Guid.NewGuid(), 3, 1);
        Assert.True(engine.Place(seller).Success);
        var buyer = Buy("terminal-buy", Guid.NewGuid(), 3, 1);
        Assert.True(engine.Place(buyer).Success);

        Assert.False(engine.TryGetOrder(seller.OrderId, out _));
        Assert.False(engine.TryGetOrder(buyer.OrderId, out _));
        Assert.Equal(0, engine.GetMetrics().ActiveOrderCount);
    }

    /// <summary>
    /// 验证导出订单簿快照并还原后，能精确保留原有的价格档位、时间挂单次序与序列号。
    /// </summary>
    [Fact]
    public void Order_book_snapshot_restores_price_time_priority()
    {
        var firstOwner = Guid.NewGuid();
        var secondOwner = Guid.NewGuid();
        var book = new DaoHuaOrderBook(Herb);
        Assert.True(book.Place(Sell("restore-first", firstOwner, 10, 2)).Success);
        Assert.True(book.Place(Sell("restore-second", secondOwner, 10, 2)).Success);

        var restored = new DaoHuaOrderBook(Herb);
        restored.RestoreState(book.ExportState());
        var result = restored.Place(Buy("restore-buy", Guid.NewGuid(), 10, 3));

        Assert.Equal(["restore-first", "restore-second"], result.Fills.Select(fill => fill.MakerOrderId));
        Assert.Equal([2L, 1L], result.Fills.Select(fill => fill.Quantity));
        Assert.Equal(3, restored.ExportState().Sequence);
    }

    /// <summary>
    /// 验证玩家专属挂单索引在新增挂单、快照恢复以及主动撤单全流程中的同步准确性。
    /// </summary>
    [Fact]
    public void Owner_order_index_tracks_active_and_restored_orders()
    {
        var owner = Guid.NewGuid();
        var engine = new DaoHuaMarketEngine(shardCount: 4);
        Assert.True(engine.Place(Sell("owner-index", owner, 10, 2)).Success);
        Assert.Equal("owner-index", Assert.Single(engine.GetActiveOrders(owner)).OrderId);

        var restored = new DaoHuaMarketEngine(shardCount: 4);
        restored.RestoreState(engine.ExportState());
        Assert.Equal("owner-index", Assert.Single(restored.GetActiveOrders(owner)).OrderId);

        Assert.True(restored.Cancel("owner-index", owner).Success);
        Assert.Empty(restored.GetActiveOrders(owner));
    }

    /// <summary>
    /// 验证灵市品种哈希算法（FNV-1a UTF-16）的确定性与强化等级敏感性，确保相同品种分片路由稳定。
    /// </summary>
    [Fact]
    public void Market_partition_hash_is_stable_and_includes_enhancement_level()
    {
        var baseInstrument = new DaoHuaMarketInstrument(DaoHuaItemIds.SpiritHerb, 0);
        var enhancedInstrument = new DaoHuaMarketInstrument(DaoHuaItemIds.SpiritHerb, 7);

        Assert.Equal("fnv1a64-utf16-v1", DaoHuaMarketPartitioner.Algorithm);
        Assert.Equal(60, DaoHuaMarketPartitioner.GetPartition(baseInstrument, 64));
        Assert.Equal(43, DaoHuaMarketPartitioner.GetPartition(enhancedInstrument, 64));
        Assert.True(DaoHuaMarketPartitioner.IsOwnedBy(baseInstrument, 60, 64));
        Assert.False(DaoHuaMarketPartitioner.IsOwnedBy(baseInstrument, 59, 64));
    }

    /// <summary>
    /// 验证非法分片拓扑参数（分片数<=0 或分片越界）时抛出参数异常。
    /// </summary>
    [Fact]
    public void Market_partition_hash_rejects_invalid_topology()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DaoHuaMarketPartitioner.GetPartition(Herb, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DaoHuaMarketPartitioner.IsOwnedBy(Herb, 2, 2));
    }

    /// <summary>
    /// 验证 1-2-5 阶梯价格步进规则（Price Increment Bands），在不同单价区间下步长平滑递增。
    /// </summary>
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
        Assert.Equal(increment, DaoHuaMarketPricePolicy.GetIncrement(price));
    }

    /// <summary>
    /// 验证价格规范化取整（向上/向下）以及禁止低于系统回收价（Vendor Floor）的底线约束。
    /// </summary>
    [Fact]
    public void Market_price_policy_rounds_to_band_and_enforces_vendor_floor()
    {
        Assert.Equal(74, DaoHuaMarketPricePolicy.Normalize(75, 3, roundUp: false));
        Assert.Equal(76, DaoHuaMarketPricePolicy.Normalize(75, 3, roundUp: true));
        Assert.True(DaoHuaMarketPricePolicy.IsValid(76, 3));
        Assert.False(DaoHuaMarketPricePolicy.IsValid(75, 3));
        Assert.False(DaoHuaMarketPricePolicy.IsValid(2, 3));

        var engine = new DaoHuaMarketEngine();
        var invalidIncrement = engine.Place(Sell("invalid-increment", Guid.NewGuid(), 75, 1));
        Assert.False(invalidIncrement.Success);
        Assert.Equal("INVALID_PRICE_INCREMENT", invalidIncrement.Code);

        var belowVendor = engine.Place(Sell("below-vendor", Guid.NewGuid(), 2, 1));
        Assert.False(belowVendor.Success);
        Assert.Equal("PRICE_BELOW_VENDOR", belowVendor.Code);
    }

    /// <summary>
    /// 验证订单具有 30 天有效期限，到期自动出簿并标记为 Expired，快照恢复后到期时间保持一致。
    /// </summary>
    [Fact]
    public void Market_orders_expire_at_thirty_days_and_restore_time_metadata()
    {
        var createdAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var expiresAt = createdAt + DaoHuaMarketPricePolicy.ListingLifetime;
        var owner = Guid.NewGuid();
        var engine = new DaoHuaMarketEngine(shardCount: 4);
        var placement = engine.Place(new DaoHuaMarketOrderRequest(
            "expires-after-thirty-days",
            owner,
            Herb,
            DaoHuaMarketSide.Sell,
            10,
            2,
            createdAt));

        Assert.True(placement.Success);
        Assert.Equal(createdAt, placement.Order!.CreatedAt);
        Assert.Equal(expiresAt, placement.Order.ExpiresAt);

        var restored = new DaoHuaMarketEngine(shardCount: 4);
        restored.RestoreState(engine.ExportState());
        var restoredOrder = Assert.Single(restored.GetActiveOrders(owner));
        Assert.Equal(createdAt, restoredOrder.CreatedAt);
        Assert.Equal(expiresAt, restoredOrder.ExpiresAt);
        Assert.Empty(restored.Expire(expiresAt.AddTicks(-1)));

        var expired = Assert.Single(restored.Expire(expiresAt));
        Assert.Equal(DaoHuaMarketOrderStatus.Expired, expired.Status);
        Assert.Empty(restored.GetActiveOrders(owner));
        Assert.Empty(restored.GetBook(Herb).Asks);
        Assert.False(restored.TryGetOrder(expired.OrderId, out _));
    }

    /// <summary>
    /// 验证盘口只读版本号（BookVersion）在挂单、部分成交、撤单与超期回收各生命周期的递增演进。
    /// </summary>
    [Fact]
    public void Market_book_read_version_tracks_partial_fill_cancel_and_expiry()
    {
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var engine = new DaoHuaMarketEngine(shardCount: 4);
        var initial = engine.GetBookVersion(Herb);
        var seller = Guid.NewGuid();
        var placement = engine.Place(new DaoHuaMarketOrderRequest(
            "version-sell",
            seller,
            Herb,
            DaoHuaMarketSide.Sell,
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

        var fill = engine.Place(new DaoHuaMarketOrderRequest(
            "version-buy",
            Guid.NewGuid(),
            Herb,
            DaoHuaMarketSide.Buy,
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

        var expiring = engine.Place(new DaoHuaMarketOrderRequest(
            "version-expire",
            Guid.NewGuid(),
            Herb,
            DaoHuaMarketSide.Sell,
            10,
            1,
            now));
        Assert.True(expiring.Success);
        var beforeExpiration = engine.GetBookVersion(Herb);
        Assert.Single(engine.Expire(now + DaoHuaMarketPricePolicy.ListingLifetime));
        var afterExpiration = engine.GetBookVersion(Herb);
        Assert.NotEqual(beforeExpiration, afterExpiration);
        Assert.Equal(0, afterExpiration.ActiveOrderCount);
    }

    private static DaoHuaMarketOrderRequest Sell(string id, Guid owner, long price, long quantity) =>
        Sell(id, owner, Herb, price, quantity);

    private static DaoHuaMarketOrderRequest Sell(
        string id,
        Guid owner,
        DaoHuaMarketInstrument instrument,
        long price,
        long quantity) =>
        new(id, owner, instrument, DaoHuaMarketSide.Sell, price, quantity);

    private static DaoHuaMarketOrderRequest Buy(string id, Guid owner, long price, long quantity) =>
        new(id, owner, Herb, DaoHuaMarketSide.Buy, price, quantity);
}
