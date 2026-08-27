namespace DaoHua.LoadTests;

public static class CapacityTargetScenario
{
    public const int RegisteredAccountTarget = 5_000;
    public const int SustainedNetworkTarget = 100;
    public const int PeakNetworkTarget = 500;
    public const int ColdComputeTarget = 2_000;
    public const long DataDiskBudgetBytes = 12L * 1024 * 1024 * 1024;
    public const long WalBudgetBytes = 2L * 1024 * 1024 * 1024;
    public const long LocalLogBudgetBytes = 100L * 1024 * 1024;
    public const long PeakNetworkBudgetBytesPerSecond = 512L * 1024;
    public const double ColdComputeBudgetMilliseconds = 30_000;
    public const long ProcessWorkingSetBudgetBytes = 768L * 1024 * 1024;

    public static async Task<LoadTestReport> RunAsync(LoadTestOptions options, CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
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

    public static async Task<LoadTestReport> RunPostgresOnlyAsync(
        LoadTestOptions options,
        CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var cpuBefore = process.TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await PostgresCapacityProbe.RunAsync(options, cancellationToken);
            process.Refresh();
            return new LoadTestReport(
                "loadtest.v1",
                "postgres",
                RegisteredAccountTarget,
                options.StateChangeSeconds,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                managedBefore,
                GC.GetTotalMemory(forceFullCollection: false),
                process.WorkingSet64,
                0,
                0,
                0,
                0,
                result.DataBytes > DataDiskBudgetBytes || result.WalBytes > WalBudgetBytes ? 1 : 0,
                result.DataBytes <= DataDiskBudgetBytes && result.WalBytes <= WalBudgetBytes,
                new Dictionary<string, object?> { ["postgres"] = result },
                []);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return LoadTestReport.Failure("postgres", exception.Message);
        }
    }

    private sealed record SyntheticStorageEstimate(
        long AccountBytes,
        long SectionBytes,
        long QueueBytes,
        long EstimatedBytes,
        long AverageSectionBytes);

    private sealed record SyntheticNetworkEstimate(
        long BytesPerSecond,
        long AverageMessageBytes,
        int PeakPlayers);

    private static class CapacitySynthetic
    {
        public static SyntheticStorageEstimate EstimateStorage(int players)
        {
            long sectionBytes = 0;
            var accountBytes = players * 256L;
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

        public static SyntheticNetworkEstimate EstimatePeakNetwork(int players)
        {
            long total = 0;
            for (var index = 0; index < players; index++)
            {
                total += 128 + CreatePayload(index, "state").Length;
            }

            return new SyntheticNetworkEstimate(total, total / players, players);
        }

        public static byte[] CreatePayload(int index, string kind)
        {
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

        public static Guid DeterministicId(int index)
        {
            Span<byte> bytes = stackalloc byte[16];
            BitConverter.TryWriteBytes(bytes, index + 1);
            bytes[15] = 0xD1;
            return new Guid(bytes);
        }
    }

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
