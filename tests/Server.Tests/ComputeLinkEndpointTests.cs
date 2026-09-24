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

    /// <summary>令牌不符的拨入：hello 阶段回 UNAUTHORIZED 错误帧并以 PolicyViolation 关闭，节点不进注册表。</summary>
    [Fact]
    public async Task InvalidTokenIsRejectedAfterHello()
    {
        await using var app = await StartHostAsync();
        var registry = app.Services.GetRequiredService<ComputeLinkRegistry>();
        using var client = await ConnectAsync(app, "wrong-token-value");

        await SendAsync(client, new ComputeLinkHello(
            ComputeLinkContract.Version, "bad-token-node", 1, Catalog.Fingerprint));

        var error = Assert.IsType<ComputeLinkError>(await ReceiveAsync(client));
        Assert.Equal("UNAUTHORIZED", error.Code);
        var close = Assert.IsType<WebSocketReceiveResult>(await ReceiveAsync(client));
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
        Assert.Equal(0, registry.NodeCount);
    }

    /// <summary>命中 ComputeNode:Tokens 映射的节点只认映射令牌：共享令牌对该节点失效。</summary>
    [Fact]
    public async Task MappedNodeRequiresItsOwnToken()
    {
        await using var app = await StartHostAsync(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["odin"] = "odin-secret" });
        var registry = app.Services.GetRequiredService<ComputeLinkRegistry>();

        // 映射令牌 → 正常接入。
        using (var client = await ConnectAsync(app, "odin-secret"))
        {
            await SendAsync(client, new ComputeLinkHello(
                ComputeLinkContract.Version, "odin", 1, Catalog.Fingerprint));
            var welcome = Assert.IsType<ComputeLinkWelcome>(await ReceiveAsync(client));
            Assert.Single(registry.Nodes, entry => entry.NodeId == welcome.NodeId);
        }
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (registry.NodeCount > 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);

        // 共享令牌对命中映射的节点失效 → UNAUTHORIZED。
        using (var client = await ConnectAsync(app, TestToken))
        {
            await SendAsync(client, new ComputeLinkHello(
                ComputeLinkContract.Version, "odin", 1, Catalog.Fingerprint));
            var error = Assert.IsType<ComputeLinkError>(await ReceiveAsync(client));
            Assert.Equal("UNAUTHORIZED", error.Code);
        }

        // 未命中映射的节点回退共享令牌 → 正常接入。
        using (var client = await ConnectAsync(app, TestToken))
        {
            await SendAsync(client, new ComputeLinkHello(
                ComputeLinkContract.Version, "other-node", 1, Catalog.Fingerprint));
            var welcome = Assert.IsType<ComputeLinkWelcome>(await ReceiveAsync(client));
            Assert.Single(registry.Nodes, entry => entry.NodeId == welcome.NodeId);
        }
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

    /// <summary>令牌拒绝与握手拒绝必须进入指标计数，供运维面板观测拒绝原因分布。</summary>
    [Fact]
    public async Task RejectionsAreCountedInMetrics()
    {
        await using var app = await StartHostAsync();
        var metrics = app.Services.GetRequiredService<ComputeLinkMetrics>();

        // 令牌不符：hello 后 UNAUTHORIZED 拒绝。
        using (var client = await ConnectAsync(app, "bad-token"))
        {
            await SendAsync(client, new ComputeLinkHello(
                ComputeLinkContract.Version, "bad-token", 1, Catalog.Fingerprint));
            var error = Assert.IsType<ComputeLinkError>(await ReceiveAsync(client));
            Assert.Equal("UNAUTHORIZED", error.Code);
        }

        // 握手期协议违规（首帧不是 hello 的契约版本）：计数 handshakeRejected。
        using (var client = await ConnectAsync(app))
        {
            await SendAsync(client, new ComputeLinkHello("compute-link.v0.9", "old", 1, Catalog.Fingerprint));
            var error = Assert.IsType<ComputeLinkError>(await ReceiveAsync(client));
            Assert.Equal("LINK_ERROR", error.Code);
        }

        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.TokenRejected);
        Assert.Equal(1, snapshot.HandshakeRejected);
        Assert.Equal(0, snapshot.NodesConnected);
    }

    /// <summary>成功接入与断开必须计入节点生命周期指标；迟到结果进入丢弃计数。</summary>
    [Fact]
    public async Task NodeLifecycleAndLateResultsAreCounted()
    {
        await using var app = await StartHostAsync();
        var registry = app.Services.GetRequiredService<ComputeLinkRegistry>();
        var metrics = app.Services.GetRequiredService<ComputeLinkMetrics>();

        using (var client = await ConnectAsync(app))
        {
            await SendAsync(client, new ComputeLinkHello(
                ComputeLinkContract.Version, "metric-node", 2, Catalog.Fingerprint));
            var welcome = Assert.IsType<ComputeLinkWelcome>(await ReceiveAsync(client));
            Assert.Single(registry.Nodes, entry => entry.NodeId == welcome.NodeId);

            // 未知 batchId 的结果帧被丢弃计数。
            await SendAsync(client, new ComputeLinkResult(Guid.NewGuid(), new ComputeAdvanceBatchResponse(
                ComputeDispatchContract.Version, Catalog.Fingerprint, 1, [])));
            await Task.Delay(300);
        }
        // 客户端 dispose 触发对端关闭；等服务端注销落定。
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (registry.NodeCount > 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);

        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.NodesConnected);
        Assert.Equal(1, snapshot.NodesDisconnected);
        Assert.Equal(1, snapshot.LateResultsDropped);
    }

    /// <summary>节点上报 draining（compute-link v1.3）后必须立即被 Acquire 排除，链路保持回收在飞结果。</summary>
    [Fact]
    public async Task DrainingFrameExcludesNodeFromAcquire()
    {
        await using var app = await StartHostAsync();
        var registry = app.Services.GetRequiredService<ComputeLinkRegistry>();
        using var client = await ConnectAsync(app);

        await SendAsync(client, new ComputeLinkHello(
            ComputeLinkContract.Version, "draining-node", 2, Catalog.Fingerprint));
        var welcome = Assert.IsType<ComputeLinkWelcome>(await ReceiveAsync(client));
        var node = Assert.Single(registry.Nodes, entry => entry.NodeId == welcome.NodeId);
        Assert.NotNull(registry.Acquire());
        node.ReleaseSlot();

        await SendAsync(client, new ComputeLinkDraining("node maintenance"));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!node.Draining && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);

        Assert.True(node.Draining);
        Assert.Equal(0, node.Available);
        Assert.Null(registry.Acquire());
        // 排空不是断链：在飞批次结果照常回收，链路保持。
        Assert.Equal(WebSocketState.Open, client.State);
    }

    /// <summary>主服 DrainAll 必须把 drain 帧下推到每条在线链路并把全部节点标记停派。</summary>
    [Fact]
    public async Task DrainAllPushesDrainFrameToNodes()
    {
        await using var app = await StartHostAsync();
        var registry = app.Services.GetRequiredService<ComputeLinkRegistry>();
        using var client = await ConnectAsync(app);

        await SendAsync(client, new ComputeLinkHello(
            ComputeLinkContract.Version, "drained-node", 2, Catalog.Fingerprint));
        var welcome = Assert.IsType<ComputeLinkWelcome>(await ReceiveAsync(client));
        var node = Assert.Single(registry.Nodes, entry => entry.NodeId == welcome.NodeId);

        registry.DrainAll("master draining");

        var frame = Assert.IsType<ComputeLinkDrain>(await ReceiveAsync(client));
        Assert.Equal("master draining", frame.Reason);
        Assert.True(node.Draining);
        Assert.Null(registry.Acquire());
    }

    /// <summary>装配最小宿主：仅端点所需单例 + WS 中间件 + compute-link 路由。</summary>
    private static async Task<WebApplication> StartHostAsync(
        IReadOnlyDictionary<string, string>? nodeTokens = null)
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
            ComputeNodeToken = TestToken,
            ComputeNodeTokens = nodeTokens ?? new Dictionary<string, string>()
        });
        builder.Services.AddSingleton<ComputeLinkMetrics>();
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

    /// <summary>以指定令牌（默认合法令牌）建立一条已升级的 compute-link 客户端连接。</summary>
    private static async Task<ClientWebSocket> ConnectAsync(WebApplication app, string token = TestToken)
    {
        var client = new ClientWebSocket();
        client.Options.SetRequestHeader(ComputeLinkContract.TokenHeader, token);
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
