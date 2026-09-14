using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 对抗性压测：长时离线挂机（24 小时 / 10 万次行动推进）、GC 内存分配、浮点有限性与边界保护。
/// </summary>
public sealed class AdversarialSimulationStressTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 验证 <c>LongOfflineSimulation_24Hours_ExecutesDeterministicallyWithoutOverflow</c>（Long Offline Simulation 24Hours Executes Deterministically Without Overflow）所描述的领域行为。
    /// </summary>
    [Fact]
    public void LongOfflineSimulation_24Hours_ExecutesDeterministicallyWithoutOverflow()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "长生者", Baseline, "catalog-a");
        // 24 小时 = 86400 秒，打坐 10 秒一轮，-1 无限循环
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, ActionCatalog.Default);

        var target = Baseline.AddHours(24);
        var beforeMem = GC.GetAllocatedBytesForCurrentThread();
        var result = SimulationEngine.AdvanceTo(player, target, ActionCatalog.Default);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeMem;

        Assert.True(result.Changed);
        Assert.Equal(86400, result.ElapsedSeconds);
        // 每轮 10 秒，共 8640 轮
        Assert.Equal(8640, result.CompletedActions);
        Assert.Equal(8640, result.CompletedRepeats);
        Assert.Single(player.Actions);
        Assert.Equal(-1, player.Actions[0].RemainingRepeats);

        // 浮点数值有限性与正确性验证
        Assert.True(double.IsFinite(player.Progression.Qi));
        Assert.True(double.IsFinite(player.Progression.LifetimeQi));
        Assert.True(player.Progression.Qi > 0);
        Assert.Equal(player.Progression.Qi, player.Progression.LifetimeQi);

        // 验证单次 24 小时快进产生的内存分配在极低合理范围内 (< 10MB)
        Assert.True(allocated < 10 * 1024 * 1024, $"24小时快进内存分配超标: {allocated} bytes");
    }

    /// <summary>
    /// 验证 <c>MaxTransitionsPerAdvance_BoundaryProtectionEnforced</c>（Max Transitions Per Advance Boundary Protection Enforced）所描述的领域行为。
    /// </summary>
    [Fact]
    public void MaxTransitionsPerAdvance_BoundaryProtectionEnforced()
    {
        // 模拟 1 秒一次的行动，在单次 AdvanceTo 中推进 120,000 秒 (> 100,000 次转换限制) 时必须快速失败
        var fastCatalog = new ActionCatalog([
            new ActionDefinition(new ActionId("test.1sec"), "1秒行动", ActionKind.Cultivation, 1, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(), 1)
        ]);

        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "极速者", Baseline, "catalog-a");
        player.Enqueue(new ActionIntent(new ActionId("test.1sec"), -1), Baseline, fastCatalog);

        // 推进 120,000 秒 -> 120,000 次转换 > 100,000
        var ex = Assert.Throws<DomainException>(() =>
            SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(120_000), fastCatalog));

        Assert.Equal(DomainErrorCode.InvariantViolation, ex.Code);
        Assert.Contains("单次模拟超过最大状态转换数", ex.Message);
    }

    /// <summary>
    /// 验证 <c>MaxTransitionsPerAdvance_Under100k_SucceedsCleanly</c>（Max Transitions Per Advance Under 100k Succeeds Cleanly）所描述的领域行为。
    /// </summary>
    [Fact]
    public void MaxTransitionsPerAdvance_Under100k_SucceedsCleanly()
    {
        // 50,000 次转换应稳定通过且无死锁
        var fastCatalog = new ActionCatalog([
            new ActionDefinition(new ActionId("test.1sec"), "1秒行动", ActionKind.Cultivation, 1, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(), 1)
        ]);

        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "极速者2", Baseline, "catalog-a");
        player.SetLoopEnabled(false);
        player.Enqueue(new ActionIntent(new ActionId("test.1sec"), 50_000), Baseline, fastCatalog);

        // 推进 50,000 秒 -> 50,000 次转换
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(50_000), fastCatalog);

        Assert.True(result.Changed);
        Assert.Equal(50000, result.CompletedActions);
        Assert.Empty(player.Actions);
    }
}
