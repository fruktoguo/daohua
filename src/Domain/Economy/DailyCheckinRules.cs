using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Domain.Economy;

/// <summary>
/// 离散截断几何分布参数快照：在整数闭区间 [Min, Max] 上以权重 q^offset（或镜像）抽样，
/// 长期期望精确锚定 TargetMean，高值以长尾形式出现。
/// </summary>
public sealed record TruncatedGeometricDistribution(
    int Min,
    int Max,
    double TargetMean,
    double Q,
    bool Mirror,
    double ExpectedMean);

/// <summary>
/// 每日签到领域规则（对齐《道劫余生》长尾分布与连签推进模型，剥离幸运值与固定池）：
/// - 奖励资产：item.gongde-bound（绑定功德）；
/// - 奖励区间：[10, 999] 整数；
/// - 期望均值：无连签基础均值 30，每有效连签 1 天提供 +1% 均值推进量，渐近逼近上限 60；
/// - 自然日口径：复用 UTC+8 自然日序号（ShopDailyWindow.DayIndexOf）。
/// </summary>
public static class DailyCheckinRules
{
    /// <summary>签到奖励资产 ID（绑定功德，不可交易）。</summary>
    public const string RewardItemId = "item.gongde-bound";

    /// <summary>单次签到保底奖励。</summary>
    public const int MinMerit = 10;

    /// <summary>单次签到长尾上限。</summary>
    public const int MaxMerit = 999;

    /// <summary>无连续签到加成时的基准日均奖励。</summary>
    public const double BaseMeanMerit = 30.0;

    /// <summary>连续签到无穷天时目标均值的渐近上限。</summary>
    public const double MeanCeilingMerit = 60.0;

    /// <summary>每个有效连续签到天数额外提供的均值推进率（+1%/天）。</summary>
    public const double StreakBonusPerDay = 0.01;

    private const double ExpectationEpsilon = 1e-9;
    private const int GeometricSolveIterations = 80;
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    /// <summary>计算指定时刻对应的 UTC+8 自然日序号。</summary>
    public static long DayIndexOf(DateTimeOffset at) => ShopDailyWindow.DayIndexOf(at);

