using Protocol;
using System.Text.Json;
using Xunit;

namespace Server.Tests;

public sealed class WireEnvelopeTests
{
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
    public void BinaryEnvelope_RejectsTamperingAndOversizeFrames()
    {
        var envelope = WireEnvelope.Json(WireMessageType.Ping, Guid.NewGuid(), 1, new { ok = true });
        var bytes = WireEnvelopeCodec.Encode(envelope);
        bytes[0] ^= 0xff;
        Assert.False(WireEnvelopeCodec.TryDecode(bytes, out _));
        Assert.Throws<ProtocolException>(() => WireEnvelope.Create(WireMessageType.Ping, Guid.NewGuid(), 0, new byte[ProtocolConstants.MaxFrameBytes + 1], WireCodec.Json));
    }
}
