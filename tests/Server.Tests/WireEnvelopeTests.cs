using Protocol;
using System.Text.Json;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 二进制信封门禁：JSON 载荷编解码往返一致、篡改帧安全拒绝、超 MaxFrameBytes 载荷构造即抛。
/// </summary>
public sealed class WireEnvelopeTests
{
    /// <summary>Command 信封经二进制编码往返后类型码、请求 Id、序号与载荷字段全部一致。</summary>
    [Fact]
    public void BinaryEnvelope_RoundTripsJsonPayload()
    {
        var requestId = Guid.NewGuid();
        var envelope = WireEnvelope.Json(WireMessageType.Command, requestId, 7, new CommandEnvelopeDto(requestId.ToString("D"), "player.presence", JsonSerializer.SerializeToElement(new { presence = "online" })));
        var bytes = WireEnvelopeCodec.Encode(envelope);
        Assert.True(WireEnvelopeCodec.TryDecode(bytes, out var decoded));
        Assert.Equal(WireMessageType.Command, decoded.MessageType);
        Assert.Equal(requestId, decoded.RequestId);
        Assert.Equal(7, decoded.Sequence);
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<CommandEnvelopeDto>(decoded, out var command));
        Assert.NotNull(command);
        Assert.Equal("player.presence", command!.Command);
    }

    /// <summary>角色卡沿用原信封，可用钱包与不可售储备分别往返，不合并数量。</summary>
    [Fact]
    public void PlayerCardEnvelope_RoundTripsAvailableWalletAndOfflineReserveSeparately()
    {
        var requestId = Guid.NewGuid();
        var card = new PlayerCardDto(requestId.ToString("D"), Guid.NewGuid().ToString("D"),
            AutoUseSlots: [new(0, "item.qi-gathering-pill", 3, true, "potion", 7)]);
        var bytes = WireEnvelopeCodec.Encode(WireEnvelope.Json(WireMessageType.PlayerCard, requestId, 8, card));
        Assert.True(WireEnvelopeCodec.TryDecode(bytes, out var decoded));
        Assert.Equal(WireMessageType.PlayerCard, decoded.MessageType);
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<PlayerCardDto>(decoded, out var restored));
        var slot = Assert.Single(restored!.AutoUseSlots!);
        Assert.Equal(3, slot.Quantity);
        Assert.Equal(7, slot.OfflineReserveQuantity);
        using var json = JsonDocument.Parse(decoded.Payload);
        var wireSlot = json.RootElement.GetProperty("autoUseSlots")[0];
        Assert.Equal(3, wireSlot.GetProperty("quantity").GetDouble());
        Assert.Equal(7, wireSlot.GetProperty("offlineReserveQuantity").GetDouble());
    }

    [Fact]
    public void PlayerCardEnvelope_RoundTripsCurrentReport()
    {
        var requestId = Guid.NewGuid();
        var card = new PlayerCardDto(
            requestId.ToString("D"),
            Guid.NewGuid().ToString("D"),
            CurrentReport: new
            {
                reportId = "test-report-id",
                actionId = "combat.hunt",
                experience = 1200.0,
                combatExperience = 600.0,
                encounters = 10,
                items = new[] { new { itemId = "item.ningxue-pill", quantity = 5 } }
            });
        var bytes = WireEnvelopeCodec.Encode(WireEnvelope.Json(WireMessageType.PlayerCard, requestId, 9, card));
        Assert.True(WireEnvelopeCodec.TryDecode(bytes, out var decoded));
        Assert.Equal(WireMessageType.PlayerCard, decoded.MessageType);
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<PlayerCardDto>(decoded, out var restored));
        Assert.NotNull(restored!.CurrentReport);
        using var json = JsonDocument.Parse(decoded.Payload);
        var reportElem = json.RootElement.GetProperty("currentReport");
        Assert.Equal("test-report-id", reportElem.GetProperty("reportId").GetString());
        Assert.Equal(1200.0, reportElem.GetProperty("experience").GetDouble());
    }
    /// <summary>篡改帧解码返回 false；超 MaxFrameBytes 的载荷在构造信封时即抛 ProtocolException。</summary>

    [Fact]
    public void BinaryEnvelope_RejectsTamperingAndOversizeFrames()
    {
        var envelope = WireEnvelope.Json(WireMessageType.Ping, Guid.NewGuid(), 1, new { ok = true });
        var bytes = WireEnvelopeCodec.Encode(envelope);
        bytes[0] ^= 0xff;
        Assert.False(WireEnvelopeCodec.TryDecode(bytes, out _));
        Assert.Throws<ProtocolException>(() => WireEnvelope.Create(WireMessageType.Ping, Guid.NewGuid(), 0, new byte[ProtocolConstants.MaxFrameBytes + 1], WireCodec.Json));
    }
}
