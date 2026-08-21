using System.Globalization;
using Xiuxian.Domain.Models;

namespace Xiuxian.Domain.GameData;

/// <summary>
/// 技能的配置数据传输对象。所有数值直接指定，仅灵力消耗通过 ManaCostRatio 系数解算。
/// ManaCostRatio = 1.0 时消耗 = ceil(该技能设计阶位基准玩家最大灵力 × 1%)。
/// </summary>
public sealed record XiuxianAbilityConfig(
    string Id,
    string Name,
    XiuxianCombatStyle Style = XiuxianCombatStyle.FiveElements,
    XiuxianDamageType DamageType = XiuxianDamageType.Nature,
    int CooldownMilliseconds = 0,
    int CastMilliseconds = 0,
    double ManaCostRatio = 0,
    XiuxianCombatAbilityEffectType EffectType = XiuxianCombatAbilityEffectType.Damage,
    XiuxianCombatAbilityTargetType TargetType = XiuxianCombatAbilityTargetType.Enemy,
    double DamageRatio = 1.0,
    long FlatDamage = 0,
    double DamageRatioLevelBonus = 0,
    double FlatDamageLevelBonus = 0,
    double HealingRatio = 0,
    long FlatHealing = 0,
    double HealingRatioLevelBonus = 0,
    double FlatHealingLevelBonus = 0,
    double DamageOverTimeRatio = 0,
    int DamageOverTimeMilliseconds = 0,
    int DamageOverTimeTickMilliseconds = 3000,
    double StunChance = 0,
    int StunMilliseconds = 0,
    double BlindChance = 0,
    int BlindMilliseconds = 0,
    double SilenceChance = 0,
    int SilenceMilliseconds = 0,
    double PenetrationRatio = 0,
    double ArmorDamageRatio = 0,
    double HpDrainRatio = 0,
    double PierceChance = 0,
    double TriggerHpRatio = 1.0,
    IReadOnlyList<XiuxianCombatTrigger>? DefaultTriggers = null,
    int Tier = 1);

/// <summary>
/// 装备的配置数据传输对象，支持直接填写显式 Stats 或通过 intensity + weights 自动解算。
/// </summary>
public sealed record XiuxianEquipmentConfig(
    string Id,
    string Name,
    string Slot,
    int Tier = 1,
    int Quality = 1,
    double? Intensity = null,
    double? Budget = null,
    XiuxianEquipmentCombatStats? Stats = null,
    XiuxianEquipmentCombatStats? EnhancementBonus = null,
    IReadOnlyDictionary<string, double>? Weights = null,
    int AttackIntervalMilliseconds = 0,
    XiuxianCombatStyle? Style = null,
    XiuxianDamageType? DamageType = null);

/// <summary>
/// 消耗品增益效果配置，支持显式属性或由强度权重推导。
/// </summary>
public sealed record XiuxianBuffEffectConfig(
    string BuffId,
    string Name,
    int DurationSeconds,
    bool Stackable = false,
    int MaxStacks = 1,
    double? QiGainMultiplier = null,
    double? AttackLevelRatio = null,
    double? DamageRatio = null,
    double? AttackSpeedRatio = null,
    double? GatheringOutputMultiplier = null,
    double? AlchemyOutputMultiplier = null,
    double? Intensity = null,
    IReadOnlyDictionary<string, double>? Weights = null);

/// <summary>
/// 消耗品配置。
/// </summary>
public sealed record XiuxianConsumableConfig(
    IReadOnlySet<XiuxianActionType> UsableInActionTypes,
    int CooldownSeconds = 0,
    int RecoverySeconds = 0,
    double TriggerHpRatio = 0.6,
    bool TriggerAtCombatStart = false,
    long? HitpointRestore = null,
    long? ManapointRestore = null,
    IReadOnlyList<XiuxianBuffEffectConfig>? Buffs = null,
    IReadOnlyList<XiuxianCombatTrigger>? DefaultCombatTriggers = null,
    double? Intensity = null,
    IReadOnlyDictionary<string, double>? Weights = null);

