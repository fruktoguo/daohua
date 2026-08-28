using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Protocol;

public static class ProtocolConstants
{
    public const ushort CurrentVersion = 1;
    public const int MaxFrameBytes = 1_048_576;
}

public enum WireMessageType : byte
{
    Hello = 1,
    HelloAck = 2,
    Snapshot = 3,
    SnapshotDelta = 4,
    ActionIntent = 5,
    ActionResult = 6,
    AssetView = 7,
    MarketBook = 8,
    MarketOrder = 9,
    Error = 10,
    Ping = 11,
    Pong = 12,
    ComputeAdvanceRequest = 13,
    ComputeAdvanceResponse = 14,
    /// <summary>通用玩家命令；具体业务命令由 command 字段分发。</summary>
    Command = 15,
    /// <summary>通用玩家命令回执。</summary>
    CommandResult = 16,
    /// <summary>服务端推送的战报/系统日志事件批次（仅 WS 下行）。</summary>
    LogBatch = 17
}

public enum WireCodec : byte
{
    None = 0,
    Json = 1,
    Protobuf = 2
}

/// <summary>统一 WebSocket/内部通道信封。载荷由边界层按 Codec 解释，领域层不感知该类型。</summary>
public readonly record struct WireEnvelope(
    ushort ProtocolVersion,
    WireMessageType MessageType,
    Guid RequestId,
    long Sequence,
    WireCodec Codec,
    ReadOnlyMemory<byte> Payload)
{
    public static WireEnvelope Create(WireMessageType messageType, Guid requestId, long sequence, ReadOnlyMemory<byte> payload, WireCodec codec = WireCodec.Json, ushort version = ProtocolConstants.CurrentVersion) =>
        version == 0 || !Enum.IsDefined(messageType) || !Enum.IsDefined(codec) || requestId == Guid.Empty || sequence < 0 || payload.Length > ProtocolConstants.MaxFrameBytes
            ? throw new ProtocolException("协议帧参数无效。")
            : new(version, messageType, requestId, sequence, codec, payload);

    public static WireEnvelope Json<T>(WireMessageType messageType, Guid requestId, long sequence, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ProtocolJson.Options);
        return Create(messageType, requestId, sequence, bytes, WireCodec.Json);
    }
}

public sealed class ProtocolException(string message) : Exception(message);

public static class WireEnvelopeCodec
{
    private const uint Magic = 0x44485731; // DHW1
    private const int HeaderBytes = 4 + 2 + 1 + 1 + 16 + 8 + 4;

