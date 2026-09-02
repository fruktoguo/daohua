using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Protocol;

/// <summary>网络传输协议基础常量约束</summary>
public static class ProtocolConstants
{
    /// <summary>当前网络信封协议主版本号</summary>
    public const ushort CurrentVersion = 1;
    /// <summary>单帧网络包体最大允许字节数（1MB），超出将拒绝传输以防内存溢出</summary>
    public const int MaxFrameBytes = 1_048_576;
}

/// <summary>
/// 底层通信信封消息类型枚举。
/// 任何在此处增改的枚举，必须在同一次提交中同步对齐 frontend/src/network/protocol.ts。
/// </summary>
public enum WireMessageType : byte
{
    /// <summary>客户端发起的初次握手问候</summary>
    Hello = 1,
    /// <summary>服务端握手确认与指纹校验结果</summary>
    HelloAck = 2,
    /// <summary>全量角色状态快照下发</summary>
    Snapshot = 3,
    /// <summary>增量分段/资产变更快照下发</summary>
    SnapshotDelta = 4,
    /// <summary>行动排队意图请求（旧协议，推荐走 Command）</summary>
    ActionIntent = 5,
    /// <summary>行动排队响应</summary>
    ActionResult = 6,
    /// <summary>玩家资产钱包视图同步</summary>
    AssetView = 7,
    /// <summary>坊市盘口深度下发</summary>
    MarketBook = 8,
    /// <summary>坊市挂单操作请求</summary>
    MarketOrder = 9,
    /// <summary>通用协议级错误包</summary>
    Error = 10,
    /// <summary>心跳探活 Ping</summary>
    Ping = 11,
    /// <summary>心跳应答 Pong</summary>
    Pong = 12,
    /// <summary>调度冷挂机计算节点补算请求</summary>
    ComputeAdvanceRequest = 13,
    /// <summary>计算节点离线补算应答</summary>
    ComputeAdvanceResponse = 14,
    /// <summary>通用玩家命令（具体业务由内部 command 字段分发）</summary>
    Command = 15,
    /// <summary>通用玩家命令执行回执</summary>
    CommandResult = 16,
    /// <summary>服务端推送的战报/系统日志事件批次（仅 WebSocket 下行）</summary>
    LogBatch = 17
}

/// <summary>信封内有效载荷的编码格式</summary>
public enum WireCodec : byte
{
    /// <summary>无编码/裸二进制</summary>
    None = 0,
    /// <summary>UTF-8 JSON 文本载荷</summary>
    Json = 1,
    /// <summary>Protobuf 二进制载荷（预留）</summary>
    Protobuf = 2
}

/// <summary>
/// 统一 WebSocket / 内部通信二进制信封。
/// 报头共 36 字节：Magic(4B) + Version(2B) + Type(1B) + Codec(1B) + RequestId(16B) + Sequence(8B) + PayloadLength(4B)。
/// 领域层不感知该类型，由传输边界层按 Codec 解释。
/// </summary>
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

/// <summary>
/// 二进制网络信封编码与解码编解码器。
/// 采用大端字节序（Big Endian）写入 36 字节定长报头，后续紧跟负载。
/// 魔数：0x44485731 (ASCII "DHW1" = DaoHua Wire v1)。
/// </summary>
public static class WireEnvelopeCodec
{
    private const uint Magic = 0x44485731; // DHW1
    private const int HeaderBytes = 4 + 2 + 1 + 1 + 16 + 8 + 4;

    /// <summary>将 WireEnvelope 结构打包为完整的网络二进制字节数组</summary>
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

    /// <summary>
    /// 高性能下发路径：在单次缓冲区内直接就地序列化 JSON 并预留报头空间，
    /// 消除先 JsonSerializer.SerializeToUtf8Bytes 再二次包装产生的双重内存堆分配。
    /// </summary>
    public static byte[] EncodeJson<T>(WireMessageType messageType, Guid requestId, long sequence, T value, ushort version = ProtocolConstants.CurrentVersion)
    {
        if (version != ProtocolConstants.CurrentVersion
            || !Enum.IsDefined(messageType)
            || requestId == Guid.Empty
            || sequence < 0)
        {
            throw new ProtocolException("协议帧参数无效。");
        }

        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>(512);
        bufferWriter.Advance(HeaderBytes);
        using (var jsonWriter = new Utf8JsonWriter(bufferWriter))
        {
            JsonSerializer.Serialize(jsonWriter, value, ProtocolJson.Options);
        }

        var payloadLength = bufferWriter.WrittenCount - HeaderBytes;
        if (payloadLength > ProtocolConstants.MaxFrameBytes)
            throw new ProtocolException("协议帧超过最大大小。");

        var output = new byte[bufferWriter.WrittenCount];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4, 2), version);
        output[6] = (byte)messageType;
        output[7] = (byte)WireCodec.Json;
        requestId.TryWriteBytes(output.AsSpan(8, 16));
        BinaryPrimitives.WriteInt64BigEndian(output.AsSpan(24, 8), sequence);
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(32, 4), payloadLength);
        bufferWriter.WrittenSpan[HeaderBytes..].CopyTo(output.AsSpan(HeaderBytes));
        return output;
    }

    /// <summary>
    /// 尝试从字节流解析二进制协议帧；格式非法或魔数不匹配时返回 false，不抛异常
    /// </summary>
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

    /// <summary>解码二进制帧，失败时抛出 ProtocolException</summary>
    public static WireEnvelope Decode(ReadOnlySpan<byte> bytes) =>
        TryDecode(bytes, out var envelope) ? envelope : throw new ProtocolException("协议帧格式无效。");

    /// <summary>反序列化信封内的 JSON 有效载荷为强类型对象</summary>
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

