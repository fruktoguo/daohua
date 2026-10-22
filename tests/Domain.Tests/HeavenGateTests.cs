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
    /// 重分配走「散功 2 返 1、灌注 1:1」点数池：透支可分配额度或清空全部灵根一律拒绝。
    /// </summary>
    [Fact]
    public void AllocationUsesPointPoolAndKeepsOneEffectiveRoot()
    {
        var innate = new LinggenValues(60, 60, 60, 60, 60);

        // 金系散到 0 返还 30 点，木系从 60 拉到 90 消耗 30 点，额度刚好用尽。
        Assert.Equal(0, HeavenGateRules.ResolveRemainingPoints(innate, new LinggenValues(0, 90, 60, 60, 60)), 6);
        // 只散不灌时全额返还（60 → 0 返还 30）。
        Assert.Equal(30, HeavenGateRules.ResolveRemainingPoints(innate, new LinggenValues(0, 60, 60, 60, 60)), 6);
        Assert.True(HeavenGateRules.IsValidAllocation(innate, new LinggenValues(0, 90, 60, 60, 60)));

        // 再把火系也拉到 90 需额外 30 点，已无余额可透支。
        Assert.True(HeavenGateRules.ResolveRemainingPoints(innate, new LinggenValues(0, 90, 60, 90, 60)) < 0);
        Assert.False(HeavenGateRules.IsValidAllocation(innate, new LinggenValues(0, 90, 60, 90, 60)));

        // 五系归零没有有效灵根，禁止确认。
        Assert.False(HeavenGateRules.IsValidAllocation(innate, LinggenValues.Empty));
    }

    /// <summary>
    /// 单轴可达上限由其余四轴的让点额度决定，且夹取在 [0, 100]。
    /// </summary>
    [Fact]
    public void MaxReachableValueUsesDonatedPointsFromOtherAxes()
    {
        var innate = new LinggenValues(60, 60, 60, 60, 60);
        // 其余四轴各散到 0，共返还 4 × 30 = 120 点，但单轴封顶 100。
        var drained = new LinggenValues(60, 0, 0, 0, 0);
        Assert.Equal(LinggenValues.MaxPointsPerElement,
            HeavenGateRules.ResolveMaxReachableValue(innate, drained, LinggenElement.Metal));
        // 全部维持先天值时没有额外额度，上限即当前值。
        Assert.Equal(60, HeavenGateRules.ResolveMaxReachableValue(innate, innate, LinggenElement.Metal));
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
