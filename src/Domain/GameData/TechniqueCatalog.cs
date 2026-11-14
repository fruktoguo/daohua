using System.Text.Json;
using Domain.Player;

namespace Domain.GameData;

/// <summary>
/// 七技艺体系的固定标识与经验曲线原语。
/// 技艺经验复用境界修为曲线的共享削减段表（<see cref="RealmProgressionCatalog.DiscountedExpToNext"/>，
/// 总纲 §3.2.2）累计经验逐段推进：技艺从 0 级起算，升入 L 级消费第 L−1 段削减值
/// （段目标 1 级削减 95% → 段目标 30 级削减 0%，30 级以上与修为曲线完全一致）；
/// 削减只作用于升级需求侧，修为曲线配表原值与产出端不变。
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

    /// <summary>全技艺经验全局乘数：基于行动等级与基础耗时公式整体翻倍。</summary>
    public const double GlobalExperienceMultiplier = 2.0;

    /// <summary>消耗上级装备锻造时的炼器额外乘数（与全局乘数叠算为 4× 原基准）。</summary>
    public const double EquipmentUpgradeForgingMultiplier = 2.0;

    private static readonly HashSet<string> KnownSet = new(All, StringComparer.Ordinal);

    /// <summary>校验标识是否为七种技艺之一。</summary>
    public static bool IsKnown(string? techniqueId) => techniqueId is not null && KnownSet.Contains(techniqueId);

    /// <summary>
    /// 判定行动是否属于「消耗上级装备进行锻造」的炼器配方：
    /// 行动归属 <see cref="Forging"/> 技艺且配方输入中至少存在 1 件数量 &gt; 0 的 <c>equipment.*</c> 装备
    /// （覆盖 Lv.12/16/18/20/22/24 升阶锻造，排除不耗上级装备的 Lv.1 装备/工具、Lv.9 散件与精炼材料）。
    /// </summary>
    public static bool IsEquipmentUpgradeForgingAction(ActionDefinition? action, GameDataCatalog? catalog = null)
    {
        if (action is null || !string.Equals(action.Technique, Forging, StringComparison.Ordinal))
            return false;
        foreach (var pair in action.Inputs)
        {
            if (pair.Value <= 0) continue;
            if (pair.Key.Value.StartsWith("equipment.", StringComparison.Ordinal)
                || (catalog is not null && catalog.RawEquipments.ContainsKey(pair.Key.Value)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 计算单轮生活行动、装备强化或秘法操作的技艺经验（同时作为等额底蕴授予基数）：
    /// 公式 = round(BaseExperienceForLevel(level) × (durationSeconds / 10) × GlobalExperienceMultiplier(2.0) × 炼器升阶倍率)，最低保底 1 点。
    /// </summary>
    public static double ComputeTechniqueExperience(
        GameDataCatalog? gameData,
        int level,
        double durationSeconds,
        ActionDefinition? action = null)
    {
        var normalizedLevel = Math.Max(1, level);
        var baseExp = normalizedLevel <= 1
            ? 10.0
            : (gameData?.RealmProgression?.BaseExperienceForLevel(normalizedLevel) ?? 10.0);
        var multiplier = GlobalExperienceMultiplier
            * (IsEquipmentUpgradeForgingAction(action, gameData) ? EquipmentUpgradeForgingMultiplier : 1.0);
        return Math.Max(1.0, Math.Round(baseExp * (durationSeconds / 10.0) * multiplier));
    }

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

    /// <summary>技艺前期经验削减覆盖的目标等级数：1~30 级按线性比例下调，30 级以上不削减（与共享削减段表同值）。</summary>
    public const int EarlyDiscountLevels = RealmProgressionCatalog.EarlyUpgradeDiscountLevels;

    /// <summary>
    /// 升入指定等级所需经验的削减比例：目标等级 1 → 95%，目标等级 30 → 0%，中间按
    /// <c>0.95 × (30 − L) / 29</c> 线性插值；目标等级 &gt; 30 或非法值返回 0（不削减）。
    /// 委托 <see cref="RealmProgressionCatalog.EarlyUpgradeDiscountRatio"/> 单一事实源（总纲 §3.2.2）。
    /// </summary>
    public static double EarlyDiscountRatio(double targetLevel)
        => RealmProgressionCatalog.EarlyUpgradeDiscountRatio(targetLevel);

    /// <summary>
    /// 累计经验换算技艺等级（0 级起）：逐段消费共享削减段表（<see cref="RealmProgressionCatalog.DiscountedExpToNext"/>），
    /// 不足一段停在当前级；配表缺失或经验无效时按 0 级计（经验仍可累计，解锁曲线后自然升段）。
    /// </summary>
    public static double LevelFor(double cumulativeExp, RealmProgressionCatalog? catalog)
    {
        if (!double.IsFinite(cumulativeExp) || cumulativeExp <= 0 || catalog is null) return 0;
        var level = 0d;
        var remaining = cumulativeExp;
        var segments = catalog.DiscountedExpToNext;
        while (level < segments.Count)
        {
            var need = segments[(int)level];
            if (need <= 0 || remaining + 1e-9 < need) break;
            remaining -= need;
            level += 1;
        }
        return level;
    }

    /// <summary>L→L+1 所需经验（共享削减段表第 L 段）；配表缺失或已封顶返回 0。</summary>
    public static double ExpToNextFor(double level, RealmProgressionCatalog? catalog)
    {
        if (catalog is null || level < 0 || level >= catalog.DiscountedExpToNext.Count) return 0;
        return catalog.DiscountedExpToNext[(int)level];
    }
}
