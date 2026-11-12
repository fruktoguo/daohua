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
}
