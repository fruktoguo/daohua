namespace Domain.Combat;

/// <summary>
/// 讨伐难度与分层匹配规则（总纲 §7.3 / §7.10）。
/// 支持默认（0）与 T1~T5（1~5）共六档难度；
/// 斗法匹配分为默认、T1~T3、T4~T5 三个互斥区间。
/// </summary>
public static class CombatDifficultyRules
{
    public const int DefaultDifficulty = 0;
    public const int MinDifficulty = 0;
    public const int MaxDifficulty = 5;

    public const int BracketDefault = 0;
    public const int BracketT1ToT3 = 1;
    public const int BracketT4ToT5 = 2;

    /// <summary>规范化难度等级，钳位在 [0, 5] 之间。</summary>
    public static int Clamp(int difficulty) => Math.Clamp(difficulty, MinDifficulty, MaxDifficulty);

    /// <summary>获取难度对应的斗法匹配区间（0: 默认, 1: T1~T3, 2: T4~T5）。</summary>
    public static int GetBracket(int difficulty)
    {
        var clamped = Clamp(difficulty);
        return clamped switch
        {
            0 => BracketDefault,
            <= 3 => BracketT1ToT3,
            _ => BracketT4ToT5
        };
    }

    /// <summary>难度对应的全属性提升倍率（指数提升：1.25^T，T5 为 1.25^5 ≈ 3.0517578）。</summary>
    public static double AttributeMultiplier(int difficulty)
    {
        var clamped = Clamp(difficulty);
        return Math.Pow(1.25, clamped);
    }

    /// <summary>难度对应的经验提升倍率（线性提升：1 + 0.60 * T，T5 为 4.0，即提升 300%）。</summary>
    public static double ExperienceMultiplier(int difficulty)
    {
        var clamped = Clamp(difficulty);
        return 1.0 + 0.60 * clamped;
    }

    /// <summary>难度对应的掉落率提升倍率（线性提升：1 + 0.25 * T，T5 为 2.25，即提升 125%）。</summary>
    public static double DropRateMultiplier(int difficulty)
    {
        var clamped = Clamp(difficulty);
        return 1.0 + 0.25 * clamped;
    }

    /// <summary>对于基础掉率已达 100% 的掉落项，转为按此倍率提升掉落量区间。</summary>
    public static double DropQuantityMultiplier(int difficulty)
    {
        var clamped = Clamp(difficulty);
        return 1.0 + 0.25 * clamped;
    }

    /// <summary>
    /// 对战斗单位的各项数值属性向上取整（底层计算取整）。
    /// </summary>
    public static CombatantStats CeilStats(CombatantStats stats)
    {
        return stats with
        {
            MaxHitpoints = Math.Ceiling(stats.MaxHitpoints),
            AttackPower = Math.Ceiling(stats.AttackPower),
            Defense = Math.Ceiling(stats.Defense),
            SpellAttack = Math.Ceiling(stats.SpellAttack),
            MaxManapoints = Math.Ceiling(stats.MaxManapoints),
            MagicResistance = Math.Ceiling(stats.MagicResistance),
            Accuracy = Math.Ceiling(stats.Accuracy),
            Evasion = Math.Ceiling(stats.Evasion),
            Critical = Math.Ceiling(stats.Critical),
            CriticalResistance = Math.Ceiling(stats.CriticalResistance),
            CriticalDamagePoints = Math.Ceiling(stats.CriticalDamagePoints),
            BreakPower = Math.Ceiling(stats.BreakPower),
            ParryResolve = Math.Ceiling(stats.ParryResolve),
            PhysicalDamageBonusPoints = Math.Ceiling(stats.PhysicalDamageBonusPoints),
            SpellDamageBonusPoints = Math.Ceiling(stats.SpellDamageBonusPoints),
            TotalDamageBonusPoints = Math.Ceiling(stats.TotalDamageBonusPoints),
            BasicAttackBonusPoints = Math.Ceiling(stats.BasicAttackBonusPoints),
            SkillBonusPoints = Math.Ceiling(stats.SkillBonusPoints),
            PhysicalDamageReductionPoints = Math.Ceiling(stats.PhysicalDamageReductionPoints),
            SpellDamageReductionPoints = Math.Ceiling(stats.SpellDamageReductionPoints),
            DamageAbsorptionPoints = Math.Ceiling(stats.DamageAbsorptionPoints),
            FixedDamageBonus = Math.Ceiling(stats.FixedDamageBonus),
            FixedDamageReduction = Math.Ceiling(stats.FixedDamageReduction),
            FixedPhysicalDamageBonus = Math.Ceiling(stats.FixedPhysicalDamageBonus),
            FixedPhysicalDamageReduction = Math.Ceiling(stats.FixedPhysicalDamageReduction),
            FixedSpellDamageBonus = Math.Ceiling(stats.FixedSpellDamageBonus),
            FixedSpellDamageReduction = Math.Ceiling(stats.FixedSpellDamageReduction),
            AttackSpeedPoints = Math.Ceiling(stats.AttackSpeedPoints),
            ActionSpeedPoints = Math.Ceiling(stats.ActionSpeedPoints),
            CooldownReductionPoints = Math.Ceiling(stats.CooldownReductionPoints),
            ThreatBonusPoints = Math.Ceiling(stats.ThreatBonusPoints),
            HpRegen = Math.Ceiling(stats.HpRegen),
            MpRegen = Math.Ceiling(stats.MpRegen),
            CombatExperience = Math.Ceiling(stats.CombatExperience)
        };
    }

