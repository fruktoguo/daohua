namespace Server.Clean;

/// <summary>
/// 主服务侧对 compute.v1 冷挂机批处理契约的镜像定义。
/// ComputeNode 是无项目引用的独立进程，无法共享其程序集；两侧通过 JSON 字段名对齐，
/// 序列化一律使用 ServerJson.Options（camelCase），与节点端 JsonSerializerDefaults.Web 线格式一致。
/// </summary>
public static class ComputeDispatchContract
{
    /// <summary>契约版本字符串，必须与 ComputeNode 端 compute.v1 完全一致。</summary>
    public const string Version = "compute.v1";
    /// <summary>内部鉴权令牌的 HTTP 请求头名称。</summary>
    public const string TokenHeader = "X-Compute-Token";
    /// <summary>批处理端点相对路径。</summary>
    public const string AdvanceBatchPath = "/internal/v1/compute/advance-batch";
    /// <summary>每个任务允许包含的最大 section 数量（与节点上限一致）。</summary>
    public const int MaxSectionsPerJob = 32;
}

/// <summary>compute.v1 批次推进请求。</summary>
/// <param name="ContractVersion">契约版本。</param>
/// <param name="GameDataFingerprint">主服务当前游戏数据指纹。</param>
/// <param name="Jobs">待推进的离线任务列表。</param>
public sealed record ComputeAdvanceBatchRequest(
    string ContractVersion,
    string? GameDataFingerprint,
    IReadOnlyList<ComputeAdvanceJobRequest> Jobs);

/// <summary>compute.v1 单个离线任务推进请求（compute_jobs.input_payload 的序列化形态）。</summary>
/// <param name="JobId">任务唯一标识。</param>
/// <param name="PlayerId">玩家唯一标识。</param>
/// <param name="SourceStateVersion">源状态版本号。</param>
/// <param name="FromUnixMilliseconds">起始时间戳（毫秒）。</param>
/// <param name="TargetUnixMilliseconds">目标时间戳（毫秒）。</param>
/// <param name="Sections">待推进的状态 section 载荷列表。</param>
/// <param name="Actions">可选的行动意图列表。</param>
/// <param name="IdempotencyKey">可选的幂等键。</param>
public sealed record ComputeAdvanceJobRequest(
    Guid JobId,
    Guid PlayerId,
    long SourceStateVersion,
    long FromUnixMilliseconds,
    long TargetUnixMilliseconds,
    IReadOnlyList<ComputeSectionPayload> Sections,
    IReadOnlyList<ComputeActionIntent>? Actions = null,
    string? IdempotencyKey = null);

/// <summary>compute.v1 单个状态 section 载荷。</summary>
/// <param name="Kind">section 类型标识。</param>
/// <param name="Version">section 当前版本号。</param>
/// <param name="Codec">载荷编码，线上为 identity 或 brotli。</param>
/// <param name="PayloadBase64">Base64 编码的（可能压缩的）载荷。</param>
/// <param name="Checksum">原始载荷的 SHA-256 校验和。</param>
public sealed record ComputeSectionPayload(
    string Kind,
    long Version,
    string Codec,
    string PayloadBase64,
    string Checksum);

/// <summary>compute.v1 离线阶段行动意图。</summary>
/// <param name="ActionId">行动标识。</param>
/// <param name="DurationSeconds">单次行动持续时间（秒）。</param>
/// <param name="RepeatCount">重复次数。</param>
public sealed record ComputeActionIntent(
    string ActionId,
    double DurationSeconds,
    int RepeatCount = 1);

/// <summary>compute.v1 批次推进响应。</summary>
/// <param name="ContractVersion">实际使用的契约版本。</param>
/// <param name="GameDataFingerprint">计算节点当前游戏数据指纹。</param>
/// <param name="ServerTimeUnixMilliseconds">节点响应时间戳（毫秒）。</param>
/// <param name="Jobs">每个任务的推进结果。</param>
public sealed record ComputeAdvanceBatchResponse(
    string ContractVersion,
    string GameDataFingerprint,
    long ServerTimeUnixMilliseconds,
    IReadOnlyList<ComputeAdvanceJobResult> Jobs);

