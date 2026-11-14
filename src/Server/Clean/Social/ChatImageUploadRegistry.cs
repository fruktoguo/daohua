using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

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
    public const int MaxCachedHashes = 50_000;
    private const int TicketLifetimeMinutes = 5;

    private static readonly string[] DefaultAllowedImageHosts = ["beeimg.cn", "*.beeimg.cn"];

    private readonly byte[] _secretBytes;
    private readonly string _uploadEndpoint;
    private readonly string _publicBaseUrl;
    private readonly Uri _publicBaseUri;
    private readonly int _maxCachedHashes;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly HashSet<string> _allowedExactHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _allowedWildcardSuffixes = [];

    /// <summary>账号每日已用额度计数：AccountId -> (DateKey, Count)</summary>
    private readonly ConcurrentDictionary<Guid, (string DateKey, int Count)> _dailyCounts = new();
    private readonly object _dailyPruneGate = new();
    private string _lastPrunedDate = string.Empty;

    /// <summary>全服哈希去重缓存：SHA256Hex -> PublicUrl（受容量上限约束）</summary>
    private readonly ConcurrentDictionary<string, string> _hashToUrl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _hashInsertionOrder = new();
    /// <summary>已核销的一次性 Ticket 摘要 -> 过期毫秒戳，防重放同一 Confirm。</summary>
    private readonly Dictionary<string, long> _redeemedTickets = new(StringComparer.Ordinal);
    private readonly object _cacheGate = new();

    public ChatImageUploadRegistry(IConfiguration configuration, IHostEnvironment? hostEnvironment = null)
        : this(configuration, hostEnvironment, null, MaxCachedHashes)
    {
    }

    internal ChatImageUploadRegistry(
        IConfiguration configuration,
        IHostEnvironment? hostEnvironment,
        Func<DateTimeOffset>? utcNow,
        int maxCachedHashes = MaxCachedHashes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCachedHashes, 1);
        _maxCachedHashes = maxCachedHashes;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

        var secret = configuration["Chat:ImageSecret"]?.Trim();
        var isProduction = hostEnvironment?.IsProduction() == true;

        if (string.IsNullOrWhiteSpace(secret))
        {
            if (isProduction)
            {
                throw new InvalidOperationException(
                    "生产环境必须显式配置高熵 Chat:ImageSecret（环境变量 Chat__ImageSecret）。");
            }

            // 开发/测试未显式配置时自动生成进程级随机密钥，避免任何静态默认值进入公开源码
            secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        }
        else if (isProduction && (secret.Length < 32 || secret.StartsWith("daohua-chat-image-secret", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "生产环境 Chat:ImageSecret 长度不足 32 位或仍使用历史泄露默认值，拒绝启动。");
        }

        _secretBytes = Encoding.UTF8.GetBytes(secret);

        _uploadEndpoint = configuration["Chat:ImageUploadEndpoint"]
            ?? "https://img.yuohira.com/upload";
        _publicBaseUrl = (configuration["Chat:ImageBaseUrl"]
            ?? "https://img.yuohira.com").TrimEnd('/');
        if (!Uri.TryCreate(_publicBaseUrl, UriKind.Absolute, out var baseUri)
            || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new InvalidOperationException("Chat:ImageBaseUrl 必须是有效的无凭据 HTTPS 基址。");
        }
        _publicBaseUri = baseUri;

        foreach (var hostPattern in DefaultAllowedImageHosts)
            AddAllowedHostPattern(hostPattern);

        var extraHosts = configuration["Chat:AllowedImageHosts"];
        if (!string.IsNullOrWhiteSpace(extraHosts))
        {
            foreach (var raw in extraHosts.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddAllowedHostPattern(raw);
        }
    }

    internal int CachedHashCount => _hashToUrl.Count;
    internal int DailyCountEntryCount => _dailyCounts.Count;

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

        if (!IsValidSha256Hex(sha256Hex))
        {
            errorMessage = "图片 SHA-256 哈希无效。";
            return false;
        }

        var normalizedExt = NormalizeExtension(ext);
        var normalizedHash = sha256Hex.Trim().ToLowerInvariant();
        var now = _utcNow();
        var today = now.UtcDateTime.ToString("yyyyMMdd");
        PruneStaleDailyCountsIfNeeded(today);

        // 1. 全服哈希去重预检：已有相同图片时，直接秒回已有 URL，不消耗当日配额
        if (_hashToUrl.TryGetValue(normalizedHash, out var existingUrl))
        {
            var remaining = DailyLimitPerAccount;
            if (_dailyCounts.TryGetValue(accountId, out var existingQuota) && existingQuota.DateKey == today)
                remaining = Math.Max(0, DailyLimitPerAccount - existingQuota.Count);
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
        var targetUrl = $"{_publicBaseUrl}/{filename}";

        // 4. 原子自增当日配额（注意：全服去重映射 _hashToUrl 仅在实际落盘成功后经 RegisterUploadedUrl 写入，防止网络或客户端中断产生 404 幽灵直链）
        var updatedQuota = _dailyCounts.AddOrUpdate(
            accountId,
            _ => (today, 1),
            (_, old) => old.DateKey == today ? (today, old.Count + 1) : (today, 1)
        );
        var newRemaining = Math.Max(0, DailyLimitPerAccount - updatedQuota.Count);

        // 5. 构造并签名一次性 Ticket
        var expiresAt = now.AddMinutes(TicketLifetimeMinutes).ToUnixTimeMilliseconds();
        var payloadObj = new
        {
            acc = accountId.ToString("N"),
            hash = normalizedHash,
            ext = normalizedExt,
            exp = expiresAt,
            jti = Guid.NewGuid().ToString("N")
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

    /// <summary>
    /// 客户端上传成功后核验 Ticket 签名、调用者身份、一次性使用与 HTTPS 白名单目标后登记全服哈希映射。
    /// </summary>
    public bool RegisterUploadedUrl(
        Guid accountId,
        string sha256Hex,
        string url,
        string ticket,
        out string? errorMessage)
    {
        errorMessage = null;
        if (accountId == Guid.Empty)
        {
            errorMessage = "账号身份无效。";
            return false;
        }
        if (!IsValidSha256Hex(sha256Hex))
        {
            errorMessage = "图片 SHA-256 哈希无效。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(ticket))
        {
            errorMessage = "缺少上传确认凭据或目标地址。";
            return false;
        }

        var normalizedHash = sha256Hex.Trim().ToLowerInvariant();
        var trimmedUrl = url.Trim();
        var trimmedTicket = ticket.Trim();
        var nowMs = _utcNow().ToUnixTimeMilliseconds();

        if (!TryVerifyTicket(trimmedTicket, accountId, normalizedHash, nowMs, out var ticketExt, out var ticketExp, out errorMessage))
            return false;

        if (!IsTrustedImageUrl(trimmedUrl, normalizedHash, ticketExt, out errorMessage))
            return false;

        var ticketDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmedTicket)));
        lock (_cacheGate)
        {
            if (_redeemedTickets.Count > 256)
            {
                var expiredKeys = _redeemedTickets
                    .Where(kv => kv.Value <= nowMs)
                    .Select(kv => kv.Key)
                    .ToArray();
                foreach (var key in expiredKeys)
                    _redeemedTickets.Remove(key);
            }

            if (!_redeemedTickets.TryAdd(ticketDigest, ticketExp))
            {
                errorMessage = "上传凭证已核销，禁止重复确认。";
                return false;
            }

            if (!_hashToUrl.ContainsKey(normalizedHash))
            {
                while (_hashToUrl.Count >= _maxCachedHashes && _hashInsertionOrder.TryDequeue(out var oldest))
                    _hashToUrl.TryRemove(oldest, out _);
                _hashInsertionOrder.Enqueue(normalizedHash);
            }
            _hashToUrl[normalizedHash] = trimmedUrl;
        }

        return true;
    }

    private bool TryVerifyTicket(
        string ticket,
        Guid expectedAccountId,
        string expectedHash,
        long nowMs,
        out string ticketExt,
        out long expiresAtMs,
        out string? errorMessage)
    {
        ticketExt = string.Empty;
        expiresAtMs = 0;
        errorMessage = null;

        var parts = ticket.Split('.');
        if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
        {
            errorMessage = "上传凭证格式无效。";
            return false;
        }

        if (!TryBase64UrlDecode(parts[1], out var providedSig))
        {
            errorMessage = "上传凭证签名解码失败。";
            return false;
        }

        var expectedSig = HMACSHA256.HashData(_secretBytes, Encoding.UTF8.GetBytes(parts[0]));
        if (!CryptographicOperations.FixedTimeEquals(expectedSig, providedSig))
        {
            errorMessage = "上传凭证签名校验失败。";
            return false;
        }

        if (!TryBase64UrlDecode(parts[0], out var payloadBytes))
        {
            errorMessage = "上传凭证载荷无效。";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadBytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errorMessage = "上传凭证载荷格式错误。";
                return false;
            }

            var acc = root.TryGetProperty("acc", out var accProp) ? accProp.GetString() : null;
            var hash = root.TryGetProperty("hash", out var hashProp) ? hashProp.GetString() : null;
            var ext = root.TryGetProperty("ext", out var extProp) ? extProp.GetString() : null;
            if (!root.TryGetProperty("exp", out var expProp) || !expProp.TryGetInt64(out expiresAtMs))
            {
                errorMessage = "上传凭证缺少有效期。";
                return false;
            }

            if (!string.Equals(acc, expectedAccountId.ToString("N"), StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "上传凭证与当前账号不匹配。";
                return false;
            }
            if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "上传凭证与图片哈希不匹配。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(ext) || !IsAllowedExtension(ext))
            {
                errorMessage = "上传凭证图片扩展名无效。";
                return false;
            }
            if (expiresAtMs <= nowMs)
            {
                errorMessage = "上传凭证已过期。";
                return false;
            }

            ticketExt = ext.ToLowerInvariant();
            return true;
        }
        catch (JsonException)
        {
            errorMessage = "上传凭证载荷解析失败。";
            return false;
        }
    }

    private bool IsTrustedImageUrl(string url, string normalizedHash, string ticketExt, out string? errorMessage)
    {
        errorMessage = null;
        if (url.Length > 200 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            errorMessage = "图片直链格式或长度无效。";
            return false;
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "图片直链必须使用 HTTPS 协议。";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
        {
            errorMessage = "图片直链禁止携带用户信息或非标准端口。";
            return false;
        }
        if (uri.HostNameType != UriHostNameType.Dns)
        {
            errorMessage = "图片直链禁止使用 IP 地址宿主。";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            errorMessage = "图片直链禁止包含查询参数或片段。";
            return false;
        }

        // 自建 R2 域名必须严格命中服务端签发的目标路径 /{hash16}.{ext}，防止同域任意路径注入。
        if (string.Equals(uri.Host, _publicBaseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            var basePath = _publicBaseUri.AbsolutePath.TrimEnd('/');
            var expectedPath = $"{basePath}/{normalizedHash[..16]}.{ticketExt}";
            if (!string.Equals(uri.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "自建图床直链路径与凭证哈希不匹配。";
                return false;
            }
            return true;
        }

        if (!IsAllowedImageHost(uri.Host))
        {
            errorMessage = "图片直链域名不在受信任图床白名单内。";
            return false;
        }

        var path = uri.AbsolutePath;
        if (path.Length <= 1 || !(path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)))
        {
            errorMessage = "受信任图床直链必须指向有效的图片文件扩展名。";
            return false;
        }

        return true;
    }

    private bool IsAllowedImageHost(string host)
    {
        if (_allowedExactHosts.Contains(host))
            return true;
        foreach (var suffix in _allowedWildcardSuffixes)
        {
            if (host.Length > suffix.Length + 1
                && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && host[host.Length - suffix.Length - 1] == '.')
            {
                return true;
            }
        }
        return false;
    }

    private void AddAllowedHostPattern(string raw)
    {
        var clean = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(clean)) return;
        if (clean.StartsWith("*.", StringComparison.Ordinal) && clean.Length > 2)
            _allowedWildcardSuffixes.Add(clean[2..]);
        else
            _allowedExactHosts.Add(clean);
    }

    private void PruneStaleDailyCountsIfNeeded(string today)
    {
        if (string.Equals(_lastPrunedDate, today, StringComparison.Ordinal) && _dailyCounts.Count <= 10_000)
            return;

        lock (_dailyPruneGate)
        {
            if (string.Equals(_lastPrunedDate, today, StringComparison.Ordinal) && _dailyCounts.Count <= 10_000)
                return;
            foreach (var entry in _dailyCounts)
            {
                if (!string.Equals(entry.Value.DateKey, today, StringComparison.Ordinal))
                    _dailyCounts.TryRemove(entry.Key, out _);
            }
            _lastPrunedDate = today;
        }
    }

    private static bool IsValidSha256Hex(string? sha256Hex)
        => !string.IsNullOrWhiteSpace(sha256Hex)
           && sha256Hex.Trim().Length == 64
           && sha256Hex.Trim().All(Uri.IsHexDigit);

    private static bool IsAllowedExtension(string ext) => ext.ToLowerInvariant() is "png" or "gif" or "webp" or "jpg";

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

    private static bool TryBase64UrlDecode(string input, out byte[] decoded)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        try
        {
            decoded = Convert.FromBase64String(padded);
            // 拒绝末尾填充位异变或附带 '=' 等非规范 Base64Url 延展变体，防止绕过一次性 Ticket 摘要核销
            if (!string.Equals(Base64UrlEncode(decoded), input, StringComparison.Ordinal))
            {
                decoded = [];
                return false;
            }
            return true;
        }
        catch (FormatException)
        {
            decoded = [];
            return false;
        }
    }
}
