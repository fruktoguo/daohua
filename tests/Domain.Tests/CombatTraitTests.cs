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
        // 天生蛮力：物理攻击 +10%
        var ratios1 = new Dictionary<string, double>();
        var points1 = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers(["trait.tianshengmanli"], new Dictionary<string, int> { ["trait.tianshengmanli"] = 1 }, ratios1, points1);
        Assert.Equal(0.10, ratios1["physicalAttack"], precision: 4);

        // Lv.2 -> +10.5% (1.05x)
        var ratios2 = new Dictionary<string, double>();
        var points2 = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers(["trait.tianshengmanli"], new Dictionary<string, int> { ["trait.tianshengmanli"] = 2 }, ratios2, points2);
        Assert.Equal(0.105, ratios2["physicalAttack"], precision: 4);

        // Lv.10 -> +14.5% (1.45x)
        var ratios10 = new Dictionary<string, double>();
        var points10 = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers(["trait.tianshengmanli"], new Dictionary<string, int> { ["trait.tianshengmanli"] = 10 }, ratios10, points10);
        Assert.Equal(0.145, ratios10["physicalAttack"], precision: 4);
    }

    [Fact]
    public void DoubleEdgedTrait_LocksNegativePenalties_WhenLevelIncreases()
    {
        // 1. 万剑归一：物攻 65%、暴击 30%、破招 25%、最大生命 -30%、双减免 -25
        var wjLevels = new Dictionary<string, int> { ["trait.wanjianguiyi"] = 10 }; // 10级 (1.45x)
        var ratiosWj = new Dictionary<string, double>();
        var pointsWj = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers(["trait.wanjianguiyi"], wjLevels, ratiosWj, pointsWj);

        // 正向加成提升 (65% * 1.45 = 94.25%)
        Assert.Equal(0.65 * 1.45, ratiosWj["physicalAttack"], precision: 4);
        Assert.Equal(0.30 * 1.45, ratiosWj["criticalChance"], precision: 4);
        Assert.Equal(0.25 * 1.45, ratiosWj["break"], precision: 4);

        // 负面惩罚锁定不变：最大生命削减 -30% 仍为 -0.30；减免 -25 点仍为 -25
        Assert.Equal(-0.30, ratiosWj["maxHitpoints"], precision: 4);
        Assert.Equal(-25, pointsWj["physicalDamageReductionPoints"], precision: 4);
        Assert.Equal(-25, pointsWj["spellDamageReductionPoints"], precision: 4);

        // 2. 不动明王：双抗 80%、最大生命 50%、全伤害降低 -45 点
        var bdLevels = new Dictionary<string, int> { ["trait.budongmingwang"] = 5 }; // 5级 (1.20x)
        var ratiosBd = new Dictionary<string, double>();
        var pointsBd = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers(["trait.budongmingwang"], bdLevels, ratiosBd, pointsBd);

        Assert.Equal(0.80 * 1.20, ratiosBd["physicalArmor"], precision: 4);
        Assert.Equal(0.80 * 1.20, ratiosBd["spellResistance"], precision: 4);
        Assert.Equal(0.50 * 1.20, ratiosBd["maxHitpoints"], precision: 4);
        // 全伤害降低惩罚锁定 -45 点不变
        Assert.Equal(-45, pointsBd["totalDamageBonusPoints"], precision: 4);

        // 3. 九幽噬灵：术攻 65%、最大灵力 50%、暴击 20%、护甲 -35%、闪避 -30%
        var jyLevels = new Dictionary<string, int> { ["trait.jiuyoushiling"] = 3 }; // 3级 (1.10x)
        var ratiosJy = new Dictionary<string, double>();
        var pointsJy = new Dictionary<string, double>();
        CombatTraitRules.AccumulateModifiers(["trait.jiuyoushiling"], jyLevels, ratiosJy, pointsJy);

        Assert.Equal(0.65 * 1.10, ratiosJy["spellAttack"], precision: 4);
        Assert.Equal(0.50 * 1.10, ratiosJy["maxManapoints"], precision: 4);
        Assert.Equal(0.20 * 1.10, ratiosJy["criticalChance"], precision: 4);
        // 负面惩罚锁定不变
        Assert.Equal(-0.35, ratiosJy["physicalArmor"], precision: 4);
        Assert.Equal(-0.30, ratiosJy["evasion"], precision: 4);
    }

    [Fact]
    public void MechanicTraits_ScaleDurationsAndThreatPoints()
    {
        // 力微饭否：1 级 100 点，2 级 105 点
        Assert.Equal(100.0, CombatTraitRules.ThreatOnTakenPoints(["trait.liweifanfou"], new Dictionary<string, int> { ["trait.liweifanfou"] = 1 }));
        Assert.Equal(105.0, CombatTraitRules.ThreatOnTakenPoints(["trait.liweifanfou"], new Dictionary<string, int> { ["trait.liweifanfou"] = 2 }));
    }
}