/// <summary>compute.v1 单个任务推进结果。</summary>
/// <param name="JobId">任务唯一标识。</param>
/// <param name="PlayerId">玩家唯一标识。</param>
/// <param name="Success">是否推进成功。</param>
/// <param name="SourceStateVersion">源状态版本号。</param>
/// <param name="TargetStateVersion">目标状态版本号。</param>
/// <param name="Sections">推进后的 section 载荷，失败时为 <see langword="null"/>。</param>
/// <param name="Metrics">推进计算指标，失败时为 <see langword="null"/>。</param>
/// <param name="ErrorCode">错误码，成功时为 <see langword="null"/>。</param>
/// <param name="ErrorMessage">错误消息，成功时为 <see langword="null"/>。</param>
/// <param name="Retryable">失败结果是否可重试。</param>
public sealed record ComputeAdvanceJobResult(
    Guid JobId,
    Guid PlayerId,
    bool Success,
    long SourceStateVersion,
    long TargetStateVersion,
    IReadOnlyList<ComputeSectionPayload>? Sections,
    ComputeAdvanceMetrics? Metrics,
    string? ErrorCode,
    string? ErrorMessage,
    bool Retryable);

/// <summary>compute.v1 推进计算指标。</summary>
/// <param name="ElapsedMilliseconds">推进耗时（毫秒）。</param>
/// <param name="CompletedActions">完成的行动数。</param>
/// <param name="SectionCount">输出的 section 数量。</param>
/// <param name="InputBytes">输入总字节数。</param>
/// <param name="OutputBytes">输出总字节数。</param>
public sealed record ComputeAdvanceMetrics(
    long ElapsedMilliseconds,
    long CompletedActions,
    long SectionCount,
    long InputBytes,
    long OutputBytes);

/// <summary>
/// 冷挂机 section 载荷在存储编码与 compute.v1 线编码之间的转换边界。
/// 存储侧 codec 为 json/br-json、checksum 针对解压后的原始字节；
/// 线侧 codec 为 identity/brotli、节点回传的 checksum 针对编码后字节，写回前需按存储口径重算。
/// </summary>
public static class ComputeSectionCodec
{
    /// <summary>把持久化 section 映射为 compute.v1 线载荷；校验和沿用存储口径（原始字节 SHA-256）。</summary>
    public static ComputeSectionPayload ToWire(StoredSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var codec = section.Codec switch
        {
            SectionCodec.RawJson => "identity",
            SectionCodec.BrotliJson => "brotli",
            _ => throw new InvalidDataException($"section {section.SectionKind} 存储编码不可外传：{section.Codec}。")
        };
        return new ComputeSectionPayload(
            section.SectionKind,
            section.StateVersion,
            codec,
            Convert.ToBase64String(section.Payload),
            section.Checksum);
    }

    /// <summary>
    /// 把节点回传的线载荷还原为可写入 game.player_state_sections 的 SectionWrite。
    /// 跨边界数据必须完整校验：Base64、编码标识、节点侧编码校验和、解压上限与存储口径校验和全部通过才返回。
    /// </summary>
    public static SectionWrite ToSectionWrite(
        Guid playerId,
        long stateVersion,
        ComputeSectionPayload payload,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!PlayerSectionKinds.IsValid(payload.Kind))
            throw new InvalidDataException($"节点回传未知 section：{payload.Kind}。");
        var codec = payload.Codec switch
        {
            "identity" => SectionCodec.RawJson,
            "brotli" => SectionCodec.BrotliJson,
            _ => throw new InvalidDataException($"节点回传不支持的编码：{payload.Codec}。")
        };
        byte[] encoded;
        try
        {
            encoded = Convert.FromBase64String(payload.PayloadBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"节点回传 section {payload.Kind} Base64 无效。", exception);
        }
        if (encoded.Length == 0 || encoded.Length > 4 * 1024 * 1024)
            throw new InvalidDataException($"节点回传 section {payload.Kind} 载荷大小无效。");
        // 节点侧 checksum 针对编码后字节；不一致说明传输或节点产出已损坏，拒绝落库。
        var encodedChecksum = Convert.ToHexStringLower(SHA256.HashData(encoded));
        if (!string.Equals(encodedChecksum, payload.Checksum, StringComparison.Ordinal))
            throw new InvalidDataException($"节点回传 section {payload.Kind} 校验和不匹配。");
        var raw = SectionCodec.DecodeRaw(codec, encoded);
        var rawChecksum = Convert.ToHexStringLower(SHA256.HashData(raw));
        var write = new SectionWrite(
            playerId,
            payload.Kind,
            stateVersion,
            SectionCodec.CurrentSchemaVersion,
            codec,
            encoded,
            raw.Length,
            rawChecksum,
            updatedAt);
        write.Validate();
        return write;
    }
}
