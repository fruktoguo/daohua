using System.IO;
using Domain.GameData;
using System.Text.Json;
using Domain.Player;
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
/// 运维面板 /ops/api/compute 端点回归：节点组视图、计数快照与安全红线
/// （响应不得携带令牌/连接串/玩家数据）。
/// </summary>

public sealed class OpsComputeEndpointTests
{
    /// <summary>格式边界拒绝未知资产、非法变体、非有限值及重复键；收益可信性另由独立重演测试守门。</summary>
    [Fact]
    public void ComputeAssetDeltaFormatValidationRejectsMalformedRows()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var request = new ComputeAdvanceJobRequest(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "测试玩家", 1, 7, 1000, 2000,
            "checksum", catalog.Fingerprint, [],
            [new ComputeAssetBalance("item.ningxue-grass", "", 3, 1)]);
        var playerId = request.PlayerId;

        Assert.Throws<InvalidDataException>(() => ComputeDispatcherWorker.ValidateAndMergeAssetDeltas(
            playerId, request, [new ComputeAssetDelta("item.audit-forged", "", -1)], catalog));
        Assert.Throws<InvalidDataException>(() => ComputeDispatcherWorker.ValidateAndMergeAssetDeltas(
            playerId, request, [new ComputeAssetDelta("item.ningxue-grass", "+1", -1)], catalog));
        Assert.Throws<InvalidDataException>(() => ComputeDispatcherWorker.ValidateAndMergeAssetDeltas(
            playerId, request, [new ComputeAssetDelta("item.ningxue-grass", "", double.NaN)], catalog));
        Assert.Throws<InvalidDataException>(() => ComputeDispatcherWorker.ValidateAndMergeAssetDeltas(
            playerId, request, [new ComputeAssetDelta("item.ningxue-grass", "", 1), new ComputeAssetDelta("item.ningxue-grass", "", 1)], catalog));
        Assert.Equal(1, Assert.Single(ComputeDispatcherWorker.ValidateAndMergeAssetDeltas(
            playerId, request, [new ComputeAssetDelta("item.ningxue-grass", "", 1)], catalog)).Delta);

