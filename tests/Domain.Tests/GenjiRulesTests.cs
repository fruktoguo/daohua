using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 筑基期「根基（Genji）」领域不变量门禁（TASK-20261119-01）：
/// 1. 累计上限池成长曲线（Lv.30 起 30 点，小境 +4 / 中境 +8 / 大境 +20 覆盖不叠加）；
/// 2. 单项六维向上取整加成（90+10%→99，96+33%→128）与面板缓存即时刷新；
/// 3. 对称守恒修为消耗曲线（0% 处 20% 基准 = 10% 修为，50% 处恰为 50% 修为，总和守恒且受灵根放大）；
/// 4. 半步筑基满修为解锁、扣当前修为不扣底蕴、筑基破境任意有效六维 ≥99 门槛及后续境界免门槛。
/// </summary>
public sealed class GenjiRulesTests
{
    private static readonly GameDataCatalog Catalog = GameDataCatalog.LoadEmbedded();

    [Fact]
    public void CalculateCap_FollowsNonStackingMilestoneSchedule()
    {
        Assert.Equal(0, GenjiRules.CalculateCap(1));
        Assert.Equal(0, GenjiRules.CalculateCap(29));
        // Lv.30 半步筑基起步 30 点
        Assert.Equal(30, GenjiRules.CalculateCap(30));
        // Lv.31 筑基一层（大境界突破 +20）
        Assert.Equal(50, GenjiRules.CalculateCap(31));
        // Lv.32/33 普通小境界（+4, +4）
        Assert.Equal(54, GenjiRules.CalculateCap(32));
        Assert.Equal(58, GenjiRules.CalculateCap(33));
        // Lv.34 筑基四层·中阶（+8）
        Assert.Equal(66, GenjiRules.CalculateCap(34));
        // Lv.37 筑基七层·后阶（+4+4+8 = +16）
        Assert.Equal(82, GenjiRules.CalculateCap(37));
        // Lv.40 筑基巅峰·极阶（+4+4+8 = +16）
        Assert.Equal(98, GenjiRules.CalculateCap(40));
        // Lv.42 半步金丹（筑基期 12 级总计 +76 → 106）
        Assert.Equal(106, GenjiRules.CalculateCap(42));
        // Lv.43 金丹一层（大境界 +20 → 126）
        Assert.Equal(126, GenjiRules.CalculateCap(43));
        // Lv.126 半步飞升（30 + 8×76 = 638），Lv.127 飞升（+20 → 658）
        Assert.Equal(638, GenjiRules.CalculateCap(126));
        Assert.Equal(658, GenjiRules.CalculateCap(127));
    }

    [Fact]
    public void ResolveBonusPoints_CeilsPercentageAndMatchesSpecExamples()
    {
        Assert.Equal(0, GenjiRules.ResolveBonusPoints(0, 30));
        Assert.Equal(0, GenjiRules.ResolveBonusPoints(96, 0));
        // 标准半步筑基 90 点六维 + 10 点根基 → +9 点 = 99
        Assert.Equal(9, GenjiRules.ResolveBonusPoints(90, 10));
        // 向上取整边界：90 × 11% = 9.9 → +10
        Assert.Equal(10, GenjiRules.ResolveBonusPoints(90, 11));
        // 展示示例：力道 96 × 33% = 31.68 → 96 (+32)
        Assert.Equal(32, GenjiRules.ResolveBonusPoints(96, 33));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(106)]
    public void CostCurve_StartsAt20PercentHitsBaseAtHalfAndConservesTotalSum(int cap)
    {
        var half = cap / 2;
        // 0 点起步严格为正常要求（50% 升级修为）的 20%
        Assert.Equal(0.20, GenjiRules.ResolveRelativeCostMultiplier(0, cap), 12);
        // 达到 50% 上限处严格回到正常要求 1.00（即当前境界 50% 修为）
        Assert.Equal(1.00, GenjiRules.ResolveRelativeCostMultiplier(half, cap), 12);

        double sum = 0;
        double prev = 0;
        for (var k = 0; k < cap; k++)
        {
            var mult = GenjiRules.ResolveRelativeCostMultiplier(k, cap);
            Assert.True(mult > prev, $"倍率应在 k={k} 处严格单调递增");
            // 最高单点成本不得超过本层 100% 修为条（即相对倍率 < 2.0）
            Assert.True(mult < 2.0, $"最高单次消耗不得超过本层修为上限，实际 mult={mult}");
            sum += mult;
            prev = mult;
        }
        // 前 50% 省下的修为 100% 精确守恒挪至后 50%：总倍率之和 == cap
        Assert.Equal(cap, sum, 9);

        // 灵根乘区影响校验：五灵根（3.0x）下同点位消耗为单灵根（1.0x）的 3 倍
        var singleCap = 100_000d;
        var fiveLinggenCap = 300_000d;
        Assert.Equal(
            GenjiRules.ResolveCondenseExpCost(singleCap, half, cap) * 3,
            GenjiRules.ResolveCondenseExpCost(fiveLinggenCap, half, cap));
    }

