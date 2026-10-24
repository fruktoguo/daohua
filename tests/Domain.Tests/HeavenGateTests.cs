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
        // 五系齐全 → 灵根乘区 3.0（§7.7.5.1），瓶颈同步放大，需补足到乘区后瓶颈。
        player.Progression.HeavenGate.Apply(
            new LinggenValues(60, 60, 60, 60, 60),
            new LinggenValues(60, 60, 60, 60, 60),
            entered: true,
            rerollCount: 0);
        player.Progression.Experience = Realm.ExpCapFor(HeavenGateRules.HeavenGateRealmLevel) * 3;
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
    /// 开天门总值期望随改命次数成长（0 次在 100 左右、40 次在高位且能随出 500 满值）。
    /// </summary>
    [Fact]
    public void RollGrowsExpectationWithRerollCountAndCanReachFull500()
    {
        var sampleCount = 2000;
        var sum0 = 0.0;
        for (var i = 0; i < sampleCount; i++)
        {
            sum0 += HeavenGateRules.Roll(i * 10007L, 0).Total;
        }
        var avg0 = sum0 / sampleCount;
        // 初始期望 180 左右（容许合理统计波动 165~195）
        Assert.InRange(avg0, 165.0, 195.0);

        var sum40 = 0.0;
        var hit500 = false;
        for (var i = 0; i < sampleCount; i++)
        {
            var res = HeavenGateRules.Roll(i * 10007L, 40);
            sum40 += res.Total;
            if (res.Total == 500)
            {
                hit500 = true;
                Assert.Equal(5, res.EffectiveCount);
                Assert.Equal(100, res.Metal);
                Assert.Equal(100, res.Wood);
                Assert.Equal(100, res.Water);
                Assert.Equal(100, res.Fire);
                Assert.Equal(100, res.Earth);
            }
        }
        var avg40 = sum40 / sampleCount;
        // 改命 40 次后期望大幅提升至 480 以上，且能抽中五系 500 满值
        Assert.True(avg40 > 480.0, $"改命 40 次均值应 > 480，实际为 {avg40}");
        Assert.True(hit500, "改命 40 次在 2000 样本中应能命中 500 满值");
    }

    /// <summary>
    /// 无论随机总点数多少，活跃灵根条数 N 必须严格受容量下限约束且总和严格等于抽样总值。
    /// </summary>
    [Fact]
    public void RollActiveRootCountRespectsCapacityBoundaries()
    {
        for (var i = 0; i < 2000; i++)
        {
            var roll = HeavenGateRules.Roll(i * 99991L + 17L, i % 45);
            var total = roll.Total;
            var activeCount = roll.EffectiveCount;

            Assert.InRange(total, HeavenGateRules.RollTotalMin, HeavenGateRules.RollTotalMax);
            var expectedMinCount = Math.Max(1, (total + HeavenGateRules.RollAxisCap - 1) / HeavenGateRules.RollAxisCap);
            var expectedMaxCount = Math.Min(5, total);

            Assert.InRange(activeCount, expectedMinCount, expectedMaxCount);
            Assert.Equal(total, roll.Metal + roll.Wood + roll.Water + roll.Fire + roll.Earth);
            Assert.All(
                new[] { roll.Metal, roll.Wood, roll.Water, roll.Fire, roll.Earth },
                p => Assert.InRange(p, 0, HeavenGateRules.RollAxisCap));
        }
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

    /// <summary>
    /// 灵根破镜乘区真实接入突破瓶颈（总纲 §7.7.5.1）：入天门后瓶颈 = 配表值 × (1 + 0.5×(条数−1))，
    /// 修为只到原瓶颈（未乘乘区）时不得突破；补足到乘区后瓶颈才放行；未入天门时瓶颈保持原值。
    /// </summary>
    [Fact]
    public void BreakthroughCapScalesWithLinggenMultiplierAfterEnteringGate()
    {
        const int gateLevel = HeavenGateRules.HeavenGateRealmLevel;
        const int nextLevel = gateLevel + 1;
        var rawCap = Realm.ExpCapFor(gateLevel);

        // 场景 1：未入天门（仅有 confirmed 未 entered 也不放大），瓶颈恒为配表原值。
        var player = CreatePlayerAt(gateLevel);
        player.Progression.HeavenGate.Apply(
            new LinggenValues(60, 60, 60, 60, 60),
            new LinggenValues(60, 60, 60, 60, 60),
            entered: false,
            rerollCount: 0);
        player.Progression.Experience = rawCap;
        var gateBlock = Assert.Throws<DomainException>(() => player.Breakthrough(Realm));
        Assert.Contains("叩开天门", gateBlock.Message, StringComparison.Ordinal);

        // 场景 2：入天门五系齐全 → 乘区 3.0，瓶颈 = 800000 × 3 = 2400000。
        // 修为仅到原瓶颈时必须被乘区挡下（这正是「破镜难度真实生效」的断言）。
        player.Progression.HeavenGate.Apply(
            new LinggenValues(60, 60, 60, 60, 60),
            new LinggenValues(60, 60, 60, 60, 60),
            entered: true,
            rerollCount: 0);
        player.Progression.Experience = rawCap;
        var capped = Assert.Throws<DomainException>(() => player.Breakthrough(Realm));
        Assert.Contains("修为未满", capped.Message, StringComparison.Ordinal);

        // 场景 3：补足到乘区后瓶颈（3× 原值）即可突破，突破清零本层修为。
        player.Progression.Experience = rawCap * 3;
        player.Breakthrough(Realm);
        Assert.Equal(nextLevel, player.Progression.RealmLevel);
        Assert.Equal(0, player.Progression.Experience);

        // 场景 4：下一层散功为单灵根（拖掉四轴）→ 乘区回落 1.0，瓶颈回到配表原值。
        player.Progression.HeavenGate.Apply(
            new LinggenValues(100, 0, 0, 0, 0),
            new LinggenValues(100, 0, 0, 0, 0),
            entered: true,
            rerollCount: 0);
        player.Progression.Experience = Realm.ExpCapFor(nextLevel);
        player.Breakthrough(Realm);
        Assert.Equal(nextLevel + 1, player.Progression.RealmLevel);
    }
}
