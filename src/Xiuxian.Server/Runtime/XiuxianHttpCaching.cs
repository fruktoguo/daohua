namespace Xiuxian.Server.Runtime;

/// <summary>
/// 统一处理只读市场接口的条件响应，避免每个端点各自实现不一致的缓存判断。
/// </summary>
public static class XiuxianHttpCaching
{
    public static string CreateWeakTag(string scope, params object?[] components)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var value = string.Join(
            '-',
            components.Select(component => component?.ToString() ?? "null"));
        return $"W/\"{scope}-{value}\"";
    }

    public static bool IsNotModified(HttpRequest request, string etag)
    {
        foreach (var headerValue in request.Headers.IfNoneMatch)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) continue;
            foreach (var candidate in headerValue.Split(','))
            {
                var normalized = candidate.Trim();
                if (normalized == "*" || string.Equals(normalized, etag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static void ApplyHeaders(HttpResponse response, string etag)
    {
        response.Headers.ETag = etag;
        // 允许浏览器保留响应，但每次读取都必须向服务端验证版本。
        response.Headers.CacheControl = "no-cache, must-revalidate";
    }

    public static IResult NotModified(HttpResponse response, string etag)
    {
        ApplyHeaders(response, etag);
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }
}
