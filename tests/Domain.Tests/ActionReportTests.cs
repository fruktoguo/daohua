using System.Text.Json;

namespace Domain.Tests;

/// <summary>
/// 行动统计三账本（行动实例账本 / 离线挂机账本 / 每日统计账本）领域测试：
/// 实例账本记账与续记、出队归档、离线/每日入账与重置、物品得失、等级提升与战斗计数。
/// </summary>
public sealed class ActionReportTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "fp");

    /// <summary>计时行动推进产生实例账本：完成轮次、物品产出（获得）与输入材料（失去）记账正确。</summary>
    [Fact]
    public void TimedActionAccumulatesInstanceReport()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("甲");
        // alchemy.refine-qi-pill：12 秒/轮，输入凝气草×3，产出引气丹×1。
        var wallet = CreateWallet(player, (new ItemId("item.ningxue-grass"), 6d));
        var action = player.Enqueue(new ActionIntent(new ActionId("alchemy.refine-qi-pill"), 2), Baseline, catalog.Actions);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(24), catalog, wallet);

        var report = player.Duel.ReportFor(action.Id) ?? player.Duel.Reports.FirstOrDefault(r => r.ActionId == "alchemy.refine-qi-pill");
        Assert.NotNull(report);
        Assert.Equal(2, report.Cycles);
        Assert.Contains(report.Items, item => item.ItemId == "item.qi-gathering-pill" && item.Quantity == 2);
        Assert.Contains(report.ItemsLost, item => item.ItemId == "item.ningxue-grass" && item.Quantity == 6);
        // 每日账本同步入账。
        Assert.Equal(2, player.Duel.DailyReport.Cycles);
    }

    /// <summary>材料不足被 Blocked 的行动不建账。</summary>
    [Fact]
    public void BlockedActionCreatesNoReport()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("甲");
        player.SetLoopEnabled(false);
        var action = player.Enqueue(new ActionIntent(new ActionId("alchemy.refine-qi-pill"), 1), Baseline, catalog.Actions);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(24), catalog, CreateWallet(player));

        Assert.Null(player.Duel.ReportFor(action.Id));
        Assert.Empty(player.Duel.Reports);
    }

    /// <summary>同一实例轮转/重复执行账本续记不重置；实例出队后归档进 Reports。</summary>
    [Fact]
    public void InstanceReportContinuesAcrossRotationAndArchivesOnRemoval()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("甲");
        var first = player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);
        var second = player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);

        // cultivation.draw-qi：10 秒/轮，每轮完成入账 10 修为 + 10 灵力。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog.Actions);
        Assert.Equal(10, player.Duel.ReportFor(first.Id)?.Experience);
        Assert.Equal(1, player.Duel.ReportFor(first.Id)?.Cycles);

        // 轮转：第二项上队首建账推进，第一项账本驻留不重置。
        player.MoveAction(second.Id, -1);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(20), catalog.Actions);
        Assert.Equal(10, player.Duel.ReportFor(second.Id)?.Experience);
        Assert.Equal(10, player.Duel.ReportFor(first.Id)?.Experience);

        // 第一项回到队首后续记原账本。
        player.MoveAction(first.Id, -1);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(30), catalog.Actions);
        Assert.Equal(20, player.Duel.ReportFor(first.Id)?.Experience);
        Assert.Equal(2, player.Duel.ReportFor(first.Id)?.Cycles);

        // 实例出队即归档；账本离开 ActionReports 进入 Reports 头部。
        player.RemoveAction(first.Id);
        Assert.Null(player.Duel.ReportFor(first.Id));
        Assert.Single(player.Duel.Reports);
        Assert.Equal(first.Id, player.Duel.Reports[0].ActionInstanceId);
        Assert.Equal(20, player.Duel.Reports[0].Experience);
    }

    /// <summary>转入 Offline 重置离线账本；离线推进收益同时进离线账本与每日账本。</summary>
    [Fact]
    public void OfflinePresenceRoutesRewardsToOfflineAndDailyReports()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("甲");
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);

        // 在线推进 10 秒：离线账本不收（Presence=Online），每日账本正常入账。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog.Actions);
        Assert.Equal(0, player.Duel.OfflineReport.Experience);
        Assert.Equal(10, player.Duel.DailyReport.Experience);

        // 转入离线重置离线账本后推进：收益同时进离线账本与每日账本。
        player.SetPresence(PresenceState.Offline, Baseline);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(20), catalog.Actions);
        Assert.Equal(10, player.Duel.OfflineReport.Experience);
        Assert.Equal(20, player.Duel.DailyReport.Experience);

        // 再次离线会话重置离线账本。
        player.SetPresence(PresenceState.Online, Baseline);
        player.SetPresence(PresenceState.Offline, Baseline);
        Assert.Equal(0, player.Duel.OfflineReport.Experience);
    }

    /// <summary>跨 UTC+8 日界推进时每日账本 rollover，只保留当日收益。</summary>
    [Fact]
    public void DailyReportRollsOverAtUtc8DayBoundary()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("甲");
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);

        // Baseline 为 UTC 00:00；UTC+8 日界在 UTC 16:00（Baseline+16h），推进 20 小时必跨日。
        SimulationEngine.AdvanceTo(player, Baseline.AddHours(20), catalog.Actions);

        var endUnix = Baseline.AddHours(20).ToUnixTimeMilliseconds() / 1000d;
        var expectedDay = (long)Math.Floor((endUnix + 28800) / 86400);
        Assert.Equal(expectedDay, player.Duel.DailyReport.DayIndex);
        // 当日仅累计日界之后的约 4 小时收益（每 10 秒 10 修为 ≈ 1 修为/秒），而非全程 20 小时。
        Assert.InRange(player.Duel.DailyReport.Experience, 4 * 3600, 4 * 3600 + 20);
    }

    /// <summary>功法参悟经验入账并记录 ability 级 LevelUp；突破记录 realm 级 LevelUp。</summary>
    [Fact]
    public void AbilityExperienceAndBreakthroughRecordLevelUps()
    {
        var player = CreatePlayer("甲");
        var abilityId = new AbilityId("ability.test");
        player.LearnAbility(abilityId);

        // 功法曲线保持 1 基：经验 100 → 2 级（floor(cbrt(100/100))+1），产生一条 ability 升级记录。
        player.GrantAbilityExperience(abilityId, 100);
        var daily = player.Duel.DailyReport;
        Assert.Contains(daily.AbilityExp, entry => entry.AbilityId == "ability.test" && entry.Experience == 100);
        Assert.Contains(daily.LevelUps, entry => entry.Kind == "ability" && entry.TargetId == "ability.test"
            && entry.FromLevel == 1 && entry.ToLevel == 2);

        // 突破（无瓶颈配表时直接升级）记录 realm 级 LevelUp，TargetId 为空串。
        player.Breakthrough(null);
        Assert.Contains(daily.LevelUps, entry => entry.Kind == "realm" && entry.TargetId == ""
            && entry.FromLevel == 1 && entry.ToLevel == 2);
    }

    /// <summary>战斗路径：遭遇场次、分级击杀与六维经验/升级入账正确。</summary>
    [Fact]
    public void CombatPathRecordsEncountersKillsAndRootExp()
    {
        // 高额六维奖励（20 万全额入各维）按境界派生点成本表升到 14 点（累计 192,667 ≤ 20 万 < 216,000），触发 root 升级记录。
        var catalog = CreateCombatCatalog(experience: 200_000);
        var player = CreatePlayer("甲");
        var action = player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2.5), catalog);

        var report = player.Duel.ReportFor(action.Id);
        Assert.NotNull(report);
        Assert.Equal(1, report.Encounters);
        Assert.Equal(1, report.Cycles);
        Assert.Equal(1, report.NormalKills);
        Assert.Equal(0, report.EliteKills);
        Assert.Equal(0, report.BossKills);
        Assert.Equal(0, report.Deaths);
        Assert.True(report.RootExp.Total > 0);
        Assert.Contains(report.LevelUps, entry => entry.Kind == "root" && entry.ToLevel == 14);
    }

    /// <summary>技能每次成功施放结算 1 点功法经验：装配分段与战报 AbilityExp 同步入账。</summary>
    [Fact]
    public void SkillUseGrantsOneAbilityExpPerCast()
    {
        var embedded = GameDataCatalog.LoadEmbedded();
        var catalog = CreateCombatCatalog(experience: 0,
            abilities: new Dictionary<string, JsonElement>
            {
                ["ability.heavy-slash"] = embedded.Abilities["ability.heavy-slash"].Clone()
            });
        var player = CreatePlayer("甲");
        var abilityId = new AbilityId("ability.heavy-slash");
        player.LearnAbility(abilityId);
        player.Loadout.AbilitySlots[0] = new PlayerAbilitySlot("ability.heavy-slash", 1);
        var action = player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // 单波内重斩（18s 冷却）至多施放一次：短时窗内恰好结算 1 点。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2.6), catalog);

        Assert.Equal(1, player.Loadout.AbilityExperiences[abilityId]);
        var report = player.Duel.ReportFor(action.Id);
        Assert.NotNull(report);
        Assert.Equal(1, Assert.Single(report.AbilityExp, entry => entry.AbilityId == "ability.heavy-slash").Experience);
    }

    /// <summary>讨伐失败计入 Deaths。</summary>
    [Fact]
    public void CombatDefeatRecordsDeath()
    {
        var catalog = CreateCombatCatalog(enemyHitpoints: 100_000, enemyAttackPower: 10_000);
        var player = CreatePlayer("甲");
        var action = player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(60), catalog);

        Assert.Equal(1, player.Progression.TotalDefeats);
        Assert.Equal(1, player.Duel.ReportFor(action.Id)?.Deaths);
    }

    /// <summary>斗法分段导出/恢复往返后三账本完整还原。</summary>
    [Fact]
    public void ReportsSurviveExportRestoreRoundTrip()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("甲");
        var action = player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);
        player.SetPresence(PresenceState.Offline, Baseline);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog.Actions);

        var restored = PlayerAggregate.Restore(player.ExportState());

        var report = restored.Duel.ReportFor(action.Id);
        Assert.NotNull(report);
        Assert.Equal(10, report.Experience);
        Assert.Equal(1, report.Cycles);
        Assert.Equal(10, restored.Duel.OfflineReport.Experience);
        Assert.Equal(10, restored.Duel.DailyReport.Experience);
        Assert.Equal(player.Duel.DailyReport.DayIndex, restored.Duel.DailyReport.DayIndex);
    }

    private static AssetWallet CreateWallet(PlayerAggregate player, params (ItemId ItemId, double Quantity)[] balances)
    {
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Restore(balances.Select(balance => new AssetBalance(player.PlayerId, new AssetKey(balance.ItemId), balance.Quantity, 0)));
        return wallet;
    }

    private static GameDataCatalog CreateCombatCatalog(
        double enemyHitpoints = 1,
        double enemyAttackPower = 0,
        double experience = 0,
        Dictionary<string, JsonElement>? abilities = null)
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
            [],
            experience);
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
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter },
            abilities: abilities,
            realmProgression: GameDataCatalog.LoadEmbedded().RealmProgression);
    }
}
