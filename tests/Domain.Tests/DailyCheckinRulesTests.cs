using Domain.Economy;
using Protocol;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 每日签到领域规则与截断几何分布单元测试：
/// 覆盖均值曲线、连续签到重置与衔接、分布期望无偏性、边界抽样与协议信封往返。
/// </summary>
public sealed class DailyCheckinRulesTests
{
    [Fact]
    public void ComputeTargetMean_FollowsStreakCurveAndStaysBounded()
    {
        Assert.Equal(30.0, DailyCheckinRules.ComputeTargetMean(0), precision: 8);
        Assert.InRange(DailyCheckinRules.ComputeTargetMean(1), 30.29, 30.30);
        Assert.InRange(DailyCheckinRules.ComputeTargetMean(30), 36.92, 36.93);
        Assert.Equal(45.0, DailyCheckinRules.ComputeTargetMean(100), precision: 8);

        var veryLongStreak = DailyCheckinRules.ComputeTargetMean(1_000_000);
        Assert.True(veryLongStreak > 59.9 && veryLongStreak < 60.0);
    }

    [Fact]
    public void ResolveEffectiveStreakDays_HandlesFirstClaimContinueSameDayAndBreak()
    {
        const long today = 20_500;
        Assert.Equal(1, DailyCheckinRules.ResolveEffectiveStreakDays(null, 0, today));
        Assert.Equal(5, DailyCheckinRules.ResolveEffectiveStreakDays(today, 5, today));
        Assert.Equal(6, DailyCheckinRules.ResolveEffectiveStreakDays(today - 1, 5, today));
        Assert.Equal(1, DailyCheckinRules.ResolveEffectiveStreakDays(today - 2, 5, today));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(100)]
    public void TruncatedGeometricDistribution_ExactExpectedMeanMatchesTarget(int streakDays)
    {
        var targetMean = DailyCheckinRules.ComputeTargetMean(streakDays);
        var dist = DailyCheckinRules.BuildTruncatedGeometricDistribution(
            DailyCheckinRules.MinMerit,
            DailyCheckinRules.MaxMerit,
            targetMean);

        Assert.Equal(10, dist.Min);
        Assert.Equal(999, dist.Max);
        Assert.False(dist.Mirror);
        Assert.InRange(dist.Q, 0.0, 1.0);
        Assert.True(Math.Abs(dist.ExpectedMean - targetMean) < 1e-6);

        // 逐点求和校验解析分布的真实数学期望确实钉在 targetMean 上。
        var span = dist.Max - dist.Min;
        var weightSum = 0.0;
        var weightedRewardSum = 0.0;
        var weight = 1.0;
        for (var offset = 0; offset <= span; offset++)
        {
            weightSum += weight;
            weightedRewardSum += (dist.Min + offset) * weight;
            weight *= dist.Q;
        }
        var trueExpectation = weightedRewardSum / weightSum;
        Assert.True(Math.Abs(trueExpectation - targetMean) < 1e-6, $"true={trueExpectation}, target={targetMean}");
    }

    [Fact]
    public void RollExpandedMeanInteger_SupportsBoundariesAndMirrorBranch()
    {
        var targetMean = DailyCheckinRules.ComputeTargetMean(1);
        Assert.Equal(10, DailyCheckinRules.RollExpandedMeanInteger(10, 999, targetMean, () => 0.0));
        Assert.Equal(999, DailyCheckinRules.RollExpandedMeanInteger(10, 999, targetMean, () => 1.0));

        var mirrored = DailyCheckinRules.BuildTruncatedGeometricDistribution(10, 100, 80.0);
        Assert.True(mirrored.Mirror);
        Assert.True(Math.Abs(mirrored.ExpectedMean - 80.0) < 1e-6);
        Assert.Equal(100, DailyCheckinRules.RollExpandedMeanInteger(10, 100, 80.0, () => 0.0));
        Assert.Equal(10, DailyCheckinRules.RollExpandedMeanInteger(10, 100, 80.0, () => 1.0));
    }

    [Fact]
    public void ChinaDayIndexAndNextReset_FollowUtcPlus8Midnight()
    {
        // 2026-11-16 23:59:59 UTC+8 == 2026-11-16 15:59:59 UTC
        var beforeMidnight = new DateTimeOffset(2026, 11, 16, 23, 59, 59, TimeSpan.FromHours(8));
        // 2026-11-17 00:00:00 UTC+8 == 2026-11-16 16:00:00 UTC
        var atMidnight = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.FromHours(8));

