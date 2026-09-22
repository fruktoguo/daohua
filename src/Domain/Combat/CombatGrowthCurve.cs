namespace Domain.Combat;

/// <summary>
/// 全单位统一的成长曲线单点实现：玩家（境界等级）与怪物（配表等级）共用同一组公式，
/// 杜绝两条属性管线各自实现导致数值漂移。所有输入输出均为 double。
/// </summary>
public static class CombatGrowthCurve
{
    /// <summary>成长等级上限（境界与怪物等级共用同一上限）。</summary>
    public const double MaxLevel = 127;

    /// <summary>等级成长进度：x = (clamp(level,1,127)-1)/(127-1)，平方曲线（前期缓后期陡）。</summary>
    public static double Progress(double level)
        => Math.Pow((Math.Clamp(level, 1, MaxLevel) - 1) / (MaxLevel - 1), 2);

    /// <summary>吟唱时长乘区（行动速度 ATR）：1 - 0.5·x²。</summary>
    public static double CastTimeFactor(double level) => 1 - 0.5 * Progress(level);

    /// <summary>冷却乘区（CDR）：1 - 0.75·x²。</summary>
    public static double CooldownFactor(double level) => 1 - 0.75 * Progress(level);

    /// <summary>技能强度乘区：1 + (Tier-1)×0.30。</summary>
    public static double SkillIntensity(double tier) => 1 + 0.3 * (tier - 1);

    /// <summary>暴击伤害倍率：1.5 + 3.5·x²。</summary>
    public static double CriticalMultiplier(double level) => 1.5 + 3.5 * Progress(level);

    /// <summary>普攻伤害倍率：1 + x²（100%→200% AD）。</summary>
    public static double BasicAttackRatio(double level) => 1 + Progress(level);

    /// <summary>技能等级成长：伤害/治疗倍率每级 +0.6%。</summary>
    public static double AbilityRatioGrowth(double level) => 1 + (level - 1) * 0.006;

    /// <summary>技能等级成长：固定伤害/治疗数值每级 +1.0%。</summary>
    public static double AbilityFlatGrowth(double level) => 1 + (level - 1) * 0.01;
}
