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
}
