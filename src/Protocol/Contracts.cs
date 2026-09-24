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
    LogBatch = 17,
    /// <summary>客户端发起传音频道发言（仅 WebSocket 上行）</summary>
    ChatSend = 18,
    /// <summary>服务端广播的单条传音消息（仅 WebSocket 下行）</summary>
    ChatMessage = 19,
    /// <summary>服务端回放某频道作用域最近 100 条传音（仅 WebSocket 下行）</summary>
    ChatHistory = 20,
    /// <summary>客户端发起社交指令（好友申请/队伍邀请等，仅 WebSocket 上行）</summary>
    SocialAction = 21,
    /// <summary>服务端下发社交状态（好友/申请/队伍/附近修士，仅 WebSocket 下行）</summary>
    SocialState = 22,
    /// <summary>客户端请求某频道作用域的历史回放（仅 WebSocket 上行）</summary>
    ChatHistoryRequest = 23
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
/// <param name="ProtocolVersion">协议版本号</param>
/// <param name="MessageType">信封消息类型</param>
/// <param name="RequestId">请求唯一标识</param>
/// <param name="Sequence">单调递增序列号</param>
/// <param name="Codec">有效载荷编码格式</param>
/// <param name="Payload">序列化后的有效载荷字节</param>
public readonly record struct WireEnvelope(
    ushort ProtocolVersion,
    WireMessageType MessageType,
    Guid RequestId,
    long Sequence,
    WireCodec Codec,
    ReadOnlyMemory<byte> Payload)
{
    /// <summary>
    /// 创建并校验 <see cref="WireEnvelope"/> 实例。
    /// </summary>
    /// <param name="messageType">信封消息类型</param>
    /// <param name="requestId">请求唯一标识</param>
    /// <param name="sequence">单调递增序列号</param>
    /// <param name="payload">序列化后的有效载荷字节</param>
    /// <param name="codec">有效载荷编码格式，默认为 JSON</param>
    /// <param name="version">协议版本号，默认为当前主版本</param>
    /// <returns>构造完成的 <see cref="WireEnvelope"/> 实例</returns>
    /// <exception cref="ProtocolException">协议帧参数无效时抛出</exception>
    public static WireEnvelope Create(WireMessageType messageType, Guid requestId, long sequence, ReadOnlyMemory<byte> payload, WireCodec codec = WireCodec.Json, ushort version = ProtocolConstants.CurrentVersion) =>
        version == 0 || !Enum.IsDefined(messageType) || !Enum.IsDefined(codec) || requestId == Guid.Empty || sequence < 0 || payload.Length > ProtocolConstants.MaxFrameBytes
            ? throw new ProtocolException("协议帧参数无效。")
            : new(version, messageType, requestId, sequence, codec, payload);

    /// <summary>
    /// 使用 JSON 序列化值并创建 <see cref="WireEnvelope"/>。
    /// </summary>
    /// <param name="messageType">信封消息类型</param>
    /// <param name="requestId">请求唯一标识</param>
    /// <param name="sequence">单调递增序列号</param>
    /// <param name="value">需要 JSON 序列化的业务对象</param>
    /// <returns>JSON 编码后的 <see cref="WireEnvelope"/> 实例</returns>
    public static WireEnvelope Json<T>(WireMessageType messageType, Guid requestId, long sequence, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ProtocolJson.Options);
        return Create(messageType, requestId, sequence, bytes, WireCodec.Json);
    }
}


