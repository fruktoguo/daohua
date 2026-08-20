namespace Xiuxian.Domain.Simulation;

/// <summary>
/// 战斗公式的无状态实现。公式与事件调度分离，便于数值版本化和边界回归测试。
/// </summary>
public static class XiuxianCombatFormula
{
    public static double CalculateHitChance(double accuracyRating, double evasionRating)
    {
        var accuracy = Math.Pow(Math.Max(0.01, accuracyRating), 1.4);
        var evasion = Math.Pow(Math.Max(0.01, evasionRating), 1.4);
        return accuracy / (accuracy + evasion);
    }

    /// <summary>
    /// 公开战斗模拟器的控制命中修正：韧性为百分比点数，而不是 0 到 1 的比例。
    /// </summary>
    public static double CalculateControlChance(double baseChance, double tenacity)
    {
        var chance = Math.Clamp(double.IsFinite(baseChance) ? baseChance : 0, 0, 1);
        var normalizedTenacity = double.IsFinite(tenacity) ? Math.Max(0, tenacity) : 0;
        return chance * 100 / (100 + normalizedTenacity);
    }

    public static double CalculatePercentage(long value, long maximum)
    {
        if (maximum <= 0) return 0;
        return Math.Clamp(value * 100d / maximum, 0, 100);
    }

    public static double CalculateEffectiveResistance(double resistance, double penetrationRatio)
    {
        return resistance >= 0
            ? resistance / (1 + Math.Max(0, penetrationRatio))
            : resistance;
    }

    public static long CalculateMitigatedDamage(
        long rolledDamage,
        double resistance,
        double penetrationRatio)
    {
        var effectiveResistance = CalculateEffectiveResistance(resistance, penetrationRatio);
        var multiplier = effectiveResistance >= 0
            ? 100d / (100d + effectiveResistance)
            : (100d - effectiveResistance) / 100d;
        return Math.Max(1, (long)Math.Ceiling(Math.Max(1, rolledDamage) * multiplier));
    }

    public static double CalculateCriticalChance(double criticalChance, double criticalAvoidance)
    {
        var chance = double.IsFinite(criticalChance) ? criticalChance : 0;
        var avoidance = double.IsFinite(criticalAvoidance) ? criticalAvoidance : 0;
        return Math.Clamp(chance - avoidance, 0, 1);
    }

    public static double ApplyCriticalDamage(double damage, double criticalDamageRatio, bool isCritical)
    {
        if (!isCritical) return Math.Max(0, damage);
        var bonus = double.IsFinite(criticalDamageRatio) ? Math.Max(0, criticalDamageRatio) : 0;
        return Math.Max(0, damage) * (1 + bonus);
    }

    public static double ApplyDamageAbsorption(double damage, double damageAbsorptionRatio)
    {
        var absorption = double.IsFinite(damageAbsorptionRatio)
            ? Math.Clamp(damageAbsorptionRatio, 0, 1)
            : 0;
        return Math.Max(0, damage) * (1 - absorption);
    }

    public static int CalculateAttackIntervalMilliseconds(
        int baseIntervalMilliseconds,
        int attackLevel,
        double attackSpeedRatio)
    {
        return Math.Max(
            100,
            (int)Math.Round(
                Math.Max(100, baseIntervalMilliseconds)
                / (1 + Math.Max(0, attackLevel) / 2_000d)
                / (1 + Math.Max(-0.99, attackSpeedRatio))));
    }

    public static int CalculateEnrageStacks(
        long encounterElapsedMilliseconds,
        int enrageTimeMilliseconds,
        int maxStacks = 10)
    {
        if (encounterElapsedMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(encounterElapsedMilliseconds));
        if (enrageTimeMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(enrageTimeMilliseconds));
        if (maxStacks < 0) throw new ArgumentOutOfRangeException(nameof(maxStacks));

        return Math.Min(
            maxStacks,
            checked((int)(encounterElapsedMilliseconds / enrageTimeMilliseconds)));
    }

