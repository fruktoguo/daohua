namespace Domain.Tests;

/// <summary>
/// 非战斗行动六维经验收益门禁（总纲 §20.1.4）：rootexp 收益按配表定向定额入账——
/// 多维均分、不吃收益倍率、不受技艺/工具速度折减影响；赶路按实际行进秒数一次性结算，
/// 强化按目标装备等阶换算行动等级与当次基础耗时动态折算。
/// </summary>
public sealed class RootExpYieldTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "catalog-a");

    private static AssetWallet CreateWallet(PlayerAggregate player, params (AssetKey Asset, double Quantity)[] balances)
    {
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Restore(balances.Select(balance => new AssetBalance(player.PlayerId, balance.Asset, balance.Quantity, 0)));
        return wallet;
    }

    private static GameDataCatalog CreateMinimalCatalog(
        ActionCatalog? actions = null,
        Dictionary<string, System.Text.Json.JsonElement>? rawEquipments = null,
        RealmProgressionCatalog? realmProgression = null,
        WorldGraph? worldGraph = null)
        => new("test-1", "test-fingerprint", actions ?? ActionCatalog.Default,
            items: new Dictionary<ItemId, ItemDefinition>(),
            rawEquipments: rawEquipments,
            realmProgression: realmProgression,
            worldGraph: worldGraph);

    /// <summary>按轮定额入账：多维归属各取配表份额，账本同步记账，不串入修为池。</summary>
    [Fact]
    public void RootExpPerRepeatCreditsConfiguredAspects()
    {
        var cultivate = new ActionDefinition(new ActionId("cultivation.test"), "引气测试", ActionKind.Cultivation, 10,
            new Dictionary<ItemId, double>(),
            [
                new ActionYield(ActionYieldKind.Experience, ActionYieldPeriod.PerRepeat, 10),
                new ActionYield(ActionYieldKind.RootExp, ActionYieldPeriod.PerRepeat, 5, Aspect: CoreAspectKind.Meridians),
                new ActionYield(ActionYieldKind.RootExp, ActionYieldPeriod.PerRepeat, 5, Aspect: CoreAspectKind.Bone),
            ]);
        var catalog = CreateMinimalCatalog(actions: new ActionCatalog([cultivate]),
            realmProgression: new RealmProgressionCatalog([100, 200, 300], [80]));
        var player = CreatePlayer("修士");
        player.Enqueue(new ActionIntent(cultivate.Id, -1), Baseline, catalog.Actions);
        var wallet = CreateWallet(player);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet);

        var core = player.Loadout.CoreAttributes;
        Assert.Equal(5, core.MeridiansExp);
        Assert.Equal(5, core.BoneExp);
        Assert.Equal(0, core.AgilityExp);
        Assert.Equal(0, core.ConsciousnessExp);
        // 六维经验独立于修为通道：修为仍只由 experience 收益入账。
        Assert.Equal(10, player.Progression.Experience);
        var rootExp = player.Duel.CurrentReport?.RootExp;
        Assert.NotNull(rootExp);
        Assert.Equal(5, rootExp!.Meridians);
        Assert.Equal(5, rootExp.Bone);
        Assert.Equal(0, rootExp.Stamina);
    }

    /// <summary>定额口径：收益倍率与技艺速度折减均不改变配表声明的每轮六维经验。</summary>
    [Fact]
    public void RootExpFlatAmountIgnoresMultiplierAndTechniqueSpeed()
    {
        var curve = new RealmProgressionCatalog([100, 200, 300], [80]);
        var gather = new ActionDefinition(new ActionId("gather.test"), "采集测试", ActionKind.Exploration, 10,
            new Dictionary<ItemId, double>(),
            [new ActionYield(ActionYieldKind.RootExp, ActionYieldPeriod.PerRepeat, 8, Aspect: CoreAspectKind.Agility)],
            Technique: TechniqueCatalog.Gathering);
        var catalog = CreateMinimalCatalog(actions: new ActionCatalog([gather]), realmProgression: curve);
        var player = CreatePlayer("采集者");
        var wallet = CreateWallet(player);
        var action = player.Enqueue(new ActionIntent(gather.Id, -1), Baseline, catalog.Actions);

        // 技艺 2 级使耗时折减为 10/1.1：轮次更快完成，但每轮六维定额不变。
        player.Loadout.TechniqueExperiences[TechniqueCatalog.Gathering] = 300;
        var effective = SimulationEngine.TimedActionDuration(player, action, gather, catalog, wallet, null);
        Assert.Equal(10 / 1.1, effective, 12);
        // 进度按整秒量化：9.09s 轮长的小数尾段按自然边界多次贴整进位，凑满 10 个整秒才完成首轮。
        for (var i = 0; i < 40 && player.Actions[0].CompletedRepeats < 1; i++)
            SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(effective + i), catalog, wallet);
        Assert.Equal(8, player.Loadout.CoreAttributes.AgilityExp);

        // 收益倍率只放大产出/修为通道：六维定额按配表原值入账。
        SimulationEngine.CompleteTimedAction(player, action, gather, multiplier: 5, catalog, wallet,
            Baseline.AddSeconds(effective + 3), new ActionMutationBatch());
        Assert.Equal(16, player.Loadout.CoreAttributes.AgilityExp);
    }

    /// <summary>按秒六维收益随推进整秒累计，跨轮次边界的整段快进与逐秒推进序列一致。</summary>
    [Fact]
    public void RootExpPerSecondAccruesByElapsedSeconds()
    {
        var channel = new ActionDefinition(new ActionId("channel.test"), "引导测试", ActionKind.Cultivation, 20,
            new Dictionary<ItemId, double>(),
            [new ActionYield(ActionYieldKind.RootExp, ActionYieldPeriod.PerSecond, 2, Aspect: CoreAspectKind.Consciousness)]);
        var catalog = CreateMinimalCatalog(actions: new ActionCatalog([channel]),
            realmProgression: new RealmProgressionCatalog([100, 200, 300], [80]));
        var player = CreatePlayer("引导者");
        player.Enqueue(new ActionIntent(channel.Id, -1), Baseline, catalog.Actions);
        var wallet = CreateWallet(player);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet);
        Assert.Equal(20, player.Loadout.CoreAttributes.ConsciousnessExp);

        // 跨单轮边界再推进 10 秒：按秒累计与按轮定额互不干扰。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(20), catalog, wallet);
        Assert.Equal(40, player.Loadout.CoreAttributes.ConsciousnessExp);
    }

    /// <summary>赶路抵达按实际行进总秒数一次性取整入账（per-second × 全程秒数）。</summary>
    [Fact]
    public void TravelFinishGrantsPerSecondRootExpByTotalDuration()
    {
        var graph = new WorldGraph();
        graph.AddNode(WorldNode.Create("n1", "起点", 0, 0));
        graph.AddNode(WorldNode.Create("n2", "终点", 10, 0));
        graph.AddUndirectedEdge(WorldEdge.Create("e12", "n1", "n2", 10, 10));
        var travel = new ActionDefinition(new ActionId("world.travel"), "赶路", ActionKind.Exploration, 0,
            new Dictionary<ItemId, double>(),
            [new ActionYield(ActionYieldKind.RootExp, ActionYieldPeriod.PerSecond, 1, Aspect: CoreAspectKind.Agility)],
            ExecutionKind: ActionExecutionKind.Travel);
        var catalog = CreateMinimalCatalog(actions: new ActionCatalog([travel]), worldGraph: graph);
        var player = CreatePlayer("行者");
        player.TravelTo("n1");

        var plan = graph.Plan("n1", "n2");
        var travelState = new TravelActionState("n2", plan.TotalTravelSeconds, plan.EdgeIds, plan.Segments);
        player.EnqueuePriority(new ActionIntent(travel.Id, 1, "n2"), Baseline, catalog.Actions, travelState);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, CreateWallet(player));

        Assert.Equal("n2", player.World.NodeId);
        Assert.Equal(10, player.Loadout.CoreAttributes.AgilityExp);
    }

    /// <summary>
    /// 强化单轮六维·神识：按目标装备等阶换算行动等级（T1→Lv1）与当次基础耗时动态折算——
    /// Lv1 普通怪经验 10 × 6s/10 = 6，定额不吃成功率判定结果。
    /// </summary>
    [Fact]
    public void EnhancementRepeatGrantsConsciousnessByTargetTier()
    {
        var sword = new ItemId("equipment.test-sword");
        var enhance = new ActionDefinition(new ActionId("enhance.equipment"), "装备强化",
            ActionKind.Enhancement, 0, new Dictionary<ItemId, double>(), [], Technique: TechniqueCatalog.Enhancement);
        var catalog = CreateMinimalCatalog(actions: new ActionCatalog([enhance]),
            rawEquipments: new Dictionary<string, System.Text.Json.JsonElement>
            {
                [sword.Value] = System.Text.Json.JsonDocument.Parse("""{"id":"equipment.test-sword","name":"测试剑","flatStats":{"physicalAttack":10}}""").RootElement.Clone()
            },
            realmProgression: new RealmProgressionCatalog([10_000, 20_000, 30_000], [80]));
        var player = CreatePlayer("强化者");
        var stone = new ItemId(EnhancementRules.SpiritStoneItemId);
        var wallet = CreateWallet(player, (new AssetKey(sword), 1), (new AssetKey(stone), 500));
        player.Enqueue(new ActionIntent(enhance.Id, -1, TargetItemId: sword.Value), Baseline, catalog.Actions);

        // t=1 基础耗时 6s：一轮结算后神识入账 6，与强化成败分支无关。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(6.2), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(6, player.Loadout.CoreAttributes.ConsciousnessExp);
    }

    /// <summary>真实配表端到端：引气入体一轮 10s → 经脉/根骨各 +5（Lv1 普通怪 10 平分）。</summary>
    [Fact]
    public void EmbeddedDrawQiSplitsTenBetweenMeridiansAndBone()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var drawQi = catalog.Actions.Get(new ActionId("cultivation.draw-qi"));
        var rootYields = drawQi.Yields.Where(yield => yield.Kind == ActionYieldKind.RootExp).ToArray();
        Assert.Equal(2, rootYields.Length);
        Assert.Contains(rootYields, yield => yield.Aspect == CoreAspectKind.Meridians && yield.Amount == 5);
        Assert.Contains(rootYields, yield => yield.Aspect == CoreAspectKind.Bone && yield.Amount == 5);

        var player = CreatePlayer("修士");
        player.Enqueue(new ActionIntent(drawQi.Id, -1), Baseline, catalog.Actions);
        var wallet = CreateWallet(player);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet);

        Assert.Equal(5, player.Loadout.CoreAttributes.MeridiansExp);
        Assert.Equal(5, player.Loadout.CoreAttributes.BoneExp);
    }
}
