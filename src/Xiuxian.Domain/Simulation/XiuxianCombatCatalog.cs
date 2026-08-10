using Xiuxian.Domain.GameData;
using Xiuxian.Domain.Models;

namespace Xiuxian.Domain.Simulation;

/// <summary>
/// 战斗目录只读取版本化游戏数据；运行时不保留第二份技能、装备、怪物或遭遇定义。
/// </summary>
public static class XiuxianCombatCatalog
{
    // 强化总奖励倍率属于稳定公式版本，装备基础值和单级成长仍由 JSON 配置。
    private static readonly double[] EnhancementTotalBonusMultipliers =
    [
        0, 1, 2.1, 3.3, 4.6, 6, 7.5, 9.1, 10.8, 12.6, 14.5,
        16.7, 19.2, 22, 25.1, 28.5, 32.2, 36.2, 40.5, 45.1, 50
    ];

    public static IReadOnlyCollection<XiuxianCombatAbilityDefinition> AllAbilities =>
        XiuxianGameData.Current.Abilities.Values.ToArray();

    public static IReadOnlyCollection<XiuxianCombatEquipmentDefinition> AllEquipment =>
        XiuxianGameData.Current.Equipment.Values.ToArray();

    public static IReadOnlyCollection<XiuxianCombatEncounterDefinition> AllEncounters =>
        XiuxianGameData.Current.Encounters.Values.ToArray();

    public static XiuxianCombatAbilityDefinition GetAbility(string abilityId)
    {
        return XiuxianGameData.Current.Abilities.TryGetValue(abilityId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"未知战斗能力: {abilityId}");
    }

    public static XiuxianCombatAbilityDefinition GetAbilityAtLevel(string abilityId, int level)
    {
        var definition = GetAbility(abilityId);
        var normalizedLevel = Math.Max(1, level);
        if (normalizedLevel == 1
            || (definition.DamageRatioLevelBonus == 0
                && definition.FlatDamageLevelBonus == 0
                && definition.HealingRatioLevelBonus == 0
                && definition.FlatHealingLevelBonus == 0))
        {
            return definition;
        }

        var levelOffset = normalizedLevel - 1;
        return new XiuxianCombatAbilityDefinition
        {
            Id = definition.Id,
            Name = definition.Name,
            Style = definition.Style,
            DamageType = definition.DamageType,
            CooldownMilliseconds = definition.CooldownMilliseconds,
            CastMilliseconds = definition.CastMilliseconds,
            ManaCost = definition.ManaCost,
            EffectType = definition.EffectType,
            TargetType = definition.TargetType,
            DamageRatio = definition.DamageRatio + levelOffset * definition.DamageRatioLevelBonus,
            FlatDamage = Math.Max(0, (long)Math.Floor(definition.FlatDamage + levelOffset * definition.FlatDamageLevelBonus)),
            DamageRatioLevelBonus = definition.DamageRatioLevelBonus,
            FlatDamageLevelBonus = definition.FlatDamageLevelBonus,
            HealingRatio = definition.HealingRatio + levelOffset * definition.HealingRatioLevelBonus,
            FlatHealing = Math.Max(0, (long)Math.Floor(definition.FlatHealing + levelOffset * definition.FlatHealingLevelBonus)),
            HealingRatioLevelBonus = definition.HealingRatioLevelBonus,
            FlatHealingLevelBonus = definition.FlatHealingLevelBonus,
            DamageOverTimeRatio = definition.DamageOverTimeRatio,
            DamageOverTimeMilliseconds = definition.DamageOverTimeMilliseconds,
            DamageOverTimeTickMilliseconds = definition.DamageOverTimeTickMilliseconds,
            StunChance = definition.StunChance,
            StunMilliseconds = definition.StunMilliseconds,
            BlindChance = definition.BlindChance,
            BlindMilliseconds = definition.BlindMilliseconds,
            SilenceChance = definition.SilenceChance,
            SilenceMilliseconds = definition.SilenceMilliseconds,
            PenetrationRatio = definition.PenetrationRatio,
            ArmorDamageRatio = definition.ArmorDamageRatio,
            HpDrainRatio = definition.HpDrainRatio,
            PierceChance = definition.PierceChance,
            TriggerHpRatio = definition.TriggerHpRatio,
            DefaultTriggers = definition.DefaultTriggers
        };
    }

    public static bool TryGetAbility(string? abilityId, out XiuxianCombatAbilityDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(abilityId))
        {
            definition = null!;
            return false;
        }

