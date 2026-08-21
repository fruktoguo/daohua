using Xiuxian.Domain.GameData;
using Xiuxian.Domain.Models;
using Xunit;

namespace Xiuxian.Domain.Tests;

public sealed class XiuxianDataBudgetResolverTests
{
    [Fact]
    public void ResolveAbility_WithExplicitValues_PreservesExplicitFields()
    {
        var config = new XiuxianAbilityConfig(
            Id: "ability.custom-slash",
            Name: "自定义剑招",
            Style: XiuxianCombatStyle.Sword,
            DamageType: XiuxianDamageType.Physical,
            CooldownMilliseconds: 10000,
            CastMilliseconds: 500,
            ManaCost: 20,
            DamageRatio: 1.75,
            FlatDamage: 40,
            StunChance: 0.25,
            StunMilliseconds: 2000,
            PenetrationRatio: 0.15);

        var definition = XiuxianDataBudgetResolver.ResolveAbility(config);

        Assert.Equal("ability.custom-slash", definition.Id);
        Assert.Equal("自定义剑招", definition.Name);
        Assert.Equal(XiuxianCombatStyle.Sword, definition.Style);
        Assert.Equal(1.75, definition.DamageRatio);
        Assert.Equal(40, definition.FlatDamage);
        Assert.Equal(0.25, definition.StunChance);
        Assert.Equal(2000, definition.StunMilliseconds);
        Assert.Equal(0.15, definition.PenetrationRatio);
    }

    [Fact]
    public void ResolveAbility_WithIntensityAndWeights_CalculatesDeterministicStats()
    {
        // 强度 40.0，权重：伤害倍率 60%，固定伤害 20%，穿透 20%
        var config = new XiuxianAbilityConfig(
            Id: "ability.weight-blade",
            Name: "破阵斩",
            Style: XiuxianCombatStyle.Blade,
            DamageType: XiuxianDamageType.Physical,
            CooldownMilliseconds: 12000,
            CastMilliseconds: 600,
            ManaCost: 0,
            Intensity: 40.0,
            Weights: new Dictionary<string, double>
            {
                ["damageRatio"] = 0.60,
                ["flatDamage"] = 0.20,
                ["penetrationRatio"] = 0.20
            });

        var definition = XiuxianDataBudgetResolver.ResolveAbility(config);

        // 验证计算：
        // totalBudget = 40.0
        // damageRatio = 1.0 + 40.0 * 0.60 * 0.025 = 1.0 + 0.60 = 1.60
        // flatDamage = round(40.0 * 0.20 * 4.2) = round(33.6) = 34
        // penetrationRatio = 40.0 * 0.20 * 0.008 = 0.064
        Assert.Equal(1.60, definition.DamageRatio, precision: 3);
        Assert.Equal(34, definition.FlatDamage);
        Assert.Equal(0.064, definition.PenetrationRatio, precision: 3);
        Assert.True(definition.DamageRatioLevelBonus > 0);
        Assert.True(definition.FlatDamageLevelBonus > 0);
    }

    [Fact]
    public void ResolveAbility_WithTierDefaultBudget_CalculatesFromTierBase()
    {
        // 第 2 阶基准预算 25.0，技能强度乘区 1.0 + (2-1)*0.30 = 1.30 -> 基础 32.5
        // 耗蓝 50 MP 补偿 +4.0 -> 总预算 36.5
        var config = new XiuxianAbilityConfig(
            Id: "ability.tier2-smash",
            Name: "裂地击",
            Tier: 2,
            ManaCost: 50,
            Weights: new Dictionary<string, double>
            {
                ["damageRatio"] = 0.70,
                ["flatDamage"] = 0.30
            });

        var definition = XiuxianDataBudgetResolver.ResolveAbility(config);

        Assert.True(definition.DamageRatio > 1.0);
        Assert.True(definition.FlatDamage > 0);
    }

    [Fact]
    public void ResolveEquipment_WithIntensityAndWeights_GeneratesStatsAndEnhancementBonus()
    {
        // 强度 100.0，双手法器 (two-hand)
        var config = new XiuxianEquipmentConfig(
            Id: "equipment.iron-bulwark",
            Name: "玄铁重盾",
            Slot: "two-hand",
            Tier: 3,
            Intensity: 100.0,
            Weights: new Dictionary<string, double>
            {
                ["armor"] = 0.50,
                ["allResistance"] = 0.30,
                ["weakenRatio"] = 0.20
            });

        var definition = XiuxianDataBudgetResolver.ResolveEquipment(config);

        // armor: 100 * 0.50 * 1.0 = 50.0
        // allRes: 100 * 0.30 * 0.4 = 12.0 for water/nature/fire
        // weaken: 100 * 0.20 * 0.003 = 0.06
        Assert.Equal(50.0, definition.Stats.Armor, precision: 2);
        Assert.Equal(12.0, definition.Stats.WaterResistance, precision: 2);
        Assert.Equal(12.0, definition.Stats.NatureResistance, precision: 2);
        Assert.Equal(12.0, definition.Stats.FireResistance, precision: 2);
        Assert.Equal(0.06, definition.Stats.WeakenRatio, precision: 3);

        // 自动生成的强化增量应为 Stats / 50.0
        Assert.Equal(1.0, definition.EnhancementBonus.Armor, precision: 2);
        Assert.Equal(0.24, definition.EnhancementBonus.WaterResistance, precision: 3);
        Assert.Equal(0.0012, definition.EnhancementBonus.WeakenRatio, precision: 4);
    }

    [Fact]
    public void ResolveItem_WithConsumableWeights_CalculatesRestoreAndBuffValues()
    {
        var config = new XiuxianItemConfig(
            Id: "item.spirit-elixir",
            Name: "回元大丹",
            Description: "测试丹药",
            Kind: XiuxianItemKind.Consumable,
            Category: "丹药",
            Tier: 2,
            Consumable: new XiuxianConsumableConfig(
                UsableInActionTypes: new HashSet<XiuxianActionType> { XiuxianActionType.Combat },
                Intensity: 30.0,
                Weights: new Dictionary<string, double>
                {
                    ["hitpointRestore"] = 0.60,
                    ["manapointRestore"] = 0.40
                },
                Buffs:
                [
                    new XiuxianBuffEffectConfig(
                        BuffId: "buff.elixir-power",
                        Name: "丹煞",
                        DurationSeconds: 120,
                        Intensity: 20.0,
                        Weights: new Dictionary<string, double>
                        {
                            ["damageRatio"] = 0.50,
                            ["attackSpeedRatio"] = 0.50
                        })
                ]));

        var definition = XiuxianDataBudgetResolver.ResolveItem(config);

        Assert.NotNull(definition.Consumable);
        // hp: 30 * 0.60 * 12.0 = 216
        // mp: 30 * 0.40 * 8.0 = 96
        Assert.Equal(216, definition.Consumable.HitpointRestore);
        Assert.Equal(96, definition.Consumable.ManapointRestore);

        Assert.Single(definition.Consumable.Buffs);
        var buff = definition.Consumable.Buffs[0];
        // dmg: 20 * 0.50 * 0.01 = 0.10 (+10%)
        // spd: 20 * 0.50 * 0.01 = 0.10 (+10%)
        Assert.Equal(0.10, buff.DamageRatio, precision: 3);
        Assert.Equal(0.10, buff.AttackSpeedRatio, precision: 3);
    }
}