        Assert.Equal("2026-11-16", DailyCheckinRules.ChinaDateKeyOf(beforeMidnight));
        Assert.Equal("2026-11-17", DailyCheckinRules.ChinaDateKeyOf(atMidnight));
        Assert.Equal(DailyCheckinRules.DayIndexOf(beforeMidnight) + 1, DailyCheckinRules.DayIndexOf(atMidnight));
        Assert.Equal(atMidnight.ToUnixTimeSeconds(), DailyCheckinRules.NextResetUnixSeconds(beforeMidnight));
    }

    [Fact]
    public void CheckinSummaryEnvelope_RoundTripsBinaryWireFrame()
    {
        var requestId = Guid.NewGuid();
        var summary = new CheckinSummaryDto(
            CanClaimToday: true,
            TodayDate: "2026-11-16",
            DayIndex: 20774,
            StreakDays: 12,
            TotalDays: 20,
            LastRewardMerit: 42,
            IsBot: false);
        var envelope = WireEnvelope.Json(WireMessageType.CheckinSummary, requestId, 7, summary);
        var bytes = WireEnvelopeCodec.Encode(envelope);

        Assert.True(WireEnvelopeCodec.TryDecode(bytes, out var decoded));
        Assert.Equal(WireMessageType.CheckinSummary, decoded.MessageType);
        Assert.Equal(requestId, decoded.RequestId);
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<CheckinSummaryDto>(decoded, out var parsed));
        Assert.Equal(summary, parsed);
    }

    [Fact]
    public void ResolveFortuneTier_MatchesDaojieBoundaries()
    {
        Assert.Equal("very_bad", DailyCheckinRules.ResolveFortuneTier(10));
        Assert.Equal("bad", DailyCheckinRules.ResolveFortuneTier(25));
        Assert.Equal("neutral", DailyCheckinRules.ResolveFortuneTier(30));
        Assert.Equal("good", DailyCheckinRules.ResolveFortuneTier(45));
        Assert.Equal("great", DailyCheckinRules.ResolveFortuneTier(55));
        Assert.Equal("transcendent_1", DailyCheckinRules.ResolveFortuneTier(80));
        Assert.Equal("transcendent_2", DailyCheckinRules.ResolveFortuneTier(130));
        Assert.Equal("transcendent_3", DailyCheckinRules.ResolveFortuneTier(250));
        Assert.Equal("transcendent_4", DailyCheckinRules.ResolveFortuneTier(500));
        Assert.Equal("perfect", DailyCheckinRules.ResolveFortuneTier(999));
    }

    [Fact]
    public void WeekAndMonthKeys_RollOverAcrossMondayAndMonthBoundary()
    {
        // 2026-11-15 是周日；2026-11-16 是周一
        var sunday = new DateTimeOffset(2026, 11, 15, 23, 0, 0, TimeSpan.FromHours(8));
        var monday = new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.FromHours(8));
        Assert.Equal(6, DailyCheckinRules.ChinaDayOfWeekIndexMonday(sunday));
        Assert.Equal(0, DailyCheckinRules.ChinaDayOfWeekIndexMonday(monday));
        Assert.NotEqual(DailyCheckinRules.WeekStartDayIndexOf(sunday), DailyCheckinRules.WeekStartDayIndexOf(monday));

        // 2026-11-30 -> 2026-12-01
        var nov = new DateTimeOffset(2026, 11, 30, 12, 0, 0, TimeSpan.FromHours(8));
        var dec = new DateTimeOffset(2026, 12, 1, 0, 1, 0, TimeSpan.FromHours(8));
        Assert.Equal(202611, DailyCheckinRules.ChinaMonthKeyOf(nov));
        Assert.Equal(202612, DailyCheckinRules.ChinaMonthKeyOf(dec));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(100)]
    [InlineData(1000)]
    public void CheckinTiltedDistribution_MatchesTargetMeanAndSuppressesLowTail(int streakDays)
    {
        var targetMean = DailyCheckinRules.ComputeTargetMean(streakDays);
        var dist = DailyCheckinRules.BuildCheckinDistribution(streakDays);
        var probs = DailyCheckinRules.ComputeCheckinProbabilities(streakDays);

        Assert.Equal(DailyCheckinRules.MinMerit, dist.Min);
        Assert.Equal(DailyCheckinRules.MaxMerit, dist.Max);
        Assert.True(Math.Abs(dist.ExpectedMean - targetMean) < 1e-6);
        Assert.Equal(DailyCheckinRules.MaxMerit - DailyCheckinRules.MinMerit + 1, probs.Length);

        var probSum = 0.0;
        var expectation = 0.0;
        var veryBadProb = 0.0;
        var badTotalProb = 0.0;
        for (var offset = 0; offset < probs.Length; offset++)
        {
            var merit = DailyCheckinRules.MinMerit + offset;
            var p = probs[offset];
            Assert.True(p > 0.0);
            probSum += p;
            expectation += merit * p;
            var tier = DailyCheckinRules.ResolveFortuneTier(merit);
            if (tier == "very_bad") veryBadProb += p;
            if (tier is "very_bad" or "bad") badTotalProb += p;
        }

        Assert.True(Math.Abs(probSum - 1.0) < 1e-9);
        Assert.True(Math.Abs(expectation - targetMean) < 1e-6, $"expectation={expectation}, target={targetMean}");

        if (streakDays == 1)
        {
            // 首日（期望 ~30.30）：下下签压至 ~9.8%，坏签合计压至 ~37.8%（显著低于纯单向几何的 61.8%）。
            Assert.InRange(veryBadProb, 0.08, 0.12);
            Assert.InRange(badTotalProb, 0.35, 0.40);
        }
    }

    [Fact]
    public void RollReward_RespectsBoundsAndReducesBadFortuneWithStreak()
    {
        Assert.Equal(DailyCheckinRules.MinMerit, DailyCheckinRules.RollReward(1, () => 0.0));
        Assert.Equal(DailyCheckinRules.MaxMerit, DailyCheckinRules.RollReward(1, () => 1.0));
        Assert.InRange(DailyCheckinRules.RollReward(1, () => 0.5), DailyCheckinRules.MinMerit, DailyCheckinRules.MaxMerit);

        static double SumBadProb(int streak)
        {
            var probs = DailyCheckinRules.ComputeCheckinProbabilities(streak);
            var sum = 0.0;
            for (var i = 0; i < DailyCheckinRules.LowTailPivotOffset; i++) sum += probs[i];
            return sum;
        }

        var day1Bad = SumBadProb(1);
        var day30Bad = SumBadProb(30);
        var day100Bad = SumBadProb(100);
        Assert.True(day1Bad > day30Bad && day30Bad > day100Bad);
    }
}
