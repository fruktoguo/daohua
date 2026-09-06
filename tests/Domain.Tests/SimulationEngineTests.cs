namespace Domain.Tests;

/// <summary>模拟引擎跨切分位级确定性、行动队列不变量与失效行动自愈回归。</summary>
public sealed class SimulationEngineTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "catalog-a");

    [Fact]
    public void SingleAdvanceMatchesPerSecondSplitBitwise()
    {
        var single = CreatePlayer("甲");
        var split = CreatePlayer("乙");
        single.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, ActionCatalog.Default);
        split.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, ActionCatalog.Default);

        SimulationEngine.AdvanceTo(single, Baseline.AddSeconds(10), ActionCatalog.Default);
        for (var i = 1; i <= 10; i++) SimulationEngine.AdvanceTo(split, Baseline.AddSeconds(i), ActionCatalog.Default);

        Assert.True(single.Progression.Qi == split.Progression.Qi, $"Qi 位级不一致：{single.Progression.Qi:R} != {split.Progression.Qi:R}");
        Assert.True(single.Progression.LifetimeQi == split.Progression.LifetimeQi);
        Assert.True(single.Progression.Experience == split.Progression.Experience);

        // 再推进一段未完成的进度，比较进行中的进度与进位余数。
        SimulationEngine.AdvanceTo(single, Baseline.AddSeconds(13), ActionCatalog.Default);
        SimulationEngine.AdvanceTo(split, Baseline.AddSeconds(13), ActionCatalog.Default);
        Assert.True(single.Actions[0].ProgressSeconds == split.Actions[0].ProgressSeconds);
        Assert.True(single.Progression.SimulationCarrySeconds == split.Progression.SimulationCarrySeconds);
    }

    [Fact]
    public void CultivationExperienceIsGrantedOnlyWhenRepeatCompletes()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("甲");
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(9), catalog.Actions);
        Assert.Equal(9, player.Progression.Qi);
        Assert.Equal(0, player.Progression.Experience);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog.Actions);
        Assert.Equal(10, player.Progression.Qi);
        Assert.Equal(10, player.Progression.Experience);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(19), catalog.Actions);
        Assert.Equal(19, player.Progression.Qi);
        Assert.Equal(10, player.Progression.Experience);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(20), catalog.Actions);
        Assert.Equal(20, player.Progression.Qi);
        Assert.Equal(20, player.Progression.Experience);
    }

    [Fact]
    public void FractionalSecondSplitMatchesWholeSecondBitwise()
    {
        var single = CreatePlayer("甲");
        var split = CreatePlayer("乙");
        single.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi")), Baseline, ActionCatalog.Default);
        split.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi")), Baseline, ActionCatalog.Default);

        SimulationEngine.AdvanceTo(single, Baseline.AddSeconds(1), ActionCatalog.Default);
        for (var i = 1; i <= 10; i++) SimulationEngine.AdvanceTo(split, Baseline.AddSeconds(0.1 * i), ActionCatalog.Default);

        // 0.1 累加十次为 0.9999999999999999 < 1.0，必须靠贴整获得同一个整秒。
        Assert.True(single.Progression.Qi == split.Progression.Qi, $"Qi 位级不一致：{single.Progression.Qi:R} != {split.Progression.Qi:R}");
        Assert.True(single.Progression.Qi == 1.0, $"单秒修为应恰为 1：{single.Progression.Qi:R}");
        Assert.True(single.Actions[0].ProgressSeconds == split.Actions[0].ProgressSeconds);
        Assert.True(single.Actions[0].ProgressSeconds == 1.0);
        Assert.Equal(0, split.Progression.SimulationCarrySeconds);
    }

    [Fact]
    public void InfiniteActionStaysQueuedUnderBothSplitStyles()
    {
        var single = CreatePlayer("甲");
        var split = CreatePlayer("乙");
        var singleAction = single.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, ActionCatalog.Default);
        var splitAction = split.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, ActionCatalog.Default);

        SimulationEngine.AdvanceTo(single, Baseline.AddSeconds(23), ActionCatalog.Default);
        for (var i = 1; i <= 23; i++) SimulationEngine.AdvanceTo(split, Baseline.AddSeconds(i), ActionCatalog.Default);

        Assert.Single(single.Actions);
        Assert.Single(split.Actions);
        Assert.Equal(singleAction.Id, single.Actions[0].Id);
        Assert.Equal(splitAction.Id, split.Actions[0].Id);
        Assert.Equal(-1, single.Actions[0].RemainingRepeats);
        Assert.Equal(-1, split.Actions[0].RemainingRepeats);
        Assert.True(single.Progression.Qi == split.Progression.Qi);
    }

    [Fact]
    public void RewardCarryPersistsAcrossSnapshotRoundTrip()
    {
        var player = CreatePlayer("甲");
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi")), Baseline, ActionCatalog.Default);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(0.5), ActionCatalog.Default);
        Assert.Equal(0, player.Progression.Qi);
        Assert.Equal(0.5, player.Progression.SimulationCarrySeconds);

        // 旧存档无 carry 字段时默认 0；跨存档恢复后剩余半秒与下一秒合并为一个整秒。
        var restored = PlayerAggregate.Restore(player.ExportState());
        Assert.Equal(0.5, restored.Progression.SimulationCarrySeconds);
        SimulationEngine.AdvanceTo(restored, Baseline.AddSeconds(1), ActionCatalog.Default);

        Assert.True(restored.Progression.Qi == 1.0, $"半秒余数应合并为一个整秒：{restored.Progression.Qi:R}");
    }

    [Fact]
    public void SimulationNeverDrivesRemainingRepeatsNegative()
    {
        var player = CreatePlayer("甲");
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 3), Baseline, ActionCatalog.Default);

        for (var i = 1; i <= 40; i++)
        {
            SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(i), ActionCatalog.Default);
            Assert.All(player.Actions, action => Assert.True(action.RemainingRepeats == -1 || action.RemainingRepeats >= 1));
        }

        Assert.Empty(player.Actions);
    }

    [Fact]
    public void ZeroDurationHeadActionFromHotReloadIsSelfHealedAndQueueContinues()
    {
        // 入队时目录时长合法，配表热更后变为 0：存量 -1 行动在推进时按自愈规约移出队列。
        var enqueueCatalog = new ActionCatalog([
            new ActionDefinition(new ActionId("test.broken"), "被热更行动", ActionKind.Cultivation, 2, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(), 1),
            new ActionDefinition(new ActionId("test.valid"), "正常行动", ActionKind.Cultivation, 2, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(), 1)
        ]);
        var hotCatalog = new ActionCatalog([
            new ActionDefinition(new ActionId("test.broken"), "被热更行动", ActionKind.Cultivation, 0, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(), 1),
            new ActionDefinition(new ActionId("test.valid"), "正常行动", ActionKind.Cultivation, 2, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(), 1)
        ]);
        var player = CreatePlayer("甲");
        player.Enqueue(new ActionIntent(new ActionId("test.broken"), -1), Baseline, enqueueCatalog);
        player.Enqueue(new ActionIntent(new ActionId("test.valid"), 1), Baseline, enqueueCatalog);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(6), hotCatalog);

        Assert.Empty(player.Actions);
        var dropped = Assert.Single(result.Events, entry => entry.Code == "action.invalid-dropped");
        Assert.Equal(new ActionId("test.broken"), dropped.ActionId);
        Assert.Equal(1, result.CompletedRepeats);
        Assert.True(player.Progression.Qi == 2.0, $"只有有效行动应累积修为：{player.Progression.Qi:R}");
    }

    [Fact]
    public void LegalZeroDurationFiniteActionIsSelfHealedInsteadOfSpinning()
    {
        // 合法配置 duration=0 + 有限大 RepeatCount：入队不受限，推进时同样自愈而不是空转到转换上限。
        var catalog = new ActionCatalog([
            new ActionDefinition(new ActionId("test.instant"), "瞬时行动", ActionKind.Alchemy, 0, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>()),
            new ActionDefinition(new ActionId("test.valid"), "正常行动", ActionKind.Cultivation, 2, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(), 1)
        ]);
        var player = CreatePlayer("甲");
        player.Enqueue(new ActionIntent(new ActionId("test.instant"), 1_000_000), Baseline, catalog);
        player.Enqueue(new ActionIntent(new ActionId("test.valid"), 1), Baseline, catalog);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(4), catalog);

        Assert.Empty(player.Actions);
        Assert.Single(result.Events, entry => entry.Code == "action.invalid-dropped");
        Assert.Equal(1, result.CompletedRepeats);
        Assert.True(player.Progression.Qi == 2.0);
    }
}
