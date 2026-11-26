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

    /// <summary>秘法物品规则：化灵按回收价三倍结算，装备分解按每种原料 70% 向下取整，残卷概率边界固定。</summary>
    [Fact]
    public void SecretArtRules_UsesConfiguredPricesAndFlooredMaterialReturns()
    {
        var catalog = GameDataCatalog.LoadEmbedded();

        Assert.Equal(6, SecretArtRules.ResolveRecycleUnitPrice(catalog, "item.beast-hide", 0));

        var returns = SecretArtRules.ComputeDisassemblyReturns(catalog, "equipment.qingfeng-sword");
        Assert.NotNull(returns);
        Assert.Equal(4, returns![new ItemId("item.refined-iron")]);
        Assert.False(returns.ContainsKey(new ItemId("item.beast-bone")));

        Assert.True(SecretArtRules.FragmentProducesTome(0));
        Assert.False(SecretArtRules.FragmentProducesTome(SecretArtRules.FragmentTomeChance));
        // 残卷推演失败按残卷自身 sellPrice × 2 返还灵石保底。
        var expectedReward = SecretArtRules.ResolveRecycleUnitPrice(catalog, "item.fragment.heavy-slash", 0) * 2.0;
        Assert.Equal(20d, expectedReward);
        Assert.Equal(expectedReward, SecretArtRules.ResolveFragmentFailureReward(catalog, "item.fragment.heavy-slash"));
        Assert.Equal(0d, SecretArtRules.ResolveFragmentFailureReward(catalog, "item.nonexistent-fragment"));
        Assert.Equal(string.Empty, EnhancementRules.VariantForLevel(0));
        Assert.Equal("+3", EnhancementRules.VariantForLevel(3));
    }

    /// <summary>
    /// 秘法感悟经验按目标物品自身显式等级结算（ResolveItemLevel：直读显式 level，缺省按 1 级）。
    /// beast-hide/beast-bone 同为 T1 但显式等级 1/3：若只按等阶换算，两者经验完全相同，
    /// 高等级物品失去升级价值（玩家反馈「1级和3级经验一样」的回归门禁）。
    /// </summary>
    [Fact]
    public void SecretArtYieldExperienceScalesWithItemLevel()
    {
        var catalog = GameDataCatalog.LoadEmbedded();

        var (techLv1, senseLv1) = SecretArtRules.ComputeYieldExperience(catalog, "item.beast-hide", 5);
        var (techLv3, senseLv3) = SecretArtRules.ComputeYieldExperience(catalog, "item.beast-bone", 5);

        // 1 级基准经验 10：技艺与伴生底蕴 = round(10 × 5/10 × 2) = 10；神识维持原 2× 基准 = round(10 × 5/10) × 2 = 10。
        Assert.Equal(10d, techLv1);
        Assert.Equal(10d, senseLv1);
        // 3 级基准经验 12.15：技艺 = round(12.15 × 0.5 × 2) = 12；神识维持原倍率 = round(12.15 × 0.5) × 2 = 12。
        Assert.Equal(12d, techLv3);
        Assert.Equal(12d, senseLv3);
    }

    /// <summary>化灵单轮基础耗时按物品等级线性增长：5 + 0.3×(等级−1)，1 级仍为 5 秒。</summary>
    [Fact]
    public void LiquefyDurationSeconds_ScalesWithItemLevel()
    {
        Assert.Equal(5d, SecretArtRules.LiquefyDurationSeconds(1));
        Assert.Equal(5d, SecretArtRules.LiquefyDurationSeconds(0));
        Assert.Equal(5d, SecretArtRules.LiquefyDurationSeconds(-3));
        Assert.Equal(5.3, SecretArtRules.LiquefyDurationSeconds(2), 9);
        Assert.Equal(8.0, SecretArtRules.LiquefyDurationSeconds(11), 9);
        Assert.Equal(8.3, SecretArtRules.LiquefyDurationSeconds(12), 9);
    }

    /// <summary>化灵成功率：秘法等级不低于物品等级恒 100%；每低 1 级 −5%，下限 20%。</summary>
    [Fact]
    public void LiquefySuccessChance_PenalizesDeficitWithFloor()
    {
        Assert.Equal(1d, SecretArtRules.LiquefySuccessChance(3, 3));
        Assert.Equal(1d, SecretArtRules.LiquefySuccessChance(10, 3));
        Assert.Equal(1d, SecretArtRules.LiquefySuccessChance(0, 0));
        Assert.Equal(0.95, SecretArtRules.LiquefySuccessChance(0, 1), 9);
        Assert.Equal(0.75, SecretArtRules.LiquefySuccessChance(5, 10), 9);
        Assert.Equal(0.2, SecretArtRules.LiquefySuccessChance(0, 21), 9);
        Assert.Equal(0.2, SecretArtRules.LiquefySuccessChance(0, 30), 9);
        // 技艺等级小数取下整后再比较（floor(2.9)=2 → deficit=2）
        Assert.Equal(0.9, SecretArtRules.LiquefySuccessChance(2.9, 4), 9);

        // 确定性判定：同实例同轮次恒同值；chance 0 必败、1 必胜、非法值拒绝
        var actionId = Guid.NewGuid();
        Assert.Equal(SecretArtRules.LiquefySucceeds(actionId, 1, 0.5),
            SecretArtRules.LiquefySucceeds(actionId, 1, 0.5));
        Assert.False(SecretArtRules.LiquefySucceeds(actionId, 1, 0));
        Assert.True(SecretArtRules.LiquefySucceeds(actionId, 1, 1));
        Assert.Throws<DomainException>(() => SecretArtRules.LiquefySucceeds(actionId, 1, 1.5));
        Assert.Throws<DomainException>(() => SecretArtRules.LiquefySucceeds(actionId, 1, double.NaN));
    }

    /// <summary>技艺经验逐段推进：不足一段停在当前级，曲线封顶后不再升。</summary>
    [Fact]
    public void TechniqueLevelAdvancesSegmentBySegment()
    {
        var curve = new RealmProgressionCatalog([100, 200, 300], [80]);
        // 削减后三段为 5 / 17 / 35：累计 5 升 1 级、22 升 2 级、57 升 3 级（封顶）。
        Assert.Equal(0, TechniqueCatalog.LevelFor(0, curve));
        Assert.Equal(0, TechniqueCatalog.LevelFor(4.999, curve));
        Assert.Equal(1, TechniqueCatalog.LevelFor(5, curve));
        Assert.Equal(1, TechniqueCatalog.LevelFor(21.999, curve));
        Assert.Equal(2, TechniqueCatalog.LevelFor(22, curve));
        Assert.Equal(3, TechniqueCatalog.LevelFor(57, curve));
        // 曲线封顶后经验继续累计但等级不再推进；无效输入按 0 级计。
        Assert.Equal(3, TechniqueCatalog.LevelFor(9999, curve));
        Assert.Equal(0, TechniqueCatalog.LevelFor(-5, curve));
        Assert.Equal(0, TechniqueCatalog.LevelFor(double.NaN, curve));
        Assert.Equal(0, TechniqueCatalog.LevelFor(10, null));
        foreach (var id in new[] { "alchemy", "forging", "talisman", "enhancement", "formation", "gathering", "mining" })
            Assert.True(TechniqueCatalog.IsKnown(id));
        Assert.False(TechniqueCatalog.IsKnown("cooking"));
        Assert.False(TechniqueCatalog.IsKnown(null));
    }

    /// <summary>
    /// 技艺前期削减：升入 1 级 -95%，逐级线性收敛至升入 30 级 -0%，
    /// 30 级以后的段值与修为曲线完全一致（修为曲线本身不被削减）。
    /// </summary>
    [Fact]
    public void TechniqueSegmentExpLinearlyDiscountsFirstThirtyLevels()
    {
        var curve = new RealmProgressionCatalog([100, 200, 300], [80]);
        // 0→1 级 -95%（100→5）、1→2 级 -91.7%（200→17）、2→3 级 -88.4%（300→35）。
        Assert.Equal(5, TechniqueCatalog.ExpToNextFor(0, curve));
        Assert.Equal(17, TechniqueCatalog.ExpToNextFor(1, curve));
        Assert.Equal(35, TechniqueCatalog.ExpToNextFor(2, curve));
        Assert.Equal(0, TechniqueCatalog.ExpToNextFor(3, curve));
        Assert.Equal(0, TechniqueCatalog.ExpToNextFor(-1, curve));
        Assert.Equal(0, TechniqueCatalog.ExpToNextFor(0, null));

        Assert.Equal(0.95, TechniqueCatalog.EarlyDiscountRatio(1), 12);
        Assert.Equal(0.475, TechniqueCatalog.EarlyDiscountRatio(15.5), 12);
        Assert.Equal(0, TechniqueCatalog.EarlyDiscountRatio(30), 12);
        Assert.Equal(0, TechniqueCatalog.EarlyDiscountRatio(31), 12);
        Assert.Equal(0, TechniqueCatalog.EarlyDiscountRatio(0), 12);
        for (var level = 2; level <= 30; level++)
            Assert.True(TechniqueCatalog.EarlyDiscountRatio(level) < TechniqueCatalog.EarlyDiscountRatio(level - 1));

        // 31 段等值曲线：前 29 段被削减、第 30/31 段与修为曲线一致（30 级起削减归零）。
        var longCurve = new RealmProgressionCatalog(Enumerable.Repeat(1000d, 31).ToArray(), [80]);
        Assert.Equal(50, TechniqueCatalog.ExpToNextFor(0, longCurve));
        Assert.Equal(1000, TechniqueCatalog.ExpToNextFor(29, longCurve));
        Assert.Equal(1000, TechniqueCatalog.ExpToNextFor(30, longCurve));
        Assert.Equal(1000, longCurve.ExpCapFor(31));
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

        // 0 级：无折减；技艺经验 300 按削减后曲线推进到 3 级 → 10 / (1 + 0.05×3)。
        Assert.Equal(10, SimulationEngine.TimedActionDuration(player, action, alchemy, catalog, wallet, null));
        player.Loadout.TechniqueExperiences[TechniqueCatalog.Alchemy] = 300;
        var level = TechniqueCatalog.LevelFor(300, curve);
        Assert.Equal(10 / (1 + TechniqueCatalog.SpeedBonusRatio(level)),
            SimulationEngine.TimedActionDuration(player, action, alchemy, catalog, wallet, null), 12);
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
        // 1 级 5 秒采集：技艺经验 = round(10 × 0.5 × 2) = 10，同时伴生等额底蕴 10。
        Assert.Equal(10, player.Loadout.TechniqueExperiences[TechniqueCatalog.Gathering]);
        Assert.Equal(10, player.Progression.Foundation);
        Assert.Contains(player.Duel.CurrentReport?.TechniqueExp ?? [],
            entry => entry.TechniqueId == TechniqueCatalog.Gathering && entry.Experience == 10);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet)
            .AssetDeltas.ToList().ForEach(wallet.Apply);
        Assert.Equal(20, player.Loadout.TechniqueExperiences[TechniqueCatalog.Gathering]);
        Assert.Equal(20, player.Progression.Foundation);
        // 新曲线下 0→1 级仅需 5，10 经验已到 1 级（不足 5 时停在 0 级）。
        Assert.Equal(0, TechniqueCatalog.LevelFor(4.999, curve));
        Assert.Equal(1, TechniqueCatalog.LevelFor(10, curve));
    }

    /// <summary>
    /// 高等阶非战斗行动（如 18 级 T2 挖矿赤铜矿）按等级基准经验 × 2 全局倍率获得技艺经验（round(133.417 × 0.8 × 2) = 213 点），
    /// 1 级行动（如乌铁矿 6 秒）获得 round(10 × 0.6 × 2) = 12 点；两者均同步发放等额底蕴。
    /// </summary>
    [Fact]
    public void HighLevelActionGrantsScaledTechniqueExperience()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var crimsonCopper = catalog.Actions.Get(new ActionId("explore.mine-crimson-copper"));
        Assert.Equal(18, crimsonCopper.Level);

        var player = CreatePlayer("矿工-赤铜");
        player.World.NodeId = "node.fluorite-abyss";
        player.Enqueue(new ActionIntent(crimsonCopper.Id, 1), Baseline, catalog.Actions);
        var wallet = CreateWallet(player);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(crimsonCopper.DurationSeconds), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);

        // 18 级 8 秒行动单轮技艺经验 = round(133.417 × 0.8 × 2) = 213
        Assert.Equal(213, player.Loadout.TechniqueExperiences[TechniqueCatalog.Mining]);
        Assert.Equal(213, player.Progression.Foundation);
        var report = player.Duel.CurrentReport ?? player.Duel.Reports.FirstOrDefault();
        Assert.Contains(report?.TechniqueExp ?? [],
            entry => entry.TechniqueId == TechniqueCatalog.Mining && entry.Experience == 213);
        Assert.Equal(213, report?.Foundation);

        // 对比 1 级行动：挖掘乌铁矿（6 秒）获得 12 点技艺经验与 12 点底蕴
        var darkIron = catalog.Actions.Get(new ActionId("explore.mine-dark-iron"));
        Assert.Equal(1, darkIron.Level);
        var player1 = CreatePlayer("矿工-乌铁");
        player1.World.NodeId = "node.rock-beach";
        player1.Enqueue(new ActionIntent(darkIron.Id, 1), Baseline, catalog.Actions);
        var wallet1 = CreateWallet(player1);
        var result1 = SimulationEngine.AdvanceTo(player1, Baseline.AddSeconds(darkIron.DurationSeconds), catalog, wallet1);
        result1.AssetDeltas.ToList().ForEach(wallet1.Apply);
        Assert.Equal(12, player1.Loadout.TechniqueExperiences[TechniqueCatalog.Mining]);
        Assert.Equal(12, player1.Progression.Foundation);
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
        // t=11..20 基础成功率统一为 30%（0.30）。
        Assert.Equal(0.3, EnhancementRules.BaseSuccessRate(20), 12);
        // 技艺修正：L≥t 线性乘算 ×(1+0.01×L)；L<t 乘性衰减 ×0.9^(t-L)；终值 clamp [0, 0.99]。
        Assert.Equal(0.99, EnhancementRules.SuccessRate(1, 127), 12);
        Assert.Equal(0.5 * Math.Pow(0.9, 1), EnhancementRules.SuccessRate(1, 0), 12);
        Assert.Equal(0.4 * (1.0 + 0.01 * 5), EnhancementRules.SuccessRate(5, 5), 12);

        // 判定种子取行动实例 Id 前 8 字节、通道独立：同实例同轮必得同结果，异实例结果流独立。
        var prep = new EnhancementRules.Preparation(sword, 0, 1, 10, null, 0.5);
        var instanceA = Guid.NewGuid();
        var instanceB = Guid.NewGuid();
        Assert.Equal(EnhancementRules.Roll(instanceA, 0, prep), EnhancementRules.Roll(instanceA, 0, prep));
        Assert.Equal(1, EnhancementRules.ResultLevel(prep, EnhancementRules.Outcome.Success));
        Assert.Equal(0, EnhancementRules.ResultLevel(prep, EnhancementRules.Outcome.FailReset));
        Assert.Equal(0, EnhancementRules.ResultLevel(prep, EnhancementRules.Outcome.FailProtected));
        var prepProtected = prep with { ProtectionItemId = new ItemId("item.test-mat"), CurrentLevel = 4, TargetLevel = 5 };
        // 保护生效：较当前等级回落一级（+4 → +3），非保级。
        Assert.Equal(3, EnhancementRules.ResultLevel(prepProtected, EnhancementRules.Outcome.FailProtected));
        Assert.Equal(EnhancementRules.Roll(instanceA, 3, prep), EnhancementRules.Roll(instanceA, 3, prep));
        _ = EnhancementRules.Roll(instanceB, 0, prep); // 不同实例走同通道独立判定，不共享随机游标。
    }

    /// <summary>强化当前等级：仅读取背包内未穿戴件（扣除已装备数量后持有 ≥1）的最高变体；已穿戴件不进入判定，无可用件返回 -1。</summary>
    [Fact]
    public void EnhancementCurrentLevelOnlyReadsAvailableInventoryExcludingEquipped()
    {
        var loadout = new PlayerLoadoutState();
        var sword = new ItemId("equipment.test-sword");
        Assert.Equal(-1, EnhancementRules.CurrentLevel(loadout, sword, new Dictionary<AssetKey, double>()));

        // 背包持有 +3 且未穿戴：当前等级为 3
        var holdings = new Dictionary<AssetKey, double> { [new AssetKey(sword, "+3")] = 1 };
        Assert.Equal(3, EnhancementRules.CurrentLevel(loadout, sword, holdings));

        // 穿戴了这件 +3（holdings=1, equipped=1）：背包内可用为 0，不可强化，返回 -1
        loadout.Equipment.Add(new PlayerEquipmentSlot(sword.Value, 3));
        Assert.Equal(-1, EnhancementRules.CurrentLevel(loadout, sword, holdings));

        // 持有 2 件 +3，穿戴 1 件：背包内仍有 1 件未穿戴的 +3，返回 3
        holdings[new AssetKey(sword, "+3")] = 2;
        Assert.Equal(3, EnhancementRules.CurrentLevel(loadout, sword, holdings));

        // 穿戴了 +5（holdings 没有 +5 只有 +2）：已穿戴的不作为强化目标，只看背包内的 +2，返回 2
        loadout.Equipment[0] = new PlayerEquipmentSlot(sword.Value, 5);
        holdings.Clear();
        holdings[new AssetKey(sword, "+2")] = 1;
        Assert.Equal(2, EnhancementRules.CurrentLevel(loadout, sword, holdings));
    }

    [Fact]
    public void EnhancementRulesDistinguishMultipleVariantsWhenCoexisting()
    {
        var loadout = new PlayerLoadoutState();
        var sword = new ItemId("equipment.test-sword");
        // 背包同时持有一把 +20 和一把 +0（无变体）
        var holdings = new Dictionary<AssetKey, double>
        {
            [new AssetKey(sword, "+20")] = 1,
            [new AssetKey(sword)] = 1,
        };

        // 当 targetCap=20 时，+20 已达标被排除，未达标的 +0 被正确选出作为强化目标
        Assert.Equal(0, EnhancementRules.CurrentLevel(loadout, sword, holdings, targetCap: 20));
        // 指定 preferredLevel=0，精准选中 +0
        Assert.Equal(0, EnhancementRules.CurrentLevel(loadout, sword, holdings, targetCap: 20, preferredLevel: 0));

        // 若 targetCap=10，+20 同样被排除，返回 0
        Assert.Equal(0, EnhancementRules.CurrentLevel(loadout, sword, holdings, targetCap: 10));

        // 若持存中只有 +20，targetCap=20 时无未达标件，返回 -1
        var only20 = new Dictionary<AssetKey, double> { [new AssetKey(sword, "+20")] = 1 };
        Assert.Equal(-1, EnhancementRules.CurrentLevel(loadout, sword, only20, targetCap: 20));

        // 测试 Resolve 链路：同时持有 +20 与 +0 时，意图指明 targetLevel=20，能正常开跑且当前等级为 0
        var rawEquipments = new Dictionary<string, JsonElement>
        {
            [sword.Value] = Json("""{"id":"equipment.test-sword","name":"测试剑","flatStats":{"physicalAttack":10}}""")
        };
        var catalog = CreateMinimalCatalog(rawEquipments: rawEquipments);
        holdings[new AssetKey(new ItemId(EnhancementRules.SpiritStoneItemId))] = 1000;

        var intent = new ActionIntent(new ActionId("enhance.equipment"), 1,
            Parameters: new Dictionary<string, double>
            {
                [EnhancementRules.TargetLevelParameter] = 20,
                [EnhancementRules.InitialLevelParameter] = 0,
            }) with { TargetItemId = sword.Value };

        var prep = EnhancementRules.Resolve(loadout, intent, holdings, catalog, 0, out var check, out var reason);
        Assert.NotNull(prep);
        Assert.Equal(EnhancementRules.Check.Ready, check);
        Assert.Equal(0, prep.CurrentLevel);
        Assert.Equal(1, prep.TargetLevel);
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
        // 强化资产守恒：目标装备总持有恒为 1，落在基础键或 +1 变体键上；技艺经验全局 2× = 12 并伴生等额底蕴。
        var swordHeld = wallet.Get(new AssetKey(sword)) + wallet.Get(new AssetKey(sword, "+1"));
        Assert.Equal(1, swordHeld);
        Assert.Equal(12, player.Loadout.TechniqueExperiences[TechniqueCatalog.Enhancement]);
        Assert.Equal(12, player.Progression.Foundation);
        Assert.Contains(result.Events, entry => entry.Code is "enhance.succeeded" or "enhance.failed");
    }

    /// <summary>
    /// 强化经验直读目标装备自身显式 level，不再按 tier 换算等级（总纲 §20.1.4）：
    /// 一件 tier=2 但显式 level=16 的装备，+0→+1（基础耗时 6s）神识按原 1× 定额结算，
    /// 技艺经验与等额底蕴按全局 2× 倍率结算。
    /// </summary>
    [Fact]
    public void EnhancementUsesExplicitEquipmentLevelInsteadOfTierConversion()
    {
        var embedded = GameDataCatalog.LoadEmbedded();
        var armor = new ItemId("equipment.lv16-t2-armor");
        var enhance = new ActionDefinition(new ActionId("enhance.equipment"), "装备强化",
            ActionKind.Enhancement, 0, new Dictionary<ItemId, double>(), [], Technique: TechniqueCatalog.Enhancement);
        var rawEquipments = new Dictionary<string, JsonElement>
        {
            [armor.Value] = Json("""{"id":"equipment.lv16-t2-armor","name":"玄铜宝甲","tier":2,"level":16,"flatStats":{"physicalDefense":20}}""")
        };
        var catalog = CreateMinimalCatalog(
            actions: new ActionCatalog([enhance]),
            rawEquipments: rawEquipments,
            realmProgression: embedded.RealmProgression);
        var player = CreatePlayer("显式等级强化者");
        var stone = new ItemId(EnhancementRules.SpiritStoneItemId);
        var wallet = CreateWallet(player,
            (new AssetKey(armor), 1), (new AssetKey(stone), 5000));
        player.Enqueue(new ActionIntent(enhance.Id, -1, TargetItemId: armor.Value), Baseline, catalog.Actions);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(6.2), catalog, wallet);
        Assert.Contains(result.Events, entry => entry.Code is "enhance.succeeded" or "enhance.failed");

        var expectedConsciousness = Math.Round(embedded.RealmProgression!.BaseExperienceForLevel(16) * 0.6);
        var expectedTechExp = Math.Round(embedded.RealmProgression.BaseExperienceForLevel(16) * 0.6 * TechniqueCatalog.GlobalExperienceMultiplier);
        var tier18TechExp = Math.Round(embedded.RealmProgression.BaseExperienceForLevel(18) * 0.6 * TechniqueCatalog.GlobalExperienceMultiplier);
        Assert.NotEqual(tier18TechExp, expectedTechExp);
        Assert.Equal(expectedTechExp, player.Loadout.TechniqueExperiences[TechniqueCatalog.Enhancement]);
        Assert.Equal(expectedTechExp, player.Progression.Foundation);
        Assert.Equal(expectedConsciousness, player.Loadout.CoreAttributes.ConsciousnessExp);
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
    /// <summary>装备中不可被强化，且强化背包内装备不会同步或修改已装备槽位的强化等级。</summary>
    [Fact]
    public void EnhancementDoesNotAffectOrTargetEquippedItems()
    {
        var sword = new ItemId("equipment.test-sword");
        var enhance = new ActionDefinition(new ActionId("enhance.equipment"), "装备强化",
            ActionKind.Enhancement, 0, new Dictionary<ItemId, double>(), [], Technique: TechniqueCatalog.Enhancement);
        var rawEquipments = new Dictionary<string, JsonElement>
        {
            [sword.Value] = Json("""{"id":"equipment.test-sword","name":"测试剑"}""")
        };
        var catalog = CreateMinimalCatalog(actions: new ActionCatalog([enhance]),
            rawEquipments: rawEquipments,
            realmProgression: new RealmProgressionCatalog([100, 200, 300], [80]));
        var player = CreatePlayer("穿戴者");
        var stone = new ItemId(EnhancementRules.SpiritStoneItemId);

        // 场景 1：身上穿戴了 1 件 +0 测试剑，钱包总共只有 1 件 +0 测试剑（未穿戴数量为 0）
        player.Loadout.Equipment.Add(new PlayerEquipmentSlot(sword.Value, 0));
        var wallet = CreateWallet(player,
            (new AssetKey(sword), 1), (new AssetKey(stone), 500));
        player.Enqueue(new ActionIntent(enhance.Id, -1, TargetItemId: sword.Value), Baseline, catalog.Actions);

        // 背包内无可用件，行动直接阻塞
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet);
        Assert.Equal(ActionQueueStatus.Blocked, player.Actions[0].Status);
        Assert.Contains(result.Events, entry => entry.Code == "action.blocked");
        Assert.Equal(0, player.Loadout.Equipment[0].EnhancementLevel);

        // 场景 2：背包额外有 1 件未穿戴的 +0 测试剑（钱包总共 2 件，穿戴 1 件，背包可用 1 件）
        wallet.Credit(new AssetKey(sword), 1, "test");
        player.Actions[0].Status = ActionQueueStatus.Running;
        player.Loadout.Equipment[0] = new PlayerEquipmentSlot(sword.Value, 3); // 身上穿戴 +3 测试剑

        // 推进一轮强化：针对背包内的 +0 测试剑推进（目标等级 1）
        result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(16.2), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);

        // 身上穿戴的 +3 测试剑绝对不受影响，依然为 3
        Assert.Equal(3, player.Loadout.Equipment[0].EnhancementLevel);
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
    /// 起护等级判定：当前等级未达起护线时即使意图携带保护材料也不消耗且失败归零；
    /// 达到起护线后才检查并消耗保护材料，失败较当前等级回落一级（保护生效掉一级）；
    /// 起护等级在参数中最低 clamp 为 2。
    /// </summary>
    [Fact]
    public void EnhancementProtectFromLevelControlsMaterialConsumptionAndOutcome()
    {
        var sword = new ItemId("equipment.test-sword");
        var guardOre = new ItemId("item.guard-ore");
        var stone = new ItemId(EnhancementRules.SpiritStoneItemId);
        var catalog = CreateMinimalCatalog(rawEquipments: new Dictionary<string, JsonElement>
        {
            [sword.Value] = Json("""{"id":"equipment.test-sword","name":"测试剑","enhanceProtectionItems":["item.guard-ore"]}""")
        });
        var loadout = new PlayerLoadoutState();

        // 意图设定：从 +3 开始保护（protectLevel=3）
        var intent = new ActionIntent(new ActionId("enhance.equipment"), -1,
            TargetItemId: sword.Value, AuxItemId: guardOre.Value,
            Parameters: new Dictionary<string, double>
            {
                [EnhancementRules.TargetLevelParameter] = 5,
                [EnhancementRules.ProtectFromLevelParameter] = 3
            });

        // 场景 1：当前装备为 +1（未达起护等级 3）
        // 即使没有保护材料（guardOre 数量为 0），准入依然通过，Preparation.ProtectionItemId 恒为 null
        var holdings = new Dictionary<AssetKey, double>
        {
            [new AssetKey(sword, "+1")] = 1,
            [new AssetKey(stone)] = 500
        };
        var prep = EnhancementRules.Resolve(loadout, intent, holdings, catalog, 0, out var check, out _, currentTrackedLevel: 1);
        Assert.NotNull(prep);
        Assert.Equal(EnhancementRules.Check.Ready, check);
        Assert.Null(prep!.ProtectionItemId); // 未达线，不启用保护

        // 场景 2：当前装备达到 +3（已达起护等级 3）
        // 缺乏保护材料时受阻 Blocked
        holdings.Clear();
        holdings[new AssetKey(sword, "+3")] = 1;
        holdings[new AssetKey(stone)] = 500;
        Assert.Null(EnhancementRules.Resolve(loadout, intent, holdings, catalog, 0, out check, out _, currentTrackedLevel: 3));
        Assert.Equal(EnhancementRules.Check.Blocked, check);

        // 补充保护材料后通过，Preparation.ProtectionItemId 激活
        holdings[new AssetKey(guardOre)] = 2;
        prep = EnhancementRules.Resolve(loadout, intent, holdings, catalog, 0, out check, out _, currentTrackedLevel: 3);
        Assert.NotNull(prep);
        Assert.Equal(EnhancementRules.Check.Ready, check);
        Assert.Equal(guardOre, prep!.ProtectionItemId);

        // 场景 3：参数传入 protectLevel=1 时，服务端强制钳制为最低 2
        var intentLow = intent with
        {
            Parameters = new Dictionary<string, double>
            {
                [EnhancementRules.TargetLevelParameter] = 5,
                [EnhancementRules.ProtectFromLevelParameter] = 1 // 试图设定为 1
            }
        };
        holdings.Clear();
        holdings[new AssetKey(sword, "+1")] = 1;
        holdings[new AssetKey(stone)] = 500;
        // +1 件未达最低阈值 2，因此仍不消耗保护
        prep = EnhancementRules.Resolve(loadout, intentLow, holdings, catalog, 0, out check, out _, currentTrackedLevel: 1);
        Assert.NotNull(prep);
        Assert.Null(prep!.ProtectionItemId);
    }

    /// <summary>
    /// 多件同名装备连续冲阶（单品锁定验证）：玩家背包持有多件同名基础装备（5 件 +0 铁剑），
    /// 设定强化目标为 +3，执行多轮强化；系统必须锁定同一件装备持续强化，绝对不得轮番把多件装备都冲到 +1；
    /// 目标达成后行动出队，背包中其余 4 件 +0 原封不动。
    /// </summary>
    [Fact]
    public void EnhancementLocksOntoSingleItemWithoutCyclingThroughBagDuplicates()
    {
        var sword = new ItemId("equipment.test-sword");
        var enhance = new ActionDefinition(new ActionId("enhance.equipment"), "装备强化",
            ActionKind.Enhancement, 0, new Dictionary<ItemId, double>(), [], Technique: TechniqueCatalog.Enhancement);
        var rawEquipments = new Dictionary<string, JsonElement>
        {
            [sword.Value] = Json("""{"id":"equipment.test-sword","name":"测试剑","tier":1}""")
        };
        var catalog = CreateMinimalCatalog(actions: new ActionCatalog([enhance]),
            rawEquipments: rawEquipments,
            realmProgression: new RealmProgressionCatalog([100, 200, 300], [80]));
        var player = CreatePlayer("专一强化者");
        var stone = new ItemId(EnhancementRules.SpiritStoneItemId);

        // 背包持有 5 件 +0 测试剑与充裕灵石
        var wallet = CreateWallet(player,
            (new AssetKey(sword), 5), (new AssetKey(stone), 5000));

        // 目标为 +3，无限循环直到目标达成自动出队
        player.Enqueue(new ActionIntent(enhance.Id, -1,
            TargetItemId: sword.Value,
            Parameters: new Dictionary<string, double>
            {
                [EnhancementRules.TargetLevelParameter] = 3,
                [EnhancementRules.InitialLevelParameter] = 0
            }), Baseline, catalog.Actions);

        // 模拟推进多轮（由于判定概率，推进足够长的时间直到达到 +3 出队或完成）
        var now = Baseline;
        for (var step = 0; step < 300; step++)
        {
            if (player.Actions.Count == 0) break;
            now = now.AddSeconds(10);
            var result = SimulationEngine.AdvanceTo(player, now, catalog, wallet);
            result.AssetDeltas.ToList().ForEach(wallet.Apply);
        }

        // 核心不变量断言：
        // 1. 其余 4 件 +0 测试剑绝对原封不动！绝不能发生“多件都被强化成 +1”的缺陷！
        // 2. 目标件已达到 +3，行动圆满完成出队（队列已无行动）
        Assert.Empty(player.Actions);
        Assert.Equal(4, wallet.Get(new AssetKey(sword)));
        Assert.Equal(1, wallet.Get(new AssetKey(sword, "+3")));
        Assert.Equal(0, wallet.Get(new AssetKey(sword, "+1")));
        Assert.Equal(0, wallet.Get(new AssetKey(sword, "+2")));
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
        // 配表判定：普通持续消耗品 300s 持续且无冷却（0s CD），buff 双通道不变。
        var consumable = catalog.Items[disk].Consumable!;
        Assert.True(consumable.IsContinuous);
        Assert.False(consumable.Durable);
        Assert.Equal(300, consumable.DurationSeconds);
        Assert.Equal(0, consumable.CooldownSeconds);
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

    /// <summary>
    /// 雷击符打击不吃技能增幅（总纲 §7.7.1 技能增幅作用域、§20.2 打击公式）：
    /// 物品打击非玩家自身技能，穿戴 100 点技能增幅装备与裸身相比，敌人承伤必须完全一致。
    /// 修复前技能增幅误入原始威力乘位（20→40），穿甲后差异约 19 点，本对照必现。
    /// </summary>
    [Fact]
    public void StrikeTalismanIgnoresSkillBonusPoints()
    {
        double RunEnemyHitpoints(bool withSkillBonusCharm)
        {
            var strike = new ItemId("item.test-strike");
            var charm = new ItemId("equipment.test-charm");
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
            var rawEquipments = new Dictionary<string, JsonElement>
            {
                [charm.Value] = Json("""{"id":"equipment.test-charm","name":"测试法符","slot":"accessory","flatStats":{"skillBonusPoints":100}}""")
            };
            var monster = new CombatMonsterDefinition("enemy.test", "测试妖兽", 1, 1, CombatMonsterRank.Minion,
                CombatantStats.Create(100_000, 0, 0, 2), [], []);
            var encounter = new CombatEncounterDefinition("encounter.test", "测试遭遇", 1, 1, 1, 1, 1, 180,
                [new CombatSpawnDefinition("enemy.test", 1, 1)], [], [], 0);
            // 锚点表必须存在：缺失时面板走兜底分支，装备 flatStats 完全不参与解析，对照形同虚设。
            var catalog = new GameDataCatalog("test-1", "test-fingerprint", actions,
                items: items, rawItems: rawItems, rawEquipments: rawEquipments,
                combatMonsters: new Dictionary<string, CombatMonsterDefinition> { [monster.Id] = monster },
                combatEncounters: new Dictionary<string, CombatEncounterDefinition> { [encounter.Id] = encounter },
                baseAttributes: new BaseAttributeCatalog(
                    [new BaseAttributeAnchor(1, 1000, 100, 10, 0, 0, 0, 100, 0, 0, 0, 0, 0, 2, 1)]));
            var player = CreatePlayer("符修");
            var wallet = CreateWallet(player, (new AssetKey(strike), 1));
            if (withSkillBonusCharm)
                player.Loadout.EquippedItemIds.Add(charm);
            player.ConfigureAutoUse("combat", 0, strike.Value, 1, true, AutoUseSlotState.SlotGroupGeneral);
            player.Enqueue(new ActionIntent(new ActionId("combat.test"), -1), Baseline, catalog.Actions);

            var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(6), catalog, wallet);
            result.AssetDeltas.ToList().ForEach(wallet.Apply);
            // 雷击符必须真实激活扣件（否则对照形同虚设）。
            Assert.Equal(0, wallet.Get(new AssetKey(strike)));
            Assert.Contains(result.Events, entry => entry.Code == "auto-use.activated");
            if (withSkillBonusCharm)
                Assert.Equal(100, PlayerAttributeResolver.Resolve(player, catalog).SkillBonusPoints, 9);
            var enemyState = player.Actions[0].Expedition?.Enemies?[0].State;
            Assert.NotNull(enemyState);
            return enemyState!.Hitpoints;
        }

        var withoutBonus = RunEnemyHitpoints(false);
        var withBonus = RunEnemyHitpoints(true);
        Assert.Equal(withoutBonus, withBonus, 9);
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
                [compass.Value] = Json("""{"id":"equipment.tool-prospect-compass","name":"赤铜镐","slot":"tool-mining","tier":1,"techniqueBonus":{"technique":"mining","speedRatio":0.1}}"""),
                [hoe.Value] = Json("""{"id":"equipment.tool-herb-hoe","name":"桃木铲","slot":"tool-gathering","tier":1,"techniqueBonus":{"technique":"gathering","speedRatio":0.1}}""")
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

        // 技艺等级与工具加成同点加算：10 / (1 + 0.05×L + 0.1)。
        player.Loadout.TechniqueExperiences[TechniqueCatalog.Mining] = 300;
        var miningLevel = TechniqueCatalog.LevelFor(300, catalog.RealmProgression);
        Assert.Equal(10 / (1 + 0.1 + TechniqueCatalog.SpeedBonusRatio(miningLevel)),
            SimulationEngine.TimedActionDuration(player, miningAction, mining, catalog, wallet, null), 12);

        // 不匹配技艺的行动（alchemy）不受 mining 工具加成；
        // 绑定其他技艺的工具（桃木铲→gathering）也不计入 mining 除数。
        var alchemyAction = player.Enqueue(new ActionIntent(alchemy.Id, -1), Baseline, catalog.Actions);
        Assert.Equal(10, SimulationEngine.TimedActionDuration(player, alchemyAction, alchemy, catalog, wallet, null));
        player.Loadout.Equipment.Add(new PlayerEquipmentSlot(hoe.Value, 0));
        Assert.Equal(10 / (1 + 0.1 + TechniqueCatalog.SpeedBonusRatio(miningLevel)),
            SimulationEngine.TimedActionDuration(player, miningAction, mining, catalog, wallet, null), 12);

        // 工具槽位前缀判定。
        Assert.True(TechniqueCatalog.IsToolSlot("tool-mining"));
        Assert.False(TechniqueCatalog.IsToolSlot("weapon"));
        Assert.False(TechniqueCatalog.IsToolSlot(null));
    }

    /// <summary>技艺工具可强化：tool- 槽位装备在强化准入通过并可正常提升强化等级。</summary>
    [Fact]
    public void TechniqueToolCanBeEnhanced()
    {
        var hoe = new ItemId("equipment.tool-herb-hoe");
        var catalog = CreateMinimalCatalog(rawEquipments: new Dictionary<string, JsonElement>
        {
            [hoe.Value] = Json("""{"id":"equipment.tool-herb-hoe","name":"桃木铲","slot":"tool-gathering","tier":1,"techniqueBonus":{"technique":"gathering","speedRatio":0.1}}""")
        });
        var intent = new ActionIntent(new ActionId("enhance.equipment"), -1, TargetItemId: hoe.Value);
        var holdings = new Dictionary<AssetKey, double>
        {
            [new AssetKey(hoe)] = 1,
            [new AssetKey(new ItemId(EnhancementRules.SpiritStoneItemId))] = 999
        };
        var prep = EnhancementRules.Resolve(new PlayerLoadoutState(), intent, holdings, catalog, 0,
            out var check, out var reason);
        Assert.NotNull(prep);
        Assert.Equal(EnhancementRules.Check.Ready, check);
        Assert.Equal(10, prep.SpiritStoneCost);
        Assert.Equal(1, prep.TargetLevel);
    }

    /// <summary>技艺工具强化等级缩放：+N 工具的速度加成按 1.05^N 缩放生效。</summary>
    [Fact]
    public void EquippedToolEnhancementScalesSpeedRatio()
    {
        var compass = new ItemId("equipment.tool-prospect-compass");
        var mining = new ActionDefinition(new ActionId("mine.test"), "挖矿测试", ActionKind.Exploration, 10,
            new Dictionary<ItemId, double>(), [], Technique: TechniqueCatalog.Mining);
        var catalog = CreateMinimalCatalog(actions: new ActionCatalog([mining]),
            rawEquipments: new Dictionary<string, JsonElement>
            {
                [compass.Value] = Json("""{"id":"equipment.tool-prospect-compass","name":"赤铜镐","slot":"tool-mining","tier":1,"techniqueBonus":{"technique":"mining","speedRatio":0.1}}""")
            });
        var player = CreatePlayer("矿工");
        var wallet = CreateWallet(player);
        // +2 等级赤铜镐：速度加成 = 0.1 × 1.05^2 = 0.11025
        player.Loadout.Equipment.Add(new PlayerEquipmentSlot(compass.Value, 2));
        var miningAction = player.Enqueue(new ActionIntent(mining.Id, -1), Baseline, catalog.Actions);

        var expectedRatio = 0.1 * Math.Pow(1.05, 2);
        Assert.Equal(expectedRatio, TechniqueCatalog.EquippedToolSpeedRatio(player.Loadout, TechniqueCatalog.Mining, catalog), 12);
        Assert.Equal(10 / (1 + expectedRatio), SimulationEngine.TimedActionDuration(player, miningAction, mining, catalog, wallet, null), 12);
    }

    /// <summary>装备强化商店回收折旧遵循总纲 §4.6.2 闭式常数公式（同装备裸件代护 P=V0），白板绝对保底且随等级递增。</summary>
    [Fact]
    public void EquipmentSellPriceDepreciationFollowsClosedFormula()
    {
        const double v0 = 312.0; // 青锋剑白板基础售价
        Assert.Equal(312.0, EnhancementRules.CalculateEquipmentSellPrice(v0, v0, 0));
        Assert.Equal(478.0, EnhancementRules.CalculateEquipmentSellPrice(v0, v0, 1));
        Assert.Equal(691.0, EnhancementRules.CalculateEquipmentSellPrice(v0, v0, 2));
        Assert.Equal(1495.0, EnhancementRules.CalculateEquipmentSellPrice(v0, v0, 5));
        Assert.Equal(3449.0, EnhancementRules.CalculateEquipmentSellPrice(v0, v0, 10));
        Assert.Equal(9672.0, EnhancementRules.CalculateEquipmentSellPrice(v0, v0, 20));

        // 越界截断保护
        Assert.Equal(312.0, EnhancementRules.CalculateEquipmentSellPrice(v0, v0, -1));
        Assert.Equal(9672.0, EnhancementRules.CalculateEquipmentSellPrice(v0, v0, 99));
        Assert.Equal(0.0, EnhancementRules.CalculateEquipmentSellPrice(0, v0, 5));
        Assert.Equal(0.0, EnhancementRules.CalculateEquipmentSellPrice(-10, v0, 5));

        // 保护单价非法入参按 V0 兜底
        Assert.Equal(3449.0, EnhancementRules.CalculateEquipmentSellPrice(v0, 0, 10));
        Assert.Equal(3449.0, EnhancementRules.CalculateEquipmentSellPrice(v0, double.NaN, 10));

        // 低价特殊保护材料：强化增值只按保护单价折旧，不按白板价放大
        Assert.Equal(312.0 + 77.0 + 739.0, EnhancementRules.CalculateEquipmentSellPrice(v0, 10, 10));
    }

    /// <summary>强化回收价按配表 enhanceProtectionItems 最低回收价折算保护单价；未配置时按白板价兜底。</summary>
    [Fact]
    public void EquipmentSellPriceScalesWithConfiguredProtectionPrice()
    {
        var guardOre = new ItemDefinition(new ItemId("item.guard-ore"), "护矿石", "", true, 10);
        var priceyGuard = new ItemDefinition(new ItemId("item.pricey-guard"), "贵护符", "", true, 500);
        var darkIron = new ItemDefinition(new ItemId("item.dark-iron"), "乌铁", "", true, 5);
        var swordAction = new ActionDefinition(
            new ActionId("forge.guarded-sword"),
            "锻造白名单剑",
            ActionKind.Alchemy,
            10,
            new Dictionary<ItemId, double> { [darkIron.Id] = 10 }, // 成本: 50 → V0 = 100
            [new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, Item: new ItemId("equipment.guarded-sword"))]);
        var catalog = new GameDataCatalog("test", "fp", new ActionCatalog([swordAction]),
            items: new Dictionary<ItemId, ItemDefinition>
            {
                [guardOre.Id] = guardOre, [priceyGuard.Id] = priceyGuard, [darkIron.Id] = darkIron
            },
            rawEquipments: new Dictionary<string, JsonElement>
            {
                ["equipment.guarded-sword"] = Json("""{"id":"equipment.guarded-sword","name":"白名单剑","enhanceProtectionItems":["item.guard-ore","item.pricey-guard"]}"""),
                ["equipment.plain-sword"] = Json("""{"id":"equipment.plain-sword","name":"裸护剑"}""")
            });

        const double v0 = 100.0;
        // 白名单列表取最低回收价（10 < 500）
        Assert.Equal(10.0, EnhancementRules.ResolveProtectionUnitPrice(catalog, "equipment.guarded-sword", v0));
        // +10：100 + 7.687*10 + 739 = 915.87 → 916
        Assert.Equal(916.0, EnhancementRules.ComputeEquipmentSellPrice(catalog, "equipment.guarded-sword", 10));

        // 未配置保护白名单：按白板价 V0 代护
        var plainAction = new ActionDefinition(
            new ActionId("forge.plain-sword"),
            "锻造裸护剑",
            ActionKind.Alchemy,
            10,
            new Dictionary<ItemId, double> { [darkIron.Id] = 10 },
            [new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, Item: new ItemId("equipment.plain-sword"))]);
        var plainCatalog = new GameDataCatalog("test", "fp", new ActionCatalog([plainAction]),
            items: new Dictionary<ItemId, ItemDefinition> { [darkIron.Id] = darkIron },
            rawEquipments: new Dictionary<string, JsonElement>
            {
                ["equipment.plain-sword"] = Json("""{"id":"equipment.plain-sword","name":"裸护剑"}""")
            });
        Assert.Equal(100.0, EnhancementRules.ResolveProtectionUnitPrice(plainCatalog, "equipment.plain-sword", v0));
        // +10：100 + 7.687*100 + 739 = 1607.7 → 1608
        Assert.Equal(1608.0, EnhancementRules.ComputeEquipmentSellPrice(plainCatalog, "equipment.plain-sword", 10));
    }

    /// <summary>白板装备基础售价计算等于锻造原材料成本的 2.0 倍（制造翻倍铁律）。</summary>
    [Fact]
    public void ComputeEquipmentBaseSellPrice_ComputesDoubledMaterialCost()
    {
        var darkIron = new ItemDefinition(new ItemId("item.dark-iron"), "乌铁", "", true, 5);
        var beastBone = new ItemDefinition(new ItemId("item.beast-bone"), "兽骨", "", true, 6);
        var swordAction = new ActionDefinition(
            new ActionId("forge.test-sword"),
            "锻造测试剑",
            ActionKind.Alchemy,
            10,
            new Dictionary<ItemId, double> { [darkIron.Id] = 10, [beastBone.Id] = 2 }, // 成本: 10*5 + 2*6 = 62
            [new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, Item: new ItemId("equipment.test-sword"))]);
        var catalog = new GameDataCatalog("test", "fp", new ActionCatalog([swordAction]),
            items: new Dictionary<ItemId, ItemDefinition> { [darkIron.Id] = darkIron, [beastBone.Id] = beastBone },
            rawEquipments: new Dictionary<string, JsonElement>
            {
                ["equipment.test-sword"] = Json("""{"id":"equipment.test-sword","name":"测试剑","slot":"weapon"}""")
            });

        var cost = EnhancementRules.ComputeEquipmentMaterialCost(catalog, "equipment.test-sword");
        Assert.Equal(62.0, cost);
        var v0 = EnhancementRules.ComputeEquipmentBaseSellPrice(catalog, "equipment.test-sword");
        Assert.Equal(124.0, v0);
    }

    /// <summary>通用技艺加速物品（定神香 30%、青灵香 50%）在持续时间内提供准确的速度除数折减，耗尽后恢复。</summary>
    [Fact]
    public void GenericTechniqueSpeedConsumablesDiscountDuration()
    {
        var dingshen = new ItemDefinition(new ItemId("item.dingshen-incense"), "定神香", "", true, 120,
            new ConsumableDefinition(new HashSet<string> { AutoUseBar.Exploration, AutoUseBar.Alchemy, AutoUseBar.Forging },
                DurationSeconds: 300, IsContinuous: true, TechniqueSpeedRatio: 0.3));
        var qingling = new ItemDefinition(new ItemId("item.qingling-incense"), "青灵香", "", true, 380,
            new ConsumableDefinition(new HashSet<string> { AutoUseBar.Exploration, AutoUseBar.Alchemy, AutoUseBar.Forging },
                DurationSeconds: 300, IsContinuous: true, TechniqueSpeedRatio: 0.5));
        var alchemy = new ActionDefinition(new ActionId("alchemy.test"), "炼丹测试", ActionKind.Alchemy, 10,
            new Dictionary<ItemId, double>(), [], Technique: TechniqueCatalog.Alchemy);
        var catalog = new GameDataCatalog("test", "fp", new ActionCatalog([alchemy]),
            items: new Dictionary<ItemId, ItemDefinition> { [dingshen.Id] = dingshen, [qingling.Id] = qingling });

        var player = CreatePlayer("测试道友");
        var wallet = CreateWallet(player);
        var action = player.Enqueue(new ActionIntent(alchemy.Id, -1), Baseline, catalog.Actions);

        // 未配置消耗品：耗时 10 秒
        Assert.Equal(10.0, SimulationEngine.TimedActionDuration(player, action, alchemy, catalog, wallet, null), 12);

        // 1. 装配定神香（300s，生效中）：耗时折减为 10 / (1 + 0.3)
        player.Settings.AutoUseSlots.Add(new AutoUseSlotState(AutoUseBar.Alchemy, 0, dingshen.Id.Value, 1, true, RemainingDurationSeconds: 300));
        Assert.Equal(0.3, TimedActionDurationRules.EffectiveConsumableSpeedRatio(player.Settings.AutoUseSlots, alchemy, catalog), 12);
        Assert.Equal(10.0 / 1.3, SimulationEngine.TimedActionDuration(player, action, alchemy, catalog, wallet, null), 12);

        // 2. 切换为青灵香（50%）：耗时折减为 10 / (1 + 0.5)
        player.Settings.AutoUseSlots[0] = new AutoUseSlotState(AutoUseBar.Alchemy, 0, qingling.Id.Value, 1, true, RemainingDurationSeconds: 200);
        Assert.Equal(0.5, TimedActionDurationRules.EffectiveConsumableSpeedRatio(player.Settings.AutoUseSlots, alchemy, catalog), 12);
        Assert.Equal(10.0 / 1.5, SimulationEngine.TimedActionDuration(player, action, alchemy, catalog, wallet, null), 12);

        // 3. 持续时间耗尽（RemainingDurationSeconds <= 0）：速度加成归零，恢复 10 秒
        player.Settings.AutoUseSlots[0] = player.Settings.AutoUseSlots[0] with { RemainingDurationSeconds = 0 };
        Assert.Equal(0.0, TimedActionDurationRules.EffectiveConsumableSpeedRatio(player.Settings.AutoUseSlots, alchemy, catalog), 12);
        Assert.Equal(10.0, SimulationEngine.TimedActionDuration(player, action, alchemy, catalog, wallet, null), 12);
    }

    /// <summary>通用技艺加速物品（定神香）在采矿与炼器行动中端到端自动充能、扣件、加速并扣减时长；对无技艺修炼行动不激活。</summary>
    [Fact]
    public void GenericTechniqueSpeedConsumablesEndToEndAutoUseAndAdvance()
    {
        var dingshen = new ItemDefinition(new ItemId("item.dingshen-incense"), "定神香", "", true, 120,
            new ConsumableDefinition(new HashSet<string> { AutoUseBar.Exploration, AutoUseBar.Forging },
                DurationSeconds: 300, IsContinuous: true, TechniqueSpeedRatio: 0.3));
        var miningAction = new ActionDefinition(new ActionId("explore.mine-test"), "采矿测试", ActionKind.Exploration, 10,
            new Dictionary<ItemId, double>(), [], Technique: TechniqueCatalog.Mining);
        var forgingAction = new ActionDefinition(new ActionId("forging.refine-test"), "炼器测试", ActionKind.Alchemy, 10,
            new Dictionary<ItemId, double>(), [], Technique: TechniqueCatalog.Forging);
        var drawQiAction = new ActionDefinition(new ActionId("cultivation.draw-qi"), "吐纳", ActionKind.Cultivation, 10,
            new Dictionary<ItemId, double>(), []);

        var catalog = new GameDataCatalog("test", "fp", new ActionCatalog([miningAction, forgingAction, drawQiAction]),
            items: new Dictionary<ItemId, ItemDefinition> { [dingshen.Id] = dingshen });

        // 1. 采矿行动：历练栏放入未充能定神香（RemainingDurationSeconds = 0），钱包持存 3 个
        var player = CreatePlayer("采矿道友");
        var wallet = CreateWallet(player, (new AssetKey(dingshen.Id), 3));
        player.Settings.AutoUseSlots.Add(new AutoUseSlotState(AutoUseBar.Exploration, 0, dingshen.Id.Value, 1, true, RemainingDurationSeconds: 0));
        player.Enqueue(new ActionIntent(miningAction.Id, -1), Baseline, catalog.Actions);

        // 推进至第一轮完成：实际耗时为 10 / 1.3 秒，按这段真实耗时扣减定神香。
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(8), catalog, wallet);
        foreach (var d in result.AssetDeltas) wallet.Apply(d);
        Assert.True(result.CompletedRepeats > 0, $"实际完成轮次: {result.CompletedRepeats}, 事件: {string.Join(", ", result.Events.Select(e => e.Code))}");

        // 断言：定神香自动扣除 1 个（剩余 2 个）并触发 activated 事件
        Assert.Equal(2.0, wallet.Get(new AssetKey(dingshen.Id)));
        Assert.Contains(result.Events, e => e.Code == "auto-use.activated");
        // 槽位扣减的是加速后的本轮真实耗时，而非原始 10 秒。
        var slot = player.Settings.AutoUseSlots[0];
        Assert.Equal(300.0 - (10.0 / 1.3), slot.RemainingDurationSeconds, 12);

        // 2. 负向场景：吐纳修炼（无技艺）中定神香不应激活不扣件
        var playerCultivation = CreatePlayer("修炼道友");
        var walletCultivation = CreateWallet(playerCultivation, (new AssetKey(dingshen.Id), 3));
        playerCultivation.Settings.AutoUseSlots.Add(new AutoUseSlotState(AutoUseBar.Exploration, 0, dingshen.Id.Value, 1, true, RemainingDurationSeconds: 0));
        playerCultivation.Enqueue(new ActionIntent(drawQiAction.Id, -1), Baseline, catalog.Actions);

        var cultResult = SimulationEngine.AdvanceTo(playerCultivation, Baseline.AddSeconds(12), catalog, walletCultivation);
        foreach (var d in cultResult.AssetDeltas) walletCultivation.Apply(d);

        // 定神香依然为 3，未充能不消耗
        Assert.Equal(3.0, walletCultivation.Get(new AssetKey(dingshen.Id)));
        Assert.DoesNotContain(cultResult.Events, e => e.Code == "auto-use.activated");
        Assert.Equal(0.0, playerCultivation.Settings.AutoUseSlots[0].RemainingDurationSeconds);
    }

    /// <summary>强化专属成功率消耗品（淬灵粉 +3%、固灵砂 +5%）在生效期间提供准确的成功率加算。</summary>
    [Fact]
    public void EnhancementSuccessBonusConsumablesIncreaseSuccessRate()
    {
        var cuiling = new ItemDefinition(new ItemId("item.cuiling-powder"), "淬灵粉", "", true, 150,
            new ConsumableDefinition(new HashSet<string> { AutoUseBar.Enhancement },
                DurationSeconds: 300, IsContinuous: true, EnhancementSuccessBonus: 0.03));
        var guling = new ItemDefinition(new ItemId("item.guling-sand"), "固灵砂", "", true, 450,
            new ConsumableDefinition(new HashSet<string> { AutoUseBar.Enhancement },
                DurationSeconds: 300, IsContinuous: true, EnhancementSuccessBonus: 0.05));
        var catalog = new GameDataCatalog("test", "fp", ActionCatalog.Default,
            items: new Dictionary<ItemId, ItemDefinition> { [cuiling.Id] = cuiling, [guling.Id] = guling });

        var slots = new List<AutoUseSlotState>
        {
            new(AutoUseBar.Enhancement, 0, cuiling.Id.Value, 1, true, RemainingDurationSeconds: 300)
        };

        // 1. 淬灵粉：解析为 +0.03，成功率精准上浮 0.03
        Assert.Equal(0.03, EnhancementRules.EffectiveEnhancementSuccessBonus(slots, catalog), 12);
        var baseRate = EnhancementRules.SuccessRate(1, 0, 0);
        var boostedRate = EnhancementRules.SuccessRate(1, 0, 0.03);
        Assert.Equal(baseRate + 0.03, boostedRate, 12);

        // 2. 固灵砂：解析为 +0.05，成功率精准上浮 0.05
        slots[0] = new AutoUseSlotState(AutoUseBar.Enhancement, 0, guling.Id.Value, 1, true, RemainingDurationSeconds: 150);
        Assert.Equal(0.05, EnhancementRules.EffectiveEnhancementSuccessBonus(slots, catalog), 12);
        var gulingRate = EnhancementRules.SuccessRate(1, 0, 0.05);
        Assert.Equal(baseRate + 0.05, gulingRate, 12);

        // 3. 槽位未启用或耗竭时：加成归零
        slots[0] = slots[0] with { RemainingDurationSeconds = 0 };
        Assert.Equal(0.0, EnhancementRules.EffectiveEnhancementSuccessBonus(slots, catalog), 12);
    }

    /// <summary>
    /// 装备分级强化履历与暂存记录生命周期测试（TASK-20261030-01）：
    /// 1. 分级尝试与胜负次数精准累加；
    /// 2. 进行中（IsCompleted=false）记录不会被客户端回执误删；
    /// 3. 出队标记 IsCompleted=true 后，回执可彻底清理该条记录；
    /// 4. 聚合导出与恢复完整保留暂存明细。
    /// </summary>
    [Fact]
    public void StagedEnhancementRecords_LifecycleAndAckPruning()
    {
        var playerId = new PlayerId(Guid.NewGuid());
        var accountId = new AccountId(Guid.NewGuid());
        var player = PlayerAggregate.Create(playerId, accountId, "测试修士", Baseline, "test-fp");

        var actionA = Guid.NewGuid();
        var actionB = Guid.NewGuid();
        var swordId = "equipment.qingyun-sword";
        var robeId = "equipment.wujin-robe";

        // 行动 A 强化剑：+1 尝试 3 次，成 2 次；+2 尝试 2 次，成 1 次
        Assert.True(player.Progression.RecordEnhancementAttempt(actionA, swordId, 1, true, 1000));
        Assert.True(player.Progression.RecordEnhancementAttempt(actionA, swordId, 1, false, 1001));
        Assert.True(player.Progression.RecordEnhancementAttempt(actionA, swordId, 1, true, 1002));
        Assert.True(player.Progression.RecordEnhancementAttempt(actionA, swordId, 2, false, 1003));
        Assert.True(player.Progression.RecordEnhancementAttempt(actionA, swordId, 2, true, 1004));

        // 行动 B 强化甲：+1 尝试 1 次，成 1 次（正在进行中）
        Assert.True(player.Progression.RecordEnhancementAttempt(actionB, robeId, 1, true, 1005));

        var recA = player.Progression.StagedEnhancements[actionA];
        Assert.Equal(swordId, recA.ItemId);
        Assert.False(recA.IsCompleted);
        Assert.Equal(3, recA.Levels[1].Attempts);
        Assert.Equal(2, recA.Levels[1].Successes);
        Assert.Equal(2.0 / 3.0, recA.Levels[1].SuccessRate, 6);
        Assert.Equal(2, recA.Levels[2].Attempts);
        Assert.Equal(1, recA.Levels[2].Successes);

        // 测试：当客户端回执尝试移除尚未完成的 actionA 与 actionB 时，服务端拒绝移除（返回 0）
        var removedActive = player.RemoveCompletedEnhancementRecords([actionA, actionB]);
        Assert.Equal(0, removedActive);
        Assert.Equal(2, player.Progression.StagedEnhancements.Count);

        // 行动 A 出队完成：
        Assert.True(player.Progression.MarkEnhancementCompleted(actionA, 1010));
        Assert.True(player.Progression.StagedEnhancements[actionA].IsCompleted);

        // 再次发送回执移除 [actionA, actionB]：只有 actionA 被清理，actionB（仍在进行中）严格保留
        var removedFinished = player.RemoveCompletedEnhancementRecords([actionA, actionB]);
        Assert.Equal(1, removedFinished);
        Assert.False(player.Progression.StagedEnhancements.ContainsKey(actionA));
        Assert.True(player.Progression.StagedEnhancements.ContainsKey(actionB));
        Assert.False(player.Progression.StagedEnhancements[actionB].IsCompleted);

        // 快照导出与恢复往返验证
        var exported = player.ExportState();
        Assert.NotNull(exported.Progression.StagedEnhancements);
        Assert.Single(exported.Progression.StagedEnhancements);
        Assert.Equal(actionB, exported.Progression.StagedEnhancements[0].ActionId);

        var restored = PlayerAggregate.Restore(exported);
        Assert.True(restored.Progression.StagedEnhancements.ContainsKey(actionB));
        Assert.Equal(robeId, restored.Progression.StagedEnhancements[actionB].ItemId);
        Assert.Equal(1, restored.Progression.StagedEnhancements[actionB].Levels[1].Successes);
    }

    /// <summary>
    /// 排队版秘法行动即使携带 initialLevel（如 +20 装备化灵/分解），也不得套用强化 5+t 时长分支（否则 +20 会坍缩为 0 秒）；
    /// 化灵改为按目标物品配表显式 level 取 5 + 0.3×(等级−1) 动态基础耗时（强化 +N 不计入）。
    /// </summary>
    [Fact]
    public void TimedActionDurationRules_SecretArtWithInitialLevelDoesNotUseEnhancementFormula()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var def = catalog.Actions.Get(new ActionId(SecretArtRules.LiquefyActionId));
        var player = CreatePlayer("秘法耗时");
        var action = player.Enqueue(
            new ActionIntent(def.Id, 1, TargetItemId: "equipment.qingfeng-sword",
                Parameters: new Dictionary<string, double> { [EnhancementRules.InitialLevelParameter] = EnhancementRules.MaxLevel }),
            Baseline,
            catalog.Actions);

        // 青锋剑配表 level=1：基础耗时恒 5 秒（+20 initialLevel 不参与耗时计算）。
        var expected = TimedActionDurationRules.CalculateCore(
            SecretArtRules.LiquefyDurationSeconds(CraftingRecipeRules.ResolveItemLevel("equipment.qingfeng-sword", catalog)),
            def.Kind,
            def.Technique,
            player.World.EffectiveNodeBuildingEffects,
            player.Loadout.TechniqueExperiences,
            player.Loadout.Equipment,
            catalog);
        var runtimeDuration = TimedActionDurationRules.Calculate(player, action, def, catalog, assets: null);
        var snapshotDuration = TimedActionDurationRules.Calculate(player.ExportState(), player.ExportState().Actions[0], def, catalog);

        Assert.True(expected > 0);
        Assert.Equal(expected, runtimeDuration, 9);
        Assert.Equal(expected, snapshotDuration, 9);
    }

    /// <summary>化灵耗时挂钩物品等级：12 级妖丹基础耗时 8.3 秒（运行时与快照两条路径一致）。</summary>
    [Fact]
    public void TimedActionDurationRules_LiquefyDurationScalesWithItemLevel()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var def = catalog.Actions.Get(new ActionId(SecretArtRules.LiquefyActionId));
        var player = CreatePlayer("化灵耗时");
        var action = player.Enqueue(
            new ActionIntent(def.Id, 1, TargetItemId: "item.demon-dan"),
            Baseline,
            catalog.Actions);

        var itemLevel = CraftingRecipeRules.ResolveItemLevel("item.demon-dan", catalog);
        Assert.Equal(12, itemLevel);
        var expected = TimedActionDurationRules.CalculateCore(
            SecretArtRules.LiquefyDurationSeconds(itemLevel),
            def.Kind,
            def.Technique,
            player.World.EffectiveNodeBuildingEffects,
            player.Loadout.TechniqueExperiences,
            player.Loadout.Equipment,
            catalog);

        Assert.Equal(expected, TimedActionDurationRules.Calculate(player, action, def, catalog, assets: null), 9);
        Assert.Equal(expected,
            TimedActionDurationRules.Calculate(player.ExportState(), player.ExportState().Actions[0], def, catalog), 9);
        Assert.Equal(8.3, expected, 9);
    }

    /// <summary>
    /// NodeBuildings.SpeedRatioFor 传入明确技艺标识时不得因专项值为 0 而穿透回退到其他同大类技艺分支。
    /// </summary>
    [Fact]
    public void BuildingEffectSet_SpeedRatioFor_IsolatesExplicitTechniquesAndPreservesFallback()
    {
        var effects = new BuildingEffectSet(
            ExplorationSpeedRatio: 0.50,
            AlchemySpeedRatio: 0.40,
            GatheringSpeedRatio: 0,
            MiningSpeedRatio: 0,
            ForgingSpeedRatio: 0,
            TalismanSpeedRatio: 0,
            FormationSpeedRatio: 0.15,
            EnhancementSpeedRatio: 0.20);

        Assert.Equal(0d, effects.SpeedRatioFor(ActionKind.Exploration, TechniqueCatalog.Mining));
        Assert.Equal(0d, effects.SpeedRatioFor(ActionKind.Alchemy, TechniqueCatalog.Forging));
        Assert.Equal(0d, effects.SpeedRatioFor(ActionKind.Alchemy, TechniqueCatalog.Talisman));
        Assert.Equal(0.50, effects.SpeedRatioFor(ActionKind.Exploration, TechniqueCatalog.Gathering));
        Assert.Equal(0.15, effects.SpeedRatioFor(ActionKind.Alchemy, TechniqueCatalog.Formation));
        Assert.Equal(0.20, effects.SpeedRatioFor(ActionKind.Enhancement, TechniqueCatalog.Enhancement));
        Assert.Equal(0.50, effects.SpeedRatioFor(ActionKind.Exploration));
        Assert.Equal(0.40, effects.SpeedRatioFor(ActionKind.Alchemy));
    }

    /// <summary>
    /// 锻造/制造行动不得消耗已穿戴或处于强化锁定队列中的装备与采集工具：
    /// 1. 入队/开轮时材料仅被穿戴或强化锁定，必须阻塞并保留钱包余额；
    /// 2. 轮次进行中将未穿戴工具装配上身时，完成结算前复核也必须阻塞而非扣成 0 余额幽灵穿戴件。
    /// </summary>
    [Fact]
    public void Crafting_BlocksEquippedAndEnhanceLockedInputs_IncludingMidRoundEquip()
    {
        var hoe = new ItemId("equipment.tool-herb-hoe");
        var upgradedHoe = new ItemId("equipment.tool-iron-hoe");
        var craftAction = new ActionDefinition(
            new ActionId("forge.upgrade-herb-hoe"), "升级药锄", ActionKind.Alchemy, 10,
            new Dictionary<ItemId, double> { [hoe] = 1 },
            [new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, Item: upgradedHoe)],
            Technique: TechniqueCatalog.Forging);
        var enhanceAction = new ActionDefinition(
            new ActionId(EnhancementRules.ActionId), "强化装备", ActionKind.Enhancement, 6,
            new Dictionary<ItemId, double>(), [],
            Technique: TechniqueCatalog.Enhancement);
        var catalog = CreateMinimalCatalog(
            actions: new ActionCatalog([craftAction, enhanceAction]),
            rawEquipments: new Dictionary<string, JsonElement>
            {
                [hoe.Value] = Json("""{"id":"equipment.tool-herb-hoe","name":"青铜药锄","slot":"tool-gathering"}"""),
                [upgradedHoe.Value] = Json("""{"id":"equipment.tool-iron-hoe","name":"玄铁药锄","slot":"tool-gathering"}""")
            });

        // 1. 已穿戴工具时制造被阻塞且不扣余额
        var equippedPlayer = CreatePlayer("已穿戴工具修士");
        equippedPlayer.Loadout.Equipment.Add(new PlayerEquipmentSlot(hoe.Value, 0));
        var equippedWallet = CreateWallet(equippedPlayer, (new AssetKey(hoe), 1));
        equippedPlayer.Enqueue(new ActionIntent(craftAction.Id, 1), Baseline, catalog.Actions);

        var equippedResult = SimulationEngine.AdvanceTo(equippedPlayer, Baseline.AddSeconds(10), catalog, equippedWallet);
        foreach (var d in equippedResult.AssetDeltas) equippedWallet.Apply(d);
        Assert.Equal(0, equippedResult.CompletedRepeats);
        Assert.Equal(ActionQueueStatus.Blocked, equippedPlayer.Actions[0].Status);
        Assert.Equal(1.0, equippedWallet.Get(new AssetKey(hoe)));
        Assert.Equal(0.0, equippedWallet.Get(new AssetKey(upgradedHoe)));

        // 2. 材料虽未穿戴但被同队列后续 enhance.equipment 锁定：制造仍必须阻塞
        var lockedPlayer = CreatePlayer("强化锁定期修士");
        var lockedWallet = CreateWallet(lockedPlayer, (new AssetKey(hoe), 1));
        lockedPlayer.Enqueue(new ActionIntent(craftAction.Id, 1), Baseline, catalog.Actions);
        lockedPlayer.Enqueue(new ActionIntent(enhanceAction.Id, 1, TargetItemId: hoe.Value), Baseline, catalog.Actions);

        var lockedResult = SimulationEngine.AdvanceTo(lockedPlayer, Baseline.AddSeconds(10), catalog, lockedWallet);
        foreach (var d in lockedResult.AssetDeltas) lockedWallet.Apply(d);
        Assert.Equal(0, lockedResult.CompletedRepeats);
        Assert.Equal(ActionQueueStatus.Blocked, lockedPlayer.Actions[0].Status);
        Assert.Equal(1.0, lockedWallet.Get(new AssetKey(hoe)));

        // 3. 轮次进行中将工具穿戴上身：完成结算复核必须阻塞，不得把已穿戴件扣减成 0
        var midRoundPlayer = CreatePlayer("轮中穿戴修士");
        var midRoundWallet = CreateWallet(midRoundPlayer, (new AssetKey(hoe), 1));
        midRoundPlayer.Enqueue(new ActionIntent(craftAction.Id, 1), Baseline, catalog.Actions);

        var halfResult = SimulationEngine.AdvanceTo(midRoundPlayer, Baseline.AddSeconds(5), catalog, midRoundWallet);
        foreach (var d in halfResult.AssetDeltas) midRoundWallet.Apply(d);
        Assert.Equal(ActionQueueStatus.Running, midRoundPlayer.Actions[0].Status);

        midRoundPlayer.Loadout.Equipment.Add(new PlayerEquipmentSlot(hoe.Value, 0));
        var finishResult = SimulationEngine.AdvanceTo(midRoundPlayer, Baseline.AddSeconds(11), catalog, midRoundWallet);
        foreach (var d in finishResult.AssetDeltas) midRoundWallet.Apply(d);
        Assert.Equal(0, finishResult.CompletedRepeats);
        Assert.Equal(ActionQueueStatus.Blocked, midRoundPlayer.Actions[0].Status);
        Assert.Equal(1.0, midRoundWallet.Get(new AssetKey(hoe)));
        Assert.Equal(0.0, midRoundWallet.Get(new AssetKey(upgradedHoe)));
    }

    /// <summary>
    /// 装备强化队列互斥与保护材料防挤占校验：
    /// 另一条排队中的 enhance.equipment 已锁定目标件或同件保护材料时，新强化必须被阻塞；
    /// 仅当前行动自身（excludeActionId）不与自己冲突。
    /// </summary>
    [Fact]
    public void EnhancementRules_Resolve_ExcludesQueuedEnhanceLocksAndEquippedItems()
    {
        var hoe = new ItemId("equipment.tool-herb-hoe");
        var stone = new ItemId(EnhancementRules.SpiritStoneItemId);
        var enhanceAction = new ActionDefinition(
            new ActionId(EnhancementRules.ActionId), "强化装备", ActionKind.Enhancement, 6,
            new Dictionary<ItemId, double>(), [],
            Technique: TechniqueCatalog.Enhancement);
        var catalog = CreateMinimalCatalog(
            actions: new ActionCatalog([enhanceAction]),
            rawEquipments: new Dictionary<string, JsonElement>
            {
                [hoe.Value] = Json("""{"id":"equipment.tool-herb-hoe","name":"青铜药锄","slot":"tool-gathering"}""")
            });

        var player = CreatePlayer("排队强化修士");
        var intent = new ActionIntent(enhanceAction.Id, -1, TargetItemId: hoe.Value);
        var actionA = player.Enqueue(intent, Baseline, catalog.Actions);
        var actionB = player.Enqueue(intent, Baseline, catalog.Actions);

        // 1. 仅持 1 件未穿戴工具，actionA 与 actionB 同时瞄准同一件 +0 工具
        var holdings = new Dictionary<AssetKey, double>
        {
            [new AssetKey(hoe)] = 1,
            [new AssetKey(stone)] = 1000
        };
        var selfCheck = EnhancementRules.Resolve(player.Loadout, actionA.Intent, holdings, catalog, 0,
            out var checkA, out _, actions: player.Actions, excludeActionId: actionA.Id);
        Assert.NotNull(selfCheck);
        Assert.Equal(EnhancementRules.Check.Ready, checkA);

        var conflictCheck = EnhancementRules.Resolve(player.Loadout, actionB.Intent, holdings, catalog, 0,
            out var checkB, out _, actions: player.Actions, excludeActionId: actionB.Id);
        Assert.Null(conflictCheck);
        Assert.Equal(EnhancementRules.Check.Blocked, checkB);

        // 2. 持 2 件白板工具时，actionA 锁定 1 件，actionB 仍要求同件本体代护（需再占 2 件）应阻塞
        holdings[new AssetKey(hoe)] = 2;
        var protectIntent = new ActionIntent(enhanceAction.Id, -1, TargetItemId: hoe.Value, AuxItemId: hoe.Value);
        var protectConflict = EnhancementRules.Resolve(player.Loadout, protectIntent, holdings, catalog, 0,
            out var checkProtect, out _, actions: player.Actions, excludeActionId: actionB.Id);
        Assert.Null(protectConflict);
        Assert.Equal(EnhancementRules.Check.Blocked, checkProtect);

        // 3. 工具已穿戴且无队列锁时同样阻塞
        var equippedOnly = CreatePlayer("穿戴工具待强修士");
        equippedOnly.Loadout.Equipment.Add(new PlayerEquipmentSlot(hoe.Value, 0));
        holdings[new AssetKey(hoe)] = 1;
        Assert.Null(EnhancementRules.Resolve(equippedOnly.Loadout, intent, holdings, catalog, 0,
            out var checkEquipped, out _));
        Assert.Equal(EnhancementRules.Check.Blocked, checkEquipped);
    }

    /// <summary>
    /// 炼器倍率与边界校验：
    /// 只有 forging 技艺且配方输入中实际消耗了上级 equipment.* 的装备锻造行动享 4×（全局 2× × 升阶 2×）；
    /// Lv.1 装备/工具、无上级装备消耗的 Lv.9 散件与 Lv.18 材料精炼仅享全局 2×。
    /// </summary>
    [Fact]
    public void EquipmentUpgradeForgingGrantsFourTimesTechniqueExpAndEqualFoundationWhileStandaloneRecipesStayTwoTimes()
    {
        var catalog = GameDataCatalog.LoadEmbedded();

        var lv1Sword = catalog.Actions.Get(new ActionId("forge.qingfeng-sword"));
        var lv9Sword = catalog.Actions.Get(new ActionId("forge.xuantie-sword"));
        var copperRefine = catalog.Actions.Get(new ActionId("craft.refine-copper"));
        var lv18Sword = catalog.Actions.Get(new ActionId("forge.pogang-sword"));
        var lv20Blade = catalog.Actions.Get(new ActionId("forge.duanchen-blade"));

        Assert.False(TechniqueCatalog.IsEquipmentUpgradeForgingAction(lv1Sword, catalog));
        Assert.False(TechniqueCatalog.IsEquipmentUpgradeForgingAction(lv9Sword, catalog));
        Assert.False(TechniqueCatalog.IsEquipmentUpgradeForgingAction(copperRefine, catalog));
        Assert.True(TechniqueCatalog.IsEquipmentUpgradeForgingAction(lv18Sword, catalog));
        Assert.True(TechniqueCatalog.IsEquipmentUpgradeForgingAction(lv20Blade, catalog));

        // 1) Lv.1 装备锻造（15s）：全局 2× → 30 经验 + 30 底蕴
        var lv1Expected = TechniqueCatalog.ComputeTechniqueExperience(catalog, lv1Sword.Level ?? 1, lv1Sword.DurationSeconds, lv1Sword);
        Assert.Equal(30d, lv1Expected);

        // 2) Lv.9 玄铁剑（无上级装备输入，20s）：只享全局 2×
        var lv9Base = catalog.RealmProgression!.BaseExperienceForLevel(9);
        Assert.Equal(Math.Round(lv9Base * (lv9Sword.DurationSeconds / 10.0) * 2.0),
            TechniqueCatalog.ComputeTechniqueExperience(catalog, lv9Sword.Level ?? 1, lv9Sword.DurationSeconds, lv9Sword));

        // 3) Lv.18 精炼玄铜（输入只有矿石，20s）：只享全局 2×
        var lv18Base = catalog.RealmProgression.BaseExperienceForLevel(18);
        Assert.Equal(Math.Round(lv18Base * (copperRefine.DurationSeconds / 10.0) * 2.0),
            TechniqueCatalog.ComputeTechniqueExperience(catalog, copperRefine.Level ?? 1, copperRefine.DurationSeconds, copperRefine));

        // 4) Lv.18 破罡剑（消耗清风剑 × 2）：全局 2× × 升阶 2× = 4×
        var lv18UpgradeExpected = TechniqueCatalog.ComputeTechniqueExperience(catalog, lv18Sword.Level ?? 1, lv18Sword.DurationSeconds, lv18Sword);
        Assert.Equal(Math.Round(lv18Base * (lv18Sword.DurationSeconds / 10.0) * 4.0), lv18UpgradeExpected);

        // 实跑模拟端到端核对技艺经验、伴生底蕴与三账本
        var blacksmith = CreatePlayer("升阶锻造师");
        blacksmith.Progression.RealmLevel = 18;
        blacksmith.Progression.Experience = 50; // 修为明确未满，照常发底蕴
        var wallet = CreateWallet(blacksmith, lv18Sword.Inputs.Select(p => (new AssetKey(p.Key), p.Value * 2)).ToArray());
        blacksmith.Enqueue(new ActionIntent(lv18Sword.Id, 1), Baseline, catalog.Actions);

        var result = SimulationEngine.AdvanceTo(blacksmith, Baseline.AddSeconds(lv18Sword.DurationSeconds), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);

        Assert.Equal(lv18UpgradeExpected, blacksmith.Loadout.TechniqueExperiences[TechniqueCatalog.Forging]);
        Assert.Equal(lv18UpgradeExpected, blacksmith.Progression.Foundation);
        Assert.Equal(50, blacksmith.Progression.Experience);
        Assert.Equal(0, blacksmith.Progression.LifetimeExperience);
        var archivedReport = blacksmith.Duel.Reports.FirstOrDefault();
        Assert.NotNull(archivedReport);
        Assert.Equal(lv18UpgradeExpected, archivedReport.Foundation);
        Assert.Contains(archivedReport.TechniqueExp, entry => entry.TechniqueId == TechniqueCatalog.Forging && entry.Experience == lv18UpgradeExpected);
    }

    /// <summary>
    /// 全部技艺获取经验时，无论当前修为是否圆满，均等额直接向底蕴池发放底蕴（不增加当前修为或 LifetimeExperience），
    /// 且发放后的底蕴可用于后续打坐修炼 1:1 放大获取修为；同步覆盖 PlayerAggregate.GrantTechniqueExperience 与秘法转化路径。
    /// </summary>
    [Fact]
    public void AllTechniqueExpGrantsEqualFoundationEvenWhenExperienceNotFullAndFeedsMeditation()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("生活打坐双修");
        player.Progression.Experience = 100; // 明确未满（1 级 100 < 500 削减上限），初始底蕴 0
        player.Progression.Foundation = 0;

        // 1) 直接领域接口 GrantTechniqueExperience
        player.GrantTechniqueExperience(TechniqueCatalog.Alchemy, 40, catalog);
        Assert.Equal(40, player.Loadout.TechniqueExperiences[TechniqueCatalog.Alchemy]);
        Assert.Equal(40, player.Progression.Foundation);
        Assert.Equal(100, player.Progression.Experience);
        Assert.Equal(0, player.Progression.LifetimeExperience);
        Assert.Contains(PlayerSectionKind.Loadout, player.DirtySections);
        Assert.Contains(PlayerSectionKind.Progression, player.DirtySections);

        // 2) 秘法转化排队行动（消耗残卷，按物品真实等级与 8 秒时长推导技艺经验、神识与等额底蕴）
        var fragment = new ItemId("item.fragment.heavy-slash");
        var convert = catalog.Actions.Get(new ActionId(SecretArtRules.ConvertActionId));
        var (expectedTechExp, expectedConsciousnessExp) = SecretArtRules.ComputeYieldExperience(catalog, fragment.Value, convert.DurationSeconds);
        var wallet = CreateWallet(player, (new AssetKey(fragment), 2));
        player.Enqueue(new ActionIntent(convert.Id, 1, TargetItemId: fragment.Value), Baseline, catalog.Actions);
        var convertRes = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(convert.DurationSeconds), catalog, wallet);
        convertRes.AssetDeltas.ToList().ForEach(wallet.Apply);

        Assert.Equal(expectedTechExp, player.Loadout.TechniqueExperiences[TechniqueCatalog.Enhancement]);
        Assert.Equal(40 + expectedTechExp, player.Progression.Foundation);
        Assert.Equal(expectedConsciousnessExp, player.Loadout.CoreAttributes.ConsciousnessExp);
        Assert.Equal(100, player.Progression.Experience);

        // 3) 用积攒的底蕴去打坐修炼（引气入体 10s：基础 10 点修为，底蕴按 1:1 兑换放大）
        var drawQi = catalog.Actions.Get(new ActionId("cultivation.draw-qi"));
        player.Enqueue(new ActionIntent(drawQi.Id, 1), Baseline.AddSeconds(convert.DurationSeconds), catalog.Actions);
        var cultRes = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(convert.DurationSeconds + drawQi.DurationSeconds), catalog, wallet);
        cultRes.AssetDeltas.ToList().ForEach(wallet.Apply);

        // 打坐消耗底蕴 1:1 放大修为，剩余底蕴严格减少
        Assert.True(player.Progression.Experience > 110, $"预期底蕴放大打坐修为，实际 Experience={player.Progression.Experience}");
        Assert.True(player.Progression.Foundation < 40 + expectedTechExp, $"预期底蕴被打坐消耗，实际 Foundation={player.Progression.Foundation}");
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
