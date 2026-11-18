using System.Net;
using Domain.GameData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Server.Clean;
using Xunit;

namespace Server.Tests;

public sealed class CatalogHttpCacheTests
{
    [Fact]
    public async Task Catalog_WeakRevalidationSkipsBodyAndKeepsTheIndependentFingerprint()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var catalog = GameDataCatalog.LoadEmbedded();
        builder.Services.AddSingleton(catalog);
        await using var app = builder.Build();
        var builds = 0;
        var group = app.MapGroup("/catalog");
        group.MapCatalogGet("/one", _ => { Interlocked.Increment(ref builds); return new { hello = 1 }; });
        group.MapCatalogGet("/two", _ => new { hello = 2 });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var first = await client.GetAsync("/catalog/one");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("{\"hello\":1}", await first.Content.ReadAsStringAsync());
        var tag = Assert.IsType<System.Net.Http.Headers.EntityTagHeaderValue>(first.Headers.ETag);
        Assert.True(tag.IsWeak);
        Assert.Equal(catalog.Fingerprint, Assert.Single(first.Headers.GetValues(CatalogHttpCache.FingerprintHeader)));
        Assert.Contains("Accept-Encoding", first.Headers.Vary);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/catalog/one");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", $"\"stale\", {tag.Tag}");
        using var notModified = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(tag, notModified.Headers.ETag);
        Assert.Equal(catalog.Fingerprint, Assert.Single(notModified.Headers.GetValues(CatalogHttpCache.FingerprintHeader)));
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, builds);

        using var other = new HttpRequestMessage(HttpMethod.Get, "/catalog/two");
        other.Headers.IfNoneMatch.Add(tag);
        using var distinct = await client.SendAsync(other);
        Assert.Equal(HttpStatusCode.OK, distinct.StatusCode);
        Assert.NotEqual(tag, distinct.Headers.ETag);
    }
}
