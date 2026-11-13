using System.Text.Json;
using Domain.Social;

namespace Domain.Tests;

public sealed class DouDiZhuGameTests
{
    private static readonly Guid[] Players = [Guid.Parse("00000000-0000-0000-0000-000000000001"),
        Guid.Parse("00000000-0000-0000-0000-000000000002"), Guid.Parse("00000000-0000-0000-0000-000000000003")];
    private static readonly int[] Deck = Enumerable.Range(0, 54).ToArray();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T00:00:00Z");

    [Fact]
    public void Create_DealsThreeHandsAndPersistsBottomCards()
    {
        var game = NewGame(firstBidder: 1);

        Assert.All(game.Hands, hand => Assert.Equal(17, hand.Length));
        Assert.Equal(3, game.BottomCards.Length);
        Assert.Equal(54, game.Hands.SelectMany(hand => hand).Concat(game.BottomCards).Distinct().Count());
        Assert.Equal(1, game.CurrentSeat);
        Assert.Equal(DouDiZhuGamePhase.Bidding, game.Phase);

        var restored = JsonSerializer.Deserialize<DouDiZhuGameState>(JsonSerializer.Serialize(game))!;
        Assert.Equal(game.Deck, restored.Deck);
        Assert.Equal(game.Hands.SelectMany(hand => hand), restored.Hands.SelectMany(hand => hand));
        Assert.Equal(game.BottomCards, restored.BottomCards);
    }

    [Fact]
    public void ApplyBid_ResolvesLandlordAndDoublesRobMultiplier()
    {
        var game = NewGame(firstBidder: 0);
        game = DouDiZhuGame.ApplyBid(game, Players[0], 0, DouDiZhuBidAction.Call, game.ActionAvailableAt);
        game = DouDiZhuGame.ApplyBid(game, Players[1], 1, DouDiZhuBidAction.Rob, game.ActionAvailableAt);
        game = DouDiZhuGame.ApplyBid(game, Players[2], 2, DouDiZhuBidAction.Pass, game.ActionAvailableAt);
        Assert.Equal(DouDiZhuGamePhase.Bidding, game.Phase);
        Assert.Equal(0, game.CurrentSeat);
        game = DouDiZhuGame.ApplyBid(game, Players[0], game.Version, DouDiZhuBidAction.Pass, game.ActionAvailableAt);

        Assert.Equal(DouDiZhuGamePhase.Playing, game.Phase);
        Assert.Equal(1, game.LandlordSeat);
        Assert.Equal(20, game.Hands[1].Length);
        Assert.Equal(34, game.Hands[0].Length + game.Hands[2].Length);
        Assert.Equal(1, game.RobCount);
        Assert.Equal(1, game.CurrentSeat);
    }

    [Fact]
    public void ApplyBid_GivesEarlierPasserAChanceToRobAfterSomebodyCalls()
    {
        var game = NewGame(firstBidder: 0);
        game = DouDiZhuGame.ApplyBid(game, Players[0], game.Version, DouDiZhuBidAction.Pass, game.ActionAvailableAt);
        game = DouDiZhuGame.ApplyBid(game, Players[1], game.Version, DouDiZhuBidAction.Call, game.ActionAvailableAt);
        Assert.Equal(2, game.BidsRemaining);
        game = DouDiZhuGame.ApplyBid(game, Players[2], game.Version, DouDiZhuBidAction.Pass, game.ActionAvailableAt);
        game = DouDiZhuGame.ApplyBid(game, Players[0], game.Version, DouDiZhuBidAction.Rob, game.ActionAvailableAt);
        Assert.Equal(1, game.CurrentSeat);
        game = DouDiZhuGame.ApplyBid(game, Players[1], game.Version, DouDiZhuBidAction.Pass, game.ActionAvailableAt);

        Assert.Equal(DouDiZhuGamePhase.Playing, game.Phase);
        Assert.Equal(0, game.LandlordSeat);
        Assert.Equal(1, game.RobCount);
    }

    [Fact]
    public void ApplyBid_RedealsAfterEveryonePassesAndRejectsStaleVersion()
    {
        var game = NewGame(firstBidder: 0);
        game = DouDiZhuGame.ApplyBid(game, Players[0], 0, DouDiZhuBidAction.Pass, game.ActionAvailableAt);
        game = DouDiZhuGame.ApplyBid(game, Players[1], 1, DouDiZhuBidAction.Pass, game.ActionAvailableAt);
        var redealt = DouDiZhuGame.ApplyBid(game, Players[2], 2, DouDiZhuBidAction.Pass, game.ActionAvailableAt);

        Assert.Equal(1, redealt.DealNumber);
        Assert.Equal(3, redealt.BidsRemaining);
        Assert.Equal(51, redealt.Hands.Sum(hand => hand.Length));
        Assert.Throws<DomainException>(() => DouDiZhuGame.ApplyBid(redealt, Players[0], 0,
            DouDiZhuBidAction.Call, game.ActionAvailableAt));
    }

