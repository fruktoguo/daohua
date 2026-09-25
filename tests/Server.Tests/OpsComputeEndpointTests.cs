using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    /// <summary>装配最小宿主：面板路由 + 端点实际读取的服务；不挂 UseOpsGate（门禁另有测试）。</summary>
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
        builder.Services.AddSingleton<PlayerRuntime>();
        builder.Services.AddSingleton<IPlayerRuntime>(services => services.GetRequiredService<PlayerRuntime>());
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<MarketRuntime>();
        builder.Services.AddSingleton(new OpsLogBuffer(100));

        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.MapOpsPanel(new OpsOptions(true, 5129, "127.0.0.1", "/ops", 500),
            "test-version", DateTimeOffset.UtcNow);
        await app.StartAsync();
        return app;
    }

    private static string BaseAddress(WebApplication app) =>
        app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
}