    /// <summary>
    /// 根据难度缩放怪物属性，且所有属性底层执行 Math.Ceiling 向上取整。
    /// </summary>
    public static CombatantStats ScaleMonsterStats(CombatantStats baseStats, int difficulty)
    {
        var mult = AttributeMultiplier(difficulty);
        // 普攻周期 AttackIntervalSeconds 不随属性放大而增加（保持怪物攻击节奏）
        return baseStats with
        {
            MaxHitpoints = Math.Ceiling(baseStats.MaxHitpoints * mult),
            AttackPower = Math.Ceiling(baseStats.AttackPower * mult),
            Defense = Math.Ceiling(baseStats.Defense * mult),
            SpellAttack = Math.Ceiling(baseStats.SpellAttack * mult),
            MaxManapoints = Math.Ceiling(baseStats.MaxManapoints * mult),
            MagicResistance = Math.Ceiling(baseStats.MagicResistance * mult),
            Accuracy = Math.Ceiling(baseStats.Accuracy * mult),
            Evasion = Math.Ceiling(baseStats.Evasion * mult),
            Critical = Math.Ceiling(baseStats.Critical * mult),
            CriticalResistance = Math.Ceiling(baseStats.CriticalResistance * mult),
            CriticalDamagePoints = Math.Ceiling(baseStats.CriticalDamagePoints * mult),
            BreakPower = Math.Ceiling(baseStats.BreakPower * mult),
            ParryResolve = Math.Ceiling(baseStats.ParryResolve * mult),
            PhysicalDamageBonusPoints = Math.Ceiling(baseStats.PhysicalDamageBonusPoints * mult),
            SpellDamageBonusPoints = Math.Ceiling(baseStats.SpellDamageBonusPoints * mult),
            TotalDamageBonusPoints = Math.Ceiling(baseStats.TotalDamageBonusPoints * mult),
            BasicAttackBonusPoints = Math.Ceiling(baseStats.BasicAttackBonusPoints * mult),
            SkillBonusPoints = Math.Ceiling(baseStats.SkillBonusPoints * mult),
            PhysicalDamageReductionPoints = Math.Ceiling(baseStats.PhysicalDamageReductionPoints * mult),
            SpellDamageReductionPoints = Math.Ceiling(baseStats.SpellDamageReductionPoints * mult),
            DamageAbsorptionPoints = Math.Ceiling(baseStats.DamageAbsorptionPoints * mult),
            FixedDamageBonus = Math.Ceiling(baseStats.FixedDamageBonus * mult),
            FixedDamageReduction = Math.Ceiling(baseStats.FixedDamageReduction * mult),
            FixedPhysicalDamageBonus = Math.Ceiling(baseStats.FixedPhysicalDamageBonus * mult),
            FixedPhysicalDamageReduction = Math.Ceiling(baseStats.FixedPhysicalDamageReduction * mult),
            FixedSpellDamageBonus = Math.Ceiling(baseStats.FixedSpellDamageBonus * mult),
            FixedSpellDamageReduction = Math.Ceiling(baseStats.FixedSpellDamageReduction * mult),
            AttackSpeedPoints = Math.Ceiling(baseStats.AttackSpeedPoints * mult),
            ActionSpeedPoints = Math.Ceiling(baseStats.ActionSpeedPoints * mult),
            CooldownReductionPoints = Math.Ceiling(baseStats.CooldownReductionPoints * mult),
            ThreatBonusPoints = Math.Ceiling(baseStats.ThreatBonusPoints * mult),
            HpRegen = Math.Ceiling(baseStats.HpRegen * mult),
            MpRegen = Math.Ceiling(baseStats.MpRegen * mult),
            CombatExperience = Math.Ceiling(baseStats.CombatExperience * mult)
        };
    }
}
