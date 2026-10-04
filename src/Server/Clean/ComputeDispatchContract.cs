namespace Server.Clean;

/// <summary>
/// 主服务侧对 compute.v2 冷挂机批处理契约的镜像定义。
/// ComputeNode 是无共享宿主依赖的独立进程（仅引用 Domain 程序集），无法共享本程序集；
/// 两侧通过 JSON 字段名对齐，序列化一律使用 ServerJson.Options（camelCase），
/// 与节点端 JsonSerializerDefaults.Web 线格式一致；字段集对齐由 Server.Tests 单测门禁。
///
/// v2 相对 v1 的差异：
/// 1. 任务携带完整玩家身份与提交头（AccountId/DisplayName/CreatedAtUnixMilliseconds/
///    HeadChecksum/HeadGameDataFingerprint），节点据此重建 PlayerStateBundle 并还原聚合；
/// 2. 任务携带资产余额快照（AssetBalances），节点钱包还原使消耗品/材料可用性判定与在线一致；
/// 3. 结果新增 AssetDeltas：节点结算出的资产增量由主服在结果落库事务内经经济事务原子入账；
/// 4. 移除 v1 的 Actions/ActionIntent 行动意图字段——真实模拟直接从 action_queue section 推进；
/// 5. checksum 口径统一写死：请求侧针对「解码后原始 JSON 字节」（提交头绑定口径），
///    响应侧针对「编码后线字节」（传输完整性），存储口径校验和由主服解压后重算。
/// </summary>
public static class ComputeDispatchContract
{
    /// <summary>契约版本字符串，必须与 ComputeNode 端 compute.v2 完全一致。</summary>
    public const string Version = "compute.v2";
    /// <summary>内部鉴权令牌的 HTTP 请求头名称。</summary>
    public const string TokenHeader = "X-Compute-Token";
    /// <summary>批处理端点相对路径。</summary>
    public const string AdvanceBatchPath = "/internal/v1/compute/advance-batch";
    /// <summary>每个任务允许包含的最大 section 数量（与节点上限一致；v2 实际载荷恒等于全集 8 段）。</summary>
    public const int MaxSectionsPerJob = 32;
    /// <summary>节点单批次任务数的绝对上限（与节点 ComputeContract.HardMaxJobs 一致）。</summary>
    public const int HardMaxJobsPerBatch = 256;
    /// <summary>节点侧单批次解码后字节硬上限（与节点 ComputeContract.MaxBatchBytes 一致）；
    /// 超过该上限的任务在任何节点上都无法通过契约校验。</summary>
    public const long MaxBatchBytes = 16L * 1024 * 1024;
    /// <summary>单任务资产余额快照的最大行数（与节点校验口径一致）。</summary>
    public const int MaxAssetBalancesPerJob = 1024;
    /// <summary>单任务资产增量回传的最大行数（与经济事务单行数上限一致）。</summary>
    public const int MaxAssetDeltasPerResult = 512;
}

/// <summary>compute.v2 批次推进请求。</summary>
/// <param name="ContractVersion">契约版本。</param>
/// <param name="GameDataFingerprint">主服务当前游戏数据指纹。</param>
/// <param name="Jobs">待推进的离线任务列表。</param>
public sealed record ComputeAdvanceBatchRequest(
    string ContractVersion,
    string? GameDataFingerprint,
    IReadOnlyList<ComputeAdvanceJobRequest> Jobs);

/// <summary>
/// compute.v2 单个离线任务推进请求（compute_jobs.input_payload 的序列化形态）。
/// 身份与提交头字段与 8 段载荷一起构成节点还原 PlayerAggregate 的完整输入；
/// 模拟推进所需的一切领域语义都装在 section 载荷内，不再有旁路行动意图。
/// </summary>
/// <param name="JobId">任务唯一标识。</param>
/// <param name="PlayerId">玩家唯一标识。</param>
/// <param name="AccountId">玩家所属账号标识（聚合还原所需身份）。</param>
/// <param name="DisplayName">玩家显示名（聚合还原所需身份）。</param>
/// <param name="CreatedAtUnixMilliseconds">角色创建时刻（毫秒）；DecodeAggregate 对 lastSimulatedAt 下限取 max(createdAt)。</param>
/// <param name="SourceStateVersion">源状态版本号（提交头 CAS 基准）。</param>
/// <param name="FromUnixMilliseconds">起始模拟时刻（毫秒），即提交头 last_simulated_at。</param>
/// <param name="TargetUnixMilliseconds">目标模拟时刻（毫秒）。</param>
/// <param name="HeadChecksum">任务发起时提交头 checksum：绑定 8 段载荷完整性，节点据此校验后拒绝残缺输入。</param>
/// <param name="HeadGameDataFingerprint">存档写入时的配表指纹；节点还原后经 RebindGameDataFingerprint 对齐当前配表，与主服行为一致。</param>
/// <param name="Sections">8 段完整状态 section 载荷（存储编码映射为线编码，checksum 为原始 JSON SHA-256）。</param>
/// <param name="AssetBalances">玩家当前正余额快照（quantity &gt; 0），节点据此还原钱包做消耗品/材料可用性判定。</param>
/// <param name="IdempotencyKey">可选的幂等键。</param>
public sealed record ComputeAdvanceJobRequest(
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
    IReadOnlyList<ComputeSectionPayload> Sections,
    IReadOnlyList<ComputeAssetBalance>? AssetBalances = null,
    string? IdempotencyKey = null);

