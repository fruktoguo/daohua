using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Server.Clean;

/// <summary>公开配表与轻量只读缓存端点的弱 ETag 条件响应复用工具。</summary>
public static class CatalogHttpCache
{
    internal const string FingerprintHeader = "X-Game-Data-Fingerprint";
    private const string CachePolicy = "public, max-age=300, stale-while-revalidate=60";

    /// <summary>按 UTF-8 JSON 正文计算弱 ETag，兼容 Caddy 的 identity/gzip/zstd 编码表示。</summary>
    public static EntityTagHeaderValue ComputeWeakETag(ReadOnlySpan<byte> body)
        => new($"\"{Convert.ToHexString(SHA256.HashData(body))}\"", isWeak: true);

    /// <summary>写出带弱 ETag 协商的预序列化 JSON 响应；命中 If-None-Match 时返回 304 空体。</summary>
    public static IResult WriteConditionalJson(HttpContext context, EntityTagHeaderValue tag, byte[] body, string cacheControl)
    {
        context.Response.Headers.CacheControl = cacheControl;
        context.Response.Headers.Vary = "Accept-Encoding";
        context.Response.GetTypedHeaders().ETag = tag;
        var headers = context.Request.GetTypedHeaders();
        foreach (var candidate in headers.IfNoneMatch)
        {
            if (candidate == EntityTagHeaderValue.Any || candidate.Compare(tag, useStrongComparison: false))
                return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        return Results.Bytes(body, "application/json; charset=utf-8");
    }

    internal static RouteHandlerBuilder MapCatalogGet<T>(this RouteGroupBuilder group, string route,
        Func<Domain.GameData.GameDataCatalog, T> select)
    {
        // GameDataCatalog 和 Web JSON 配置均为宿主单例；Lazy 缓存归属当前端点而非全局。
        var services = ((IEndpointRouteBuilder)group).ServiceProvider;
        var representation = new Lazy<CatalogResponse>(() =>
        {
            var data = services.GetRequiredService<Domain.GameData.GameDataCatalog>();
            var options = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
            var body = JsonSerializer.SerializeToUtf8Bytes(select(data), options);
            var etag = ComputeWeakETag(body);
            return new CatalogResponse(data.Fingerprint, etag, body);
        });

        return group.MapGet(route, (HttpContext context) => representation.Value.Write(context));
    }

    private sealed record CatalogResponse(string Fingerprint, EntityTagHeaderValue Tag, byte[] Body)
    {
        public IResult Write(HttpContext context)
        {
            context.Response.Headers[FingerprintHeader] = Fingerprint;
            return WriteConditionalJson(context, Tag, Body, CachePolicy);
        }
    }
}
