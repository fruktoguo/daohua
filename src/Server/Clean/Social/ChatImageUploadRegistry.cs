using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Server.Clean.Social;

/// <summary>
/// 图片上传凭据签发结果。
/// </summary>
/// <param name="Cached">是否命中既有哈希缓存（命中时不消耗每日配额，无需再传 R2）。</param>
/// <param name="Url">生成的图片公网直链。</param>
/// <param name="Ticket">签名凭证（未命中缓存时提供，用于提交给 Cloudflare Worker）。</param>
/// <param name="UploadEndpoint">Worker 上传端点。</param>
/// <param name="RemainingToday">该账号今日剩余新增上传额度。</param>
public sealed record ChatUploadTicketResult(
    bool Cached,
    string Url,
    string? Ticket = null,
    string? UploadEndpoint = null,
    int RemainingToday = 0
);

/// <summary>
/// 传音图片上传配额与防重鉴权注册表（单例）。
/// 职责：
/// 1. 账号级单日上传配额控制（每账号每日限 20 张新图，UTC 日期重置）；
/// 2. 全服图片 SHA-256 哈希去重索引（相同图片直接复用既有直链，不占 R2 且不消耗配额）；
/// 3. 为合法玩家签发有时效、防篡改的 HMAC-SHA256 签名 Ticket。
/// </summary>
public sealed class ChatImageUploadRegistry
{
    public const int DailyLimitPerAccount = 20;
    private const int TicketLifetimeMinutes = 5;

    private readonly byte[] _secretBytes;
    private readonly string _uploadEndpoint;
    private readonly string _publicBaseUrl;

    /// <summary>账号每日已用额度计数：AccountId -> (DateKey, Count)</summary>
    private readonly ConcurrentDictionary<Guid, (string DateKey, int Count)> _dailyCounts = new();

    /// <summary>全服哈希去重缓存：SHA256Hex -> PublicUrl</summary>
    private readonly ConcurrentDictionary<string, string> _hashToUrl = new(StringComparer.OrdinalIgnoreCase);

    public ChatImageUploadRegistry(IConfiguration configuration)
    {
        var secret = configuration["Chat:ImageSecret"] ?? "daohua-chat-image-secret-2026";
        _secretBytes = Encoding.UTF8.GetBytes(secret);

        _uploadEndpoint = configuration["Chat:ImageUploadEndpoint"]
            ?? "https://img.yuohira.com/upload";
        _publicBaseUrl = configuration["Chat:ImageBaseUrl"]
            ?? "https://img.yuohira.com";
    }

    /// <summary>
    /// 请求上传凭据或取回已去重直链。
    /// </summary>
    public bool TryAcquireUploadTicket(
        Guid accountId,
        string sha256Hex,
        string ext,
        out ChatUploadTicketResult result,
        out string? errorMessage)
    {
        result = default!;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(sha256Hex) || sha256Hex.Length != 64)
        {
            errorMessage = "图片 SHA-256 哈希无效。";
            return false;
        }

        var normalizedExt = NormalizeExtension(ext);
        var normalizedHash = sha256Hex.ToLowerInvariant();
        var today = DateTime.UtcNow.ToString("yyyyMMdd");

        // 1. 全服哈希去重预检：已有相同图片时，直接秒回已有 URL，不消耗当日配额
        if (_hashToUrl.TryGetValue(normalizedHash, out var existingUrl))
        {
            var (_, currentCount) = _dailyCounts.GetOrAdd(accountId, _ => (today, 0));
            var remaining = Math.Max(0, DailyLimitPerAccount - currentCount);
            result = new ChatUploadTicketResult(
                Cached: true,
                Url: existingUrl,
                Ticket: null,
                UploadEndpoint: null,
                RemainingToday: remaining
            );
            return true;
        }

        // 2. 检查当日新增配额
        var (dateKey, count) = _dailyCounts.GetOrAdd(accountId, _ => (today, 0));
        if (dateKey != today)
        {
            count = 0;
            _dailyCounts[accountId] = (today, 0);
        }

        if (count >= DailyLimitPerAccount)
        {
            errorMessage = $"今日图片上传配额已用完（每日上限 {DailyLimitPerAccount} 张）。";
            return false;
        }

        // 3. 计算预定短直链（使用哈希前 16 位，直链格式短小，严密适配聊天 200 字符限制）
        var shortId = normalizedHash[..16];
        var filename = $"{shortId}.{normalizedExt}";
        var targetUrl = $"{_publicBaseUrl.TrimEnd('/')}/{filename}";

        // 4. 预先注册哈希映射并原子自增配额
        _dailyCounts.AddOrUpdate(
            accountId,
            _ => (today, 1),
            (_, old) => old.DateKey == today ? (today, old.Count + 1) : (today, 1)
        );
        _hashToUrl[normalizedHash] = targetUrl;

        var newRemaining = Math.Max(0, DailyLimitPerAccount - (count + 1));

        // 5. 构造并签名 Ticket
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(TicketLifetimeMinutes).ToUnixTimeMilliseconds();
        var payloadObj = new
        {
            acc = accountId.ToString("N"),
            hash = normalizedHash,
            ext = normalizedExt,
            exp = expiresAt
        };
        var payloadJson = JsonSerializer.Serialize(payloadObj);
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        var sigBytes = HMACSHA256.HashData(_secretBytes, Encoding.UTF8.GetBytes(payloadB64));
        var sigB64 = Base64UrlEncode(sigBytes);
        var ticket = $"{payloadB64}.{sigB64}";

        result = new ChatUploadTicketResult(
            Cached: false,
            Url: targetUrl,
            Ticket: ticket,
            UploadEndpoint: _uploadEndpoint,
            RemainingToday: newRemaining
        );
        return true;
    }

    private static string NormalizeExtension(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return "png";
        var clean = ext.Trim().TrimStart('.').ToLowerInvariant();
        return clean switch
        {
            "gif" => "gif",
            "webp" => "webp",
            "jpg" or "jpeg" => "jpg",
            _ => "png"
        };
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }
}