/// <summary>协议异常，用于协议参数校验或编解码失败场景。</summary>
/// <param name="message">异常消息</param>
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

    /// <summary>将 <see cref="WireEnvelope"/> 结构打包为完整的网络二进制字节数组</summary>
    /// <param name="envelope">待编码的信封实例</param>
    /// <returns>包含 36 字节报头和负载的完整字节数组</returns>
    /// <exception cref="ProtocolException">协议帧参数无效时抛出</exception>
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
    /// <param name="messageType">信封消息类型</param>
    /// <param name="requestId">请求唯一标识</param>
    /// <param name="sequence">单调递增序列号</param>
    /// <param name="value">需要 JSON 序列化的业务对象</param>
    /// <param name="version">协议版本号，默认为当前主版本</param>
    /// <returns>JSON 编码后的完整字节数组</returns>
    /// <exception cref="ProtocolException">协议帧参数无效或超过最大帧大小时抛出</exception>
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
    /// 尝试从字节流解析二进制协议帧；格式非法或魔数不匹配时返回 false，不抛异常。
    /// </summary>
    /// <param name="bytes">待解析的二进制数据</param>
    /// <param name="envelope">解析成功时输出的 <see cref="WireEnvelope"/> 实例</param>
    /// <returns>解析成功返回 true，否则返回 false</returns>
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

    /// <summary>
    /// 解码二进制帧，失败时抛出 <see cref="ProtocolException"/>。
    /// </summary>
    /// <param name="bytes">待解码的二进制数据</param>
    /// <returns>解析后的 <see cref="WireEnvelope"/> 实例</returns>
    /// <exception cref="ProtocolException">协议帧格式无效时抛出</exception>
    public static WireEnvelope Decode(ReadOnlySpan<byte> bytes) =>
        TryDecode(bytes, out var envelope) ? envelope : throw new ProtocolException("协议帧格式无效。");

    /// <summary>
    /// 反序列化信封内的 JSON 有效载荷为强类型对象。
    /// </summary>
    /// <param name="envelope">包含 JSON 负载的信封</param>
    /// <param name="value">反序列化成功时输出的强类型对象</param>
    /// <returns>反序列化成功返回 true，否则返回 false</returns>
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
    /// <summary>获取协议层共享的 JSON 序列化选项</summary>
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
    /// <summary>
    /// 读取 JSON 对象，将键统一归一化为 camelCase 后构建 double 参数字典。
    /// </summary>
    /// <param name="reader">UTF-8 JSON 读取器</param>
    /// <param name="typeToConvert">目标转换类型</param>
    /// <param name="options">当前序列化选项</param>
    /// <returns>归一化后的只读参数字典；若 JSON 为 null 则返回 null</returns>
    /// <exception cref="JsonException">JSON 格式或值类型非法时抛出</exception>
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

    /// <summary>
    /// 将 double 参数字典的键统一转换为 camelCase 后写入 JSON 对象。
    /// </summary>
    /// <param name="writer">UTF-8 JSON 写入器</param>
    /// <param name="value">待写入的只读参数字典</param>
    /// <param name="options">当前序列化选项</param>
    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, double> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var pair in value)
            writer.WriteNumber(JsonNamingPolicy.CamelCase.ConvertName(pair.Key), pair.Value);
        writer.WriteEndObject();
    }
}

/// <summary>握手问候请求载荷</summary>
/// <param name="ClientVersion">客户端版本号</param>
/// <param name="GameDataFingerprint">客户端本地游戏数据指纹；与服务端不一致时握手将收到 GAME_DATA_MISMATCH 错误，可选</param>
/// <param name="DeviceFingerprint">防多开设备指纹（客户端硬件特征哈希），可选；服务端启用设备配额时必填</param>
public sealed record HelloRequest(string ClientVersion, string? GameDataFingerprint = null, string? DeviceFingerprint = null);
/// <summary>防多开子系统下发给客户端的功能开关</summary>
/// <param name="TabGuardEnabled">是否启用前端多标签页互斥守卫</param>
public sealed record AntiMultiOpenConfigDto(bool TabGuardEnabled);
/// <summary>握手问候应答载荷</summary>
/// <param name="ServerVersion">服务端版本号</param>
/// <param name="ProtocolVersion">协议版本号</param>
/// <param name="GameDataFingerprint">游戏数据指纹</param>
/// <param name="ServerSequence">服务端当前序列号</param>
/// <param name="AntiMultiOpen">防多开功能开关，可选</param>
public sealed record HelloResponse(string ServerVersion, ushort ProtocolVersion, string GameDataFingerprint, long ServerSequence, AntiMultiOpenConfigDto? AntiMultiOpen = null);

