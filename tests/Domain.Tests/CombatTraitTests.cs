using Domain.Combat;

namespace Domain.Tests;

/// <summary>
/// 特质系统测试（总纲 §7.12）：容量推算、消耗阶梯、属性百分比加成与双刃剑负面惩罚锁定。
/// </summary>
public sealed class CombatTraitTests
{
    [Fact]
    public void TraitCapacityAndCost_CalculateCorrectly()
    {
        // 初始 Lv.1 为 5 点
        Assert.Equal(5, CombatTraitRules.CalculateCapacity(1));
        // 消耗总计
        Assert.Equal(2, CombatTraitRules.TotalCost(["trait.tianshengmanli"]));
        Assert.Equal(20, CombatTraitRules.TotalCost(["trait.wanjianguiyi"]));
    }

    [Fact]
    public void NormalTrait_ScalesPositiveModifiers_WhenLevelIncreases()
    {
        // 天生蛮力：物理攻击 +5%
        var ratios1 = new Dictionary<string, double>();
        var points1 = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers(["trait.tianshengmanli"], new Dictionary<string, int> { ["trait.tianshengmanli"] = 1 }, ratios1, points1);
        Assert.Equal(0.05, ratios1["physicalAttack"], precision: 4);

        // Lv.2 -> +5.25% (1.05x)
        var ratios2 = new Dictionary<string, double>();
        var points2 = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers(["trait.tianshengmanli"], new Dictionary<string, int> { ["trait.tianshengmanli"] = 2 }, ratios2, points2);
        Assert.Equal(0.0525, ratios2["physicalAttack"], precision: 4);

        // Lv.10 -> +7.25% (1.45x)
        var ratios10 = new Dictionary<string, double>();
        var points10 = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers(["trait.tianshengmanli"], new Dictionary<string, int> { ["trait.tianshengmanli"] = 10 }, ratios10, points10);
        Assert.Equal(0.0725, ratios10["physicalAttack"], precision: 4);
    }

    [Fact]
    public void DoubleEdgedTrait_LocksNegativePenalties_WhenLevelIncreases()
    {
        // 1. 万剑归一：物攻 60%、暴击 50%、破招 45%、最大生命 -25%、双减免 -15
        var wjLevels = new Dictionary<string, int> { ["trait.wanjianguiyi"] = 10 }; // 10级 (1.45x)
        var ratiosWj = new Dictionary<string, double>();
        var pointsWj = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers(["trait.wanjianguiyi"], wjLevels, ratiosWj, pointsWj);

        // 正向加成提升 (60% * 1.45 = 87%)
        Assert.Equal(0.60 * 1.45, ratiosWj["physicalAttack"], precision: 4);
        Assert.Equal(0.50 * 1.45, ratiosWj["criticalChance"], precision: 4);
        Assert.Equal(0.45 * 1.45, ratiosWj["break"], precision: 4);

        // 负面惩罚锁定不变：最大生命削减 -25% 仍为 -0.25；减免 -15 点仍为 -15
        Assert.Equal(-0.25, ratiosWj["maxHitpoints"], precision: 4);
        Assert.Equal(-15, pointsWj["physicalDamageReductionPoints"], precision: 4);
        Assert.Equal(-15, pointsWj["spellDamageReductionPoints"], precision: 4);

        // 2. 不动明王：双抗 110%、最大生命 85%、全伤害降低 -30 点
        var bdLevels = new Dictionary<string, int> { ["trait.budongmingwang"] = 5 }; // 5级 (1.20x)
        var ratiosBd = new Dictionary<string, double>();
        var pointsBd = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers(["trait.budongmingwang"], bdLevels, ratiosBd, pointsBd);

        Assert.Equal(1.10 * 1.20, ratiosBd["physicalArmor"], precision: 4);
        Assert.Equal(1.10 * 1.20, ratiosBd["spellResistance"], precision: 4);
        Assert.Equal(0.85 * 1.20, ratiosBd["maxHitpoints"], precision: 4);
        // 全伤害降低惩罚锁定 -30 点不变
        Assert.Equal(-30, pointsBd["totalDamageBonusPoints"], precision: 4);

        // 3. 九幽噬灵：术攻 60%、最大灵力 75%、暴击 40%、护甲 -30%、闪避 -25%
        var jyLevels = new Dictionary<string, int> { ["trait.jiuyoushiling"] = 3 }; // 3级 (1.10x)
        var ratiosJy = new Dictionary<string, double>();
        var pointsJy = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers(["trait.jiuyoushiling"], jyLevels, ratiosJy, pointsJy);

        Assert.Equal(0.60 * 1.10, ratiosJy["spellAttack"], precision: 4);
        Assert.Equal(0.75 * 1.10, ratiosJy["maxManapoints"], precision: 4);
        Assert.Equal(0.40 * 1.10, ratiosJy["criticalChance"], precision: 4);
        // 负面惩罚锁定不变
        Assert.Equal(-0.30, ratiosJy["physicalArmor"], precision: 4);
        Assert.Equal(-0.25, ratiosJy["evasion"], precision: 4);
    }

    [Fact]
    public void MechanicTraits_ScaleDurationsAndThreatPoints()
    {
        // 力微饭否：1 级 100 点，2 级 105 点
        Assert.Equal(100.0, CombatTraitRules.ThreatOnTakenPoints(["trait.liweifanfou"], new Dictionary<string, int> { ["trait.liweifanfou"] = 1 }));
        Assert.Equal(105.0, CombatTraitRules.ThreatOnTakenPoints(["trait.liweifanfou"], new Dictionary<string, int> { ["trait.liweifanfou"] = 2 }));
    }

    [Fact]
    public void CombatAttributeRefresh_UsesEntryTraitsForNumericAndReactiveEffects()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "入战特质",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), catalog.Fingerprint);
        player.Progression.TraitIds.Add("trait.tianshengmanli");
        player.Progression.TraitIds.Add(CombatTraitRules.ThreatOnTakenTraitId);
        player.Progression.UnlockedTraits[CombatTraitRules.ThreatOnTakenTraitId] = new(CombatTraitRules.ThreatOnTakenTraitId, 3, 0);
        var entry = PlayerAttributeResolver.Resolve(player, catalog);
        var frozen = CombatTraitState.Capture(entry.ToCombatant(0));

        player.Progression.TraitIds.Clear();
        player.Progression.UnlockedTraits[CombatTraitRules.ThreatOnTakenTraitId] = new(CombatTraitRules.ThreatOnTakenTraitId, 10, 0);
        var current = PlayerAttributeResolver.Resolve(player, catalog);
        var inBattle = PlayerAttributeResolver.ResolveForCombat(player, catalog, frozen);

        Assert.True(entry.PhysicalAttack > current.PhysicalAttack);
        Assert.Equal(entry.PhysicalAttack, inBattle.PhysicalAttack);
        Assert.Equal(110, inBattle.Effects.ThreatOnTakenPoints, 8);
        Assert.Equal(0, current.Effects.ThreatOnTakenPoints);
    }
}