/// <summary>
/// 物品总配置数据传输对象。
/// </summary>
public sealed record XiuxianItemConfig(
    string Id,
    string Name,
    string Description,
    XiuxianItemKind Kind,
    string Category,
    int Tier = 1,
    long SellPrice = 0,
    bool IsTradable = true,
    XiuxianConsumableConfig? Consumable = null);

/// <summary>
/// 核心统一强度预算与权重解析器 (Intensity & Weights Resolver)。
/// 负责将策划/配置中的 Intensity + Weights 严格按照量化价值换算模型解算为最终的战斗定义。
/// </summary>
public static class XiuxianDataBudgetResolver
{
    /// <summary>
    /// 获取指定阶位的基础强度预算。
    /// </summary>
    public static double GetTierBaseBudget(int tier) => tier switch
    {
        1 => 10.0,
        2 => 25.0,
        3 => 60.0,
        4 => 130.0,
        5 => 250.0,
        6 => 450.0,
        7 => 750.0,
        8 => 1150.0,
        9 => 1700.0,
        10 => 2450.0,
        11 => 3400.0,
        12 => 4600.0,
        _ => 10.0 * Math.Pow(1.7, Math.Max(0, tier - 1))
    };

    /// <summary>
    /// 获取指定品质的乘区倍率。
    /// </summary>
    public static double GetQualityMultiplier(int quality) => quality switch
    {
        1 => 1.00,
        2 => 1.15,
        3 => 1.35,
        4 => 1.60,
        5 => 1.90,
        6 => 2.30,
        7 => 2.80,
        8 => 3.50,
        _ => 1.00
    };

    /// <summary>
    /// 获取指定阶位的技能强度乘区 (Skill Intensity Multiplier)。
    /// </summary>
    public static double GetSkillIntensityMultiplier(int tier) => 1.0 + Math.Max(0, tier - 1) * 0.30;

    /// <summary>
    /// 获取指定阶位基准玩家的最大灵力值，与 BaseFlatAnchors 锚点表一致。
    /// 用于 ManaCostRatio 解算：ManaCost = ceil(BaseMp × ManaCostRatio × 0.01)。
    /// </summary>
    public static double GetTierBaseMaxManapoints(int tier) => tier switch
    {
        1 => 50,
        2 => 90,
        3 => 570,
        4 => 1402,
        5 => 2770,
        6 => 4234,
        7 => 6282,
        8 => 8586,
        9 => 11618,
        10 => 15474,
        11 => 20258,
        12 => 26066,
        _ => 50
    };

    /// <summary>
    /// 获取装备部位槽位的预算占比权重。
    /// </summary>
    public static double GetEquipmentSlotWeight(string? slot)
    {
        if (string.IsNullOrWhiteSpace(slot)) return 0.20;
        var normalized = slot.Trim().ToLowerInvariant().Replace("_", "-");
        return normalized switch
        {
            "two-hand" or "twohand" => 0.35,
            "weapon" or "main-hand" or "mainhand" => 0.20,
            "off-hand" or "offhand" or "shield" => 0.15,
            "body" or "chest" or "armor" => 0.20,
            "head" or "helm" or "helmet" => 0.15,
            "legs" or "pants" or "boots" => 0.15,
            "ring" or "finger" => 0.15,
            "neck" or "necklace" or "amulet" => 0.15,
            _ => 0.15
        };
    }

