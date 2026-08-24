// ==============================================================================
// 文件名称: CombatCatalog.cs
// 主要职责: 集中检索版本化战斗技能、装备、怪物与副本遭遇定义，并提供强化与装备属性聚合计算。
// 核心逻辑 / 定义内容:
//   1. 技能、装备、遭遇等战斗静态数据原型的检索与等级动态数值计算（GetAbilityAtLevel）。
//   2. 装备强化加成计算（CalculateEnhancementMultipliers）与全身装备属性聚合（AggregateEquipmentAttributes）。
//   3. 装备强化消耗材料与成功概率计算（GetEnhancementCost）。
// ==============================================================================

using Domain.GameData;
using Domain.Models;


namespace Domain.Simulation;

/// <summary>
/// 战斗配置目录，集中检索版本化技能、装备、怪物与副本遭遇定义，并提供强化倍率与装备属性聚合计算。
/// </summary>
public static class CombatCatalog
{
    /// <summary>获取全部战斗技能原型列表。</summary>
    public static IReadOnlyCollection<CombatAbilityDefinition> AllAbilities =>
        GameDataCatalog.Current.Abilities.Values.ToArray();

    /// <summary>获取全部战斗装备原型列表。</summary>
    public static IReadOnlyCollection<CombatEquipmentDefinition> AllEquipment =>
        GameDataCatalog.Current.Equipment.Values.ToArray();

    /// <summary>获取全部战斗遭遇副本原型列表。</summary>
    public static IReadOnlyCollection<CombatEncounterDefinition> AllEncounters =>
        GameDataCatalog.Current.Encounters.Values.ToArray();

    /// <summary>
    /// 根据技能 ID 获取技能静态定义原型。
    /// </summary>
    /// <param name="abilityId">技能唯一 ID。</param>
    /// <returns>技能原型对象。</returns>
    /// <exception cref="KeyNotFoundException">当技能 ID 不存在时抛出。</exception>
    public static CombatAbilityDefinition GetAbility(string abilityId)
    {
        return GameDataCatalog.Current.Abilities.TryGetValue(abilityId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"未知战斗能力: {abilityId}");
    }

    /// <summary>
    /// 获取指定等级计算加成后的技能数值定义。
    /// </summary>
    /// <param name="abilityId">技能唯一 ID。</param>
    /// <param name="level">技能等级（1 ~ N）。</param>
    /// <returns>计算了等级伤害/治疗加成后的技能定义副本。</returns>
    public static CombatAbilityDefinition GetAbilityAtLevel(string abilityId, int level)
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

