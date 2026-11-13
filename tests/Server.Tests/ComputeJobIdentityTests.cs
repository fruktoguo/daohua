using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 计算任务标识派生：纯哈希函数，不触数据库。
/// 归此而不进 PostgresIntegration 集合，避免该用例在 --fast 下被整类排除。
/// </summary>
public sealed class ComputeJobIdentityTests
{
    /// <summary>派生 job_id 必须对相同输入确定、对不同输入区分。</summary>
    [Fact]
    public void DeriveJobIdIsDeterministicAndDistinct()
    {
        var player = Guid.NewGuid();
        var first = PostgresComputeJobRepository.DeriveJobId(player, 1_700_000_000_000);
        Assert.Equal(first, PostgresComputeJobRepository.DeriveJobId(player, 1_700_000_000_000));
        Assert.NotEqual(first, PostgresComputeJobRepository.DeriveJobId(player, 1_700_000_000_001));
        Assert.NotEqual(first, PostgresComputeJobRepository.DeriveJobId(Guid.NewGuid(), 1_700_000_000_000));
    }

    /// <summary>
    /// 回归：提交头 last_simulated_at 只到毫秒（节点回传口径），与微秒精度收益边界差 &lt;1ms
    /// 时必须判为已结清——该幻影欠债曾让终末批玩家每轮再入队同一 job_id 被 SkippedExisting
    /// 挡回，永久占住扫描窗口并饿死全部真实候选。
    /// </summary>
    [Fact]
    public void ComputeBatchTargetTreatsSubMillisecondTerminalDebtAsSettled()
    {
        var anchor = new DateTimeOffset(2026, 11, 9, 13, 34, 35, TimeSpan.Zero).AddTicks(2_912_480); // .291248s
        var boundary = anchor.Add(Domain.Player.PlayerLifecycleRules.OfflineIdleLimit);
        var settledAt = DateTimeOffset.FromUnixTimeMilliseconds(boundary.ToUnixTimeMilliseconds());
        Assert.True(settledAt < boundary); // 复现生产截断形态：.291 < .291248
        var now = boundary.AddHours(2);
        Assert.Null(ColdCatchUpWorker.ComputeBatchTarget(settledAt, anchor, now, TimeSpan.FromHours(1)));
    }

    /// <summary>毫秒对齐不改变真实欠债语义：残债终末批仍补到压毫秒边界，非终末批照常按间隔结算。</summary>
    [Fact]
    public void ComputeBatchTargetStillSettlesRealDebt()
    {
        var anchor = new DateTimeOffset(2026, 11, 9, 13, 34, 35, TimeSpan.Zero).AddTicks(2_912_480);
        var boundary = anchor.Add(Domain.Player.PlayerLifecycleRules.OfflineIdleLimit);
        var floorBoundary = DateTimeOffset.FromUnixTimeMilliseconds(boundary.ToUnixTimeMilliseconds());
        var now = boundary.AddHours(2);
        // 终末批残债 ≥1ms：目标 = 压毫秒的边界。
        Assert.Equal(floorBoundary,
            ColdCatchUpWorker.ComputeBatchTarget(floorBoundary.AddMilliseconds(-1), anchor, now, TimeSpan.FromHours(1)));
        // 非终末批：欠债满批算间隔 → 目标 = 压毫秒的 now；不足间隔 → null。
        var anchorRecent = now.AddHours(-3).AddTicks(2_480);
        var floorNow = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        Assert.Equal(floorNow,
            ColdCatchUpWorker.ComputeBatchTarget(now.AddHours(-2), anchorRecent, now, TimeSpan.FromHours(1)));
        Assert.Null(ColdCatchUpWorker.ComputeBatchTarget(now.AddMinutes(-30), anchorRecent, now, TimeSpan.FromHours(1)));
    }

    /// <summary>
    /// 回归：装批同时遵守「字节硬预算 + 模拟时长软预算」，
    /// 超出模拟时长软预算的重任务不得与同批小任务挤占导致 30s 超时，而首条 48h 单任务必须放行不饿死。
    /// </summary>
    [Fact]
    public void PackHonorsByteHardCapAndSimulatedSpanSoftCapWithoutStarving48HourJob()
    {
        var maxBytes = 10_000L;
        var maxWork = (long)TimeSpan.FromHours(16).TotalMilliseconds;
        var h48 = (long)TimeSpan.FromHours(48).TotalMilliseconds;
        var h40 = (long)TimeSpan.FromHours(40).TotalMilliseconds;
        var h10 = (long)TimeSpan.FromHours(10).TotalMilliseconds;
        var h4 = (long)TimeSpan.FromHours(4).TotalMilliseconds;
        var h1 = (long)TimeSpan.FromHours(1).TotalMilliseconds;

        // 1. 首条 48h 单任务即使大于 16h 软预算也必须放行，后续 1h 进 overflow。
        var (fitsSingle, overflowSingle) = ComputeBatchBudget.Pack(
            [(Id: 1, Bytes: 100L, Work: h48), (Id: 2, Bytes: 100L, Work: h1)],
            item => item.Bytes, maxBytes, item => item.Work, maxWork);
        Assert.Equal([1], fitsSingle.Select(item => item.Id).ToArray());
        Assert.Equal([2], overflowSingle.Select(item => item.Id).ToArray());

        // 2. [10h, 40h, 4h] 在 16h 软预算下跳过 40h 放入 [10h, 4h]。
        var (fitsGreedy, overflowGreedy) = ComputeBatchBudget.Pack(
            [(Id: 1, Bytes: 100L, Work: h10), (Id: 2, Bytes: 100L, Work: h40), (Id: 3, Bytes: 100L, Work: h4)],
            item => item.Bytes, maxBytes, item => item.Work, maxWork);
        Assert.Equal([1, 3], fitsGreedy.Select(item => item.Id).ToArray());
        Assert.Equal([2], overflowGreedy.Select(item => item.Id).ToArray());

        // 3. 字节硬上限仍然严格生效：即使 Fits 为空，超过 maxBytes 也不得放行。
        var (fitsOversized, overflowOversized) = ComputeBatchBudget.Pack(
            [(Id: 1, Bytes: maxBytes + 1, Work: h1)],
            item => item.Bytes, maxBytes, item => item.Work, maxWork);
        Assert.Empty(fitsOversized);
        Assert.Equal([1], overflowOversized.Select(item => item.Id).ToArray());
    }
}