/// <summary>玩家持久化头 DTO</summary>
/// <param name="PlayerId">玩家唯一标识</param>
/// <param name="StateVersion">状态版本号</param>
/// <param name="SchemaVersion">分段 schema 版本号</param>
/// <param name="LastSimulatedAt">最近一次模拟推进时间</param>
/// <param name="GameDataFingerprint">游戏数据指纹</param>
/// <param name="Checksum">数据校验和</param>
/// <param name="UpdatedAt">头信息更新时间</param>
public sealed record PlayerHeadDto(
    string PlayerId,
    long StateVersion,
    int SchemaVersion,
    DateTimeOffset LastSimulatedAt,
    string GameDataFingerprint,
    string Checksum,
    DateTimeOffset UpdatedAt);

/// <summary>玩家分段快照载荷 DTO</summary>
/// <param name="Section">分段标识名</param>
/// <param name="SectionVersion">分段数据版本号</param>
/// <param name="SchemaVersion">分段 schema 版本号</param>
/// <param name="Data">分段业务载荷（规范化 JSON 直接内嵌，不再走 Base64 字节二次编码）；
/// JsonElement.WriteTo 按原始文本透传，信封中该成员的字节区间即 <paramref name="Checksum"/> 哈希输入，客户端可逐字节复算</param>
/// <param name="Checksum">规范化 JSON 字节的 SHA-256（小写 hex）</param>
/// <param name="UpdatedAt">分段更新时间</param>
public sealed record PlayerSectionDto(
    string Section,
    long SectionVersion,
    int SchemaVersion,
    JsonElement Data,
    string Checksum,
    DateTimeOffset UpdatedAt);

/// <summary>玩家全量快照 DTO（包含全部分段及资产钱包列表）</summary>
/// <param name="PlayerId">玩家唯一标识</param>
/// <param name="DisplayName">展示名称</param>
/// <param name="LastSimulatedAt">最近一次模拟推进时间</param>
/// <param name="StateVersion">状态版本号</param>
/// <param name="Head">玩家持久化头</param>
/// <param name="Sections">全部分段快照列表</param>
/// <param name="Assets">资产钱包列表</param>
public sealed record PlayerSnapshotDto(
    string PlayerId,
    string DisplayName,
    DateTimeOffset LastSimulatedAt,
    long StateVersion,
    PlayerHeadDto Head,
    IReadOnlyList<PlayerSectionDto> Sections,
    IReadOnlyList<AssetBalanceDto> Assets);

/// <summary>
/// 玩家增量快照 DTO（仅携带本次变更的分段与受影响资产）。
/// LastSimulatedAt 必须随每个增量下发：浏览器以「服务端权威时刻 + 本地单调流逝」推算进度，
/// 缺少该字段会让服务端时钟锚点停留在最后一次全量快照，倒计时与进度条偏移会随时间线性累积。
/// </summary>
/// <param name="PlayerId">玩家唯一标识</param>
/// <param name="StateVersion">状态版本号</param>
/// <param name="LastSimulatedAt">最近一次模拟推进时间</param>
/// <param name="ChangedSections">本次变更的分段快照列表</param>
/// <param name="ChangedAssets">本次变更的资产余额列表，可选</param>
public sealed record PlayerSnapshotDeltaDto(
    string PlayerId,
    long StateVersion,
    DateTimeOffset LastSimulatedAt,
    IReadOnlyList<PlayerSectionDto> ChangedSections,
    IReadOnlyList<AssetBalanceDto>? ChangedAssets = null);

/// <summary>行动排队意图请求 DTO</summary>
/// <param name="RequestId">请求唯一标识，客户端幂等键</param>
/// <param name="ActionId">行动配置 ID</param>
/// <param name="RepeatCount">计划重复次数；必须为 [1, 1_000_000] 内的整数，或特殊值 -1 表示无限循环直至玩家取消/资源断绝</param>
/// <param name="TargetNodeId">目标节点标识，可选</param>
/// <param name="Parameters">额外参数字典，可选</param>
/// <param name="PvpEnabled">斗法开关：玩家同意参与 PVP 匹配，仅对战斗遭遇行动生效</param>
public sealed record ActionIntentRequest(
    string RequestId,
    string ActionId,
    double RepeatCount = 1,
    string? TargetNodeId = null,
    [property: JsonConverter(typeof(CamelCaseKeyDoubleDictionaryConverter))] IReadOnlyDictionary<string, double>? Parameters = null,
    bool PvpEnabled = false);

