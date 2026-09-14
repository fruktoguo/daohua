namespace Domain.Tests;

public sealed class CombatExpeditionSimulationTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InfiniteCombatActionCanBeEnqueuedAndKeepsRunningState()
    {
        var catalog = CreateCatalog(enemyHitpoints: 1, enemyAttackPower: 0);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");

        var action = player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2), catalog);

        Assert.True(result.Changed);
        Assert.Single(player.Actions);
        Assert.Equal(action.Id, player.Actions[0].Id);
        Assert.Equal(-1, player.Actions[0].RemainingRepeats);
        Assert.Equal(1, player.Actions[0].CompletedRepeats);
        Assert.NotNull(player.Actions[0].Expedition);
    }

    [Fact]
    public void FiniteCombatActionIsRemovedAfterWinningEncounter()
    {
        var catalog = CreateCatalog(enemyHitpoints: 1, enemyAttackPower: 0);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        player.SetLoopEnabled(false);

        player.Enqueue(new ActionIntent(new ActionId("combat.test"), 1), Baseline, catalog.Actions);
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2), catalog);

        Assert.Equal(1, result.CompletedRepeats);
        Assert.Empty(player.Actions);
        Assert.Equal(1, player.Progression.CompletedActions);
    }

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

    [Fact]
    public void CombatWaveFightsMonstersBackToBackAndCountsOneRepeatPerWave()
    {
        var catalog = CreateWaveCatalog();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(1), catalog);
        var events = new List<(string Code, string Message)>(result.Events.Select(entry => (entry.Code, entry.Message)));
        var action = Assert.Single(player.Actions);
        Assert.Equal(CombatExpeditionPhase.Combat, action.Expedition!.Phase);
        Assert.Equal(1, action.Expedition.WaveNumber);

        // 逐步推进直到本波三只全部讨伐成功；同波内不允许重新进入搜寻阶段。
        var guard = 0;
        while (events.Count(entry => entry.Code == "combat.won") < 3 && guard++ < 120)
        {
            result = SimulationEngine.AdvanceTo(player, player.LastSimulatedAt.AddSeconds(0.5), catalog);
            events.AddRange(result.Events.Select(entry => (entry.Code, entry.Message)));
        }

        Assert.Equal(3, events.Count(entry => entry.Code == "combat.won"));
        Assert.Equal(2, events.Count(entry => entry.Code == "combat.wave.next"));
        Assert.Equal(1, player.Actions[0].CompletedRepeats);
        Assert.Equal(2, player.Actions[0].Expedition!.WaveNumber);
    }

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
        Assert.Equal(player.Actions[0].Expedition, restored.Actions[0].Expedition);
    }

    [Fact]
    public void CombatRestoreRejectsOutcomeThatDoesNotMatchHitpoints()
    {
        var stats = CombatantStats.Create(10, 1, 0, 1);

        Assert.Throws<DomainException>(() => CombatEncounterState.Restore(
            "encounter.test", 1, stats, stats, 0, 10, 0, 0, 0, 0, 0, CombatOutcome.Running));
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
                new Dictionary<ItemId, double>(),
                ExecutionKind: ActionExecutionKind.CombatEncounter,
                CombatEncounterId: "encounter.wave")
        ]);
        var monster = new CombatMonsterDefinition(
            "enemy.wave",
            "波次妖兽",
            10,
            CombatantStats.Create(1, 0, 0, 2),
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

    private static GameDataCatalog CreateCatalog(double enemyHitpoints, double enemyAttackPower)
    {
        var actionId = new ActionId("combat.test");
        var actions = new ActionCatalog([
            new ActionDefinition(
                actionId,
                "讨伐测试",
                ActionKind.Combat,
                0,
                new Dictionary<ItemId, double>(),
                new Dictionary<ItemId, double>(),
                ExecutionKind: ActionExecutionKind.CombatEncounter,
                CombatEncounterId: "encounter.test")
        ]);
        var monster = new CombatMonsterDefinition(
            "enemy.test",
            "测试妖兽",
            1,
            CombatantStats.Create(enemyHitpoints, enemyAttackPower, 0, 2),
            []);
        var encounter = new CombatEncounterDefinition(
            "encounter.test",
            "测试遭遇",
            1,
            1,
            1,
            1,
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
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter });
    }
}
