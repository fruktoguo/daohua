namespace DaoHua.LoadTests;

/// <summary>
/// 负载压测配置选项，封装压测场景类型、模拟玩家规模、测试时长、服务端连接地址与并发连接数。
/// </summary>
public sealed record LoadTestOptions(
    string Scenario,
    int Players,
    int DurationSeconds,
    Uri ServerUri,
    int ConnectConcurrency)
{
    /// <summary>
    /// 解析命令行传入的参数选项（如 --scenario, --players, --duration-seconds, --url, --connect-concurrency），
    /// 并根据不同压测场景赋予合理的默认并发规模与时长。
    /// </summary>
    public static LoadTestOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[index][2..];
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";
            values[key] = value;
        }

        var scenario = values.GetValueOrDefault("scenario", "idle-offline");
        var defaultPlayers = scenario switch
        {
            "online" => 100,
            "fully-offline" => 100_000,
            "market" => 100_000,
            "market-read" => 500,
            "market-expiry" => 100_000,
            "capacity-target" => 5_000,
            "combat-offline" => 10_000,
            _ => 10_000
        };
        return new LoadTestOptions(
            scenario,
            ReadInt(values, "players", defaultPlayers, 1, 1_000_000),
            ReadInt(
                values,
                "duration-seconds",
                scenario is "online" or "market-read" ? 15 : 3600,
                1,
                2_592_000),
            new Uri(values.GetValueOrDefault("url", "ws://127.0.0.1:5128/ws")),
            ReadInt(values, "connect-concurrency", 64, 1, 1024));
    }

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        if (!values.TryGetValue(key, out var raw)) return fallback;
        if (!int.TryParse(raw, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new ArgumentOutOfRangeException(key, $"{key} 必须在 {minimum} 到 {maximum} 之间。");
        }

        return parsed;
    }
}

/// <summary>
/// 负载压测标准化结果报告，记录压测场景、玩家规模、执行耗时、CPU/内存指标、网络包体吞吐、状态机流转与错误计数。
/// </summary>
public sealed record LoadTestReport(
    string Scenario,
    int Players,
    int DurationSeconds,
    double ElapsedMilliseconds,
    double CpuMilliseconds,
    long ManagedBytesBefore,
    long ManagedBytesAfter,
    long WorkingSetBytes,
    long SentBytes,
    long ReceivedBytes,
    long SentFrames,
    long ReceivedFrames,
    long CompletedActions,
    long Transitions,
    int Errors,
    object? ServerMetrics,
    string Notes);
