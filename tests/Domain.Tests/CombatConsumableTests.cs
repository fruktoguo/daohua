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

    /// <summary>测试阵盘：战斗持续消耗品，2s 持续时长、无冷却，携带回蓝 Buff。</summary>
    private static readonly ItemId DiskId = new("item.test-disk");
    private static readonly string DiskJson = """
        {"id":"item.test-disk","name":"测试阵盘","kind":"consumable","category":"formation",
         "consumable":{"usableInActionTypes":["combat"],"durationSeconds":2,"isContinuous":true,
         "buffs":[{"buffId":"formation.test","name":"聚灵测试","durationSeconds":2,"mpRegen":5,"mpRegenPostRatio":0.3}]}}
        """;

    /// <summary>测试回复药品：即时消耗、60s 冷却，回复走 30s Buff（hpRegen=12，每 5 秒结算点）。</summary>
    private static readonly ItemId RegenPillId = new("item.test-regen-pill");
    private static readonly string RegenPillJson = """
        {"id":"item.test-regen-pill","name":"测试回春散","kind":"consumable","category":"pill",
         "consumable":{"usableInActionTypes":["combat"],"cooldownSeconds":60,
         "buffs":[{"buffId":"buff.test-regen","name":"回春","durationSeconds":30,"hpRegen":12}],
         "triggers":[{"dependency":"self","condition":"current-hp-percentage","comparator":"less-than-equal","value":50}]}}
        """;

    private static GameDataCatalog CreateCatalog(double enemyHitpoints, double enemyAttackPower,
        double searchSeconds = 1, double respawnSeconds = 1, BaseAttributeCatalog? baseAttributes = null,
        PartyWipeRule onPartyWipe = PartyWipeRule.Respawn, double playerRespawnSeconds = 180)
    {
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("combat.test"), "讨伐测试", ActionKind.Combat, 0,
                new Dictionary<ItemId, double>(), [],
                ExecutionKind: ActionExecutionKind.CombatEncounter, CombatEncounterId: "encounter.test")
        ]);
        var items = new Dictionary<ItemId, ItemDefinition>
        {
            [PillId] = new(PillId, "测试丹药", "", true, 0,
                new ConsumableDefinition(new HashSet<string> { "combat" },
                    Triggers: [Json("""{"dependency":"self","condition":"current-hp-percentage","comparator":"less-than-equal","value":50}""")],
                    CooldownSeconds: 60),
                "pill"),
            [TalismanId] = new(TalismanId, "护身符", "", true, 0,
                new ConsumableDefinition(new HashSet<string> { "combat" },
                    DurationSeconds: 60, IsContinuous: true),
                "talisman"),
            [DiskId] = new(DiskId, "测试阵盘", "", true, 0,
                new ConsumableDefinition(new HashSet<string> { "combat" },
                    DurationSeconds: 2, IsContinuous: true),
                "formation"),
            [RegenPillId] = new(RegenPillId, "测试回春散", "", true, 0,
                new ConsumableDefinition(new HashSet<string> { "combat" },
                    Triggers: [Json("""{"dependency":"self","condition":"current-hp-percentage","comparator":"less-than-equal","value":50}""")],
                    CooldownSeconds: 60),
                "pill"),
        };
        var rawItems = new Dictionary<string, JsonElement>
        {
            [PillId.Value] = Json(PillJson),
            [TalismanId.Value] = Json(TalismanJson),
            [DiskId.Value] = Json(DiskJson),
            [RegenPillId.Value] = Json(RegenPillJson),
        };
        var monster = new CombatMonsterDefinition("enemy.test", "测试妖兽", 1, 1, CombatMonsterRank.Minion,
            CombatantStats.Create(enemyHitpoints, enemyAttackPower, 0, 2), [], []);
        var encounter = new CombatEncounterDefinition("encounter.test", "测试遭遇", 1, 1, 1, searchSeconds, respawnSeconds, playerRespawnSeconds,
            [new CombatSpawnDefinition("enemy.test", 1, 1)], [], [], 0, OnPartyWipe: onPartyWipe);
        return new GameDataCatalog("test-1", "test-fingerprint", actions,
            items: items, rawItems: rawItems,
            combatMonsters: new Dictionary<string, CombatMonsterDefinition> { [monster.Id] = monster },
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter },
            baseAttributes: baseAttributes);
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
            player.ConfigureAutoUse("exploration", 0, PillId.Value, 1, true, AutoUseSlotState.SlotGroupPotion));
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
        player.Resources.Hitpoints = player.Resources.MaxHitpoints * 0.4;
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(30), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(1d, wallet.Get(new AssetKey(PillId)));
        Assert.Contains(result.Events, entry => entry.Code == "auto-use.activated");
        Assert.True(player.Settings.AutoUseSlots[0].CooldownUntilUnixSeconds > 0);
    }

    /// <summary>
    /// 战斗内即时药品的 Buff 必须同时注入战内单位（与组队会话 EvaluateInstant 同口径）：
    /// 战内回复/免伤只读单位 Buff 列表，仅写战外 ActiveBuff 会被下一步战斗快照覆盖，
    /// 表现为「药已扣件却不回血、角色身上也没有 Buff 图标」。
    /// </summary>
    [Fact]
    public void InstantPillBuffEntersBattleUnitWhileCombatIsLive()
    {
        var catalog = CreateCatalog(enemyHitpoints: 100_000, enemyAttackPower: 0, searchSeconds: 1);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var wallet = CreateWallet(player, (RegenPillId, 1));
        player.ConfigureAutoUse("combat", 0, RegenPillId.Value, 1, true, AutoUseSlotState.SlotGroupPotion);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // 推进到战斗相位：满血不满足触发条件（气血≤50%），药品保持未使用。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(6), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(CombatExpeditionPhase.Combat, player.Actions[0].Expedition!.Phase);
        Assert.Equal(1d, wallet.Get(new AssetKey(RegenPillId)));

        // 战斗内压到 4% 气血：下一个资源边界自动使用，Buff 必须同时落到战内单位。
        player.Resources.Hitpoints = 40;
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(11), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);

        Assert.Equal(0d, wallet.Get(new AssetKey(RegenPillId)));
        Assert.Contains(player.Buffs.Active, entry => entry.BuffId == "buff.test-regen");
        var expedition = player.Actions[0].Expedition!;
        var battleBuff = Assert.Single(expedition.PlayerState!.Buffs!, entry => entry.BuffId == "buff.test-regen");
        Assert.Equal(12, battleBuff.EffectValue(BuffEffectChannel.HpRegen));
        Assert.True(battleBuff.ExpiresAtSeconds > expedition.BattleElapsedSeconds);
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

    /// <summary>
    /// 战中续补回归：原地复活遭遇下玩家阵亡-起身循环、波次永不结束（不存在任何新入场），
    /// 持续消耗品槽位冷却（墙钟）到点后必须在战中直接补能并重新注入 Buff，
    /// 而不是等下一次入场才恢复——对齐「复活后阵盘不自动生效」的线上反馈。
    /// </summary>
    [Fact]
    public void SustainedSlotRechargesMidCombatWithoutReentry()
    {
        // 怪不可击杀：本场战斗永不结算；玩家被秒杀→原地复活 10s 读条→起身续打循环。
        var catalog = CreateCatalog(enemyHitpoints: 100_000_000, enemyAttackPower: 100_000,
            searchSeconds: 5, playerRespawnSeconds: 10);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var wallet = CreateWallet(player, (DiskId, 2));
        player.ConfigureAutoUse("combat", 0, DiskId.Value, 1, true, AutoUseSlotState.SlotGroupGeneral);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // T+8：入场扣第 1 件充入 2s（5s~7s 耗竭），无 CD 立即在战中续补第 2 件；
        // 此时已扣除全部 2 件，第 2 件处于生效中（约剩 1s）。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(8), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(0d, wallet.Get(new AssetKey(DiskId)));
        Assert.InRange(player.Settings.AutoUseSlots[0].RemainingDurationSeconds, 0, 2);
        Assert.Equal(0d, player.Settings.AutoUseSlots[0].CooldownUntilUnixSeconds);
        var buffs = player.Actions[0].Expedition?.PlayerState?.Buffs ?? [];
        Assert.Contains(buffs, buff => buff.BuffId == "formation.test");

        // T+15：第 2 件也自然耗竭，钱包已空不再续补，冷却恒为 0。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(15), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(0d, wallet.Get(new AssetKey(DiskId)));
        Assert.Equal(0d, player.Settings.AutoUseSlots[0].RemainingDurationSeconds);
        Assert.Equal(0d, player.Settings.AutoUseSlots[0].CooldownUntilUnixSeconds);
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
        Assert.Equal(0, player.Resources.Hitpoints);
        Assert.Equal(CombatExpeditionPhase.Combat, player.Actions[0].Expedition!.Phase);
        var downedRemaining = player.Settings.AutoUseSlots[0].RemainingDurationSeconds;
        Assert.InRange(downedRemaining, 45, 60);

        // 躺尸窗口内继续推进：持续消耗品按战斗时钟继续扣减（不冻结）。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(20), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(0, player.Resources.Hitpoints);
        Assert.True(player.Settings.AutoUseSlots[0].RemainingDurationSeconds < downedRemaining);
    }

    /// <summary>
    /// 持续消耗品无冷却：阵盘 2s 时长耗竭后冷却为 0，
    /// 下一波入场只要有存量即可直接重新扣件充能，无需等待冷却。
    /// </summary>
    [Fact]
    public void SustainedSlotRechargesAcrossWavesWithoutCooldown()
    {
        // 妖兽 30 血、0 攻击：波次战斗稳定数秒内结束，玩家永不阵亡；
        // 波间搜寻 5s + 复活 30s。
        var catalog = CreateCatalog(enemyHitpoints: 30, enemyAttackPower: 0, searchSeconds: 5, respawnSeconds: 30);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var wallet = CreateWallet(player, (DiskId, 1));
        player.ConfigureAutoUse("combat", 0, DiskId.Value, 1, true, AutoUseSlotState.SlotGroupGeneral);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // T+15：首波入场扣 1 件充入 2s，战斗时钟耗竭后冷却恒为 0。
        var first = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(15), catalog, wallet);
        first.AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(0d, wallet.Get(new AssetKey(DiskId)));
        Assert.Equal(0d, player.Settings.AutoUseSlots[0].RemainingDurationSeconds);
        Assert.Equal(0d, player.Settings.AutoUseSlots[0].CooldownUntilUnixSeconds);
        Assert.Contains(first.Events, entry => entry.Code == "auto-use.depleted");

        // 波间补货 1 件。
        wallet.Apply(new AssetDelta(player.PlayerId, new AssetKey(DiskId), 1, "test"));
        Assert.Equal(1d, wallet.Get(new AssetKey(DiskId)));

        // T+45：第二波入场，无冷却阻断——立刻自动扣除新补的 1 件并充能。
        var second = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(45), catalog, wallet);
        second.AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(0d, wallet.Get(new AssetKey(DiskId)));
        Assert.Contains(second.Events, entry => entry.Code == "auto-use.activated");
        Assert.Equal(0d, player.Settings.AutoUseSlots[0].CooldownUntilUnixSeconds);
    }

    /// <summary>
    /// durable 时代注入的永驻 Buff（MaxValue 过期）由资源边界同步兜底回收：
    /// 物品转为普通持续品或撤槽后 SourceItemId 已不可追，仍须在下一边界清除。
    /// </summary>
    [Fact]
    public void OrphanedDurableBuffIsReclaimedAtResourceBoundary()
    {
        var catalog = CreateCatalog(enemyHitpoints: 100_000, enemyAttackPower: 0);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var wallet = CreateWallet(player);
        // 模拟 durable 时代残留：阵盘现为普通持续品且槽位未装配，MaxValue 过期永不自然到期。
        player.Buffs.Active.Add(new ActiveBuff("formation.test", double.MaxValue, 1,
            "聚灵测试", DiskId.Value, Effects: [new BuffEffect(BuffEffectChannel.MpRegen, 5), new BuffEffect(BuffEffectChannel.MpRegenPostRatio, 0.3)]));
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(1), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.DoesNotContain(player.Buffs.Active, entry => entry.BuffId == "formation.test");
    }

    /// <summary>
    /// 药品回复走 Buff 通道：激活不写即时回血，30s Buff 折叠进属性回复收口，
    /// 战外按 5 秒结算点入账，到期后回复回落到基础值。
    /// </summary>
    [Fact]
    public void InstantPillRegenFlowsThroughBuffOnFiveSecondTicks()
    {
        // 搜寻 30s：药品激活发生在入战前，战外资源回复路径可直接观测；
        // 锚点基准回复 hpRegen=2（每 5 秒结算点），属性缓存通道同时被覆盖。
        var catalog = CreateCatalog(enemyHitpoints: 100_000, enemyAttackPower: 0, searchSeconds: 30,
            baseAttributes: new BaseAttributeCatalog(
                [new BaseAttributeAnchor(1, 1000, 100, 10, 0, 0, 0, 100, 0, 0, 0, 0, 0, 2, 1)]));
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "甲", Baseline, "catalog-a");
        var wallet = CreateWallet(player, (RegenPillId, 1));
        player.ConfigureAutoUse("combat", 0, RegenPillId.Value, 1, true, AutoUseSlotState.SlotGroupPotion);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);
        // 触发条件 self 气血≤50%：40 点气血相对 1000 上限为 4%，必定激活。
        player.Resources.Hitpoints = 40;
        var baseline = PlayerAttributeResolver.Resolve(player, catalog);

        // T+3：药品已激活——无即时回血（无 hitpointRestore 字段），回复只进 Buff。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(0d, wallet.Get(new AssetKey(RegenPillId)));
        var buff = Assert.Single(player.Buffs.Active, entry => entry.BuffId == "buff.test-regen");
        Assert.Equal(12, buff.EffectValue(BuffEffectChannel.HpRegen));
        Assert.Equal(RegenPillId.Value, buff.SourceItemId);
        Assert.Equal(40, player.Resources.Hitpoints, 6);

        // 回复收口公式：(锚点经乘区 + Buff 12) 每 5 秒结算点一次——T+5 边界后入账一次。
        var perTick = PlayerAttributeResolver.Resolve(player, catalog).HpRegen;
        Assert.Equal(12, perTick - baseline.HpRegen, 9);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(8), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(40 + perTick, player.Resources.Hitpoints, 6);

        // Buff 到期（激活后 30s）后回复回落到基础值。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(40), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.DoesNotContain(player.Buffs.Active, entry => entry.BuffId == "buff.test-regen");
        Assert.Equal(baseline.HpRegen, PlayerAttributeResolver.Resolve(player, catalog).HpRegen, 9);
    }
}
