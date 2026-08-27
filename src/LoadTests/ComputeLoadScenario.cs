namespace DaoHua.LoadTests;

public static class ComputeLoadScenario
{
    public static async Task<LoadTestReport> RunAsync(LoadTestOptions options, CancellationToken cancellationToken = default)
    {
        var result = await RunLocalAsync(options.Players, cancellationToken);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var gatePassed = result.ElapsedMilliseconds <= CapacityTargetScenario.ColdComputeBudgetMilliseconds
                         && result.Errors == 0;
        return new LoadTestReport(
            "loadtest.v1",
            "compute",
            options.Players,
            options.DurationSeconds,
            result.ElapsedMilliseconds,
            result.CpuMilliseconds,
            result.ManagedBytesBefore,
            result.ManagedBytesAfter,
            process.WorkingSet64,
            0,
            0,
            0,
            0,
            result.Errors,
            gatePassed,
            new Dictionary<string, object?>
            {
                ["completedPlayers"] = result.CompletedPlayers,
                ["checksum"] = result.Checksum,
                ["actions"] = result.CompletedActions,
                ["transitions"] = result.Transitions
            },
            []);
    }

    internal static Task<ComputeResult> RunLocalAsync(int players, CancellationToken cancellationToken)
    {
        var process = Process.GetCurrentProcess();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var cpuBefore = process.TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();
        var checksums = new long[players];
        long actions = 0;
        long transitions = 0;
        var errors = 0;

        Parallel.For(
            0,
            players,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                CancellationToken = cancellationToken
            },
            index =>
            {
                try
                {
                    var state = unchecked((ulong)index + 1);
                    var localActions = 0L;
                    var localTransitions = 0L;
                    // 固定 3600 秒补算：按行动边界推进，不生成逐秒日志。
                    for (var step = 0; step < 1_200; step++)
                    {
                        state = Mix(state + (ulong)step * 0x9E3779B97F4A7C15UL);
                        if ((state & 7) == 0)
                        {
                            localActions++;
                            state = Mix(state ^ (ulong)localActions);
                        }

                        localTransitions++;
                    }

                    checksums[index] = unchecked((long)state);
                    Interlocked.Add(ref actions, localActions);
                    Interlocked.Add(ref transitions, localTransitions);
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }
            });

        process.Refresh();
        return Task.FromResult(new ComputeResult(
            players - errors,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            managedBefore,
            GC.GetTotalMemory(forceFullCollection: false),
            checksums.Aggregate(0L, (current, value) => current ^ value),
            actions,
            transitions,
            errors));
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5BUL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    internal sealed record ComputeResult(
        int CompletedPlayers,
        double ElapsedMilliseconds,
        double CpuMilliseconds,
        long ManagedBytesBefore,
        long ManagedBytesAfter,
        long Checksum,
        long CompletedActions,
        long Transitions,
        int Errors);
}
