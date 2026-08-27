namespace DaoHua.ComputeNode;

/// <summary>
/// ComputeNode 与主服务之间唯一的冷挂机批处理契约。该契约只传输可校验的 section 载荷，
/// 不暴露 ECS、Fiber、数据库连接或运行时对象。
/// </summary>
public static class ComputeContract
{
    public const string Version = "compute.v1";
    public const string TokenHeader = "X-Compute-Token";
    public const int DefaultMaxJobs = 128;
    public const int HardMaxJobs = 256;
    public const long MaxBatchBytes = 16L * 1024 * 1024;
    public const int MaxSectionsPerJob = 32;
    public const int MaxPayloadBytesPerSection = 512 * 1024;

    public static object? Validate(AdvanceBatchRequest? batch, int maxJobs)
    {
        if (batch is null)
        {
            return new { code = "INVALID_BODY", message = "请求体不能为空。" };
        }

        if (!string.Equals(batch.ContractVersion, Version, StringComparison.Ordinal))
        {
            return new { code = "UNSUPPORTED_CONTRACT", message = $"只支持 {Version}。" };
        }

        if (batch.Jobs is null || batch.Jobs.Count is < 1 or > HardMaxJobs || batch.Jobs.Count > maxJobs)
        {
            return new { code = "INVALID_BATCH_SIZE", message = $"批次必须包含 1 到 {maxJobs} 个任务。" };
        }

        var totalBytes = 0L;
        foreach (var job in batch.Jobs)
        {
            var error = ValidateJob(job);
            if (error is not null) return error;
            totalBytes += job.Sections.Sum(section => section.PayloadBase64.Length);
            if (totalBytes * 3 > MaxBatchBytes)
            {
                return new { code = "BATCH_TOO_LARGE", message = "批次载荷超过 16 MiB 限制。" };
            }
        }

        return null;
    }

    private static object? ValidateJob(AdvanceJobRequest? job)
    {
        if (job is null || job.JobId == Guid.Empty || job.PlayerId == Guid.Empty)
        {
            return new { code = "INVALID_JOB_ID", message = "JobId 与 PlayerId 必须是非空 UUID。" };
        }

        if (job.SourceStateVersion < 0
            || job.FromUnixMilliseconds < 0
            || job.TargetUnixMilliseconds < job.FromUnixMilliseconds)
        {
            return new { code = "INVALID_TIME_RANGE", message = "状态版本或时间范围无效。" };
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (job.TargetUnixMilliseconds > now + TimeSpan.FromMinutes(5).TotalMilliseconds)
        {
            return new { code = "TARGET_IN_FUTURE", message = "目标时间不能超过当前时间 5 分钟。" };
        }

        if (job.Sections is null || job.Sections.Count is < 1 or > MaxSectionsPerJob)
        {
            return new { code = "INVALID_SECTIONS", message = $"每个任务必须包含 1 到 {MaxSectionsPerJob} 个 section。" };
        }

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
                return new { code = "INVALID_SECTION", message = "section 字段、编码或大小无效。" };
            }
        }

        if (job.Actions is not null && job.Actions.Count > 256)
        {
            return new { code = "INVALID_ACTIONS", message = "单个任务行动意图不得超过 256 条。" };
        }
        if (!string.IsNullOrWhiteSpace(job.IdempotencyKey) && job.IdempotencyKey.Length > 256)
        {
            return new { code = "INVALID_IDEMPOTENCY_KEY", message = "幂等键长度不能超过 256。" };
        }

        return null;
    }
}

public sealed record AdvanceBatchRequest(
    string ContractVersion,
    string? GameDataFingerprint,
    IReadOnlyList<AdvanceJobRequest> Jobs);

public sealed record AdvanceJobRequest(
    Guid JobId,
    Guid PlayerId,
    long SourceStateVersion,
    long FromUnixMilliseconds,
    long TargetUnixMilliseconds,
    IReadOnlyList<SectionPayload> Sections,
    IReadOnlyList<ActionIntent>? Actions = null,
    string? IdempotencyKey = null);

public sealed record SectionPayload(
    string Kind,
    long Version,
    string Codec,
    string PayloadBase64,
    string Checksum);

public sealed record ActionIntent(
    string ActionId,
    double DurationSeconds,
    int RepeatCount = 1);

public sealed record AdvanceBatchResponse(
    string ContractVersion,
    string GameDataFingerprint,
    long ServerTimeUnixMilliseconds,
    IReadOnlyList<AdvanceJobResult> Jobs);

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
    public static AdvanceJobResult Failed(
        AdvanceJobRequest job,
        string code,
        string message,
        bool retryable = false) =>
        new(job.JobId, job.PlayerId, false, job.SourceStateVersion, job.SourceStateVersion,
            null, null, code, message, retryable);
}

public sealed record ComputeAdvanceMetrics(
    long ElapsedMilliseconds,
    long CompletedActions,
    long SectionCount,
    long InputBytes,
    long OutputBytes);