/// <summary>行动排队意图应答 DTO</summary>
/// <param name="RequestId">请求唯一标识</param>
/// <param name="ActionInstanceId">行动实例标识</param>
/// <param name="ActionId">行动配置 ID</param>
/// <param name="EnqueuedAt">入队时间</param>
/// <param name="RemainingRepeats">剩余重复次数；-1 表示该行动处于无限循环，结算后不会自动出队</param>
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
/// <param name="RequestId">请求唯一标识，客户端幂等键</param>
/// <param name="Command">命令名称</param>
/// <param name="Payload">命令 JSON 载荷</param>
/// <param name="ExpectedStateVersion">客户端期望的服务端状态版本；大于 0 时若与服务端当前版本不符将返回 STATE_VERSION_CONFLICT，0 表示不做乐观并发校验</param>
public sealed record CommandEnvelopeDto(
    string RequestId,
    string Command,
    JsonElement Payload,
    long ExpectedStateVersion = 0);

/// <summary>通用命令回执；失败时不返回内部异常或存储细节。</summary>
/// <param name="RequestId">请求唯一标识</param>
/// <param name="Succeeded">命令是否执行成功</param>
/// <param name="Code">业务结果代码</param>
/// <param name="Message">可展示给用户的提示消息</param>
/// <param name="StateVersion">服务端当前状态版本</param>
/// <param name="Payload">附加结果 JSON 载荷，可选</param>
public sealed record CommandResultDto(
    string RequestId,
    bool Succeeded,
    string Code,
    string Message,
    long StateVersion,
    JsonElement? Payload = null);

/// <summary>资产持有余额传输 DTO</summary>
/// <param name="AssetId">资产配置 ID</param>
/// <param name="Variant">资产变体，可选</param>
/// <param name="Quantity">当前持有数量</param>
/// <param name="Version">资产版本号</param>
public sealed record AssetBalanceDto(string AssetId, string? Variant, double Quantity, long Version);
/// <summary>资产变动差量传输 DTO</summary>
/// <param name="AssetId">资产配置 ID</param>
/// <param name="Variant">资产变体，可选</param>
/// <param name="QuantityDelta">数量变动值</param>
/// <param name="Reason">变动原因</param>
/// <param name="InstanceId">关联实例标识，可选</param>
public sealed record AssetDeltaDto(string AssetId, string? Variant, double QuantityDelta, string Reason, string? InstanceId = null);

/// <summary>坊市挂单操作请求 DTO</summary>
/// <param name="RequestId">请求唯一标识</param>
/// <param name="OrderId">订单唯一标识</param>
/// <param name="AssetId">资产配置 ID</param>
/// <param name="Variant">资产变体，可选</param>
/// <param name="Side">买卖方向（buy/sell，服务端解析大小写不敏感）</param>
/// <param name="Price">挂单单价</param>
/// <param name="Quantity">挂单数量</param>
public sealed record MarketOrderRequestDto(
    string RequestId,
    string OrderId,
    string AssetId,
    string? Variant,
    string Side,
    double Price,
    double Quantity);

/// <summary>坊市订单详情传输 DTO</summary>
/// <param name="OrderId">订单唯一标识</param>
/// <param name="OwnerId">挂单者玩家标识</param>
/// <param name="AssetId">资产配置 ID</param>
/// <param name="Variant">资产变体，可选</param>
/// <param name="Side">买卖方向</param>
/// <param name="Price">挂单单价</param>
/// <param name="OriginalQuantity">原始挂单数量</param>
/// <param name="RemainingQuantity">剩余未成交数量</param>
/// <param name="Status">订单状态（active/partially_filled/filled/cancelled/expired）</param>
/// <param name="Sequence">订单序列号</param>
/// <param name="CreatedAt">创建时间</param>
/// <param name="ExpiresAt">到期时间；无期限订单为 null</param>
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
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>坊市撮合成交明细 DTO</summary>
/// <param name="TradeId">成交唯一标识</param>
/// <param name="BuyOrderId">买入订单标识</param>
/// <param name="SellOrderId">卖出订单标识</param>
/// <param name="BuyerId">买方玩家标识</param>
/// <param name="SellerId">卖方玩家标识</param>
/// <param name="AssetId">资产配置 ID</param>
/// <param name="Variant">资产变体，可选</param>
/// <param name="Price">成交价</param>
/// <param name="Quantity">成交数量</param>
/// <param name="TradedAt">成交时间</param>
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
/// <param name="AssetId">资产配置 ID</param>
/// <param name="Variant">资产变体，可选</param>
/// <param name="Asks">卖方档位列表</param>
/// <param name="Bids">买方档位列表</param>
/// <param name="Version">盘口版本号</param>
/// <param name="CapturedAt">快照捕获时间</param>
public sealed record MarketBookDto(
    string AssetId,
    string? Variant,
    IReadOnlyList<MarketLevelDto> Asks,
    IReadOnlyList<MarketLevelDto> Bids,
    long Version,
    DateTimeOffset CapturedAt);

