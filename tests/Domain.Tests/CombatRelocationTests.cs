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
    /// v15 旧存档缺省形态：OriginNodeId/IsPinned 均缺失（解码后 OriginNodeId=null、
    /// IsPinned=false），仍按池选远征处理——迁往异池节点时正常重锚。
    /// </summary>
    [Fact]
    public void CombatAction_ReanchorsToNewLocationPool_WhenLegacySaveLacksMarkers()
    {
        var gameData = CreateWorld(out var actions);
        var player = CreatePlayer("旧档游侠");
        player.TravelTo("n1");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(0.5), gameData);
        Assert.Equal("encounter.e1", player.Actions[0].Expedition!.EncounterId);

        // 抹掉新字段，还原 v15 载荷解码后的远征形态
        player.Actions[0].Expedition = player.Actions[0].Expedition! with { OriginNodeId = null, IsPinned = false };

        player.TravelTo("n2");
        var moved = SimulationEngine.AdvanceTo(player, player.LastSimulatedAt.AddSeconds(0.5), gameData);

        var expedition = player.Actions[0].Expedition!;
        Assert.Equal("encounter.e2", expedition.EncounterId);
        Assert.Equal(CombatExpeditionPhase.InitialSearch, expedition.Phase);
        Assert.Contains(moved.Events, e => e.Code == "combat.search.started" && e.Message.Contains("山门遭遇"));
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
}
