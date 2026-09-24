namespace DaoHua.ComputeNode;

/// <summary>
/// compute-link.v1.2 节点侧镜像定义：节点主动拨入主服 WebSocket 的链路信封。
/// v1.1 在 hello 增加节点批次承接能力字段（MaxJobsPerBatch/MaxBatchBytes）；
/// v1.2 在 hello 增加 RuntimeFingerprint（进程架构/运行时描述），用于跨架构确定性差异诊断。
/// 与服务端 Server.Clean.ComputeLinkContracts 通过 JSON 字段名对齐（camelCase + type 判别），
/// 节点无项目引用，两侧各自维护同一形状的私有副本（字段对齐由 Server.Tests 单测门禁）。
/// </summary>
public static class ComputeLinkContract
{
    /// <summary>链路契约版本，必须与主服完全一致。</summary>
    public const string Version = "compute-link.v1.2";
    /// <summary>握手鉴权请求头（与 compute.v2 同源）。</summary>
    public const string TokenHeader = ComputeContract.TokenHeader;
    /// <summary>单帧消息最大字节数：compute.v2 批次上限 16 MiB + 信封冗余。</summary>
    public const int MaxMessageBytes = 20 * 1024 * 1024;
    /// <summary>等待主服 welcome 应答的超时。</summary>
    public static readonly TimeSpan WelcomeTimeout = TimeSpan.FromSeconds(15);
}

/// <summary>
/// 节点拨入后的首帧握手：声明契约版本、节点名、并发容量、配表指纹、批次承接能力与运行时指纹。
/// MaxJobsPerBatch/MaxBatchBytes 为 0 表示未声明，主服按自身配置上限装配；
/// RuntimeFingerprint 形如 "Arm64/.NET 10.0.1"，仅作诊断展示，不参与裁决。
/// </summary>
public sealed record LinkHello(
    string ContractVersion,
    string NodeName,
    int MaxConcurrentBatches,
    string? GameDataFingerprint,
    int MaxJobsPerBatch = 0,
    long MaxBatchBytes = 0,
    string? RuntimeFingerprint = null);

/// <summary>主服接受注册后的应答帧。</summary>
public sealed record LinkWelcome(
    string ContractVersion,
    Guid NodeId,
    long ServerTimeUnixMilliseconds);

/// <summary>主服经链路下发的单个批次。</summary>
public sealed record LinkBatch(Guid BatchId, AdvanceBatchRequest Request);

/// <summary>节点回传的批次结果。</summary>
public sealed record LinkResult(Guid BatchId, AdvanceBatchResponse Response);

/// <summary>链路级错误帧（握手拒绝等），收到后连接即被主服关闭。</summary>
public sealed record LinkError(string Code, string Message);

/// <summary>compute-link 帧编解码：顶层 type 字段判别 + 扁平 JSON 文本帧。</summary>
public static class LinkCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>把链路消息编码为 WebSocket 文本帧字节。</summary>
    public static byte[] Encode(object message)
    {
        var node = JsonSerializer.SerializeToNode(message, Options)!.AsObject();
        node.Insert(0, "type", message switch
        {
            LinkHello => "hello",
            LinkWelcome => "welcome",
            LinkBatch => "batch",
            LinkResult => "result",
            LinkError => "error",
            _ => throw new InvalidDataException($"未知链路消息类型：{message.GetType().Name}。")
        });
        return JsonSerializer.SerializeToUtf8Bytes(node, Options);
    }

    /// <summary>把一帧原始字节解码为具体链路消息。</summary>
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
            "hello" => root.Deserialize<LinkHello>(Options)
                ?? throw new InvalidDataException("hello 帧载荷无效。"),
            "welcome" => root.Deserialize<LinkWelcome>(Options)
                ?? throw new InvalidDataException("welcome 帧载荷无效。"),
            "batch" => root.Deserialize<LinkBatch>(Options)
                ?? throw new InvalidDataException("batch 帧载荷无效。"),
            "result" => root.Deserialize<LinkResult>(Options)
                ?? throw new InvalidDataException("result 帧载荷无效。"),
            "error" => root.Deserialize<LinkError>(Options)
                ?? throw new InvalidDataException("error 帧载荷无效。"),
            var other => throw new InvalidDataException($"未知链路帧类型：{other}。")
        };
    }
}
