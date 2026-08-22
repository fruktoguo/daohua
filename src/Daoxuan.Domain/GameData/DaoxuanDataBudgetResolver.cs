using System.Globalization;
using Daoxuan.Domain.Models;

namespace Daoxuan.Domain.GameData;

/// <summary>
/// 技能的配置数据传输对象。所有数值直接指定，仅灵力消耗通过 ManaCostRatio 系数解算。
/// ManaCostRatio = 1.0 时消耗 = ceil(该技能设计阶位基准玩家最大灵力 × 1%)。
/// </summary>
public sealed record DaoxuanAbilityConfig(
    string Id,
    string Name,
    DaoxuanCombatStyle Style = DaoxuanCombatStyle.FiveElements,
    DaoxuanDamageType DamageType = DaoxuanDamageType.Nature,
    int CooldownMilliseconds = 0,
    int CastMilliseconds = 0,
    double ManaCostRatio = 0,
    DaoxuanCombatAbilityEffectType EffectType = DaoxuanCombatAbilityEffectType.Damage,
    DaoxuanCombatAbilityTargetType TargetType = DaoxuanCombatAbilityTargetType.Enemy,
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
    IReadOnlyList<DaoxuanCombatTrigger>? DefaultTriggers = null,
    int Tier = 1);

/// <summary>
/// 装备的配置数据传输对象，支持直接填写显式 Stats 或通过 intensity + weights 自动解算。
/// </summary>
public sealed record DaoxuanEquipmentConfig(
    string Id,
    string Name,
    string Slot,
    int Tier = 1,
    int Quality = 1,
    double? Intensity = null,
    double? Budget = null,
    DaoxuanEquipmentCombatStats? Stats = null,
    DaoxuanEquipmentCombatStats? EnhancementBonus = null,
    IReadOnlyDictionary<string, double>? Weights = null,
    int AttackIntervalMilliseconds = 0,
    DaoxuanCombatStyle? Style = null,
    DaoxuanDamageType? DamageType = null);

