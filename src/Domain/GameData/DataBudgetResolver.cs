// ==============================================================================
// 文件名称: DataBudgetResolver.cs
// 主要职责: 核心数值强度预算与权重解析器，负责将品阶预算（Tier Budget）与强度（Intensity）结合权重模型解算为严格量化平衡的装备、技能与怪物数据。
// 核心逻辑 / 定义内容:
//   1. 品阶基础强度预算（Tier Budget）与装备品质乘区（Quality Multipliers）定义
//   2. 装备数值解算（ResolveEquipment）：基于权重将强度预算分配给各类战斗与辅助属性
//   3. 技能效果解算（ResolveAbility）：基于预算与乘区解算技能伤害、治疗、Dot 与控制参数
//   4. 怪物基准属性（GetTierExpectedAttributes）与全品阶期望数值推导
// ==============================================================================
using System.Globalization;
using Domain.Models;

namespace Domain.GameData;


/// <summary>
/// 核心统一强度预算与权重解析器 (Intensity & Weights Resolver)。
/// 负责将策划/配置中的 Intensity + Weights 严格按照量化价值换算模型解算为最终的战斗定义。
/// </summary>
public static class DataBudgetResolver
{
    /// <summary>
    /// 获取指定品阶（1 ~ 12 阶）单件白板装备的基础强度数值预算（源自 expected-attributes.json 权威真源 pieceBasePercentage）。
    /// </summary>
    /// <param name="tier">品阶（1 ~ 12）。</param>
    /// <returns>基础强度预算数值（例如 1 阶为 20.0，对应 20% 属性增幅总和）。</returns>
    public static double GetTierBaseBudget(int tier) => tier switch
    {
        1 => 28.0,
        2 => 40.0,
        3 => 40.0,
        4 => 60.0,
        5 => 80.0,
        6 => 100.0,
        7 => 140.0,
        8 => 180.0,
        9 => 240.0,
        10 => 300.0,
        11 => 400.0,
        12 => 500.0,
        _ => 20.0 * Math.Pow(1.35, Math.Max(0, tier - 1))
    };

    /// <summary>
    /// 获取指定装备/物品品质（1 ~ 8 品）的强度放大倍率乘区。
    /// </summary>
    /// <param name="quality">品质品级（1:普通 ~ 8:神话）。</param>
    /// <returns>品质放大倍率。</returns>
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
    /// <param name="tier">品阶层次。</param>
    /// <returns>技能强度倍率。</returns>
    public static double GetSkillIntensityMultiplier(int tier) => 1.0 + Math.Max(0, tier - 1) * 0.30;

    /// <summary>
    /// 获取指定阶位标准玩家的期望最大灵力值 (finalExpected.maxManapoints，源自 expected-attributes.json)。
    /// 用于 ManaCostRatio 解算：ManaCost = ceil(ExpectedMp × ManaCostRatio × 0.01)。
    /// </summary>
    /// <param name="tier">品阶层次。</param>
    /// <returns>期望最大灵力基准值。</returns>
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
    /// 获取装备部位槽位的预算占比权重（如双手武器 35%，单手武器 20%，衣服 20%，首饰 15% 等）。
    /// </summary>
    /// <summary>
    /// 根据装备穿戴部位槽位获取其基准预算系数。
    /// 双手法器 0.35、单手/衣服 0.20、其它 0.15。
    /// </summary>
    /// <param name="slot">部位槽位枚举。</param>
    /// <returns>部位预算权重比例。</returns>
    public static double GetEquipmentSlotWeight(EquipmentSlot slot) => slot switch
    {
        EquipmentSlot.TwoHand => 0.35,
        EquipmentSlot.Weapon or EquipmentSlot.Armor => 0.20,
        _ => 0.15
    };