        Assert.Empty(ComputeDispatcherWorker.ValidateAndMergeAssetDeltas(playerId, request, null, catalog));
    }
    private const string ComputeToken = "ops-endpoint-secret-token";

    /// <summary>端点必须暴露节点名/容量/在飞/拨入时长/运行时指纹与链路计数，且不泄露令牌。</summary>
    [Fact]
    public async Task ComputeApiExposesNodeViewsWithoutSecrets()
    {
        await using var app = await StartHostAsync();
        var registry = app.Services.GetRequiredService<ComputeLinkRegistry>();
        var metrics = app.Services.GetRequiredService<ComputeLinkMetrics>();

        var node = new ComputeLinkNode("ops-node", 4, 96, 4L * 1024 * 1024,
            runtimeFingerprint: "Arm64/.NET 10.0.1");
        Assert.True(registry.TryRegister(node));
        Assert.True(node.TryReserveSlot());
        metrics.RecordBatchWritten(node, 8, 4096);
        metrics.RecordBatchCompleted(node, 8, 32_000, TimeSpan.FromMilliseconds(120), 5_000);

        using var http = new HttpClient();
        var response = await http.GetAsync($"{BaseAddress(app)}/ops/api/compute");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);

        // 安全红线：响应全文不得出现令牌或连接串片段。
        Assert.DoesNotContain(ComputeToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Database=unused", body, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(ComputeLinkContract.Version, root.GetProperty("contractVersion").GetString());
        Assert.Equal(ComputeDispatchContract.Version, root.GetProperty("dispatchContractVersion").GetString());

        var totals = root.GetProperty("totals");
        Assert.Equal(1, totals.GetProperty("nodeCount").GetInt32());
        Assert.Equal(4, totals.GetProperty("capacity").GetInt32());
        Assert.Equal(1, totals.GetProperty("inFlight").GetInt32());

        var view = Assert.Single(root.GetProperty("nodes").EnumerateArray());
        Assert.Equal("ops-node", view.GetProperty("name").GetString());
        Assert.Equal(node.NodeId, view.GetProperty("nodeId").GetGuid());
        Assert.Equal(4, view.GetProperty("capacity").GetInt32());
        Assert.Equal(1, view.GetProperty("inFlight").GetInt32());
        Assert.Equal(3, view.GetProperty("available").GetInt32());
        Assert.Equal("Arm64/.NET 10.0.1", view.GetProperty("runtimeFingerprint").GetString());
        Assert.True(view.GetProperty("uptimeSeconds").GetDouble() >= 0);
        Assert.Equal(1, view.GetProperty("batchesCompleted").GetInt64());
        Assert.Equal(4_096, view.GetProperty("bytesSent").GetInt64());

        var snapshot = root.GetProperty("metrics");
        Assert.Equal(1, snapshot.GetProperty("nodesConnected").GetInt64());
        Assert.Equal(1, snapshot.GetProperty("batchesWritten").GetInt64());
        Assert.Equal(1, snapshot.GetProperty("recentBatches").GetProperty("sampleCount").GetInt32());

        // 测试宿主未装配任务仓库：backlog 输出 null 而非报错。
        Assert.Equal(JsonValueKind.Null, root.GetProperty("backlog").ValueKind);
    }

    /// <summary>派发链未启用且未装配时端点仍可用：enabled=false、空节点组、计数为零。</summary>
    [Fact]
    public async Task ComputeApiReportsDisabledWhenNotConfigured()
    {
        await using var app = await StartHostAsync(computeEnabled: false);
        using var http = new HttpClient();
        using var document = JsonDocument.Parse(
            await http.GetByteArrayAsync($"{BaseAddress(app)}/ops/api/compute"));

        var root = document.RootElement;
        Assert.False(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, root.GetProperty("totals").GetProperty("nodeCount").GetInt32());
        Assert.Empty(root.GetProperty("nodes").EnumerateArray());
        Assert.Equal(0, root.GetProperty("metrics").GetProperty("nodesConnected").GetInt64());
    }

    /// <summary>装配最小宿主：面板路由 + 端点实际读取的服务（面板已并入主端口，无独立端口门禁）。</summary>
    private static async Task<WebApplication> StartHostAsync(bool computeEnabled = true)
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
            ComputeNodeEnabled = computeEnabled,
            ComputeNodeToken = computeEnabled ? ComputeToken : null
        });
        builder.Services.AddSingleton<ComputeLinkMetrics>();
        builder.Services.AddSingleton<ComputeLinkRegistry>();
        builder.Services.AddHealthChecks();
        // 面板其他端点的参数类型必须可在 DI 推断为服务（懒构造，本测试不请求它们，
        // 只保证路由元数据推断不把复杂类型误判为 Body）。
        builder.Services.AddSingleton<Domain.GameData.GameDataCatalog>();
        builder.Services.AddSingleton<IPlayerRuntime, UnusedPlayerRuntime>();
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<MarketRuntime>();
        builder.Services.AddSingleton(new OpsLogBuffer(100));
        builder.Services.AddSingleton(new OpsOptions(true, "/ops", 500, "test-ops-token"));
        // 面板路由的复杂类型参数必须在 DI 中可解析，否则最小 API 会把它推断成请求体而整体构建失败。
        builder.Services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused"));
        builder.Services.AddSingleton<OpsStorageInsights>();

        builder.Services.AddSingleton(new Server.Clean.AccountAbuse.AccountRiskPolicyRegistry());
        builder.Services.AddSingleton(new Server.Clean.AccountAbuse.AccountRiskHasher("ops-endpoint-tests"));
        builder.Services.AddSingleton<Server.Clean.AccountAbuse.PostgresAccountRiskRepository>();
        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.MapOpsPanel(new OpsOptions(true, "/ops", 500, "test-ops-token"),
            "test-version", DateTimeOffset.UtcNow);
        await app.StartAsync();
        return app;
    }

    private static string BaseAddress(WebApplication app) =>
        app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    /// <summary>仅为路由元数据推断占位的 IPlayerRuntime 空实现；本测试不请求玩家端点，成员永不调用。</summary>
    private sealed class UnusedPlayerRuntime : IPlayerRuntime
    {
        public int LoadedCount => 0;
        public DateTimeOffset? NextAdvanceBoundaryUtc => null;
        public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated { add { } remove { } }
        public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted { add { } remove { } }
        public ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public PlayerRuntimeSnapshot? TryGetLoaded(Guid playerId) => null;
        public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload,
            long expectedStateVersion, Guid requestId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask FlushAsync(Guid playerId, bool force, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask FlushAllAsync(bool force, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask RemoveAsync(Guid playerId, bool flush, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<PlayerRuntimeSnapshot> MutateOwnedAsync(Guid playerId, Action<PlayerAggregate> mutate, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId) => [];
        public ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<PlayerRuntimeSnapshot>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
