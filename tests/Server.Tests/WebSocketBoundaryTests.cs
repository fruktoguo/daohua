using System.Net.WebSockets;
using Domain.GameData;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Protocol;
using Server.Clean;
using Xunit;

namespace Server.Tests;

public sealed class WebSocketBoundaryTests
{
    [Theory]
    [InlineData(null, true)] // 已鉴权的非浏览器工具。
    [InlineData("https://game.example:443", true)]
    [InlineData("https://client.example:8443", true)]
    [InlineData("https://client.example", false)]
    [InlineData("http://game.example", false)]
    [InlineData("https://other.game.example", false)]
    [InlineData("null", false)]
    [InlineData("https://game.example/path", false)]
    public void PlayerWebSocketRequiresOneFullAllowedOrigin(string? origin, bool allowed)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = "unused",
            ["Server:WebSocketAllowedOrigins:0"] = "https://client.example:8443"
        }).Build();
        var options = ServerOptions.From(configuration);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("game.example");
        if (origin is not null) context.Request.Headers.Origin = origin;
        Assert.Equal(allowed, WebSocketEndpoint.IsAllowedPlayerWebSocketOrigin(context, options));
        context.Request.Headers.Origin = new[] { "https://game.example", "https://client.example:8443" };
        Assert.False(WebSocketEndpoint.IsAllowedPlayerWebSocketOrigin(context, options));
    }

    [Fact]
    public void SessionTokenSelectionUsesCookieWithBearerFallback()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer test-bearer";
        Assert.Equal("test-bearer", SessionAuthenticationDefaults.ReadPresentedToken(context));
        context.Request.Headers.Cookie = $"{SessionAuthenticationDefaults.CookieName}=test-cookie";
        Assert.Equal("test-cookie", SessionAuthenticationDefaults.ReadPresentedToken(context));
    }

    [Fact]
    public async Task ComputeHandshakeSlotIsReturnedWhenUpgradeFails()
    {
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton(GameDataCatalog.LoadEmbedded())
            .AddSingleton<ComputeLinkRegistry>()
            .AddSingleton(ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Storage:ConnectionString"] = "unused" }).Build()))
            .BuildServiceProvider();
        for (var i = 0; i < 17; i++)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Features.Set<IHttpWebSocketFeature>(new FailingAccept());
            context.Request.Headers[ComputeLinkContract.TokenHeader] = "unit-test";
            await Assert.ThrowsAsync<IOException>(() => ComputeLinkEndpoint.HandleAsync(context));
        }
    }

    private sealed class FailingAccept : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) =>
            Task.FromException<WebSocket>(new IOException("升级替身失败"));
    }
}