/// <summary>盘口单一价格档位明细 DTO</summary>
/// <param name="Price">档位价格</param>
/// <param name="Quantity">该价格累计数量</param>
/// <param name="OrderCount">该价格订单数</param>
public sealed record MarketLevelDto(double Price, double Quantity, int OrderCount);

/// <summary>调度冷挂机计算节点进行确定性离线补算请求 DTO</summary>
/// <param name="JobId">任务唯一标识</param>
/// <param name="PlayerId">玩家唯一标识</param>
/// <param name="From">补算起始时间</param>
/// <param name="To">补算结束时间</param>
/// <param name="StatePayloadBase64">Base64 编码的玩家状态负载</param>
/// <param name="GameDataFingerprint">游戏数据指纹</param>
public sealed record ComputeAdvanceRequestDto(
    string JobId,
    string PlayerId,
    DateTimeOffset From,
    DateTimeOffset To,
    string StatePayloadBase64,
    string GameDataFingerprint);

/// <summary>计算节点补算完成响应 DTO</summary>
/// <param name="JobId">任务唯一标识</param>
/// <param name="Succeeded">补算是否成功</param>
/// <param name="PlayerId">玩家唯一标识</param>
/// <param name="From">补算起始时间</param>
/// <param name="To">补算结束时间</param>
/// <param name="StatePayloadBase64">Base64 编码的新状态负载</param>
/// <param name="AssetDeltas">补算产生的资产变动列表</param>
/// <param name="Events">补算期间触发的事件列表</param>
/// <param name="ErrorCode">错误码，仅在失败时有值</param>
/// <param name="ErrorMessage">可读错误消息，仅在失败时有值</param>
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
/// <param name="Code">错误代码</param>
/// <param name="Message">可读错误消息</param>
/// <param name="RequestId">关联请求标识，可选</param>
public sealed record ErrorDto(string Code, string Message, string? RequestId = null);

/// <summary>战报/系统日志事件；template 采用前端引用语法 {{kind:id|label|extra}}，前端仅做解析渲染。Count 表示连续相同模板的合并次数。</summary>
/// <param name="Sequence">事件序列号</param>
/// <param name="TimestampUnixMilliseconds">事件时间戳（Unix 毫秒）</param>
/// <param name="Template">前端模板字符串</param>
/// <param name="Count">连续相同模板合并次数，默认为 1</param>
/// <param name="Channel">日志分组频道：`system`（系统）或 `combat`（战斗），缺省 `system`</param>
public sealed record LogEventDto(long Sequence, long TimestampUnixMilliseconds, string Template, int Count = 1, string Channel = "system");

/// <summary>日志事件批次载荷，按 sequence 单调递增推送。</summary>
/// <param name="Events">日志事件列表</param>
public sealed record LogBatchDto(IReadOnlyList<LogEventDto> Events);

/// <summary>日志分组频道标识常量（前端日志面板 Tab 分流口径）。</summary>
public static class LogChannels
{
    /// <summary>系统分组：消耗品使用、行动完成、抵达、离线补算、斗法撮合与结算及各类系统通知。</summary>
    public const string System = "system";
    /// <summary>战斗分组：战斗过程事件（搜寻、遭遇、施放、攻击、胜负、复活）。</summary>
    public const string Combat = "combat";

