// ==============================================================================
// 文件名称: HttpCaching.cs
// 主要职责: 提供 HTTP 条件缓存与 ETag 协商辅助工具方法
// 核心逻辑 / 定义内容:
//   1. 为只读市场深度、指标与玩家订单接口生成弱实体标签 (Weak ETag)。
//   2. 解析客户端 If-None-Match 请求头并进行匹配校验。
//   3. 注入 Cache-Control 协商缓存头并构造高效的 304 Not Modified 响应。
// ==============================================================================

namespace Server.Runtime;

/// <summary>
/// HTTP 条件缓存与 ETag 协商工具类 (HttpCaching)。
/// <para>
/// 职责：
/// 1. 为只读市场深度、指标与玩家订单接口生成弱实体标签 (Weak ETag)，组合版本修订号、品种与序列号。
/// 2. 解析客户端 <c>If-None-Match</c> 请求头并进行匹配校验。
/// 3. 设置 <c>Cache-Control: no-cache, must-revalidate</c> 并在命中缓存时高效返回 304 Not Modified。
/// </para>
/// </summary>
public static class HttpCaching
{
    /// <summary>
    /// 根据业务作用域与状态要素拼接生成弱 ETag 字符串（格式：<c>W/"scope-comp1-comp2..."</c>）。
    /// </summary>
    public static string CreateWeakTag(string scope, params object?[] components)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var value = string.Join(
            '-',
            components.Select(component => component?.ToString() ?? "null"));
        return $"W/\"{scope}-{value}\"";
    }

    /// <summary>
    /// 检查客户端请求的 If-None-Match 头是否包含当前 ETag。
    /// </summary>
    public static bool IsNotModified(HttpRequest request, string etag)
    {
        var targetSpan = etag.AsSpan();
        foreach (var headerValue in request.Headers.IfNoneMatch)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) continue;
            var span = headerValue.AsSpan();
            while (!span.IsEmpty)
            {
                var commaIndex = span.IndexOf(',');
                ReadOnlySpan<char> candidate;
                if (commaIndex >= 0)
                {
                    candidate = span[..commaIndex].Trim();
                    span = span[(commaIndex + 1)..];
                }
                else
                {
                    candidate = span.Trim();
                    span = default;
                }

                if (candidate.SequenceEqual("*") || candidate.SequenceEqual(targetSpan))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 在 HTTP 响应头中注入 ETag 与协商缓存控制头。
    /// </summary>
    public static void ApplyHeaders(HttpResponse response, string etag)
    {
        response.Headers.ETag = etag;
        // 允许浏览器保留响应，但每次读取都必须向服务端验证版本。
        response.Headers.CacheControl = "no-cache, must-revalidate";
    }

    /// <summary>
    /// 构造 304 Not Modified 响应结果。
    /// </summary>
    public static IResult NotModified(HttpResponse response, string etag)
    {
        ApplyHeaders(response, etag);
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }
}
