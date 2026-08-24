// ==============================================================================
// 文件名称: BuffEvaluator.cs
// 主要职责: 修仙世界通用 Buff 词缀与属性修饰器解算引擎 (BuffEvaluator)。
// 核心逻辑 / 定义内容:
//   1. 活跃 Buff 时间有效性与层数（Stacks）判定。
//   2. 通用属性加算比例总和解算（GetAdditiveBonus / ApplyAdditiveBonus）。
//   3. 通用产出与收益独立最大乘区解算（GetMaxMultiplier / ScaleMultiplier）。
//   4. 复合属性修饰流水线计算。
// ==============================================================================

namespace Domain.Simulation;

using Domain.Constants;
using Domain.Models;

/// <summary>
/// 全域 Buff 通用属性与加成解算器。
/// </summary>
public static class BuffEvaluator
{
    /// <summary>
    /// 获取当前有效 Buff 集合在指定属性键上的加算总和（如所有伤害加成百分比总和：Σ(val * stacks)）。
    /// </summary>
    /// <param name="buffs">待评估的 Buff 列表。</param>
    /// <param name="at">评估对齐时间戳。</param>
    /// <param name="statKey">属性键名（见 StatKeys）。</param>
    /// <returns>加算加成总和。</returns>
    public static double GetAdditiveBonus(IEnumerable<BuffState> buffs, DateTimeOffset at, string statKey)
    {
        if (buffs is null) return 0.0;
        return buffs
            .Where(b => b.ExpiresAt > at)
            .Sum(b => b.GetModifier(statKey, 0.0) * Math.Max(1, b.Stacks));
    }

    /// <summary>
    /// 获取当前有效 Buff 集合在指定属性键上的最大独立倍率（如修炼、采集、炼丹产出倍率）。
    /// 规则：取所有有效 Buff 中针对该属性配置的最高独立倍率。
    /// </summary>
    /// <param name="buffs">待评估的 Buff 列表。</param>
    /// <param name="at">评估对齐时间戳。</param>
    /// <param name="statKey">属性键名（见 StatKeys）。</param>
    /// <param name="defaultMultiplier">无生效 Buff 时的默认基准倍率（默认 1.0）。</param>
    /// <returns>最大有效独立倍率。</returns>
    public static double GetMaxMultiplier(
        IEnumerable<BuffState> buffs,
        DateTimeOffset at,
        string statKey,
        double defaultMultiplier = 1.0)
    {
        if (buffs is null) return defaultMultiplier;
        return buffs
            .Where(b => b.ExpiresAt > at)
            .Select(b => b.GetModifier(statKey, defaultMultiplier))
            .Where(mult => mult > 0)
            .DefaultIfEmpty(defaultMultiplier)
            .Max();
    }

    /// <summary>
    /// 按照加算百分比修饰基础数值：baseValue * (1.0 + additiveBonus)。
    /// </summary>
    public static double ApplyAdditiveBonus(
        double baseValue,
        IEnumerable<BuffState> buffs,
        DateTimeOffset at,
        string statKey)
    {
        var bonus = GetAdditiveBonus(buffs, at, statKey);
        return baseValue * Math.Max(0.0, 1.0 + bonus);
    }

    /// <summary>
    /// 按照独立乘区倍率缩放基础数值：baseValue * maxMultiplier。
    /// </summary>
    public static double ScaleMultiplier(
        double baseValue,
        IEnumerable<BuffState> buffs,
        DateTimeOffset at,
        string statKey,
        double defaultMultiplier = 1.0)
    {
        var mult = GetMaxMultiplier(buffs, at, statKey, defaultMultiplier);
        return baseValue * mult;
    }
}
