using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace DaoHua.Server.Clean.TapTap;

/// <summary>
/// TapTap OAuth <c>state</c> 防 Login CSRF 保护器：
/// 为每次授权生成 128 位随机高熵 <c>state</c>，并签发带 HMAC-SHA256 防篡改签名与过期时间的短期 HttpOnly Cookie；
/// 回调阶段采用恒定时间比较验证请求 <c>state</c> 与签名 Cookie 完全一致。
/// </summary>
public sealed class TapTapOAuthStateProtector
{
    /// <summary>存储签名 state 的 HttpOnly Cookie 名称。</summary>
    public const string CookieName = "daohua.taptap.state";

    /// <summary>Cookie 作用路径，收敛至 TapTap 认证路由。</summary>
    public const string CookiePath = "/api/v1/auth/taptap";

    /// <summary>OAuth state 有效窗口。</summary>
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private readonly byte[] _hmacKey;

    /// <summary>从配置初始化签名密钥；未配置显式密钥时派生自 ClientSecret 或生成进程级随机密钥。</summary>
    public TapTapOAuthStateProtector(IConfiguration configuration)
    {
        var explicitSecret = configuration["TapTap:StateSecret"]?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitSecret))
        {
            _hmacKey = SHA256.HashData(Encoding.UTF8.GetBytes(explicitSecret));
            return;
        }

        var clientSecret = configuration["TapTap:ClientSecret"]?.Trim();
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            // 按用途域分离派生密钥，避免直接使用原始 ClientSecret 作为多用途 MAC 密钥。
            _hmacKey = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(clientSecret),
                "daohua.taptap.oauth-state.v1"u8);
            return;
        }

        _hmacKey = RandomNumberGenerator.GetBytes(32);
    }

    /// <summary>生成随机 <c>state</c> 及对应的 HMAC-SHA256 签名 Cookie 载荷。</summary>
    public (string State, string CookieValue) CreateState(DateTimeOffset? now = null)
    {
        var issuedAt = now ?? DateTimeOffset.UtcNow;
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var expiresAtUnixSeconds = issuedAt.Add(StateLifetime).ToUnixTimeSeconds();
        var payload = $"{state}.{expiresAtUnixSeconds.ToString(CultureInfo.InvariantCulture)}";
        var signature = ComputeSignatureBase64Url(payload);
        return (state, $"{payload}.{signature}");
    }

    /// <summary>向响应写入短期 <c>HttpOnly + SameSite=Lax + Path=/api/v1/auth/taptap</c> 签名 Cookie。</summary>
    public string IssueStateCookie(HttpContext context, DateTimeOffset? now = null)
    {
        var (state, cookieValue) = CreateState(now);
        context.Response.Cookies.Append(CookieName, cookieValue, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = CookiePath,
            MaxAge = StateLifetime,
            IsEssential = true
        });
        return state;
    }

    /// <summary>清理浏览器端 OAuth state Cookie。</summary>
    public static void ClearStateCookie(HttpContext context)
    {
        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = CookiePath
        });
    }

    /// <summary>校验查询参数 <c>state</c> 与 HMAC-SHA256 签名 Cookie 是否一致且未过期。</summary>
    public bool ValidateState(string? queryState, string? cookieValue, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(queryState) || string.IsNullOrWhiteSpace(cookieValue))
            return false;

        var parts = cookieValue.Split('.');
        if (parts.Length != 3 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]) || string.IsNullOrEmpty(parts[2]))
            return false;

        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresAtUnixSeconds))
            return false;

        var checkTime = now ?? DateTimeOffset.UtcNow;
        if (checkTime.ToUnixTimeSeconds() > expiresAtUnixSeconds)
            return false;

        var payload = $"{parts[0]}.{parts[1]}";
        var expectedSignature = HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(payload));
        if (!TryBase64UrlDecode(parts[2], out var providedSignature))
            return false;

        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature))
            return false;

        var queryBytes = Encoding.UTF8.GetBytes(queryState.Trim());
        var cookieStateBytes = Encoding.UTF8.GetBytes(parts[0]);
        return CryptographicOperations.FixedTimeEquals(queryBytes, cookieStateBytes);
    }

    private string ComputeSignatureBase64Url(string payload)
    {
        var signature = HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(signature)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static bool TryBase64UrlDecode(string input, out byte[] decoded)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        try
        {
            decoded = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            decoded = [];
            return false;
        }
    }
}