        // 根据等级差值累加伤害倍率、固定伤害及治疗加成
        var levelOffset = normalizedLevel - 1;
        return new CombatAbilityDefinition
        {
            Id = definition.Id,
            Name = definition.Name,
            Style = definition.Style,
            Category = definition.Category,
            Element = definition.Element,
            CooldownMilliseconds = definition.CooldownMilliseconds,
            CastMilliseconds = definition.CastMilliseconds,
            ManaCost = definition.ManaCost,
            EffectType = definition.EffectType,
            TargetType = definition.TargetType,
            DamageRatio = definition.DamageRatio + levelOffset * definition.DamageRatioLevelBonus,
            FlatDamage = Math.Max(0, Math.Floor(definition.FlatDamage + levelOffset * definition.FlatDamageLevelBonus)),
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
            TriggerHpRatio = definition.TriggerHpRatio,
            DefaultTriggers = definition.DefaultTriggers
        };
    }

    /// <summary>
    /// 尝试根据技能 ID 获取技能定义。
    /// </summary>
    public static bool TryGetAbility(string? abilityId, out CombatAbilityDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(abilityId))
        {
            definition = null!;
            return false;
        }

        return GameDataCatalog.Current.Abilities.TryGetValue(abilityId, out definition!);
    }

    /// <summary>
    /// 尝试根据装备物品 ID 获取战斗装备原型定义。
    /// </summary>
    public static bool TryGetEquipment(string? itemId, out CombatEquipmentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            definition = null!;
            return false;
        }

        return GameDataCatalog.Current.Equipment.TryGetValue(itemId, out definition!);
    }

    /// <summary>
    /// 获取指定强化等级对应的累计属性加成总倍率。
    /// </summary>
    /// <param name="enhancementLevel">强化等级（0 ~ 20）。</param>
    /// <returns>强化总倍率系数。</returns>
    /// <exception cref="ArgumentOutOfRangeException">当等级超出 0~20 范围时抛出。</exception>
    public static double GetEnhancementTotalBonusMultiplier(int enhancementLevel)
    {
        if (enhancementLevel < 0 || enhancementLevel >= CombatConstants.EnhancementTotalBonusMultipliers.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(enhancementLevel), "强化等级必须在 0 到 20 之间。");
        }

        return CombatConstants.EnhancementTotalBonusMultipliers[enhancementLevel];
    }

    /// <summary>
    /// 将玩家当前所有穿戴装备的基础属性与强化增量聚合累加为最终装备属性修饰。
    /// </summary>
    /// <param name="equippedItems">穿戴的装备及强化等级列表。</param>
    /// <returns>合并后的装备战斗属性修饰对象。</returns>
    public static EquipmentCombatStats AggregateEquipmentStats(
        IReadOnlyList<EquippedItem>? equippedItems)
    {
        if (equippedItems is null || equippedItems.Count == 0) return new EquipmentCombatStats();

        double accuracy = 0;
        double damage = 0;
        double attackSpeed = 0;
        double evasion = 0;
        double armor = 0;
        double magicResistance = 0;
        double metalResistance = 0;
        double woodResistance = 0;
        double waterResistance = 0;
        double fireResistance = 0;
        double earthResistance = 0;
        double tenacity = 0;
        double penetration = 0;
        double lifeSteal = 0;
        double armorDamage = 0;
        double hpDrain = 0;
        double criticalChance = 0;
        double criticalDamageRatio = 0;
        double criticalAvoidance = 0;
        double damageAbsorptionRatio = 0;
        double breakRating = 0;
        double parryRating = 0;
        double skillIntensity = 0;
        double actionSpeedRatio = 0;
        double cooldownReductionRatio = 0;
        var attackInterval = 0;
        CombatStyle? style = null;
        DamageCategory? category = null;
        Element? element = null;

        // 统一按 Slot 枚举值升序做稳定排序后再遍历，确保流派/元素覆盖具有跨端重放确定性
        var sortedEquipped = equippedItems
            .Select(equipped =>
            {
                if (!TryGetEquipment(equipped.ItemId, out var definition))
                {
                    throw new KeyNotFoundException($"未知战斗装备: {equipped.ItemId}");
                }
                return (Equipped: equipped, Definition: definition);
            })
            .OrderBy(x => (int)x.Definition.Slot);

        // 逐件装备累计基础属性与强化成长
        foreach (var (equipped, definition) in sortedEquipped)
        {
            var multiplier = GetEnhancementTotalBonusMultiplier(equipped.EnhancementLevel);
            var stats = definition.Stats;
            var bonus = definition.EnhancementBonus;
            accuracy += stats.AccuracyRatio + multiplier * bonus.AccuracyRatio;
            damage += stats.DamageRatio + multiplier * bonus.DamageRatio;
            attackSpeed += stats.AttackSpeedRatio + multiplier * bonus.AttackSpeedRatio;
            evasion += stats.EvasionRatio + multiplier * bonus.EvasionRatio;
            armor += stats.Armor + multiplier * bonus.Armor;
            magicResistance += stats.MagicResistance + multiplier * bonus.MagicResistance;
            metalResistance += stats.MetalResistance + multiplier * bonus.MetalResistance;
            woodResistance += stats.WoodResistance + multiplier * bonus.WoodResistance;
            waterResistance += stats.WaterResistance + multiplier * bonus.WaterResistance;
            fireResistance += stats.FireResistance + multiplier * bonus.FireResistance;
            earthResistance += stats.EarthResistance + multiplier * bonus.EarthResistance;
            tenacity += stats.Tenacity + multiplier * bonus.Tenacity;
            penetration += stats.PenetrationRatio + multiplier * bonus.PenetrationRatio;
            lifeSteal += stats.LifeStealRatio + multiplier * bonus.LifeStealRatio;
            armorDamage += stats.ArmorDamageRatio + multiplier * bonus.ArmorDamageRatio;
            hpDrain += stats.HpDrainRatio + multiplier * bonus.HpDrainRatio;
            criticalChance += stats.CriticalChance + multiplier * bonus.CriticalChance;
            criticalDamageRatio += stats.CriticalDamageRatio + multiplier * bonus.CriticalDamageRatio;
            criticalAvoidance += stats.CriticalAvoidance + multiplier * bonus.CriticalAvoidance;
            damageAbsorptionRatio += stats.DamageAbsorptionRatio + multiplier * bonus.DamageAbsorptionRatio;
            breakRating += stats.BreakRating + multiplier * bonus.BreakRating;
            parryRating += stats.ParryRating + multiplier * bonus.ParryRating;
            skillIntensity += stats.SkillIntensity + multiplier * bonus.SkillIntensity;
            actionSpeedRatio += stats.ActionSpeedRatio + multiplier * bonus.ActionSpeedRatio;
            cooldownReductionRatio += stats.CooldownReductionRatio + multiplier * bonus.CooldownReductionRatio;
            if (stats.AttackIntervalMilliseconds > 0)
            {
                attackInterval = stats.AttackIntervalMilliseconds;
                style = stats.Style;
                category = stats.Category;
                element = stats.Element;
            }
        }

        return new EquipmentCombatStats(
            accuracy,
            damage,
            attackSpeed,
            evasion,
            armor,
            magicResistance,
            metalResistance,
            woodResistance,
            waterResistance,
            fireResistance,
            earthResistance,
            tenacity,
            penetration,
            Math.Max(0, lifeSteal),
            Math.Max(0, armorDamage),
            Math.Max(0, hpDrain),
            Math.Clamp(criticalChance, 0, 1),
            Math.Max(0, criticalDamageRatio),
            Math.Clamp(criticalAvoidance, 0, 1),
            Math.Clamp(damageAbsorptionRatio, 0, 1),
            Math.Max(0, breakRating),
            Math.Max(0, parryRating),
            Math.Max(0, skillIntensity),
            Math.Max(0, actionSpeedRatio),
            Math.Max(0, cooldownReductionRatio),
            attackInterval,
            style,
            category,
            element);
    }

    /// <summary>
    /// 获取指定 ID 的战斗遭遇副本定义。
    /// </summary>
    public static CombatEncounterDefinition GetEncounter(string encounterId)
    {
        return GameDataCatalog.Current.Encounters.TryGetValue(encounterId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"未知战斗遭遇: {encounterId}");
    }

    /// <summary>
    /// 尝试根据 ID 获取战斗遭遇副本定义。
    /// </summary>
    public static bool TryGetEncounter(string? encounterId, out CombatEncounterDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(encounterId))
        {
            definition = null!;
            return false;
        }

        return GameDataCatalog.Current.Encounters.TryGetValue(encounterId, out definition!);
    }
}

/// <summary>
/// 核心内置技能 ID 常量定义。
/// </summary>
public static class CombatAbilityIds
{
    /// <summary>重劈狂刃。</summary>
    public const string HeavySlash = "ability.heavy-slash";
    /// <summary>连环绝斩。</summary>
    public const string ChainStrike = "ability.chain-strike";
    /// <summary>掌心惊雷。</summary>
    public const string PalmLightning = "ability.palm-lightning";
    /// <summary>炽炎爆裂。</summary>
    public const string BlazingBlast = "ability.blazing-blast";
    /// <summary>道门怒吼/嘲讽。</summary>
    public const string Taunt = "ability.taunt";
    /// <summary>重云护体。</summary>
    public const string HeavyClouds = "ability.heavy-clouds";
    /// <summary>青坪引幽曲。</summary>
    public const string QingpingMelody = "ability.qingping-melody";
    /// <summary>九天引雷诀。</summary>
    public const string LightningAttraction = "ability.lightning-attraction";
    /// <summary>破阵狂澜曲。</summary>
    public const string PozhenMelody = "ability.pozhen-melody";
    /// <summary>穿星一矢。</summary>
    public const string StarPiercer = "ability.star-piercer";
}