    /// <summary>
    /// 兼容重载：从字符串槽位获取部位预算权重比例。
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
    /// <param name="config">技能配置传输对象。</param>
    /// <returns>解算后的技能原型定义。</returns>
    public static CombatAbilityDefinition ResolveAbility(AbilityConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // ManaCost = ceil(阶位基准玩家最大灵力 × ManaCostRatio × 0.01)
        var baseMp = GetTierBaseMaxManapoints(config.Tier);
        var manaCost = config.ManaCostRatio > 0
            ? (long)Math.Ceiling(baseMp * config.ManaCostRatio * 0.01)
            : 0L;

        return new CombatAbilityDefinition
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
    /// <param name="config">装备配置传输对象。</param>
    /// <returns>解算后的装备原型定义。</returns>
    public static CombatEquipmentDefinition ResolveEquipment(EquipmentConfig config)
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
        var enhancementBonus = config.EnhancementBonus ?? new EquipmentCombatStats(
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

        return new CombatEquipmentDefinition
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
    /// <param name="config">物品配置传输对象。</param>
    /// <returns>解算后的物品原型定义。</returns>
    public static ItemDefinition ResolveItem(ItemConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        ConsumableDefinition? consumable = null;
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

            consumable = new ConsumableDefinition
            {
                UsableInActionTypes = consConfig.UsableInActionTypes,
                DurationSeconds = consConfig.DurationSeconds,
                IsContinuous = consConfig.IsContinuous || consConfig.DurationSeconds > 0,
                CultivationMultiplier = consConfig.CultivationMultiplier > 0 ? consConfig.CultivationMultiplier : 1.0,
                GatheringMultiplier = consConfig.GatheringMultiplier > 0 ? consConfig.GatheringMultiplier : 1.0,
                AlchemyMultiplier = consConfig.AlchemyMultiplier > 0 ? consConfig.AlchemyMultiplier : 1.0,
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

        return new ItemDefinition
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

    private static BuffEffectDefinition ResolveBuff(BuffEffectConfig config)
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

        return new BuffEffectDefinition
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

    private static EquipmentCombatStats ResolveEquipmentStats(
        double totalBudget,
        IReadOnlyDictionary<string, double> weights,
        int attackIntervalMilliseconds,
        CombatStyle? style,
        DamageType? damageType)
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

        return new EquipmentCombatStats(
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
    /// 获取指定阶位标准玩家的期望属性矩阵（源自 expected-attributes.json）。
    /// </summary>
    /// <param name="tier">品阶层次（1 ~ 12）。</param>
    /// <returns>期望属性矩阵对象。</returns>
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
    /// 内部复用通用的 CharacterStatResolver 解析基础 14 项属性，并组装技能与掉落信息。
    /// </summary>
    /// <param name="config">怪物配置传输对象。</param>
    /// <param name="abilities">已解算的技能库字典。</param>
    /// <returns>解算后的战斗单位定义原型。</returns>
    public static CombatantDefinition ResolveCombatant(
        CombatantConfig config,
        IReadOnlyDictionary<string, CombatAbilityDefinition> abilities)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(abilities);

        var statRequest = new CharacterStatRequest
        {
            Level = config.Level > 0 ? config.Level : 1,
            Tier = config.Tier,
            Rank = config.Rank,
            Style = config.Style,
            DamageType = config.DamageType,
            Weights = config.Weights,
            Floor = true,
            MaxHitpoints = config.MaxHitpoints,
            MaxManapoints = config.MaxManapoints,
            MaxDamage = config.MaxDamage,
            DefensiveMaxDamage = config.DefensiveMaxDamage,
            Armor = config.Armor,
            WaterResistance = config.WaterResistance,
            NatureResistance = config.NatureResistance,
            FireResistance = config.FireResistance,
            AccuracyRating = config.AccuracyRating,
            EvasionRating = config.EvasionRating,
            CriticalRating = config.CriticalRating,
            CriticalAvoidance = config.CriticalAvoidance != 0 ? config.CriticalAvoidance : null,
            BreakRating = config.BreakRating != 0 ? config.BreakRating : null,
            ParryRating = config.ParryRating != 0 ? config.ParryRating : null,
            HpRegen = config.HpRegen,
            MpRegen = config.MpRegen
        };

        var stats = CharacterStatResolver.Resolve(statRequest);

        // 技能列表解析
        var resolvedAbilities = new List<CombatAbilityDefinition>();
        if (config.Skills is { Count: > 0 })
        {
            foreach (var skill in config.Skills)
            {
                if (abilities.TryGetValue(skill.AbilityId, out var baseAbility))
                {
                    resolvedAbilities.Add(new CombatAbilityDefinition
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

        return new CombatantDefinition
        {
            Id = config.Id,
            Name = config.Name,
            Visual = config.Visual is { Source.Length: > 0 }
                ? config.Visual
                : new CharacterVisualDefinition { Renderer = VisualRendererKind.Text, Source = config.Name[..1] },
            Style = config.Style,
            DamageType = config.DamageType,
            MaxHitpoints = stats.MaxHitpoints,
            MaxManapoints = stats.MaxManapoints,
            AttackIntervalMilliseconds = config.AttackIntervalMilliseconds > 0 ? config.AttackIntervalMilliseconds : 1000,
            AccuracyRating = stats.AccuracyRating,
            EvasionRating = stats.EvasionRating,
            MaxDamage = stats.MaxDamage,
            DefensiveMaxDamage = stats.DefensiveMaxDamage,
            Armor = stats.Armor,
            WaterResistance = stats.WaterResistance,
            NatureResistance = stats.NatureResistance,
            FireResistance = stats.FireResistance,
            Tenacity = config.Tenacity,
            PierceChance = config.PierceChance,
            CurseRatio = config.CurseRatio,
            FuryRatio = config.FuryRatio,
            WeakenRatio = config.WeakenRatio,
            LifeStealRatio = config.LifeStealRatio,
            ArmorDamageRatio = config.ArmorDamageRatio,
            HpDrainRatio = config.HpDrainRatio,
            CriticalRating = stats.CriticalRating,
            CriticalChance = config.CriticalChance,
            CriticalDamageRatio = config.CriticalDamageRatio,
            CriticalAvoidance = stats.CriticalAvoidance,
            DamageAbsorptionRatio = config.DamageAbsorptionRatio,
            BreakRating = stats.BreakRating,
            ParryRating = stats.ParryRating,
            SkillIntensity = config.SkillIntensity,
            DynamicResistanceFactorK = config.DynamicResistanceFactorK,
            ActionSpeedReductionRatio = config.ActionSpeedReductionRatio,
            CooldownReductionRatio = config.CooldownReductionRatio,
            HpRegen = stats.HpRegen,
            MpRegen = stats.MpRegen,
            HpRegenPerTenSeconds = config.HpRegenPerTenSeconds,
            MpRegenPerTenSeconds = config.MpRegenPerTenSeconds,
            EnrageTimeMilliseconds = config.EnrageTimeMilliseconds,
            AttackLevel = config.Level > 0 ? config.Level : 1,
            Tier = config.Tier,
            Rank = config.Rank,
            ExperienceMultiplier = config.ExperienceMultiplier ?? 1.0,
            Experience = config.Experience,
            SpiritMin = config.SpiritMin,
            SpiritMax = config.SpiritMax,
            BudgetCost = config.BudgetCost is > 0 ? config.BudgetCost.Value : 10.0,
            DropTable = config.DropTable ?? [],
            RareDropTable = config.RareDropTable ?? [],
            Abilities = resolvedAbilities
        };
    }
}