    /// <summary>
    /// 将技能配置解析为不可变的技能定义。
    /// 所有数值直接使用配置值，仅灵力消耗通过 ManaCostRatio 系数解算。
    /// </summary>
    public static XiuxianCombatAbilityDefinition ResolveAbility(XiuxianAbilityConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // ManaCost = ceil(阶位基准玩家最大灵力 × ManaCostRatio × 0.01)
        var baseMp = GetTierBaseMaxManapoints(config.Tier);
        var manaCost = config.ManaCostRatio > 0
            ? (long)Math.Ceiling(baseMp * config.ManaCostRatio * 0.01)
            : 0L;

        return new XiuxianCombatAbilityDefinition
        {
            Id = config.Id,
            Name = config.Name,
            Style = config.Style,
            DamageType = config.DamageType,
            CooldownMilliseconds = config.CooldownMilliseconds,
            CastMilliseconds = config.CastMilliseconds,
            ManaCost = manaCost,
            EffectType = config.EffectType,
            TargetType = config.TargetType,
            DamageRatio = config.DamageRatio,
            FlatDamage = config.FlatDamage,
            DamageRatioLevelBonus = config.DamageRatioLevelBonus,
            FlatDamageLevelBonus = config.FlatDamageLevelBonus,
            HealingRatio = config.HealingRatio,
            FlatHealing = config.FlatHealing,
            HealingRatioLevelBonus = config.HealingRatioLevelBonus,
            FlatHealingLevelBonus = config.FlatHealingLevelBonus,
            DamageOverTimeRatio = config.DamageOverTimeRatio,
            DamageOverTimeMilliseconds = config.DamageOverTimeMilliseconds,
            DamageOverTimeTickMilliseconds = config.DamageOverTimeTickMilliseconds,
            StunChance = config.StunChance,
            StunMilliseconds = config.StunMilliseconds,
            BlindChance = config.BlindChance,
            BlindMilliseconds = config.BlindMilliseconds,
            SilenceChance = config.SilenceChance,
            SilenceMilliseconds = config.SilenceMilliseconds,
            PenetrationRatio = config.PenetrationRatio,
            ArmorDamageRatio = config.ArmorDamageRatio,
            HpDrainRatio = config.HpDrainRatio,
            PierceChance = config.PierceChance,
            TriggerHpRatio = config.TriggerHpRatio,
            DefaultTriggers = config.DefaultTriggers ?? []
        };
    }

    /// <summary>
    /// 将装备配置解析为不可变的装备定义。
    /// 若提供了 weights 或 intensity，则按装备槽位预算模型推导 Stats，并自动生成 50 级均摊强化增量。
    /// </summary>
    public static XiuxianCombatEquipmentDefinition ResolveEquipment(XiuxianEquipmentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var hasWeights = config.Weights is { Count: > 0 };
        var slotWeight = GetEquipmentSlotWeight(config.Slot);
        var totalBudget = config.Intensity ?? config.Budget ?? (hasWeights
            ? GetTierBaseBudget(config.Tier) * GetQualityMultiplier(config.Quality) * slotWeight * 5.0
            : 0);

        var normalizedWeights = NormalizeWeights(config.Weights);
        var stats = config.Stats ?? ResolveEquipmentStats(totalBudget, normalizedWeights, config.AttackIntervalMilliseconds, config.Style, config.DamageType);

        // 如果未显式提供强化成长，自动按 50 级均摊（+20 级刚好获得 +40% 翻倍成长当量）
        var enhancementBonus = config.EnhancementBonus ?? new XiuxianEquipmentCombatStats(
            AccuracyRatio: stats.AccuracyRatio / 50.0,
            DamageRatio: stats.DamageRatio / 50.0,
            AttackSpeedRatio: stats.AttackSpeedRatio / 50.0,
            EvasionRatio: stats.EvasionRatio / 50.0,
            Armor: stats.Armor / 50.0,
            WaterResistance: stats.WaterResistance / 50.0,
            NatureResistance: stats.NatureResistance / 50.0,
            FireResistance: stats.FireResistance / 50.0,
            Tenacity: stats.Tenacity / 50.0,
            PenetrationRatio: stats.PenetrationRatio / 50.0,
            PierceChance: stats.PierceChance / 50.0,
            CurseRatio: stats.CurseRatio / 50.0,
            FuryRatio: stats.FuryRatio / 50.0,
            WeakenRatio: stats.WeakenRatio / 50.0,
            LifeStealRatio: stats.LifeStealRatio / 50.0,
            ArmorDamageRatio: stats.ArmorDamageRatio / 50.0,
            HpDrainRatio: stats.HpDrainRatio / 50.0,
            CriticalChance: stats.CriticalChance / 50.0,
            CriticalDamageRatio: stats.CriticalDamageRatio / 50.0,
            CriticalAvoidance: stats.CriticalAvoidance / 50.0,
            DamageAbsorptionRatio: stats.DamageAbsorptionRatio / 50.0,
            BreakRating: stats.BreakRating / 50.0,
            ParryRating: stats.ParryRating / 50.0,
            SkillIntensity: stats.SkillIntensity / 50.0,
            ActionSpeedRatio: stats.ActionSpeedRatio / 50.0,
            CooldownReductionRatio: stats.CooldownReductionRatio / 50.0,
            AttackIntervalMilliseconds: 0,
            Style: null,
            DamageType: null);

        return new XiuxianCombatEquipmentDefinition
        {
            Id = config.Id,
            Name = config.Name,
            Slot = config.Slot,
            Tier = config.Tier,
            Stats = stats,
            EnhancementBonus = enhancementBonus
        };
    }

