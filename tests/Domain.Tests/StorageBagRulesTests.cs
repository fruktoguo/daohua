using System;
using Domain.Combat;
using Domain.GameData;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 通用稀有储物袋掉落与开启规则单元测试。
/// 验证爆率指数成长、层次倍率、品阶映射与独立多重开启概率分布。
/// </summary>
public sealed class StorageBagRulesTests
{
    [Fact]
    public void CalculateDropChance_MatchesFormulaAcrossLevelsAndRanks()
    {
        // 1. 等级 1 普通小怪：基础爆率 0.03%
        var minionLvl1 = StorageBagDropRules.CalculateDropChance(1, CombatMonsterRank.Minion);
        Assert.Equal(0.0003, minionLvl1, precision: 9);

        // 2. 等级 2 普通小怪：0.03% * 105%
        var minionLvl2 = StorageBagDropRules.CalculateDropChance(2, CombatMonsterRank.Minion);
        Assert.Equal(0.0003 * 1.05, minionLvl2, precision: 9);

        // 3. 等级 1 精英：5 倍
        var eliteLvl1 = StorageBagDropRules.CalculateDropChance(1, CombatMonsterRank.Elite);
        Assert.Equal(0.0003 * 5.0, eliteLvl1, precision: 9);

        // 4. 等级 1 妖王/Boss：20 倍
        var bossLvl1 = StorageBagDropRules.CalculateDropChance(1, CombatMonsterRank.Boss);
        Assert.Equal(0.0003 * 20.0, bossLvl1, precision: 9);

        // 5. 等级 14 精英：0.03% * (1.05)^13 * 5
        var expectedEliteLvl14 = 0.0003 * Math.Pow(1.05, 13) * 5.0;
        var eliteLvl14 = StorageBagDropRules.CalculateDropChance(14, CombatMonsterRank.Elite);
        Assert.Equal(expectedEliteLvl14, eliteLvl14, precision: 9);

        // 6. 等级 18 妖王：0.03% * (1.05)^17 * 20
        var expectedBossLvl18 = 0.0003 * Math.Pow(1.05, 17) * 20.0;
        var bossLvl18 = StorageBagDropRules.CalculateDropChance(18, CombatMonsterRank.Boss);
        Assert.Equal(expectedBossLvl18, bossLvl18, precision: 9);

        // 7. 负数或 0 等级下限夹取为 1
        var minionLvl0 = StorageBagDropRules.CalculateDropChance(0, CombatMonsterRank.Minion);
        Assert.Equal(0.0003, minionLvl0, precision: 9);
    }

    [Fact]
    public void ResolveStorageBagItemId_ReturnsBrokenBagForCurrentStage()
    {
        Assert.Equal("item.storage-bag.broken", StorageBagDropRules.ResolveStorageBagItemId(1).Value);
        Assert.Equal("item.storage-bag.broken", StorageBagDropRules.ResolveStorageBagItemId(18).Value);
        Assert.Equal("item.storage-bag.broken", StorageBagDropRules.ResolveStorageBagItemId(54).Value);
    }

    [Fact]
    public void OpenBrokenBags_BoundaryAndZeroCount_ReturnsZeroRewards()
    {
        var rng = new Random(42);
        var zero = StorageBagOpenRules.OpenBrokenBags(rng, 0);
        Assert.False(zero.HasAnyRewards);
        Assert.Equal(0, zero.SpiritStones);
        Assert.Equal(0, zero.Gongde);
        Assert.Equal(0, zero.AwakeningStones);

        var negative = StorageBagOpenRules.OpenBrokenBags(rng, -5);
        Assert.False(negative.HasAnyRewards);
        Assert.Equal(0, negative.SpiritStones);
    }

    [Fact]
    public void OpenBrokenBags_SingleBag_GuaranteesSpiritStonesInRange()
    {
        var rng = new Random(12345);
        for (var i = 0; i < 200; i++)
        {
            var result = StorageBagOpenRules.OpenBrokenBags(rng, 1);
            Assert.True(result.HasAnyRewards);
            // 基础 1-1000 灵石，额外 10% 概率触发 1-10000 灵石
            // 最小灵石必然 >= 1，最大不超过 1000 + 10000 = 11000
            Assert.InRange(result.SpiritStones, 1, 11000);
            // 功德 0 或 5-10 或 50-100 或两者皆中
            Assert.InRange(result.Gongde, 0, 110);
            // 觉醒石 0 或 1
            Assert.InRange(result.AwakeningStones, 0, 1);
        }
    }

    [Fact]
    public void OpenBrokenBags_StatisticalDistribution_ConvergesToExpectedMeans()
    {
        // 开启 50,000 个破损储物袋，通过大数定律验证概率收敛性
        var rng = new Random(20261102);
        const int count = 50_000;
        var result = StorageBagOpenRules.OpenBrokenBags(rng, count);

        // 1. 灵石期望分析：
        // 基础 1-1000 均匀均值 = 500.5
        // 额外 10% * (1-10000 均匀均值 5000.5) = 500.05
        // 单袋综合期望灵石 ≈ 1000.55
        var avgStones = result.SpiritStones / count;
        Assert.InRange(avgStones, 960, 1040);

        // 2. 功德期望分析：
        // 10% * 7.5 = 0.75
        // 1% * 75 = 0.75
        // 单袋综合期望功德 ≈ 1.50
        var avgGongde = result.Gongde / count;
        Assert.InRange(avgGongde, 1.40, 1.60);

        // 3. 觉醒石期望分析：
        // 0.1% 概率 = 0.001
        // 50,000 次期望约为 50 颗（置信区间 30 ~ 70 颗）
        Assert.InRange(result.AwakeningStones, 30, 70);
    }
}
