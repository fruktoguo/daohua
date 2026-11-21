using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DaoHua.Server.Clean.TapTap;

/// <summary>TapTap OAuth 2.0 配置项。</summary>
public sealed class TapTapOptions
{
    public const string SectionName = "TapTap";

    /// <summary>开发者控制台申请的 Client ID (公钥)，须通过配置或环境变量注入。</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>开发者控制台申请的 Client Secret (私钥)，须通过配置或环境变量注入，禁止硬编码。</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>OAuth 2.0 网页授权回调默认重定向地址。</summary>
    public string RedirectUri { get; set; } = "https://daohua.yuohira.com/api/v1/auth/taptap/callback";

    /// <summary>允许的额外回调重定向地址白名单（未配置时仅允许默认 RedirectUri）。</summary>
    public string[] AllowedRedirectUris { get; set; } = [];
}

/// <summary>TapTap 授权换取 Token 的原始凭据。</summary>
public sealed record TapTapAccessToken(
    string Kid,
    string TokenType,
    string MacKey,
    string MacAlgorithm);

/// <summary>TapTap 用户个人资料。</summary>
public sealed record TapTapUserProfile(
    string OpenId,
    string? UnionId,
    string? Name,
    string? Avatar);

/// <summary>TapTap OAuth 2.0 授权、凭据交换与 MAC Token 签算服务。</summary>
public sealed class TapTapAuthService
{
    private readonly HttpClient _httpClient;
    private readonly TapTapOptions _options;
    private readonly ILogger<TapTapAuthService> _logger;

    public TapTapAuthService(
        HttpClient httpClient,
        IOptions<TapTapOptions> options,
        ILogger<TapTapAuthService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>TapTap 网页 OAuth 所需的 ClientId 与 ClientSecret 是否均已配置。</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ClientId)
        && !string.IsNullOrWhiteSpace(_options.ClientSecret);