    [Fact]
    public void ApplyPlay_RejectsIllegalCardsWithoutChangingStateAndPassResetsLead()
    {
        var game = StartPlay(firstBidder: 0, landlordSeat: 0);
        var invalidCards = new[] { game.Hands[0][0], game.Hands[0][2], game.Hands[0][4], game.Hands[0][6], game.Hands[0][8] };
        Assert.Throws<DomainException>(() => DouDiZhuGame.ApplyPlay(game, Players[0], game.Version, invalidCards, game.ActionAvailableAt));
        Assert.Equal(20, game.Hands[0].Length);

        var lead = game.Hands[0].Take(1).ToArray();
        game = DouDiZhuGame.ApplyPlay(game, Players[0], game.Version, lead, game.ActionAvailableAt);
        game = DouDiZhuGame.ApplyPass(game, Players[1], game.Version, game.ActionAvailableAt);
        game = DouDiZhuGame.ApplyPass(game, Players[2], game.Version, game.ActionAvailableAt);

        Assert.Null(game.LastPattern);
        Assert.Equal(0, game.ConsecutivePasses);
        Assert.Equal(0, game.CurrentSeat);
    }

    [Fact]
    public void ApplyPlay_RejectsActorAndVersionOutsideTurnBoundary()
    {
        var game = StartPlay(firstBidder: 0, landlordSeat: 0);
        Assert.Throws<DomainException>(() => DouDiZhuGame.ApplyPlay(game, Players[1], game.Version,
            [game.Hands[1][0]], Now));
        Assert.Throws<DomainException>(() => DouDiZhuGame.ApplyPlay(game, Players[0], game.Version - 1,
            [game.Hands[0][0]], Now));
    }

    [Fact]
    public void SetHosted_AnotherPlayersHostWaitsForTheirTurn()
    {
        var game = NewGame(firstBidder: 0);
        game = DouDiZhuGame.SetHosted(game, Players[1], game.Version, true, Now);
        Assert.Equal(new[] { false, true, false }, game.HostedSeats);

        game = DouDiZhuGame.ApplyBid(game, Players[0], game.Version, DouDiZhuBidAction.Call, game.ActionAvailableAt);
        game = DouDiZhuGame.ApplyHostedTurn(game, game.Version, game.ActionAvailableAt + DouDiZhuGame.HostedDelay);

        Assert.Equal(DouDiZhuGamePhase.Bidding, game.Phase);
        Assert.Equal(0, game.LandlordSeat);
        Assert.Equal(2, game.CurrentSeat);
        Assert.Equal(3, game.Version);
    }

    [Fact]
    public void ApplyTimeout_RejectsEarlyTimeoutAndHostsWithImmediateLegalActionAtDeadline()
    {
        var game = NewGame(firstBidder: 2);
        Assert.Throws<DomainException>(() => DouDiZhuGame.ApplyTimeout(game, game.Version,
            game.TurnDeadlineAt.AddTicks(-1)));

        var timeoutAt = game.TurnDeadlineAt;
        game = DouDiZhuGame.ApplyTimeout(game, game.Version, timeoutAt);

        Assert.True(game.HostedSeats[2]);
        Assert.Equal(2, game.LandlordSeat);
        Assert.Equal(0, game.CurrentSeat);
        Assert.Equal(2, game.Version);
        Assert.Equal(game.ActionAvailableAt + DouDiZhuGame.TurnTimeout, game.TurnDeadlineAt);
    }

    [Fact]
    public void ThreeHostedSeats_CompleteAWholeGameUsingOnlyLegalActions()
    {
        var game = NewGame(firstBidder: 0) with { HostedSeats = [true, true, true] };
        var actionCount = 0;
        while (game.Phase is DouDiZhuGamePhase.Bidding or DouDiZhuGamePhase.Playing)
        {
            game = DouDiZhuGame.ApplyHostedTurn(game, game.Version, game.ActionAvailableAt + DouDiZhuGame.HostedDelay);
            actionCount++;
            Assert.True(actionCount < 200, "三名托管座位应在有限步数内结束牌局。");
        }

        Assert.Equal(DouDiZhuGamePhase.Finished, game.Phase);
        Assert.NotNull(game.WinnerSeat);
        Assert.True(game.History.Length > 0);
        Assert.Contains(game.History, entry => entry.Cards.Length > 0);
        Assert.All(game.HostedSeats, isHosted => Assert.True(isHosted));
        Assert.Equal(0, DouDiZhuGame.CalculateSettlementDeltas(game, [1_000_000, 1_000_000, 1_000_000]).Sum());
    }