    [Fact]
    public void CondenseAndBreakthrough_EnforcesUnlockFullExpSpendAnd99AttributeGate()
    {
        var realm = Catalog.RealmProgression!;
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "筑基试炼", DateTimeOffset.UtcNow, Catalog.Fingerprint);
        player.Progression.HeavenGate.Apply(
            new LinggenValues(100, 0, 0, 0, 0),
            new LinggenValues(100, 0, 0, 0, 0),
            entered: true,
            rerollCount: 0);
        player.Progression.RealmLevel = 30;

        // 赋予标准半步筑基力道 90 点经验
        var expFor90 = realm.RootCumulativeExpFor(90);
        player.Loadout.CoreAttributes = player.Loadout.CoreAttributes with { AttackExp = expFor90 };

        var cap30 = HeavenGateRules.ResolveExpCapWithLinggen(30, player.Progression.HeavenGate.EffectiveValues, true, realm);
        player.Progression.Foundation = 500_000;
        player.Progression.LifetimeExperience = 1_000_000;

        // 1. 半步筑基修为未满前不允许首次开启根基
        player.Progression.Experience = cap30 * 0.8;
        Assert.Throws<DomainException>(() => player.CondenseGenji(realm));

        // 2. 修为满 100% 但根基 0 点（最高有效六维 90 < 99），突破至筑基一层必须被拦截
        player.Progression.Experience = cap30;
        var ex = Assert.Throws<DomainException>(() => player.Breakthrough(realm));
        Assert.Contains("99", ex.Message);

        // 3. 满修为首次凝练第 1 点：扣减 Experience（10% cap30），不扣 Foundation，且解锁后剩余修为仍可继续连点
        var attrsBefore = PlayerAttributeResolver.Resolve(player, Catalog);
        var firstCost = GenjiRules.ResolveCondenseExpCost(cap30, 0, 30);
        Assert.Equal(Math.Round(cap30 * 0.10, MidpointRounding.AwayFromZero), firstCost);

        player.CondenseGenji(realm);
        Assert.Equal(1, player.Progression.Genji.Points);
        Assert.True(player.Progression.Genji.Unlocked);
        Assert.Equal(cap30 - firstCost, player.Progression.Experience, 6);
        Assert.Equal(500_000, player.Progression.Foundation);
        Assert.Equal(1_000_000, player.Progression.LifetimeExperience);

        // 未再满 100% 也可直接凝练第 2 点（常驻养成）
        player.CondenseGenji(realm);
        Assert.Equal(2, player.Progression.Genji.Points);

        // 4. 补足修为凝练至 10 点根基：90 + ceil(90×10%) = 99，面板物攻立即刷新，突破 Lv.31 放行
        while (player.Progression.Genji.Points < 10)
        {
            player.Progression.Experience = cap30;
            player.CondenseGenji(realm);
        }
        var attrsAfter = PlayerAttributeResolver.Resolve(player, Catalog);
        Assert.True(attrsAfter.PhysicalAttack > attrsBefore.PhysicalAttack);

        player.Progression.Experience = cap30;
        player.Breakthrough(realm);
        Assert.Equal(31, player.Progression.RealmLevel);

        // 5. Lv.31 → Lv.32 突破不再要求额外根基（即使六维归零也放行）
        player.Loadout.CoreAttributes = new PlayerCoreAttributes();
        var cap31 = HeavenGateRules.ResolveExpCapWithLinggen(31, player.Progression.HeavenGate.EffectiveValues, true, realm);
        player.Progression.Experience = cap31;
        player.Breakthrough(realm);
        Assert.Equal(32, player.Progression.RealmLevel);
    }
}
