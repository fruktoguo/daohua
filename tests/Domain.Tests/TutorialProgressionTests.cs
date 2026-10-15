using Domain.Persistence;
using DaoHua.Ecs.Systems;

namespace Domain.Tests;

/// <summary>
/// 新手引导里程碑推进测试（总纲 §22）：行动轮结算、旅行到达与讨伐波次清空三边界的
/// 服务端权威推进、赠送入账、跳过期权与幂等语义回归。
/// </summary>
public sealed class TutorialProgressionTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly AssetKey NingxueGrass = new(new ItemId("item.ningxue-grass"));
    private static readonly AssetKey RefinedIron = new(new ItemId("item.refined-iron"));
    private static readonly AssetKey BeastBone = new(new ItemId("item.beast-bone"));
    private static readonly AssetKey Gongde = new(new ItemId("item.gongde"));
    private static readonly AssetKey SpiritStone = new(new ItemId("currency.spirit-stone"));

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "fp");

    private static AssetWallet CreateWallet(PlayerAggregate player, params (ItemId ItemId, double Quantity)[] balances)
    {
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Restore(balances.Select(balance => new AssetBalance(player.PlayerId, new AssetKey(balance.ItemId), balance.Quantity, 0)));
        return wallet;
    }


    /// <summary>推进模拟并把资产差量应用到测试钱包（与运行时 CommitSimulation 同构）。</summary>
    private static SimulationResult Advance(PlayerAggregate player, DateTimeOffset target,
        GameDataCatalog catalog, AssetWallet wallet)
    {
        var result = SimulationEngine.AdvanceTo(player, target, catalog, wallet);
        foreach (var delta in result.AssetDeltas) wallet.Apply(delta);
        return result;
    }

    /// <summary>发起一次真实旅行：路径规划 + 优先级插队 world.travel，与服务端命令同构。</summary>
    private static void EnqueueTravel(PlayerAggregate player, GameDataCatalog catalog, string targetNodeId, DateTimeOffset at)
    {
        var plan = catalog.WorldGraph.PlanFromPosition(
            player.World.EdgeId, player.World.EdgeProgress, player.World.NodeId, targetNodeId);
        player.EnqueuePriority(
            new ActionIntent(new ActionId("world.travel"), 1, TargetNodeId: targetNodeId),
            at, catalog.Actions,
            new TravelActionState(targetNodeId, plan.TotalTravelSeconds, plan.EdgeIds, plan.Segments));
    }

    /// <summary>采集完成一轮推进到炼丹步，赠送凝气草与采集产出同通道入账。</summary>
    [Fact]
    public void GatherRoundGrantsAlchemyMaterialsAndAdvances()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("新人");
        player.SetLoopEnabled(false);
        var wallet = CreateWallet(player);
        player.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), 1), Baseline, catalog.Actions);

        var result = Advance(player, Baseline.AddSeconds(20), catalog, wallet);

        Assert.Equal(TutorialSteps.Alchemy, player.Settings.TutorialStep);
        // 采集产出 1 + 引导赠送 3（引气丹单轮输入）。
        Assert.Equal(4, wallet.Get(NingxueGrass));
        Assert.Contains(result.Events, e => e.Code == "tutorial.grant");
    }

    /// <summary>炼丹/炼器链：赠送材料在行动完成的同一结算窗口入账，可立即供下一步消耗。</summary>
    [Fact]
    public void AlchemyForgeChainGrantsInputsForNextStep()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("炼丹");
        player.SetLoopEnabled(false);
        player.Settings.TutorialStep = TutorialSteps.Alchemy;
        var wallet = CreateWallet(player, (new ItemId("item.ningxue-grass"), 3d));
        player.Enqueue(new ActionIntent(new ActionId("alchemy.refine-qi-pill"), 1), Baseline, catalog.Actions);

        var result = Advance(player, Baseline.AddSeconds(20), catalog, wallet);

        Assert.Equal(TutorialSteps.Forge, player.Settings.TutorialStep);
        Assert.Equal(7, wallet.Get(RefinedIron));
        Assert.Equal(1, wallet.Get(BeastBone));
        Assert.Contains(result.Events, e => e.Code == "tutorial.grant");

        player.Enqueue(new ActionIntent(new ActionId("forge.qingfeng-sword"), 1),
            player.LastSimulatedAt, catalog.Actions);
        Advance(player, player.LastSimulatedAt.AddSeconds(30), catalog, wallet);

        Assert.Equal(TutorialSteps.TravelToPeak, player.Settings.TutorialStep);
        // 炼器输入已消耗：赠送额度不重复入账。
        Assert.Equal(0, wallet.Get(RefinedIron));
        Assert.Equal(0, wallet.Get(BeastBone));
    }

    /// <summary>驻留型里程碑：旅行到达云巅/回村两个目标节点分别推进到修炼步与讨伐步。</summary>
    [Fact]
    public void TravelArrivalAdvancesResidenceMilestones()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("旅人");
        player.SetLoopEnabled(false);
        player.Settings.TutorialStep = TutorialSteps.TravelToPeak;
        var wallet = CreateWallet(player);

        EnqueueTravel(player, catalog, "node.cloud-peak", Baseline);
        Advance(player, Baseline.AddSeconds(3600), catalog, wallet);

        Assert.Equal("node.cloud-peak", player.World.NodeId);
        Assert.Equal(TutorialSteps.Cultivate, player.Settings.TutorialStep);

        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 1),
            player.LastSimulatedAt, catalog.Actions);
        Advance(player, player.LastSimulatedAt.AddSeconds(30), catalog, wallet);
        Assert.Equal(TutorialSteps.TravelToVillage, player.Settings.TutorialStep);

        EnqueueTravel(player, catalog, "node.yunxi-village", player.LastSimulatedAt);
        Advance(player, player.LastSimulatedAt.AddSeconds(3600), catalog, wallet);

        Assert.Equal("node.yunxi-village", player.World.NodeId);
        Assert.Equal(TutorialSteps.Combat, player.Settings.TutorialStep);
    }

    /// <summary>交错路径：完成炼器步时玩家已驻留云巅草庐，驻留步在同一结算窗口连续收敛。</summary>
    [Fact]
    public void ResidenceStepCatchesUpWhenPlayerAlreadyAtTarget()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("赶路");
        player.SetLoopEnabled(false);
        player.TravelTo("node.cloud-peak");
        player.Settings.TutorialStep = TutorialSteps.Forge;
        var wallet = CreateWallet(player,
            (new ItemId("item.refined-iron"), 7d), (new ItemId("item.beast-bone"), 1d));
        player.Enqueue(new ActionIntent(new ActionId("forge.qingfeng-sword"), 1), Baseline, catalog.Actions);

        Advance(player, Baseline.AddSeconds(30), catalog, wallet);

        Assert.Equal(TutorialSteps.Cultivate, player.Settings.TutorialStep);
    }

    /// <summary>讨伐完成一轮收敛到终态并发放功德 ×1000 与灵石 ×10000。</summary>
    [Fact]
    public void CombatRoundCompletionAwardsFinalReward()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("试炼");
        player.SetLoopEnabled(false);
        player.Settings.TutorialStep = TutorialSteps.Combat;
        var wallet = CreateWallet(player);
        player.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), 1), Baseline, catalog.Actions);

        var result = Advance(player, Baseline.AddSeconds(3600), catalog, wallet);

        Assert.Equal(TutorialSteps.Done, player.Settings.TutorialStep);
        Assert.Equal(1000, wallet.Get(Gongde));
        Assert.Equal(10000, wallet.Get(SpiritStone));
        Assert.Contains(result.Events, e => e.Code == "tutorial.completed");
    }

    /// <summary>跳过引导后收敛终态：后续行动不再触发赠送与终奖；重复跳过幂等。</summary>
    [Fact]
    public void SkipTutorialBlocksGrantsAndIsIdempotent()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("跳过");
        player.SetLoopEnabled(false);
        var wallet = CreateWallet(player);
        player.SkipTutorial();
        player.SkipTutorial();
        Assert.Equal(TutorialSteps.Done, player.Settings.TutorialStep);

        player.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), 1), Baseline, catalog.Actions);
        var result = Advance(player, Baseline.AddSeconds(20), catalog, wallet);

        Assert.Equal(TutorialSteps.Done, player.Settings.TutorialStep);
        // 采集产出照常入账，引导赠送不再发放。
        Assert.Equal(1, wallet.Get(NingxueGrass));
        Assert.DoesNotContain(result.Events, e => e.Code == "tutorial.grant");
    }

    /// <summary>非当前步骤对应的行动完成不改变引导状态（里程碑幂等语义）。</summary>
    [Fact]
    public void UnrelatedActionDoesNotAdvanceTutorial()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("修行");
        player.SetLoopEnabled(false);
        var wallet = CreateWallet(player);
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 1), Baseline, catalog.Actions);

        Advance(player, Baseline.AddSeconds(20), catalog, wallet);

        Assert.Equal(TutorialSteps.Gather, player.Settings.TutorialStep);
    }

    /// <summary>
    /// 全流程回归：从采集到讨伐顺序走完七环，每环阶段奖励到账、终奖仅发放一次，
    /// 且引导进度经 settings 分段编码解码往返后保持终态（断线重连/换端恢复语义）。
    /// </summary>
    [Fact]
    public void FullFlowWalksAllStagesGrantsEachRewardAndSurvivesPersistence()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("全链");
        player.SetLoopEnabled(false);
        var wallet = CreateWallet(player);

        // 环1 采集：产出 1 + 引导赠送 3（引气丹单轮输入）→ Alchemy
        player.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), 1), Baseline, catalog.Actions);
        var r1 = Advance(player, Baseline.AddSeconds(20), catalog, wallet);
        Assert.Equal(TutorialSteps.Alchemy, player.Settings.TutorialStep);
        Assert.Equal(4, wallet.Get(NingxueGrass));
        Assert.Contains(r1.Events, e => e.Code == "tutorial.grant");

        // 环2 炼丹：消耗赠送材料，完成后赠炼器材料 → Forge
        player.Enqueue(new ActionIntent(new ActionId("alchemy.refine-qi-pill"), 1), player.LastSimulatedAt, catalog.Actions);
        var r2 = Advance(player, player.LastSimulatedAt.AddSeconds(20), catalog, wallet);
        Assert.Equal(TutorialSteps.Forge, player.Settings.TutorialStep);
        Assert.Equal(7, wallet.Get(RefinedIron));
        Assert.Equal(1, wallet.Get(BeastBone));
        Assert.Contains(r2.Events, e => e.Code == "tutorial.grant");

        // 环3 炼器：赠送材料恰好耗尽 → TravelToPeak
        player.Enqueue(new ActionIntent(new ActionId("forge.qingfeng-sword"), 1), player.LastSimulatedAt, catalog.Actions);
        Advance(player, player.LastSimulatedAt.AddSeconds(30), catalog, wallet);
        Assert.Equal(TutorialSteps.TravelToPeak, player.Settings.TutorialStep);
        Assert.Equal(0, wallet.Get(RefinedIron));
        Assert.Equal(0, wallet.Get(BeastBone));

        // 环4 驻留云巅 → Cultivate
        EnqueueTravel(player, catalog, "node.cloud-peak", player.LastSimulatedAt);
        Advance(player, player.LastSimulatedAt.AddSeconds(3600), catalog, wallet);
        Assert.Equal("node.cloud-peak", player.World.NodeId);
        Assert.Equal(TutorialSteps.Cultivate, player.Settings.TutorialStep);

        // 环5 引气入体一轮 → TravelToVillage
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 1), player.LastSimulatedAt, catalog.Actions);
        Advance(player, player.LastSimulatedAt.AddSeconds(30), catalog, wallet);
        Assert.Equal(TutorialSteps.TravelToVillage, player.Settings.TutorialStep);

        // 环6 驻留回村 → Combat
        EnqueueTravel(player, catalog, "node.yunxi-village", player.LastSimulatedAt);
        Advance(player, player.LastSimulatedAt.AddSeconds(3600), catalog, wallet);
        Assert.Equal("node.yunxi-village", player.World.NodeId);
        Assert.Equal(TutorialSteps.Combat, player.Settings.TutorialStep);

        // 环7 讨伐一轮 → Done，终奖入账且事件仅一条
        player.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), 1), player.LastSimulatedAt, catalog.Actions);
        var r7 = Advance(player, player.LastSimulatedAt.AddSeconds(3600), catalog, wallet);
        Assert.Equal(TutorialSteps.Done, player.Settings.TutorialStep);
        Assert.Equal(1000, wallet.Get(Gongde));
        Assert.Equal(10000, wallet.Get(SpiritStone));
        Assert.Single(r7.Events, e => e.Code == "tutorial.completed");

        // 终态后再跑行动：赠送与终奖均不重复发放（推进幂等）
        player.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), 1), player.LastSimulatedAt, catalog.Actions);
        var r8 = Advance(player, player.LastSimulatedAt.AddSeconds(20), catalog, wallet);
        Assert.Equal(1000, wallet.Get(Gongde));
        Assert.Equal(10000, wallet.Get(SpiritStone));
        Assert.DoesNotContain(r8.Events, e => e.Code == "tutorial.grant" || e.Code == "tutorial.completed");

        // 持久化往返：全量编码 → 组装 bundle → 解码回 DomainState，tutorialStep 保持终态
        var state = player.ExportState();
        var writes = SectionCodec.EncodeAggregateFull(state, player.LastSimulatedAt);
        var stored = writes.ToDictionary(
            write => write.SectionKind,
            write => new StoredSection(
                write.PlayerId, write.SectionKind, write.StateVersion, write.SchemaVersion,
                write.Codec, write.Payload.ToArray(), write.RawBytes, write.Checksum, write.UpdatedAt));
        var head = new PlayerStateHead(
            state.PlayerId.Value, Math.Max(1, state.StateVersion), SectionCodec.CurrentSchemaVersion,
            player.LastSimulatedAt, state.GameDataFingerprint,
            SectionCodec.ComputeHeadChecksum(stored.Values), player.LastSimulatedAt);
        var bundle = new PlayerStateBundle(
            new PlayerIdentity(player.PlayerId.Value, player.AccountId.Value, player.DisplayName, player.CreatedAt),
            head, stored, []);
        var restored = SectionCodec.DecodeAggregate(bundle);
        Assert.Equal(TutorialSteps.Done, restored.Settings.TutorialStep);
    }

    /// <summary>
    /// 跳过补发清单：游标+1..Done 的未领赠送按物品合并成一张表；
    /// SkipTutorial 把步骤与奖励游标一起推进到 Done，二次跳过清单为空（幂等）。
    /// </summary>
    [Fact]
    public void PendingSkipGrantsMergesRemainingRewardsAndSkipAdvancesCursor()
    {
        var player = CreatePlayer("补发");
        // 已激活炼丹步：其赠送已入账（游标=Alchemy），剩余应为 炼器赠送 + 终奖。
        player.Settings.TutorialStep = TutorialSteps.Alchemy;
        player.Settings.TutorialRewardsStep = TutorialSteps.Alchemy;

        var grants = TutorialRules.PendingSkipGrants(player.Settings);

        Assert.Equal(4, grants.Count);
        Assert.Equal(7, grants[new ItemId("item.refined-iron")]);
        Assert.Equal(1, grants[new ItemId("item.beast-bone")]);
        Assert.Equal(1000, grants[new ItemId("item.gongde")]);
        Assert.Equal(10000, grants[new ItemId("currency.spirit-stone")]);

        player.SkipTutorial();
        Assert.Equal(TutorialSteps.Done, player.Settings.TutorialStep);
        Assert.Equal(TutorialSteps.Done, player.Settings.TutorialRewardsStep);
        Assert.Empty(TutorialRules.PendingSkipGrants(player.Settings));
    }

    /// <summary>
    /// 重新学习：TutorialStep 回到起点、奖励游标保持不动——
    /// 重走已领过赠送的里程碑不再重复入账（游标拦截）。
    /// </summary>
    [Fact]
    public void RestartResetsStepButKeepsRewardsCursor()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("重学");
        player.SetLoopEnabled(false);
        var wallet = CreateWallet(player);

        // 先完成采集环：赠送入账，游标推进到 Alchemy。
        player.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), 1), Baseline, catalog.Actions);
        Advance(player, Baseline.AddSeconds(20), catalog, wallet);
        Assert.Equal(TutorialSteps.Alchemy, player.Settings.TutorialStep);
        Assert.Equal(TutorialSteps.Alchemy, player.Settings.TutorialRewardsStep);
        Assert.Equal(4, wallet.Get(NingxueGrass));

        // 重学：步骤归零、游标不动。
        player.RestartTutorial();
        Assert.Equal(TutorialSteps.Gather, player.Settings.TutorialStep);
        Assert.Equal(TutorialSteps.Alchemy, player.Settings.TutorialRewardsStep);

        // 重走采集环：推进照常，但赠送不再发（只入账行动产出 1）。
        player.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), 1), player.LastSimulatedAt, catalog.Actions);
        var result = Advance(player, player.LastSimulatedAt.AddSeconds(20), catalog, wallet);
        Assert.Equal(TutorialSteps.Alchemy, player.Settings.TutorialStep);
        Assert.Equal(5, wallet.Get(NingxueGrass));
        Assert.DoesNotContain(result.Events, e => e.Code == "tutorial.grant");
    }

    /// <summary>
    /// 跳过后重学：奖励游标已至 Done，重走全流程不再触发任何赠送与终奖事件。
    /// </summary>
    [Fact]
    public void RestartAfterSkipDoesNotRegrantAnyReward()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("跳过重学");
        player.SetLoopEnabled(false);
        var wallet = CreateWallet(player);

        player.SkipTutorial();
        player.RestartTutorial();
        Assert.Equal(TutorialSteps.Gather, player.Settings.TutorialStep);
        Assert.Equal(TutorialSteps.Done, player.Settings.TutorialRewardsStep);

        player.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), 1), Baseline, catalog.Actions);
        var r1 = Advance(player, Baseline.AddSeconds(20), catalog, wallet);
        Assert.DoesNotContain(r1.Events, e => e.Code == "tutorial.grant" || e.Code == "tutorial.completed");

        player.Settings.TutorialStep = TutorialSteps.Combat;
        player.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), 1), player.LastSimulatedAt, catalog.Actions);
        var r2 = Advance(player, player.LastSimulatedAt.AddSeconds(3600), catalog, wallet);
        Assert.Equal(TutorialSteps.Done, player.Settings.TutorialStep);
        Assert.Equal(0, wallet.Get(Gongde));
        Assert.Equal(0, wallet.Get(SpiritStone));
        Assert.DoesNotContain(r2.Events, e => e.Code == "tutorial.grant" || e.Code == "tutorial.completed");
    }
}
