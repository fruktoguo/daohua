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

    [Fact]
    public void BorrowedEnvelope_UsesOnlyTheValidBufferSlice()
    {
        var packet = WireEnvelopeCodec.EncodeJson(WireMessageType.Ping, Guid.NewGuid(), 1, new { });
        var buffer = new byte[packet.Length + 20];
        packet.CopyTo(buffer, 10);
        var validSlice = buffer.AsMemory(10, packet.Length);
        Assert.True(WireEnvelopeCodec.TryDecodeBorrowed(validSlice, out var borrowed));
        Assert.Equal("{}"u8.ToArray(), borrowed.Payload.ToArray());
        buffer[10 + 36] = (byte)'[';
        Assert.Equal((byte)'[', borrowed.Payload.Span[0]);
        Assert.False(WireEnvelopeCodec.TryDecodeBorrowed(buffer, out _));
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
    public void ChatInteractions_RoundTripQuoteAndVersionedLikeState()
    {
        var requestId = Guid.NewGuid();
        var id = Guid.NewGuid().ToString("D");
        var sourceId = Guid.NewGuid().ToString("D");
        var actor = Guid.NewGuid().ToString("D");
        var target = Guid.NewGuid().ToString("D");
        var send = new ChatSendDto(requestId.ToString("D"), ChatChannels.Friend, "回复", target, sourceId);
        Assert.Equal(send, RoundTrip(WireMessageType.ChatSend, send));

        var message = new ChatMessageDto(id, ChatChannels.Friend, "pair", actor, "回帖者", "回复", 123L,
            Quote: new(sourceId, "原发言者", "{{item:item.qi-gathering-pill|聚气丹}}"), LikeCount: 2, LikedByMe: true, LikeVersion: 4);
        var history = RoundTrip(WireMessageType.ChatHistory, new ChatHistoryDto(ChatChannels.Friend, "pair", target, [message]));
        Assert.Equal(message, Assert.Single(history.Messages));
        Assert.Equal(message, RoundTrip(WireMessageType.ChatMessage, message));

        Assert.Equal(31, (byte)WireMessageType.ChatLikeSet);
        var set = new ChatLikeSetDto(ChatChannels.Friend, id, false, target);
        Assert.Equal(set, RoundTrip(WireMessageType.ChatLikeSet, set));
        var missingLiked = WireEnvelope.Json(WireMessageType.ChatLikeSet, requestId, 1, new { channel = ChatChannels.World, messageId = id });
        Assert.False(WireEnvelopeCodec.TryDeserializeJson<ChatLikeSetDto>(missingLiked, out _));

        Assert.Equal(32, (byte)WireMessageType.ChatLikeChanged);
        var update = new ChatLikeChangedDto(ChatChannels.Friend, "pair", id, actor, false, 1, 5);
        Assert.Equal(update, RoundTrip(WireMessageType.ChatLikeChanged, update));
        var wire = WireEnvelope.Json(WireMessageType.ChatLikeChanged, requestId, 1, update);
        using var json = JsonDocument.Parse(wire.Payload);
        Assert.Equal(actor, json.RootElement.GetProperty("actorId").GetString());
        Assert.Equal(5, json.RootElement.GetProperty("likeVersion").GetInt64());
        Assert.Equal(1, json.RootElement.GetProperty("likeCount").GetDouble());

        T RoundTrip<T>(WireMessageType type, T payload)
        {
            var bytes = WireEnvelopeCodec.EncodeJson(type, requestId, 1, payload);
            var restored = WireEnvelopeCodec.Decode(bytes);
            Assert.Equal(type, restored.MessageType);
            Assert.Equal(requestId, restored.RequestId);
            Assert.True(WireEnvelopeCodec.TryDeserializeJson<T>(restored, out var result));
            return result!;
        }
    }

    [Fact]
    public void BinaryEnvelope_RejectsTamperingAndOversizeFrames()
    {
        var envelope = WireEnvelope.Json(WireMessageType.Ping, Guid.NewGuid(), 1, new { ok = true });
        var bytes = WireEnvelopeCodec.Encode(envelope);
        bytes[0] ^= 0xff;
        Assert.False(WireEnvelopeCodec.TryDecode(bytes, out _));
        Assert.Throws<ProtocolException>(() => WireEnvelope.Create(WireMessageType.Ping, Guid.NewGuid(), 0, new byte[ProtocolConstants.MaxFrameBytes + 1], WireCodec.Json));
    }

    [Fact]
    public void LogBatchEnvelope_RoundTripsStreamIdentityAndFoldedRange()
    {
        var streamId = Guid.NewGuid().ToString("D");
        var log = new LogEventDto(2, 1234, "完成行动", 2, LogChannels.System, streamId);
        var packet = WireEnvelopeCodec.EncodeJson(WireMessageType.LogBatch, Guid.NewGuid(), 1, new LogBatchDto([log]));
        var envelope = WireEnvelopeCodec.Decode(packet);

        Assert.Equal(WireMessageType.LogBatch, envelope.MessageType);
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<LogBatchDto>(envelope, out var batch));
        Assert.Equal(log, Assert.Single(batch!.Events));
        using var payload = JsonDocument.Parse(envelope.Payload);
        Assert.Equal(streamId, payload.RootElement.GetProperty("events")[0].GetProperty("streamId").GetString());
    }
}
