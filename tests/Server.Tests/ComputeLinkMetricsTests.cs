using Microsoft.Extensions.Diagnostics.HealthChecks;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// compute-link 派发链观测指标回归：计数聚合、节点视图、批次样本环边界与健康检查口径。
/// 全部进程内断言，不依赖数据库。
/// </summary>
public sealed class ComputeLinkMetricsTests
{
    /// <summary>节点接入/拒绝/迟到结果与任务结局计数必须精确聚合并经快照暴露。</summary>
    [Fact]
    public void CountersAggregateIntoSnapshot()
    {
        var metrics = new ComputeLinkMetrics();
        var node = new ComputeLinkNode("alpha", 2);

        metrics.RecordNodeConnected();
        metrics.RecordNodeConnected();
        metrics.RecordNodeDisconnected();
        metrics.RecordTokenRejected();
        metrics.RecordNodeLimitRejected();
        metrics.RecordFingerprintRejected();
        metrics.RecordHandshakeRejected();
        metrics.RecordLateResultDropped();
        metrics.RecordLateResultDropped();

        metrics.RecordJobsClaimed(7);
        metrics.RecordJobsReleased(3);
        metrics.RecordJobApplied(node);
        metrics.RecordJobDriftDiscarded();
        metrics.RecordJobNodeFailed(node);
        metrics.RecordJobPersistFailed();
        metrics.RecordJobDead(node);

        var snapshot = metrics.Snapshot();
        Assert.Equal(2, snapshot.NodesConnected);
        Assert.Equal(1, snapshot.NodesDisconnected);
        Assert.Equal(1, snapshot.TokenRejected);
        Assert.Equal(1, snapshot.NodeLimitRejected);
        Assert.Equal(1, snapshot.FingerprintRejected);
        Assert.Equal(1, snapshot.HandshakeRejected);
        Assert.Equal(2, snapshot.LateResultsDropped);
        Assert.Equal(7, snapshot.JobsClaimed);
        Assert.Equal(3, snapshot.JobsReleased);
        Assert.Equal(1, snapshot.JobsApplied);
        Assert.Equal(1, snapshot.JobsDriftDiscarded);
        Assert.Equal(1, snapshot.JobsNodeFailed);
        Assert.Equal(1, snapshot.JobsPersistFailed);
        Assert.Equal(1, snapshot.JobsDead);

        // 任务结局同步进节点级计数：运维面板的每节点失败率由此导出。
        Assert.Equal(1, node.JobsApplied);
        Assert.Equal(2, node.JobsFailed);
    }

    /// <summary>批次写帧/完成/失败打点必须同步节点侧吞吐与往返时延统计。</summary>
    [Fact]
    public void BatchLifecycleUpdatesNodeAndSamples()
    {
        var metrics = new ComputeLinkMetrics();
        var node = new ComputeLinkNode("beta", 4);

        metrics.RecordBatchWritten(node, 8, 4096);
        metrics.RecordBatchCompleted(node, 8, 32_000, TimeSpan.FromMilliseconds(120), 5_000);
        metrics.RecordBatchWritten(node, 4, 2048);
        metrics.RecordBatchFailed(node, ComputeLinkMetrics.BatchFailureKind.Timeout);

        Assert.Equal(2, node.BatchesSent);
        Assert.Equal(1, node.BatchesCompleted);
        Assert.Equal(1, node.BatchesFailed);
        Assert.Equal(6_144, node.BytesSent);
        Assert.Equal(120, node.LastRoundTripMs);
        Assert.Equal(120, node.AvgRoundTripMs);
        Assert.Equal(120, node.MaxRoundTripMs);

        var snapshot = metrics.Snapshot();
        Assert.Equal(2, snapshot.BatchesWritten);
        Assert.Equal(1, snapshot.BatchesCompleted);
        Assert.Equal(1, snapshot.BatchesFailedTimeout);
        Assert.Equal(12, snapshot.JobsDispatched);
        Assert.Equal(1, snapshot.RecentBatches.SampleCount);
        Assert.Equal(120, snapshot.RecentBatches.RoundTripP50Ms);
        Assert.Equal(120, snapshot.RecentBatches.RoundTripMaxMs);
        Assert.Equal(5_000, snapshot.RecentBatches.WaitAgeP50Ms);
        Assert.Equal(8, snapshot.RecentBatches.JobsP50);
        Assert.Equal(32_000, snapshot.RecentBatches.BytesP50);
        Assert.Equal(1, snapshot.RecentBatches.JobsHistogram["5-16"]);
    }

