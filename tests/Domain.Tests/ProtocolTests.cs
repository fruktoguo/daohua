using System.Text.Json;

namespace Domain.Tests;

/// <summary>
/// 协议测试：二进制/JSON 信封、通用命令、行动意图参数与日志批次的编解码往返。
/// </summary>
public sealed class ProtocolTests
{
    /// <summary>
    /// JSON 信封携带的类型化载荷（HelloRequest）可完整反序列化，字段不丢不错位。
    /// </summary>
    [Fact]
    public void JsonEnvelopeCanDeserializeTypedPayload()
    {
        var requestId = Guid.NewGuid();
        var envelope = WireEnvelope.Json(WireMessageType.Hello, requestId, 1, new HelloRequest("client-1", "catalog-a"));
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<HelloRequest>(envelope, out var request));
        Assert.Equal("client-1", request!.ClientVersion);
        Assert.Equal("catalog-a", request.GameDataFingerprint);
    }

    /// <summary>
    /// 截断帧必须安全解码失败（返回 false）而非抛异常或越界，保护连接层不被坏包打断。
    /// </summary>
    [Fact]
    public void DecoderRejectsTruncatedFrame()
    {
        Assert.False(WireEnvelopeCodec.TryDecode(new byte[8], out _));
    }

    /// <summary>
    /// 通用命令通道协议对齐：Command/CommandResult 消息类型码经二进制信封往返稳定，
    /// 内嵌 JSON 字段（command、expectedStateVersion、actionId）完整保留。
    /// </summary>
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
            new CommandResultDto("req-1", true, "OK", "accepted", 8, CommandResultSnapshotForms.Delta));
        var decodedResult = WireEnvelopeCodec.Decode(WireEnvelopeCodec.Encode(resultEnvelope));
        Assert.Equal(WireMessageType.CommandResult, decodedResult.MessageType);
        // 回执快照形态字段随信封往返：前端按形态分派全量/增量合并，不做模糊探测。
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<CommandResultDto>(decodedResult, out var parsedResult));
        Assert.Equal(CommandResultSnapshotForms.Delta, parsedResult!.SnapshotForm);
    }

    [Fact]
    public void CommandResultWithDataRoundTripSucceeds()
    {
        var awakened = new AwakenedTraitResultDto("trait.tianshengmanli", 2, 1, true, false);
        var dataObj = new { awakenedTraits = new[] { awakened } };
        var dataElement = JsonSerializer.SerializeToElement(dataObj, ProtocolJson.Options);

        var envelope = WireEnvelope.Json(
            WireMessageType.CommandResult,
            requestId: Guid.NewGuid(),
            sequence: 10,
            new CommandResultDto("req-2", true, "OK", "success", 9, CommandResultSnapshotForms.Snapshot, Data: dataElement));

        var decoded = WireEnvelopeCodec.Decode(WireEnvelopeCodec.Encode(envelope));
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<CommandResultDto>(decoded, out var parsedResult));
        Assert.NotNull(parsedResult);
        Assert.True(parsedResult.Succeeded);
        Assert.NotNull(parsedResult.Data);

        var root = parsedResult.Data.Value;
        Assert.True(root.TryGetProperty("awakenedTraits", out var listElement));
        var list = listElement.Deserialize<List<AwakenedTraitResultDto>>(ProtocolJson.Options);
        Assert.NotNull(list);
        Assert.Single(list);
        Assert.Equal("trait.tianshengmanli", list[0].TraitId);
        Assert.Equal(2, list[0].Level);
        Assert.Equal(1, list[0].Exp);
        Assert.True(list[0].LeveledUp);
        Assert.False(list[0].IsNew);
    }

    /// <summary>
    /// 行动意图 Parameters 字典键必须保持 camelCase（前端契约），
    /// 序列化→反序列化→再序列化后报文逐字节一致。
    /// </summary>
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
