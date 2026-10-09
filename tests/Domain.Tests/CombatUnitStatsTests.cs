using System.Text.Json;

namespace Domain.Tests;

/// <summary>
/// 行动内战斗单位统计（combatants）领域门禁：
/// 伤害/承伤/减免按物理·术法分列、自动回复与其他治疗分列、同类怪物折叠、
/// 导出恢复往返、行动归档即弃、斗法本方并入且对方队伍不入账。
/// </summary>
public sealed class CombatUnitStatsTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "fp");

    /// <summary>攻防双向落账：攻方计输出、守方计实收与减免，物理通道各自分列且行内守恒。</summary>
    [Fact]
    public void HitRecordsDealtTakenAndMitigated()
    {
        var catalog = CreateCombatCatalog(enemyHitpoints: 100_000, enemyAttackPower: 50);
        var player = CreatePlayer("甲");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(6), catalog);

        var report = player.Duel.CurrentReport;
        Assert.NotNull(report);
        var self = Assert.Single(report.CombatStats.Entries, unit => unit.UnitKey == "player");
        var enemy = Assert.Single(report.CombatStats.Entries, unit => unit.UnitKey == "monster:enemy.test");
        Assert.Equal(CombatStatUnitKind.Self, self.Kind);
        Assert.Equal(CombatStatUnitKind.Monster, enemy.Kind);
        Assert.Equal("甲", self.DisplayName);
        Assert.Equal("测试妖兽", enemy.DisplayName);
        // 玩家物理普攻输出 == 怪物实收物理伤害；怪物物理输出 == 玩家实收物理伤害，
        // 减免独立成列（减免 + 实收 = 原始威力，攻方不重复计原始值）。
        Assert.True(self.DamageDealtPhysical > 0);
        Assert.Equal(self.DamageDealtPhysical, enemy.DamageTakenPhysical);
        Assert.True(enemy.DamageDealtPhysical > 0);
        Assert.Equal(enemy.DamageDealtPhysical, self.DamageTakenPhysical);
        Assert.True(self.DamageMitigatedPhysical > 0);
    }

    /// <summary>同定义 ID 的多只怪物折叠为单个 monster:{defId} 统计单位。</summary>
    [Fact]
    public void SameTypeMonstersMergeIntoOneUnit()
    {
        var catalog = CreateCombatCatalog(enemyHitpoints: 60, enemyAttackPower: 0,
            spawns:
            [
                new CombatSpawnDefinition("enemy.test", 1, 1),
                new CombatSpawnDefinition("enemy.test", 1, 1),
            ]);
        var player = CreatePlayer("甲");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(8), catalog);

        var report = player.Duel.CurrentReport;
        Assert.NotNull(report);
        var enemy = Assert.Single(report.CombatStats.Entries, unit => unit.Kind == CombatStatUnitKind.Monster);
        Assert.Equal("monster:enemy.test", enemy.UnitKey);
        Assert.True(enemy.DamageTakenPhysical > 0);
    }

    /// <summary>术法技能伤害计入术法通道：掌心雷施放后玩家 DamageDealtSpell &gt; 0。</summary>
    [Fact]
    public void SpellDamageRecordedSeparately()
    {
        var embedded = GameDataCatalog.LoadEmbedded();
        var catalog = CreateCombatCatalog(enemyHitpoints: 100_000, enemyAttackPower: 0,
            abilities: new Dictionary<string, JsonElement>
            {
                ["ability.palm-lightning"] = embedded.Abilities["ability.palm-lightning"].Clone()
            });
        var player = CreatePlayer("甲");
        var abilityId = new AbilityId("ability.palm-lightning");
        player.LearnAbility(abilityId);
        player.Loadout.AbilitySlots[0] = new PlayerAbilitySlot("ability.palm-lightning", 1);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // 吟唱 2s + 普攻周期：推进 8 秒保证掌心雷至少命中一次。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(8), catalog);

        var self = Assert.Single(player.Duel.CurrentReport!.CombatStats.Entries, unit => unit.UnitKey == "player");
        Assert.True(self.DamageDealtSpell > 0);
        var enemy = Assert.Single(player.Duel.CurrentReport.CombatStats.Entries,
            unit => unit.UnitKey == "monster:enemy.test");
        Assert.Equal(self.DamageDealtSpell, enemy.DamageTakenSpell);
    }

    /// <summary>行动存续期间的战外自然回复计「自动回复」：搜寻读条窗口内按 5 秒结算点入账。</summary>
    [Fact]
    public void OutOfCombatRegenCountsAsAutoRegen()
    {
        var catalog = CreateCombatCatalog(enemyHitpoints: 100_000, enemyAttackPower: 0, searchSeconds: 60,
            baseAttributes: new BaseAttributeCatalog(
                [new BaseAttributeAnchor(1, 1000, 100, 10, 0, 0, 0, 100, 0, 0, 0, 0, 0, 2, 1)]));
        var player = CreatePlayer("甲");
        // 半血进入讨伐：搜寻窗口内自然回复（每 5 秒结算点 +2）应持续入账。
        player.Resources.Hitpoints = 500;
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(12), catalog);

        var self = Assert.Single(player.Duel.CurrentReport!.CombatStats.Entries, unit => unit.UnitKey == "player");
        Assert.True(self.HealingRegen > 0);
        Assert.Equal(0, self.HealingOther);
    }

    /// <summary>药品瞬时回血计「其他治疗」：触发激活的瞬回量按实际入账值记录。</summary>
    [Fact]
    public void InstantPillCountsAsOtherHealing()
    {
        var catalog = CreateCombatCatalog(enemyHitpoints: 100_000, enemyAttackPower: 0, searchSeconds: 60,
            baseAttributes: new BaseAttributeCatalog(
                [new BaseAttributeAnchor(1, 1000, 100, 10, 0, 0, 0, 100, 0, 0, 0, 0, 0, 2, 1)]),
            pill: true);
        var player = CreatePlayer("甲");
        var wallet = CreateWallet(player, (new ItemId("item.test-pill"), 1));
        player.ConfigureAutoUse("combat", 0, "item.test-pill", 1, true, AutoUseSlotState.SlotGroupPotion);
        // 气血 40/1000 = 4%，满足 self ≤50% 触发：瞬时 +50 计其他治疗。
        player.Resources.Hitpoints = 40;
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // 10 秒窗口覆盖药品激活边界与首个自然回复结算点。
        var advanced = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet);
        Assert.Contains(advanced.Events, entry => entry.Code == "auto-use.activated");

        var self = Assert.Single(player.Duel.CurrentReport!.CombatStats.Entries, unit => unit.UnitKey == "player");
        Assert.Equal(50, self.HealingOther);
        Assert.True(self.HealingRegen > 0);
    }

    /// <summary>战内 5 秒结算点回复计「自动回复」：怪物面板 HpRegen 经 tick 落账到怪物行。</summary>
    [Fact]
    public void InCombatTickRegenCountsAsAutoRegen()
    {
        var catalog = CreateCombatCatalog(enemyHitpoints: 100_000, enemyAttackPower: 0, enemyHpRegen: 10);
        var player = CreatePlayer("甲");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // 推进超过一个 5 秒结算点：怪物已掉血后 tick 回复入账。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(7), catalog);

        var enemy = Assert.Single(player.Duel.CurrentReport!.CombatStats.Entries,
            unit => unit.UnitKey == "monster:enemy.test");
        Assert.True(enemy.HealingRegen > 0);
    }

    /// <summary>统计随账本经导出/恢复往返完整还原。</summary>
    [Fact]
    public void StatsSurviveExportRestoreRoundTrip()
    {
        var catalog = CreateCombatCatalog(enemyHitpoints: 100_000, enemyAttackPower: 50);
        var player = CreatePlayer("甲");
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(6), catalog);
        var before = player.Duel.CurrentReport!.CombatStats.Entries
            .Select(unit => (unit.UnitKey, unit.DamageDealtPhysical, unit.DamageTakenPhysical,
                unit.DamageMitigatedPhysical, unit.DamageDealtSpell, unit.DamageTakenSpell,
                unit.DamageMitigatedSpell, unit.HealingRegen, unit.HealingOther))
            .ToArray();
        Assert.NotEmpty(before);

        var restored = PlayerAggregate.Restore(player.ExportState());

        var after = restored.Duel.CurrentReport!.CombatStats.Entries
            .Select(unit => (unit.UnitKey, unit.DamageDealtPhysical, unit.DamageTakenPhysical,
                unit.DamageMitigatedPhysical, unit.DamageDealtSpell, unit.DamageTakenSpell,
                unit.DamageMitigatedSpell, unit.HealingRegen, unit.HealingOther))
            .ToArray();
        Assert.Equal(before, after);
    }

    /// <summary>行动出队归档时统计即弃：归档战报不携带 combatants。</summary>
    [Fact]
    public void StatsAreDiscardedOnArchive()
    {
        var catalog = CreateCombatCatalog(enemyHitpoints: 60, enemyAttackPower: 0);
        var player = CreatePlayer("甲");
        var action = player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(8), catalog);
        Assert.NotEmpty(player.Duel.CurrentReport!.CombatStats.Entries);

        player.RemoveAction(action.Id);

        var archived = Assert.Single(player.Duel.Reports);
        Assert.Empty(archived.CombatStats.Entries);
        // 导出文档同样不携带统计字段（归档即弃口径落到持久化层）。
        var document = PlayerDuelStateCodec.Export(player.Duel, null);
        Assert.All(document.Reports, entry => Assert.Null(entry.Combatants));
    }

    /// <summary>
    /// 斗法统计只并入本方：撮合剧本双方视角各持一行 player 统计，
    /// 挂载后并入队首行动账本且不出现对方单位行。
    /// </summary>
    [Fact]
    public void DuelStatsMergeSelfOnlyIntoHeadReport()
    {
        var catalog = CreateCombatCatalog();
        var initiator = CreatePlayer("甲");
        var defender = CreatePlayer("乙");
        foreach (var seeker in new[] { initiator, defender })
        {
            seeker.Duel.EncounterCharges = 1;
            seeker.Enqueue(new ActionIntent(new ActionId("combat.test"), -1, PvpEnabled: true), Baseline, catalog.Actions);
            SimulationEngine.AdvanceTo(seeker, Baseline.AddSeconds(2), catalog, duel: new DuelAdvanceOptions(true));
        }
        var duelId = Guid.NewGuid();
        var now = Baseline.ToUnixTimeMilliseconds() / 1000d;
        initiator.TryReserveDuel(duelId, now);
        defender.TryReserveDuel(duelId, now);
        var initiatorMirror = initiator.BuildDuelMirror(catalog) with { Stats = CombatantStats.Create(10_000, 1_000, 0, 1) };
        var defenderMirror = defender.BuildDuelMirror(catalog) with { Stats = CombatantStats.Create(10, 0, 0, 5) };
        var script = DuelScriptEngine.Resolve(initiatorMirror, defenderMirror, 42, catalog.Duel);
        Assert.NotNull(script.InitiatorStats);
        Assert.NotNull(script.DefenderStats);

        initiator.AttachDuel(DuelPlaybackState.ForViewer(script, duelId.ToString("N"), initiatorMirror, defenderMirror, true));

        var entries = initiator.Duel.CurrentReport!.CombatStats.Entries;
        var self = Assert.Single(entries, unit => unit.UnitKey == "player");
        // 发起方物理输出并入本方行；对方（防守方）不生成任何单位行。
        Assert.True(self.DamageDealtPhysical > 0);
        Assert.True(self.DamageTakenPhysical >= 0);
        Assert.DoesNotContain(entries, unit => unit.Kind != CombatStatUnitKind.Self);
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
        double searchSeconds = 1,
        double enemyHpRegen = 0,
        IReadOnlyList<CombatSpawnDefinition>? spawns = null,
        Dictionary<string, JsonElement>? abilities = null,
        BaseAttributeCatalog? baseAttributes = null,
        bool pill = false)
    {
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("combat.test"), "讨伐测试", ActionKind.Combat, 0,
                new Dictionary<ItemId, double>(), [],
                ExecutionKind: ActionExecutionKind.CombatEncounter, CombatEncounterId: "encounter.test")
        ]);
        var monster = new CombatMonsterDefinition("enemy.test", "测试妖兽", 1, 1, CombatMonsterRank.Minion,
            CombatantStats.Create(enemyHitpoints, enemyAttackPower, 0, 2) with { HpRegen = enemyHpRegen },
            [], [], 0);
        var encounter = new CombatEncounterDefinition("encounter.test", "测试遭遇", 1, 1, 1,
            searchSeconds, 1, 180,
            spawns ?? [new CombatSpawnDefinition("enemy.test", 1, 1)], [], [], 0);
        var items = new Dictionary<ItemId, ItemDefinition>();
        var rawItems = new Dictionary<string, JsonElement>();
        if (pill)
        {
            var pillId = new ItemId("item.test-pill");
            items[pillId] = new ItemDefinition(pillId, "测试丹药", "", true, 0,
                new ConsumableDefinition(new HashSet<string> { "combat" },
                    Triggers: [JsonDocument.Parse("""{"dependency":"self","condition":"current-hp-percentage","comparator":"less-than-equal","value":50}""").RootElement.Clone()],
                    CooldownSeconds: 60),
                "pill");
            rawItems[pillId.Value] = JsonDocument.Parse("""
                {"id":"item.test-pill","name":"测试丹药","kind":"consumable","category":"pill",
                 "consumable":{"usableInActionTypes":["combat"],"cooldownSeconds":60,"hitpointRestore":50,
                 "triggers":[{"dependency":"self","condition":"current-hp-percentage","comparator":"less-than-equal","value":50}]}}
                """).RootElement.Clone();
        }
        return new GameDataCatalog("test-1", "test-fingerprint", actions,
            items: items, rawItems: rawItems,
            combatMonsters: new Dictionary<string, CombatMonsterDefinition> { [monster.Id] = monster },
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter },
            abilities: abilities,
            baseAttributes: baseAttributes,
            realmProgression: GameDataCatalog.LoadEmbedded().RealmProgression);
    }
}
