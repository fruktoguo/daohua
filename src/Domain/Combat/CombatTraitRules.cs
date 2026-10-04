namespace Domain.Combat;

/// <summary>
/// 特质规则单点表（总纲 §7.12）：特质 ID → 面板点数折算与开战 Buff 授予的集中映射。
/// 配表化（traits 词表）属远期扩展；本期映射硬编码于此，玩家与怪物面板同构消费。
/// </summary>
internal static class CombatTraitRules
{
    /// <summary>看不到我：开战获得隐身 buff，威胁权重归零持续 10 秒。</summary>
    internal const string UnseenTraitId = "trait.bukandaowo";
    /// <summary>力微饭否：受到伤害时按攻击方面板原始伤害累积自身威胁。</summary>
    internal const string ThreatOnTakenTraitId = "trait.liweifanfou";
    /// <summary>隐身 buff：看不到我特质的开战授予效果（威胁增幅点数 -100）。</summary>
    internal const string UnseenBuffId = "buff.yinshen";
    /// <summary>隐身 buff 持续时长（战斗时钟秒）。</summary>
    internal const double UnseenBuffDurationSeconds = 10;
    /// <summary>力微饭否折算点数：原始伤害 1:1 转为自身威胁。</summary>
    internal const double ThreatOnTakenTraitPoints = 100;

    /// <summary>特质 → 受击涨威胁点数折算：由属性解析汇入 <see cref="CombatEffectProfile"/> 通道。</summary>
    internal static double ThreatOnTakenPoints(IReadOnlyList<string>? traits)
        => ContainsTrait(traits, ThreatOnTakenTraitId) ? ThreatOnTakenTraitPoints : 0;

    /// <summary>特质 → 开战 Buff 授予：遭遇创建时对每个单位按其面板 Traits 生效。</summary>
    internal static void ApplyBattleStartTraits(CombatantState unit)
    {
        if (ContainsTrait(unit.Stats.Traits, UnseenTraitId))
            unit.Buffs.Add(new CombatBuffState(UnseenBuffId, UnseenBuffDurationSeconds, ThreatPoints: -100));
    }

    private static bool ContainsTrait(IReadOnlyList<string>? traits, string traitId)
    {
        if (traits is null) return false;
        for (var i = 0; i < traits.Count; i++)
            if (string.Equals(traits[i], traitId, StringComparison.Ordinal)) return true;
        return false;
    }
}