    /// <summary>按领域事件码解析所属日志分组：`combat.` 前缀归战斗，其余归系统。</summary>
    /// <param name="eventCode">领域模拟事件码。</param>
    /// <returns>日志分组频道标识。</returns>
    public static string FromEventCode(string eventCode) =>
        eventCode.StartsWith("combat.", StringComparison.Ordinal) ? Combat : System;
}

/// <summary>传音频道标识常量（协议与前端共享口径）。</summary>
public static class ChatChannels
{
    /// <summary>世界频道（全服在线修士）</summary>
    public const string World = "world";
    /// <summary>附近频道（同一地图节点在线修士）</summary>
    public const string Nearby = "nearby";
    /// <summary>好友私聊频道（双向好友 1:1）</summary>
    public const string Friend = "friend";
    /// <summary>队伍频道（队伍全体成员）</summary>
    public const string Party = "party";
    /// <summary>宗门频道（占位，尚未开放）</summary>
    public const string Sect = "sect";

    /// <summary>判定是否为当前可收发的频道标识。</summary>
    /// <param name="channel">频道标识。</param>
    /// <returns>可收发返回 true；`sect` 与未知值返回 false。</returns>
    public static bool IsSupported(string? channel) =>
        channel is World or Nearby or Friend or Party;
}

/// <summary>客户端传音发言请求。</summary>
/// <param name="RequestId">请求唯一标识，服务端回执配对与幂等去重键</param>
/// <param name="Channel">目标频道标识（见 <see cref="ChatChannels"/>）</param>
/// <param name="Body">消息正文（去除首尾空白后 1~200 字符）</param>
/// <param name="TargetPlayerId">好友频道必填的私聊对象玩家 ID；其余频道忽略</param>
public sealed record ChatSendDto(string RequestId, string Channel, string Body, string? TargetPlayerId = null);

/// <summary>单条传音消息。</summary>
/// <param name="MessageId">消息唯一标识</param>
/// <param name="Channel">频道标识</param>
/// <param name="ScopeKey">频道作用域键（世界为空串、附近为节点 ID、好友为双方 ID 升序拼接、队伍为队伍 ID）</param>
/// <param name="SenderId">发送者玩家 ID</param>
/// <param name="SenderName">发送者道号</param>
/// <param name="Body">消息正文</param>
/// <param name="SentAtUnixMilliseconds">发送时刻（Unix 毫秒）</param>
public sealed record ChatMessageDto(
    string MessageId,
    string Channel,
    string ScopeKey,
    string SenderId,
    string SenderName,
    string Body,
    long SentAtUnixMilliseconds);

/// <summary>频道历史回放载荷：某作用域最近 100 条传音，按时间正序。</summary>
/// <param name="Channel">频道标识</param>
/// <param name="ScopeKey">频道作用域键</param>
/// <param name="TargetPlayerId">好友频道的私聊对象玩家 ID；其余频道为 null</param>
/// <param name="Messages">最近消息列表</param>
public sealed record ChatHistoryDto(string Channel, string ScopeKey, string? TargetPlayerId, IReadOnlyList<ChatMessageDto> Messages);

/// <summary>客户端频道历史回放请求。</summary>
/// <param name="Channel">目标频道标识</param>
/// <param name="TargetPlayerId">好友频道必填的私聊对象玩家 ID</param>
public sealed record ChatHistoryRequestDto(string Channel, string? TargetPlayerId = null);

/// <summary>社交指令动作标识常量。</summary>
public static class SocialActions
{
    /// <summary>发起好友申请</summary>
    public const string FriendRequest = "friend.request";
    /// <summary>接受收到的好友申请</summary>
    public const string FriendAccept = "friend.accept";
    /// <summary>拒绝收到的好友申请</summary>
    public const string FriendReject = "friend.reject";
    /// <summary>解除好友关系</summary>
    public const string FriendRemove = "friend.remove";
    /// <summary>邀请入队</summary>
    public const string PartyInvite = "party.invite";
    /// <summary>接受队伍邀请</summary>
    public const string PartyAccept = "party.accept";
    /// <summary>拒绝队伍邀请</summary>
    public const string PartyDecline = "party.decline";
    /// <summary>离开当前队伍</summary>
    public const string PartyLeave = "party.leave";

