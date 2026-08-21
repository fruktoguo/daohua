namespace Daoxuan.Domain.Simulation;

/// <summary>
/// 战斗公式的无状态实现。公式与事件调度分离，便于数值版本化和边界回归测试。
/// </summary>
public static class DaoxuanCombatFormula
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
    public static long CalculateManaCost(long baseCost, int multiplier = DaoxuanCombatConstants.DefaultManaCostMultiplier)
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
        double resistanceFactor = DaoxuanCombatConstants.DefaultResistanceFactor,
        double baseFactor = DaoxuanCombatConstants.DefaultDamageBaseFactor)
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
    /// 对抗概率计算公式：自身属性 / (自身属性 + 对方对抗属性 * 系数 + 基础平滑常数)。
    /// 适用于：闪避 vs 命中、暴击 vs 免爆、破招 vs 拆招。
    /// </summary>
    public static double CalculateContestRate(
        double selfAttr,
        double opponentAttr,
        double factor = DaoxuanCombatConstants.DefaultContestOpponentFactor,
        double baseConst = DaoxuanCombatConstants.DefaultContestBaseConst)
    {
        var selfVal = Math.Max(0, selfAttr);
        var opponentVal = Math.Max(0, opponentAttr);
        var denom = selfVal + opponentVal * factor + baseConst;
        if (denom <= 0) return 0;
        return Math.Clamp(selfVal / denom, 0, 1);
    }

    /// <summary>
    /// 闪避率计算 (闪避 vs 命中)。
    /// 闪避率 = 守方闪避 / (守方闪避 + 攻方命中 * 系数 + 基础平滑常数)。
    /// 闪避成功则免除本次全部伤害。0闪避0命中时闪避率为0% (即100%命中)。
    /// </summary>
    public static double CalculateDodgeContestRate(
        double evasion,
        double hit,
        double factor = DaoxuanCombatConstants.DefaultContestOpponentFactor,
        double baseConst = DaoxuanCombatConstants.DefaultContestBaseConst)
    {
        return CalculateContestRate(evasion, hit, factor, baseConst);
    }

    /// <summary>
    /// 命中率计算 (命中 vs 闪避)。
    /// 命中率 = 1.0 - 闪避率 = (攻方命中 * 系数 + 基础平滑常数) / (守方闪避 + 攻方命中 * 系数 + 基础平滑常数)。
    /// </summary>
    public static double CalculateHitContestRate(
        double hit,
        double evasion,
        double factor = DaoxuanCombatConstants.DefaultContestOpponentFactor,
        double baseConst = DaoxuanCombatConstants.DefaultContestBaseConst)
    {
        return 1.0 - CalculateDodgeContestRate(evasion, hit, factor, baseConst);
    }

    /// <summary>
    /// 暴击率计算 (暴击 vs 免爆)。
    /// 暴击率 = 攻方暴击 / (攻方暴击 + 守方免爆 * 系数 + 基础平滑常数)。
    /// </summary>
    public static double CalculateCritContestRate(
        double crit,
        double critAvoid,
        double factor = DaoxuanCombatConstants.DefaultContestOpponentFactor,
        double baseConst = DaoxuanCombatConstants.DefaultContestBaseConst)
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
        double factor = DaoxuanCombatConstants.DefaultContestOpponentFactor,
        double baseConst = DaoxuanCombatConstants.DefaultContestBaseConst)
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
        double factor = DaoxuanCombatConstants.DefaultContestOpponentFactor,
        double baseConst = DaoxuanCombatConstants.DefaultContestBaseConst)
    {
        if (defenderParry <= attackerBreak) return false;
        var rate = CalculateContestRate(defenderParry, attackerBreak, factor, baseConst);
        return rollRatio < rate;
    }

    /// <summary>
    /// 计算击败单个真实战斗单位所获得的战斗经验。
    /// 基础经验：1.0 点
    /// 对方每超过自身 1 级：指数额外获得 10% (1.0 * 1.10^diff)
    /// 对方每低于自身 1 级：指数减少 20% (1.0 * 0.80^diff)
    /// </summary>
    public static double CalculateCombatExperience(double playerLevel, double targetLevel)
    {
        var levelDiff = targetLevel - playerLevel;
        if (levelDiff > 0)
        {
            return Math.Max(0.01, 1.0 * Math.Pow(1.10, levelDiff));
        }
        if (levelDiff < 0)
        {
            return Math.Max(0.01, 1.0 * Math.Pow(0.80, -levelDiff));
        }
        return 1.0;
    }

    public readonly record struct BaseAttributePool(
        double Level,
        int Tier,
        double BaseHitpoints,
        double BaseManapoints,
        double BasePhysicalAttack,
        double BaseSpellAttack,
        double BaseArmor,
        double BaseMagicResistance,
        double BaseAccuracy,
        double BaseEvasion,
        double BaseCritical,
        double BaseCriticalAvoidance,
        double BaseBreak,
        double BaseParry,
        double BaseHpRegen,
        double BaseMpRegen);

    private static readonly (double Level, double Hp, double Mp, double Ad, double Ap, double Armor, double Mr, double Acc, double Eva, double Crit, double CAvoid, double Break, double Parry)[] BaseFlatAnchors =
    [
        (1,   100,   50,    10,   5,    10,  0,    1,   1,   1,   1,   1,   1),
        (18,  232,   90,    15,   7,    13,  0,    3,   1,   1,   1,   1,   1),
        (30,  472,   570,   30,   27,   23,  14,   7,   4,   3,   2,   2,   2),
        (42,  1108,  1402,  62,   67,   46,  41,   12,  8,   6,   4,   4,   4),
        (54,  2164,  2770,  116,  133,  83,  85,   19,  15,  13,  8,   7,   7),
        (66,  4624,  4234,  196,  232,  138, 152,  28,  24,  22,  14,  13,  12),
        (78,  8056,  6282,  308,  369,  214, 246,  39,  35,  33,  25,  23,  20),
        (90,  11644, 8586,  430,  517,  297, 347,  50,  46,  44,  36,  34,  30),
        (102, 16360, 11618, 590,  712,  406, 480,  65,  61,  59,  50,  48,  44),
        (114, 22372, 15474, 794,  961,  545, 650,  84,  79,  77,  68,  66,  62),
        (126, 29824, 20258, 1047, 1270, 717, 860,  107, 102, 100, 91,  89,  84),
        (127, 38872, 26066, 1354, 1645, 927, 1116, 135, 130, 128, 119, 116, 111)
    ];

    public static BaseAttributePool CalculateBaseAttributes(long spirit)
    {
        var progress = DaoxuanCombatConstants.CalculateRealmProgress(spirit);
        var level = Math.Max(1.0, progress.TotalLevel);
        var tier = CalculateTier(level);

        // 在 12 阶基准锚点中进行分段线性平滑插值
        var lowIndex = 0;
        for (var i = 0; i < BaseFlatAnchors.Length - 1; i++)
        {
            if (level >= BaseFlatAnchors[i].Level && level <= BaseFlatAnchors[i + 1].Level)
            {
                lowIndex = i;
                break;
            }
            if (level > BaseFlatAnchors[i + 1].Level)
            {
                lowIndex = i + 1;
            }
        }

        var low = BaseFlatAnchors[lowIndex];
        var high = lowIndex < BaseFlatAnchors.Length - 1 ? BaseFlatAnchors[lowIndex + 1] : low;
        var span = Math.Max(1e-6, high.Level - low.Level);
        var t = lowIndex < BaseFlatAnchors.Length - 1 ? Math.Clamp((level - low.Level) / span, 0.0, 1.0) : 0.0;

        var baseHp = low.Hp + (high.Hp - low.Hp) * t;
        var baseMp = low.Mp + (high.Mp - low.Mp) * t;
        var baseAd = low.Ad + (high.Ad - low.Ad) * t;
        var baseAp = low.Ap + (high.Ap - low.Ap) * t;
        var baseArmor = low.Armor + (high.Armor - low.Armor) * t;
        var baseMagicRes = low.Mr + (high.Mr - low.Mr) * t;
        var baseAccuracy = low.Acc + (high.Acc - low.Acc) * t;
        var baseEvasion = low.Eva + (high.Eva - low.Eva) * t;
        var baseCrit = low.Crit + (high.Crit - low.Crit) * t;
        var baseCritAvoid = low.CAvoid + (high.CAvoid - low.CAvoid) * t;
        var baseBreak = low.Break + (high.Break - low.Break) * t;
        var baseParry = low.Parry + (high.Parry - low.Parry) * t;

        // 基础生命回复与灵力回复固定为每 5 秒 2% 基础上限，四舍五入取整（保底 1.0）
        var baseHpRegen = Math.Max(1.0, Math.Round(baseHp * 0.02));
        var baseMpRegen = Math.Max(1.0, Math.Round(baseMp * 0.02));

        return new BaseAttributePool(
            level,
            tier,
            baseHp,
            baseMp,
            baseAd,
            baseAp,
            baseArmor,
            baseMagicRes,
            baseAccuracy,
            baseEvasion,
            baseCrit,
            baseCritAvoid,
            baseBreak,
            baseParry,
            baseHpRegen,
            baseMpRegen);
    }

    public static int CalculateTier(double level)
    {
        if (level <= 18.0) return level <= 1.0 ? 1 : 2;
        if (level <= 30.0) return 3;
        if (level <= 42.0) return 4;
        if (level <= 54.0) return 5;
        if (level <= 66.0) return 6;
        if (level <= 78.0) return 7;
        if (level <= 90.0) return 8;
        if (level <= 102.0) return 9;
        if (level <= 114.0) return 10;
        if (level <= 126.0) return 11;
        return 12;
    }
}
