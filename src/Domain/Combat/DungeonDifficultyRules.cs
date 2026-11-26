namespace Domain.Combat;

/// <summary>
/// 追忆副本 11 档难度阶梯与双轴数值缩放规则（总纲 §30.3）。
/// 虚境 3 档（0..2，固定原始等级）：普通 / 困难 / 地狱；
/// 现世 8 阶（3..10，动态怪物等级 + 阶位继续叠乘）：凡阶 / 黄阶 / 玄阶 / 地阶 / 天阶 / 灵阶 / 圣阶 / 帝阶（对齐八品品质色）。
/// </summary>
public static class DungeonDifficultyRules
{
    public const int MinDifficulty = 0;
    public const int MaxDifficulty = 10;
    public const int TierCount = 11;

    /// <summary>现世模式的起始难度档位（d >= 3 为现世八阶）。</summary>
    public const int RealityMinDifficulty = 3;

    /// <summary>现世动态怪物等级硬上限（对齐境界 127 级上限）。</summary>
    public const int MaxMonsterLevel = 127;

    /// <summary>基准单次体力消耗（虚境·普通 d=0）。</summary>
    public const double BaseEnergyCost = 30.0;

    private static readonly int[] s_energyCosts = [30, 40, 50, 60, 80, 100, 120, 150, 180, 210, 240];
    private static readonly string[] s_names =
    [
        "虚境·普通", "虚境·困难", "虚境·地狱",
        "现世·凡阶", "现世·黄阶", "现世·玄阶", "现世·地阶", "现世·天阶", "现世·灵阶", "现世·圣阶", "现世·帝阶"
    ];

    /// <summary>规范化追忆副本难度档位至 [0, 10]。</summary>
    public static int Clamp(int difficulty) => Math.Clamp(difficulty, MinDifficulty, MaxDifficulty);

    /// <summary>判定给定难度值是否处于合法追忆副本档位 [0, 10]。</summary>
    public static bool IsValid(int difficulty) => difficulty is >= MinDifficulty and <= MaxDifficulty;

    /// <summary>判定给定难度是否属于现世模式（d >= 3，启用动态怪物等级）。</summary>
    public static bool IsReality(int difficulty) => Clamp(difficulty) >= RealityMinDifficulty;

    /// <summary>获取指定难度的单次体力消耗（30 → 240）。</summary>
    public static int EnergyCost(int difficulty) => s_energyCosts[Clamp(difficulty)];

    /// <summary>获取指定难度的中文显示名。</summary>
    public static string DisplayName(int difficulty) => s_names[Clamp(difficulty)];

    /// <summary>单次体力相对普通档的消耗倍率 S(d) = Cost(d) / 30（1.0× → 8.0×）。</summary>
    public static double EnergyCostScale(int difficulty) => EnergyCost(difficulty) / BaseEnergyCost;

    /// <summary>单点体力效率倍率 E(d) = 1 + 0.15 × d（1.00× → 2.50×）。</summary>
    public static double EnergyEfficiencyMultiplier(int difficulty) => 1.0 + 0.15 * Clamp(difficulty);

    /// <summary>单次总收益倍率 Total(d) = (Cost(d) / 30) × (1 + 0.15 × d)（1.00× → 20.00×）。</summary>
    public static double TotalRewardMultiplier(int difficulty) => EnergyCostScale(difficulty) * EnergyEfficiencyMultiplier(difficulty);

    /// <summary>概率型稀有物品单份掉率加成倍率（1 + 0.25 × d，1.00× → 3.50×）。</summary>
    public static double RareDropRateMultiplier(int difficulty) => 1.0 + 0.25 * Clamp(difficulty);

    /// <summary>生命值双轴倍率（1.30^d，检验团队 DPS 与续航压力）。</summary>
    public static double HitpointMultiplier(int difficulty) => Math.Pow(1.30, Clamp(difficulty));

    /// <summary>常规攻防与四维双轴倍率（1.15^d，适配平方减伤避免高阶完全破不了防）。</summary>
    public static double StatMultiplier(int difficulty) => Math.Pow(1.15, Clamp(difficulty));

    /// <summary>
    /// 解析现世模式下的目标基准等级（含下限保护，总纲 §30.3.2）：
    /// 虚境返回副本最低怪原始等级；现世取 max(队伍最高玩家等级, 副本最低怪原始等级) 并钳位至 [1, 127]。
    /// </summary>
    public static int ResolveTargetBaseLevel(int minDungeonMonsterLevel, int maxPartyRealmLevel, int difficulty)
    {
        var floor = Math.Clamp(minDungeonMonsterLevel, 1, MaxMonsterLevel);
        if (!IsReality(difficulty)) return floor;
        return Math.Clamp(Math.Max(maxPartyRealmLevel, floor), 1, MaxMonsterLevel);
    }