    /// <summary>
    /// 将物品配置解析为不可变的物品定义。
    /// </summary>
    public static XiuxianItemDefinition ResolveItem(XiuxianItemConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        XiuxianConsumableDefinition? consumable = null;
        if (config.Consumable is { } consConfig)
        {
            var hasConsWeights = consConfig.Weights is { Count: > 0 };
            var consBudget = consConfig.Intensity ?? (hasConsWeights ? config.Tier * 15.0 : 0);
            var normalizedConsWeights = NormalizeWeights(consConfig.Weights);

            var hpRestore = consConfig.HitpointRestore ?? (hasConsWeights && normalizedConsWeights.TryGetValue("hitpointrestore", out var wHp)
                ? (long)Math.Round(consBudget * wHp * 12.0)
                : 0L);

            var mpRestore = consConfig.ManapointRestore ?? (hasConsWeights && normalizedConsWeights.TryGetValue("manapointrestore", out var wMp)
                ? (long)Math.Round(consBudget * wMp * 8.0)
                : 0L);

            var buffs = consConfig.Buffs?.Select(ResolveBuff).ToList() ?? [];

            consumable = new XiuxianConsumableDefinition
            {
                UsableInActionTypes = consConfig.UsableInActionTypes,
                CooldownSeconds = consConfig.CooldownSeconds,
                RecoverySeconds = consConfig.RecoverySeconds,
                TriggerHpRatio = consConfig.TriggerHpRatio,
                TriggerAtCombatStart = consConfig.TriggerAtCombatStart,
                HitpointRestore = hpRestore,
                ManapointRestore = mpRestore,
                Buffs = buffs,
                DefaultCombatTriggers = consConfig.DefaultCombatTriggers ?? []
            };
        }

        return new XiuxianItemDefinition
        {
            Id = config.Id,
            Name = config.Name,
            Description = config.Description,
            Kind = config.Kind,
            Category = config.Category,
            Tier = config.Tier,
            SellPrice = config.SellPrice,
            IsTradable = config.IsTradable,
            Consumable = consumable
        };
    }

