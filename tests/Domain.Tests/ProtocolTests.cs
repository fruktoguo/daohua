using System.Text.Json;

namespace Domain.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void BinaryEnvelopeRoundTripsWithoutMutation()
    {
        var requestId = Guid.NewGuid();
        var payload = new byte[] { 1, 2, 3, 4 };
        var envelope = WireEnvelope.Create(WireMessageType.ActionIntent, requestId, 42, payload, WireCodec.Protobuf);
        var encoded = WireEnvelopeCodec.Encode(envelope);
        var decoded = WireEnvelopeCodec.Decode(encoded);

        Assert.Equal(envelope.ProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(envelope.MessageType, decoded.MessageType);
        Assert.Equal(envelope.RequestId, decoded.RequestId);
        Assert.Equal(envelope.Sequence, decoded.Sequence);
        Assert.Equal(payload, decoded.Payload.ToArray());
    }

    [Fact]
    public void JsonEnvelopeCanDeserializeTypedPayload()
    {
        var requestId = Guid.NewGuid();
        var envelope = WireEnvelope.Json(WireMessageType.Hello, requestId, 1, new HelloRequest("client-1", "catalog-a"));
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<HelloRequest>(envelope, out var request));
        Assert.Equal("client-1", request!.ClientVersion);
        Assert.Equal("catalog-a", request.GameDataFingerprint);
    }

    [Fact]
    public void DecoderRejectsTruncatedFrame()
    {
        Assert.False(WireEnvelopeCodec.TryDecode(new byte[8], out _));
    }

    [Fact]
    public void GenericCommandUsesStableMessageTypeAndJsonPayload()
    {
        using var document = JsonDocument.Parse("{\"actionId\":\"cultivation.draw-qi\"}");
        var request = new CommandEnvelopeDto("req-1", "enqueue_action", document.RootElement.Clone(), 7);
        var envelope = WireEnvelope.Json(WireMessageType.Command, Guid.NewGuid(), 8, request);
        var bytes = WireEnvelopeCodec.Encode(envelope);
        var decoded = WireEnvelopeCodec.Decode(bytes);

        Assert.Equal(WireMessageType.Command, decoded.MessageType);
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<CommandEnvelopeDto>(decoded, out var parsed));
        Assert.Equal("enqueue_action", parsed!.Command);
        Assert.Equal(7, parsed.ExpectedStateVersion);
        Assert.Equal("cultivation.draw-qi", parsed.Payload.GetProperty("actionId").GetString());

        var resultEnvelope = WireEnvelope.Json(
            WireMessageType.CommandResult,
            requestId: Guid.NewGuid(),
            sequence: 9,
            new CommandResultDto("req-1", true, "OK", "accepted", 8));
        Assert.Equal(WireMessageType.CommandResult, WireEnvelopeCodec.Decode(WireEnvelopeCodec.Encode(resultEnvelope)).MessageType);
    }

    [Fact]
    public void ActionIntentParametersRoundTripKeepsCamelCaseKeys()
    {
        var request = new ActionIntentRequest("req-1", "cultivation.draw-qi", 1, null, new Dictionary<string, double> { ["MaxPower"] = 5 });

        var json = JsonSerializer.Serialize(request, ProtocolJson.Options);
        Assert.Contains("\"maxPower\":5", json);

        var parsed = JsonSerializer.Deserialize<ActionIntentRequest>(json, ProtocolJson.Options)!;
        Assert.Equal(5, parsed.Parameters!["maxPower"]);

        // 序列化→反序列化→再序列化后键与值完全一致。
        Assert.Equal(json, JsonSerializer.Serialize(parsed, ProtocolJson.Options));
    }
}