    [Fact]
    public void ApplyPlay_FinishesAfterTripleRobWithoutMultiplierOverflow()
    {
        var game = NewGame(firstBidder: 0);
        game = DouDiZhuGame.ApplyBid(game, Players[0], game.Version, DouDiZhuBidAction.Pass, game.ActionAvailableAt);
        game = DouDiZhuGame.ApplyBid(game, Players[1], game.Version, DouDiZhuBidAction.Call, game.ActionAvailableAt);
        game = DouDiZhuGame.ApplyBid(game, Players[2], game.Version, DouDiZhuBidAction.Rob, game.ActionAvailableAt);
        game = DouDiZhuGame.ApplyBid(game, Players[0], game.Version, DouDiZhuBidAction.Rob, game.ActionAvailableAt);
        game = DouDiZhuGame.ApplyBid(game, Players[1], game.Version, DouDiZhuBidAction.Rob, game.ActionAvailableAt);

        Assert.Equal(DouDiZhuGamePhase.Playing, game.Phase);
        Assert.Equal(1, game.LandlordSeat);
        Assert.Equal(3, game.RobCount);

        var lastCard = game.Hands[1][0];
        game = game with
        {
            Hands = [game.Hands[0], [lastCard], game.Hands[2]],
            FarmerPlayCount = 1
        };
        game = DouDiZhuGame.ApplyPlay(game, Players[1], game.Version, [lastCard], game.ActionAvailableAt);

        Assert.Equal(DouDiZhuGamePhase.Finished, game.Phase);
        Assert.Equal(1, game.WinnerSeat);
        Assert.True(game.LandlordWon);
        Assert.False(game.Spring);
        Assert.Equal(8, game.FinalMultiplier);
    }

    [Fact]
    public void CalculateSettlementDeltas_TransfersOnlyAvailableStonesAndRemainsZeroSum()
    {
        var landlordGame = FinishedGame(landlordWon: true);
        var landlordWin = DouDiZhuGame.CalculateSettlementDeltas(landlordGame, [100, 5, 50]);
        Assert.Equal(new[] { 25d, -5d, -20d }, landlordWin);
        Assert.Equal(0, landlordWin.Sum());

        var farmerGame = FinishedGame(landlordWon: false);
        var farmerWin = DouDiZhuGame.CalculateSettlementDeltas(farmerGame, [11, 100, 100]);
        Assert.Equal(new[] { -11d, 5.5d, 5.5d }, farmerWin);
        Assert.Equal(0, farmerWin.Sum());
    }

    private static DouDiZhuGameState NewGame(int firstBidder) => DouDiZhuGame.CreateWithDeck(
        Guid.Parse("10000000-0000-0000-0000-000000000001"), Guid.Parse("20000000-0000-0000-0000-000000000001"),
        Players, 10, Deck, firstBidder, Now);

    private static DouDiZhuGameState FinishedGame(bool landlordWon) => NewGame(0) with
    {
        Phase = DouDiZhuGamePhase.Finished,
        LandlordSeat = 0,
        LandlordWon = landlordWon,
        FinalMultiplier = 2
    };

    private static DouDiZhuGameState StartPlay(int firstBidder, int landlordSeat)
    {
        var game = NewGame(firstBidder);
        var seat = firstBidder;
        game = DouDiZhuGame.ApplyBid(game, Players[seat], game.Version,
            seat == landlordSeat ? DouDiZhuBidAction.Call : DouDiZhuBidAction.Pass, game.ActionAvailableAt);
        seat = (seat + 1) % 3;
        game = DouDiZhuGame.ApplyBid(game, Players[seat], game.Version,
            seat == landlordSeat ? DouDiZhuBidAction.Call : DouDiZhuBidAction.Pass, game.ActionAvailableAt);
        seat = (seat + 1) % 3;
        game = DouDiZhuGame.ApplyBid(game, Players[seat], game.Version,
            seat == landlordSeat ? DouDiZhuBidAction.Call : DouDiZhuBidAction.Pass, game.ActionAvailableAt);
        return game;
    }
}