    private static XiuxianBuffEffectDefinition ResolveBuff(XiuxianBuffEffectConfig config)
    {
        var hasWeights = config.Weights is { Count: > 0 };
        var budget = config.Intensity ?? (hasWeights ? 10.0 : 0.0);
        var normalizedWeights = NormalizeWeights(config.Weights);

        var qiGain = config.QiGainMultiplier ?? (hasWeights && normalizedWeights.TryGetValue("qigainmultiplier", out var wQi)
            ? 1.0 + budget * wQi * 0.025
            : 1.0);

        var atkRatio = config.AttackLevelRatio ?? (hasWeights && normalizedWeights.TryGetValue("attacklevelratio", out var wAtk)
            ? budget * wAtk * 0.01
            : 0.0);

        var dmgRatio = config.DamageRatio ?? (hasWeights && normalizedWeights.TryGetValue("damageratio", out var wDmg)
            ? budget * wDmg * 0.01
            : 0.0);

        var atkSpdRatio = config.AttackSpeedRatio ?? (hasWeights && normalizedWeights.TryGetValue("attackspeedratio", out var wSpd)
            ? budget * wSpd * 0.01
            : 0.0);

        var gatherMult = config.GatheringOutputMultiplier ?? (hasWeights && normalizedWeights.TryGetValue("gatheringoutputmultiplier", out var wGather)
            ? 1.0 + budget * wGather * 0.05
            : 1.0);

        var alchemyMult = config.AlchemyOutputMultiplier ?? (hasWeights && normalizedWeights.TryGetValue("alchemyoutputmultiplier", out var wAlch)
            ? 1.0 + budget * wAlch * 0.025
            : 1.0);

        return new XiuxianBuffEffectDefinition
        {
            BuffId = config.BuffId,
            Name = config.Name,
            DurationSeconds = config.DurationSeconds,
            Stackable = config.Stackable,
            MaxStacks = config.MaxStacks,
            QiGainMultiplier = qiGain,
            AttackLevelRatio = atkRatio,
            DamageRatio = dmgRatio,
            AttackSpeedRatio = atkSpdRatio,
            GatheringOutputMultiplier = gatherMult,
            AlchemyOutputMultiplier = alchemyMult
        };
    }