    /// <summary>判定是否为受支持的社交动作。</summary>
    /// <param name="action">动作标识。</param>
    /// <returns>受支持返回 true。</returns>
    public static bool IsSupported(string? action) => action is
        FriendRequest or FriendAccept or FriendReject or FriendRemove or
        PartyInvite or PartyAccept or PartyDecline or PartyLeave;
}

/// <summary>客户端社交指令请求。</summary>
/// <param name="RequestId">请求唯一标识，服务端回执配对键</param>
/// <param name="Action">动作标识（见 <see cref="SocialActions"/>）</param>
/// <param name="TargetPlayerId">目标玩家 ID；好友申请/解除、队伍邀请必填；接受/拒绝好友申请时为目标好友；接受/拒绝队伍邀请忽略该字段</param>
/// <param name="TargetName">目标道号；仅在 <c>friend.request</c> 且未提供 TargetPlayerId 时用于检索目标</param>
public sealed record SocialActionDto(string RequestId, string Action, string? TargetPlayerId = null, string? TargetName = null);

/// <summary>好友条目。</summary>
/// <param name="PlayerId">好友玩家 ID</param>
/// <param name="DisplayName">好友道号</param>
/// <param name="Online">是否在线</param>
public sealed record FriendDto(string PlayerId, string DisplayName, bool Online);

/// <summary>好友申请条目。</summary>
/// <param name="RequestId">申请唯一标识（用于接受/拒绝）</param>
/// <param name="PlayerId">对端玩家 ID</param>
/// <param name="DisplayName">对端道号</param>
/// <param name="CreatedAtUnixMilliseconds">申请时刻（Unix 毫秒）</param>
public sealed record FriendRequestDto(string RequestId, string PlayerId, string DisplayName, long CreatedAtUnixMilliseconds);

/// <summary>队伍成员条目。</summary>
/// <param name="PlayerId">成员玩家 ID</param>
/// <param name="DisplayName">成员道号</param>
/// <param name="Online">是否在线</param>
public sealed record PartyMemberDto(string PlayerId, string DisplayName, bool Online);

/// <summary>队伍状态。</summary>
/// <param name="PartyId">队伍唯一标识</param>
/// <param name="LeaderId">队长玩家 ID</param>
/// <param name="Members">成员列表（含队长）</param>
public sealed record PartyDto(string PartyId, string LeaderId, IReadOnlyList<PartyMemberDto> Members);

/// <summary>同节点在线修士条目。</summary>
/// <param name="PlayerId">修士玩家 ID</param>
/// <param name="DisplayName">修士道号</param>
public sealed record NearbyPlayerDto(string PlayerId, string DisplayName);

/// <summary>待处理的队伍邀请条目。</summary>
/// <param name="PartyId">邀请方队伍标识</param>
/// <param name="InviterId">邀请人玩家 ID</param>
/// <param name="InviterName">邀请人道号</param>
public sealed record PartyInvitationDto(string PartyId, string InviterId, string InviterName);

/// <summary>社交状态下发载荷；RequestId 非空表示对某条 <see cref="SocialActionDto"/> 的回执，为空表示服务端主动推送。</summary>
/// <param name="RequestId">关联的社交指令请求 ID，主动推送时为 null</param>
/// <param name="Friends">好友列表</param>
/// <param name="IncomingRequests">收到的好友申请</param>
/// <param name="OutgoingRequests">发出的好友申请</param>
/// <param name="Party">所在队伍；未组队时为 null</param>
/// <param name="PartyInvitation">待处理的队伍邀请；无邀请时为 null</param>
/// <param name="NearbyPlayers">同节点在线修士（含自己）</param>
public sealed record SocialStateDto(
    string? RequestId,
    IReadOnlyList<FriendDto> Friends,
    IReadOnlyList<FriendRequestDto> IncomingRequests,
    IReadOnlyList<FriendRequestDto> OutgoingRequests,
    PartyDto? Party,
    PartyInvitationDto? PartyInvitation,
    IReadOnlyList<NearbyPlayerDto> NearbyPlayers);

