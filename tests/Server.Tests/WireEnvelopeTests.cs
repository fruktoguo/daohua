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
