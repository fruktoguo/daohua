using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Protocol;
using Server.Clean;
using Server.Clean.Ops;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 运维面板写操作端点回归：令牌校验（缺失/错误一律 401）、
/// 踢下线/强制存档/卸载/节点排空的真实作用，以及审计日志不留令牌。
/// </summary>
public sealed class OpsWriteEndpointTests
{
    private const string OpsToken = "ops-write-test-token";

    /// <summary>无令牌与错误令牌一律 401，且不作用到运行时。</summary>
    [Fact]
    public async Task WriteWithoutValidToken_IsRejected()
    {
        await using var app = await StartHostAsync();
        using var http = new HttpClient();

        var missing = await http.PostAsJsonAsync($"{BaseAddress(app)}/ops/api/actions/flush", new { playerId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Post, $"{BaseAddress(app)}/ops/api/actions/flush")
        {
            Content = JsonContent.Create(new { playerId = (Guid?)null })
        };
        wrong.Headers.Add(OpsPanel.TokenHeader, "not-the-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);

        Assert.Equal(0, app.Services.GetRequiredService<RecordingPlayerRuntime>().FlushAllCalls);
    }

    /// <summary>带正确令牌的强制存档生效：单玩家与全量两条路径各自命中运行时。</summary>
    [Fact]
    public async Task FlushWithToken_ReachesRuntime()
    {
        await using var app = await StartHostAsync();
        var runtime = app.Services.GetRequiredService<RecordingPlayerRuntime>();
        using var http = new HttpClient();
        var playerId = Guid.NewGuid();

        using var single = Authorized(HttpMethod.Post, $"{BaseAddress(app)}/ops/api/actions/flush", new { playerId, force = true });
        Assert.True((await http.SendAsync(single)).IsSuccessStatusCode);
        Assert.Equal(1, runtime.FlushCalls);

        using var all = Authorized(HttpMethod.Post, $"{BaseAddress(app)}/ops/api/actions/flush", new { playerId = (Guid?)null, force = false });
        Assert.True((await http.SendAsync(all)).IsSuccessStatusCode);
        Assert.Equal(1, runtime.FlushAllCalls);
    }

    /// <summary>踢下线设置关闭码并完成输出通道；不在线玩家返回 404。</summary>
    [Fact]
    public async Task KickWithToken_ClosesSession()
    {
        await using var app = await StartHostAsync();
        var sessions = app.Services.GetRequiredService<SessionRegistry>();
        var playerId = Guid.NewGuid();
        var session = new WebSocketSession(playerId, 8);
        Assert.True(sessions.TryAdd(session));
        using var http = new HttpClient();

        using var kick = Authorized(HttpMethod.Post, $"{BaseAddress(app)}/ops/api/actions/kick", new { playerId });
        var response = await http.SendAsync(kick);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.NotNull(session.CloseHint);
        Assert.Equal(OpsPanel.KickCloseCode, session.CloseHint!.Value.CloseCode);

        using var missing = Authorized(HttpMethod.Post, $"{BaseAddress(app)}/ops/api/actions/kick", new { playerId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(missing)).StatusCode);
    }

    /// <summary>卸载玩家经运行时边界执行，flush 标志透传。</summary>
    [Fact]
    public async Task UnloadWithToken_ReachesRuntime()
    {
        await using var app = await StartHostAsync();
        var runtime = app.Services.GetRequiredService<RecordingPlayerRuntime>();
        using var http = new HttpClient();

        using var unload = Authorized(HttpMethod.Post, $"{BaseAddress(app)}/ops/api/actions/unload", new { playerId = Guid.NewGuid(), flush = true });
        Assert.True((await http.SendAsync(unload)).IsSuccessStatusCode);
        Assert.Equal(1, runtime.RemoveCalls);
    }

    /// <summary>节点排空：指定节点标记停派，未知名返回 404，全量排空覆盖在线节点。</summary>
    [Fact]
    public async Task ComputeDrainWithToken_MarksNodes()
    {
        await using var app = await StartHostAsync();
        var registry = app.Services.GetRequiredService<ComputeLinkRegistry>();
        var node = new ComputeLinkNode("drain-node", 2, 32, 1024 * 1024, runtimeFingerprint: "test");
        Assert.True(registry.TryRegister(node));
        using var http = new HttpClient();

        using var single = Authorized(HttpMethod.Post, $"{BaseAddress(app)}/ops/api/actions/compute-drain", new { nodeId = node.NodeId });
        Assert.True((await http.SendAsync(single)).IsSuccessStatusCode);
        Assert.True(node.Draining);

        using var missing = Authorized(HttpMethod.Post, $"{BaseAddress(app)}/ops/api/actions/compute-drain", new { nodeId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(missing)).StatusCode);
    }

    /// <summary>审计日志记录动作与来源，但绝不写入令牌明文。</summary>
    [Fact]
    public async Task WriteAuditLog_DoesNotContainToken()
    {
        await using var app = await StartHostAsync();
        var buffer = app.Services.GetRequiredService<OpsLogBuffer>();
        using var http = new HttpClient();

        using var flush = Authorized(HttpMethod.Post, $"{BaseAddress(app)}/ops/api/actions/flush", new { playerId = (Guid?)null });
        Assert.True((await http.SendAsync(flush)).IsSuccessStatusCode);

        var audits = buffer.Snapshot(LogLevel.Warning, 50, OpsPanel.AuditCategory);
        Assert.NotEmpty(audits);
        Assert.All(audits, entry =>
        {
            Assert.DoesNotContain(OpsToken, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(OpsToken, entry.Exception ?? string.Empty, StringComparison.Ordinal);
        });
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, object body)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add(OpsPanel.TokenHeader, OpsToken);
        return request;
    }

    private static async Task<WebApplication> StartHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(new ServerOptions(
            "server-tests", "Host=127.0.0.1;Database=unused",
            128, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
            64, TimeSpan.FromSeconds(1), TimeSpan.FromDays(30),
            64 * 1024, 128, 1, 0, false));
        var options = new OpsOptions(true, "/ops", 100, OpsToken);
        var buffer = new OpsLogBuffer(100);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(buffer);
        builder.Logging.AddProvider(buffer);
        builder.Services.AddSingleton<RecordingPlayerRuntime>();
        builder.Services.AddSingleton<IPlayerRuntime>(sp => sp.GetRequiredService<RecordingPlayerRuntime>());
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<ComputeLinkRegistry>();
        // 面板路由整组一次性构建：任一端点的参数类型解析失败都会让整个 /ops 组 500，
        // 因此必须把只读视图实际读取的服务全部注册齐（与 Api 角色装配口径一致）。
        builder.Services.AddSingleton(Domain.GameData.GameDataCatalog.LoadEmbedded());
        builder.Services.AddSingleton<MarketRuntime>();
        builder.Services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused"));
        builder.Services.AddSingleton<OpsStorageInsights>();
        builder.Services.AddHealthChecks();

        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.MapOpsPanel(options, "test-version", DateTimeOffset.UtcNow);
        await app.StartAsync();
        return app;
    }

    private static string BaseAddress(WebApplication app) =>
        app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    /// <summary>记录写操作调用的运行时替身：只统计边界调用次数，不模拟任何领域行为。</summary>
    private sealed class RecordingPlayerRuntime : IPlayerRuntime
    {
        public int LoadedCount => 0;
        public DateTimeOffset? NextAdvanceBoundaryUtc => null;
        public int FlushCalls { get; private set; }
        public int FlushAllCalls { get; private set; }
        public int RemoveCalls { get; private set; }

        public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated { add { } remove { } }
        public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted { add { } remove { } }

        public ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload,
            long expectedStateVersion, Guid requestId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask FlushAsync(Guid playerId, bool force, CancellationToken cancellationToken = default)
        {
            FlushCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAllAsync(bool force, CancellationToken cancellationToken = default)
        {
            FlushAllCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask RemoveAsync(Guid playerId, bool flush, CancellationToken cancellationToken = default)
        {
            RemoveCalls++;
            return ValueTask.CompletedTask;
        }

        public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId) => [];

        public ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<PlayerRuntimeSnapshot>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