    private static XiuxianEquipmentCombatStats ResolveEquipmentStats(
        double totalBudget,
        IReadOnlyDictionary<string, double> weights,
        int attackIntervalMilliseconds,
        XiuxianCombatStyle? style,
        XiuxianDamageType? damageType)
    {
        double accuracy = 0;
        double damage = 0;
        double attackSpeed = 0;
        double evasion = 0;
        double armor = 0;
        double waterResistance = 0;
        double natureResistance = 0;
        double fireResistance = 0;
        double tenacity = 0;
        double penetration = 0;
        double pierce = 0;
        double curse = 0;
        double fury = 0;
        double weaken = 0;
        double lifeSteal = 0;
        double armorDamage = 0;
        double hpDrain = 0;
        double criticalChance = 0;
        double criticalDamageRatio = 0;
        double criticalAvoidance = 0;
        double damageAbsorption = 0;

        if (weights.TryGetValue("accuracyratio", out var wAcc) || weights.TryGetValue("accuracy", out wAcc))
            accuracy = totalBudget * wAcc * 0.02;

        if (weights.TryGetValue("damageratio", out var wDmg) || weights.TryGetValue("damage", out wDmg))
            damage = totalBudget * wDmg * 0.02;

        if (weights.TryGetValue("attackspeedratio", out var wSpd) || weights.TryGetValue("attackspeed", out wSpd))
            attackSpeed = totalBudget * wSpd * 0.01;

        if (weights.TryGetValue("evasionratio", out var wEva) || weights.TryGetValue("evasion", out wEva))
            evasion = totalBudget * wEva * 0.02;

        if (weights.TryGetValue("armor", out var wArmor))
            armor = totalBudget * wArmor * 1.0;

        if (weights.TryGetValue("waterresistance", out var wWater) || weights.TryGetValue("waterres", out wWater))
            waterResistance = totalBudget * wWater * 1.0;

        if (weights.TryGetValue("natureresistance", out var wNature) || weights.TryGetValue("natureres", out wNature))
            natureResistance = totalBudget * wNature * 1.0;

        if (weights.TryGetValue("fireresistance", out var wFire) || weights.TryGetValue("fireres", out wFire))
            fireResistance = totalBudget * wFire * 1.0;

        if (weights.TryGetValue("allresistance", out var wAllRes) || weights.TryGetValue("allres", out wAllRes))
        {
            var res = totalBudget * wAllRes * 0.4;
            waterResistance += res;
            natureResistance += res;
            fireResistance += res;
        }

        if (weights.TryGetValue("tenacity", out var wTenacity))
            tenacity = totalBudget * wTenacity * 1.0;

        if (weights.TryGetValue("penetrationratio", out var wPen) || weights.TryGetValue("penetration", out wPen))
            penetration = totalBudget * wPen * 0.008;

        if (weights.TryGetValue("piercechance", out var wPierce) || weights.TryGetValue("pierce", out wPierce))
            pierce = Math.Clamp(totalBudget * wPierce * 0.008, 0, 1);

        if (weights.TryGetValue("curseratio", out var wCurse) || weights.TryGetValue("curse", out wCurse))
            curse = totalBudget * wCurse * 0.002;

        if (weights.TryGetValue("furyratio", out var wFury) || weights.TryGetValue("fury", out wFury))
            fury = totalBudget * wFury * 0.003;

        if (weights.TryGetValue("weakenratio", out var wWeaken) || weights.TryGetValue("weaken", out wWeaken))
            weaken = totalBudget * wWeaken * 0.003;

        if (weights.TryGetValue("lifestealratio", out var wLifeSteal) || weights.TryGetValue("lifesteal", out wLifeSteal))
            lifeSteal = totalBudget * wLifeSteal * 0.005;

        if (weights.TryGetValue("armordamageratio", out var wArmorDmg) || weights.TryGetValue("armordamage", out wArmorDmg))
            armorDamage = totalBudget * wArmorDmg * 0.005;

        if (weights.TryGetValue("hpdrainratio", out var wHpDrain) || weights.TryGetValue("hpdrain", out wHpDrain))
            hpDrain = totalBudget * wHpDrain * 0.005;

        if (weights.TryGetValue("criticalchance", out var wCrit) || weights.TryGetValue("crit", out wCrit))
            criticalChance = Math.Clamp(totalBudget * wCrit * 0.005, 0, 1);

        if (weights.TryGetValue("criticaldamageratio", out var wCritDmg) || weights.TryGetValue("critdmg", out wCritDmg))
            criticalDamageRatio = totalBudget * wCritDmg * 0.01;

        if (weights.TryGetValue("criticalavoidance", out var wCritAvoid) || weights.TryGetValue("critavoid", out wCritAvoid))
            criticalAvoidance = Math.Clamp(totalBudget * wCritAvoid * 0.005, 0, 1);

        if (weights.TryGetValue("damageabsorptionratio", out var wAbsorb) || weights.TryGetValue("absorption", out wAbsorb))
            damageAbsorption = Math.Clamp(totalBudget * wAbsorb * 0.005, 0, 1);

        return new XiuxianEquipmentCombatStats(
            AccuracyRatio: accuracy,
            DamageRatio: damage,
            AttackSpeedRatio: attackSpeed,
            EvasionRatio: evasion,
            Armor: armor,
            WaterResistance: waterResistance,
            NatureResistance: natureResistance,
            FireResistance: fireResistance,
            Tenacity: tenacity,
            PenetrationRatio: penetration,
            PierceChance: pierce,
            CurseRatio: curse,
            FuryRatio: fury,
            WeakenRatio: weaken,
            LifeStealRatio: lifeSteal,
            ArmorDamageRatio: armorDamage,
            HpDrainRatio: hpDrain,
            CriticalChance: criticalChance,
            CriticalDamageRatio: criticalDamageRatio,
            CriticalAvoidance: criticalAvoidance,
            DamageAbsorptionRatio: damageAbsorption,
            AttackIntervalMilliseconds: attackIntervalMilliseconds,
            Style: style,
            DamageType: damageType);
    }

    private static IReadOnlyDictionary<string, double> NormalizeWeights(IReadOnlyDictionary<string, double>? weights)
    {
        if (weights is null || weights.Count == 0) return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var sum = weights.Values.Where(v => v > 0).Sum();
        var factor = sum > 0 ? 1.0 / sum : 1.0;

        foreach (var (key, value) in weights)
        {
            if (value <= 0) continue;
            var cleanKey = key.Trim().ToLowerInvariant().Replace("_", "").Replace("-", "");
            result[cleanKey] = value * factor;
        }

        return result;
    }
}
