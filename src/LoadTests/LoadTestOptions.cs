namespace DaoHua.LoadTests;

/// <summary>
/// 压测命令行选项。
/// </summary>
/// <param name="Scenario">压测场景名称。</param>
/// <param name="Players">玩家数量。</param>
/// <param name="DurationSeconds">压测持续时间（秒）。</param>
/// <param name="Concurrency">并发连接数。</param>
/// <param name="ServerUri">服务端地址。</param>
/// <param name="DatabaseConnectionString">PostgreSQL 连接字符串。</param>
/// <param name="RequireDatabase">是否要求数据库真实门禁。</param>
/// <param name="RequireServer">是否要求服务端真实门禁。</param>
/// <param name="KeepProbeSchema">是否保留容量探测临时 schema。</param>
/// <param name="StateChangesPerSecond">每秒状态变更数。</param>
/// <param name="StateChangeSeconds">状态变更持续秒数。</param>
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
    /// <summary>
    /// 从命令行参数解析压测选项。
    /// </summary>
    /// <param name="args">命令行参数数组。</param>
    /// <returns>解析后的 <see cref="LoadTestOptions"/> 实例。</returns>
    /// <exception cref="ArgumentException">当参数不合法或场景未知时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">当数值越界时抛出。</exception>
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

    /// <summary>
    /// 解析绝对 WebSocket/HTTP URL。
    /// </summary>
    /// <param name="value">原始 URL 字符串。</param>
    /// <returns>解析后的绝对 <see cref="Uri"/>。</returns>
    /// <exception cref="ArgumentException">当 URL 不合法或方案不支持时抛出。</exception>
    private static Uri ParseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("ws" or "wss" or "http" or "https"))
        {
            throw new ArgumentException($"--url 必须是绝对 HTTP/HTTPS/WS/WSS URL: {value}");
        }

        return uri;
    }

    /// <summary>
    /// 从参数字典读取有范围约束的整数值。
    /// </summary>
    /// <param name="values">参数字典。</param>
    /// <param name="key">参数键。</param>
    /// <param name="fallback">未提供时的默认值。</param>
    /// <param name="min">最小值。</param>
    /// <param name="max">最大值。</param>
    /// <returns>合法的整数值。</returns>
    /// <exception cref="ArgumentOutOfRangeException">当数值越界时抛出。</exception>
    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback, int min, int max)
    {
        if (!values.TryGetValue(key, out var raw)) return fallback;
        if (!int.TryParse(raw, out var value) || value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(key, $"{key} 必须在 {min} 到 {max} 之间。");
        }

        return value;
    }

    /// <summary>
    /// 从参数字典读取布尔值。
    /// </summary>
    /// <param name="values">参数字典。</param>
    /// <param name="key">参数键。</param>
    /// <param name="fallback">未提供时的默认值。</param>
    /// <returns>解析后的布尔值。</returns>
    /// <exception cref="ArgumentException">当取值不是 true/false 时抛出。</exception>
    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var raw)) return fallback;
        if (!bool.TryParse(raw, out var value)) throw new ArgumentException($"{key} 必须是 true 或 false。");
        return value;
    }
}
