using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Server.Clean.Ops;

/// <summary>
/// 运维监控面板配置。
/// 面板并入主 API 端口，页面由前端 React 应用承载；
/// 未配置路径时每次启动动态生成 `/ops-&lt;16位hex&gt;` 随机路径，所有读写接口统一由密码（`X-Ops-Token`）保护。
/// </summary>
/// <param name="Enabled">是否启用面板。</param>
/// <param name="Path">面板路径前缀，规范化后为 `/ops` 或 `/ops-&lt;后缀&gt;`，不含尾斜杠。</param>
/// <param name="LogBufferCapacity">面板日志环形缓冲容量。</param>
/// <param name="Token">访问密码（请求头 `X-Ops-Token`）；未配置时由进程启动随机生成。</param>
public sealed partial record OpsOptions(
    bool Enabled,
    string Path,
    int LogBufferCapacity,
    string Token)
{
    /// <summary>禁用态默认值，供未启用面板时直接使用。</summary>
    public static readonly OpsOptions Disabled = new(false, "/ops", 500, string.Empty);

    /// <summary>
    /// 从配置绑定面板选项；启用时严格校验取值范围，快速失败。
    /// 路径或密码未配置时生成进程级随机值，由宿主在启动日志打印一次。
    /// </summary>
    /// <param name="configuration">应用配置。</param>
    public static OpsOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var enabled = configuration.GetValue("Ops:Enabled", false);
        // 未启用时直接返回禁用默认值，既不校验也不保留可能非法的配置，避免阻断启动。
        if (!enabled) return Disabled;

        var rawPath = configuration["Ops:Path"];
        var path = string.IsNullOrWhiteSpace(rawPath) ? GeneratePath() : NormalizePath(rawPath);
        var capacity = configuration.GetValue("Ops:LogBufferCapacity", 500);
        var token = configuration["Ops:Token"]?.Trim();

        if (capacity is < 50 or > 10_000) throw new InvalidOperationException("Ops:LogBufferCapacity 必须在 50..10000。");
        if (token is { Length: > 512 }) throw new InvalidOperationException("Ops:Token 长度不能超过 512。");
        if (string.IsNullOrWhiteSpace(token)) token = GenerateToken();
        return new OpsOptions(true, path, capacity, token);
    }

    /// <summary>生成进程级随机路径（`/ops-` + 8 字节加密随机数的十六进制表示）。</summary>
    private static string GeneratePath() =>
        "/ops-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>生成一次性运维密码（16 字节加密随机数的十六进制表示）。</summary>
    private static string GenerateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string NormalizePath(string raw)
    {
        var path = raw.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        path = path.TrimEnd('/');
        if (path.Length == 0) path = "/ops";
        if (!ValidPathRegex().IsMatch(path))
            throw new InvalidOperationException("Ops:Path 必须为 /ops 或 /ops-<字母数字_-后缀> 格式。");
        return path;
    }

    [GeneratedRegex("^/ops(-[a-zA-Z0-9_-]{1,120})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidPathRegex();

    /// <summary>密码绝不进入日志或异常文本，覆写默认记录打印。</summary>
    public override string ToString() =>
        $"OpsOptions {{ Enabled = {Enabled}, Path = {Path}, LogBufferCapacity = {LogBufferCapacity}, Token = *** }}";
}
