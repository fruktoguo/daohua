namespace Domain.GameData;

/// <summary>
/// 七技艺体系的固定标识与经验曲线原语。
/// 技艺经验复用境界修为曲线（<see cref="RealmProgressionCatalog.ExpToNext"/>）累计经验逐段推进：
/// 技艺从 0 级起算，L→L+1 消耗 ExpToNext[L]（0→1 级需 ExpToNext[0]）。
/// 等级只提供速度加成（每级 5%：实际耗时 = 基础耗时 / (1 + 0.05×L)），不影响配方产出。
/// </summary>
public static class TechniqueCatalog
{
    /// <summary>炼丹技艺。</summary>
    public const string Alchemy = "alchemy";
    /// <summary>炼器技艺。</summary>
    public const string Forging = "forging";
    /// <summary>制符技艺。</summary>
    public const string Talisman = "talisman";
    /// <summary>强化技艺。</summary>
    public const string Enhancement = "enhancement";
    /// <summary>阵法技艺。</summary>
    public const string Formation = "formation";
    /// <summary>采集技艺。</summary>
    public const string Gathering = "gathering";
    /// <summary>挖矿技艺。</summary>
    public const string Mining = "mining";

    private static readonly HashSet<string> KnownSet = new(StringComparer.Ordinal)
        { Alchemy, Forging, Talisman, Enhancement, Formation, Gathering, Mining };

    /// <summary>校验标识是否为七种技艺之一。</summary>
    public static bool IsKnown(string? techniqueId) => techniqueId is not null && KnownSet.Contains(techniqueId);

    /// <summary>等级对应的行动耗时折减：duration /= 1 + 0.05×L。</summary>
    public static double SpeedBonusRatio(double level) => 0.05d * Math.Max(0, level);

    /// <summary>
    /// 累计经验换算技艺等级（0 级起）：逐段消费 ExpToNext[L]，不足一段停在当前级；
    /// 配表缺失或经验无效时按 0 级计（经验仍可累计，解锁曲线后自然升段）。
    /// </summary>
    public static double LevelFor(double cumulativeExp, RealmProgressionCatalog? catalog)
    {
        if (!double.IsFinite(cumulativeExp) || cumulativeExp <= 0 || catalog is null) return 0;
        var level = 0d;
        var remaining = cumulativeExp;
        var segments = catalog.ExpToNext;
        while (level < segments.Count)
        {
            var need = segments[(int)level];
            if (need <= 0 || remaining + 1e-9 < need) break;
            remaining -= need;
            level += 1;
        }
        return level;
    }

    /// <summary>L→L+1 所需经验；配表缺失或已封顶返回 0。</summary>
    public static double ExpToNextFor(double level, RealmProgressionCatalog? catalog)
    {
        if (catalog is null || level < 0 || level >= catalog.ExpToNext.Count) return 0;
        return catalog.ExpToNext[(int)level];
    }
}
