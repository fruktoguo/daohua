namespace DaoHua.ComputeNode;

/// <summary>
/// ComputeNode 与主服务之间唯一的冷挂机批处理契约。该契约只传输可校验的 section 载荷，
/// 不暴露 ECS、Fiber、数据库连接或运行时对象。
/// </summary>
public static class ComputeContract
{
    /// <summary>当前支持的契约版本字符串。</summary>
    public const string Version = "compute.v1";
    /// <summary>内部鉴权令牌的 HTTP 请求头名称。</summary>
    public const string TokenHeader = "X-Compute-Token";
    /// <summary>默认每批次最大任务数。</summary>
    public const int DefaultMaxJobs = 128;
    /// <summary>每批次任务数的绝对上限。</summary>
    public const int HardMaxJobs = 256;
    /// <summary>单个批次请求体的最大字节数。</summary>
    public const long MaxBatchBytes = 16L * 1024 * 1024;
    /// <summary>每个任务允许包含的最大 section 数量。</summary>
    public const int MaxSectionsPerJob = 32;
    /// <summary>单个 section 载荷解压后的最大字节数。</summary>
    public const int MaxPayloadBytesPerSection = 512 * 1024;

    /// <summary>
    /// 批次级契约校验：请求体、版本、任务数与解码后字节总量。
    /// 这一级违规代表主服装配/链路问题，与单个任务载荷无关——
    /// 调用方必须把整批按可重试退回，不得连坐判死单任务。
    /// </summary>
    /// <param name="batch">批次请求。</param>
    /// <param name="maxJobs">当前配置的单批次最大任务数。</param>
    /// <returns>若校验通过返回 <see langword="null"/>；否则返回违规信息。</returns>
    public static ContractViolation? ValidateBatch(AdvanceBatchRequest? batch, int maxJobs)
    {
        if (batch is null)
        {
            return new ContractViolation("INVALID_BODY", "请求体不能为空。");
        }

        if (!string.Equals(batch.ContractVersion, Version, StringComparison.Ordinal))
        {
            return new ContractViolation("UNSUPPORTED_CONTRACT", $"只支持 {Version}。");
        }

        if (batch.Jobs is null || batch.Jobs.Count is < 1 or > HardMaxJobs || batch.Jobs.Count > maxJobs)
        {
            return new ContractViolation("INVALID_BATCH_SIZE", $"批次必须包含 1 到 {maxJobs} 个任务。");
        }

        // Base64 解码体积约为字符数的 3/4；这里直接用字符数做廉价的批次总量估算，
        // 在任何一段被实际解码/解压前就拒绝超限批次，避免被压缩载荷放大内存占用。
        // 估算口径必须就是解码后字节：totalBytes * 3 / 4（不是 * 3——那会把上限误卡到 ~1/4 处）。
        var totalBytes = 0L;
        foreach (var job in batch.Jobs)
        {
            if (job?.Sections is null) continue;
            totalBytes += job.Sections.Sum(section => (long)(section?.PayloadBase64?.Length ?? 0));
            if (totalBytes * 3 / 4 > MaxBatchBytes)
            {
                return new ContractViolation("BATCH_TOO_LARGE", "批次载荷超过 16 MiB 限制。");
            }
        }

        return null;
    }

    /// <summary>
    /// 校验整个离线批处理请求（HTTP 调试口口径）：批次级 + 逐任务首个违规。
    /// </summary>
    /// <param name="batch">批次请求。</param>
    /// <param name="maxJobs">当前配置的单批次最大任务数。</param>
    /// <returns>若校验通过返回 <see langword="null"/>；否则返回违规信息。</returns>
    public static ContractViolation? Validate(AdvanceBatchRequest? batch, int maxJobs)
    {
        var violation = ValidateBatch(batch, maxJobs);
        if (violation is not null) return violation;
        foreach (var job in batch!.Jobs)
        {
            var error = ValidateJob(job);
            if (error is not null) return error;
        }
        return null;
    }

    /// <summary>
    /// 校验单个任务请求、section 集合与时间范围是否合法。
    /// </summary>
    /// <param name="job">单个任务请求。</param>
    /// <returns>若校验通过返回 <see lang="null"/>；否则返回违规信息。</returns>
    public static ContractViolation? ValidateJob(AdvanceJobRequest? job)
    {
        if (job is null || job.JobId == Guid.Empty || job.PlayerId == Guid.Empty)
        {
            return new ContractViolation("INVALID_JOB_ID", "JobId 与 PlayerId 必须是非空 UUID。");
        }

        if (job.SourceStateVersion < 0
            || job.FromUnixMilliseconds < 0
            || job.TargetUnixMilliseconds < job.FromUnixMilliseconds)
        {
            return new ContractViolation("INVALID_TIME_RANGE", "状态版本或时间范围无效。");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // 节点只补算已经流逝的时间；目标时间落在未来意味着主服务时钟漂移或请求被伪造，
        // 仅容忍 5 分钟时钟偏差，超出直接拒绝而不是截断。
        if (job.TargetUnixMilliseconds > now + TimeSpan.FromMinutes(5).TotalMilliseconds)
        {
            return new ContractViolation("TARGET_IN_FUTURE", "目标时间不能超过当前时间 5 分钟。");
        }

        if (job.Sections is null || job.Sections.Count is < 1 or > MaxSectionsPerJob)
        {
            return new ContractViolation("INVALID_SECTIONS", $"每个任务必须包含 1 到 {MaxSectionsPerJob} 个 section。");
        }

        // 同一任务内 section kind 必须唯一：重复 kind 会让主服务写回时无法确定哪个版本为准。
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in job.Sections)
        {
            if (section is null
                || string.IsNullOrWhiteSpace(section.Kind)
                || section.Kind.Length > 64
                || !kinds.Add(section.Kind)
                || section.Version < 0
                || string.IsNullOrWhiteSpace(section.PayloadBase64)
                || section.PayloadBase64.Length > MaxPayloadBytesPerSection * 2
                || string.IsNullOrWhiteSpace(section.Checksum)
                || section.Checksum.Length != 64
                || section.Codec is not ("identity" or "brotli"))
            {
                return new ContractViolation("INVALID_SECTION", "section 字段、编码或大小无效。");
            }
        }

        if (job.Actions is not null && job.Actions.Count > 256)
        {
            return new ContractViolation("INVALID_ACTIONS", "单个任务行动意图不得超过 256 条。");
        }
        if (!string.IsNullOrWhiteSpace(job.IdempotencyKey) && job.IdempotencyKey.Length > 256)
        {
            return new ContractViolation("INVALID_IDEMPOTENCY_KEY", "幂等键长度不能超过 256。");
        }

        return null;
    }
}

