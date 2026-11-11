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

        var game = new DouDiZhuGameDto("game", ["me", "left", "right"], 10, "bidding", 0,
            [1, 4], [17, 17, 17], [], null, 0, 0, [], null, 0, [], 2, null, null, false, 0,
            [0, 0, 0], 39000, [false, false, false], DealNumber: 1, DealStartedAtUnixMilliseconds: 0,
            ActionAvailableAtUnixMilliseconds: 9000, DealHand: [4, 1], Bids: [new(1, "pass", 1)],
            ServerNowUnixMilliseconds: 9100);
        var party = new PartyDto("party", "me", "牌桌", "any", "", 0, true, false, [], 3, DouDiZhuGame: game);
        var state = new SocialStateDto(null, [], [], [], party, null, [],
            DouDiZhuInteractions: [new("event", "game", "me", "left", "tomato", 1000)]);
        var envelope = WireEnvelope.Json(WireMessageType.SocialState, Guid.NewGuid(), 2, state);
        var decoded = WireEnvelopeCodec.Decode(WireEnvelopeCodec.Encode(envelope));
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<SocialStateDto>(decoded, out var parsed));
        Assert.Equal([4, 1], parsed!.Party!.DouDiZhuGame!.DealHand!);
        Assert.Equal(9000, parsed.Party.DouDiZhuGame.ActionAvailableAtUnixMilliseconds);
        Assert.Equal(9100, parsed.Party.DouDiZhuGame.ServerNowUnixMilliseconds);
        Assert.Empty(parsed.Party.DouDiZhuGame.BottomCards);
        Assert.Equal("pass", Assert.Single(parsed.Party.DouDiZhuGame.Bids!).Action);
        Assert.Equal("target", parsedAction.TargetPlayerId);
        Assert.Equal("tomato", Assert.Single(parsed.DouDiZhuInteractions!).Kind);
    }
}
