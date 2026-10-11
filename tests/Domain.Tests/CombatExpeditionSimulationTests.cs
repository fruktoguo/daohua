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
    /// 原地复活（onPartyWipe 缺省 respawn）：玩家阵亡不拆场——相位保持 Combat、同波敌人保留、
    /// 战斗时钟上的复活截止点落地；边界前一刻仍 0 血躺尸，到点满血起身续打同一波残局；
    /// 快照恢复的副本与在线副本在同一时刻产生一致结果（倒地窗口时序确定性）。
    /// </summary>
    [Fact]
    public void DownedPlayerRevivesInPlaceKeepingSameWave()
    {
        var catalog = CreateCatalog(enemyHitpoints: 1_000, enemyAttackPower: 1_000);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");

        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3), catalog);

        var action = Assert.Single(player.Actions);
        Assert.Equal(ActionQueueStatus.Running, action.Status);
        Assert.Equal(CombatExpeditionPhase.Combat, action.Expedition!.Phase);
        var respawnAt = Assert.IsType<double>(action.Expedition.PlayerRespawnAtSeconds);
        Assert.Equal(0, player.Resources.Hitpoints);
        var enemy = Assert.Single(action.Expedition.Enemies!);
        Assert.True(enemy.State.Hitpoints > 0);
        Assert.Contains(result.Events, entry => entry.Code == "combat.lost");
        Assert.Equal(1, player.Progression.TotalDefeats);
        var remaining = respawnAt - action.Expedition.BattleElapsedSeconds;
        Assert.True(remaining is > 0 and <= 180);
        var restored = PlayerAggregate.Restore(player.ExportState());
        var boundary = player.LastSimulatedAt.AddSeconds(remaining);
        SimulationEngine.AdvanceTo(player, boundary.AddMilliseconds(-1), catalog);
        Assert.Equal(0, player.Resources.Hitpoints);
        Assert.Equal(CombatExpeditionPhase.Combat, action.Expedition.Phase);
        Assert.NotNull(action.Expedition.PlayerRespawnAtSeconds);
        var respawned = SimulationEngine.AdvanceTo(player, boundary, catalog);
        SimulationEngine.AdvanceTo(restored, boundary, catalog);
        Assert.Contains(respawned.Events, entry => entry.Code == "combat.respawned");
        Assert.Equal(player.Resources.MaxHitpoints, player.Resources.Hitpoints);
        Assert.Equal(CombatExpeditionPhase.Combat, action.Expedition.Phase);
        Assert.Null(action.Expedition.PlayerRespawnAtSeconds);
        Assert.Equal(-1, action.RemainingRepeats);
        Assert.Equal(0, action.CompletedRepeats);
        Assert.Equal(action.Expedition with { Enemies = null }, restored.Actions[0].Expedition! with { Enemies = null });
        Assert.Equal(action.Expedition.Enemies, restored.Actions[0].Expedition!.Enemies);
    }

    /// <summary>
    /// 团灭即败（onPartyWipe=defeat）：玩家方全灭即终局拆场——进入复活点读条相位、敌人快照清空、
    /// 波次重置回第 1 波；读条完成满血满蓝清冷却回到搜寻相位。
    /// </summary>
    [Fact]
    public void DefeatPartyWipeTearsDownAndRestartsFromWaveOne()
    {
        var catalog = CreateCatalog(enemyHitpoints: 1_000, enemyAttackPower: 1_000, onPartyWipe: PartyWipeRule.Defeat);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");

        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3), catalog);

        var action = Assert.Single(player.Actions);
        Assert.Equal(ActionQueueStatus.Running, action.Status);
        Assert.Equal(CombatExpeditionPhase.Respawning, action.Expedition!.Phase);
        Assert.Null(action.Expedition.PlayerRespawnAtSeconds);
        Assert.True(action.Expedition.Enemies is null || action.Expedition.Enemies.Count == 0);
        Assert.Equal(0, player.Resources.Hitpoints);
        Assert.Contains(result.Events, entry => entry.Code == "combat.lost");
        Assert.Equal(1, player.Progression.TotalDefeats);
        var remaining = action.Expedition.PhaseDurationSeconds - action.Expedition.PhaseElapsedSeconds;
        Assert.Equal(180, action.Expedition.PhaseDurationSeconds);
        var boundary = player.LastSimulatedAt.AddSeconds(remaining);
        var respawned = SimulationEngine.AdvanceTo(player, boundary, catalog);
        Assert.Contains(respawned.Events, entry => entry.Code == "combat.respawned");
        Assert.Equal(player.Resources.MaxHitpoints, player.Resources.Hitpoints);
        Assert.Equal(player.Resources.MaxManapoints, player.Resources.Manapoints);
        Assert.Equal(CombatExpeditionPhase.Search, action.Expedition.Phase);
        Assert.Equal(1, action.Expedition.WaveNumber);
        Assert.Equal(-1, action.RemainingRepeats);
        Assert.Equal(0, action.CompletedRepeats);
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
        Assert.Equal(player.Resources.Hitpoints, restored.Resources.Hitpoints);
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
        player.Resources.Hitpoints = 10;
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(12), catalog);

        var action = Assert.Single(player.Actions);
        Assert.Equal(CombatExpeditionPhase.InitialSearch, action.Expedition!.Phase);
        Assert.True(player.Resources.Hitpoints > 10);
        Assert.Equal(player.Resources.Hitpoints, action.Expedition.PlayerHitpoints);
    }

    /// <summary>
    /// 战内 Buff 跨波携带回归（总纲 §7.8.1）：resetOnWave=false 的 Buff 在整场战斗行动存续期间
    /// 随波次清空折算携带、下一波入场按剩余秒数重注入；行动实例离队时统一移除。
    /// </summary>
    [Fact]
    public void CombatBuffsCarryAcrossWavesAndClearOnActionRemoval()
    {
        var catalog = CreateCatalog(enemyHitpoints: 1, enemyAttackPower: 0);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        // 预置跨波携带态（等价于上一场已折算的嘲讽类 Buff）：2 层、剩余 30s、威胁增幅 400 点。
        player.Combat.Buffs = new PlayerCombatBuffState(
            [new CombatBuffCarry("buff.carry-test", 30, 2, ThreatPoints: 400, Name: "携带增益")]);

        var action = player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        // 搜寻 1s 后进入首波战斗：携带 Buff 注入战斗单位并随快照下发。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(1.2), catalog);
        Assert.Equal(CombatExpeditionPhase.Combat, action.Expedition!.Phase);
        var injected = Assert.Single(action.Expedition.PlayerState!.Buffs!);
        Assert.Equal("buff.carry-test", injected.BuffId);
        Assert.Equal(2, injected.Stacks);
        Assert.Equal(400, injected.ThreatPoints, 9);
        Assert.Equal(30, injected.ExpiresAtSeconds, 6);

        // 波次清空（怪 1 气血秒杀）回到搜寻：Buff 折算回携带态、剩余随战斗时钟略减。
        var guard = 0;
        while (action.Expedition.Phase == CombatExpeditionPhase.Combat && guard++ < 60)
            SimulationEngine.AdvanceTo(player, player.LastSimulatedAt.AddSeconds(0.5), catalog);
        Assert.Equal(CombatExpeditionPhase.Search, action.Expedition.Phase);
        var carried = Assert.Single(player.Combat.Buffs.Buffs);
        Assert.Equal("buff.carry-test", carried.BuffId);
        Assert.True(carried.RemainingSeconds is < 30 and > 0);

        // 第二波入场按定格剩余重注入：到期时刻 = 新时钟 0 + 剩余秒数。
        while (action.Expedition.Phase != CombatExpeditionPhase.Combat && guard++ < 120)
            SimulationEngine.AdvanceTo(player, player.LastSimulatedAt.AddSeconds(0.5), catalog);
        var reinjected = Assert.Single(action.Expedition.PlayerState!.Buffs!);
        Assert.Equal("buff.carry-test", reinjected.BuffId);
        Assert.Equal(carried.RemainingSeconds, reinjected.ExpiresAtSeconds, 6);

        // 行动移除 = 整场战斗行动结束：携带态统一清空。
        Assert.True(player.RemoveAction(action.Id));
        Assert.True(player.Combat.Buffs.IsEmpty);
    }

    /// <summary>
    /// 战内 Buff 跨波携带序列化回归：携带态经导出/恢复 JSON 往返后仍满足不变量，
    /// 且搜寻相位存续的 Buff 在恢复副本中逐条一致。
    /// </summary>
    [Fact]
    public void CombatBuffCarrySurvivesStateRoundTrip()
    {
        var catalog = CreateCatalog(enemyHitpoints: 1, enemyAttackPower: 0);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        player.Combat.Buffs = new PlayerCombatBuffState(
            [new CombatBuffCarry("buff.carry-test", 30, 2, ThreatPoints: 400, Name: "携带增益")]);
        var action = player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3.5), catalog);

        var restored = PlayerAggregate.Restore(player.ExportState());
        Assert.Equal(player.Combat.Buffs.Buffs, restored.Combat.Buffs.Buffs);
        Assert.True(restored.Combat.Buffs.IsValid);
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

    /// <summary>
    /// 快照生命/灵力超出恢复时点模板上限（装备/Buff/配表漂移可使历史快照合法超限）按上限钳制恢复，
    /// 而不是判损坏拒绝——否则该玩家实体将永久不可装载。
    /// </summary>
    [Fact]
    public void CombatRestoreClampsSnapshotAboveRestoredMax()
    {
        var stats = CombatantStats.Create(100, 1, 0, 1) with { MaxManapoints = 50 };
        var enemyStats = CombatantStats.Create(10, 1, 0, 1);

        var restored = CombatEncounterState.Restore(
            "encounter.test", 1, new CombatantTemplate(stats),
            new CombatantSnapshot(240, 90, 0, 0, 0),
            [new CombatantTemplate(enemyStats)],
            [new CombatantSnapshot(10, 0, 0, 0, 0)],
            0, CombatOutcome.Running);

        Assert.Equal(100, restored.Player.Hitpoints);
        Assert.Equal(50, restored.Player.Manapoints);
        Assert.Equal(CombatOutcome.Running, restored.Outcome);
    }

    /// <summary>
    /// 负值/非有限生命灵力仍是真损坏：拒绝恢复而非静默钳制。
    /// </summary>
    [Fact]
    public void CombatRestoreRejectsNegativeOrNonFiniteVitalSnapshot()
    {
        var stats = CombatantStats.Create(100, 1, 0, 1) with { MaxManapoints = 50 };
        var enemyStats = CombatantStats.Create(10, 1, 0, 1);

        Assert.Throws<DomainException>(() => CombatEncounterState.Restore(
            "encounter.test", 1, new CombatantTemplate(stats),
            new CombatantSnapshot(-1, 10, 0, 0, 0),
            [new CombatantTemplate(enemyStats)],
            [new CombatantSnapshot(10, 0, 0, 0, 0)],
            0, CombatOutcome.Running));
        Assert.Throws<DomainException>(() => CombatEncounterState.Restore(
            "encounter.test", 1, new CombatantTemplate(stats),
            new CombatantSnapshot(10, double.NaN, 0, 0, 0),
            [new CombatantTemplate(enemyStats)],
            [new CombatantSnapshot(10, 0, 0, 0, 0)],
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
        double searchSeconds = 1, BaseAttributeCatalog? baseAttributes = null,
        PartyWipeRule onPartyWipe = PartyWipeRule.Respawn)
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
            0,
            OnPartyWipe: onPartyWipe);
        return new GameDataCatalog(
            "test-1",
            "test-fingerprint",
            actions,
            combatMonsters: new Dictionary<string, CombatMonsterDefinition> { [monster.Id] = monster },
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter },
            baseAttributes: baseAttributes);
    }
}
