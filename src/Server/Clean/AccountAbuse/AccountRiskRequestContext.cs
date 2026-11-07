using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Server.Clean.AccountAbuse;

/// <summary>浏览器可提交的信号契约；来源 IP 和主体键始终由服务端生成。</summary>
public sealed record AccountRiskClientSignals(
    string? DeviceConfigHash,
    string? DeviceId,
    string? NetworkHash,
    string? BrowserFingerprint,
    bool? Automated = null);

/// <summary>认证边界的信号校验、可信来源推导与加密设备 Cookie。</summary>
public sealed class AccountRiskRequestContext(IDataProtectionProvider protection)
{
    private const string DeviceCookie = "daohua.device";
    private readonly IDataProtector _protector = protection.CreateProtector("daohua.account-risk.device.v1");

    /// <summary>收集可信代理处理后的来源；任何客户端提交的 IP、主体键均没有接缝。</summary>
    public AccountRiskSignals Collect(HttpContext context, AccountRiskClientSignals? client, bool persistCookie = true)
    {
        ValidateHash(client?.DeviceConfigHash);
        ValidateHash(client?.NetworkHash);
        ValidateHash(client?.BrowserFingerprint);
        if (client?.DeviceId is { } requestedId && !Guid.TryParseExact(requestedId, "D", out _))
            throw new ArgumentException("设备标识无效。");
        // 客户端自报 DeviceId 仅作为软信号输入；首次请求由服务端随机生成硬设备锚点，
        // 只有服务端签发并解密成功的 Cookie 才能进入设备配额与 PersonKey。
        var deviceId = ReadDeviceId(context);
        if (!deviceId.HasValue && client?.DeviceId is not null)
            deviceId = Guid.NewGuid();
        if (persistCookie && deviceId.HasValue) context.Response.Cookies.Append(DeviceCookie, _protector.Protect(deviceId.Value.ToString("D")), new CookieOptions
        {
            HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax,
            Path = "/", MaxAge = TimeSpan.FromDays(365), IsEssential = true
        });
        var isHeadless = IsHeadless(context, client);
        var isAndroid = IsAndroid(context);
        // 稳定主体键推导：
        // 1. Android 移动端硬件配置高度雷同，严禁将粗粒度硬件规格 DeviceConfigHash 降级充当主体键，避免同款手机玩家被误杀；
        //    同时 Android 环境下音频/Canvas 特征在同芯片机型上高度趋同，因此仅信赖设备唯一安装标识或安全 Cookie；
        // 2. PC/桌面端优先信赖具有足够熵值的 BrowserFingerprint，其次信赖受保护 Cookie/安装标识；
        // 3. 任何端均不得把 DeviceConfigHash 作为单项自然人硬杀指标（配置已在 80 分软风险中按 45 分权重参与综合评估）。
        var personKey = !string.IsNullOrWhiteSpace(client?.BrowserFingerprint) && !isAndroid
            ? $"fp:{client.BrowserFingerprint}"
            : (deviceId.HasValue ? $"device:{deviceId:D}" : null);
        var ip = context.Connection.RemoteIpAddress;
        if (ip?.IsIPv4MappedToIPv6 == true) ip = ip.MapToIPv4();
        // 网段是弱关联信号，公共网络和家庭 NAT 不因 IP 相同直接封禁。
        var network = ip is null ? null : NetworkPrefix(ip);
        return new AccountRiskSignals(
            client?.DeviceConfigHash,
            deviceId?.ToString("D"),
            ip?.ToString(),
            network,
            personKey,
            client?.BrowserFingerprint,
            IsHeadless: isHeadless);
    }

    private Guid? ReadDeviceId(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(DeviceCookie, out var value)) return null;
        try { return Guid.ParseExact(_protector.Unprotect(value), "D"); }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    private static void ValidateHash(string? value)
    {
        if (value is null) return;
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("设备校验信息无效。");
    }

    private static string NetworkPrefix(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) { bytes[3] = 0; return $"{new IPAddress(bytes)}/24"; }
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    private static bool IsHeadless(HttpContext context, AccountRiskClientSignals? client)
    {
        if (client?.Automated == true) return true;
        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent)) return true;
        if (userAgent.Contains("HeadlessChrome", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("PhantomJS", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("Selenium", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("Puppeteer", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("Playwright", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (context.Request.Headers.TryGetValue("Sec-CH-UA", out var secChUa)
            && secChUa.ToString().Contains("HeadlessChrome", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    /// <summary>检测当前请求是否来自 Android 移动端环境。</summary>
    public static bool IsAndroid(HttpContext context)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        return userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase);
    }
}
