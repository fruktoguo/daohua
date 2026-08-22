using Daoxuan.Domain.Market;
using Daoxuan.Domain.Models;
using Daoxuan.Domain.Simulation;
using Xunit;

namespace Daoxuan.Domain.Tests;

/// <summary>
/// 验证 DaoxuanMarketCoordinator（灵市协调器）的资产冻结、订单协调、撮合结算、撤单退款与快照恢复。
/// 确保玩家背包资产与灵市挂单账本在不同生命周期转换中保持严格一致，无资产凭空产生或丢失。
/// </summary>
public sealed class DaoxuanMarketCoordinatorTests
{
    /// <summary>
    /// 验证买卖单挂单时的资产冻结、部分撮合成交时的资产交割与剩余未成交撤单退款流程。
    /// 涵盖卖方扣除物品、买方扣除灵石、撮合扣除交易税并计算卖方所得，以及撤单退还剩余质押。
    /// </summary>
    [Fact]
    public void Sell_and_buy_orders_freeze_and_settle_assets_with_partial_fill()
    {
        var engine = new DaoxuanMarketEngine(shardCount: 4);
        var coordinator = new DaoxuanMarketCoordinator(engine);
        var seller = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid());
        var buyer = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid());
        seller.Inventory.Add(DaoxuanItemIds.SpiritHerb, 5);
        buyer.Inventory.Add(DaoxuanItemIds.Spirit, 100);

        var sell = new DaoxuanMarketOrderRequest(
            "coordinator-sell",
            seller.PlayerId,
            new DaoxuanMarketInstrument(DaoxuanItemIds.SpiritHerb),
            DaoxuanMarketSide.Sell,
            10,
            5);
        Assert.True(coordinator.Place(seller, sell).Success);
        Assert.Equal(0, seller.Inventory.Get(DaoxuanItemIds.SpiritHerb));

        var buy = new DaoxuanMarketOrderRequest(
            "coordinator-buy",
            buyer.PlayerId,
            sell.Instrument,
            DaoxuanMarketSide.Buy,
            12,
            3);
        var placement = coordinator.Place(buyer, buy);
        Assert.True(placement.Success);
        Assert.Equal(70, buyer.Inventory.Get(DaoxuanItemIds.Spirit));
        Assert.Equal(3, buyer.Inventory.Get(DaoxuanItemIds.SpiritHerb));
        coordinator.Claim(seller);
        Assert.Equal(29, seller.Inventory.Get(DaoxuanItemIds.Spirit));
        Assert.Equal(1, coordinator.ActiveReservationCount);

        Assert.True(coordinator.Cancel(seller, sell.OrderId).Success);
        Assert.Equal(2, seller.Inventory.Get(DaoxuanItemIds.SpiritHerb));
        Assert.Equal(0, coordinator.ActiveReservationCount);
    }

    /// <summary>
    /// 验证当玩家背包中可用灵石/物品不足时，挂单请求被原子性拒绝，且引擎不产生脏订单。
    /// </summary>
    [Fact]
    public void Insufficient_assets_do_not_create_market_orders()
    {
        var engine = new DaoxuanMarketEngine();
        var coordinator = new DaoxuanMarketCoordinator(engine);
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid());
        var request = new DaoxuanMarketOrderRequest(
            "no-spirit",
            state.PlayerId,
            new DaoxuanMarketInstrument(DaoxuanItemIds.SpiritHerb),
            DaoxuanMarketSide.Buy,
            10,
            1);

        var result = coordinator.Place(state, request);

        Assert.False(result.Success);
        Assert.Equal("INSUFFICIENT_SPIRIT", result.Code);
        Assert.Equal(0, engine.GetMetrics().ActiveOrderCount);
    }

    /// <summary>
    /// 验证灵市账本导出快照并重新加载后，活跃挂单的资产质押与未领取的成交结算款项能无损恢复。
    /// </summary>
    [Fact]
    public void Ledger_snapshot_restores_reservations_and_pending_settlement()
    {
        var engine = new DaoxuanMarketEngine(shardCount: 4);
        var coordinator = new DaoxuanMarketCoordinator(engine);
        var seller = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid());
        var buyer = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid());
        seller.Inventory.Add(DaoxuanItemIds.SpiritHerb, 5);
        buyer.Inventory.Add(DaoxuanItemIds.Spirit, 100);
        var instrument = new DaoxuanMarketInstrument(DaoxuanItemIds.SpiritHerb);

        Assert.True(coordinator.Place(seller, new DaoxuanMarketOrderRequest(
            "ledger-sell", seller.PlayerId, instrument, DaoxuanMarketSide.Sell, 10, 5)).Success);
        Assert.True(coordinator.Place(buyer, new DaoxuanMarketOrderRequest(
            "ledger-buy", buyer.PlayerId, instrument, DaoxuanMarketSide.Buy, 12, 3)).Success);

        var restoredEngine = new DaoxuanMarketEngine(shardCount: 4);
        restoredEngine.RestoreState(engine.ExportState());
        var restoredCoordinator = new DaoxuanMarketCoordinator(restoredEngine);
        restoredCoordinator.RestoreState(coordinator.ExportState());

        Assert.Equal(1, restoredCoordinator.ActiveReservationCount);
        Assert.True(restoredCoordinator.Claim(seller));
        Assert.Equal(29, seller.Inventory.Get(DaoxuanItemIds.Spirit));
        Assert.Equal(0, seller.Inventory.Get(DaoxuanItemIds.SpiritHerb));
    }

    /// <summary>
    /// 验证过期订单被批量回收后，卖方未售出的质押物品与买方未成交的质押灵石均可由玩家通过 Claim 安全赎回。
    /// </summary>
    [Fact]
    public void Expired_orders_release_seller_items_and_buyer_spirit_to_pending_claims()
    {
        var now = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var createdAt = now - DaoxuanMarketPricePolicy.ListingLifetime;
        var engine = new DaoxuanMarketEngine(shardCount: 4);
        var coordinator = new DaoxuanMarketCoordinator(engine);
        var seller = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid());
        var buyer = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid());
        seller.Inventory.Add(DaoxuanItemIds.SpiritHerb, 5);
        buyer.Inventory.Add(DaoxuanItemIds.Spirit, 100);
        var sellerHerbsBefore = seller.Inventory.Get(DaoxuanItemIds.SpiritHerb);
        var buyerSpiritBefore = buyer.Inventory.Get(DaoxuanItemIds.Spirit);
        var instrument = new DaoxuanMarketInstrument(DaoxuanItemIds.SpiritHerb);

        Assert.True(coordinator.Place(seller, new DaoxuanMarketOrderRequest(
            "expiring-sell",
            seller.PlayerId,
            instrument,
            DaoxuanMarketSide.Sell,
            12,
            5,
            createdAt)).Success);
        Assert.True(coordinator.Place(buyer, new DaoxuanMarketOrderRequest(
            "expiring-buy",
            buyer.PlayerId,
            instrument,
            DaoxuanMarketSide.Buy,
            10,
            3,
            createdAt)).Success);
        Assert.Equal(2, coordinator.ActiveReservationCount);

        Assert.Equal(2, coordinator.Expire(now));
        Assert.Equal(0, coordinator.ActiveReservationCount);
        Assert.True(coordinator.Claim(seller));
        Assert.True(coordinator.Claim(buyer));
        Assert.Equal(sellerHerbsBefore, seller.Inventory.Get(DaoxuanItemIds.SpiritHerb));
        Assert.Equal(buyerSpiritBefore, buyer.Inventory.Get(DaoxuanItemIds.Spirit));
        Assert.False(coordinator.Claim(seller));
        Assert.False(coordinator.Claim(buyer));
    }
}
