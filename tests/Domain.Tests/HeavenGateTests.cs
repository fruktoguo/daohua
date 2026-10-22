using System.Text.Json;
using Domain.Persistence;

namespace Domain.Tests;

/// <summary>
/// 开天门与五行灵根回归：覆盖叩天门突破前置、抽取与分配守恒、改命成本曲线与乘区语义。
/// 只断言真实业务不变量，不重复验证同一实现细节。
/// </summary>
public sealed class HeavenGateTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly RealmProgressionCatalog Realm = RealmProgressionCatalog.Parse(
        JsonDocument.Parse("""{"expToNext":[100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,100,800000,100,100],"lifespanYearsByTier":[100,120,140,160,180,200,220,240,260,280,300,320]}""").RootElement);

    /// <summary>
    /// 叩天门满修为但未入天门时，突破必须被阻断并提示先开天门取得灵根。
    /// </summary>
    [Fact]
    public void BreakthroughAtHeavenGateRealmRequiresEnteringGate()
    {
        var player = CreatePlayerAt(HeavenGateRules.HeavenGateRealmLevel);
        player.Progression.Experience = Realm.ExpCapFor(HeavenGateRules.HeavenGateRealmLevel);

        var error = Assert.Throws<DomainException>(() => player.Breakthrough(Realm));
        Assert.Contains("叩开天门", error.Message, StringComparison.Ordinal);

        // 入天门后同一状态可以正常突破：灵根是前置而非永久封禁。
        player.Progression.HeavenGate.Apply(
            new LinggenValues(60, 60, 60, 60, 60),
            new LinggenValues(60, 60, 60, 60, 60),
            entered: true,
            rerollCount: 0);
        player.Breakthrough(Realm);
        Assert.Equal(HeavenGateRules.HeavenGateRealmLevel + 1, player.Progression.RealmLevel);
    }

    /// <summary>
    /// 开天门抽取的总值落在配表区间内，且单系不超过单轴上界；同种子结果逐位一致。
    /// </summary>
    [Fact]
    public void RollIsDeterministicAndWithinConfiguredBounds()
    {
        var first = HeavenGateRules.Roll(20261022L);
        var again = HeavenGateRules.Roll(20261022L);

        Assert.Equal(first, again);
        Assert.InRange(first.Total, HeavenGateRules.RollTotalMin, HeavenGateRules.RollTotalMax);
        Assert.True(first.IsValid);
        Assert.All(
            new[] { first.Metal, first.Wood, first.Water, first.Fire, first.Earth },
            points => Assert.InRange(points, 0, HeavenGateRules.RollAxisCap));
    }

    /// <summary>
    /// 重分配必须总值守恒：改变总值或清空全部灵根的提交一律拒绝。
    /// </summary>
    [Fact]
    public void AllocationMustPreserveTotalAndKeepOneEffectiveRoot()
    {
        var rolled = new LinggenValues(60, 60, 60, 60, 60);

        Assert.True(HeavenGateRules.IsValidAllocation(rolled, new LinggenValues(100, 100, 40, 40, 20)));
        Assert.False(HeavenGateRules.IsValidAllocation(rolled, new LinggenValues(100, 100, 40, 40, 21)));
        Assert.False(HeavenGateRules.IsValidAllocation(rolled, LinggenValues.Empty));
    }

    /// <summary>
    /// 逆天改命的底蕴成本按当前升级所需修为折算：默认 25%，使用逆命丹降为 5%。
    /// </summary>
    [Fact]
    public void RerollFoundationCostFollowsRatioAndPillDiscount()
    {
        const double expCap = 800_000;

        Assert.Equal(200_000, HeavenGateRules.ResolveRerollFoundationCost(expCap, useReverseFatePill: false));
        Assert.Equal(40_000, HeavenGateRules.ResolveRerollFoundationCost(expCap, useReverseFatePill: true));
        // 未配置修为上限时不应产出凭空成本。
        Assert.Equal(0, HeavenGateRules.ResolveRerollFoundationCost(0, useReverseFatePill: false));
    }

    /// <summary>
    /// 逆命丹炼制成本与改命默认档同口径（升级所需修为的 25%）。
    /// </summary>
    [Fact]
    public void ReverseFatePillRecipeCostMatchesDefaultRerollRatio()
    {
        const double expCap = 800_000;
        Assert.Equal(
            HeavenGateRules.ResolveRerollFoundationCost(expCap, useReverseFatePill: false),
            HeavenGateRules.ResolveReverseFatePillFoundationCost(expCap));
    }

    /// <summary>
    /// 灵根乘区：未入天门恒为 1.0；入天门后基础 1.0 已含首条灵根，
    /// 此后每多一条 +50%，五系齐全封顶 3.0 倍。
    /// </summary>
    [Fact]
    public void CultivationMultiplierScalesWithEffectiveRootsOnlyAfterEntering()
    {
        var oneRoot = new LinggenValues(100, 0, 0, 0, 0);
        var threeRoots = new LinggenValues(60, 60, 60, 0, 0);
        var fiveRoots = new LinggenValues(60, 60, 60, 60, 60);

        Assert.Equal(1, HeavenGateRules.ResolveCultivationMultiplier(fiveRoots, entered: false));
        Assert.Equal(1, HeavenGateRules.ResolveCultivationMultiplier(oneRoot, entered: true));
        Assert.Equal(2, HeavenGateRules.ResolveCultivationMultiplier(threeRoots, entered: true));
        Assert.Equal(3.0, HeavenGateRules.ResolveCultivationMultiplier(fiveRoots, entered: true));
    }

    /// <summary>
    /// 灵根点数与增伤/减伤点数一一对应：100 点提供 100 点增伤，减伤同样 100 点但走 NonLinear
    /// 曲线（100 点对应实际减伤 50%，即伤害减半）。
    /// </summary>
    [Fact]
    public void LinggenPointsMapToOneHundredPercentBonusAndHalvedDamage()
    {
        Assert.Equal(100, HeavenGateRules.ResolveDamageBonusPoints(100));
        Assert.Equal(100, HeavenGateRules.ResolveDamageReductionPoints(100));
        // NonLinear 曲线的实际生效值：100 / (100 + 100) = 0.5。
        Assert.Equal(0.5, PercentValue.Multiplier(100, PercentCurveMode.NonLinear), 6);
    }

    /// <summary>
    /// 灵根状态随快照往返保留：抽取结果、生效灵根、入天门标记与累计改命次数都不丢失。
    /// </summary>
    [Fact]
    public void HeavenGateStateRoundTripsThroughSnapshot()
    {
        var player = CreatePlayerAt(HeavenGateRules.HeavenGateRealmLevel);
        var rolled = new LinggenValues(40, 50, 60, 70, 80);
        var confirmed = new LinggenValues(60, 60, 60, 60, 60);
        player.Progression.HeavenGate.Apply(rolled, confirmed, entered: true, rerollCount: 7);

        var restored = PlayerAggregate.Restore(player.ExportState());

        Assert.Equal(rolled, restored.Progression.HeavenGate.RolledValues);
        Assert.Equal(confirmed, restored.Progression.HeavenGate.ConfirmedValues);
        Assert.True(restored.Progression.HeavenGate.Entered);
        Assert.Equal(7, restored.Progression.HeavenGate.RerollCount);
    }

    private static PlayerAggregate CreatePlayerAt(int realmLevel)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "问道", Baseline, "catalog-a");
        player.Progression.RealmLevel = realmLevel;
        return player;
    }
}
