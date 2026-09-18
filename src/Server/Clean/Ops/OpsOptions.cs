using System.Net;

namespace Server.Clean.Ops;

/// <summary>
/// 本地运维信息面板配置（Development-only）。
/// 面板独立监听一个端口，默认绑定 <c>0.0.0.0</c> 以便同一局域网设备访问；
/// 生产环境不会装配面板，配置误开也会被环境判定拦截。
/// </summary>
/// <param name="Enabled">是否启用面板（仍要求运行在 Development 环境）。</param>
/// <param name="Port">面板监听端口。</param>
/// <param name="ListenAddress">面板监听地址（必须是 IP 字面量）。</param>
/// <param name="Path">面板路径前缀，规范化后不含尾斜杠。</param>
/// <param name="LogBufferCapacity">面板日志环形缓冲容量。</param>
public sealed record OpsOptions(
    bool Enabled,
    int Port,
    string ListenAddress,
    string Path,
    int LogBufferCapacity)
{
    /// <summary>默认端口：与主 API 端口区分，避免与游戏流量混用。</summary>
    public const int DefaultPort = 5129;

    /// <summary>面板完整监听 URL。</summary>
    public string Url => $"http://{ListenAddress}:{Port}";

    /// <summary>禁用态默认值，供非 Development 环境直接使用。</summary>
    public static readonly OpsOptions Disabled = new(false, DefaultPort, "127.0.0.1", "/ops", 500);

    /// <summary>
    /// 从配置绑定面板选项；启用时严格校验取值范围与监听地址，快速失败。
    /// </summary>
    /// <param name="configuration">应用配置。</param>
    public static OpsOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var enabled = configuration.GetValue("Ops:Enabled", false);
        // 未启用时直接返回禁用默认值，既不校验也不保留可能非法的配置，避免阻断生产启动。
        if (!enabled) return Disabled;

        var port = configuration.GetValue("Ops:Port", DefaultPort);
        var listenAddress = NormalizeAddress(configuration["Ops:ListenAddress"]);
        var path = NormalizePath(configuration["Ops:Path"]);
        var capacity = configuration.GetValue("Ops:LogBufferCapacity", 500);

        if (port is < 1 or > 65_535) throw new InvalidOperationException("Ops:Port 必须在 1..65535。");
        if (capacity is < 50 or > 10_000) throw new InvalidOperationException("Ops:LogBufferCapacity 必须在 50..10000。");
        if (!IPAddress.TryParse(listenAddress, out _))
            throw new InvalidOperationException("Ops:ListenAddress 必须是合法 IP 字面量（如 0.0.0.0 或 127.0.0.1）。");
        return new OpsOptions(true, port, listenAddress, path, capacity);
    }

    private static string NormalizeAddress(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "127.0.0.1" : raw.Trim();

    private static string NormalizePath(string? raw)
    {
        var path = string.IsNullOrWhiteSpace(raw) ? "/ops" : raw.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        path = path.TrimEnd('/');
        return path.Length == 0 ? "/ops" : path;
    }
}
