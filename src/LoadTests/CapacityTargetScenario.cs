namespace DaoHua.LoadTests;

/// <summary>
/// 容量目标综合压测场景。
/// 结合账号存储估算、网络峰值估算、本地计算与 PostgreSQL 真实探测，
/// 验证 2c4g-40g 预算下的容量目标。
/// </summary>
public static class CapacityTargetScenario
{
    /// <summary>目标注册账号数。</summary>
    public const int RegisteredAccountTarget = 5_000;
    /// <summary>目标持续在线并发玩家数。</summary>
    public const int SustainedNetworkTarget = 100;
    /// <summary>目标峰值网络并发玩家数。</summary>
    public const int PeakNetworkTarget = 500;
    /// <summary>目标冷挂机同时补算玩家数。</summary>
    public const int ColdComputeTarget = 2_000;
    /// <summary>数据盘容量预算（字节）。</summary>
    public const long DataDiskBudgetBytes = 12L * 1024 * 1024 * 1024;
    /// <summary>WAL 容量预算（字节）。</summary>
    public const long WalBudgetBytes = 2L * 1024 * 1024 * 1024;
    /// <summary>本地日志容量预算（字节）。</summary>
    public const long LocalLogBudgetBytes = 100L * 1024 * 1024;
    /// <summary>峰值网络带宽预算（字节/秒）。</summary>
    public const long PeakNetworkBudgetBytesPerSecond = 512L * 1024;
    /// <summary>冷挂机补算时间预算（毫秒）。</summary>
    public const double ColdComputeBudgetMilliseconds = 30_000;
    /// <summary>进程工作集内存预算（字节）。</summary>
    public const long ProcessWorkingSetBudgetBytes = 768L * 1024 * 1024;

