// ==============================================================================
// 文件名称: BuffEvaluatorTests.cs
// 主要职责: 验证全域通用 Buff 词缀与属性修饰器解算引擎 (BuffEvaluator) 的正确性。
// ==============================================================================

namespace Domain.Tests;

using Domain.Constants;
using Domain.Models;
using Domain.Simulation;
using Xunit;

public sealed class BuffEvaluatorTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetAdditiveBonus_ShouldSumActiveModifiersMultipliedByStacks()
    {
        var buffs = new List<BuffState>
        {
            new()
            {
                BuffId = "buff.dmg-1",
                Name = "攻击强化",
                Stacks = 2,
                ExpiresAt = Baseline.AddMinutes(5),
                DamageRatio = 0.15 // +15% per stack -> 30%
            },
            new()
            {
                BuffId = "buff.dmg-2",
                Name = "狂暴战意",
                Stacks = 1,
                ExpiresAt = Baseline.AddMinutes(1),
                DamageRatio = 0.20 // +20%
            },
            new()
            {
                BuffId = "buff.dmg-expired",
                Name = "失效药力",
                Stacks = 3,
                ExpiresAt = Baseline.AddMinutes(-1),
                DamageRatio = 0.50 // expired
            }
        };

        var bonus = BuffEvaluator.GetAdditiveBonus(buffs, Baseline, StatKeys.DamageRatio);

        // Expected: (0.15 * 2) + (0.20 * 1) = 0.50
        Assert.Equal(0.50, bonus, 4);
    }

    [Fact]
    public void GetMaxMultiplier_ShouldReturnHighestActiveMultiplierOrDefault()
    {
        var buffs = new List<BuffState>
        {
            new()
            {
                BuffId = "buff.gather-1",
                Name = "寻草诀",
                Stacks = 1,
                ExpiresAt = Baseline.AddMinutes(5),
                GatheringOutputMultiplier = 1.5
            },
            new()
            {
                BuffId = "buff.gather-2",
                Name = "天工探灵",
                Stacks = 1,
                ExpiresAt = Baseline.AddMinutes(2),
                GatheringOutputMultiplier = 2.0
            },
            new()
            {
                BuffId = "buff.gather-expired",
                Name = "过期灵香",
                Stacks = 1,
                ExpiresAt = Baseline.AddMinutes(-5),
                GatheringOutputMultiplier = 5.0
            }
        };

        var maxMult = BuffEvaluator.GetMaxMultiplier(buffs, Baseline, StatKeys.GatheringOutput, 1.0);
        Assert.Equal(2.0, maxMult, 4);

        var emptyMult = BuffEvaluator.GetMaxMultiplier([], Baseline, StatKeys.GatheringOutput, 1.0);
        Assert.Equal(1.0, emptyMult, 4);
    }

    [Fact]
    public void CustomStatKeys_ShouldBeEvaluatedSeamlessly()
    {
        var buffs = new List<BuffState>
        {
            new()
            {
                BuffId = "buff.speed",
                Name = "神行术",
                Stacks = 2,
                ExpiresAt = Baseline.AddMinutes(10)
            }
        };
        buffs[0].SetModifier(StatKeys.MovementSpeed, 1.35); // 35% speed
        buffs[0].SetModifier(StatKeys.CriticalChance, 0.05); // 5% crit per stack

        var speedMult = BuffEvaluator.GetMaxMultiplier(buffs, Baseline, StatKeys.MovementSpeed, 1.0);
        Assert.Equal(1.35, speedMult, 4);

        var critBonus = BuffEvaluator.GetAdditiveBonus(buffs, Baseline, StatKeys.CriticalChance);
        Assert.Equal(0.10, critBonus, 4); // 0.05 * 2 stacks
    }

    [Fact]
    public void ApplyAdditiveBonus_And_ScaleMultiplier_ShouldWorkCorrectly()
    {
        var buffs = new List<BuffState>
        {
            new()
            {
                BuffId = "buff.alchemy",
                Name = "丹火纯青",
                Stacks = 1,
                ExpiresAt = Baseline.AddMinutes(5),
                AlchemyOutputMultiplier = 1.5
            }
        };
        buffs[0].SetModifier(StatKeys.DamageRatio, 0.20); // +20%

        // Base = 100, Additive = +20% -> 120
        var bonusValue = BuffEvaluator.ApplyAdditiveBonus(100.0, buffs, Baseline, StatKeys.DamageRatio);
        Assert.Equal(120.0, bonusValue, 4);

        // Base = 10, Multiplier = 1.5 -> 15
        var scaledValue = BuffEvaluator.ScaleMultiplier(10.0, buffs, Baseline, StatKeys.AlchemyOutput, defaultMultiplier: 1.0);
        Assert.Equal(15.0, scaledValue, 4);
    }
}
