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
    public void SustainedCultivationConsumableMatchesOfflineAndPerSecondAdvance()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var single = CreatePlayer("整段");
        var split = CreatePlayer("逐秒");
        var pill = new ItemId("item.qi-gathering-pill");
        var singleWallet = CreateWallet(single, (pill, 1d));
        var splitWallet = CreateWallet(split, (pill, 1d));
        single.ConfigureAutoUse("cultivation", 0, pill.Value, 1, true);
        split.ConfigureAutoUse("cultivation", 0, pill.Value, 1, true);
        single.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);
        split.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);

        var singleResult = SimulationEngine.AdvanceTo(single, Baseline.AddSeconds(20), catalog, singleWallet);
        foreach (var delta in singleResult.AssetDeltas) singleWallet.Apply(delta);
        for (var second = 1; second <= 20; second++)
        {
            var result = SimulationEngine.AdvanceTo(split, Baseline.AddSeconds(second), catalog, splitWallet);
            foreach (var delta in result.AssetDeltas) splitWallet.Apply(delta);
        }

        Assert.Equal(26d, single.Progression.Qi, 10);
        Assert.Equal(26d, single.Progression.Experience, 10);
        Assert.Equal(single.Progression.Qi, split.Progression.Qi);
        Assert.Equal(single.Progression.Experience, split.Progression.Experience);
        Assert.Equal(280d, Assert.Single(single.Settings.AutoUseSlots).RemainingDurationSeconds);
        Assert.Equal(280d, Assert.Single(split.Settings.AutoUseSlots).RemainingDurationSeconds);
        Assert.Equal(0d, singleWallet.Get(new AssetKey(pill)));
        Assert.Equal(0d, splitWallet.Get(new AssetKey(pill)));
        var debit = Assert.Single(singleResult.AssetDeltas);
        Assert.Equal(pill, debit.Asset.ItemId);
        Assert.Equal(-1d, debit.QuantityDelta);
    }

    [Fact]
    public void SustainedConsumableOverdraftBuffsCurrentRepeatThenRequiresNextItem()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("透支");
        player.ConfigureAutoUse("cultivation", 0, "item.qi-gathering-pill", 1, true);
        player.Settings.AutoUseSlots[0] = player.Settings.AutoUseSlots[0] with { RemainingDurationSeconds = 3d };
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(20), catalog, CreateWallet(player));

        Assert.Empty(result.AssetDeltas);
        Assert.Equal(23d, player.Progression.Qi, 10);
        Assert.Equal(23d, player.Progression.Experience, 10);
        Assert.Equal(0d, player.Settings.AutoUseSlots[0].RemainingDurationSeconds);
        Assert.Single(result.Events, entry => entry.Code == "auto-use.depleted");
    }

    [Fact]
    public void SustainedConsumableDoesNotActivateMidActionAfterStockArrives()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("中途补货");
        var pill = new ItemId("item.qi-gathering-pill");
        var wallet = CreateWallet(player);
        player.ConfigureAutoUse("cultivation", 0, pill.Value, 1, true);
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);

        var firstHalf = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(0.5), catalog, wallet);
        wallet.Credit(new AssetKey(pill), 1, "test grant");
        var secondHalf = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(1), catalog, wallet);

        Assert.Empty(firstHalf.AssetDeltas);
        Assert.Empty(secondHalf.AssetDeltas);
        Assert.Equal(1d, player.Progression.Qi);
        Assert.Equal(0d, player.Settings.AutoUseSlots[0].RemainingDurationSeconds);

        var next = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(11), catalog, wallet);
        Assert.Contains(next.AssetDeltas, delta => delta.Asset.ItemId == pill && delta.QuantityDelta == -1d);
        Assert.Equal(11.3d, player.Progression.Qi, 10);
        Assert.Equal(300d, player.Settings.AutoUseSlots[0].RemainingDurationSeconds);
    }

    [Fact]
    public void SustainedExplorationAndAlchemyOutputsUseCeilingMultiplier()
    {
        var gatheringIncense = new ItemId("item.test-incense");
        var alchemyFlame = new ItemId("item.test-flame");
        var herb = new ItemId("item.test-herb");
        var pill = new ItemId("item.test-pill");
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("test.gather"), "采集", ActionKind.Exploration, 2, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double> { [herb] = 3 }),
            new ActionDefinition(new ActionId("test.alchemy"), "炼制", ActionKind.Alchemy, 2, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double> { [pill] = 1 })
        ]);
        var catalog = new GameDataCatalog(
            "test",
            "test",
            actions,
            new Dictionary<ItemId, ItemDefinition>
            {
                [gatheringIncense] = new(gatheringIncense, "寻灵香", "", Consumable: new ConsumableDefinition(new HashSet<ActionKind> { ActionKind.Exploration }, 300, true, GatheringMultiplier: 1.5)),
                [alchemyFlame] = new(alchemyFlame, "地火引", "", Consumable: new ConsumableDefinition(new HashSet<ActionKind> { ActionKind.Alchemy }, 300, true, AlchemyMultiplier: 1.25))
            });
        var player = CreatePlayer("产出");
        var wallet = CreateWallet(player, (gatheringIncense, 1d), (alchemyFlame, 1d));
        player.ConfigureAutoUse("exploration", 0, gatheringIncense.Value, 1, true);
        player.ConfigureAutoUse("alchemy", 0, alchemyFlame.Value, 1, true);
        player.Enqueue(new ActionIntent(new ActionId("test.gather")), Baseline, actions);
        player.Enqueue(new ActionIntent(new ActionId("test.alchemy")), Baseline, actions);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(4), catalog, wallet);
        var deltas = result.AssetDeltas.ToDictionary(delta => delta.Asset.ItemId, delta => delta.QuantityDelta);

        Assert.Equal(5d, deltas[herb]);
        Assert.Equal(2d, deltas[pill]);
        Assert.Equal(-1d, deltas[gatheringIncense]);
        Assert.Equal(-1d, deltas[alchemyFlame]);
        Assert.Equal(298d, player.Settings.AutoUseSlots.Single(slot => slot.ActionType == "exploration").RemainingDurationSeconds);
        Assert.Equal(298d, player.Settings.AutoUseSlots.Single(slot => slot.ActionType == "alchemy").RemainingDurationSeconds);
    }

    private static AssetWallet CreateWallet(PlayerAggregate player, params (ItemId ItemId, double Quantity)[] balances)
    {
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Restore(balances.Select(balance => new AssetBalance(player.PlayerId, new AssetKey(balance.ItemId), balance.Quantity, 0)));
        return wallet;
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
