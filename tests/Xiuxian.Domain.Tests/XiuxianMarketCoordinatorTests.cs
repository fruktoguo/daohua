using Xiuxian.Domain.Market;
using Xiuxian.Domain.Models;
using Xiuxian.Domain.Simulation;
using Xunit;

namespace Xiuxian.Domain.Tests;

public sealed class XiuxianMarketCoordinatorTests
{
    [Fact]
    public void Sell_and_buy_orders_freeze_and_settle_assets_with_partial_fill()
    {
        var engine = new XiuxianMarketEngine(shardCount: 4);
        var coordinator = new XiuxianMarketCoordinator(engine);
        var seller = XiuxianSimulation.CreateDefaultState(Guid.NewGuid());
        var buyer = XiuxianSimulation.CreateDefaultState(Guid.NewGuid());
        seller.Inventory.Add(XiuxianItemIds.SpiritHerb, 5);
        buyer.Inventory.Add(XiuxianItemIds.Spirit, 100);

        var sell = new XiuxianMarketOrderRequest(
            "coordinator-sell",
            seller.PlayerId,
            new XiuxianMarketInstrument(XiuxianItemIds.SpiritHerb),
            XiuxianMarketSide.Sell,
            10,
            5);
        Assert.True(coordinator.Place(seller, sell).Success);
        Assert.Equal(0, seller.Inventory.Get(XiuxianItemIds.SpiritHerb));

        var buy = new XiuxianMarketOrderRequest(
            "coordinator-buy",
            buyer.PlayerId,
            sell.Instrument,
            XiuxianMarketSide.Buy,
            12,
            3);
        var placement = coordinator.Place(buyer, buy);
        Assert.True(placement.Success);
        Assert.Equal(70, buyer.Inventory.Get(XiuxianItemIds.Spirit));
        Assert.Equal(3, buyer.Inventory.Get(XiuxianItemIds.SpiritHerb));
        coordinator.Claim(seller);
        Assert.Equal(29, seller.Inventory.Get(XiuxianItemIds.Spirit));
        Assert.Equal(1, coordinator.ActiveReservationCount);

        Assert.True(coordinator.Cancel(seller, sell.OrderId).Success);
        Assert.Equal(2, seller.Inventory.Get(XiuxianItemIds.SpiritHerb));
        Assert.Equal(0, coordinator.ActiveReservationCount);
    }

    [Fact]
    public void Insufficient_assets_do_not_create_market_orders()
    {
        var engine = new XiuxianMarketEngine();
        var coordinator = new XiuxianMarketCoordinator(engine);
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid());
        var request = new XiuxianMarketOrderRequest(
            "no-spirit",
            state.PlayerId,
            new XiuxianMarketInstrument(XiuxianItemIds.SpiritHerb),
            XiuxianMarketSide.Buy,
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
        var engine = new XiuxianMarketEngine(shardCount: 4);
        var coordinator = new XiuxianMarketCoordinator(engine);
        var seller = XiuxianSimulation.CreateDefaultState(Guid.NewGuid());
        var buyer = XiuxianSimulation.CreateDefaultState(Guid.NewGuid());
        seller.Inventory.Add(XiuxianItemIds.SpiritHerb, 5);
        buyer.Inventory.Add(XiuxianItemIds.Spirit, 100);
        var instrument = new XiuxianMarketInstrument(XiuxianItemIds.SpiritHerb);

        Assert.True(coordinator.Place(seller, new XiuxianMarketOrderRequest(
            "ledger-sell", seller.PlayerId, instrument, XiuxianMarketSide.Sell, 10, 5)).Success);
        Assert.True(coordinator.Place(buyer, new XiuxianMarketOrderRequest(
            "ledger-buy", buyer.PlayerId, instrument, XiuxianMarketSide.Buy, 12, 3)).Success);

        var restoredEngine = new XiuxianMarketEngine(shardCount: 4);
        restoredEngine.RestoreState(engine.ExportState());
        var restoredCoordinator = new XiuxianMarketCoordinator(restoredEngine);
        restoredCoordinator.RestoreState(coordinator.ExportState());

        Assert.Equal(1, restoredCoordinator.ActiveReservationCount);
        Assert.True(restoredCoordinator.Claim(seller));
        Assert.Equal(29, seller.Inventory.Get(XiuxianItemIds.Spirit));
        Assert.Equal(0, seller.Inventory.Get(XiuxianItemIds.SpiritHerb));
    }

    [Fact]
    public void Expired_orders_release_seller_items_and_buyer_spirit_to_pending_claims()
    {
        var now = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var createdAt = now - XiuxianMarketPricePolicy.ListingLifetime;
        var engine = new XiuxianMarketEngine(shardCount: 4);
        var coordinator = new XiuxianMarketCoordinator(engine);
        var seller = XiuxianSimulation.CreateDefaultState(Guid.NewGuid());
        var buyer = XiuxianSimulation.CreateDefaultState(Guid.NewGuid());
        seller.Inventory.Add(XiuxianItemIds.SpiritHerb, 5);
        buyer.Inventory.Add(XiuxianItemIds.Spirit, 100);
        var sellerHerbsBefore = seller.Inventory.Get(XiuxianItemIds.SpiritHerb);
        var buyerSpiritBefore = buyer.Inventory.Get(XiuxianItemIds.Spirit);
        var instrument = new XiuxianMarketInstrument(XiuxianItemIds.SpiritHerb);

        Assert.True(coordinator.Place(seller, new XiuxianMarketOrderRequest(
            "expiring-sell",
            seller.PlayerId,
            instrument,
            XiuxianMarketSide.Sell,
            12,
            5,
            createdAt)).Success);
        Assert.True(coordinator.Place(buyer, new XiuxianMarketOrderRequest(
            "expiring-buy",
            buyer.PlayerId,
            instrument,
            XiuxianMarketSide.Buy,
            10,
            3,
            createdAt)).Success);
        Assert.Equal(2, coordinator.ActiveReservationCount);

        Assert.Equal(2, coordinator.Expire(now));
        Assert.Equal(0, coordinator.ActiveReservationCount);
        Assert.True(coordinator.Claim(seller));
        Assert.True(coordinator.Claim(buyer));
        Assert.Equal(sellerHerbsBefore, seller.Inventory.Get(XiuxianItemIds.SpiritHerb));
        Assert.Equal(buyerSpiritBefore, buyer.Inventory.Get(XiuxianItemIds.Spirit));
        Assert.False(coordinator.Claim(seller));
        Assert.False(coordinator.Claim(buyer));
    }
}
