namespace Daoxuan.LoadTests;

public sealed record LoadTestOptions(
    string Scenario,
    int Players,
    int DurationSeconds,
    Uri ServerUri,
    int ConnectConcurrency)
{
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
