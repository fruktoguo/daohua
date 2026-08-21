using Daoxuan.Domain.Market;
using Daoxuan.Domain.Models;
using Daoxuan.Domain.Simulation;
using Xunit;

namespace Daoxuan.Domain.Tests;

public sealed class DaoxuanMarketCoordinatorTests
{
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
