using System.Security.Cryptography;

namespace Server.Clean.Ops;

/// <summary>
/// 本地运维面板配置（Development-only）。
/// 面板并入主 API 端口（不再独立监听），页面由前端 React 应用承载；
/// 写操作由一次性令牌保护，只读视图仅依赖环境装配判定。
/// </summary>
/// <param name="Enabled">是否启用面板（仍要求运行在 Development 环境）。</param>
/// <param name="Path">面板路径前缀，规范化后不含尾斜杠。</param>
/// <param name="LogBufferCapacity">面板日志环形缓冲容量。</param>
/// <param name="Token">写操作一次性令牌；未配置时由进程启动随机生成。</param>
public sealed record OpsOptions(
    bool Enabled,
    string Path,
    int LogBufferCapacity,
    string Token)
{
    /// <summary>禁用态默认值，供非 Development 环境直接使用。</summary>
    public static readonly OpsOptions Disabled = new(false, "/ops", 500, string.Empty);

    /// <summary>
    /// 从配置绑定面板选项；启用时严格校验取值范围，快速失败。
    /// 令牌未配置时生成一次性随机值，由宿主在启动日志打印一次。
    /// </summary>
    /// <param name="configuration">应用配置。</param>
    public static OpsOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var enabled = configuration.GetValue("Ops:Enabled", false);
        // 未启用时直接返回禁用默认值，既不校验也不保留可能非法的配置，避免阻断生产启动。
        if (!enabled) return Disabled;

        var path = NormalizePath(configuration["Ops:Path"]);
        var capacity = configuration.GetValue("Ops:LogBufferCapacity", 500);
        var token = configuration["Ops:Token"]?.Trim();

        if (capacity is < 50 or > 10_000) throw new InvalidOperationException("Ops:LogBufferCapacity 必须在 50..10000。");
        if (token is { Length: > 512 }) throw new InvalidOperationException("Ops:Token 长度不能超过 512。");
        if (string.IsNullOrWhiteSpace(token)) token = GenerateToken();
        return new OpsOptions(true, path, capacity, token);
    }

    /// <summary>生成一次性运维令牌（16 字节加密随机数的十六进制表示）。</summary>
    private static string GenerateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string NormalizePath(string? raw)
    {
        var path = string.IsNullOrWhiteSpace(raw) ? "/ops" : raw.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        path = path.TrimEnd('/');
        return path.Length == 0 ? "/ops" : path;
    }

    /// <summary>令牌绝不进入日志或异常文本，覆写默认记录打印。</summary>
    public override string ToString() =>
        $"OpsOptions {{ Enabled = {Enabled}, Path = {Path}, LogBufferCapacity = {LogBufferCapacity}, Token = *** }}";
}
