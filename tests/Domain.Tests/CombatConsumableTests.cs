using System.Text.Json;

namespace Domain.Tests;

/// <summary>
/// 战斗消耗品体系门禁：药品栏/通用栏分组约束、战斗存活时钟持续扣减（含躺尸等待仍扣、
/// 寻敌与脱战不扣）、即时消耗品共享触发条件求值。
/// </summary>
public sealed class CombatConsumableTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    /// <summary>测试药品：即时消耗、60s 冷却、self 气血≤50% 才允许自动使用。</summary>
    private static readonly ItemId PillId = new("item.test-pill");
    private static readonly string PillJson = """
        {"id":"item.test-pill","name":"测试丹药","kind":"consumable","category":"pill",
         "consumable":{"usableInActionTypes":["combat"],"cooldownSeconds":60,"hitpointRestore":50,
         "triggers":[{"dependency":"self","condition":"current-hp-percentage","comparator":"less-than-equal","value":50}]}}
        """;

    /// <summary>测试符箓：战斗持续消耗品，60s 持续时长，携带免伤 Buff。</summary>
    private static readonly ItemId TalismanId = new("item.test-talisman");
    private static readonly string TalismanJson = """
        {"id":"item.test-talisman","name":"护身符","kind":"consumable","category":"talisman",
         "consumable":{"usableInActionTypes":["combat"],"durationSeconds":60,"isContinuous":true,
         "buffs":[{"buffId":"buff.test-guard","name":"护身","durationSeconds":60,"damageAbsorptionPoints":10}]}}
        """;

    private static GameDataCatalog CreateCatalog(double enemyHitpoints, double enemyAttackPower,
        double searchSeconds = 1, double respawnSeconds = 1)
    {
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("combat.test"), "讨伐测试", ActionKind.Combat, 0,
                new Dictionary<ItemId, double>(), [],
                ExecutionKind: ActionExecutionKind.CombatEncounter, CombatEncounterId: "encounter.test")
        ]);
        var items = new Dictionary<ItemId, ItemDefinition>
        {
            [PillId] = new(PillId, "测试丹药", "", true, 0,
                new ConsumableDefinition((IReadOnlySet<ActionKind>)new HashSet<ActionKind> { ActionKind.Combat },
                    Triggers: [Json("""{"dependency":"self","condition":"current-hp-percentage","comparator":"less-than-equal","value":50}""")]),
                "pill"),
            [TalismanId] = new(TalismanId, "护身符", "", true, 0,
                new ConsumableDefinition((IReadOnlySet<ActionKind>)new HashSet<ActionKind> { ActionKind.Combat },
                    DurationSeconds: 60, IsContinuous: true),
                "talisman"),
        };
        var rawItems = new Dictionary<string, JsonElement>
        {
            [PillId.Value] = Json(PillJson),
            [TalismanId.Value] = Json(TalismanJson),
        };
        var monster = new CombatMonsterDefinition("enemy.test", "测试妖兽", 1, 1, CombatMonsterRank.Minion,
            CombatantStats.Create(enemyHitpoints, enemyAttackPower, 0, 2), [], []);
        var encounter = new CombatEncounterDefinition("encounter.test", "测试遭遇", 1, 1, 1, searchSeconds, respawnSeconds, 180,
            [new CombatSpawnDefinition("enemy.test", 1, 1)], [], [], 0);
        return new GameDataCatalog("test-1", "test-fingerprint", actions,
            items: items, rawItems: rawItems,
            combatMonsters: new Dictionary<string, CombatMonsterDefinition> { [monster.Id] = monster },
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter });
    }

    private static AssetWallet CreateWallet(PlayerAggregate player, params (ItemId ItemId, double Quantity)[] balances)
    {
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Restore(balances.Select(balance => new AssetBalance(player.PlayerId, new AssetKey(balance.ItemId), balance.Quantity, 0)));
        return wallet;
    }

    /// <summary>药品栏仅战斗域可用：非战斗行动装配 potion 组直接拒绝。</summary>
    [Fact]
    public void PotionGroupIsCombatOnly()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        Assert.Throws<DomainException>(() =>
            player.ConfigureAutoUse("cultivation", 0, PillId.Value, 1, true, AutoUseSlotState.SlotGroupPotion));
        player.ConfigureAutoUse("combat", 0, PillId.Value, 1, true, AutoUseSlotState.SlotGroupPotion);
        var slot = Assert.Single(player.Settings.AutoUseSlots);
        Assert.Equal(AutoUseSlotState.SlotGroupPotion, slot.Group);
        // 同下标的 general 组槽位与 potion 组互不覆盖。
        player.ConfigureAutoUse("combat", 0, TalismanId.Value, 1, true);
        Assert.Equal(2, player.Settings.AutoUseSlots.Count);
        Assert.Contains(player.Settings.AutoUseSlots, slot => slot.Group == AutoUseSlotState.SlotGroupGeneral);
    }

    /// <summary>即时消耗品共享触发判定：自身气血百分比高于阈值时不触发，压到阈值下才自动使用。</summary>
    [Fact]
    public void InstantPillHonorsSharedTriggerConditions()
    {
        var catalog = CreateCatalog(enemyHitpoints: 100_000, enemyAttackPower: 0);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var wallet = CreateWallet(player, (PillId, 2));
        player.ConfigureAutoUse("combat", 0, PillId.Value, 1, true, AutoUseSlotState.SlotGroupPotion);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // 满血推进 20s：触发条件（气血≤50%）不满足，药品不得消耗。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(20), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(2d, wallet.Get(new AssetKey(PillId)));

        // 气血压到 40%（≤50%）：下一个激活边界自动使用并消耗 1 件。
        player.Combat.Hitpoints = player.Combat.MaxHitpoints * 0.4;
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(30), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(1d, wallet.Get(new AssetKey(PillId)));
        Assert.Contains(result.Events, entry => entry.Code == "auto-use.activated");
        Assert.True(player.Settings.AutoUseSlots[0].CooldownUntilUnixSeconds > 0);
    }

    /// <summary>
    /// 战斗持续消耗品只在遭遇战中按战斗时钟扣减：入场补能、战中扣减、生效 Buff 注入。
    /// </summary>
    [Fact]
    public void SustainedCombatSlotDrainsOnlyInsideBattle()
    {
        var catalog = CreateCatalog(enemyHitpoints: 100_000, enemyAttackPower: 1, searchSeconds: 5);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var wallet = CreateWallet(player, (TalismanId, 1));
        player.ConfigureAutoUse("combat", 0, TalismanId.Value, 1, true, AutoUseSlotState.SlotGroupGeneral);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // 寻敌 5s 内不入战：槽位不应补能也不应扣减。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(4), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(0d, player.Settings.AutoUseSlots[0].RemainingDurationSeconds);
        Assert.Equal(1d, wallet.Get(new AssetKey(TalismanId)));

        // 入战后推进 15s：入场补能扣 1 件充入 60s，战斗时钟扣减约 10s。
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(15), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(0d, wallet.Get(new AssetKey(TalismanId)));
        var remaining = player.Settings.AutoUseSlots[0].RemainingDurationSeconds;
        Assert.InRange(remaining, 40, 55);
        // 生效 Buff 已注入战内快照。
        Assert.Contains(player.Actions[0].Expedition!.PlayerState!.Buffs ?? [],
            buff => buff.BuffId == "buff.test-guard");
    }

    /// <summary>寻敌阶段不扣减：战斗胜利清场后的搜寻窗口内剩余时长冻结。</summary>
    [Fact]
    public void SustainedSlotDoesNotDrainDuringSearch()
    {
        // 首波搜寻 30s、波间再搜寻 30s：第二波开战前的长窗口内剩余时长必须冻结。
        var catalog = CreateCatalog(enemyHitpoints: 1, enemyAttackPower: 0, searchSeconds: 30, respawnSeconds: 30);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var wallet = CreateWallet(player, (TalismanId, 1));
        player.ConfigureAutoUse("combat", 0, TalismanId.Value, 1, true, AutoUseSlotState.SlotGroupGeneral);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // 35s：首波搜寻结束后入场补能并打赢（1 血秒杀），随后进入 30s 波间搜寻。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(35), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(0d, wallet.Get(new AssetKey(TalismanId)));
        var frozenAt = player.Settings.AutoUseSlots[0].RemainingDurationSeconds;
        Assert.InRange(frozenAt, 50, 60);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(55), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(frozenAt, player.Settings.AutoUseSlots[0].RemainingDurationSeconds);
    }

    /// <summary>躺尸等待期间仍持续扣减：玩家阵亡等待复活时钟不冻结持续消耗。</summary>
    [Fact]
    public void SustainedSlotKeepsDrainingWhilePlayerDowned()
    {
        var catalog = CreateCatalog(enemyHitpoints: 1_000_000, enemyAttackPower: 100_000);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var wallet = CreateWallet(player, (TalismanId, 1));
        player.ConfigureAutoUse("combat", 0, TalismanId.Value, 1, true, AutoUseSlotState.SlotGroupGeneral);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // 推进到玩家已被击倒（敌方高攻秒杀），进入原地等待复活窗口。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(0, player.Combat.Hitpoints);
        Assert.Equal(CombatExpeditionPhase.Combat, player.Actions[0].Expedition!.Phase);
        var downedRemaining = player.Settings.AutoUseSlots[0].RemainingDurationSeconds;
        Assert.InRange(downedRemaining, 45, 60);

        // 躺尸窗口内继续推进：持续消耗品按战斗时钟继续扣减（不冻结）。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(20), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(0, player.Combat.Hitpoints);
        Assert.True(player.Settings.AutoUseSlots[0].RemainingDurationSeconds < downedRemaining);
    }
}