/// <summary>compute.v2 单个状态 section 载荷。</summary>
/// <param name="Kind">section 类型标识。</param>
/// <param name="Version">section 当前版本号。</param>
/// <param name="Codec">载荷编码，线上为 identity 或 brotli。</param>
/// <param name="PayloadBase64">Base64 编码的（可能压缩的）载荷。</param>
/// <param name="Checksum">checksum 口径按方向约定：请求侧为「解码后原始 JSON 字节」的 SHA-256（提交头绑定口径），
/// 响应侧为「编码后线字节」的 SHA-256（传输完整性）。</param>
public sealed record ComputeSectionPayload(
    string Kind,
    long Version,
    string Codec,
    string PayloadBase64,
    string Checksum);

/// <summary>compute.v2 任务输入携带的一行资产余额快照（quantity &gt; 0 的可用余额）。</summary>
/// <param name="AssetId">资产标识（物品/货币定义 ID）。</param>
/// <param name="Variant">资产变体；普通资产为空串。</param>
/// <param name="Quantity">当前可用余额。</param>
public sealed record ComputeAssetBalance(string AssetId, string Variant, double Quantity);

/// <summary>compute.v2 结果回传的一行资产增量；主服按现有经济事务口径原子入账。</summary>
/// <param name="AssetId">资产标识（物品/货币定义 ID）。</param>
/// <param name="Variant">资产变体；普通资产为空串。</param>
/// <param name="QuantityDelta">增减量（double，负数为扣减）。</param>
public sealed record ComputeAssetDelta(string AssetId, string Variant, double QuantityDelta);

/// <summary>compute.v2 批次推进响应。</summary>
/// <param name="ContractVersion">实际使用的契约版本。</param>
/// <param name="GameDataFingerprint">计算节点当前游戏数据指纹。</param>
/// <param name="ServerTimeUnixMilliseconds">节点响应时间戳（毫秒）。</param>
/// <param name="Jobs">每个任务的推进结果。</param>
public sealed record ComputeAdvanceBatchResponse(
    string ContractVersion,
    string GameDataFingerprint,
    long ServerTimeUnixMilliseconds,
    IReadOnlyList<ComputeAdvanceJobResult> Jobs);

/// <summary>compute.v2 单个任务推进结果。</summary>
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
public sealed record ComputeAdvanceJobResult(
    Guid JobId,
    Guid PlayerId,
    bool Success,
    long SourceStateVersion,
    long TargetStateVersion,
    IReadOnlyList<ComputeSectionPayload>? Sections,
    IReadOnlyList<ComputeAssetDelta>? AssetDeltas,
    ComputeAdvanceMetrics? Metrics,
    string? ErrorCode,
    string? ErrorMessage,
    bool Retryable);

/// <summary>compute.v2 推进计算指标。</summary>
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
/// 批次装配的解码后字节预算口径：section 按 Base64 字符数 ×3/4 ≈ 解码后字节，
/// 资产余额行按字段实长估算，与节点 ComputeContract.ValidateBatch 的估算公式逐字一致。
/// </summary>
public static class ComputeBatchBudget
{
    /// <summary>估算单个任务请求的解码后字节数（PayloadBase64 为 null 的非法载荷按 0 计）。</summary>
    public static long EstimateDecodedBytes(ComputeAdvanceJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = request.Sections.Sum(section => (long)(section?.PayloadBase64?.Length ?? 0)) * 3 / 4;
        if (request.AssetBalances is { Count: > 0 } balances)
            bytes += balances.Sum(row => 32L + (row?.AssetId?.Length ?? 0) + (row?.Variant?.Length ?? 0));
        return bytes;
    }

    /// <summary>
    /// 按预算贪心装批：保持输入顺序，装得下的进返回元组的 Fits，放不下的进 Overflow。
    /// 单个元素超过预算时也进 Overflow，由调用方决定退回或判死。
    /// </summary>
    public static (List<T> Fits, List<T> Overflow) Pack<T>(
        IReadOnlyList<T> items, Func<T, long> measure, long budget)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(measure);
        var fits = new List<T>(items.Count);
        var overflow = new List<T>();
        long used = 0;
        foreach (var item in items)
        {
            var size = measure(item);
            if (used + size > budget) overflow.Add(item);
            else
            {
                fits.Add(item);
                used += size;
            }
        }
        return (fits, overflow);
    }
}

/// <summary>
/// 冷挂机 section 载荷在存储编码与 compute.v2 线编码之间的转换边界。
/// 存储侧 codec 为 json/br-json、checksum 针对解压后的原始字节；
/// 线侧 codec 为 identity/brotli、节点回传的 checksum 针对编码后字节，写回前需按存储口径重算。
/// </summary>
public static class ComputeSectionCodec
{
    /// <summary>把持久化 section 映射为 compute.v2 线载荷；校验和沿用存储口径（原始字节 SHA-256）。</summary>
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
