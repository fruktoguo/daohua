using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Routing;

namespace Server.Clean.Social;

/// <summary>
/// 传音自定义表情包 REST 路由（/api/v1/chat/stickers* 与无状态图片中继 /api/v1/chat/sticker-fetch）：
/// - 统一要求已鉴权 Cookie 会话，严格从 ClaimsPrincipal 解析 player_id；
/// - 中继接口不落盘、不存库，仅解决第三方图片站无 CORS 头时浏览器无法读取图片二进制保存至本地 IndexedDB 的问题。
/// </summary>
public static class StickerEndpoints
{
    private const int MaxRelayImageBytes = 5 * 1024 * 1024;
    private const int MaxRedirects = 2;

    private static readonly HttpClient RelayHttpClient = CreateSsrfSafeHttpClient();

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v1/chat/stickers").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal user, PostgresStickerRepository repo, CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await repo.GetStateAsync(playerId, ct).ConfigureAwait(false));
            }
            catch (StickerOperationException ex)
            {
                return MapFailure(ex);
            }
        });

        group.MapPost("/sync", async (StickerSyncRequest request, ClaimsPrincipal user, PostgresStickerRepository repo, CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await repo.SyncAsync(playerId, request, ct).ConfigureAwait(false));
            }
            catch (StickerOperationException ex)
            {
                return MapFailure(ex);
            }
        }).RequireRateLimiting("command");

        group.MapPost("/delete", async (StickerDeleteRequest request, ClaimsPrincipal user, PostgresStickerRepository repo, CancellationToken ct) =>
        {
            if (!TryGetPlayerId(user, out var playerId)) return Results.Unauthorized();
            try
            {
                return Results.Ok(await repo.DeleteAsync(playerId, request, ct).ConfigureAwait(false));
            }
            catch (StickerOperationException ex)
            {
                return MapFailure(ex);
            }
        }).RequireRateLimiting("command");

        // 无状态 SSRF 防护图片抓取中继：仅当客户端直连外部图床受 CORS 限制时使用，不落盘不存库
        endpoints.MapGet("/api/v1/chat/sticker-fetch", async (
            string? url,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (user.Identity?.IsAuthenticated != true || !TryGetPlayerId(user, out _))
                return Results.Unauthorized();

            return await FetchExternalStickerAsync(url, ct).ConfigureAwait(false);
        }).RequireAuthorization().RequireRateLimiting("upload");
    }

    internal static async Task<IResult> FetchExternalStickerAsync(
        string? rawUrl,
        CancellationToken ct,
        HttpClient? httpClientOverride = null)
    {
        if (!TryValidatePublicHttpsUri(rawUrl, out var currentUri, out var validationError))
            return Results.BadRequest(new { title = "INVALID_STICKER_URL", detail = validationError });

        var client = httpClientOverride ?? RelayHttpClient;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (httpClientOverride is null && !await IsPublicDnsTargetAsync(currentUri!.DnsSafeHost, ct).ConfigureAwait(false))
                return Results.BadRequest(new { title = "BLOCKED_STICKER_HOST", detail = "禁止访问内网或受限主机地址。" });

            using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
            req.Headers.UserAgent.ParseAdd("DaoHuaStickerRelay/1.0");
            req.Headers.Accept.ParseAdd("image/webp,image/png,image/jpeg,image/gif,*/*;q=0.5");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            HttpResponseMessage resp;
            try
            {
                resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Json(new { title = "STICKER_FETCH_TIMEOUT", detail = "拉取外部表情包超时。" }, statusCode: 504);
            }
            catch (HttpRequestException)
            {
                return Results.Json(new { title = "STICKER_FETCH_FAILED", detail = "外部图片地址不可达或已被拦截。" }, statusCode: 502);
            }

            using (resp)
            {
                var status = (int)resp.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop == MaxRedirects || resp.Headers.Location is null)
                        return Results.Json(new { title = "STICKER_FETCH_FAILED", detail = "图片重定向次数过多。" }, statusCode: 502);

                    var nextUri = resp.Headers.Location.IsAbsoluteUri
                        ? resp.Headers.Location
                        : new Uri(currentUri!, resp.Headers.Location);
                    if (!TryValidatePublicHttpsUri(nextUri.AbsoluteUri, out var validatedNext, out var nextErr))
                        return Results.BadRequest(new { title = "INVALID_STICKER_URL", detail = nextErr });

                    currentUri = validatedNext;
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    var mappedCode = status is 404 or 410 ? 404 : 502;
                    return Results.Json(
                        new { title = mappedCode == 404 ? "STICKER_EXPIRED" : "STICKER_FETCH_FAILED", detail = $"外部图片返回状态 {status}。" },
                        statusCode: mappedCode);
                }

                if (resp.Content.Headers.ContentLength is > MaxRelayImageBytes)
                    return Results.Json(new { title = "STICKER_TOO_LARGE", detail = "图片体积超过 5 MB 限制。" }, statusCode: 413);

                await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
                var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeoutCts.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxRelayImageBytes)
                        return Results.Json(new { title = "STICKER_TOO_LARGE", detail = "图片体积超过 5 MB 限制。" }, statusCode: 413);
                    buffer.Write(chunk, 0, read);
                }

                var bytes = buffer.ToArray();
                var detectedMime = DetectSafeRasterMime(bytes);
                if (detectedMime is null)
                    return Results.Json(new { title = "UNSUPPORTED_IMAGE_FORMAT", detail = "目标资源不是有效的 PNG/JPEG/GIF/WebP 图片。" }, statusCode: 415);

                return new RelayImageResult(bytes, detectedMime);
            }
        }

        return Results.Json(new { title = "STICKER_FETCH_FAILED", detail = "拉取外部图片失败。" }, statusCode: 502);
    }

    internal static bool TryValidatePublicHttpsUri(string? rawUrl, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;
        var trimmed = rawUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 500)
        {
            error = "图片地址为空或过长。";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !parsed.IsDefaultPort
            || parsed.HostNameType != UriHostNameType.Dns)
        {
            error = "中继仅支持标准 HTTPS 域名直链。";
            return false;
        }

        var host = parsed.DnsSafeHost;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            error = "禁止请求本地保留域名。";
            return false;
        }

        uri = parsed;
        return true;
    }

    internal static async Task<bool> IsPublicDnsTargetAsync(string host, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.Length > 0 && addresses.All(IsPublicUnicastAddress);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsPublicUnicastAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            if (!address.TryWriteBytes(b, out _)) return false;
            return b[0] switch
            {
                0 => false,                                       // 0.0.0.0/8
                10 => false,                                      // 10.0.0.0/8
                100 when b[1] is >= 64 and <= 127 => false,       // 100.64.0.0/10 CGNAT
                127 => false,                                     // 127.0.0.0/8
                169 when b[1] == 254 => false,                    // 169.254.0.0/16 Link-Local
                172 when b[1] is >= 16 and <= 31 => false,        // 172.16.0.0/12
                192 when b[1] == 0 && b[2] == 0 => false,         // 192.0.0.0/24
                192 when b[1] == 0 && b[2] == 2 => false,         // 192.0.2.0/24 TEST-NET-1
                192 when b[1] == 168 => false,                    // 192.168.0.0/16
                198 when b[1] is 18 or 19 => false,               // 198.18.0.0/15
                198 when b[1] == 51 && b[2] == 100 => false,      // 198.51.100.0/24 TEST-NET-2
                203 when b[1] == 0 && b[2] == 113 => false,       // 203.0.113.0/24 TEST-NET-3
                >= 224 => false,                                  // 224.0.0.0/4 Multicast & Reserved
                _ => true
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6Teredo)
                return false;
            Span<byte> b = stackalloc byte[16];
            if (!address.TryWriteBytes(b, out _)) return false;
            // fc00::/7 Unique Local Address
            if ((b[0] & 0xfe) == 0xfc) return false;
            return true;
        }

        return false;
    }

    internal static string? DetectSafeRasterMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47
            && bytes[4] == 0x0d && bytes[5] == 0x0a && bytes[6] == 0x1a && bytes[7] == 0x0a)
            return "image/png";

        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
            return "image/jpeg";

        if (bytes.Length >= 6
            && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38
            && (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
            return "image/gif";

        if (bytes.Length >= 12
            && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return "image/webp";

        return null;
    }

    private static HttpClient CreateSsrfSafeHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (ctx, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false);
                var publicAddresses = addresses.Where(IsPublicUnicastAddress).ToArray();
                if (publicAddresses.Length == 0)
                    throw new HttpRequestException("Target host resolved to a restricted IP address.");

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(publicAddresses, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler);
    }

    private static bool TryGetPlayerId(ClaimsPrincipal user, out Guid playerId)
        => Guid.TryParse(user.FindFirstValue("player_id"), out playerId) && playerId != Guid.Empty;

    private static IResult MapFailure(StickerOperationException ex)
    {
        if (ex.Failure == StickerOperationFailure.LimitExceeded)
        {
            return Results.Json(new
            {
                code = "STICKER_LIMIT_EXCEEDED",
                title = "STICKER_LIMIT_EXCEEDED",
                detail = ex.Message,
                maxStickers = PostgresStickerRepository.MaxStickersPerPlayer,
                state = ex.CurrentState
            }, statusCode: StatusCodes.Status409Conflict);
        }

        return Results.BadRequest(new
        {
            code = "STICKER_INVALID_ARGUMENT",
            title = "STICKER_INVALID_ARGUMENT",
            detail = ex.Message
        });
    }

    private sealed class RelayImageResult(byte[] bytes, string contentType) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = contentType;
            httpContext.Response.ContentLength = bytes.Length;
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await httpContext.Response.Body.WriteAsync(bytes, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