    public static byte[] Encode(in WireEnvelope envelope)
    {
        if (envelope.ProtocolVersion != ProtocolConstants.CurrentVersion
            || !Enum.IsDefined(envelope.MessageType)
            || !Enum.IsDefined(envelope.Codec)
            || envelope.RequestId == Guid.Empty
            || envelope.Sequence < 0
            || envelope.Payload.Length > ProtocolConstants.MaxFrameBytes)
        {
            throw new ProtocolException("协议帧参数无效。");
        }

        var output = new byte[HeaderBytes + envelope.Payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4, 2), envelope.ProtocolVersion);
        output[6] = (byte)envelope.MessageType;
        output[7] = (byte)envelope.Codec;
        envelope.RequestId.TryWriteBytes(output.AsSpan(8, 16));
        BinaryPrimitives.WriteInt64BigEndian(output.AsSpan(24, 8), envelope.Sequence);
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(32, 4), envelope.Payload.Length);
        envelope.Payload.Span.CopyTo(output.AsSpan(HeaderBytes));
        return output;
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out WireEnvelope envelope)
    {
        envelope = default;
        if (bytes.Length < HeaderBytes || bytes.Length > HeaderBytes + ProtocolConstants.MaxFrameBytes) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]) != Magic) return false;
        var version = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(4, 2));
        var type = (WireMessageType)bytes[6];
        var codec = (WireCodec)bytes[7];
        if (!Enum.IsDefined(type) || !Enum.IsDefined(codec)) return false;
        var requestId = new Guid(bytes.Slice(8, 16));
        var sequence = BinaryPrimitives.ReadInt64BigEndian(bytes.Slice(24, 8));
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(32, 4));
        if (version != ProtocolConstants.CurrentVersion || requestId == Guid.Empty || sequence < 0 || payloadLength < 0 || payloadLength > ProtocolConstants.MaxFrameBytes || payloadLength != bytes.Length - HeaderBytes) return false;
        envelope = new WireEnvelope(version, type, requestId, sequence, codec, bytes.Slice(HeaderBytes, payloadLength).ToArray());
        return true;
    }

    public static WireEnvelope Decode(ReadOnlySpan<byte> bytes) =>
        TryDecode(bytes, out var envelope) ? envelope : throw new ProtocolException("协议帧格式无效。");

    public static bool TryDeserializeJson<T>(in WireEnvelope envelope, out T? value)
    {
        value = default;
        if (envelope.Codec != WireCodec.Json) return false;
        try
        {
            value = JsonSerializer.Deserialize<T>(envelope.Payload.Span, ProtocolJson.Options);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

public sealed record HelloRequest(string ClientVersion, string? GameDataFingerprint = null);
public sealed record HelloResponse(string ServerVersion, ushort ProtocolVersion, string GameDataFingerprint, long ServerSequence);

public sealed record PlayerHeadDto(
    string PlayerId,
    long StateVersion,
    int SchemaVersion,
    DateTimeOffset LastSimulatedAt,
    string GameDataFingerprint,
    string Checksum,
    DateTimeOffset UpdatedAt);

public sealed record PlayerSectionDto(
    string Section,
    long SectionVersion,
    int SchemaVersion,
    string Codec,
    string PayloadBase64,
    int RawBytes,
    int StoredBytes,
    string Checksum,
    DateTimeOffset UpdatedAt);

public sealed record PlayerSnapshotDto(
    string PlayerId,
    string DisplayName,
    DateTimeOffset LastSimulatedAt,
    long StateVersion,
    PlayerHeadDto Head,
    IReadOnlyList<PlayerSectionDto> Sections,
    IReadOnlyList<AssetBalanceDto> Assets);

public sealed record PlayerSnapshotDeltaDto(
    string PlayerId,
    long StateVersion,
    IReadOnlyList<PlayerSectionDto> ChangedSections,
    IReadOnlyList<AssetBalanceDto>? ChangedAssets = null);

public sealed record ActionIntentRequest(
    string RequestId,
    string ActionId,
    double RepeatCount = 1,
    string? TargetNodeId = null,
    IReadOnlyDictionary<string, double>? Parameters = null);

public sealed record ActionIntentResponse(
    string RequestId,
    string ActionInstanceId,
    string ActionId,
    DateTimeOffset EnqueuedAt,
    double RemainingRepeats);

/// <summary>
/// WebSocket/HTTP 共用的玩家命令载荷。信封已经提供 RequestId 时，载荷中的 RequestId 仍需一致，
/// 以便 JSON 日志、重试和客户端幂等键在脱离信封后仍可追踪。
/// </summary>
public sealed record CommandEnvelopeDto(
    string RequestId,
    string Command,
    JsonElement Payload,
    long ExpectedStateVersion = 0);

/// <summary>通用命令回执；失败时不返回内部异常或存储细节。</summary>
public sealed record CommandResultDto(
    string RequestId,
    bool Succeeded,
    string Code,
    string Message,
    long StateVersion,
    JsonElement? Payload = null);

public sealed record AssetBalanceDto(string AssetId, string? Variant, double Quantity, long Version);
public sealed record AssetDeltaDto(string AssetId, string? Variant, double QuantityDelta, string Reason, string? InstanceId = null);

public sealed record MarketOrderRequestDto(
    string RequestId,
    string OrderId,
    string AssetId,
    string? Variant,
    string Side,
    double Price,
    double Quantity);

public sealed record MarketOrderDto(
    string OrderId,
    string OwnerId,
    string AssetId,
    string? Variant,
    string Side,
    double Price,
    double OriginalQuantity,
    double RemainingQuantity,
    string Status,
    long Sequence,
    DateTimeOffset CreatedAt);

public sealed record MarketFillDto(
    string TradeId,
    string BuyOrderId,
    string SellOrderId,
    string BuyerId,
    string SellerId,
    string AssetId,
    string? Variant,
    double Price,
    double Quantity,
    DateTimeOffset TradedAt);

public sealed record MarketBookDto(
    string AssetId,
    string? Variant,
    IReadOnlyList<MarketLevelDto> Asks,
    IReadOnlyList<MarketLevelDto> Bids,
    long Version,
    DateTimeOffset CapturedAt);

public sealed record MarketLevelDto(double Price, double Quantity, int OrderCount);

public sealed record ComputeAdvanceRequestDto(
    string JobId,
    string PlayerId,
    DateTimeOffset From,
    DateTimeOffset To,
    string StatePayloadBase64,
    string GameDataFingerprint);

public sealed record ComputeAdvanceResponseDto(
    string JobId,
    bool Succeeded,
    string PlayerId,
    DateTimeOffset From,
    DateTimeOffset To,
    string StatePayloadBase64,
    IReadOnlyList<AssetDeltaDto> AssetDeltas,
    IReadOnlyList<string> Events,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record ErrorDto(string Code, string Message, string? RequestId = null);

/// <summary>战报/系统日志事件；template 采用前端引用语法 {{kind:id|label|extra}}，前端仅做解析渲染。</summary>
public sealed record LogEventDto(long Sequence, long TimestampUnixMilliseconds, string Template);

/// <summary>日志事件批次载荷，按 sequence 单调递增推送。</summary>
public sealed record LogBatchDto(IReadOnlyList<LogEventDto> Events);

