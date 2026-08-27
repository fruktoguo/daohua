namespace DaoHua.LoadTests;

public sealed record LoadTestOptions(
    string Scenario,
    int Players,
    int DurationSeconds,
    int Concurrency,
    Uri ServerUri,
    string DatabaseConnectionString,
    bool RequireDatabase,
    bool RequireServer,
    bool KeepProbeSchema,
    int StateChangesPerSecond,
    int StateChangeSeconds)
{
    public static LoadTestOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"不支持的位置参数: {argument}");
            }

            var key = argument[2..];
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";
            values[key] = value;
        }

        var scenario = values.GetValueOrDefault("scenario", "capacity-target").Trim().ToLowerInvariant();
        var defaultPlayers = scenario switch
        {
            "websocket" => 100,
            "http-read" => 500,
            "compute" => 2_000,
            "postgres" => 5_000,
            "capacity-target" => 5_000,
            _ => throw new ArgumentException($"未知场景: {scenario}")
        };
        var defaultDuration = scenario is "websocket" or "http-read" ? 30 : 1;
        var defaultConnection = Environment.GetEnvironmentVariable("DAOHUA_DATABASE_URL")
                                ?? "Host=127.0.0.1;Port=5432;Database=daohua;Username=daohua;Password=daohua-local-dev;Maximum Pool Size=16;Timeout=5;Command Timeout=30";
        return new LoadTestOptions(
            scenario,
            ReadInt(values, "players", defaultPlayers, 1, 1_000_000),
            ReadInt(values, "duration-seconds", defaultDuration, 1, 86_400),
            ReadInt(values, "concurrency", ReadInt(values, "connect-concurrency", 64, 1, 2_048), 1, 2_048),
            ParseUri(values.GetValueOrDefault("url", "ws://127.0.0.1:5128/ws")),
            values.GetValueOrDefault("database", defaultConnection),
            ReadBool(values, "require-database", false),
            ReadBool(values, "require-server", false),
            ReadBool(values, "keep-probe-schema", false),
            ReadInt(values, "state-changes-per-second", 300, 1, 100_000),
            ReadInt(values, "state-change-seconds", 5, 1, 600));
    }

    private static Uri ParseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("ws" or "wss" or "http" or "https"))
        {
            throw new ArgumentException($"--url 必须是绝对 HTTP/HTTPS/WS/WSS URL: {value}");
        }

        return uri;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback, int min, int max)
    {
        if (!values.TryGetValue(key, out var raw)) return fallback;
        if (!int.TryParse(raw, out var value) || value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(key, $"{key} 必须在 {min} 到 {max} 之间。");
        }

        return value;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var raw)) return fallback;
        if (!bool.TryParse(raw, out var value)) throw new ArgumentException($"{key} 必须是 true 或 false。");
        return value;
    }
}