    public static long CalculateRegeneration(long maxResource, double ratio)
    {
        if (maxResource < 0) throw new ArgumentOutOfRangeException(nameof(maxResource));
        if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ratio));
        }

        return Math.Max(0, (long)Math.Floor(maxResource * ratio));
    }

    /// <summary>
    /// 计算功法灵力消耗。
    /// </summary>
    public static long CalculateManaCost(long baseCost, int multiplier = XiuxianCombatConstants.DefaultManaCostMultiplier)
    {
        return Math.Max(0, checked(baseCost * multiplier));
    }

    /// <summary>
    /// 减伤计算公式：伤害 = 原始伤害 * (原始伤害 / (原始伤害 + 抗性 * 抗性减伤系数 + 系数))。
    /// </summary>
    public static long CalculateMitigatedDamageWithResistanceFactor(
        long rawDamage,
        double resistance,
        double penetrationRatio = 0,
        double resistanceFactor = XiuxianCombatConstants.DefaultResistanceFactor,
        double baseFactor = XiuxianCombatConstants.DefaultDamageBaseFactor)
    {
        if (rawDamage <= 0) return 0;
        var effectiveResistance = CalculateEffectiveResistance(resistance, penetrationRatio);
        if (effectiveResistance <= 0) return rawDamage;

        var denominator = rawDamage + effectiveResistance * resistanceFactor + baseFactor;
        if (denominator <= 0) return rawDamage;
        var multiplier = rawDamage / denominator;
        return Math.Max(1, (long)Math.Ceiling(rawDamage * multiplier));
    }

    /// <summary>
    /// 对抗概率计算公式：自身属性 / (自身属性 + 对方对抗属性 * 系数 + 系数)。
    /// 适用于：命中 vs 闪避、暴击 vs 免爆、破招 vs 拆招。
    /// </summary>
    public static double CalculateContestRate(
        double selfAttr,
        double opponentAttr,
        double factor = XiuxianCombatConstants.DefaultContestOpponentFactor,
        double baseConst = XiuxianCombatConstants.DefaultContestBaseConst)
    {
        var selfVal = Math.Max(0, selfAttr);
        var opponentVal = Math.Max(0, opponentAttr);
        var denom = selfVal + opponentVal * factor + baseConst;
        if (denom <= 0) return 0;
        return Math.Clamp(selfVal / denom, 0, 1);
    }

    /// <summary>
    /// 命中率计算 (命中 vs 闪避)。
    /// 闪避成功则不造成伤害。
    /// </summary>
    public static double CalculateHitContestRate(
        double hit,
        double evasion,
        double factor = XiuxianCombatConstants.DefaultContestOpponentFactor,
        double baseConst = XiuxianCombatConstants.DefaultContestBaseConst)
    {
        return CalculateContestRate(hit, evasion, factor, baseConst);
    }

    /// <summary>
    /// 暴击率计算 (暴击 vs 免爆)。
    /// </summary>
    public static double CalculateCritContestRate(
        double crit,
        double critAvoid,
        double factor = XiuxianCombatConstants.DefaultContestOpponentFactor,
        double baseConst = XiuxianCombatConstants.DefaultContestBaseConst)
    {
        return CalculateContestRate(crit, critAvoid, factor, baseConst);
    }

    /// <summary>
    /// 破招判定：当攻击者破招 > 被攻击者拆招时计算破招概率。
    /// 破招成功：命中与暴击效果翻倍 (× BreakSuccessEffectMultiplier)。
    /// </summary>
    public static bool CheckBreakSuccess(
        double attackerBreak,
        double defenderParry,
        double rollRatio,
        double factor = XiuxianCombatConstants.DefaultContestOpponentFactor,
        double baseConst = XiuxianCombatConstants.DefaultContestBaseConst)
    {
        if (attackerBreak <= defenderParry) return false;
        var rate = CalculateContestRate(attackerBreak, defenderParry, factor, baseConst);
        return rollRatio < rate;
    }

    /// <summary>
    /// 拆招判定：当被攻击者拆招 > 攻击者破招时计算拆招概率。
    /// 拆招成功：双抗与闪避计算效果翻倍 (× ParrySuccessEffectMultiplier)。
    /// </summary>
    public static bool CheckParrySuccess(
        double defenderParry,
        double attackerBreak,
        double rollRatio,
        double factor = XiuxianCombatConstants.DefaultContestOpponentFactor,
        double baseConst = XiuxianCombatConstants.DefaultContestBaseConst)
    {
        if (defenderParry <= attackerBreak) return false;
        var rate = CalculateContestRate(defenderParry, attackerBreak, factor, baseConst);
        return rollRatio < rate;
    }
}
