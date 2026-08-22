using Daoxuan.Domain.GameData;
using Daoxuan.Domain.Models;
using Xunit;

namespace Daoxuan.Domain.Tests;

/// <summary>
/// 通用角色数值解析器 (DaoxuanCharacterStatResolver) 单元测试。
/// 验证空权重全基准默认值、生命层次系数、向下取整开关、流派偏向与显式属性覆盖。
/// </summary>
public sealed class DaoxuanCharacterStatResolverTests
{
    [Fact]
    public void Empty_or_null_weights_resolve_to_full_baseline_attributes_not_zero()
    {
        // 哪怕传入空配置/空权重，所有 14 项属性也全部有值（基于阶位与生命层次推导）
        var stats = DaoxuanCharacterStatResolver.Resolve(new DaoxuanCharacterStatRequest
        {
            Level = 1,
            Tier = 1,
            Rank = "normal",
            Floor = false
        });

        Assert.True(stats.MaxHitpoints > 0, "MaxHitpoints 必须大于 0");
        Assert.True(stats.MaxManapoints > 0, "MaxManapoints 必须大于 0");
        Assert.True(stats.MaxDamage > 0, "MaxDamage 必须大于 0");
        Assert.True(stats.DefensiveMaxDamage > 0, "DefensiveMaxDamage 必须大于 0");
        Assert.True(stats.Armor > 0, "Armor 必须大于 0");
        Assert.True(stats.WaterResistance > 0, "WaterResistance 必须大于 0 (全1权重基准)");
        Assert.True(stats.NatureResistance > 0, "NatureResistance 必须大于 0 (全1权重基准)");
        Assert.True(stats.FireResistance > 0, "FireResistance 必须大于 0 (全1权重基准)");
        Assert.True(stats.AccuracyRating > 0, "AccuracyRating 必须大于 0");
        Assert.True(stats.EvasionRating > 0, "EvasionRating 必须大于 0");
        Assert.True(stats.CriticalRating > 0, "CriticalRating 必须大于 0");
        Assert.True(stats.CriticalAvoidance > 0, "CriticalAvoidance 必须大于 0");
        Assert.True(stats.BreakRating > 0, "BreakRating 必须大于 0");
        Assert.True(stats.ParryRating > 0, "ParryRating 必须大于 0");
        Assert.True(stats.HpRegen >= 1.0, "HpRegen 必须大于等于 1.0");
        Assert.True(stats.MpRegen >= 1.0, "MpRegen 必须大于等于 1.0");
    }

    [Theory]
    [InlineData("minion", 0.80, 3.00)]
    [InlineData("elite", 1.20, 5.00)]
    [InlineData("boss", 1.50, 20.00)]
    [InlineData("leader", 1.35, 10.00)]
    [InlineData("summon", 1.00, 1.00)]
    [InlineData("pet", 1.00, 1.00)]
    [InlineData("npc", 1.00, 1.00)]
    [InlineData("normal", 1.00, 1.00)]
    public void Life_stage_rank_multipliers_are_applied_accurately(string rank, double expectedStatMult, double expectedHpMult)
    {
        var (statMult, hpMult) = DaoxuanCharacterStatResolver.GetRankMultipliers(rank);
        Assert.Equal(expectedStatMult, statMult, precision: 4);
        Assert.Equal(expectedHpMult, hpMult, precision: 4);

        var stats = DaoxuanCharacterStatResolver.Resolve(level: 1, rank: rank);
        Assert.Equal(expectedStatMult, stats.StatMultiplier, precision: 4);
        Assert.Equal(expectedHpMult, stats.HpMultiplier, precision: 4);
    }

    [Fact]
    public void Floor_option_can_be_disabled_to_retain_floating_precision()
    {
        // 开启向下取整 (默认)
        var floored = DaoxuanCharacterStatResolver.Resolve(new DaoxuanCharacterStatRequest
        {
            Level = 1,
            Rank = "minion",
            Floor = true
        });

        // 关闭向下取整 (保留浮点小数)
        var unfloored = DaoxuanCharacterStatResolver.Resolve(new DaoxuanCharacterStatRequest
        {
            Level = 1,
            Rank = "minion",
            Floor = false
        });

        Assert.Equal(Math.Floor(unfloored.Armor), floored.Armor);
        Assert.Equal(Math.Floor(unfloored.AccuracyRating), floored.AccuracyRating);
        Assert.True(unfloored.Armor >= floored.Armor);
    }

    [Fact]
    public void Magic_and_physical_styles_select_appropriate_attack_pool()
    {
        // 物理流派
        var physical = DaoxuanCharacterStatResolver.Resolve(new DaoxuanCharacterStatRequest
        {
            Level = 1,
            Tier = 1,
            Style = DaoxuanCombatStyle.Body,
            Rank = "normal",
            Floor = false
        });

        // 五行术法流派
        var magic = DaoxuanCharacterStatResolver.Resolve(new DaoxuanCharacterStatRequest
        {
            Level = 1,
            Tier = 1,
            Style = DaoxuanCombatStyle.FiveElements,
            Rank = "normal",
            Floor = false
        });

        var expectedTier1 = DaoxuanDataBudgetResolver.GetTierExpectedAttributes(1);
        Assert.Equal((long)Math.Round(expectedTier1.PhysicalAttack), physical.MaxDamage);
        Assert.Equal((long)Math.Round(expectedTier1.MagicAttack), magic.MaxDamage);
    }

    [Fact]
    public void Explicit_attribute_overrides_take_precedence()
    {
        var stats = DaoxuanCharacterStatResolver.Resolve(new DaoxuanCharacterStatRequest
        {
            Level = 1,
            Rank = "minion",
            MaxHitpoints = 99999,
            Armor = 888.5,
            HpRegen = 77.0
        });

        Assert.Equal(99999, stats.MaxHitpoints);
        Assert.Equal(888.5, stats.Armor);
        Assert.Equal(77.0, stats.HpRegen);
    }

    [Fact]
    public void Summon_and_npc_presets_work_seamlessly()
    {
        // 测试召唤物（如灵兽、傀儡）
        var summonStats = DaoxuanCharacterStatResolver.Resolve(new DaoxuanCharacterStatRequest
        {
            Level = 12,
            Tier = 1,
            Rank = "summon",
            Style = DaoxuanCombatStyle.Body,
            Weights = new Dictionary<string, double>
            {
                ["hp"] = 1.5,
                ["ad"] = 1.2
            }
        });

        Assert.Equal(1.0, summonStats.StatMultiplier);
        Assert.Equal(1.0, summonStats.HpMultiplier);
        Assert.True(summonStats.MaxHitpoints > 0);
        Assert.True(summonStats.MaxDamage > 0);
    }
}