        return XiuxianGameData.Current.Abilities.TryGetValue(abilityId, out definition!);
    }

    public static bool TryGetEquipment(string? itemId, out XiuxianCombatEquipmentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            definition = null!;
            return false;
        }

        return XiuxianGameData.Current.Equipment.TryGetValue(itemId, out definition!);
    }

    public static double GetEnhancementTotalBonusMultiplier(int enhancementLevel)
    {
        if (enhancementLevel < 0 || enhancementLevel >= EnhancementTotalBonusMultipliers.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(enhancementLevel), "强化等级必须在 0 到 20 之间。");
        }

        return EnhancementTotalBonusMultipliers[enhancementLevel];
    }

    public static XiuxianEquipmentCombatStats AggregateEquipmentStats(
        IReadOnlyList<XiuxianEquippedItem>? equippedItems)
    {
        if (equippedItems is null || equippedItems.Count == 0) return new XiuxianEquipmentCombatStats();

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
        double damageAbsorptionRatio = 0;
        var attackInterval = 0;
        XiuxianCombatStyle? style = null;
        XiuxianDamageType? damageType = null;

        foreach (var equipped in equippedItems)
        {
            if (!TryGetEquipment(equipped.ItemId, out var definition))
            {
                throw new KeyNotFoundException($"未知战斗装备: {equipped.ItemId}");
            }

            var multiplier = GetEnhancementTotalBonusMultiplier(equipped.EnhancementLevel);
            var stats = definition.Stats;
            var bonus = definition.EnhancementBonus;
            accuracy += stats.AccuracyRatio + multiplier * bonus.AccuracyRatio;
            damage += stats.DamageRatio + multiplier * bonus.DamageRatio;
            attackSpeed += stats.AttackSpeedRatio + multiplier * bonus.AttackSpeedRatio;
            evasion += stats.EvasionRatio + multiplier * bonus.EvasionRatio;
            armor += stats.Armor + multiplier * bonus.Armor;
            waterResistance += stats.WaterResistance + multiplier * bonus.WaterResistance;
            natureResistance += stats.NatureResistance + multiplier * bonus.NatureResistance;
            fireResistance += stats.FireResistance + multiplier * bonus.FireResistance;
            tenacity += stats.Tenacity + multiplier * bonus.Tenacity;
            penetration += stats.PenetrationRatio + multiplier * bonus.PenetrationRatio;
            pierce += stats.PierceChance + multiplier * bonus.PierceChance;
            curse += stats.CurseRatio + multiplier * bonus.CurseRatio;
            fury += stats.FuryRatio + multiplier * bonus.FuryRatio;
            weaken += stats.WeakenRatio + multiplier * bonus.WeakenRatio;
            lifeSteal += stats.LifeStealRatio + multiplier * bonus.LifeStealRatio;
            armorDamage += stats.ArmorDamageRatio + multiplier * bonus.ArmorDamageRatio;
            hpDrain += stats.HpDrainRatio + multiplier * bonus.HpDrainRatio;
            criticalChance += stats.CriticalChance + multiplier * bonus.CriticalChance;
            criticalDamageRatio += stats.CriticalDamageRatio + multiplier * bonus.CriticalDamageRatio;
            criticalAvoidance += stats.CriticalAvoidance + multiplier * bonus.CriticalAvoidance;
            damageAbsorptionRatio += stats.DamageAbsorptionRatio + multiplier * bonus.DamageAbsorptionRatio;
            if (stats.AttackIntervalMilliseconds > 0)
            {
                attackInterval = stats.AttackIntervalMilliseconds;
                style = stats.Style;
                damageType = stats.DamageType;
            }
        }

        return new XiuxianEquipmentCombatStats(
            accuracy,
            damage,
            attackSpeed,
            evasion,
            armor,
            waterResistance,
            natureResistance,
            fireResistance,
            tenacity,
            penetration,
            Math.Clamp(pierce, 0, 1),
            Math.Max(0, curse),
            Math.Max(0, fury),
            Math.Max(0, weaken),
            Math.Max(0, lifeSteal),
            Math.Max(0, armorDamage),
            Math.Max(0, hpDrain),
            Math.Clamp(criticalChance, 0, 1),
            Math.Max(0, criticalDamageRatio),
            Math.Clamp(criticalAvoidance, 0, 1),
            Math.Clamp(damageAbsorptionRatio, 0, 1),
            attackInterval,
            style,
            damageType);
    }

    public static XiuxianCombatEncounterDefinition GetEncounter(string encounterId)
    {
        return XiuxianGameData.Current.Encounters.TryGetValue(encounterId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"未知战斗遭遇: {encounterId}");
    }

    public static bool TryGetEncounter(string? encounterId, out XiuxianCombatEncounterDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(encounterId))
        {
            definition = null!;
            return false;
        }

        return XiuxianGameData.Current.Encounters.TryGetValue(encounterId, out definition!);
    }
}

public static class XiuxianCombatAbilityIds
{
    public const string FiveElementsFlare = "ability.five-elements-flare";
    public const string SpiritPulse = "ability.spirit-pulse";
    public const string FlameBullet = "ability.flame-bullet";
    public const string RejuvenationArt = "ability.rejuvenation-art";
    public const string BlazingStorm = "ability.blazing-storm";
    public const string EntanglingVine = "ability.entangling-vine";
    public const string NatureVeil = "ability.nature-veil";
    public const string SilencingTalisman = "ability.silencing-talisman";
    public const string StunningMountain = "ability.stunning-mountain";
}