    /// <summary>
    /// 执行完整的容量目标压测。
    /// </summary>
    /// <param name="options">压测选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>容量目标压测报告。</returns>
    public static async Task<LoadTestReport> RunAsync(LoadTestOptions options, CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        // 先强制回收再取基线，使内存增量口径只反映本次压测自身分配。
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var started = Stopwatch.GetTimestamp();
        var warnings = new List<string>();
        var errors = 0;

        var storage = CapacitySynthetic.EstimateStorage(RegisteredAccountTarget);
        var network = CapacitySynthetic.EstimatePeakNetwork(PeakNetworkTarget);
        var compute = await ComputeLoadScenario.RunLocalAsync(
            ColdComputeTarget,
            cancellationToken);

        PostgresProbeResult? postgres = null;
        try
        {
            postgres = await PostgresCapacityProbe.RunAsync(options, cancellationToken);
            if (postgres.DataBytes > DataDiskBudgetBytes || postgres.WalBytes > WalBudgetBytes) errors++;
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            if (options.RequireDatabase)
            {
                errors++;
                warnings.Add($"PostgreSQL 真实门禁失败: {exception.Message}");
            }
            else
            {
                warnings.Add($"未执行 PostgreSQL 真实门禁（可用 --require-database 强制失败）: {exception.Message}");
            }
        }

        if (storage.EstimatedBytes > DataDiskBudgetBytes) errors++;
        if (network.BytesPerSecond > PeakNetworkBudgetBytesPerSecond) errors++;
        if (compute.ElapsedMilliseconds > ColdComputeBudgetMilliseconds) errors++;

        process.Refresh();
        var workingSet = process.WorkingSet64;
        var managedAfter = GC.GetTotalMemory(forceFullCollection: false);
        if (workingSet > ProcessWorkingSetBudgetBytes) errors++;

        var elapsed = Stopwatch.GetElapsedTime(started);
        return new LoadTestReport(
            "loadtest.v1",
            "capacity-target",
            RegisteredAccountTarget,
            options.StateChangeSeconds,
            elapsed.TotalMilliseconds,
            (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            managedBefore,
            managedAfter,
            workingSet,
            network.BytesPerSecond,
            0,
            PeakNetworkTarget,
            0,
            errors,
            errors == 0,
            new Dictionary<string, object?>
            {
                ["target"] = new
                {
                    registeredAccounts = RegisteredAccountTarget,
                    sustainedNetworkPlayers = SustainedNetworkTarget,
                    peakNetworkPlayers = PeakNetworkTarget,
                    coldComputePlayers = ColdComputeTarget,
                    serverClass = "2c4g-40g"
                },
                ["budget"] = new
                {
                    dataDiskBytes = DataDiskBudgetBytes,
                    walBytes = WalBudgetBytes,
                    localLogBytes = LocalLogBudgetBytes,
                    peakNetworkBytesPerSecond = PeakNetworkBudgetBytesPerSecond,
                    coldComputeMilliseconds = ColdComputeBudgetMilliseconds,
                    processWorkingSetBytes = ProcessWorkingSetBudgetBytes
                },
                ["syntheticStorage"] = storage,
                ["syntheticNetwork"] = network,
                ["coldCompute"] = compute,
                ["postgres"] = postgres
            },
            warnings);
    }

    /// <summary>
    /// 账号与 section 的存储估算。
    /// </summary>
    /// <param name="AccountBytes">账号表估算字节数。</param>
    /// <param name="SectionBytes">玩家状态 section 估算字节数。</param>
    /// <param name="QueueBytes">队列估算字节数。</param>
    /// <param name="EstimatedBytes">总估算字节数。</param>
    /// <param name="AverageSectionBytes">平均每个 section 字节数。</param>
    private sealed record SyntheticStorageEstimate(
        long AccountBytes,
        long SectionBytes,
        long QueueBytes,
        long EstimatedBytes,
        long AverageSectionBytes);

    /// <summary>
    /// 峰值网络带宽估算。
    /// </summary>
    /// <param name="BytesPerSecond">峰值每秒字节数。</param>
    /// <param name="AverageMessageBytes">平均每条消息字节数。</param>
    /// <param name="PeakPlayers">峰值玩家数。</param>
    private sealed record SyntheticNetworkEstimate(
        long BytesPerSecond,
        long AverageMessageBytes,
        int PeakPlayers);

    /// <summary>
    /// 容量估算辅助方法。
    /// 基于目标玩家数生成确定性的 synthetic 数据并估算存储与网络。
    /// </summary>
    private static class CapacitySynthetic
    {
        /// <summary>
        /// 估算目标账号数的总存储占用。
        /// </summary>
        /// <param name="players">玩家数。</param>
        /// <returns>存储估算结果。</returns>
        public static SyntheticStorageEstimate EstimateStorage(int players)
        {
            long sectionBytes = 0;
            // 近似行宽假设：账号行 ~256B，行动队列按每玩家 6 条 × ~128B 估算；
            // section 行按 48B 行开销 + 实际 synthetic 载荷计。
            // 账号检测：每账号6项信号及索引约3KiB，30天内100次事件约100KiB。
            var accountBytes = players * (256L + 3 * 1024L + 100 * 1024L);
            var queueBytes = players * 6L * 128L;
            for (var index = 0; index < players; index++)
            {
                foreach (var kind in SectionKinds)
                {
                    var payload = CreatePayload(index, kind);
                    sectionBytes += 48 + payload.Length;
                }
            }

            return new SyntheticStorageEstimate(
                accountBytes,
                sectionBytes,
                queueBytes,
                accountBytes + sectionBytes + queueBytes,
                sectionBytes / (long)players / SectionKinds.Length);
        }

        /// <summary>
        /// 估算峰值网络带宽。
        /// </summary>
        /// <param name="players">峰值玩家数。</param>
        /// <returns>网络估算结果。</returns>
        public static SyntheticNetworkEstimate EstimatePeakNetwork(int players)
        {
            long total = 0;
            // 峰值口径：每玩家每秒一帧下行，128B 信封/帧头开销 + 状态载荷。
            for (var index = 0; index < players; index++)
            {
                total += 128 + CreatePayload(index, "state").Length;
            }

            return new SyntheticNetworkEstimate(total, total / players, players);
        }

        /// <summary>
        /// 生成指定玩家与 section 类型的 synthetic JSON 载荷。
        /// </summary>
        /// <param name="index">玩家索引。</param>
        /// <param name="kind">section 类型。</param>
        /// <returns>UTF-8 编码的 JSON 字节。</returns>
        public static byte[] CreatePayload(int index, string kind)
        {
            // 固定 schema 版本与时间戳使载荷字节长度稳定，估算结果跨运行可复现。
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                playerId = DeterministicId(index).ToString("D"),
                section = kind,
                revision = index + 1,
                lastSimulatedAtUnixMilliseconds = 1_800_000_000_000L,
                values = new[] { index % 97, index % 31, index % 11 }
            });
            return payload;
        }

        /// <summary>
        /// 基于索引生成确定性的测试玩家 ID。
        /// </summary>
        /// <param name="index">玩家索引。</param>
        /// <returns>生成的测试 <see cref="Guid"/>。</returns>
        public static Guid DeterministicId(int index)
        {
            Span<byte> bytes = stackalloc byte[16];
            BitConverter.TryWriteBytes(bytes, index + 1);
            // 末字节作生成器标记：synthetic 估算用 0xD1，真实探测用 0xD2（见 PostgresCapacityProbe）。
            bytes[15] = 0xD1;
            return new Guid(bytes);
        }
    }

    /// <summary>用于 synthetic 估算的 section 类型列表。</summary>
    internal static readonly string[] SectionKinds =
    [
        "progression",
        "world",
        "combat",
        "buffs",
        "loadout",
        "settings"
    ];
}
