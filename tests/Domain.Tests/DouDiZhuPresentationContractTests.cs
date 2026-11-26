namespace Domain.Tests;

/// <summary>保护正式牌桌使用的时刻、私有发牌顺序与短暂互动的信封契约。</summary>
public sealed class DouDiZhuPresentationContractTests
{
    [Fact]
    public void SocialPresentationAndInteractionRoundTrip()
    {
        var action = new SocialActionDto("req", SocialActions.DouDiZhuInteract,
            TargetPlayerId: "target", GameId: "game", Interaction: "tomato");
        var actionEnvelope = WireEnvelope.Json(WireMessageType.SocialAction, Guid.NewGuid(), 1, action);
        var decodedAction = WireEnvelopeCodec.Decode(WireEnvelopeCodec.Encode(actionEnvelope));
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<SocialActionDto>(decodedAction, out var parsedAction));
        Assert.Equal("tomato", parsedAction!.Interaction);
        Assert.Null(parsedAction.GameVersion);

        var seats = new DouDiZhuSeatDto[]
        {
            new("me", "本尊", "male_01", 1000),
            new("left", "左邻", "female_01", 2000),
            new("right", "右舍", "male_02", 3000),
        };
        var game = new DouDiZhuGameDto("game", 2, ["me", "left", "right"], 100, "bidding", 0,
            [1, 4], [17, 17, 17], [], null, 0, 0, [], null, 0, [], 2, null, null, false, 0,
            [0, 0, 0], 39000, [false, false, false], DealNumber: 1, DealStartedAtUnixMilliseconds: 0,
            ActionAvailableAtUnixMilliseconds: 9000, DealHand: [4, 1], Bids: [new(1, "pass", 1)],
            ServerNowUnixMilliseconds: 9100, Seats: seats);
        var state = new SocialStateDto(null, [], [], [], null, null, [],
            DouDiZhuInteractions: [new("event", "game", "me", "left", "tomato", 1000)],
            DouDiZhuGame: game,
            DouDiZhuMatchingTier: null,
            DouDiZhuQueueCounts: [1, 2, 0, 0, 0, 0, 0, 0]);
        var envelope = WireEnvelope.Json(WireMessageType.SocialState, Guid.NewGuid(), 2, state);
        var decoded = WireEnvelopeCodec.Decode(WireEnvelopeCodec.Encode(envelope));
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<SocialStateDto>(decoded, out var parsed));
        Assert.Null(parsed!.Party);
        Assert.Equal(2, parsed.DouDiZhuGame!.Tier);
        Assert.Equal(3, parsed.DouDiZhuGame.Seats!.Count);
        Assert.Equal([4, 1], parsed.DouDiZhuGame.DealHand!);
        Assert.Equal(9000, parsed.DouDiZhuGame.ActionAvailableAtUnixMilliseconds);
        Assert.Equal(9100, parsed.DouDiZhuGame.ServerNowUnixMilliseconds);
        Assert.Empty(parsed.DouDiZhuGame.BottomCards);
        Assert.Equal("pass", Assert.Single(parsed.DouDiZhuGame.Bids!).Action);
        Assert.Equal([1, 2, 0, 0, 0, 0, 0, 0], parsed.DouDiZhuQueueCounts!);
        Assert.Equal("target", parsedAction.TargetPlayerId);
        Assert.Equal("tomato", Assert.Single(parsed.DouDiZhuInteractions!).Kind);
    }
}