/// <summary>契约校验违规：错误码与消息；HTTP 调试口按 {code,message} 线形状回 400。</summary>
/// <param name="Code">错误码。</param>
/// <param name="Message">错误消息。</param>
public sealed record ContractViolation(string Code, string Message);

/// <summary>
/// 批次推进请求。
/// </summary>
/// <param name="ContractVersion">请求方使用的契约版本。</param>
/// <param name="GameDataFingerprint">请求方感知的游戏数据指纹，可选。</param>
/// <param name="Jobs">待推进的离线任务列表。</param>
public sealed record AdvanceBatchRequest(
    string ContractVersion,
    string? GameDataFingerprint,
    IReadOnlyList<AdvanceJobRequest> Jobs);

/// <summary>
/// 单个离线任务推进请求。
/// </summary>
/// <param name="JobId">任务唯一标识。</param>
/// <param name="PlayerId">玩家唯一标识。</param>
/// <param name="SourceStateVersion">源状态版本号。</param>
/// <param name="FromUnixMilliseconds">起始时间戳（毫秒）。</param>
/// <param name="TargetUnixMilliseconds">目标时间戳（毫秒）。</param>
/// <param name="Sections">待推进的状态 section 载荷列表。</param>
/// <param name="Actions">可选的行动意图列表。</param>
/// <param name="IdempotencyKey">可选的幂等键。</param>
public sealed record AdvanceJobRequest(
    Guid JobId,
    Guid PlayerId,
    long SourceStateVersion,
    long FromUnixMilliseconds,
    long TargetUnixMilliseconds,
    IReadOnlyList<SectionPayload> Sections,
    IReadOnlyList<ActionIntent>? Actions = null,
    string? IdempotencyKey = null);

/// <summary>
/// 单个状态 section 载荷。
/// </summary>
/// <param name="Kind">section 类型标识。</param>
/// <param name="Version">section 当前版本号。</param>
/// <param name="Codec">载荷编码，支持 identity 与 brotli。</param>
/// <param name="PayloadBase64">Base64 编码的载荷。</param>
/// <param name="Checksum">原始载荷的 SHA256 校验和。</param>
public sealed record SectionPayload(
    string Kind,
    long Version,
    string Codec,
    string PayloadBase64,
    string Checksum);

/// <summary>
/// 离线阶段要执行的行动意图。
/// </summary>
/// <param name="ActionId">行动标识。</param>
/// <param name="DurationSeconds">单次行动持续时间（秒）。</param>
/// <param name="RepeatCount">重复次数，默认为 1；节点只按有界次数估算可完成数，非正值（含领域侧的 -1 无限语义）不计入完成量</param>
public sealed record ActionIntent(
    string ActionId,
    double DurationSeconds,
    int RepeatCount = 1);

/// <summary>
/// 批次推进响应。
/// </summary>
/// <param name="ContractVersion">实际使用的契约版本。</param>
/// <param name="GameDataFingerprint">计算节点当前游戏数据指纹。</param>
/// <param name="ServerTimeUnixMilliseconds">服务端响应时间戳（毫秒）。</param>
/// <param name="Jobs">每个任务的推进结果。</param>
public sealed record AdvanceBatchResponse(
    string ContractVersion,
    string GameDataFingerprint,
    long ServerTimeUnixMilliseconds,
    IReadOnlyList<AdvanceJobResult> Jobs);

/// <summary>
/// 单个任务的推进结果。
/// </summary>
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
public sealed record AdvanceJobResult(
    Guid JobId,
    Guid PlayerId,
    bool Success,
    long SourceStateVersion,
    long TargetStateVersion,
    IReadOnlyList<SectionPayload>? Sections,
    ComputeAdvanceMetrics? Metrics,
    string? ErrorCode,
    string? ErrorMessage,
    bool Retryable)
{
    /// <summary>
    /// 构造一个表示失败的推进结果。
    /// </summary>
    /// <param name="job">原始任务请求。</param>
    /// <param name="code">错误码。</param>
    /// <param name="message">错误消息。</param>
    /// <param name="retryable">失败是否可重试，默认为 <see langword="false"/>。</param>
    /// <returns>失败的推进结果实例。</returns>
    public static AdvanceJobResult Failed(
        AdvanceJobRequest job,
        string code,
        string message,
        bool retryable = false) =>
        // 失败结果不带回任何 section，TargetStateVersion 保持与源版本一致，向主服务声明本任务未发生任何推进。
        new(job.JobId, job.PlayerId, false, job.SourceStateVersion, job.SourceStateVersion,
            null, null, code, message, retryable);
}

/// <summary>
/// 推进计算指标。
/// </summary>
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