/// <summary>网络传输统一 JSON 序列化选项（小驼峰命名、忽略 null 属性）</summary>
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

/// <summary>
/// string→double 参数字典转换器：线上键格式保持 camelCase（与 DictionaryKeyPolicy 写侧行为一致），
/// 读侧对键做 camelCase 归一化。DictionaryKeyPolicy 仅在写侧生效，直接回传的键会归一化对齐，
/// 修复序列化→反序列化→再序列化键不一致的 round-trip 不对称。
/// </summary>
public sealed class CamelCaseKeyDoubleDictionaryConverter : JsonConverter<IReadOnlyDictionary<string, double>>
{
    public override IReadOnlyDictionary<string, double>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("参数字典载荷必须是 JSON 对象。");
        var values = new Dictionary<string, double>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return values;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("参数字典载荷出现未知 token。");
            var key = reader.GetString()!;
            reader.Read();
            if (reader.TokenType != JsonTokenType.Number)
                throw new JsonException($"参数 {key} 的值必须是数字。");
            values[JsonNamingPolicy.CamelCase.ConvertName(key)] = reader.GetDouble();
        }
        throw new JsonException("参数字典对象未闭合。");
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, double> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var pair in value)
            writer.WriteNumber(JsonNamingPolicy.CamelCase.ConvertName(pair.Key), pair.Value);
        writer.WriteEndObject();
    }
}

/// <summary>握手问候请求载荷</summary>
public sealed record HelloRequest(string ClientVersion, string? GameDataFingerprint = null);
/// <summary>握手问候应答载荷</summary>
public sealed record HelloResponse(string ServerVersion, ushort ProtocolVersion, string GameDataFingerprint, long ServerSequence);

/// <summary>玩家持久化头 DTO</summary>
public sealed record PlayerHeadDto(
    string PlayerId,
    long StateVersion,
    int SchemaVersion,
    DateTimeOffset LastSimulatedAt,
    string GameDataFingerprint,
    string Checksum,
    DateTimeOffset UpdatedAt);

/// <summary>玩家分段快照载荷 DTO</summary>
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

/// <summary>玩家全量快照 DTO（包含全部分段及资产钱包列表）</summary>
public sealed record PlayerSnapshotDto(
    string PlayerId,
    string DisplayName,
    DateTimeOffset LastSimulatedAt,
    long StateVersion,
    PlayerHeadDto Head,
    IReadOnlyList<PlayerSectionDto> Sections,
    IReadOnlyList<AssetBalanceDto> Assets);

/// <summary>玩家增量快照 DTO（仅携带本次变更的分段与受影响资产）</summary>
public sealed record PlayerSnapshotDeltaDto(
    string PlayerId,
    long StateVersion,
    IReadOnlyList<PlayerSectionDto> ChangedSections,
    IReadOnlyList<AssetBalanceDto>? ChangedAssets = null);

/// <summary>行动排队意图请求 DTO</summary>
public sealed record ActionIntentRequest(
    string RequestId,
    string ActionId,
    double RepeatCount = 1,
    string? TargetNodeId = null,
    [property: JsonConverter(typeof(CamelCaseKeyDoubleDictionaryConverter))] IReadOnlyDictionary<string, double>? Parameters = null);

/// <summary>行动排队意图应答 DTO</summary>
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

/// <summary>资产持有余额传输 DTO</summary>
public sealed record AssetBalanceDto(string AssetId, string? Variant, double Quantity, long Version);
/// <summary>资产变动差量传输 DTO</summary>
public sealed record AssetDeltaDto(string AssetId, string? Variant, double QuantityDelta, string Reason, string? InstanceId = null);

/// <summary>坊市挂单操作请求 DTO</summary>
public sealed record MarketOrderRequestDto(
    string RequestId,
    string OrderId,
    string AssetId,
    string? Variant,
    string Side,
    double Price,
    double Quantity);

/// <summary>坊市订单详情传输 DTO</summary>
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

/// <summary>坊市撮合成交明细 DTO</summary>
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

/// <summary>坊市盘口深度传输 DTO</summary>
public sealed record MarketBookDto(
    string AssetId,
    string? Variant,
    IReadOnlyList<MarketLevelDto> Asks,
    IReadOnlyList<MarketLevelDto> Bids,
    long Version,
    DateTimeOffset CapturedAt);

/// <summary>盘口单一价格档位明细 DTO</summary>
public sealed record MarketLevelDto(double Price, double Quantity, int OrderCount);

/// <summary>调度冷挂机计算节点进行确定性离线补算请求 DTO</summary>
public sealed record ComputeAdvanceRequestDto(
    string JobId,
    string PlayerId,
    DateTimeOffset From,
    DateTimeOffset To,
    string StatePayloadBase64,
    string GameDataFingerprint);

/// <summary>计算节点补算完成响应 DTO</summary>
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

/// <summary>通用协议级错误信息 DTO</summary>
public sealed record ErrorDto(string Code, string Message, string? RequestId = null);

/// <summary>战报/系统日志事件；template 采用前端引用语法 {{kind:id|label|extra}}，前端仅做解析渲染。</summary>
public sealed record LogEventDto(long Sequence, long TimestampUnixMilliseconds, string Template);

/// <summary>日志事件批次载荷，按 sequence 单调递增推送。</summary>
public sealed record LogBatchDto(IReadOnlyList<LogEventDto> Events);