    /// <summary>
    /// 解析单个怪物在当前追忆副本难度下的实际等级（总纲 §30.3.2）：
    /// 虚境保持配表原始等级；现世将副本最低级怪物抬升至目标基准等级，其余怪物严格保留与最低级怪物的原始等级差。
    /// </summary>
    public static int ResolveDynamicMonsterLevel(int originalMonsterLevel, int minDungeonMonsterLevel, int maxPartyRealmLevel, int difficulty)
    {
        var orig = Math.Clamp(originalMonsterLevel, 1, MaxMonsterLevel);
        if (!IsReality(difficulty)) return orig;
        var floor = Math.Clamp(minDungeonMonsterLevel, 1, MaxMonsterLevel);
        var targetBase = ResolveTargetBaseLevel(floor, maxPartyRealmLevel, difficulty);
        var delta = Math.Max(0, orig - floor);
        return Math.Clamp(targetBase + delta, 1, MaxMonsterLevel);
    }

    /// <summary>
    /// 按追忆副本 11 档双轴模型缩放怪物属性，且全部缩放字段执行 Math.Ceiling 向上取整。
    /// 与 <see cref="CombatDifficultyRules.ScaleMonsterStats"/> 严守同一安全红线：
    /// 刻意不缩放普攻周期（AttackIntervalSeconds）、10 项五行元素增减伤点数及反应参数（Effects）。
    /// </summary>
    public static CombatantStats ScaleMonsterStats(CombatantStats baseStats, int difficulty)
    {
        var clamped = Clamp(difficulty);
        if (clamped == 0) return CombatDifficultyRules.CeilStats(baseStats);
        var hpMult = HitpointMultiplier(clamped);
        var statMult = StatMultiplier(clamped);
        return baseStats with
        {
            MaxHitpoints = Math.Ceiling(baseStats.MaxHitpoints * hpMult),
            AttackPower = Math.Ceiling(baseStats.AttackPower * statMult),
            Defense = Math.Ceiling(baseStats.Defense * statMult),
            SpellAttack = Math.Ceiling(baseStats.SpellAttack * statMult),
            MaxManapoints = Math.Ceiling(baseStats.MaxManapoints * statMult),
            MagicResistance = Math.Ceiling(baseStats.MagicResistance * statMult),
            Accuracy = Math.Ceiling(baseStats.Accuracy * statMult),
            Evasion = Math.Ceiling(baseStats.Evasion * statMult),
            Critical = Math.Ceiling(baseStats.Critical * statMult),
            CriticalResistance = Math.Ceiling(baseStats.CriticalResistance * statMult),
            CriticalDamagePoints = Math.Ceiling(baseStats.CriticalDamagePoints * statMult),
            BreakPower = Math.Ceiling(baseStats.BreakPower * statMult),
            ParryResolve = Math.Ceiling(baseStats.ParryResolve * statMult),
            PhysicalDamageBonusPoints = Math.Ceiling(baseStats.PhysicalDamageBonusPoints * statMult),
            SpellDamageBonusPoints = Math.Ceiling(baseStats.SpellDamageBonusPoints * statMult),
            TotalDamageBonusPoints = Math.Ceiling(baseStats.TotalDamageBonusPoints * statMult),
            BasicAttackBonusPoints = Math.Ceiling(baseStats.BasicAttackBonusPoints * statMult),
            SkillBonusPoints = Math.Ceiling(baseStats.SkillBonusPoints * statMult),
            TalismanDamageBonusPoints = Math.Ceiling(baseStats.TalismanDamageBonusPoints * statMult),
            FormationDamageBonusPoints = Math.Ceiling(baseStats.FormationDamageBonusPoints * statMult),
            PhysicalDamageReductionPoints = Math.Ceiling(baseStats.PhysicalDamageReductionPoints * statMult),
            SpellDamageReductionPoints = Math.Ceiling(baseStats.SpellDamageReductionPoints * statMult),
            DamageAbsorptionPoints = Math.Ceiling(baseStats.DamageAbsorptionPoints * statMult),
            FixedDamageBonus = Math.Ceiling(baseStats.FixedDamageBonus * statMult),
            FixedDamageReduction = Math.Ceiling(baseStats.FixedDamageReduction * statMult),
            FixedPhysicalDamageBonus = Math.Ceiling(baseStats.FixedPhysicalDamageBonus * statMult),
            FixedPhysicalDamageReduction = Math.Ceiling(baseStats.FixedPhysicalDamageReduction * statMult),
            FixedSpellDamageBonus = Math.Ceiling(baseStats.FixedSpellDamageBonus * statMult),
            FixedSpellDamageReduction = Math.Ceiling(baseStats.FixedSpellDamageReduction * statMult),
            AttackSpeedPoints = Math.Ceiling(baseStats.AttackSpeedPoints * statMult),
            ActionSpeedPoints = Math.Ceiling(baseStats.ActionSpeedPoints * statMult),
            CooldownReductionPoints = Math.Ceiling(baseStats.CooldownReductionPoints * statMult),
            ThreatBonusPoints = Math.Ceiling(baseStats.ThreatBonusPoints * statMult),
            HpRegen = Math.Ceiling(baseStats.HpRegen * statMult),
            MpRegen = Math.Ceiling(baseStats.MpRegen * statMult),
            CombatExperience = Math.Ceiling(baseStats.CombatExperience * statMult)
        };
    }
}