    /// <summary>计算指定时刻对应的 UTC+8 自然日日期文本（yyyy-MM-dd）。</summary>
    public static string ChinaDateKeyOf(DateTimeOffset at)
        => at.ToOffset(ChinaOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>计算指定时刻在 UTC+8 自然周内的偏移索引（周一=0 .. 周日=6）。</summary>
    public static int ChinaDayOfWeekIndexMonday(DateTimeOffset at)
    {
        var dow = (int)at.ToOffset(ChinaOffset).DayOfWeek;
        return (dow + 6) % 7;
    }

    /// <summary>计算指定时刻所在 UTC+8 自然周（周一）的自然日序号。</summary>
    public static long WeekStartDayIndexOf(DateTimeOffset at)
        => DayIndexOf(at) - ChinaDayOfWeekIndexMonday(at);

    /// <summary>计算指定时刻对应的 UTC+8 自然月编码（yyyyMM，例如 202611）。</summary>
    public static int ChinaMonthKeyOf(DateTimeOffset at)
    {
        var china = at.ToOffset(ChinaOffset);
        return china.Year * 100 + china.Month;
    }

    /// <summary>
    /// 按道劫余生 10 档签文规则根据功德数量（或周日均功德）判定签运档位键：
    /// 仅作为叙事签运展示，不附带任何幸运值数值。
    /// </summary>
    public static string ResolveFortuneTier(double merit)
    {
        if (merit >= MaxMerit) return "perfect";
        var baseSpan = Math.Max(1.0, MeanCeilingMerit - MinMerit);
        var ratio = Math.Max(0.0, (merit - MinMerit) / baseSpan);
        if (ratio > 8.0) return "transcendent_4";
        if (ratio > 4.0) return "transcendent_3";
        if (ratio > 2.0) return "transcendent_2";
        if (ratio > 1.0) return "transcendent_1";
        if (ratio >= 0.8) return "great";
        if (ratio >= 0.6) return "good";
        if (ratio >= 0.4) return "neutral";
        if (ratio >= 0.2) return "bad";
        return "very_bad";
    }

    /// <summary>计算指定时刻之后的下一个 UTC+8 零点 Unix 秒戳。</summary>
    public static long NextResetUnixSeconds(DateTimeOffset at)
    {
        var todayIndex = DayIndexOf(at);
        return (todayIndex + 1) * (long)ShopDailyWindow.SecondsPerDay - (long)ShopDailyWindow.UtcOffsetSeconds;
    }

    /// <summary>
    /// 解析有效连续签到天数：
    /// - 从未签到：本次签后为 1 天；
    /// - 今日已签：维持当前连签天数（至少 1）；
    /// - 昨日已签：本次签后延续为 currentStreakDays + 1；
    /// - 断签：重置为 1 天。
    /// </summary>
    public static int ResolveEffectiveStreakDays(long? lastClaimDayIndex, int currentStreakDays, long todayDayIndex)
    {
        if (lastClaimDayIndex is null) return 1;
        if (lastClaimDayIndex.Value == todayDayIndex) return Math.Max(1, currentStreakDays);
        return lastClaimDayIndex.Value == todayDayIndex - 1
            ? Math.Max(1, currentStreakDays + 1)
            : 1;
    }

    /// <summary>
    /// 按有效连续签到天数计算目标期望均值：
    /// BaseMeanMerit + (MeanCeilingMerit - BaseMeanMerit) * streakBonus / (1 + streakBonus)。
    /// </summary>
    public static double ComputeTargetMean(int effectiveStreakDays)
    {
        var days = Math.Max(0, effectiveStreakDays);
        var streakBonus = days * StreakBonusPerDay;
        return BaseMeanMerit + (MeanCeilingMerit - BaseMeanMerit) * streakBonus / (1.0 + streakBonus);
    }

    /// <summary>在给定有效连续签到天数下抽取本次每日签到奖励（保证落在 [10, 999] 内）。</summary>
    public static int RollReward(int effectiveStreakDays, Func<double>? unitRandom = null)
        => RollExpandedMeanInteger(MinMerit, MaxMerit, ComputeTargetMean(effectiveStreakDays), unitRandom);

    /// <summary>反解离散截断几何分布衰减系数 q，使 [min, max] 上的期望精确等于 targetMean。</summary>
    public static TruncatedGeometricDistribution BuildTruncatedGeometricDistribution(
        int min,
        int max,
        double targetMean)
    {
        if (max < min)
            throw new ArgumentOutOfRangeException(nameof(max), "随机区间上限不能小于下限。");
        if (!double.IsFinite(targetMean) || targetMean < min || targetMean > max)
            throw new ArgumentOutOfRangeException(nameof(targetMean), "目标均值必须落在随机区间内。");

        var span = max - min;
        var targetOffsetMean = targetMean - min;
        if (span == 0 || targetOffsetMean <= ExpectationEpsilon)
            return new TruncatedGeometricDistribution(min, max, targetMean, 0.0, false, min);
        if (Math.Abs(targetOffsetMean - span) <= ExpectationEpsilon)
            return new TruncatedGeometricDistribution(min, max, targetMean, 0.0, true, max);
        if (Math.Abs(targetOffsetMean - span / 2.0) <= ExpectationEpsilon)
            return new TruncatedGeometricDistribution(min, max, targetMean, 1.0, false, targetMean);

        var mirror = targetOffsetMean > span / 2.0;
        var solvedOffsetMean = mirror ? span - targetOffsetMean : targetOffsetMean;
        var low = 0.0;
        var high = 1.0;
        for (var i = 0; i < GeometricSolveIterations; i++)
        {
            var mid = (low + high) * 0.5;
            var mean = TruncatedGeometricOffsetMean(span, mid);
            if (mean < solvedOffsetMean) low = mid;
            else high = mid;
        }

        var q = (low + high) * 0.5;
        var expectedOffsetMean = TruncatedGeometricOffsetMean(span, q);
        var expectedMean = min + (mirror ? span - expectedOffsetMean : expectedOffsetMean);
        return new TruncatedGeometricDistribution(min, max, targetMean, q, mirror, expectedMean);
    }

    /// <summary>按截断几何分布抽取 [min, max] 内的整数。</summary>
    public static int RollExpandedMeanInteger(
        int min,
        int max,
        double targetMean,
        Func<double>? unitRandom = null)
    {
        var distribution = BuildTruncatedGeometricDistribution(min, max, targetMean);
        if (distribution.Min == distribution.Max || distribution.Q <= 0.0)
            return distribution.Mirror ? distribution.Max : distribution.Min;

        var rawU = (unitRandom ?? RollCryptoUnitRandom)();
        if (rawU >= 1.0)
            return distribution.Mirror ? distribution.Min : distribution.Max;

        var u = ClampUnitRandom(rawU);
        var span = distribution.Max - distribution.Min;
        if (Math.Abs(distribution.Q - 1.0) <= 1e-12)
        {
            var uniformOffset = Math.Min(span, (int)Math.Floor(u * (span + 1)));
            return distribution.Min + uniformOffset;
        }

        var totalWeight = GeometricWeightSum(span, distribution.Q);
        var threshold = u * totalWeight;
        var weight = 1.0;
        for (var offset = 0; offset <= span; offset++)
        {
            threshold -= weight;
            if (threshold <= 0.0 || offset == span)
                return distribution.Min + (distribution.Mirror ? span - offset : offset);
            weight *= distribution.Q;
        }

        return distribution.Mirror ? distribution.Min : distribution.Max;
    }

    private static double RollCryptoUnitRandom()
    {
        Span<byte> buffer = stackalloc byte[8];
        RandomNumberGenerator.Fill(buffer);
        // 使用 53 位精度填充 IEEE-754 double 尾数，覆盖极小长尾桶。
        return (BinaryPrimitives.ReadUInt64LittleEndian(buffer) >> 11) * (1.0 / (1UL << 53));
    }

    private static double ClampUnitRandom(double value)
    {
        if (!double.IsFinite(value) || value <= 0.0) return 0.0;
        // 与 JS 0x1_0000_0000 精度下边界对齐，确保 u=1 时稳定命中上边界。
        return Math.Min(value, 1.0 - 1e-15);
    }

    private static double GeometricWeightSum(int span, double q)
    {
        if (Math.Abs(1.0 - q) <= 1e-12) return span + 1.0;
        return (1.0 - Math.Pow(q, span + 1)) / (1.0 - q);
    }

    private static double TruncatedGeometricOffsetMean(int span, double q)
    {
        if (q <= 0.0) return 0.0;
        if (Math.Abs(1.0 - q) <= 1e-12) return span / 2.0;

        var qToNPlusOne = Math.Pow(q, span + 1);
        var qToNPlusTwo = qToNPlusOne * q;
        var numerator = q - (span + 1) * qToNPlusOne + span * qToNPlusTwo;
        var denominator = (1.0 - q) * (1.0 - qToNPlusOne);
        return denominator <= 0.0 ? 0.0 : numerator / denominator;
    }
}