    /// <summary>失败分类枚举必须落到对应计数桶。</summary>
    [Fact]
    public void BatchFailuresBucketByKind()
    {
        var metrics = new ComputeLinkMetrics();
        var node = new ComputeLinkNode("gamma", 1);
        metrics.RecordBatchFailed(node, ComputeLinkMetrics.BatchFailureKind.Write);
        metrics.RecordBatchFailed(node, ComputeLinkMetrics.BatchFailureKind.Timeout);
        metrics.RecordBatchFailed(node, ComputeLinkMetrics.BatchFailureKind.Disconnect);
        metrics.RecordBatchFailed(node, ComputeLinkMetrics.BatchFailureKind.Contract);
        metrics.RecordBatchFailed(node, ComputeLinkMetrics.BatchFailureKind.Error);

        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.BatchesFailedWrite);
        Assert.Equal(1, snapshot.BatchesFailedTimeout);
        Assert.Equal(1, snapshot.BatchesFailedDisconnect);
        Assert.Equal(1, snapshot.BatchesFailedContract);
        Assert.Equal(1, snapshot.BatchesFailedError);
        Assert.Equal(5, node.BatchesFailed);
    }

    /// <summary>批次样本环必须有界：超过容量时只保留最近 256 个，分布统计不随运行时长增长。</summary>
    [Fact]
    public void SampleRingIsBoundedAtCapacity()
    {
        var metrics = new ComputeLinkMetrics();
        var node = new ComputeLinkNode("delta", 64);
        for (var index = 0; index < 300; index++)
        {
            var jobs = index % 70 == 0 ? 70 : 1; // 少量大样本校验 65+ 桶
            metrics.RecordBatchCompleted(node, jobs, 1024, TimeSpan.FromMilliseconds(index % 5), index);
        }

        var summary = metrics.Snapshot().RecentBatches;
        Assert.Equal(256, summary.SampleCount);
        Assert.Equal(256, summary.JobsHistogram.Values.Sum());
        Assert.True(summary.JobsHistogram["65+"] > 0);
        Assert.True(summary.RoundTripMaxMs <= 4);
    }

    /// <summary>节点视图必须透出拨入时刻/在线时长/运行时指纹/在飞与等待数，供运维面板渲染。</summary>
    [Fact]
    public void NodeViewReflectsLiveRuntimeState()
    {
        var node = new ComputeLinkNode("view-node", 4, 96, 4L * 1024 * 1024,
            runtimeFingerprint: "Arm64/.NET 10.0.1");
        Assert.True(node.TryReserveSlot());
        node.Pending[Guid.NewGuid()] = new TaskCompletionSource<ComputeAdvanceBatchResponse>();
        node.Pending[Guid.NewGuid()] = new TaskCompletionSource<ComputeAdvanceBatchResponse>();

        var view = ComputeLinkNodeView.From(node);
        Assert.Equal(node.NodeId, view.NodeId);
        Assert.Equal("view-node", view.Name);
        Assert.Equal(4, view.Capacity);
        Assert.Equal(1, view.InFlight);
        Assert.Equal(3, view.Available);
        Assert.Equal(2, view.PendingResults);
        Assert.Equal(node.ConnectedAt, view.ConnectedAt);
        Assert.True(view.UptimeSeconds >= 0);
        Assert.Equal("Arm64/.NET 10.0.1", view.RuntimeFingerprint);
        Assert.False(view.Draining);
        Assert.Equal(96, node.MaxJobsPerBatch);
        Assert.Equal(4L * 1024 * 1024, node.MaxBatchBytes);
        // v1.3 排空态必须透出到运维视图：停派节点对外表现为零可用。
        node.BeginDrain();
        view = ComputeLinkNodeView.From(node);
        Assert.True(view.Draining);
        Assert.Equal(0, view.Available);
        node.ReleaseSlot();
    }

    /// <summary>注册表节点生命周期必须同步 metrics 计数（DI 注入路径）。</summary>
    [Fact]
    public void RegistryLifecycleFeedsMetrics()
    {
        var metrics = new ComputeLinkMetrics();
        var registry = new ComputeLinkRegistry(metrics);
        var node = new ComputeLinkNode("life", 1);

        Assert.True(registry.TryRegister(node));
        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.NodesConnected);
        Assert.Equal(0, snapshot.NodesDisconnected);

        registry.Unregister(node);
        snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.NodesDisconnected);
        Assert.Equal(0, registry.NodeCount);
    }

    /// <summary>健康检查只反映链路容量事实：未启用 Healthy、启用无节点 Degraded、有节点 Healthy。</summary>
    [Fact]
    public async Task HealthCheckReflectsNodePresence()
    {
        var registry = new ComputeLinkRegistry();
        var disabled = new ComputeLinkHealthCheck(Options(computeEnabled: false), registry);
        Assert.Equal(HealthStatus.Healthy,
            (await disabled.CheckHealthAsync(new HealthCheckContext())).Status);

        var enabled = new ComputeLinkHealthCheck(Options(computeEnabled: true), registry);
        var degraded = await enabled.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, degraded.Status);

        Assert.True(registry.TryRegister(new ComputeLinkNode("hc", 1)));
        var healthy = await enabled.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, healthy.Status);
        // 检查数据只含计数，不含节点名/IP 等可识别信息以外的字段（本身也只是计数）。
        Assert.Equal(1, (int)healthy.Data["nodes"]);
    }

    private static ServerOptions Options(bool computeEnabled) => new(
        "server-tests", "Host=127.0.0.1;Database=unused",
        128, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
        64, TimeSpan.FromSeconds(1), TimeSpan.FromDays(30),
        64 * 1024, 128, 1, 0, false)
    {
        ComputeNodeEnabled = computeEnabled,
        ComputeNodeToken = computeEnabled ? "hc-token" : null
    };
}
