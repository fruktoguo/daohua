using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Domain.GameData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// /internal/compute-link 端点端到端回归：真实 Kestrel + ClientWebSocket 走通握手与收发循环。
/// 只装配端点必需的 ServerOptions / ComputeLinkRegistry / GameDataCatalog，不依赖数据库。
/// </summary>
public sealed class ComputeLinkEndpointTests
{
    private const string TestToken = "endpoint-test-token";
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);
    private static readonly GameDataCatalog Catalog = GameDataCatalog.LoadEmbedded();

    /// <summary>迟到/未知批次结果只应被节流记录并丢弃，不得断开整条链路。</summary>
    [Fact]
    public async Task LateResultFrameIsDroppedWithoutKillingLink()
    {
        await using var app = await StartHostAsync();
        var registry = app.Services.GetRequiredService<ComputeLinkRegistry>();
        using var client = await ConnectAsync(app);

        await SendAsync(client, new ComputeLinkHello(
            ComputeLinkContract.Version, "odin-test", 2, Catalog.Fingerprint));
        var welcome = Assert.IsType<ComputeLinkWelcome>(await ReceiveAsync(client));
        var node = Assert.Single(registry.Nodes, entry => entry.NodeId == welcome.NodeId);

        // 模拟派发器登记的在飞批次。
        var batchId = Guid.NewGuid();
        var waiter = new TaskCompletionSource<ComputeAdvanceBatchResponse>();
        node.Pending[batchId] = waiter;

        // 未知 batchId 的迟到结果与 response 缺失的畸形结果：都应丢弃且链路保持。
        await SendAsync(client, new ComputeLinkResult(Guid.NewGuid(), new ComputeAdvanceBatchResponse(
            ComputeDispatchContract.Version, Catalog.Fingerprint, 1, [])));
        await SendAsync(client, new ComputeLinkResult(Guid.NewGuid(), null));

        // 链路仍存活：真实在飞批次的结果能正常了结等待方。
        var expected = new ComputeAdvanceBatchResponse(
            ComputeDispatchContract.Version, Catalog.Fingerprint, 2, []);
        await SendAsync(client, new ComputeLinkResult(batchId, expected));
        var completed = await waiter.Task.WaitAsync(ReceiveTimeout);
        Assert.Equal(expected.ContractVersion, completed.ContractVersion);
        Assert.Equal(expected.ServerTimeUnixMilliseconds, completed.ServerTimeUnixMilliseconds);
        Assert.Equal(WebSocketState.Open, client.State);
    }

    /// <summary>hello 声明的批次承接能力必须透传进节点视图，供派发器按能力装配批次。</summary>
    [Fact]
    public async Task HelloNegotiatesBatchCapabilities()
    {
        await using var app = await StartHostAsync();
        var registry = app.Services.GetRequiredService<ComputeLinkRegistry>();
        using var client = await ConnectAsync(app);

        await SendAsync(client, new ComputeLinkHello(
            ComputeLinkContract.Version, "cap-node", 3, Catalog.Fingerprint, 96, 4L * 1024 * 1024,
            "Arm64/.NET 10.0.1-test"));
        var welcome = Assert.IsType<ComputeLinkWelcome>(await ReceiveAsync(client));
        var node = Assert.Single(registry.Nodes, entry => entry.NodeId == welcome.NodeId);
        Assert.Equal(3, node.Capacity);
        Assert.Equal(96, node.MaxJobsPerBatch);
        Assert.Equal(4L * 1024 * 1024, node.MaxBatchBytes);
        // v1.2：运行时指纹随 hello 透传进注册视图，供跨架构确定性差异诊断。
        Assert.Equal("Arm64/.NET 10.0.1-test", node.RuntimeFingerprint);
    }

    /// <summary>超出契约硬上限的能力声明必须判协议违规并断开，不得注册。</summary>
    [Fact]
    public async Task InvalidCapabilityHelloClosesLink()
    {
        await using var app = await StartHostAsync();
        var registry = app.Services.GetRequiredService<ComputeLinkRegistry>();
        using var client = await ConnectAsync(app);

        await SendAsync(client, new ComputeLinkHello(
            ComputeLinkContract.Version, "lying-node", 1, Catalog.Fingerprint,
            ComputeDispatchContract.HardMaxJobsPerBatch + 1, 0));

        // 协议违规路径：先回 LINK_ERROR 帧再以 PolicyViolation 关闭，节点不进注册表。
        var error = Assert.IsType<ComputeLinkError>(await ReceiveAsync(client));
        Assert.Equal("LINK_ERROR", error.Code);
        var close = Assert.IsType<WebSocketReceiveResult>(await ReceiveAsync(client));
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(0, registry.NodeCount);
    }

    /// <summary>令牌不符的拨入必须在升级前以 401 拒绝。</summary>
    [Fact]
    public async Task InvalidTokenIsRejectedWith401()
    {
        await using var app = await StartHostAsync();
        using var response = await SendUpgradeRequestAsync(app, "wrong-token-value");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>在线节点数达到上限时新拨入以 429 拒绝。</summary>
    [Fact]
    public async Task NodeLimitRejectsDialInWith429()
    {
        await using var app = await StartHostAsync();
        var registry = app.Services.GetRequiredService<ComputeLinkRegistry>();
        for (var index = 0; index < ComputeLinkContract.MaxConnectedNodes; index++)
            Assert.True(registry.TryRegister(new ComputeLinkNode($"filler-{index}", 1)));

        using var response = await SendUpgradeRequestAsync(app, TestToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    /// <summary>配表指纹不匹配：先回 error 帧再以 PolicyViolation 关闭，节点不进注册表。</summary>
    [Fact]
    public async Task FingerprintMismatchClosesLinkWithReason()
    {
        await using var app = await StartHostAsync();
        var registry = app.Services.GetRequiredService<ComputeLinkRegistry>();
        using var client = await ConnectAsync(app);

        await SendAsync(client, new ComputeLinkHello(
            ComputeLinkContract.Version, "stale-node", 1, "deadbeef"));

        var error = Assert.IsType<ComputeLinkError>(await ReceiveAsync(client));
        Assert.Equal("GAME_DATA_FINGERPRINT_MISMATCH", error.Code);

        var close = Assert.IsType<WebSocketReceiveResult>(await ReceiveAsync(client));
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
        Assert.Equal("GAME_DATA_FINGERPRINT_MISMATCH", close.CloseStatusDescription);
        Assert.Equal(0, registry.NodeCount);
    }

    /// <summary>装配最小宿主：仅端点所需单例 + WS 中间件 + compute-link 路由。</summary>
    private static async Task<WebApplication> StartHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(new ServerOptions(
            "server-tests", "Host=127.0.0.1;Database=unused",
            128, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
            64, TimeSpan.FromSeconds(1), TimeSpan.FromDays(30),
            64 * 1024, 128, 1, 0, false)
        {
            ComputeNodeToken = TestToken
        });
        builder.Services.AddSingleton<ComputeLinkRegistry>();
        builder.Services.AddSingleton(Catalog);

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
            KeepAliveTimeout = TimeSpan.FromSeconds(60)
        });
        app.Map(ComputeLinkContract.Path, ComputeLinkEndpoint.HandleAsync);
        await app.StartAsync();
        return app;
    }

    /// <summary>以合法令牌建立一条已升级的 compute-link 客户端连接。</summary>
    private static async Task<ClientWebSocket> ConnectAsync(WebApplication app)
    {
        var client = new ClientWebSocket();
        client.Options.SetRequestHeader(ComputeLinkContract.TokenHeader, TestToken);
        await client.ConnectAsync(LinkUri(app), CancellationToken.None);
        return client;
    }

    /// <summary>发一条带 WS 升级头的普通 HTTP 请求，用于精确断言升级前拒绝的状态码。</summary>
    private static async Task<HttpResponseMessage> SendUpgradeRequestAsync(WebApplication app, string? token)
    {
        var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, LinkUri(app));
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("endpoint-test-ky")));
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        if (token is not null)
            request.Headers.TryAddWithoutValidation(ComputeLinkContract.TokenHeader, token);
        var response = await http.SendAsync(request);
        http.Dispose();
        return response;
    }

    /// <summary>当前测试宿主的 compute-link WebSocket 地址（127.0.0.1 随机端口）。</summary>
    private static Uri LinkUri(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var uri = new Uri(address);
        return new Uri($"ws://{uri.Host}:{uri.Port}{ComputeLinkContract.Path}");
    }

    /// <summary>发送一条链路帧。</summary>
    private static Task SendAsync(WebSocket socket, object message) =>
        socket.SendAsync(ComputeLinkCodec.Encode(message), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>读取一帧：文本帧解码为链路消息，关闭帧直接返回供调用方断言。</summary>
    private static async Task<object> ReceiveAsync(WebSocket socket)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        var scratch = new byte[64 * 1024];
        while (true)
        {
            var received = await socket.ReceiveAsync(scratch, CancellationToken.None)
                .WaitAsync(ReceiveTimeout);
            if (received.MessageType == WebSocketMessageType.Close)
                return received;
            buffer.Write(scratch.AsSpan(0, received.Count));
            if (received.EndOfMessage) break;
        }
        return ComputeLinkCodec.Decode(buffer.WrittenMemory);
    }
}
