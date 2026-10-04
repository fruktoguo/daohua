using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Server.Clean.AccountAbuse;

/// <summary>浏览器可提交的信号契约；来源 IP 和主体键始终由服务端生成。</summary>
public sealed record AccountRiskClientSignals(string? DeviceConfigHash, string? DeviceId, string? NetworkHash, string? BrowserFingerprint);

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
        // 不为缺失的设备输入生成随机锚点，否则空对象可以不断取得新额度。
        var deviceId = ReadDeviceId(context) ?? (client?.DeviceId is { } id ? Guid.Parse(id) : (Guid?)null);
        if (persistCookie && deviceId.HasValue) context.Response.Cookies.Append(DeviceCookie, _protector.Protect(deviceId.Value.ToString("D")), new CookieOptions
        {
            HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax,
            Path = "/", MaxAge = TimeSpan.FromDays(365), IsEssential = true
        });
        var ip = context.Connection.RemoteIpAddress;
        if (ip?.IsIPv4MappedToIPv6 == true) ip = ip.MapToIPv4();
        // 网段是弱关联信号，公共网络和家庭 NAT 不因 IP 相同直接封禁。
        var network = ip is null ? null : NetworkPrefix(ip);
        return new AccountRiskSignals(
            client?.DeviceConfigHash,
            deviceId?.ToString("D"),
            ip?.ToString(),
            network,
            deviceId.HasValue ? $"device:{deviceId:D}" : null,
            client?.BrowserFingerprint);
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
}
