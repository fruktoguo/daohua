namespace Domain.Tests;

/// <summary>
/// 战斗远征模拟测试：无限/有限战斗、波次推进、失败恢复与状态往返。
/// </summary>
public sealed class CombatExpeditionSimulationTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// -1 无限战斗行动完成一波讨伐后仍保持 Running 不出队（无限循环不变量）。
    /// </summary>
    [Fact]
    public void InfiniteCombatActionCanBeEnqueuedAndKeepsRunningState()
    {
        var catalog = CreateCatalog(enemyHitpoints: 1, enemyAttackPower: 0);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");

        var action = player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        // 搜寻 1s + 首次普攻引导（攻击间隔 2s），3.5s 内恰好完成一波。
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3.5), catalog);

        Assert.True(result.Changed);
        Assert.Single(player.Actions);
        Assert.Equal(action.Id, player.Actions[0].Id);
        Assert.Equal(-1, player.Actions[0].RemainingRepeats);
        Assert.Equal(1, player.Actions[0].CompletedRepeats);
        Assert.NotNull(player.Actions[0].Expedition);
    }

    /// <summary>
    /// 有限次数战斗行动清场胜利后按次数出队，完成计数正常入账。
    /// </summary>
    [Fact]
    public void FiniteCombatActionIsRemovedAfterWinningEncounter()
    {
        var catalog = CreateCatalog(enemyHitpoints: 1, enemyAttackPower: 0);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        player.SetLoopEnabled(false);

        player.Enqueue(new ActionIntent(new ActionId("combat.test"), 1), Baseline, catalog.Actions);
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3.5), catalog);

        Assert.Equal(1, result.CompletedRepeats);
        Assert.Empty(player.Actions);
        Assert.Equal(1, player.Progression.CompletedActions);
    }

    /// <summary>
    /// 战败进入重生相位后：边界前一刻保持 0 血等待，到达配置的重生边界才满血复活回到搜寻相位；
    /// 快照恢复的副本与在线副本在同一时刻产生一致结果（重生时序确定性）。
    /// </summary>
    [Fact]
    public void DefeatedCombatActionRestoresAndResumesAtConfiguredRespawnBoundary()
    {
        var catalog = CreateCatalog(enemyHitpoints: 1_000, enemyAttackPower: 1_000);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");

        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3), catalog);

        var action = Assert.Single(player.Actions);
        Assert.Equal(ActionQueueStatus.Running, action.Status);
        Assert.Equal(CombatExpeditionPhase.Respawning, action.Expedition!.Phase);
        Assert.Equal(0, player.Combat.Hitpoints);
        Assert.Contains(result.Events, entry => entry.Code == "combat.lost");
        var remaining = action.Expedition.PhaseDurationSeconds - action.Expedition.PhaseElapsedSeconds;
        Assert.Equal(180, action.Expedition.PhaseDurationSeconds);
        var restored = PlayerAggregate.Restore(player.ExportState());
        var boundary = player.LastSimulatedAt.AddSeconds(remaining);
        SimulationEngine.AdvanceTo(player, boundary.AddMilliseconds(-1), catalog);
        Assert.Equal(0, player.Combat.Hitpoints);
        Assert.Equal(CombatExpeditionPhase.Respawning, action.Expedition.Phase);
        var respawned = SimulationEngine.AdvanceTo(player, boundary, catalog);
        SimulationEngine.AdvanceTo(restored, boundary, catalog);
        Assert.Contains(respawned.Events, entry => entry.Code == "combat.respawned");
        Assert.Equal(player.Combat.MaxHitpoints, player.Combat.Hitpoints);
        Assert.Equal(player.Combat.MaxManapoints, player.Combat.Manapoints);
        Assert.Equal(CombatExpeditionPhase.Search, action.Expedition.Phase);
        Assert.Equal(-1, action.RemainingRepeats);
        Assert.Equal(0, action.CompletedRepeats);
        Assert.Equal(action.Expedition, restored.Actions[0].Expedition);
    }

    /// <summary>
    /// 同波全体敌人同场交战，清空整波才结算一次完成并推进到下一波。
    /// </summary>
    [Fact]
    public void CombatWaveFightsAllMonstersSimultaneouslyAndCountsOneRepeatPerWave()
    {
        var catalog = CreateWaveCatalog();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(1), catalog);
        var events = new List<(string Code, string Message)>(result.Events.Select(entry => (entry.Code, entry.Message)));
        var action = Assert.Single(player.Actions);
        Assert.Equal(CombatExpeditionPhase.Combat, action.Expedition!.Phase);
        Assert.Equal(1, action.Expedition.WaveNumber);
        // 固定 3 只普通怪的波次结构。
        Assert.Equal(3, action.Expedition.Enemies!.Count);

        var guard = 0;
        while (events.Count(entry => entry.Code == "combat.won") < 1 && guard++ < 120)
        {
            result = SimulationEngine.AdvanceTo(player, player.LastSimulatedAt.AddSeconds(0.5), catalog);
            events.AddRange(result.Events.Select(entry => (entry.Code, entry.Message)));
        }

        Assert.Equal(1, events.Count(entry => entry.Code == "combat.won"));
        Assert.Equal(3, player.Progression.NormalKills);
        Assert.Equal(1, player.Actions[0].CompletedRepeats);
        Assert.Equal(2, player.Actions[0].Expedition!.WaveNumber);
    }

    /// <summary>
    /// 远征状态（含敌人列表）经导出/恢复往返后继续推进，与原实例结果逐字段一致。
    /// </summary>
    [Fact]
    public void CombatExpeditionStateRoundTripsAndContinuesDeterministically()
    {
        var catalog = CreateCatalog(enemyHitpoints: 100, enemyAttackPower: 0);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(1), catalog);

        var restored = PlayerAggregate.Restore(player.ExportState());
        var first = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2), catalog);
        var second = SimulationEngine.AdvanceTo(restored, Baseline.AddSeconds(2), catalog);

        Assert.Equal(first.CompletedRepeats, second.CompletedRepeats);
        Assert.Equal(player.Combat.Hitpoints, restored.Combat.Hitpoints);
        Assert.Equal(player.Actions[0].Expedition! with { Enemies = null },
            restored.Actions[0].Expedition! with { Enemies = null });
        Assert.Equal(player.Actions[0].Expedition!.Enemies, restored.Actions[0].Expedition!.Enemies);
    }

    /// <summary>
    /// 验证搜寻相位下气血回复同步远征镜像血量。
    /// 搜寻相位（玩家视角的脱战状态）下权威气血按周期回复时，远征镜像血量必须同步，
    /// 否则界面显示冻结在上次战斗结束时的旧值。
    /// </summary>
    [Fact]
    public void RegenSyncsExpeditionHitpointsDuringSearchPhase()
    {
        var attributes = new BaseAttributeCatalog([new(1, 1000, 100, 10, 0, 0, 0, 100, 0, 0, 0, 0, 0, 7, 3)]);
        var catalog = CreateCatalog(enemyHitpoints: 1, enemyAttackPower: 0, searchSeconds: 30, baseAttributes: attributes);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        player.Combat.Hitpoints = 10;
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(12), catalog);

        var action = Assert.Single(player.Actions);
        Assert.Equal(CombatExpeditionPhase.InitialSearch, action.Expedition!.Phase);
        Assert.True(player.Combat.Hitpoints > 10);
        Assert.Equal(player.Combat.Hitpoints, action.Expedition.PlayerHitpoints);
    }

    /// <summary>
    /// 恢复遭遇状态时血量与结局矛盾（敌人已清零却仍为 Running）必须拒绝，防止损坏快照静默进入模拟。
    /// </summary>
    [Fact]
    public void CombatRestoreRejectsOutcomeThatDoesNotMatchHitpoints()
    {
        var stats = CombatantStats.Create(10, 1, 0, 1);

        Assert.Throws<DomainException>(() => CombatEncounterState.Restore(
            "encounter.test", 1, new CombatantTemplate(stats),
            new CombatantSnapshot(10, 0, 0, 0, 0),
            [new CombatantTemplate(stats)],
            [new CombatantSnapshot(0, 0, 0, 0, 0)],
            0, CombatOutcome.Running));
    }

    private static GameDataCatalog CreateWaveCatalog()
    {
        var actionId = new ActionId("combat.test");
        var actions = new ActionCatalog([
            new ActionDefinition(
                actionId,
                "讨伐测试",
                ActionKind.Combat,
                0,
                new Dictionary<ItemId, double>(),
                [],
                ExecutionKind: ActionExecutionKind.CombatEncounter,
                CombatEncounterId: "encounter.wave")
        ]);
        var monster = new CombatMonsterDefinition(
            "enemy.wave",
            "波次妖兽",
            10,
            1,
            CombatMonsterRank.Minion,
            CombatantStats.Create(1, 0, 0, 2),
            [],
            []);
        var encounter = new CombatEncounterDefinition(
            "encounter.wave",
            "波次遭遇",
            30,
            30,
            30,
            1,
            1,
            180,
            [new CombatSpawnDefinition("enemy.wave", 1, 1)],
            [],
            [],
            0);
        return new GameDataCatalog(
            "wave-1",
            "wave-fingerprint",
            actions,
            combatMonsters: new Dictionary<string, CombatMonsterDefinition> { [monster.Id] = monster },
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter });
    }

    private static GameDataCatalog CreateCatalog(double enemyHitpoints, double enemyAttackPower,
        double searchSeconds = 1, BaseAttributeCatalog? baseAttributes = null)
    {
        var actionId = new ActionId("combat.test");
        var actions = new ActionCatalog([
            new ActionDefinition(
                actionId,
                "讨伐测试",
                ActionKind.Combat,
                0,
                new Dictionary<ItemId, double>(),
                [],
                ExecutionKind: ActionExecutionKind.CombatEncounter,
                CombatEncounterId: "encounter.test")
        ]);
        var monster = new CombatMonsterDefinition(
            "enemy.test",
            "测试妖兽",
            1,
            1,
            CombatMonsterRank.Minion,
            CombatantStats.Create(enemyHitpoints, enemyAttackPower, 0, 2),
            [],
            []);
        var encounter = new CombatEncounterDefinition(
            "encounter.test",
            "测试遭遇",
            1,
            1,
            1,
            searchSeconds,
            1,
            180,
            [new CombatSpawnDefinition("enemy.test", 1, 1)],
            [],
            [],
            0);
        return new GameDataCatalog(
            "test-1",
            "test-fingerprint",
            actions,
            combatMonsters: new Dictionary<string, CombatMonsterDefinition> { [monster.Id] = monster },
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter },
            baseAttributes: baseAttributes);
    }
}
