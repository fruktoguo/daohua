namespace Domain.Tests;

/// <summary>
/// 讨伐迁地重锚测试套件：讨伐行动跟随玩家驻留地点——
/// 换地后旧遭遇远征就地重锚（搜寻作废重开/复活相位改挂新地点读条）、
/// 不可执行节点按「条件不足」阻断且不触发自动移动、同池节点不重置。
/// </summary>
public sealed class CombatRelocationTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "catalog-a");

    /// <summary>
    /// n1（桃林遭遇池 e1）/ n2（山门遭遇池 e2）/ n4（同池 e1）为可讨伐节点，
    /// n3 无遭遇池且不在白名单的固定测试目录。
    /// </summary>
    private static GameDataCatalog CreateWorld(out ActionCatalog actions,
        double enemyHitpoints = 1, double enemyAttackPower = 0,
        PartyWipeRule onPartyWipe = PartyWipeRule.Respawn)
    {
        var graph = new WorldGraph();
        graph.AddNode(WorldNode.Create("n1", "桃林", 0, 0, ["encounter.e1"]));
        graph.AddNode(WorldNode.Create("n2", "山门", 10, 0, ["encounter.e2"]));
        graph.AddNode(WorldNode.Create("n3", "茶肆", 20, 0));
        graph.AddNode(WorldNode.Create("n4", "邻坡", 30, 0, ["encounter.e1"]));
        actions = new ActionCatalog([
            new ActionDefinition(new ActionId("combat.test"), "讨伐测试", ActionKind.Combat, 0,
                new Dictionary<ItemId, double>(), [],
                AllowedNodeIds: new HashSet<string> { "n1", "n2", "n4" },
                ExecutionKind: ActionExecutionKind.CombatEncounter,
                CombatEncounterId: "encounter.e1")
        ]);
        var monster = new CombatMonsterDefinition("enemy.test", "测试妖兽", 1, 1, CombatMonsterRank.Minion,
            CombatantStats.Create(enemyHitpoints, enemyAttackPower, 0, 2), [], []);
        var first = new CombatEncounterDefinition("encounter.e1", "桃林遭遇", 1, 1, 1, 1, 1, 180,
            [new CombatSpawnDefinition("enemy.test", 1, 1)], [], [], 0, OnPartyWipe: onPartyWipe);
        var second = new CombatEncounterDefinition("encounter.e2", "山门遭遇", 1, 1, 1, 1, 1, 180,
            [new CombatSpawnDefinition("enemy.test", 1, 1)], [], [], 0, OnPartyWipe: onPartyWipe);
        return new GameDataCatalog("relocate", "relocate", actions,
            combatMonsters: new Dictionary<string, CombatMonsterDefinition> { [monster.Id] = monster },
            combatEncounters: new Dictionary<string, CombatEncounterDefinition>
            { [first.Id] = first, [second.Id] = second },
            worldGraph: graph);
    }

    /// <summary>
    /// 核心语义：n1 开搜 e1 远征后迁往 n2，准入重锚——旧桃林遭遇作废，
    /// 就地按山门遭遇池 e2 重开搜寻并落「开始搜寻」事件。
    /// </summary>
    [Fact]
    public void CombatAction_ReanchorsToNewLocationPool_WhenPlayerMoves()
    {
        var gameData = CreateWorld(out var actions);
        var player = CreatePlayer("游侠");
        player.TravelTo("n1");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(0.5), gameData);
        Assert.Equal("encounter.e1", player.Actions[0].Expedition!.EncounterId);

        player.TravelTo("n2");
        var moved = SimulationEngine.AdvanceTo(player, player.LastSimulatedAt.AddSeconds(0.5), gameData);

        var expedition = player.Actions[0].Expedition!;
        Assert.Equal("encounter.e2", expedition.EncounterId);
        Assert.Equal(CombatExpeditionPhase.InitialSearch, expedition.Phase);
        Assert.Equal(0.5, expedition.PhaseElapsedSeconds, 9);
        Assert.Contains(moved.Events, e => e.Code == "combat.search.started" && e.Message.Contains("山门遭遇"));
    }

    /// <summary>
    /// 战斗相位迁地：进行中的旧地点波次作废，抵达后按新地点遭遇池重开搜寻。
    /// </summary>
    [Fact]
    public void CombatAction_DropsInFlightBattle_WhenRelocatingDuringCombat()
    {
        var gameData = CreateWorld(out var actions, enemyHitpoints: 10_000);
        var player = CreatePlayer("缠斗者");
        player.TravelTo("n1");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3), gameData);
        Assert.Equal(CombatExpeditionPhase.Combat, player.Actions[0].Expedition!.Phase);
        Assert.Equal("encounter.e1", player.Actions[0].Expedition!.EncounterId);

        player.TravelTo("n2");
        var moved = SimulationEngine.AdvanceTo(player, player.LastSimulatedAt.AddSeconds(0.5), gameData);

        var expedition = player.Actions[0].Expedition!;
        Assert.Equal("encounter.e2", expedition.EncounterId);
        Assert.Equal(CombatExpeditionPhase.InitialSearch, expedition.Phase);
        Assert.Contains(moved.Events, e => e.Code == "combat.search.started" && e.Message.Contains("山门遭遇"));
    }

    /// <summary>
    /// 远征遭遇仍在当前节点遭遇池内时不重锚：迁往共享 e1 的 n4，
    /// 搜寻读条原样续走、不重复落「开始搜寻」事件。
    /// </summary>
    [Fact]
    public void CombatAction_KeepsExpedition_WhenEncounterStillServedLocally()
    {
        var gameData = CreateWorld(out var actions);
        var player = CreatePlayer("守坡者");
        player.TravelTo("n1");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(0.5), gameData);
        var seed = player.Actions[0].Expedition!.Seed;

        player.TravelTo("n4");
        var moved = SimulationEngine.AdvanceTo(player, player.LastSimulatedAt.AddSeconds(0.5), gameData);

        var expedition = player.Actions[0].Expedition!;
        Assert.Equal("encounter.e1", expedition.EncounterId);
        Assert.Equal(seed, expedition.Seed);
        // 搜寻读条未中断：累计满 1s 后相位已转入战斗，而非重开搜寻。
        Assert.Equal(CombatExpeditionPhase.Combat, expedition.Phase);
        Assert.DoesNotContain(moved.Events, e => e.Code == "combat.search.started");
    }

    /// <summary>
    /// 团灭复活读条中迁地：Respawning 相位保留「仍待起身」语义，
    /// 改挂新地点遭遇的复活读条，读满后在新地点复活续跑。
    /// </summary>
    [Fact]
    public void CombatAction_KeepsRespawnPhase_WhenRelocatingMidRespawn()
    {
        var gameData = CreateWorld(out var actions, enemyHitpoints: 10_000, enemyAttackPower: 10_000,
            onPartyWipe: PartyWipeRule.Defeat);
        var player = CreatePlayer("重伤者");
        player.TravelTo("n1");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3), gameData);
        Assert.Equal(CombatExpeditionPhase.Respawning, player.Actions[0].Expedition!.Phase);
        Assert.Equal("encounter.e1", player.Actions[0].Expedition!.EncounterId);

        player.TravelTo("n2");
        SimulationEngine.AdvanceTo(player, player.LastSimulatedAt.AddSeconds(1), gameData);

        var expedition = player.Actions[0].Expedition!;
        Assert.Equal(CombatExpeditionPhase.Respawning, expedition.Phase);
        Assert.Equal("encounter.e2", expedition.EncounterId);
        Assert.Equal(0, player.Resources.Hitpoints);

        var revived = SimulationEngine.AdvanceTo(player, player.LastSimulatedAt.AddSeconds(180), gameData);
        Assert.Contains(revived.Events, e => e.Code == "combat.respawned");
        Assert.Equal(player.Resources.MaxHitpoints, player.Resources.Hitpoints);
        Assert.Equal("encounter.e2", player.Actions[0].Expedition!.EncounterId);
    }

    /// <summary>
    /// 濒死复苏回归：hp=0 驻留的战斗行动入场受阻后不得永久停泊——
    /// 下一轮推进按脱战复苏 1 HP 归一并重准入续战（阵亡不回血口径下 hp=0 无自愈路径，
    /// 永久停泊会把槽位、Buff 与远征投影全部冻结）。对齐「复活后阵盘不生效+怪物行动条消失」反馈。
    /// </summary>
    [Fact]
    public void CombatAction_BlockedAtZeroHitpoints_RevivesAndResumesOnNextAdvance()
    {
        var gameData = CreateWorld(out var actions, enemyHitpoints: 10_000, enemyAttackPower: 0);
        var player = CreatePlayer("力竭者");
        player.TravelTo("n1");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, actions);
        // 倒地驻留：复苏 1 HP 前的 hp=0 状态（插队顶替/旧链路遗留均可到达）。
        player.Resources.Hitpoints = 0;

        // 推进：搜寻完成后入场判定 hp=0 → 受阻写「无法迎战」——但同轮推进内立即
        // 濒死复苏归一 1 HP 并恢复准入，重新入场开打（不停泊、无需再手动干预）。
        var blocked = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(5), gameData);
        Assert.Contains(blocked.Events, e => e.Code == "combat.downed");
        Assert.NotEqual(ActionQueueStatus.Blocked, player.Actions[0].Status);
        Assert.Equal(CombatExpeditionPhase.Combat, player.Actions[0].Expedition!.Phase);
    }

    /// <summary>
    /// 插队顶替复苏：同类远征行动被高优先级顶替离队且玩家气血归零时，
    /// 与手动移除/清空同口径复苏 1 HP——否则 hp=0 驻留会让新行动在入场处永久受阻。
    /// </summary>
    [Fact]
    public void CombatAction_PriorityEnqueueRevivesHitpoints_WhenReplacingDownedExpedition()
    {
        var gameData = CreateWorld(out var actions, enemyHitpoints: 10_000, enemyAttackPower: 10_000);
        var player = CreatePlayer("插队者");
        player.TravelTo("n1");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3), gameData);
        Assert.Equal(CombatExpeditionPhase.Combat, player.Actions[0].Expedition!.Phase);
        // 阵亡驻留：远征在场 + hp=0（阵亡状态真实形态）。
        player.Resources.Hitpoints = 0;

        player.EnqueuePriority(new ActionIntent(new ActionId("combat.test"), -1), Baseline, actions);

        Assert.Equal(1, player.Resources.Hitpoints);
    }

    /// <summary>
    /// 不可讨伐节点的阻断语义：报「条件不足」而非地点不符；
    /// 即便开启自动移动也不回入队锚点，原地受阻停泊。
    /// </summary>
    [Fact]
    public void CombatAction_BlockedWithConditionShortage_AndSkipsAutoTravel_AtInvalidNode()
    {
        var gameData = CreateWorld(out var actions);
        var player = CreatePlayer("驻足者");
        player.TravelTo("n1");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, actions);
        player.TravelTo("n3");
        player.SetAutoTravelEnabled(true);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(5), gameData);

        Assert.DoesNotContain(result.Events, e => e.Code == "action.auto-travel");
        var blocked = Assert.Single(result.Events, e => e.Code == "action.blocked");
        Assert.Contains("条件不足", blocked.Message);
        Assert.Equal(ActionQueueStatus.Blocked, player.Actions[0].Status);
        Assert.Equal("n3", player.World.NodeId);
    }

    /// <summary>
    /// 钉死远征（CreateEncounter 显式入战口径）：遭遇不在驻留节点池内也继续续跑，
    /// 迁往异池节点不重锚、读条不重置。
    /// </summary>
    [Fact]
    public void CombatAction_PinnedExpedition_ContinuesAcrossPools()
    {
        var gameData = CreateWorld(out var actions);
        var player = CreatePlayer("钉死者");
        player.TravelTo("n1");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(0.5), gameData);

        player.Actions[0].Expedition = player.Actions[0].Expedition! with { IsPinned = true, OriginNodeId = null };
        var seed = player.Actions[0].Expedition!.Seed;

        player.TravelTo("n2");
        var moved = SimulationEngine.AdvanceTo(player, player.LastSimulatedAt.AddSeconds(0.5), gameData);

        var expedition = player.Actions[0].Expedition!;
        Assert.Equal("encounter.e1", expedition.EncounterId);
        Assert.Equal(seed, expedition.Seed);
        Assert.DoesNotContain(moved.Events, e => e.Code == "combat.search.started");
    }

    /// <summary>
    /// 验证真实配表中玄元门遗址（node.xuanyuan-ruins）已纳入星陨异境讨伐白名单，
    /// 且在驻留该节点推进战斗时，正确命中玄元门遗址专属遭遇（encounter.xuanyuan-ruins）及其 BOSS。
    /// </summary>
    [Fact]
    public void CombatAction_XuanyuanRuins_ResolvesCorrectEncounterFromRealCatalog()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var actionDef = catalog.Actions.Get(new ActionId("combat.sweep-twilight-valley"));
        Assert.NotNull(actionDef.AllowedNodeIds);
        Assert.Contains("node.xuanyuan-ruins", actionDef.AllowedNodeIds);

        var player = CreatePlayer("玄元探秘者");
        player.TravelTo("node.xuanyuan-ruins");
        player.Enqueue(new ActionIntent(actionDef.Id, -1), Baseline, catalog.Actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(0.5), catalog);

        Assert.NotEmpty(player.Actions);
        var expedition = player.Actions[0].Expedition;
        Assert.NotNull(expedition);
        Assert.Equal("encounter.xuanyuan-ruins", expedition.EncounterId);

        var encounter = catalog.CombatEncounters[expedition.EncounterId];
        Assert.Contains("enemy.xuanyuan-remnant", encounter.BossEnemyIds);
    }
}