/// <summary>
/// 消耗品增益效果配置，支持显式属性或由强度权重推导。
/// </summary>
public sealed record DaoxuanBuffEffectConfig(
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
public sealed record DaoxuanConsumableConfig(
    IReadOnlySet<DaoxuanActionType> UsableInActionTypes,
    int CooldownSeconds = 0,
    int RecoverySeconds = 0,
    double TriggerHpRatio = 0.6,
    bool TriggerAtCombatStart = false,
    long? HitpointRestore = null,
    long? ManapointRestore = null,
    IReadOnlyList<DaoxuanBuffEffectConfig>? Buffs = null,
    IReadOnlyList<DaoxuanCombatTrigger>? DefaultCombatTriggers = null,
    double? Intensity = null,
    IReadOnlyDictionary<string, double>? Weights = null);

/// <summary>
/// 物品总配置数据传输对象。
/// </summary>
public sealed record DaoxuanItemConfig(
    string Id,
    string Name,
    string Description,
    DaoxuanItemKind Kind,
    string Category,
    int Tier = 1,
    long SellPrice = 0,
    bool IsTradable = true,
    DaoxuanConsumableConfig? Consumable = null);

/// <summary>
/// 核心统一强度预算与权重解析器 (Intensity & Weights Resolver)。
/// 负责将策划/配置中的 Intensity + Weights 严格按照量化价值换算模型解算为最终的战斗定义。
/// </summary>
public static class DaoxuanDataBudgetResolver
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
    /// 获取指定阶位标准玩家的期望最大灵力值 (finalExpected.maxManapoints，源自 expected-attributes.json)。
    /// 用于 ManaCostRatio 解算：ManaCost = ceil(ExpectedMp × ManaCostRatio × 0.01)。
    /// </summary>
    public static double GetTierBaseMaxManapoints(int tier) => tier switch
    {
        1 => 57.7,
        2 => 169.13,
        3 => 1434.04,
        4 => 4991.38,
        5 => 13584.15,
        6 => 29434.57,
        7 => 70503.65,
        8 => 149352.49,
        9 => 349988.51,
        10 => 796790.52,
        11 => 2017111.06,
        12 => 4745307.35,
        _ => 57.7
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
    public static DaoxuanCombatAbilityDefinition ResolveAbility(DaoxuanAbilityConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // ManaCost = ceil(阶位基准玩家最大灵力 × ManaCostRatio × 0.01)
        var baseMp = GetTierBaseMaxManapoints(config.Tier);
        var manaCost = config.ManaCostRatio > 0
            ? (long)Math.Ceiling(baseMp * config.ManaCostRatio * 0.01)
            : 0L;

        return new DaoxuanCombatAbilityDefinition
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
    public static DaoxuanCombatEquipmentDefinition ResolveEquipment(DaoxuanEquipmentConfig config)
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
        var enhancementBonus = config.EnhancementBonus ?? new DaoxuanEquipmentCombatStats(
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

        return new DaoxuanCombatEquipmentDefinition
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
    public static DaoxuanItemDefinition ResolveItem(DaoxuanItemConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        DaoxuanConsumableDefinition? consumable = null;
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

            consumable = new DaoxuanConsumableDefinition
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

        return new DaoxuanItemDefinition
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

    private static DaoxuanBuffEffectDefinition ResolveBuff(DaoxuanBuffEffectConfig config)
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

        return new DaoxuanBuffEffectDefinition
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

    private static DaoxuanEquipmentCombatStats ResolveEquipmentStats(
        double totalBudget,
        IReadOnlyDictionary<string, double> weights,
        int attackIntervalMilliseconds,
        DaoxuanCombatStyle? style,
        DaoxuanDamageType? damageType)
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

        return new DaoxuanEquipmentCombatStats(
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

    /// <summary>
    /// 标准玩家期望属性数据模型（源自 expected-attributes.json）。
    /// </summary>
    public sealed record FinalExpectedPlayerAttributes(
        double MaxHitpoints,
        double MaxManapoints,
        double PhysicalAttack,
        double MagicAttack,
        double Armor,
        double MagicResistance,
        double AccuracyRating,
        double EvasionRating,
        double CriticalRating,
        double CriticalAvoidance,
        double BreakRating,
        double ParryRating);

    /// <summary>
    /// 获取指定阶位标准玩家的期望属性矩阵（源自 expected-attributes.json）。
    /// </summary>
    public static FinalExpectedPlayerAttributes GetTierExpectedAttributes(int tier) => tier switch
    {
        1 => new(115.4, 57.7, 11.54, 5.77, 11.54, 0.0, 1.15, 1.15, 1.15, 1.15, 1.15, 1.15),
        2 => new(435.97, 169.13, 28.19, 13.15, 24.43, 0.0, 5.64, 1.88, 1.88, 1.88, 1.88, 1.88),
        3 => new(1187.48, 1434.04, 75.48, 67.93, 57.86, 35.22, 17.61, 10.06, 7.55, 5.03, 5.03, 5.03),
        4 => new(3944.69, 4991.38, 220.73, 238.53, 163.77, 145.97, 42.72, 28.48, 21.36, 14.24, 14.24, 14.24),
        5 => new(10612.31, 13584.15, 568.87, 652.24, 407.03, 416.84, 93.18, 73.56, 63.75, 39.23, 34.33, 34.33),
        6 => new(32145.83, 29434.57, 1362.58, 1612.85, 959.37, 1056.70, 194.65, 166.85, 152.94, 97.33, 90.38, 83.42),
        7 => new(90413.46, 70503.65, 3456.72, 4141.33, 2401.75, 2760.89, 437.70, 392.81, 370.36, 280.58, 258.13, 224.46),
        8 => new(202546.05, 149352.49, 7479.80, 8993.16, 5166.28, 6036.03, 869.74, 800.16, 765.37, 626.22, 591.43, 521.85),
        9 => new(492839.74, 349988.51, 17773.56, 21448.77, 12230.62, 14459.85, 1958.10, 1837.61, 1777.36, 1506.23, 1445.98, 1325.49),
        10 => new(1151983.81, 796790.52, 40884.82, 49484.02, 28063.26, 33469.94, 4325.35, 4067.88, 3964.90, 3501.47, 3398.49, 3192.52),
        11 => new(2969608.08, 2017111.06, 104250.93, 126455.28, 71392.47, 85631.13, 10654.11, 10156.25, 9957.11, 9060.97, 8861.83, 8363.97),
        12 => new(7076635.75, 4745307.35, 246495.29, 299471.75, 168760.07, 203167.46, 24576.71, 23666.46, 23302.36, 21663.91, 21117.76, 20207.52),
        _ => new(115.4, 57.7, 11.54, 5.77, 11.54, 0.0, 1.15, 1.15, 1.15, 1.15, 1.15, 1.15)
    };

    /// <summary>
    /// 将怪物配置解析为战斗定义。
    /// 支持基于阶位/等级期望属性与权重的自动推导（小怪80%/300%HP，精英120%/500%HP，Boss 150%/2000%HP）；
    /// 支持显式覆盖；特殊高级属性直接指定。
    /// </summary>
    public static DaoxuanCombatantDefinition ResolveCombatant(
        DaoxuanCombatantConfig config,
        IReadOnlyDictionary<string, DaoxuanCombatAbilityDefinition> abilities)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(abilities);

        var tier = config.Tier > 0 ? config.Tier : Math.Max(1, (config.Level - 1) / 12 + 1);
        var expected = GetTierExpectedAttributes(tier);
        var (statMultiplier, hpMultiplier) = config.Rank?.ToLowerInvariant() switch
        {
            "boss" => (1.50, 20.00),
            "elite" => (1.20, 5.00),
            _ => (0.80, 3.00)
        };

        var weights = config.Weights;
        double GetW(string[] keys, double defaultWeight)
        {
            if (weights is null || weights.Count == 0) return defaultWeight;
            foreach (var key in keys)
            {
                if (weights.TryGetValue(key, out var val)) return val;
            }
            return defaultWeight;
        }

        bool hasWeights = weights is { Count: > 0 };

        // 基础战斗属性解析
        long maxHitpoints = config.MaxHitpoints ?? (long)Math.Round(expected.MaxHitpoints * GetW(["hp", "maxHitpoints", "hitpoints"], 1.0) * statMultiplier * hpMultiplier);
        long maxManapoints = config.MaxManapoints ?? (long)Math.Round(expected.MaxManapoints * GetW(["mp", "maxManapoints", "manapoints"], 1.0) * statMultiplier);

        var isMagic = config.Style == DaoxuanCombatStyle.FiveElements || config.Style == DaoxuanCombatStyle.Talisman;
        var baseAttackExpected = isMagic ? expected.MagicAttack : expected.PhysicalAttack;
        var attackWeight = GetW(isMagic ? ["ap", "magicAttack", "attack"] : ["ad", "physicalAttack", "maxDamage", "attack"], 1.0);
        long maxDamage = config.MaxDamage ?? (long)Math.Round(baseAttackExpected * attackWeight * statMultiplier);

        var defAtkWeight = GetW(["defensiveMaxDamage", "defensiveAttack", "defensivePhysicalAttack"], attackWeight * 0.7);
        long defensiveMaxDamage = config.DefensiveMaxDamage ?? (long)Math.Round(baseAttackExpected * defAtkWeight * statMultiplier);

        double armor = config.Armor ?? Math.Round(expected.Armor * GetW(["armor", "defense"], 1.0) * statMultiplier, 2);

        var baseMRes = expected.MagicResistance > 0 ? expected.MagicResistance : expected.Armor;
        double waterResistance = config.WaterResistance ?? Math.Round(baseMRes * GetW(["waterResistance", "mres", "magicResistance"], 0) * statMultiplier, 2);
        double natureResistance = config.NatureResistance ?? Math.Round(baseMRes * GetW(["natureResistance", "mres", "magicResistance"], 0) * statMultiplier, 2);
        double fireResistance = config.FireResistance ?? Math.Round(baseMRes * GetW(["fireResistance", "mres", "magicResistance"], 0) * statMultiplier, 2);

        double accuracyRating = config.AccuracyRating ?? Math.Round(expected.AccuracyRating * GetW(["hit", "accuracyRating", "accuracy"], 1.0) * statMultiplier, 2);
        double evasionRating = config.EvasionRating ?? Math.Round(expected.EvasionRating * GetW(["eva", "evasionRating", "evasion"], 1.0) * statMultiplier, 2);
        double criticalRating = config.CriticalRating ?? (hasWeights ? Math.Round(expected.CriticalRating * GetW(["crit", "critical", "criticalRating"], 1.0) * statMultiplier, 2) : 0);
        double criticalAvoidance = config.CriticalAvoidance != 0 ? config.CriticalAvoidance : (hasWeights ? Math.Round(expected.CriticalAvoidance * GetW(["critavoid", "criticalAvoidance", "cavoid"], 1.0) * statMultiplier, 2) : 0);
        double breakRating = config.BreakRating != 0 ? config.BreakRating : (hasWeights ? Math.Round(expected.BreakRating * GetW(["break", "break_p", "breakRating"], 1.0) * statMultiplier, 2) : 0);
        double parryRating = config.ParryRating != 0 ? config.ParryRating : (hasWeights ? Math.Round(expected.ParryRating * GetW(["parry", "parryRating"], 1.0) * statMultiplier, 2) : 0);
        double hpRegen = config.HpRegen ?? (hasWeights ? Math.Round(Math.Max(1.0, maxHitpoints * 0.02 * GetW(["hpregen", "regen", "hp_regen"], 1.0)), 2) : Math.Max(1.0, Math.Round(maxHitpoints * 0.02)));
        double mpRegen = config.MpRegen ?? (maxManapoints > 0 ? (hasWeights ? Math.Round(Math.Max(1.0, maxManapoints * 0.02 * GetW(["mpregen", "regen", "mp_regen"], 1.0)), 2) : Math.Max(1.0, Math.Round(maxManapoints * 0.02))) : 0.0);
        double experienceMultiplier = config.ExperienceMultiplier ?? 1.0;

        // 技能列表解析
        var resolvedAbilities = new List<DaoxuanCombatAbilityDefinition>();
        if (config.Skills is { Count: > 0 })
        {
            foreach (var skill in config.Skills)
            {
                if (abilities.TryGetValue(skill.AbilityId, out var baseAbility))
                {
                    resolvedAbilities.Add(new DaoxuanCombatAbilityDefinition
                    {
                        Id = baseAbility.Id,
                        Name = baseAbility.Name,
                        Style = baseAbility.Style,
                        DamageType = baseAbility.DamageType,
                        CooldownMilliseconds = baseAbility.CooldownMilliseconds,
                        CastMilliseconds = baseAbility.CastMilliseconds,
                        ManaCost = baseAbility.ManaCost,
                        EffectType = baseAbility.EffectType,
                        TargetType = baseAbility.TargetType,
                        DamageRatio = baseAbility.DamageRatio + Math.Max(0, skill.Level - 1) * baseAbility.DamageRatioLevelBonus,
                        FlatDamage = baseAbility.FlatDamage + (long)(Math.Max(0, skill.Level - 1) * baseAbility.FlatDamageLevelBonus),
                        DamageRatioLevelBonus = baseAbility.DamageRatioLevelBonus,
                        FlatDamageLevelBonus = baseAbility.FlatDamageLevelBonus,
                        HealingRatio = baseAbility.HealingRatio + Math.Max(0, skill.Level - 1) * baseAbility.HealingRatioLevelBonus,
                        FlatHealing = baseAbility.FlatHealing + (long)(Math.Max(0, skill.Level - 1) * baseAbility.FlatHealingLevelBonus),
                        HealingRatioLevelBonus = baseAbility.HealingRatioLevelBonus,
                        FlatHealingLevelBonus = baseAbility.FlatHealingLevelBonus,
                        DamageOverTimeRatio = baseAbility.DamageOverTimeRatio,
                        DamageOverTimeMilliseconds = baseAbility.DamageOverTimeMilliseconds,
                        DamageOverTimeTickMilliseconds = baseAbility.DamageOverTimeTickMilliseconds,
                        StunChance = baseAbility.StunChance,
                        StunMilliseconds = baseAbility.StunMilliseconds,
                        BlindChance = baseAbility.BlindChance,
                        BlindMilliseconds = baseAbility.BlindMilliseconds,
                        SilenceChance = baseAbility.SilenceChance,
                        SilenceMilliseconds = baseAbility.SilenceMilliseconds,
                        PenetrationRatio = baseAbility.PenetrationRatio,
                        ArmorDamageRatio = baseAbility.ArmorDamageRatio,
                        HpDrainRatio = baseAbility.HpDrainRatio,
                        PierceChance = baseAbility.PierceChance,
                        TriggerHpRatio = skill.TriggerHpRatio > 0 ? skill.TriggerHpRatio : baseAbility.TriggerHpRatio,
                        DefaultTriggers = baseAbility.DefaultTriggers
                    });
                }
            }
        }

        return new DaoxuanCombatantDefinition
        {
            Id = config.Id,
            Name = config.Name,
            Visual = config.Visual is { Source.Length: > 0 }
                ? config.Visual
                : new DaoxuanCharacterVisualDefinition { Renderer = "text", Source = config.Name[..1] },
            Style = config.Style,
            DamageType = config.DamageType,
            MaxHitpoints = maxHitpoints,
            MaxManapoints = maxManapoints,
            AttackIntervalMilliseconds = config.AttackIntervalMilliseconds > 0 ? config.AttackIntervalMilliseconds : 1000,
            AccuracyRating = accuracyRating,
            EvasionRating = evasionRating,
            MaxDamage = maxDamage,
            DefensiveMaxDamage = defensiveMaxDamage,
            Armor = armor,
            WaterResistance = waterResistance,
            NatureResistance = natureResistance,
            FireResistance = fireResistance,
            Tenacity = config.Tenacity,
            PierceChance = config.PierceChance,
            CurseRatio = config.CurseRatio,
            FuryRatio = config.FuryRatio,
            WeakenRatio = config.WeakenRatio,
            LifeStealRatio = config.LifeStealRatio,
            ArmorDamageRatio = config.ArmorDamageRatio,
            HpDrainRatio = config.HpDrainRatio,
            CriticalRating = criticalRating,
            CriticalChance = config.CriticalChance,
            CriticalDamageRatio = config.CriticalDamageRatio,
            CriticalAvoidance = criticalAvoidance,
            DamageAbsorptionRatio = config.DamageAbsorptionRatio,
            BreakRating = breakRating,
            ParryRating = parryRating,
            SkillIntensity = config.SkillIntensity,
            DynamicResistanceFactorK = config.DynamicResistanceFactorK,
            ActionSpeedReductionRatio = config.ActionSpeedReductionRatio,
            CooldownReductionRatio = config.CooldownReductionRatio,
            HpRegen = hpRegen,
            MpRegen = mpRegen,
            HpRegenPerTenSeconds = config.HpRegenPerTenSeconds,
            MpRegenPerTenSeconds = config.MpRegenPerTenSeconds,
            EnrageTimeMilliseconds = config.EnrageTimeMilliseconds,
            AttackLevel = config.Level > 0 ? config.Level : 1,
            ExperienceMultiplier = experienceMultiplier,
            Experience = config.Experience,
            SpiritMin = config.SpiritMin,
            SpiritMax = config.SpiritMax,
            DropTable = config.DropTable ?? [],
            RareDropTable = config.RareDropTable ?? [],
            Abilities = resolvedAbilities
        };
    }
}
