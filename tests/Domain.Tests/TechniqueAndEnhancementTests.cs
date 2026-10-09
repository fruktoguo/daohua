using System.Text.Json;

namespace Domain.Tests;

/// <summary>
/// 七技艺体系与装备强化的领域门禁（总纲 §20）：经验曲线逐段推进、技艺速度折减、
/// 强化成本/成功率/确定性判定、+N 资产变体迁移、雷击符打击与阵盘持续消耗品生命周期。
/// </summary>
public sealed class TechniqueAndEnhancementTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "catalog-a");

    private static AssetWallet CreateWallet(PlayerAggregate player, params (AssetKey Asset, double Quantity)[] balances)
    {
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Restore(balances.Select(balance => new AssetBalance(player.PlayerId, balance.Asset, balance.Quantity, 0)));
        return wallet;
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    /// <summary>技艺经验复用境界曲线逐段推进：不足一段停在当前级，曲线封顶后不再升。</summary>
    [Fact]
    public void TechniqueLevelAdvancesSegmentBySegment()
    {
        var curve = new RealmProgressionCatalog([100, 200, 300], [80]);
        Assert.Equal(0, TechniqueCatalog.LevelFor(0, curve));
        Assert.Equal(0, TechniqueCatalog.LevelFor(99.999, curve));
        Assert.Equal(1, TechniqueCatalog.LevelFor(100, curve));
        Assert.Equal(1, TechniqueCatalog.LevelFor(299, curve));
        Assert.Equal(2, TechniqueCatalog.LevelFor(300, curve));
        Assert.Equal(3, TechniqueCatalog.LevelFor(600, curve));
        // 曲线封顶后经验继续累计但等级不再推进；无效输入按 0 级计。
        Assert.Equal(3, TechniqueCatalog.LevelFor(9999, curve));
        Assert.Equal(0, TechniqueCatalog.LevelFor(-5, curve));
        Assert.Equal(0, TechniqueCatalog.LevelFor(double.NaN, curve));
        Assert.Equal(0, TechniqueCatalog.LevelFor(100, null));
        foreach (var id in new[] { "alchemy", "forging", "talisman", "enhancement", "formation", "gathering", "mining" })
            Assert.True(TechniqueCatalog.IsKnown(id));
        Assert.False(TechniqueCatalog.IsKnown("cooking"));
        Assert.False(TechniqueCatalog.IsKnown(null));
    }

    /// <summary>技艺等级按 5%/级折减计时行动耗时；未配置技艺字段的行动不受影响。</summary>
    [Fact]
    public void TechniqueSpeedDiscountsTimedActionDuration()
    {
        var curve = new RealmProgressionCatalog([100, 200, 300], [80]);
        var alchemy = new ActionDefinition(new ActionId("alchemy.test"), "炼丹测试", ActionKind.Alchemy, 10,
            new Dictionary<ItemId, double>(), [], Technique: TechniqueCatalog.Alchemy);
        var plain = alchemy with { Id = new ActionId("plain.test"), Technique = null };
        var catalog = CreateMinimalCatalog(actions: new ActionCatalog([alchemy, plain]), realmProgression: curve);
        var player = CreatePlayer("甲");
        var action = player.Enqueue(new ActionIntent(alchemy.Id, -1), Baseline, catalog.Actions);
        var wallet = CreateWallet(player);

        // 0 级：无折减；技艺经验 300 → 2 级（100+200 逐段）→ 10 / (1 + 0.05×2)。
        Assert.Equal(10, SimulationEngine.TimedActionDuration(player, action, alchemy, catalog, wallet, null));
        player.Loadout.TechniqueExperiences[TechniqueCatalog.Alchemy] = 300;
        Assert.Equal(10 / 1.1, SimulationEngine.TimedActionDuration(player, action, alchemy, catalog, wallet, null), 12);
        // 未配置技艺字段的行动不读技艺经验。
        var plainAction = player.Enqueue(new ActionIntent(plain.Id, -1), Baseline, catalog.Actions);
        Assert.Equal(10, SimulationEngine.TimedActionDuration(player, plainAction, plain, catalog, wallet, null));
    }

    /// <summary>每完成一个有效行动轮次，技艺经验按基础耗时入账并写入战报 techniqueExp 账本。</summary>
    [Fact]
    public void TechniqueExperienceAccruesPerCompletedRepeat()
    {
        var curve = new RealmProgressionCatalog([100, 200, 300], [80]);
        var gather = new ActionDefinition(new ActionId("gather.test"), "采集测试", ActionKind.Exploration, 5,
            new Dictionary<ItemId, double>(),
            [new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, new ItemId("item.test-ore"))],
            Technique: TechniqueCatalog.Gathering);
        var catalog = CreateMinimalCatalog(actions: new ActionCatalog([gather]), realmProgression: curve);
        var player = CreatePlayer("采集者");
        player.Enqueue(new ActionIntent(gather.Id, -1), Baseline, catalog.Actions);
        var wallet = CreateWallet(player);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(5), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(5, player.Loadout.TechniqueExperiences[TechniqueCatalog.Gathering]);
        Assert.Contains(player.Duel.CurrentReport?.TechniqueExp ?? [],
            entry => entry.TechniqueId == TechniqueCatalog.Gathering && entry.Experience == 5);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(10, player.Loadout.TechniqueExperiences[TechniqueCatalog.Gathering]);
        // 技艺经验累计 10 未达首段（100），等级仍为 0；灌满后逐段升。
        player.Loadout.TechniqueExperiences[TechniqueCatalog.Gathering] = 100;
        Assert.Equal(1, TechniqueCatalog.LevelFor(
            player.Loadout.TechniqueExperiences[TechniqueCatalog.Gathering], curve));
    }

    /// <summary>强化公式族：变体解析、成本、成功率查表/指数/clamp 与技艺修正全部对齐规约。</summary>
    [Fact]
    public void EnhancementRulesMatchSpecification()
    {
        Assert.Equal(0, EnhancementRules.ParseLevel(null));
        Assert.Equal(0, EnhancementRules.ParseLevel(""));
        Assert.Equal(3, EnhancementRules.ParseLevel("+3"));
        Assert.Null(EnhancementRules.ParseLevel("x"));
        Assert.Null(EnhancementRules.ParseLevel("+"));
        var sword = new ItemId("equipment.test-sword");
        Assert.Equal(new AssetKey(sword), EnhancementRules.KeyFor(sword, 0));
        Assert.Equal("+3", EnhancementRules.KeyFor(sword, 3).Variant);

        Assert.Equal(6, EnhancementRules.DurationSeconds(1));
        Assert.Equal(25, EnhancementRules.DurationSeconds(20));
        Assert.Equal(10, EnhancementRules.SpiritStoneCostFor(1));
        Assert.Equal(200, EnhancementRules.SpiritStoneCostFor(20));

        Assert.Equal(0.5, EnhancementRules.BaseSuccessRate(1), 12);
        Assert.Equal(0.35, EnhancementRules.BaseSuccessRate(10), 12);
        Assert.Equal(0.3, EnhancementRules.BaseSuccessRate(11), 12);
        // t=20 走指数衰减 0.3×0.95^9，仍在基础 clamp [0.01, 0.5] 区间内。
        Assert.Equal(Math.Clamp(0.3 * Math.Pow(0.95, 9), 0.01, 0.5), EnhancementRules.BaseSuccessRate(20), 12);
        // 技艺修正：L≥t 加性 +0.01×L；L<t 乘性 ×0.9^(t-L)；终值 clamp [0, 0.99]。
        Assert.Equal(0.99, EnhancementRules.SuccessRate(1, 127), 12);
        Assert.Equal(0.5 * Math.Pow(0.9, 1), EnhancementRules.SuccessRate(1, 0), 12);
        Assert.Equal(0.4 + 0.01 * 5, EnhancementRules.SuccessRate(5, 5), 12);

        // 判定种子取行动实例 Id 前 8 字节、通道独立：同实例同轮必得同结果，异实例结果流独立。
        var prep = new EnhancementRules.Preparation(sword, 0, 1, 10, null, 0.5);
        var instanceA = Guid.NewGuid();
        var instanceB = Guid.NewGuid();
        Assert.Equal(EnhancementRules.Roll(instanceA, 0, prep), EnhancementRules.Roll(instanceA, 0, prep));
        Assert.Equal(1, EnhancementRules.ResultLevel(prep, EnhancementRules.Outcome.Success));
        Assert.Equal(0, EnhancementRules.ResultLevel(prep, EnhancementRules.Outcome.FailReset));
        Assert.Equal(0, EnhancementRules.ResultLevel(prep, EnhancementRules.Outcome.FailProtected));
        var prepProtected = prep with { ProtectionItemId = new ItemId("item.test-mat"), CurrentLevel = 4, TargetLevel = 5 };
        Assert.Equal(4, EnhancementRules.ResultLevel(prepProtected, EnhancementRules.Outcome.FailProtected));
        Assert.Equal(EnhancementRules.Roll(instanceA, 3, prep), EnhancementRules.Roll(instanceA, 3, prep));
        _ = EnhancementRules.Roll(instanceB, 0, prep); // 不同实例走同通道独立判定，不共享随机游标。
    }

    /// <summary>强化当前等级：装备槽位记录与钱包 +N 变体余额取最高者；未持有返回 -1。</summary>
    [Fact]
    public void EnhancementCurrentLevelReadsSlotsAndVariantBalances()
    {
        var loadout = new PlayerLoadoutState();
        var sword = new ItemId("equipment.test-sword");
        Assert.Equal(-1, EnhancementRules.CurrentLevel(loadout, sword, new Dictionary<AssetKey, double>()));
        var holdings = new Dictionary<AssetKey, double> { [new AssetKey(sword, "+3")] = 1 };
        Assert.Equal(3, EnhancementRules.CurrentLevel(loadout, sword, holdings));
        loadout.Equipment.Add(new PlayerEquipmentSlot(sword.Value, 5));
        Assert.Equal(5, EnhancementRules.CurrentLevel(loadout, sword, holdings));
        loadout.Equipment[0] = new PlayerEquipmentSlot(sword.Value, 2);
        // 槽位与余额共存时取最大（两者本应一致，异常数据下取更保守的高水位）。
        Assert.Equal(3, EnhancementRules.CurrentLevel(loadout, sword, holdings));
    }

    /// <summary>
    /// 强化端到端：单轮结算冻结成本、转移 +N 变体并授予技艺经验；
    /// 材料守恒与事件语义在成功/失败任一分支下都成立。
    /// </summary>
    [Fact]
    public void EnhancementRepeatSettlesVariantMaterialsAndTechniqueExp()
    {
        var sword = new ItemId("equipment.test-sword");
        var enhance = new ActionDefinition(new ActionId("enhance.equipment"), "装备强化",
            ActionKind.Enhancement, 0, new Dictionary<ItemId, double>(), [], Technique: TechniqueCatalog.Enhancement);
        var rawEquipments = new Dictionary<string, JsonElement>
        {
            [sword.Value] = Json("""{"id":"equipment.test-sword","name":"测试剑","flatStats":{"physicalAttack":10}}""")
        };
        var catalog = CreateMinimalCatalog(actions: new ActionCatalog([enhance]),
            rawEquipments: rawEquipments,
            realmProgression: new RealmProgressionCatalog([100, 200, 300], [80]));
        var player = CreatePlayer("强化者");
        var stone = new ItemId(EnhancementRules.SpiritStoneItemId);
        var wallet = CreateWallet(player,
            (new AssetKey(sword), 1), (new AssetKey(stone), 500));
        player.Enqueue(new ActionIntent(enhance.Id, -1, TargetItemId: sword.Value), Baseline, catalog.Actions);

        // t=1 基础耗时 6s（技艺 0 级无折减）：一轮结算后材料按冻结成本扣除。
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(6.2), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(490, wallet.Get(new AssetKey(stone)));
        // 强化资产守恒：目标装备总持有恒为 1，落在基础键或 +1 变体键上。
        var swordHeld = wallet.Get(new AssetKey(sword)) + wallet.Get(new AssetKey(sword, "+1"));
        Assert.Equal(1, swordHeld);
        Assert.Equal(6, player.Loadout.TechniqueExperiences[TechniqueCatalog.Enhancement]);
        Assert.Contains(result.Events, entry => entry.Code is "enhance.succeeded" or "enhance.failed");
    }

    /// <summary>目标装备或材料不足时强化行动走阻塞语义，不产出、不扣件。</summary>
    [Fact]
    public void EnhancementBlocksWhenTargetOrMaterialsMissing()
    {
        var sword = new ItemId("equipment.test-sword");
        var enhance = new ActionDefinition(new ActionId("enhance.equipment"), "装备强化",
            ActionKind.Enhancement, 0, new Dictionary<ItemId, double>(), [], Technique: TechniqueCatalog.Enhancement);
        var rawEquipments = new Dictionary<string, JsonElement>
        {
            [sword.Value] = Json("""{"id":"equipment.test-sword","name":"测试剑"}""")
        };
        var catalog = CreateMinimalCatalog(actions: new ActionCatalog([enhance]), rawEquipments: rawEquipments);
        var player = CreatePlayer("空手");
        player.Enqueue(new ActionIntent(enhance.Id, -1, TargetItemId: sword.Value), Baseline, catalog.Actions);
        var wallet = CreateWallet(player);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet);
        Assert.Equal(ActionQueueStatus.Blocked, player.Actions[0].Status);
        Assert.Contains(result.Events, entry => entry.Code == "action.blocked");
        Assert.Empty(result.AssetDeltas);
        Assert.Empty(player.Loadout.TechniqueExperiences);
    }

    /// <summary>
    /// 保护材料裁决：装备未配置 enhanceProtectionItems 时仅同装备裸件可代护（+0 时须持有 ≥2），
    /// 其他物品一律无效；配置白名单后只允许列表内物品，自身裸件反而被拒。
    /// </summary>
    [Fact]
    public void EnhancementProtectionMaterialFollowsWhitelistOrBaseCopyFallback()
    {
        var sword = new ItemId("equipment.test-sword");
        var guardOre = new ItemId("item.guard-ore");
        var other = new ItemId("item.other-mat");
        var stone = new ItemId(EnhancementRules.SpiritStoneItemId);
        var catalog = CreateMinimalCatalog(rawEquipments: new Dictionary<string, JsonElement>
        {
            [sword.Value] = Json("""{"id":"equipment.test-sword","name":"测试剑"}"""),
            ["equipment.listed-sword"] = Json("""{"id":"equipment.listed-sword","name":"白名单剑","enhanceProtectionItems":["item.guard-ore"]}""")
        });
        var listedSword = new ItemId("equipment.listed-sword");
        var loadout = new PlayerLoadoutState();

        static Dictionary<AssetKey, double> Holdings(params (AssetKey Key, double Qty)[] rows)
            => rows.ToDictionary(row => row.Key, row => row.Qty);
        var ready = Holdings(
            (new AssetKey(stone), 999));

        // 未配置白名单：+0 本体代护，本体+代护同一裸键，仅持 1 件不足。
        var intent = new ActionIntent(new ActionId("enhance.equipment"), -1,
            TargetItemId: sword.Value, AuxItemId: sword.Value);
        var prep = EnhancementRules.Resolve(loadout, intent,
            new Dictionary<AssetKey, double>(ready) { [new AssetKey(sword)] = 1 }, catalog, 0,
            out var check, out _);
        Assert.Null(prep);
        Assert.Equal(EnhancementRules.Check.Blocked, check);

        // 裸件 ≥2 时 Ready，Preparation 冻结保护材料为同装备。
        prep = EnhancementRules.Resolve(loadout, intent,
            new Dictionary<AssetKey, double>(ready) { [new AssetKey(sword)] = 2 }, catalog, 0,
            out check, out _);
        Assert.NotNull(prep);
        Assert.Equal(EnhancementRules.Check.Ready, check);
        Assert.Equal(sword, prep!.ProtectionItemId);

        // +N 变体强化以另一件裸件代护：源键与保护键不同，裸件 ≥1 即可。
        intent = new ActionIntent(new ActionId("enhance.equipment"), -1,
            TargetItemId: sword.Value, AuxItemId: sword.Value);
        prep = EnhancementRules.Resolve(loadout, intent,
            new Dictionary<AssetKey, double>(ready)
            {
                [new AssetKey(sword, "+3")] = 1,
                [new AssetKey(sword)] = 1
            }, catalog, 0, out check, out _);
        Assert.NotNull(prep);
        Assert.Equal(EnhancementRules.Check.Ready, check);
        Assert.Equal(3, prep!.CurrentLevel);

        // 未配置白名单时非本装备物品一律无效。
        intent = new ActionIntent(new ActionId("enhance.equipment"), -1,
            TargetItemId: sword.Value, AuxItemId: other.Value);
        Assert.Null(EnhancementRules.Resolve(loadout, intent,
            new Dictionary<AssetKey, double>(ready)
            {
                [new AssetKey(sword)] = 1,
                [new AssetKey(other)] = 5
            }, catalog, 0, out check, out _));
        Assert.Equal(EnhancementRules.Check.Blocked, check);

        // 已配置白名单：列表内材料可用；自身裸件与列表外物品被拒。
        intent = new ActionIntent(new ActionId("enhance.equipment"), -1,
            TargetItemId: listedSword.Value, AuxItemId: guardOre.Value);
        prep = EnhancementRules.Resolve(loadout, intent,
            new Dictionary<AssetKey, double>(ready)
            {
                [new AssetKey(listedSword)] = 1,
                [new AssetKey(guardOre)] = 1
            }, catalog, 0, out check, out _);
        Assert.NotNull(prep);
        Assert.Equal(guardOre, prep!.ProtectionItemId);

        intent = new ActionIntent(new ActionId("enhance.equipment"), -1,
            TargetItemId: listedSword.Value, AuxItemId: listedSword.Value);
        Assert.Null(EnhancementRules.Resolve(loadout, intent,
            new Dictionary<AssetKey, double>(ready)
            {
                [new AssetKey(listedSword)] = 2
            }, catalog, 0, out check, out _));
        Assert.Equal(EnhancementRules.Check.Blocked, check);
    }

    /// <summary>
    /// 聚灵阵盘为普通持续消耗品：入场自动消耗 1 件充入 300s（战斗时钟扣减），
    /// 不再走 durable 战外永驻注入；耗竭前槽位不写冷却。
    /// </summary>
    [Fact]
    public void FormationDiskChargesDurationOnBattleEntryWithoutDurableBuff()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("阵修");
        var disk = new ItemId("item.juling-formation-disk");
        // 配表判定：普通持续消耗品 300s 持续 + 300s 冷却，buff 双通道不变。
        var consumable = catalog.Items[disk].Consumable!;
        Assert.True(consumable.IsContinuous);
        Assert.False(consumable.Durable);
        Assert.Equal(300, consumable.DurationSeconds);
        Assert.Equal(300, consumable.CooldownSeconds);
        var wallet = CreateWallet(player, (new AssetKey(disk), 2));
        player.ConfigureAutoUse("combat", 0, disk.Value, 1, true, AutoUseSlotState.SlotGroupGeneral);
        player.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1), Baseline, catalog.Actions);

        // 非 durable：战斗行动推进中不再注入战外永驻 ActiveBuff。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(1), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.DoesNotContain(player.Buffs.Active, entry => entry.BuffId == "formation.juling");

        // 搜寻结束入场：自动消耗 1 件并充入 300s 剩余时长；耗竭前冷却为零。
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(40), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(1d, wallet.Get(new AssetKey(disk)));
        Assert.InRange(player.Settings.AutoUseSlots[0].RemainingDurationSeconds, 0, 300);
        Assert.Equal(0, player.Settings.AutoUseSlots[0].CooldownUntilUnixSeconds);
        Assert.DoesNotContain(player.Buffs.Active, entry => entry.BuffId == "formation.juling");
    }

    /// <summary>
    /// 雷击符：战斗中激活扣除自身灵力 5%，对当前主要敌人造成
    /// 0.2×灵力上限 + 2.0×法攻 的无掷点技能伤害，随后进入冷却。
    /// </summary>
    [Fact]
    public void StrikeTalismanConsumesManaAndDamagesPrimaryEnemy()
    {
        var strike = new ItemId("item.test-strike");
        var strikeJson = """
            {"id":"item.test-strike","name":"测试雷符","kind":"consumable","category":"talisman",
             "consumable":{"usableInActionTypes":["combat"],"cooldownSeconds":60,
             "strike":{"manaCostRatio":0.05,"manaDamageRatio":0.2,"spellAttackRatio":2.0,"element":"wood"}}}
            """;
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("combat.test"), "讨伐测试", ActionKind.Combat, 0,
                new Dictionary<ItemId, double>(), [],
                ExecutionKind: ActionExecutionKind.CombatEncounter, CombatEncounterId: "encounter.test")
        ]);
        var items = new Dictionary<ItemId, ItemDefinition>
        {
            [strike] = new(strike, "测试雷符", "", true, 0,
                new ConsumableDefinition(new HashSet<string> { "combat" },
                    Strike: new ConsumableStrike(0.05, 0.2, 2.0, CombatElement.Wood),
                    CooldownSeconds: 60),
                "talisman"),
        };
        var rawItems = new Dictionary<string, JsonElement> { [strike.Value] = Json(strikeJson) };
        var monster = new CombatMonsterDefinition("enemy.test", "测试妖兽", 1, 1, CombatMonsterRank.Minion,
            CombatantStats.Create(100_000, 0, 0, 2), [], []);
        var encounter = new CombatEncounterDefinition("encounter.test", "测试遭遇", 1, 1, 1, 1, 1, 180,
            [new CombatSpawnDefinition("enemy.test", 1, 1)], [], [], 0);
        var catalog = new GameDataCatalog("test-1", "test-fingerprint", actions,
            items: items, rawItems: rawItems,
            combatMonsters: new Dictionary<string, CombatMonsterDefinition> { [monster.Id] = monster },
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter });
        var player = CreatePlayer("符修");
        var wallet = CreateWallet(player, (new AssetKey(strike), 1));
        player.ConfigureAutoUse("combat", 0, strike.Value, 1, true, AutoUseSlotState.SlotGroupGeneral);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // 搜寻 1s 入战后，下一资源边界雷击符激活：扣 5% 灵力（5），消耗 1 件，伤害落敌快照。
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(6), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(0, wallet.Get(new AssetKey(strike)));
        Assert.Equal(95, player.Resources.Manapoints, 9);
        Assert.True(player.Settings.AutoUseSlots[0].CooldownUntilUnixSeconds > 0);
        var enemyState = player.Actions[0].Expedition?.Enemies?[0].State;
        Assert.NotNull(enemyState);
        // 兜底面板：MaxManapoints=100、SpellAttack=0 → 打击基数 0.2×100=20；
        // 妖兽法抗 0 → 平方穿甲 20²/(20+1) ≈ 19.0476；敌人另承受玩家普攻，HP 不得高于全额雷击后的水位。
        Assert.True(enemyState!.Hitpoints <= 100_000 - 400d / 21 + 1e-6,
            $"雷击全额应已入账，实际 HP={enemyState.Hitpoints}");
        Assert.Contains(result.Events, entry => entry.Code == "auto-use.activated");
    }

    /// <summary>雷击符灵力不足时不激活：不扣件、不进入冷却、敌人快照不动。</summary>
    [Fact]
    public void StrikeTalismanSkipsWhenManaInsufficient()
    {
        var strike = new ItemId("item.test-strike");
        var strikeJson = """
            {"id":"item.test-strike","name":"测试雷符","kind":"consumable","category":"talisman",
             "consumable":{"usableInActionTypes":["combat"],"cooldownSeconds":60,
             "strike":{"manaCostRatio":0.5,"manaDamageRatio":0.2,"spellAttackRatio":2.0,"element":"wood"}}}
            """;
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("combat.test"), "讨伐测试", ActionKind.Combat, 0,
                new Dictionary<ItemId, double>(), [],
                ExecutionKind: ActionExecutionKind.CombatEncounter, CombatEncounterId: "encounter.test")
        ]);
        var items = new Dictionary<ItemId, ItemDefinition>
        {
            [strike] = new(strike, "测试雷符", "", true, 0,
                new ConsumableDefinition(new HashSet<string> { "combat" },
                    Strike: new ConsumableStrike(0.5, 0.2, 2.0, CombatElement.Wood),
                    CooldownSeconds: 60),
                "talisman"),
        };
        var rawItems = new Dictionary<string, JsonElement> { [strike.Value] = Json(strikeJson) };
        var monster = new CombatMonsterDefinition("enemy.test", "测试妖兽", 1, 1, CombatMonsterRank.Minion,
            CombatantStats.Create(100_000, 0, 0, 2), [], []);
        var encounter = new CombatEncounterDefinition("encounter.test", "测试遭遇", 1, 1, 1, 1, 1, 180,
            [new CombatSpawnDefinition("enemy.test", 1, 1)], [], [], 0);
        var catalog = new GameDataCatalog("test-1", "test-fingerprint", actions,
            items: items, rawItems: rawItems,
            combatMonsters: new Dictionary<string, CombatMonsterDefinition> { [monster.Id] = monster },
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter });
        var player = CreatePlayer("符修");
        var wallet = CreateWallet(player, (new AssetKey(strike), 1));
        player.ConfigureAutoUse("combat", 0, strike.Value, 1, true, AutoUseSlotState.SlotGroupGeneral);
        player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

        // manaCostRatio 0.5 × 上限 100 = 50；灵力压到 10 → 灵力不足跳过激活。
        player.Resources.Manapoints = 10;
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(6), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(1, wallet.Get(new AssetKey(strike)));
        Assert.Equal(0, player.Settings.AutoUseSlots[0].CooldownUntilUnixSeconds);
        Assert.DoesNotContain(result.Events, entry => entry.Code == "auto-use.activated");
    }

    /// <summary>旧版装配文档（缺技艺经验字段）恢复安全：不抹除既有累计，等级按 0 起算。</summary>
    [Fact]
    public void LoadoutRestoreToleratesMissingTechniqueExperiences()
    {
        var loadout = new PlayerLoadoutState();
        var document = new PlayerLoadoutDocument(new PlayerCoreAttributes(), [], []);
        loadout.Restore(document);
        Assert.Empty(loadout.TechniqueExperiences);
        loadout.TechniqueExperiences[TechniqueCatalog.Alchemy] = 42;
        // 缺省文档恢复不抹除既有技艺累计（槽位装配替换路径共享同一语义）。
        loadout.Restore(document);
        Assert.Equal(42, loadout.TechniqueExperiences[TechniqueCatalog.Alchemy]);
        // 非法技艺键仍被拒绝。
        var invalid = document with
        {
            TechniqueExperiences = new Dictionary<string, double> { ["cooking"] = 1 }
        };
        Assert.Throws<DomainException>(() => loadout.Restore(invalid));
    }

    /// <summary>
    /// 技艺工具：装备 tool-mining 槽位后 mining 行动耗时按 基础/(1+0.1) 折减，
    /// 与技艺等级加成并入同一除数加算；不匹配技艺的行动与不匹配技艺的工具均不生效。
    /// </summary>
    [Fact]
    public void EquippedToolSpeedsMatchingTechniqueAction()
    {
        var compass = new ItemId("equipment.tool-prospect-compass");
        var hoe = new ItemId("equipment.tool-herb-hoe");
        var mining = new ActionDefinition(new ActionId("mine.test"), "挖矿测试", ActionKind.Exploration, 10,
            new Dictionary<ItemId, double>(), [], Technique: TechniqueCatalog.Mining);
        var alchemy = new ActionDefinition(new ActionId("alchemy.test"), "炼丹测试", ActionKind.Alchemy, 10,
            new Dictionary<ItemId, double>(), [], Technique: TechniqueCatalog.Alchemy);
        var catalog = CreateMinimalCatalog(actions: new ActionCatalog([mining, alchemy]),
            rawEquipments: new Dictionary<string, JsonElement>
            {
                [compass.Value] = Json("""{"id":"equipment.tool-prospect-compass","name":"探矿罗盘","slot":"tool-mining","tier":1,"techniqueBonus":{"technique":"mining","speedRatio":0.1}}"""),
                [hoe.Value] = Json("""{"id":"equipment.tool-herb-hoe","name":"药锄","slot":"tool-gathering","tier":1,"techniqueBonus":{"technique":"gathering","speedRatio":0.1}}""")
            },
            realmProgression: new RealmProgressionCatalog([100, 200, 300], [80]));
        var player = CreatePlayer("矿工");
        var wallet = CreateWallet(player);
        player.Loadout.Equipment.Add(new PlayerEquipmentSlot(compass.Value, 0));
        var miningAction = player.Enqueue(new ActionIntent(mining.Id, -1), Baseline, catalog.Actions);

        // 工具命中绑定技艺：10 / (1 + 0.1)；配表缺失时按无加成计。
        Assert.Equal(10 / 1.1, SimulationEngine.TimedActionDuration(player, miningAction, mining, catalog, wallet, null), 12);
        Assert.Equal(0.1, TechniqueCatalog.EquippedToolSpeedRatio(player.Loadout, TechniqueCatalog.Mining, catalog), 12);
        Assert.Equal(0, TechniqueCatalog.EquippedToolSpeedRatio(player.Loadout, TechniqueCatalog.Mining, null));

        // 技艺等级与工具加成同点加算：300 经验逐段至 2 级 → 10 / (1 + 0.1 + 0.1)。
        player.Loadout.TechniqueExperiences[TechniqueCatalog.Mining] = 300;
        Assert.Equal(10 / 1.2, SimulationEngine.TimedActionDuration(player, miningAction, mining, catalog, wallet, null), 12);

        // 不匹配技艺的行动（alchemy）不受 mining 工具加成；
        // 绑定其他技艺的工具（药锄→gathering）也不计入 mining 除数。
        var alchemyAction = player.Enqueue(new ActionIntent(alchemy.Id, -1), Baseline, catalog.Actions);
        Assert.Equal(10, SimulationEngine.TimedActionDuration(player, alchemyAction, alchemy, catalog, wallet, null));
        player.Loadout.Equipment.Add(new PlayerEquipmentSlot(hoe.Value, 0));
        Assert.Equal(10 / 1.2, SimulationEngine.TimedActionDuration(player, miningAction, mining, catalog, wallet, null), 12);

        // 工具槽位前缀判定。
        Assert.True(TechniqueCatalog.IsToolSlot("tool-mining"));
        Assert.False(TechniqueCatalog.IsToolSlot("weapon"));
        Assert.False(TechniqueCatalog.IsToolSlot(null));
    }

    /// <summary>技艺工具不可强化：tool- 槽位装备在强化准入直接 Blocked 并给出专属文案。</summary>
    [Fact]
    public void TechniqueToolIsRejectedByEnhancement()
    {
        var hoe = new ItemId("equipment.tool-herb-hoe");
        var catalog = CreateMinimalCatalog(rawEquipments: new Dictionary<string, JsonElement>
        {
            [hoe.Value] = Json("""{"id":"equipment.tool-herb-hoe","name":"药锄","slot":"tool-gathering","tier":1,"techniqueBonus":{"technique":"gathering","speedRatio":0.1}}""")
        });
        var intent = new ActionIntent(new ActionId("enhance.equipment"), -1, TargetItemId: hoe.Value);
        var holdings = new Dictionary<AssetKey, double>
        {
            [new AssetKey(hoe)] = 1,
            [new AssetKey(new ItemId(EnhancementRules.SpiritStoneItemId))] = 999
        };
        var prep = EnhancementRules.Resolve(new PlayerLoadoutState(), intent, holdings, catalog, 0,
            out var check, out var reason);
        Assert.Null(prep);
        Assert.Equal(EnhancementRules.Check.Blocked, check);
        Assert.Equal("技艺工具无法强化。", reason);
    }

    private static GameDataCatalog CreateMinimalCatalog(
        ActionCatalog? actions = null,
        Dictionary<string, JsonElement>? rawEquipments = null,
        RealmProgressionCatalog? realmProgression = null)
        => new("test-1", "test-fingerprint", actions ?? ActionCatalog.Default,
            items: new Dictionary<ItemId, ItemDefinition>(),
            rawEquipments: rawEquipments,
            realmProgression: realmProgression);
}
