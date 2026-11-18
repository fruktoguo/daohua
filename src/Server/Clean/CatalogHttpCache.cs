using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Server.Clean;

/// <summary>公开配表按进程内不可变目录生成；每个端点只计算一次实际 JSON 表示。</summary>
internal static class CatalogHttpCache
{
    internal const string FingerprintHeader = "X-Game-Data-Fingerprint";
    private const string CachePolicy = "public, max-age=300, stale-while-revalidate=60";

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
            // 弱标签让 Caddy 的 identity/gzip/zstd 表示可用同一校验标签。
            var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{Convert.ToHexString(SHA256.HashData(body))}\"", isWeak: true);
            return new CatalogResponse(data.Fingerprint, etag, body);
        });

        return group.MapGet(route, (HttpContext context) => representation.Value.Write(context));
    }

    private sealed record CatalogResponse(string Fingerprint, Microsoft.Net.Http.Headers.EntityTagHeaderValue Tag, byte[] Body)
    {
        public IResult Write(HttpContext context)
        {
            context.Response.Headers[FingerprintHeader] = Fingerprint;
            context.Response.Headers.CacheControl = CachePolicy;
            context.Response.Headers.Vary = "Accept-Encoding";
            context.Response.GetTypedHeaders().ETag = Tag;
            var headers = context.Request.GetTypedHeaders();
            foreach (var candidate in headers.IfNoneMatch)
            {
                if (candidate == Microsoft.Net.Http.Headers.EntityTagHeaderValue.Any || candidate.Compare(Tag, useStrongComparison: false))
                    return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            return Results.Bytes(Body, "application/json; charset=utf-8");
        }
    }
}
