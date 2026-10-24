using System.Text.Json;
using Domain.Player;

namespace Domain.GameData;

/// <summary>
/// 七技艺体系的固定标识与经验曲线原语。
/// 技艺经验复用境界修为曲线（<see cref="RealmProgressionCatalog.ExpToNext"/>）累计经验逐段推进：
/// 技艺从 0 级起算，L→L+1 消耗该段修为条（0→1 级需第 1 段），但前 30 级按
/// 「目标等级 1 级削减 90% → 30 级削减 0%」线性下调所需经验（见 <see cref="EarlyDiscountRatio"/>），
/// 30 级以上与境界曲线完全一致；削减只作用于技艺，修为曲线本身不变。
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

    /// <summary>全部已知技艺标识列表。</summary>
    public static readonly string[] All = [Alchemy, Forging, Talisman, Enhancement, Formation, Gathering, Mining];

    private static readonly HashSet<string> KnownSet = new(All, StringComparer.Ordinal);

    /// <summary>校验标识是否为七种技艺之一。</summary>
    public static bool IsKnown(string? techniqueId) => techniqueId is not null && KnownSet.Contains(techniqueId);

    /// <summary>等级对应的行动耗时折减：duration /= 1 + 0.05×L。</summary>
    public static double SpeedBonusRatio(double level) => 0.05d * Math.Max(0, level);

    /// <summary>技艺工具专属槽位前缀：装备配表 slot 以此前缀声明绑定对应技艺的工具槽。</summary>
    public const string ToolSlotPrefix = "tool-";

    /// <summary>判定装备槽位是否为技艺工具专属槽（tool- 前缀）。</summary>
    public static bool IsToolSlot(string? slot)
        => slot is not null && slot.StartsWith(ToolSlotPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 已装备技艺工具对指定技艺的行动速度加成合计：遍历装配槽位，
    /// 装备配表 techniqueBonus.technique 命中时累加其 speedRatio × 1.05^强化等级；
    /// 与技艺等级加成同点加算（duration /= 1 + 0.05×L + Σ工具加成）。
    /// 配表缺失、字段非法或 technique 不匹配一律忽略。
    /// </summary>
    /// <param name="loadout">玩家装配分段（装备槽来源）。</param>
    /// <param name="techniqueId">行动关联的技艺标识。</param>
    /// <param name="catalog">配表（装备 techniqueBonus 来源）；为 null 时按无加成计。</param>
    public static double EquippedToolSpeedRatio(IReadOnlyList<PlayerEquipmentSlot>? equipmentSlots, string techniqueId, GameDataCatalog? catalog)
    {
        if (catalog is null || string.IsNullOrEmpty(techniqueId) || equipmentSlots is null) return 0;
        var ratio = 0d;
        foreach (var slot in equipmentSlots)
        {
            if (!catalog.RawEquipments.TryGetValue(slot.ItemId, out var equipment)
                || !equipment.TryGetProperty("techniqueBonus", out var bonus)
                || bonus.ValueKind != JsonValueKind.Object
                || !bonus.TryGetProperty("technique", out var technique)
                || technique.ValueKind != JsonValueKind.String
                || !string.Equals(technique.GetString(), techniqueId, StringComparison.Ordinal))
                continue;
            if (bonus.TryGetProperty("speedRatio", out var speedRatio)
                && speedRatio.TryGetDouble(out var value)
                && double.IsFinite(value) && value > 0)
            {
                var level = Math.Max(0, (int)Math.Floor(slot.EnhancementLevel));
                var scale = Math.Pow(1.05, level);
                ratio += value * scale;
            }
        }
        return ratio;
    }

    /// <summary>
    /// 已装备技艺工具对指定技艺的行动速度加成合计：遍历装配槽位，
    /// 装备配表 techniqueBonus.technique 命中时累加其 speedRatio；
    /// 与技艺等级加成同点加算（duration /= 1 + 0.05×L + Σ工具加成）。
    /// </summary>
    public static double EquippedToolSpeedRatio(PlayerLoadoutState loadout, string techniqueId, GameDataCatalog? catalog)
        => EquippedToolSpeedRatio(loadout.Equipment, techniqueId, catalog);

    /// <summary>技艺前期经验削减覆盖的目标等级数：1~30 级按线性比例下调，30 级以上不削减。</summary>
    public const int EarlyDiscountLevels = 30;

    /// <summary>目标等级 1 级的削减比例（90%），其后逐级线性递减至 0%。</summary>
    private const double EarlyDiscountMaxRatio = 0.90d;

    /// <summary>
    /// 升入指定等级所需经验的削减比例：目标等级 1 → 90%，目标等级 30 → 0%，中间按
    /// <c>0.9 × (30 − L) / 29</c> 线性插值；目标等级 &gt; 30 或非法值返回 0（不削减）。
    /// 只用于技艺曲线，修为曲线（<see cref="RealmProgressionCatalog.ExpToNext"/>）保持原值。
    /// </summary>
    public static double EarlyDiscountRatio(double targetLevel)
    {
        if (!(targetLevel >= 1) || targetLevel > EarlyDiscountLevels) return 0;
        return EarlyDiscountMaxRatio * (EarlyDiscountLevels - targetLevel) / (EarlyDiscountLevels - 1);
    }

    /// <summary>目标等级的单段所需经验：修为条按 <see cref="EarlyDiscountRatio"/> 削减后取整。</summary>
    private static double SegmentExp(IReadOnlyList<double> segments, int index)
    {
        var baseExp = segments[index];
        if (baseExp <= 0) return baseExp;
        var ratio = EarlyDiscountRatio(index + 1);
        return ratio <= 0 ? baseExp : Math.Round(baseExp * (1 - ratio), MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 累计经验换算技艺等级（0 级起）：逐段消费削减后的段值，不足一段停在当前级；
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
            var need = SegmentExp(segments, (int)level);
            if (need <= 0 || remaining + 1e-9 < need) break;
            remaining -= need;
            level += 1;
        }
        return level;
    }

    /// <summary>L→L+1 所需经验（已按前期削减）；配表缺失或已封顶返回 0。</summary>
    public static double ExpToNextFor(double level, RealmProgressionCatalog? catalog)
    {
        if (catalog is null || level < 0 || level >= catalog.ExpToNext.Count) return 0;
        return SegmentExp(catalog.ExpToNext, (int)level);
    }
}
