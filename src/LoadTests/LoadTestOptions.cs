namespace DaoHua.LoadTests;

/// <summary>
/// 压测命令行选项。
/// </summary>
/// <param name="Scenario">压测场景名：capacity-target / websocket / http-read / compute / postgres / compute-link。</param>
/// <param name="Players">模拟玩家数；缺省时按场景取容量目标默认值（如 websocket=100、http-read=500）。</param>
/// <param name="DurationSeconds">持续收发压测窗口（秒），worker 在该窗口内尽量多发。</param>
/// <param name="Concurrency">同时进行中的连接或握手数上限。</param>
/// <param name="ServerUri">目标服务端地址；ws(s) 与 http(s) 可混写，由各场景归一化方案与路径。</param>
/// <param name="DatabaseConnectionString">PostgreSQL 连接串；缺省取 DAOHUA_DATABASE_URL 环境变量，再回退本地开发库。</param>
/// <param name="DatabaseExplicit">命令行是否显式传入 --database（compute-link 场景据此决定测试库解析优先级）。</param>
/// <param name="RequireDatabase">数据库探测不可达时是否计入门禁失败；false 时仅降级为警告。</param>
/// <param name="RequireServer">是否要求真实服务端：true 时先注册压测账号换取会话 Cookie，false 时 WebSocket 场景整体走匿名 Upgrade 探测。</param>
/// <param name="KeepProbeSchema">是否保留容量探测的临时 schema 与种子数据供事后排查（默认探测结束即删除）。</param>
/// <param name="StateChangesPerSecond">latest-only 队列模拟的每秒状态变更速率。</param>
/// <param name="StateChangeSeconds">状态变更持续秒数；与速率相乘得到入队总轮数。</param>
/// <param name="ComputeNodes">compute-link 场景拨入的计算节点进程数。</param>
/// <param name="ComputeWaves">compute-link 场景重复播种波次数（每波全员重新变为可补算状态）。</param>
/// <param name="NodeKill">compute-link 场景是否在中途杀掉一个节点测量断线改派时延。</param>
/// <param name="RepoRoot">仓库根目录（含 Game.slnx）；缺省从当前目录向上探测，用于定位 Server/ComputeNode 构建产物。</param>
public sealed record LoadTestOptions(
    string Scenario,
    int Players,
    int DurationSeconds,
    int Concurrency,
    Uri ServerUri,
    string DatabaseConnectionString,
    bool DatabaseExplicit,
    bool RequireDatabase,
    bool RequireServer,
    bool KeepProbeSchema,
    int StateChangesPerSecond,
    int StateChangeSeconds,
    int ComputeNodes,
    int ComputeWaves,
    bool NodeKill,
    string? RepoRoot)
{
    /// <summary>综合容量门禁要求真实目标、进程资源证据与预算同时闭环；独立小规模场景不冒充容量验收。</summary>
    public bool CapacityGate { get; init; }

    /// <summary>在本场景自动启动的隔离主服执行 100/500 认证网络业务门禁。</summary>
    public bool CapacityNetworkOnLocalServer { get; init; }

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
            // 无值的 --flag 约定为布尔 true，使开关类参数可省略显式取值。
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";
            values[key] = value;
        }

        var scenario = values.GetValueOrDefault("scenario", "capacity-target").Trim().ToLowerInvariant();
        // 各场景默认玩家数对齐容量目标：不带 --players 的运行即为门禁口径。
        var defaultPlayers = scenario switch
        {
            "capacity-target" => CapacityTargetScenario.RegisteredAccountTarget,
            "websocket" => 100,
            "http-read" => 500,
            "compute" => CapacityTargetScenario.ColdComputeTarget,
            "postgres" => CapacityTargetScenario.RegisteredAccountTarget,
            // compute-link 的 --players 是播种的离线玩家数（每玩家一个冷挂机任务）。
            "compute-link" => 32,
            _ => throw new ArgumentException($"未知场景: {scenario}")
        };
        var defaultDuration = scenario == "compute-link" ? 180 : 1;
        var defaultConnection = Environment.GetEnvironmentVariable("DAOHUA_DATABASE_URL")
                                ?? "Host=127.0.0.1;Port=5432;Database=daohua;Username=daohua;Password=daohua-local-dev;Maximum Pool Size=16;Timeout=5;Command Timeout=30";
        return new LoadTestOptions(
            scenario,
            ReadInt(values, "players", defaultPlayers, 1, 1_000_000),
            ReadInt(values, "duration-seconds", defaultDuration, 1, 86_400),
            ReadInt(values, "concurrency", ReadInt(values, "connect-concurrency", 64, 1, 2_048), 1, 2_048),
            ParseUri(values.GetValueOrDefault("url", "ws://127.0.0.1:5128/ws")),
            values.GetValueOrDefault("database", defaultConnection),
            values.ContainsKey("database"),
            ReadBool(values, "require-database", false),
            ReadBool(values, "require-server", false),
            ReadBool(values, "keep-probe-schema", false),
            ReadInt(values, "state-changes-per-second", 300, 1, 100_000),
            ReadInt(values, "state-change-seconds", 5, 1, 600),
            ReadInt(values, "compute-nodes", 2, 1, 8),
            ReadInt(values, "compute-waves", 1, 1, 20),
            ReadBool(values, "node-kill", true),
            values.GetValueOrDefault("repo-root"));
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
