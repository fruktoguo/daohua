using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>验证只读缓存端点（配表、版本探针、全表行情统计）复用的弱 ETag 条件响应协商。</summary>
public sealed class CatalogHttpCacheTests
{
    [Fact]
    public async Task WriteConditionalJson_Returns200OnMiss_And304OnMatchingWeakOrStrongIfNoneMatch()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var body = Encoding.UTF8.GetBytes("[{\"assetId\":\"item.ningxue-grass\"}]");
        var tag = CatalogHttpCache.ComputeWeakETag(body);

        var missContext = new DefaultHttpContext { RequestServices = services };
        missContext.Response.Body = new MemoryStream();
        await CatalogHttpCache.WriteConditionalJson(missContext, tag, body, "private, no-cache").ExecuteAsync(missContext);
        Assert.Equal(StatusCodes.Status200OK, missContext.Response.StatusCode);
        Assert.Equal(tag.ToString(), missContext.Response.Headers.ETag.ToString());
        Assert.Equal(body.Length, missContext.Response.Body.Length);

        var hitContext = new DefaultHttpContext { RequestServices = services };
        hitContext.Request.Headers.IfNoneMatch = tag.ToString();
        hitContext.Response.Body = new MemoryStream();
        await CatalogHttpCache.WriteConditionalJson(hitContext, tag, body, "private, no-cache").ExecuteAsync(hitContext);
        Assert.Equal(StatusCodes.Status304NotModified, hitContext.Response.StatusCode);
        Assert.Equal(0, hitContext.Response.Body.Length);
    }
}
