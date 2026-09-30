namespace Domain.Tests;

/// <summary>
/// 斗法（PVP）领域测试：遭遇计数、匹配等待相位、剧本挂载结算、弃战判负、失败冷却、暂存区与战斗收益账本。
/// </summary>
public sealed class DuelSimulationTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>勾选斗法进入战斗时按配置概率增加遭遇计数；无撮合能力时计数仍累计、不进入等待。</summary>
    [Fact]
    public void PvpEnabledActionRollsEncounterChargeOnBattleEntry()
    {
        var catalog = CreateCatalog(duel: new DuelSettings(EncounterChancePerBattle: 1));
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "fp");

        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1, PvpEnabled: true), Baseline, catalog.Actions);
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2.5), catalog);

        Assert.Equal(1, player.Duel.EncounterCharges);
        Assert.Contains(result.Events, entry => entry.Code == "duel.charge");
        Assert.NotEqual(CombatExpeditionPhase.DuelPending, player.Actions[0].Expedition!.Phase);
    }

    /// <summary>遭遇计数大于 0 且撮合开启时，搜寻完成进入 DuelPending 匹配等待而非怪物波次。</summary>
    [Fact]
    public void ChargedSeekerEntersDuelPendingWhenMatchingEnabled()
    {
        var catalog = CreateCatalog();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "fp");
        player.Duel.EncounterCharges = 1;

        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1, PvpEnabled: true), Baseline, catalog.Actions);
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2), catalog, duel: new DuelAdvanceOptions(true));

        Assert.Equal(CombatExpeditionPhase.DuelPending, player.Actions[0].Expedition!.Phase);
        Assert.True(player.IsDuelPending);
        // 匹配等待静默进行：撮合结果未知前不产生日志，无对手时由运行时回退且全程无提示。
        Assert.DoesNotContain(result.Events, entry => entry.Code == "duel.seeking");
        // 匹配等待不消耗战斗进度，也不消耗遭遇计数（撮合成功预约时才扣）。
        Assert.Equal(1, player.Duel.EncounterCharges);
    }

    /// <summary>无撮合能力的离线补算路径：计数大于 0 也回退为正常波次，本波不再撮合。</summary>
    [Fact]
    public void ChargedSeekerFallsBackToWaveWithoutMatching()
    {
        var catalog = CreateCatalog();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "fp");
        player.Duel.EncounterCharges = 1;

        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1, PvpEnabled: true), Baseline, catalog.Actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(1.5), catalog);

        var expedition = player.Actions[0].Expedition!;
        Assert.Equal(CombatExpeditionPhase.Combat, expedition.Phase);
        Assert.Equal(1, player.Duel.EncounterCharges);
    }

    /// <summary>撮合剧本全流程：双方挂载同一份剧本，发起方胜利缴获对方暂存物品，败方进一小时冷却。</summary>
    [Fact]
    public void ResolvedDuelTransfersStagedItemsAndAppliesDefeatCooldown()
    {
        var catalog = CreateCatalog();
        var duelId = Guid.NewGuid();
        var (initiator, _) = PreparePendingSeeker(catalog, "甲");
        var (defender, _) = PreparePendingSeeker(catalog, "乙");
        defender.StageItem(new ItemId("item.loot"), 3, Baseline.ToUnixTimeMilliseconds() / 1000d);

        Assert.True(initiator.TryReserveDuel(duelId, NowUnix()));
        Assert.True(defender.TryReserveDuel(duelId, NowUnix()));
        var initiatorMirror = initiator.BuildDuelMirror(catalog) with { Stats = CombatantStats.Create(10_000, 1_000, 0, 1) };
        var defenderMirror = defender.BuildDuelMirror(catalog) with { Stats = CombatantStats.Create(10, 0, 0, 5) };
        var script = DuelScriptEngine.Resolve(initiatorMirror, defenderMirror, 42, catalog.Duel);
        Assert.Equal(CombatOutcome.PlayerWon, script.Outcome);
        Assert.Single(script.DefenderStagedItems);

        var duelIdText = duelId.ToString("N");
        initiator.AttachDuel(DuelPlaybackState.ForViewer(script, duelIdText, initiatorMirror, defenderMirror, true));
        defender.AttachDuel(DuelPlaybackState.ForViewer(script, duelIdText, initiatorMirror, defenderMirror, false));

        var winResult = SimulationEngine.AdvanceTo(initiator, initiator.LastSimulatedAt.AddSeconds(script.DurationSeconds + 5), catalog, duel: new DuelAdvanceOptions(true));
        Assert.Contains(winResult.AssetDeltas, delta => delta.Asset.ItemId.Value == "item.loot" && delta.QuantityDelta == 3);
        Assert.Null(initiator.Duel.ActiveDuelId);
        Assert.Equal(1, initiator.Duel.CurrentReport?.DuelWins);
        Assert.Contains(winResult.Events, entry => entry.Code == "duel.won");

        var loseResult = SimulationEngine.AdvanceTo(defender, defender.LastSimulatedAt.AddSeconds(script.DurationSeconds + 5), catalog, duel: new DuelAdvanceOptions(true));
        Assert.Empty(defender.Duel.StagedItems);
        Assert.Null(defender.Duel.ActiveDuelId);
        Assert.True(defender.Duel.CooldownUntilUnixSeconds > defender.LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d);
        Assert.Equal(1, defender.Duel.CurrentReport?.DuelLosses);
        Assert.Equal(1, defender.Progression.TotalDefeats);
        Assert.DoesNotContain(loseResult.AssetDeltas, delta => delta.Asset.ItemId.Value == "item.loot");
    }

    /// <summary>玩家主动结束斗法行动即弃战判负；对手收到通知后按胜利结算并缴获暂存物品。</summary>
    [Fact]
    public void ForfeitCountsAsDefeatAndPeerWins()
    {
        var catalog = CreateCatalog();
        var duelId = Guid.NewGuid();
        var (initiator, _) = PreparePendingSeeker(catalog, "甲");
        var (defender, defenderId) = PreparePendingSeeker(catalog, "乙");
        initiator.StageItem(new ItemId("item.loot"), 2, Baseline.ToUnixTimeMilliseconds() / 1000d);

        initiator.TryReserveDuel(duelId, NowUnix());
        defender.TryReserveDuel(duelId, NowUnix());
        var initiatorMirror = initiator.BuildDuelMirror(catalog) with { Stats = CombatantStats.Create(10_000, 1_000, 0, 1) };
        var defenderMirror = defender.BuildDuelMirror(catalog) with { Stats = CombatantStats.Create(10, 0, 0, 5) };
        var script = DuelScriptEngine.Resolve(initiatorMirror, defenderMirror, 7, catalog.Duel);
        var duelIdText = duelId.ToString("N");
        initiator.AttachDuel(DuelPlaybackState.ForViewer(script, duelIdText, initiatorMirror, defenderMirror, true));
        defender.AttachDuel(DuelPlaybackState.ForViewer(script, duelIdText, initiatorMirror, defenderMirror, false));

        // 剧本原本发起方必胜；发起方弃战立即判负，自身暂存物品清空且进入冷却。
        Assert.Equal(CombatOutcome.PlayerWon, script.Outcome);
        var forfeit = initiator.ApplyDuelForfeit(Baseline.AddSeconds(1), catalog.Duel);
        Assert.NotNull(forfeit);
        Assert.Equal(duelId, forfeit.Value.DuelId);
        Assert.Equal(defenderId, forfeit.Value.PeerId);
        Assert.Null(initiator.Duel.ActiveDuelId);
        Assert.Empty(initiator.Duel.StagedItems);
        Assert.Equal(1, initiator.Duel.CurrentReport?.DuelLosses);
        Assert.True(initiator.Duel.CooldownUntilUnixSeconds > 0);

        Assert.True(defender.ApplyPeerForfeit(duelId));
        var peerResult = SimulationEngine.AdvanceTo(defender, defender.LastSimulatedAt.AddSeconds(1), catalog, duel: new DuelAdvanceOptions(true));
        Assert.Contains(peerResult.AssetDeltas, delta => delta.Asset.ItemId.Value == "item.loot" && delta.QuantityDelta == 2);
        Assert.Equal(1, defender.Duel.CurrentReport?.DuelWins);
        Assert.Contains(peerResult.Events, entry => entry.Code == "duel.won");
    }

    /// <summary>失败冷却期内不再判定遭遇计数、也不进入匹配等待。</summary>
    [Fact]
    public void DefeatCooldownBlocksFurtherDuelSeek()
    {
        var catalog = CreateCatalog(duel: new DuelSettings(EncounterChancePerBattle: 1, DefeatCooldownSeconds: 3600));
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "fp");
        player.Duel.EncounterCharges = 5;
        player.Duel.CooldownUntilUnixSeconds = Baseline.AddHours(1).ToUnixTimeMilliseconds() / 1000d;

        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1, PvpEnabled: true), Baseline, catalog.Actions);
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(1.5), catalog, duel: new DuelAdvanceOptions(true));

        Assert.Equal(CombatExpeditionPhase.Combat, player.Actions[0].Expedition!.Phase);
        Assert.Equal(5, player.Duel.EncounterCharges);
        Assert.DoesNotContain(result.Events, entry => entry.Code is "duel.charge" or "duel.seeking");
    }

    /// <summary>斗法开启时战斗掉落进入暂存区而非直接入账，到期后由模拟推进统一结算。</summary>
    [Fact]
    public void PvpDropsEnterStagingAreaAndSettleAfterDelay()
    {
        var catalog = CreateCatalog(duel: new DuelSettings(StagedSettleSeconds: 1200), dropChance: 1);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "fp");

        player.Enqueue(new ActionIntent(new ActionId("combat.test"), 1, PvpEnabled: true), Baseline, catalog.Actions);
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3.5), catalog, duel: new DuelAdvanceOptions(true));

        Assert.Single(player.Duel.StagedItems);
        Assert.Equal("item.loot", player.Duel.StagedItems[0].ItemId);
        Assert.DoesNotContain(result.AssetDeltas, delta => delta.Asset.ItemId.Value == "item.loot");
        Assert.Contains(player.Duel.CurrentReport!.Items, item => item.ItemId == "item.loot");

        player.RemoveAction(player.Actions[0].Id);
        // 掉落入账时刻量化到 300s 桶尾（约 t=300），结算时刻 = 1500：推进 1600 秒后必然到期。
        var settled = SimulationEngine.AdvanceTo(player, player.LastSimulatedAt.AddSeconds(1600), catalog, duel: new DuelAdvanceOptions(true));
        Assert.Empty(player.Duel.StagedItems);
        Assert.Contains(settled.AssetDeltas, delta => delta.Asset.ItemId.Value == "item.loot" && delta.QuantityDelta == 1);
        Assert.Contains(settled.Events, entry => entry.Code == "duel.staged-settled");
    }

    /// <summary>暂存区档位桶化：同物品同桶合并为一条，跨桶另起批次；结算时刻取桶尾 + 入账延迟。</summary>
    [Fact]
    public void StagedItemsMergeByItemWithinSameBucket()
    {
        var catalog = CreateCatalog();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "fp");
        var t0 = NowUnix();

        player.StageItem(new ItemId("item.loot"), 2, t0 + 10);
        player.StageItem(new ItemId("item.loot"), 3, t0 + 200);
        player.StageItem(new ItemId("item.other"), 1, t0 + 200);
        player.StageItem(new ItemId("item.loot"), 5, t0 + 310);

        // t0+10 与 t0+200 落同一桶（向上取整到同一桶尾）；t0+310 跨入下一桶。
        var lootBatches = player.Duel.StagedItems.Where(item => item.ItemId == "item.loot").ToArray();
        Assert.Equal(2, lootBatches.Length);
        Assert.Equal(5, lootBatches[0].Quantity);
        Assert.Equal(5, lootBatches[1].Quantity);
        Assert.Equal(t0 + 300, lootBatches[0].AcquiredAtUnixSeconds);
        Assert.Equal(t0 + 600, lootBatches[1].AcquiredAtUnixSeconds);
        Assert.Single(player.Duel.StagedItems, item => item.ItemId == "item.other");
        // 战报按物品聚合不受批次影响：loot 总入账量 10。
        Assert.Equal(10, player.Duel.DailyReport.Items.First(item => item.ItemId == "item.loot").Quantity);

        // 第一桶 t0+300 在 t0+1500 到期（loot 与 other 同桶同摘），第二桶 t0+600 在 t0+1800 到期。
        var firstSettle = ActionReportBook.SettleStagedItems(player, t0 + 1500, 1200);
        Assert.Equal(2, firstSettle.Length);
        Assert.Equal(5, firstSettle.First(item => item.ItemId == "item.loot").Quantity);
        Assert.Single(player.Duel.StagedItems); // 仅剩 loot 第二桶
        var secondSettle = ActionReportBook.SettleStagedItems(player, t0 + 1800, 1200);
        Assert.Single(secondSettle);
        Assert.Empty(player.Duel.StagedItems);
    }

    /// <summary>区间数量掉落：先判定后数量，抽样落在 [min,max] 闭区间且上限可达（多波次采样覆盖）。</summary>
    [Fact]
    public void RangedDropQuantitySamplesWithinInclusiveBounds()
    {
        var catalog = CreateCatalog(dropChance: 1, dropMin: 1, dropMax: 3);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "fp");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // 逐拍推进并按「单杀增量」采集每次掉落实例：同拍多杀合并的增量跳过不计。
        var observed = new HashSet<double>();
        var kills = 0d;
        var looted = 0d;
        var at = Baseline;
        for (var i = 0; i < 180 && observed.Count < 3; i++)
        {
            at = at.AddSeconds(1.5);
            SimulationEngine.AdvanceTo(player, at, catalog);
            var report = player.Duel.CurrentReport;
            if (report is null) continue;
            var newKills = report.NormalKills;
            var newLooted = report.Items.FirstOrDefault(item => item.ItemId == "item.loot")?.Quantity ?? 0;
            var killsDelta = newKills - kills;
            var lootDelta = newLooted - looted;
            kills = newKills;
            looted = newLooted;
            if (killsDelta == 1) observed.Add(lootDelta);
        }

        Assert.True(observed.Count >= 2, $"采样数不足：{string.Join(',', observed)}");
        Assert.All(observed, q => Assert.InRange(q, 1, 3));
        Assert.Contains(3d, observed);
    }

    /// <summary>斗法进行中暂存区冻结：到期物品也不入账，直到斗法标识清空。</summary>
    [Fact]
    public void StagedItemsFreezeWhileDuelIsActive()
    {
        var catalog = CreateCatalog();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "fp");
        player.StageItem(new ItemId("item.loot"), 1, Baseline.ToUnixTimeMilliseconds() / 1000d);
        player.Duel.ActiveDuelId = Guid.NewGuid().ToString("N");

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2000), catalog, duel: new DuelAdvanceOptions(true));

        Assert.Single(player.Duel.StagedItems);
        Assert.DoesNotContain(result.AssetDeltas, delta => delta.Asset.ItemId.Value == "item.loot");
    }

    /// <summary>击杀奖励入账收益账本：单参数 experience 同时喂修为、战斗经验与六维均分。</summary>
    [Fact]
    public void BattleReportAccumulatesConfiguredKillRewards()
    {
        var catalog = CreateCatalog(experience: 10);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "fp");

        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3.5), catalog);

        var report = player.Duel.CurrentReport;
        Assert.NotNull(report);
        Assert.Equal(10, report.Experience);
        Assert.Equal(10, report.CombatExperience);
        Assert.Equal(10, report.RootExp.Total);
    }

    /// <summary>行动实例出队时归档其账本，归档上限取配置 MaxBattleReports。</summary>
    [Fact]
    public void BattleReportArchivesOnNewSessionAndCapsHistory()
    {
        var catalog = CreateCatalog(duel: new DuelSettings(MaxBattleReports: 10), experience: 10);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "fp");

        for (var i = 0; i < 12; i++)
        {
            var action = player.Enqueue(new ActionIntent(new ActionId("combat.test"), 1), player.LastSimulatedAt, catalog.Actions);
            SimulationEngine.AdvanceTo(player, player.LastSimulatedAt.AddSeconds(3.5), catalog);
            player.RemoveAction(action.Id);
        }

        // 12 个会话产出 12 本账：实例出队即归档，按上限截断到 10 条。
        Assert.Equal(10, player.Duel.Reports.Count);
        Assert.Empty(player.Duel.ActionReports);
    }

    /// <summary>斗法分段状态经导出/恢复往返后保持一致。</summary>
    [Fact]
    public void DuelStateRoundTripsThroughExportAndRestore()
    {
        var catalog = CreateCatalog();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "fp");
        player.Duel.EncounterCharges = 3;
        player.StageItem(new ItemId("item.loot"), 2, 100);

        var restored = PlayerAggregate.Restore(player.ExportState());

        Assert.Equal(3, restored.Duel.EncounterCharges);
        Assert.Single(restored.Duel.StagedItems);
        Assert.Equal("item.loot", restored.Duel.StagedItems[0].ItemId);
    }

    private static double NowUnix() => Baseline.ToUnixTimeMilliseconds() / 1000d;

    private static (PlayerAggregate Player, Guid PlayerId) PreparePendingSeeker(GameDataCatalog catalog, string name)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "fp");
        player.Duel.EncounterCharges = 1;
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1, PvpEnabled: true), Baseline, catalog.Actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2), catalog, duel: new DuelAdvanceOptions(true));
        return (player, player.PlayerId.Value);
    }

    private static GameDataCatalog CreateCatalog(
        double enemyHitpoints = 1,
        double enemyAttackPower = 0,
        double searchSeconds = 1,
        DuelSettings? duel = null,
        double dropChance = 0,
        double dropMin = 1,
        double dropMax = 1,
        double experience = 0)
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
        var drops = dropChance > 0
            ? new List<CombatDropDefinition> { new(new ItemId("item.loot"), dropChance, dropMin, dropMax) }
            : (IReadOnlyList<CombatDropDefinition>)[];
        var monster = new CombatMonsterDefinition(
            "enemy.test",
            "测试妖兽",
            1,
            1,
            CombatMonsterRank.Minion,
            CombatantStats.Create(enemyHitpoints, enemyAttackPower, 0, 2),
            drops,
            [],
            experience);
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
            duel: duel);
    }
}
