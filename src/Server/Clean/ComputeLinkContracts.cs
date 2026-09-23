using System.Text.Json.Nodes;

namespace Server.Clean;

/// <summary>
/// compute-link.v1：计算节点 → 主服的反向拨入链路契约。
/// 节点主动建立 WebSocket 长连接（握手携带 X-Compute-Token）后即注册进动态节点组，
/// 主服经该连接下发 compute.v1 批次并回收结果；连接存活即节点在线，断开即热拔出。
/// 批次载荷完全复用 <see cref="ComputeDispatchContract"/> 的 compute.v1 类型，链路本身只定义信封。
/// </summary>
public static class ComputeLinkContract
{
    /// <summary>链路契约版本，必须与节点端 compute-link.v1 完全一致。</summary>
    public const string Version = "compute-link.v1";
    /// <summary>节点拨入端点路径。</summary>
    public const string Path = "/internal/compute-link";
    /// <summary>握手鉴权请求头（与 compute.v1 同源）。</summary>
    public const string TokenHeader = ComputeDispatchContract.TokenHeader;
    /// <summary>单帧消息最大字节数：compute.v1 批次上限 16 MiB + 信封冗余。</summary>
    public const int MaxMessageBytes = 20 * 1024 * 1024;
    /// <summary>同时在线节点数上限，超出拒绝拨入。</summary>
    public const int MaxConnectedNodes = 64;
    /// <summary>节点单条链路声明的最大并发批次数上限。</summary>
    public const int MaxNodeConcurrency = 64;
    /// <summary>握手首帧（hello）等待超时。</summary>
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(15);
    /// <summary>nodeName 最大长度。</summary>
    public const int MaxNodeNameLength = 128;
}

/// <summary>节点拨入后的首帧握手：声明契约版本、节点名、并发容量与配表指纹。</summary>
public sealed record ComputeLinkHello(
    string ContractVersion,
    string NodeName,
    int MaxConcurrentBatches,
    string? GameDataFingerprint);

/// <summary>主服接受注册后的应答帧。</summary>
public sealed record ComputeLinkWelcome(
    string ContractVersion,
    Guid NodeId,
    long ServerTimeUnixMilliseconds);

/// <summary>主服经链路下发的单个批次。</summary>
public sealed record ComputeLinkBatch(Guid BatchId, ComputeAdvanceBatchRequest Request);

/// <summary>节点回传的批次结果。</summary>
public sealed record ComputeLinkResult(Guid BatchId, ComputeAdvanceBatchResponse? Response);

/// <summary>链路级错误帧（握手拒绝等），发出后连接即被关闭。</summary>
public sealed record ComputeLinkError(string Code, string Message);

/// <summary>
/// compute-link.v1 帧编解码：所有消息扁平序列化为 JSON 文本帧，
/// 以顶层 type 字段判别类型（hello/welcome/batch/result/error），未知类型直接判非法。
/// </summary>
public static class ComputeLinkCodec
{
    /// <summary>把链路消息编码为 WebSocket 文本帧字节。</summary>
    public static byte[] Encode(object message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var node = JsonSerializer.SerializeToNode(message, ServerJson.Options)!.AsObject();
        node.Insert(0, "type", message switch
        {
            ComputeLinkHello => "hello",
            ComputeLinkWelcome => "welcome",
            ComputeLinkBatch => "batch",
            ComputeLinkResult => "result",
            ComputeLinkError => "error",
            _ => throw new InvalidDataException($"未知链路消息类型：{message.GetType().Name}。")
        });
        return JsonSerializer.SerializeToUtf8Bytes(node, ServerJson.Options);
    }

    /// <summary>把一帧原始字节解码为具体链路消息；type 缺失或非法时抛 <see cref="InvalidDataException"/>。</summary>
    public static object Decode(ReadOnlyMemory<byte> frame)
    {
        using var document = JsonDocument.Parse(frame);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("链路帧缺少 type 判别字段。");
        }
        return typeElement.GetString() switch
        {
            "hello" => root.Deserialize<ComputeLinkHello>(ServerJson.Options)
                ?? throw new InvalidDataException("hello 帧载荷无效。"),
            "welcome" => root.Deserialize<ComputeLinkWelcome>(ServerJson.Options)
                ?? throw new InvalidDataException("welcome 帧载荷无效。"),
            "batch" => root.Deserialize<ComputeLinkBatch>(ServerJson.Options)
                ?? throw new InvalidDataException("batch 帧载荷无效。"),
            "result" => root.Deserialize<ComputeLinkResult>(ServerJson.Options)
                ?? throw new InvalidDataException("result 帧载荷无效。"),
            "error" => root.Deserialize<ComputeLinkError>(ServerJson.Options)
                ?? throw new InvalidDataException("error 帧载荷无效。"),
            var other => throw new InvalidDataException($"未知链路帧类型：{other}。")
        };
    }
}
