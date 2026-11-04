using System;
using Domain.GameData;

namespace Domain.Combat;
/// <summary>
/// 通用稀有掉落规则：所有怪物共享的程序硬编码掉落配置（不写在配表内）。
/// 当前掉落物品为修仙储物袋（现阶段先实装「破损的储物袋」）。
/// </summary>
public static class StorageBagDropRules
{
    /// <summary>通用稀有掉落基础爆率：0.03%（0.0003）。</summary>
    public const double BaseDropChance = 0.0003;

    /// <summary>等级成长乘数：怪物每提升一级，基础爆率提升 5%（即 0.03% * 105% 复利递推）。</summary>
    public const double LevelGrowthMultiplier = 1.05;

    /// <summary>破损的储物袋物品 ID。</summary>
    public static readonly ItemId BrokenBagItemId = new("item.storage-bag.broken");

    /// <summary>
    /// 计算指定等级与层次怪物的通用稀有掉落基础概率（未计算玩家掉率加成前）。
    /// <para>怪物等级每比 1 级高 1 级，基础爆率提升 5%（1.05^(level-1)）；精英 * 5 倍，妖王/Boss * 20 倍。</para>
    /// </summary>
    public static double CalculateDropChance(int level, CombatMonsterRank rank)
    {
        var levelFactor = Math.Pow(LevelGrowthMultiplier, Math.Max(0, level - 1));
        var rankMultiplier = rank switch
        {
            CombatMonsterRank.Elite => 5.0,
            CombatMonsterRank.Boss => 20.0,
            _ => 1.0
        };
        return BaseDropChance * levelFactor * rankMultiplier;
    }
    /// <summary>
    /// 计算指定等级与层次名称怪物的通用稀有掉落基础概率。
    /// </summary>
    public static double CalculateDropChance(int level, string? rankName)
    {
        return CalculateDropChance(level, ParseRank(rankName));
    }

    /// <summary>
    /// 计算指定等级（double）与层次名称怪物的通用稀有掉落基础概率。
    /// </summary>
    public static double CalculateDropChance(double level, string? rankName)
    {
        var intLevel = Math.Max(1, (int)Math.Round(level));
        return CalculateDropChance(intLevel, ParseRank(rankName));
    }

    private static CombatMonsterRank ParseRank(string? rankName)
    {
        if (string.Equals(rankName, "boss", StringComparison.OrdinalIgnoreCase)) return CombatMonsterRank.Boss;
        if (string.Equals(rankName, "elite", StringComparison.OrdinalIgnoreCase)) return CombatMonsterRank.Elite;
        return CombatMonsterRank.Minion;
    }

    /// <summary>
    /// 随怪物等级决定掉落什么品级的储物袋（支持 double 等级）。
    /// </summary>
    public static ItemId ResolveStorageBagItemId(double level)
    {
        return ResolveStorageBagItemId(Math.Max(1, (int)Math.Round(level)));
    }
    /// <summary>
    /// 随怪物等级决定掉落什么品级的储物袋。
    /// 现阶段恒定掉落「破损的储物袋」，为后续更高品级储物袋预留扩展点。
    /// </summary>
    public static ItemId ResolveStorageBagItemId(int level)
    {
        return BrokenBagItemId;
    }
}

/// <summary>储物袋开启收益明细汇总。</summary>
public sealed record StorageBagOpenResult(
    double SpiritStones,
    double Gongde,
    double AwakeningStones)
{
    /// <summary>是否存在有效收益。</summary>
    public bool HasAnyRewards => SpiritStones > 0 || Gongde > 0 || AwakeningStones > 0;
}

/// <summary>
/// 储物袋开启规则与独立多重随机判定。
/// </summary>
public static class StorageBagOpenRules
{
    /// <summary>灵石资产 ID。</summary>
    public static readonly ItemId SpiritStoneItemId = new("currency.spirit-stone");

    /// <summary>功德资产 ID。</summary>
    public static readonly ItemId GongdeItemId = new("item.gongde");

    /// <summary>觉醒石物品 ID。</summary>
    public static readonly ItemId AwakeningStoneItemId = new("item.awakening-stone");

    /// <summary>
    /// 开启指定数量的破损储物袋，逐个判定独立概率并累加产物。
    /// <list type="bullet">
    ///   <item><description>1-1000 灵石：100% 必出</description></item>
    ///   <item><description>1-10000 灵石：10% 概率额外产出</description></item>
    ///   <item><description>5-10 功德：10% 概率产出</description></item>
    ///   <item><description>50-100 功德：1% 概率产出</description></item>
    ///   <item><description>觉醒石：0.1% 概率产出 1 颗</description></item>
    /// </list>
    /// </summary>
    public static StorageBagOpenResult OpenBrokenBags(Random rng, int count)
    {
        ArgumentNullException.ThrowIfNull(rng);
        if (count <= 0) return new StorageBagOpenResult(0, 0, 0);

        var totalStones = 0.0;
        var totalGongde = 0.0;
        var totalAwakeningStones = 0.0;

        for (var i = 0; i < count; i++)
        {
            // 1. 1-1000 灵石 100%
            totalStones += rng.Next(1, 1001);

            // 2. 1-10000 灵石 10%
            if (rng.NextDouble() < 0.10)
            {
                totalStones += rng.Next(1, 10001);
            }

            // 3. 5-10 功德 10%
            if (rng.NextDouble() < 0.10)
            {
                totalGongde += rng.Next(5, 11);
            }

            // 4. 50-100 功德 1%
            if (rng.NextDouble() < 0.01)
            {
                totalGongde += rng.Next(50, 101);
            }

            // 5. 觉醒石 0.1%
            if (rng.NextDouble() < 0.001)
            {
                totalAwakeningStones += 1.0;
            }
        }

        return new StorageBagOpenResult(totalStones, totalGongde, totalAwakeningStones);
    }
}