    /// <summary>校验并解析允许的回调重定向地址；未传自定义地址时回落到服务端配置默认值。</summary>
    public bool TryResolveAllowedRedirectUri(string? customRedirectUri, out string resolvedRedirectUri)
    {
        if (string.IsNullOrWhiteSpace(customRedirectUri))
        {
            resolvedRedirectUri = _options.RedirectUri;
            return !string.IsNullOrWhiteSpace(resolvedRedirectUri);
        }

        var candidate = customRedirectUri.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri)
            || (candidateUri.Scheme != Uri.UriSchemeHttps && candidateUri.Scheme != Uri.UriSchemeHttp))
        {
            resolvedRedirectUri = string.Empty;
            return false;
        }

        if (MatchesAllowedUri(candidateUri, _options.RedirectUri))
        {
            resolvedRedirectUri = _options.RedirectUri;
            return true;
        }

        if (_options.AllowedRedirectUris is { Length: > 0 })
        {
            foreach (var allowed in _options.AllowedRedirectUris)
            {
                if (MatchesAllowedUri(candidateUri, allowed))
                {
                    resolvedRedirectUri = allowed.Trim();
                    return true;
                }
            }
        }

        resolvedRedirectUri = string.Empty;
        return false;
    }

    private static bool MatchesAllowedUri(Uri candidateUri, string? allowedRaw)
    {
        if (string.IsNullOrWhiteSpace(allowedRaw)) return false;
        if (!Uri.TryCreate(allowedRaw.Trim(), UriKind.Absolute, out var allowedUri)) return false;
        return string.Equals(candidateUri.Scheme, allowedUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidateUri.Host, allowedUri.Host, StringComparison.OrdinalIgnoreCase)
            && candidateUri.Port == allowedUri.Port
            && string.Equals(candidateUri.AbsolutePath, allowedUri.AbsolutePath, StringComparison.Ordinal);
    }

    /// <summary>构造 TapTap 网页授权跳转地址。</summary>
    public string BuildAuthorizeUrl(string state, string? customRedirectUri = null)
    {
        if (!TryResolveAllowedRedirectUri(customRedirectUri, out var redirect))
            throw new ArgumentException("回调地址不在 TapTap 授权白名单内。", nameof(customRedirectUri));
        var encodedRedirect = Uri.EscapeDataString(redirect);
        var encodedState = Uri.EscapeDataString(state);
        return $"https://www.taptap.cn/oauth2/v2/auth?client_id={_options.ClientId}&response_type=code&redirect_uri={encodedRedirect}&scope=public_profile&state={encodedState}";
    }

    /// <summary>使用授权码 code 换取 Access Token (包含 kid 与 mac_key)。</summary>
    public async Task<TapTapAccessToken?> ExchangeCodeForTokenAsync(string code, string? customRedirectUri = null, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("TapTap OAuth is not configured (missing ClientId or ClientSecret).");
            return null;
        }

        if (!TryResolveAllowedRedirectUri(customRedirectUri, out var redirect))
        {
            _logger.LogWarning("Rejected non-whitelisted TapTap redirectUri.");
            return null;
        }
        var parameters = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirect
        };

        try
        {
            using var content = new FormUrlEncodedContent(parameters);
            using var response = await _httpClient.PostAsync("https://accounts.tapapis.cn/oauth2/v1/token", content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TapTap token exchange failed. Status: {Status}, Body: {Body}", response.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
            {
                _logger.LogWarning("TapTap token response indicated failure: {Body}", body);
                return null;
            }

            if (!root.TryGetProperty("data", out var data))
                return null;

            var kid = data.GetProperty("kid").GetString();
            var tokenType = data.TryGetProperty("token_type", out var tt) ? tt.GetString() ?? "mac" : "mac";
            var macKey = data.GetProperty("mac_key").GetString();
            var macAlgorithm = data.TryGetProperty("mac_algorithm", out var ma) ? ma.GetString() ?? "hmac-sha-1" : "hmac-sha-1";

            if (string.IsNullOrWhiteSpace(kid) || string.IsNullOrWhiteSpace(macKey))
                return null;

            return new TapTapAccessToken(kid, tokenType, macKey, macAlgorithm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during TapTap code token exchange.");
            return null;
        }
    }

    /// <summary>使用 kid 和 mac_key 签算 MAC Token 并获取用户 Profile。</summary>
    public async Task<TapTapUserProfile?> GetUserProfileAsync(TapTapAccessToken token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId))
        {
            _logger.LogWarning("TapTap ClientId is not configured.");
            return null;
        }

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("D10");
        var nonce = GenerateRandomNonce(8);
        var method = "GET";
        var uri = $"/account/profile/v1?client_id={_options.ClientId}";
        var host = "open.tapapis.cn";
        var port = "443";

        var authHeader = ComputeMacAuthorization(token.Kid, token.MacKey, ts, nonce, method, uri, host, port);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}{uri}");
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);

            using var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TapTap get profile failed. Status: {Status}, Body: {Body}", response.StatusCode, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var successProp) || !successProp.GetBoolean())
            {
                _logger.LogWarning("TapTap profile response indicated failure: {Body}", body);
                return null;
            }

            if (!root.TryGetProperty("data", out var data))
                return null;

            var openid = data.GetProperty("openid").GetString();
            if (string.IsNullOrWhiteSpace(openid))
                return null;

            var unionid = data.TryGetProperty("unionid", out var u) ? u.GetString() : null;
            var name = data.TryGetProperty("name", out var n) ? n.GetString() : null;
            var avatar = data.TryGetProperty("avatar", out var a) ? a.GetString() : null;

            return new TapTapUserProfile(openid, unionid, name, avatar);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during TapTap get profile.");
            return null;
        }
    }

    /// <summary>
    /// TapTap 官方标准 MAC Token 签算算法。
    /// 待签字符串必须格式为：{ts}\n{nonce}\n{method}\n{uri}\n{host}\n{port}\n\n (末尾连续两个换行)
    /// </summary>
    public static string ComputeMacAuthorization(
        string kid,
        string macKey,
        string timestamp,
        string nonce,
        string method,
        string uri,
        string host,
        string port)
    {
        var signingString = $"{timestamp}\n{nonce}\n{method}\n{uri}\n{host}\n{port}\n\n";
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(macKey));
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingString));
        var mac = Convert.ToBase64String(hashBytes);

        return $"MAC id=\"{kid}\",ts=\"{timestamp}\",nonce=\"{nonce}\",mac=\"{mac}\"";
    }

    private static string GenerateRandomNonce(int length)
    {
        const string chars = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            sb.Append(chars[bytes[i] % chars.Length]);
        }
        return sb.ToString();
    }
}
