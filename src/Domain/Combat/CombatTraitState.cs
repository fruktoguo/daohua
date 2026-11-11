namespace Domain.Combat;

/// <summary>本场入战定格的特质装配与等级；纯数据随战斗快照保存，战中修改留待下一场入战生效。</summary>
public sealed record CombatTraitState(IReadOnlyList<string> Ids, IReadOnlyDictionary<string, int>? Levels = null)
{
    /// <summary>入战未装备任何特质。</summary>
    public static CombatTraitState Empty { get; } = new(Array.Empty<string>());

    /// <summary>复制面板输入，隔离玩家装配集合及等级字典的后续修改。</summary>
    internal static CombatTraitState Capture(CombatantStats stats)
        => stats.Traits is not { Count: > 0 } ? Empty : new(stats.Traits.ToArray(),
            stats.TraitLevels?.Where(pair => stats.Traits.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    /// <summary>持久化输入只接受合法的特质标识与正等级。</summary>
    internal bool IsValid => Ids is not null && Ids.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 128)
        && (Levels is null || Levels.All(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 128 && pair.Value >= 1));
}
