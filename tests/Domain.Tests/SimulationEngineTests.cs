namespace Domain.Tests;

/// <summary>模拟引擎跨切分位级确定性、行动队列不变量与失效行动自愈回归。</summary>
public sealed class SimulationEngineTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "catalog-a");

    /// <summary>
    /// 确定性门禁：整段推进与逐秒切分推进的修为/经验位级一致，
    /// 含进行中进度秒与亚秒进位余数（在线逐 tick 与离线一次性补算等价）。
    /// </summary>
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

    /// <summary>
    /// 修为按秒即时入账，经验仅在整轮完成时发放（不足一轮不给经验）。
    /// </summary>
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

    /// <summary>
    /// 持续型丹药（聚气丹）在整段推进与逐秒推进下：扣件时机、倍率收益与剩余时长完全一致。
    /// </summary>
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

    /// <summary>
    /// 队列空闲（无行动在跑）时推进墙钟不得消耗持续型消耗品的剩余时长；
    /// 重新入队后从剩余时长继续扣减，不重复扣件。
    /// </summary>
    [Fact]
    public void SustainedConsumableDurationIsPreservedWhileActionQueueIsIdle()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("闲置保留");
        player.SetLoopEnabled(false);
        var pill = new ItemId("item.qi-gathering-pill");
        var wallet = CreateWallet(player, (pill, 1d));
        player.ConfigureAutoUse("cultivation", 0, pill.Value, 1, true);
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi")), Baseline, catalog.Actions);

        var first = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet);
        foreach (var delta in first.AssetDeltas) wallet.Apply(delta);
        Assert.Equal(0d, wallet.Get(new AssetKey(pill)));
        Assert.Equal(290d, player.Settings.AutoUseSlots[0].RemainingDurationSeconds);
        Assert.Empty(player.Actions);

        // 没有行动时推进墙钟不应消耗持续时间。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(100), catalog, wallet);
        Assert.Equal(290d, player.Settings.AutoUseSlots[0].RemainingDurationSeconds);

        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi")), Baseline.AddSeconds(100), catalog.Actions);
        var resumed = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(110), catalog, wallet);
        Assert.Empty(resumed.AssetDeltas);
        Assert.Equal(26d, player.Progression.Qi, 10);
        Assert.Equal(280d, player.Settings.AutoUseSlots[0].RemainingDurationSeconds);
    }

    /// <summary>
    /// SimulationCarrySeconds 是全局亚秒浮点残差而非本轮已产出凭证：
    /// 非零残差下新周期起点仍须正常扣件激活丹药。
    /// </summary>
    [Fact]
    public void SustainedConsumableActivatesWhenSimulationCarryIsNonZero()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("进位残差");
        var pill = new ItemId("item.qi-gathering-pill");
        var wallet = CreateWallet(player, (pill, 1d));
        player.ConfigureAutoUse("cultivation", 0, pill.Value, 1, true);
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);
        // 离线补算会留下非零的亚秒进位余数，它属于全局浮点残差，不得参与“动作开始扣件”的判定。
        player.Progression.SimulationCarrySeconds = 0.6965839999875751d;

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(20), catalog, wallet);
        foreach (var delta in result.AssetDeltas) wallet.Apply(delta);

        Assert.Equal(0.6965839999875751d, player.Progression.SimulationCarrySeconds, 9);
        Assert.Equal(280d, Assert.Single(player.Settings.AutoUseSlots).RemainingDurationSeconds);
        Assert.Equal(0d, wallet.Get(new AssetKey(pill)));
        Assert.Equal(-1d, Assert.Single(result.AssetDeltas).QuantityDelta);
    }

    /// <summary>
    /// 剩余时长透支规则：本轮收益完整吃满倍率后剩余归零并发出 auto-use.depleted，
    /// 下一轮需要新丹药才继续生效（本例钱包为空，不再扣件）。
    /// </summary>
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

    /// <summary>
    /// 本轮已结算过整秒收益后中途补货不得追溯激活（防止吞掉前半段无倍率收益的公平性漏洞），
    /// 须等下一轮周期起点才扣件生效。
    /// </summary>
    [Fact]
    public void SustainedConsumableDoesNotActivateAfterCycleHasAwardedSeconds()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("中途补货");
        var pill = new ItemId("item.qi-gathering-pill");
        var wallet = CreateWallet(player);
        player.ConfigureAutoUse("cultivation", 0, pill.Value, 1, true);
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);

        // 本轮已结算过整秒收益（进度 1 秒），中途补货只能等下一轮周期起点，不得追溯激活。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(1), catalog, wallet);
        wallet.Credit(new AssetKey(pill), 1, "test grant");
        var midCycle = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(5), catalog, wallet);

        Assert.Empty(midCycle.AssetDeltas);
        Assert.Equal(5d, player.Progression.Qi);
        Assert.Equal(0d, player.Settings.AutoUseSlots[0].RemainingDurationSeconds);

        // 下一轮周期起点扣件，且本轮整段收益吃到倍率。
        var nextCycle = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(11), catalog, wallet);
        Assert.Contains(nextCycle.AssetDeltas, delta => delta.Asset.ItemId == pill && delta.QuantityDelta == -1d);
        Assert.Equal(11.3d, player.Progression.Qi, 10);
        Assert.Equal(300d, player.Settings.AutoUseSlots[0].RemainingDurationSeconds);
    }

    /// <summary>
    /// 探索/炼丹产出按持续消耗品倍率向上取整结算（3×1.5→5、1×1.25→2），
    /// 两个槽位各自独立扣件与计时。
    /// </summary>
    [Fact]
    public void SustainedExplorationAndAlchemyOutputsUseCeilingMultiplier()
    {
        var gatheringIncense = new ItemId("item.test-incense");
        var alchemyFlame = new ItemId("item.test-flame");
        var herb = new ItemId("item.test-herb");
        var pill = new ItemId("item.test-pill");
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("test.gather"), "采集", ActionKind.Exploration, 2, new Dictionary<ItemId, double>(), [new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 3, herb)]),
            new ActionDefinition(new ActionId("test.alchemy"), "炼制", ActionKind.Alchemy, 2, new Dictionary<ItemId, double>(), [new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, pill)])
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

    /// <summary>
    /// 0.1s×10 的浮点尘埃（0.9999999999999999）必须贴整为整秒，
    /// 与整段推进位级一致，否则在线/离线结算会出现 1 秒漂移。
    /// </summary>
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

    /// <summary>
    /// -1 无限行动在整段与逐秒两种推进风格下均保持队列项且收益一致。
    /// </summary>
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

    /// <summary>
    /// 亚秒收益进位（SimulationCarrySeconds）随快照持久化：恢复后的半秒余量
    /// 与下一秒推进合并成一个整秒收益，不得丢失。
    /// </summary>
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

    /// <summary>
    /// 配表热更致存量队头行动时长变 0：推进时按自愈规约移出队列（action.invalid-dropped），
    /// 后续合法行动照常结算，不得空转卡死队列。
    /// </summary>
    [Fact]
    public void ZeroDurationHeadActionFromHotReloadIsSelfHealedAndQueueContinues()
    {
        // 入队时目录时长合法，配表热更后变为 0：存量 -1 行动在推进时按自愈规约移出队列。
        var enqueueCatalog = new ActionCatalog([
            new ActionDefinition(new ActionId("test.broken"), "被热更行动", ActionKind.Cultivation, 2, new Dictionary<ItemId, double>(), [new ActionYield(ActionYieldKind.Qi, ActionYieldPeriod.PerSecond, 1)]),
            new ActionDefinition(new ActionId("test.valid"), "正常行动", ActionKind.Cultivation, 2, new Dictionary<ItemId, double>(), [new ActionYield(ActionYieldKind.Qi, ActionYieldPeriod.PerSecond, 1)])
        ]);
        var hotCatalog = new ActionCatalog([
            new ActionDefinition(new ActionId("test.broken"), "被热更行动", ActionKind.Cultivation, 0, new Dictionary<ItemId, double>(), [new ActionYield(ActionYieldKind.Qi, ActionYieldPeriod.PerSecond, 1)]),
            new ActionDefinition(new ActionId("test.valid"), "正常行动", ActionKind.Cultivation, 2, new Dictionary<ItemId, double>(), [new ActionYield(ActionYieldKind.Qi, ActionYieldPeriod.PerSecond, 1)])
        ]);
        var player = CreatePlayer("甲");
        player.SetLoopEnabled(false);
        player.Enqueue(new ActionIntent(new ActionId("test.broken"), -1), Baseline, enqueueCatalog);
        player.Enqueue(new ActionIntent(new ActionId("test.valid"), 1), Baseline, enqueueCatalog);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(6), hotCatalog);

        Assert.Empty(player.Actions);
        var dropped = Assert.Single(result.Events, entry => entry.Code == "action.invalid-dropped");
        Assert.Equal(new ActionId("test.broken"), dropped.ActionId);
        Assert.Equal(1, result.CompletedRepeats);
        Assert.True(player.Progression.Qi == 2.0, $"只有有效行动应累积修为：{player.Progression.Qi:R}");
    }

    /// <summary>
    /// 合法配置 duration=0 + 巨大有限次数（100 万）：入队不受限，
    /// 但推进时同样触发自愈出队而非空转消耗完单次模拟转换上限。
    /// </summary>
    [Fact]
    public void LegalZeroDurationFiniteActionIsSelfHealedInsteadOfSpinning()
    {
        // 合法配置 duration=0 + 有限大 RepeatCount：入队不受限，推进时同样自愈而不是空转到转换上限。
        var catalog = new ActionCatalog([
            new ActionDefinition(new ActionId("test.instant"), "瞬时行动", ActionKind.Alchemy, 0, new Dictionary<ItemId, double>(), []),
            new ActionDefinition(new ActionId("test.valid"), "正常行动", ActionKind.Cultivation, 2, new Dictionary<ItemId, double>(), [new ActionYield(ActionYieldKind.Qi, ActionYieldPeriod.PerSecond, 1)])
        ]);
        var player = CreatePlayer("甲");
        player.SetLoopEnabled(false);
        player.Enqueue(new ActionIntent(new ActionId("test.instant"), 1_000_000), Baseline, catalog);
        player.Enqueue(new ActionIntent(new ActionId("test.valid"), 1), Baseline, catalog);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(4), catalog);

        Assert.Empty(player.Actions);
        Assert.Single(result.Events, entry => entry.Code == "action.invalid-dropped");
        Assert.Equal(1, result.CompletedRepeats);
        Assert.True(player.Progression.Qi == 2.0);
    }

    /// <summary>
    /// 灵力收益必须入账 Resources.Manapoints 并按上限截断，不得写入修为；
    /// 且整段推进与逐秒推进位级一致。
    /// </summary>
    [Fact]
    public void ManaYieldPerSecondCreditsCombatResourceNotProgression()
    {
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("test.mana-spring"), "引灵", ActionKind.Cultivation, 60,
                new Dictionary<ItemId, double>(),
                [new ActionYield(ActionYieldKind.Mana, ActionYieldPeriod.PerSecond, 2)])
        ]);
        var single = CreatePlayer("整段");
        var split = CreatePlayer("逐秒");
        single.Resources.Manapoints = 50;
        split.Resources.Manapoints = 50;
        single.Enqueue(new ActionIntent(new ActionId("test.mana-spring"), -1), Baseline, actions);
        split.Enqueue(new ActionIntent(new ActionId("test.mana-spring"), -1), Baseline, actions);

        SimulationEngine.AdvanceTo(single, Baseline.AddSeconds(40), actions);
        for (var i = 1; i <= 40; i++) SimulationEngine.AdvanceTo(split, Baseline.AddSeconds(i), actions);

        Assert.Equal(100, single.Resources.Manapoints); // 50 + 2*40 = 130 → 上限截断为 100
        Assert.Equal(0, single.Progression.Qi);
        Assert.Equal(0, single.Progression.LifetimeQi);
        Assert.True(single.Resources.Manapoints == split.Resources.Manapoints,
            $"灵力位级不一致：{single.Resources.Manapoints:R} != {split.Resources.Manapoints:R}");
    }

    /// <summary>
    /// 同一行动的多种收益按声明通道独立入账（修为/灵力/物品/Buff/经验互不串位）。
    /// </summary>
    [Fact]
    public void MixedYieldsSettleIndependentlyOnRepeatCompletion()
    {
        var herb = new ItemId("item.test-herb");
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("test.mixed"), "复合收益", ActionKind.Exploration, 2,
                new Dictionary<ItemId, double>(),
                [
                    new ActionYield(ActionYieldKind.Qi, ActionYieldPeriod.PerRepeat, 5),
                    new ActionYield(ActionYieldKind.Mana, ActionYieldPeriod.PerRepeat, 10),
                    new ActionYield(ActionYieldKind.Hitpoints, ActionYieldPeriod.PerRepeat, 7),
                    new ActionYield(ActionYieldKind.Experience, ActionYieldPeriod.PerRepeat, 3),
                    new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, herb),
                    new ActionYield(ActionYieldKind.Buff, ActionYieldPeriod.PerRepeat, 2, null,
                        new ActionBuffGrant("buff.focus", "凝神", 60, HpRegen: 2))
                ])
        ]);
        var player = CreatePlayer("甲");
        player.Resources.Manapoints = 50;
        player.Resources.Hitpoints = 50;
        player.Enqueue(new ActionIntent(new ActionId("test.mixed"), 1), Baseline, actions);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2), actions);

        Assert.Equal(5, player.Progression.Qi);
        Assert.Equal(5, player.Progression.LifetimeQi);
        Assert.Equal(3, player.Progression.Experience);
        Assert.Equal(60, player.Resources.Manapoints);
        Assert.Equal(57, player.Resources.Hitpoints);
        var buff = Assert.Single(player.Buffs.Active);
        Assert.Equal("buff.focus", buff.BuffId);
        Assert.Equal(2, buff.Stacks);
        Assert.Equal(2, buff.HpRegen);
        Assert.Equal(60, buff.ExpiresAtUnixSeconds - Baseline.AddSeconds(2).ToUnixTimeMilliseconds() / 1000d, 6);
        var delta = Assert.Single(result.AssetDeltas);
        Assert.Equal(herb, delta.Asset.ItemId);
        Assert.Equal(1, delta.QuantityDelta);
    }

    /// <summary>
    /// 非法收益配置在目录装载期拒绝而非静默忽略。
    /// </summary>
    [Fact]
    public void InvalidYieldDeclarationsAreRejected()
    {
        var herb = new ItemId("item.test-herb");
        // 物品按秒结算 → 拒绝
        Assert.Throws<DomainException>(() => new ActionCatalog([
            new ActionDefinition(new ActionId("test.bad1"), "x", ActionKind.Exploration, 1,
                new Dictionary<ItemId, double>(),
                [new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerSecond, 1, herb)])
        ]));
        // 负值收益 → 拒绝
        Assert.Throws<DomainException>(() => new ActionCatalog([
            new ActionDefinition(new ActionId("test.bad2"), "x", ActionKind.Exploration, 1,
                new Dictionary<ItemId, double>(),
                [new ActionYield(ActionYieldKind.Qi, ActionYieldPeriod.PerSecond, -1)])
        ]));
        // 资源收益携带实体标识 → 拒绝
        Assert.Throws<DomainException>(() => new ActionCatalog([
            new ActionDefinition(new ActionId("test.bad3"), "x", ActionKind.Exploration, 1,
                new Dictionary<ItemId, double>(),
                [new ActionYield(ActionYieldKind.Qi, ActionYieldPeriod.PerSecond, 1, herb)])
        ]));
        // 同通道同频率重复声明 → 拒绝
        Assert.Throws<DomainException>(() => new ActionCatalog([
            new ActionDefinition(new ActionId("test.bad4"), "x", ActionKind.Exploration, 1,
                new Dictionary<ItemId, double>(),
                [
                    new ActionYield(ActionYieldKind.Qi, ActionYieldPeriod.PerSecond, 1),
                    new ActionYield(ActionYieldKind.Qi, ActionYieldPeriod.PerSecond, 2)
                ])
        ]));
    }

    /// <summary>
    /// 配表 JSON 的 yields 声明经 GameDataCatalog.Parse 正确解析为强类型收益模型；
    /// 未知收益类型与非法频率在解析期直接拒绝。
    /// </summary>
    [Fact]
    public void YieldsJsonRoundTripsThroughConfigParser()
    {
        var actions = """
            [{"id":"test.parsed","name":"解析","type":"cultivation","durationSeconds":4,
              "yields":[
                {"kind":"mana","per":"second","amount":0.5},
                {"kind":"item","per":"repeat","id":"item.test-herb","amount":2},
                {"kind":"buff","per":"repeat","id":"buff.calm","name":"入定","amount":1,"durationSeconds":30,
                 "modifiers":{"hpRegen":1.5,"attackSpeedPoints":10}}]}]
            """u8.ToArray();
        var catalog = GameDataCatalog.Parse([actions]);

        var definition = catalog.Actions.Get(new ActionId("test.parsed"));
        Assert.Equal(3, definition.Yields.Count);
        Assert.Equal(new ActionYield(ActionYieldKind.Mana, ActionYieldPeriod.PerSecond, 0.5), definition.Yields[0]);
        Assert.Equal(new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 2, new ItemId("item.test-herb")), definition.Yields[1]);
        var grant = definition.Yields[2].Buff;
        Assert.NotNull(grant);
        Assert.Equal("buff.calm", grant.BuffId);
        Assert.Equal("入定", grant.Name);
        Assert.Equal(30, grant.DurationSeconds);
        Assert.Equal(1.5, grant.HpRegen);
        Assert.Equal(10, grant.AttackSpeedPoints);

        // 未知收益类型与非法频率必须在解析期直接拒绝。
        Assert.Throws<DomainException>(() => GameDataCatalog.Parse(
            ["[{\"id\":\"test.x\",\"durationSeconds\":1,\"yields\":[{\"kind\":\"spirit\",\"per\":\"repeat\",\"amount\":1}]}]"u8.ToArray()]));
        Assert.Throws<DomainException>(() => GameDataCatalog.Parse(
            ["[{\"id\":\"test.x\",\"durationSeconds\":1,\"yields\":[{\"kind\":\"qi\",\"per\":\"hour\",\"amount\":1}]}]"u8.ToArray()]));
    }
}
