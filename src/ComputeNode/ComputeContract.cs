using Domain.Persistence;

namespace DaoHua.ComputeNode;

/// <summary>
/// ComputeNode 与主服务之间唯一的冷挂机批处理契约。该契约只传输可校验的 section 载荷，
/// 不暴露 ECS、Fiber、数据库连接或运行时对象。
///
/// v2 相对 v1：节点改为执行真实领域模拟（DecodeAggregate → EcsWorldShard 实体装配 →
/// EcsGameRuntime.RunPlayerOnceAsync → EncodeAggregate），任务因此必须携带完整玩家身份、
/// 提交头快照与资产余额快照；结果回传资产增量行由主服在落库事务内经经济事务入账。
/// v1 的 Actions/ActionIntent 行动意图字段随假补算路径一并删除。
/// checksum 口径：请求侧针对「解码后原始 JSON 字节」（提交头绑定口径），
/// 响应侧针对「编码后线字节」（传输完整性），存储口径校验和由主服解压后重算。
/// </summary>
public static class ComputeContract
{
    /// <summary>当前支持的契约版本字符串。</summary>
    public const string Version = "compute.v2";
    /// <summary>内部鉴权令牌的 HTTP 请求头名称。</summary>
    public const string TokenHeader = "X-Compute-Token";
    /// <summary>默认每批次最大任务数。</summary>
    public const int DefaultMaxJobs = 128;
    /// <summary>每批次任务数的绝对上限。</summary>
    public const int HardMaxJobs = 256;
    /// <summary>单个批次请求体的最大字节数。</summary>
    public const long MaxBatchBytes = 16L * 1024 * 1024;
    /// <summary>每个任务允许包含的最大 section 数量（v2 实际载荷恒等于 <see cref="PlayerSectionKinds.All"/> 全集）。</summary>
    public const int MaxSectionsPerJob = 32;
    /// <summary>单个 section 载荷解压后的最大字节数。</summary>
    public const int MaxPayloadBytesPerSection = 512 * 1024;
    /// <summary>单任务资产余额快照的最大行数（与主服 ComputeDispatchContract.MaxAssetBalancesPerJob 一致）。</summary>
    public const int MaxAssetBalancesPerJob = 1024;
    /// <summary>单任务资产增量回传的最大行数（与主服 ComputeDispatchContract.MaxAssetDeltasPerResult 一致）。</summary>
    public const int MaxAssetDeltasPerResult = 512;
    /// <summary>玩家显示名长度上限（与 PlayerAggregate 构造器不变量一致，改名规则另由领域层裁决）。</summary>
    private const int MaxDisplayNameLength = 64;

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
        // 资产余额行按字段实长估算（主服 ComputeBatchBudget.EstimateDecodedBytes 同口径）。
        var totalBytes = 0L;
        foreach (var job in batch.Jobs)
        {
            if (job?.Sections is not null)
                totalBytes += job.Sections.Sum(section => (long)(section?.PayloadBase64?.Length ?? 0)) * 3 / 4;
            if (job?.AssetBalances is { Count: > 0 } balances)
                totalBytes += balances.Sum(row => 32L + (row?.AssetId?.Length ?? 0) + (row?.Variant?.Length ?? 0));
            if (totalBytes > MaxBatchBytes)
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
    /// 校验单个任务请求、身份/提交头字段、section 集合、余额快照与时间范围是否合法。
    /// </summary>
    /// <param name="job">单个任务请求。</param>
    /// <returns>若校验通过返回 <see lang="null"/>；否则返回违规信息。</returns>
    public static ContractViolation? ValidateJob(AdvanceJobRequest? job)
    {
        if (job is null || job.JobId == Guid.Empty || job.PlayerId == Guid.Empty || job.AccountId == Guid.Empty)
        {
            return new ContractViolation("INVALID_JOB_ID", "JobId、PlayerId 与 AccountId 必须是非空 UUID。");
        }

        if (string.IsNullOrWhiteSpace(job.DisplayName) || job.DisplayName.Length > MaxDisplayNameLength
            || job.CreatedAtUnixMilliseconds < 0)
        {
            return new ContractViolation("INVALID_IDENTITY", "玩家显示名或创建时刻无效。");
        }

        // SourceStateVersion 必须为正：DecodeAggregate 拒绝空头（head.StateVersion <= 0），
        // 契约层先行拦下，避免把必然失败的任务送进引擎。
        if (job.SourceStateVersion < 1
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

        // 提交头校验和与存档指纹是聚合还原与完整性核验的必备输入。
        if (string.IsNullOrWhiteSpace(job.HeadChecksum) || job.HeadChecksum.Length != 64
            || job.HeadChecksum.Any(character => !Uri.IsHexDigit(character))
            || string.IsNullOrWhiteSpace(job.HeadGameDataFingerprint) || job.HeadGameDataFingerprint.Length > 128)
        {
            return new ContractViolation("INVALID_HEAD", "提交头校验和或存档配表指纹无效。");
        }

        // v2 节点执行真实模拟：必须携带完整 8 段载荷，缺段/多段都无法还原聚合。
        if (job.Sections is null || job.Sections.Count != PlayerSectionKinds.All.Count)
        {
            return new ContractViolation("INVALID_SECTIONS", $"每个任务必须恰好包含 {PlayerSectionKinds.All.Count} 个 section。");
        }

        // 同一任务内 section kind 必须唯一且覆盖全集：重复 kind 会让还原与主服写回都无法确定版本归属。
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in job.Sections)
        {
            if (section is null
                || string.IsNullOrWhiteSpace(section.Kind)
                || !PlayerSectionKinds.IsValid(section.Kind)
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

        // 资产余额快照：可选但若有则必须逐行合法；(assetId, variant) 重复会破坏钱包还原的唯一键。
        if (job.AssetBalances is { } balances)
        {
            if (balances.Count > MaxAssetBalancesPerJob)
            {
                return new ContractViolation("INVALID_BALANCES", $"资产余额行数不得超过 {MaxAssetBalancesPerJob}。");
            }
            var keys = new HashSet<(string, string)>();
            foreach (var row in balances)
            {
                if (row is null
                    || string.IsNullOrWhiteSpace(row.AssetId) || row.AssetId.Length > 128
                    || row.Variant is null || row.Variant.Length > 128
                    || !double.IsFinite(row.Quantity) || row.Quantity < 0
                    || !keys.Add((row.AssetId, row.Variant)))
                {
                    return new ContractViolation("INVALID_BALANCES", "资产余额行字段无效或键重复。");
                }
            }
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
/// 身份与提交头字段与 8 段载荷一起构成还原 PlayerAggregate 的完整输入；
/// 模拟推进所需的一切领域语义都装在 section 载荷内。
/// </summary>
/// <param name="JobId">任务唯一标识。</param>
/// <param name="PlayerId">玩家唯一标识。</param>
/// <param name="AccountId">玩家所属账号标识（聚合还原所需身份）。</param>
/// <param name="DisplayName">玩家显示名（聚合还原所需身份）。</param>
/// <param name="CreatedAtUnixMilliseconds">角色创建时刻（毫秒）。</param>
/// <param name="SourceStateVersion">源状态版本号（提交头 CAS 基准）。</param>
/// <param name="FromUnixMilliseconds">起始模拟时刻（毫秒），即提交头 last_simulated_at。</param>
/// <param name="TargetUnixMilliseconds">目标模拟时刻（毫秒）。</param>
/// <param name="HeadChecksum">任务发起时提交头 checksum：绑定 8 段载荷完整性。</param>
/// <param name="HeadGameDataFingerprint">存档写入时的配表指纹；还原后经 RebindGameDataFingerprint 对齐当前配表。</param>
/// <param name="Sections">8 段完整状态 section 载荷（checksum 为原始 JSON SHA-256）。</param>
/// <param name="AssetBalances">玩家当前正余额快照（quantity &gt; 0），用于钱包还原。</param>
/// <param name="IdempotencyKey">可选的幂等键。</param>
public sealed record AdvanceJobRequest(
    Guid JobId,
    Guid PlayerId,
    Guid AccountId,
    string DisplayName,
    long CreatedAtUnixMilliseconds,
    long SourceStateVersion,
    long FromUnixMilliseconds,
    long TargetUnixMilliseconds,
    string HeadChecksum,
    string HeadGameDataFingerprint,
    IReadOnlyList<SectionPayload> Sections,
    IReadOnlyList<AssetBalanceRow>? AssetBalances = null,
    string? IdempotencyKey = null);

/// <summary>
/// 单个状态 section 载荷。
/// </summary>
/// <param name="Kind">section 类型标识。</param>
/// <param name="Version">section 当前版本号。</param>
/// <param name="Codec">载荷编码，支持 identity 与 brotli。</param>
/// <param name="PayloadBase64">Base64 编码的载荷。</param>
/// <param name="Checksum">checksum 口径按方向约定：请求侧为「解码后原始 JSON 字节」的 SHA-256（提交头绑定口径），
/// 响应侧为「编码后线字节」的 SHA-256（传输完整性）。</param>
public sealed record SectionPayload(
    string Kind,
    long Version,
    string Codec,
    string PayloadBase64,
    string Checksum);

/// <summary>任务输入携带的一行资产余额快照（quantity &gt; 0 的可用余额）。</summary>
/// <param name="AssetId">资产标识（物品/货币定义 ID）。</param>
/// <param name="Variant">资产变体；普通资产为空串。</param>
/// <param name="Quantity">当前可用余额。</param>
public sealed record AssetBalanceRow(string AssetId, string Variant, double Quantity);

/// <summary>结果回传的一行资产增量；主服按现有经济事务口径原子入账。</summary>
/// <param name="AssetId">资产标识（物品/货币定义 ID）。</param>
/// <param name="Variant">资产变体；普通资产为空串。</param>
/// <param name="QuantityDelta">增减量（double，负数为扣减）。</param>
public sealed record AssetDeltaRow(string AssetId, string Variant, double QuantityDelta);

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
/// <param name="TargetStateVersion">目标状态版本号；无实质推进时与源版本相同（仅推进模拟时钟）。</param>
/// <param name="Sections">推进后发生变更的 section 载荷；无变更时为空数组，失败时为 <see langword="null"/>。</param>
/// <param name="AssetDeltas">推进结算出的资产增量行（QuantityDelta 非零）；无增量或失败时为 <see langword="null"/>。</param>
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
    IReadOnlyList<AssetDeltaRow>? AssetDeltas,
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
        // 失败结果不带回任何 section/资产增量，TargetStateVersion 保持与源版本一致，向主服务声明本任务未发生任何推进。
        new(job.JobId, job.PlayerId, false, job.SourceStateVersion, job.SourceStateVersion,
            null, null, null, code, message, retryable);
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
