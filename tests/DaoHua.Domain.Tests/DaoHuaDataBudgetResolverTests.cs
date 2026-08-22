// ==============================================================================
// 文件名称: DaoHuaDataBudgetResolverTests.cs
// 主要职责: 数值预算解析器（DaoHuaDataBudgetResolver）量化推导算法单元测试。
// 核心逻辑 / 定义内容:
//   1. 验证技能（Ability）显式参数透传与基于品阶法力期望的消耗预算换算。
//   2. 验证法宝装备（Equipment）总预算在各基础属性权重与词条上的确定性分配。
//   3. 验证消耗品（Consumable）效果预算推导与词条增量（Affix Increment）计算规则。
// ==============================================================================

using DaoHua.Domain.GameData;
using DaoHua.Domain.Models;
using Xunit;

namespace DaoHua.Domain.Tests;

/// <summary>
/// 验证 DaoHuaDataBudgetResolver（数值预算解析器）的确定性解析逻辑。
/// 覆盖技能、法宝装备、消耗品物品及词条增量在配置强度与属性权重下的预算映射与数值生成。
/// </summary>
public sealed class DaoHuaDataBudgetResolverTests
{
    /// <summary>
    /// 验证显式配置属性的保留与法力消耗计算。
    /// 确保配置中明确声明的战斗风格、伤害系数、固定伤害、控制与穿透参数完整透传，并依品阶期望法力计算法力消耗。
    /// </summary>
    [Fact]
    public void ResolveAbility_WithExplicitValues_PreservesExplicitFields()
    {
        var config = new DaoHuaAbilityConfig(
            Id: "ability.custom-slash",
            Name: "自定义剑招",
            Style: DaoHuaCombatStyle.Sword,
            DamageType: DaoHuaDamageType.Physical,
            CooldownMilliseconds: 10000,
            CastMilliseconds: 500,
            ManaCostRatio: 10,
            DamageRatio: 1.75,
            FlatDamage: 40,
            StunChance: 0.25,
            StunMilliseconds: 2000,
            PenetrationRatio: 0.15);

        var definition = DaoHuaDataBudgetResolver.ResolveAbility(config);

        Assert.Equal("ability.custom-slash", definition.Id);
        Assert.Equal("自定义剑招", definition.Name);
        Assert.Equal(DaoHuaCombatStyle.Sword, definition.Style);
        Assert.Equal(1.75, definition.DamageRatio);
        Assert.Equal(40, definition.FlatDamage);
        Assert.Equal(0.25, definition.StunChance);
        Assert.Equal(2000, definition.StunMilliseconds);
        Assert.Equal(0.15, definition.PenetrationRatio);
        // Tier 1 ExpectedMp=57.7, ManaCostRatio=10 → ceil(57.7 * 10 * 0.01) = ceil(5.77) = 6
        Assert.Equal(6, definition.ManaCost);
    }

    /// <summary>
    /// 验证按法力消耗比率（ManaCostRatio）和品阶基准期望法力值向上取整的计算逻辑。
    /// </summary>
    [Fact]
    public void ResolveAbility_ManaCostRatio_CalculatesCeiledManaCost()
    {
        // Tier 3 ExpectedMp=1434.04, ManaCostRatio=3 → ceil(1434.04 * 3 * 0.01) = ceil(43.0212) = 44
        var config = new DaoHuaAbilityConfig(
            Id: "ability.tier3-spell",
            Name: "烈火术",
            Tier: 3,
            ManaCostRatio: 3,
            DamageRatio: 1.5);

        var definition = DaoHuaDataBudgetResolver.ResolveAbility(config);

        Assert.Equal(44, definition.ManaCost);
        Assert.Equal(1.5, definition.DamageRatio);
    }

    /// <summary>
    /// 验证零法力消耗配置解析结果为 0，适用于无消耗的基础攻击与武技。
    /// </summary>
    [Fact]
    public void ResolveAbility_ZeroManaCostRatio_ProducesZeroCost()
    {
        var config = new DaoHuaAbilityConfig(
            Id: "ability.free-attack",
            Name: "普攻",
            ManaCostRatio: 0,
            DamageRatio: 0.5);

        var definition = DaoHuaDataBudgetResolver.ResolveAbility(config);

        Assert.Equal(0, definition.ManaCost);
    }

    /// <summary>
    /// 验证依据装备强度（Intensity）与属性权重字典生成基础属性及对应强化成长曲线。
    /// </summary>
    [Fact]
    public void ResolveEquipment_WithIntensityAndWeights_GeneratesStatsAndEnhancementBonus()
    {
        // 强度 100.0，双手法器 (two-hand)
        var config = new DaoHuaEquipmentConfig(
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

        var definition = DaoHuaDataBudgetResolver.ResolveEquipment(config);

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

    /// <summary>
    /// 验证依据消耗品权重计算生命/灵力即时恢复量以及附带持续 Buff 属性加成。
    /// </summary>
    [Fact]
    public void ResolveItem_WithConsumableWeights_CalculatesRestoreAndBuffValues()
    {
        var config = new DaoHuaItemConfig(
            Id: "item.spirit-elixir",
            Name: "回元大丹",
            Description: "测试丹药",
            Kind: DaoHuaItemKind.Consumable,
            Category: "丹药",
            Tier: 2,
            Consumable: new DaoHuaConsumableConfig(
                UsableInActionTypes: new HashSet<DaoHuaActionType> { DaoHuaActionType.Combat },
                Intensity: 30.0,
                Weights: new Dictionary<string, double>
                {
                    ["hitpointRestore"] = 0.60,
                    ["manapointRestore"] = 0.40
                },
                Buffs:
                [
                    new DaoHuaBuffEffectConfig(
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

        var definition = DaoHuaDataBudgetResolver.ResolveItem(config);

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
