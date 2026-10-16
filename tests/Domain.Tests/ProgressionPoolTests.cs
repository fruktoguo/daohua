using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using DaoHua.Ecs;

namespace Domain.Tests;

/// <summary>
/// 成长四池（修为/底蕴/战斗经验/累计修为）结算回归：
/// CreditExperience 三段口径、底蕴兑换放大与满层沉淀、战斗经验对抗修正、快照往返。
/// </summary>
public sealed class ProgressionPoolTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name = "池子")
        => PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "catalog-a");

    private static GameDataCatalog CatalogWithRealm(ActionCatalog actions, params double[] expToNext)
        => new("test", "fp", actions,
            realmProgression: new RealmProgressionCatalog(expToNext, [80]));

    // ── ComputeExperienceCredit 纯函数分支 ──

    /// <summary>层境未满且无底蕴：收益按增幅量全额入账，无放大。</summary>
    [Fact]
    public void Credit_NoFoundationBelowCap_AmplifiedOnly()
        => Assert.Equal(10, PlayerProgression.ComputeExperienceCredit(10, 100, 30, 0));

    /// <summary>底蕴充足：单笔放大至 3× 增幅量，兑换消耗 = 2× 增幅量。</summary>
    [Fact]
    public void Credit_AmpleFoundation_TripleGain()
        => Assert.Equal(30, PlayerProgression.ComputeExperienceCredit(10, 100, 0, 1000));

    /// <summary>底蕴不足时按余量部分放大：兑多少补多少，不透支。</summary>
    [Fact]
    public void Credit_PartialFoundation_LimitedBoost()
        => Assert.Equal(15, PlayerProgression.ComputeExperienceCredit(10, 100, 0, 5));

    /// <summary>距瓶颈容量不足时先填满：兑换放大与溢出一并截断在瓶颈内。</summary>
    [Fact]
    public void Credit_CapacityClamp_WinsOverBoost()
        => Assert.Equal(8, PlayerProgression.ComputeExperienceCredit(10, 100, 92, 1000));

    /// <summary>满层（已达上限）恒入账 0：增幅量全部转沉淀。</summary>
    [Fact]
    public void Credit_AtCap_Zero()
        => Assert.Equal(0, PlayerProgression.ComputeExperienceCredit(10, 100, 100, 0));

    /// <summary>满级无上限（cap≤0）恒入账 0。</summary>
    [Fact]
    public void Credit_MaxLevel_Zero()
        => Assert.Equal(0, PlayerProgression.ComputeExperienceCredit(10, 0, 0, 50));

    // ── CreditExperience 状态迁移 ──

    /// <summary>底蕴兑换放大：修为 +3×、底蕴 −2×、战斗传入战斗经验入账、累计修为记增幅量。</summary>
    [Fact]
    public void CreditExperience_FullBoostLedger()
    {
        var p = CreatePlayer();
        p.Progression.Foundation = 100;
        var credited = p.Progression.CreditExperience(8, 100, combatExperience: 4);
        Assert.Equal(24, credited);          // 3×8 截断于 8+100 与容量之内
        Assert.Equal(24, p.Progression.Experience);
        Assert.Equal(84, p.Progression.Foundation);    // 100 − (24−8)
        Assert.Equal(4, p.Progression.CombatExperience);
        Assert.Equal(8, p.Progression.LifetimeExperience);
    }

    /// <summary>打坐修炼等日常修行只增加修为/底蕴/累计修为，不增加战斗经验（缺省为 0）。</summary>
    [Fact]
    public void CreditExperience_DefaultCombatExperienceIsZero()
    {
        var p = CreatePlayer();
        var credited = p.Progression.CreditExperience(10, 100);
        Assert.Equal(10, credited);
        Assert.Equal(10, p.Progression.Experience);
        Assert.Equal(0, p.Progression.CombatExperience);
        Assert.Equal(10, p.Progression.LifetimeExperience);
    }

    /// <summary>满层沉淀：修为不动、增幅量全额入底蕴、战斗传入经验照记。</summary>
    [Fact]
    public void CreditExperience_AtCapAllToFoundation()
    {
        var p = CreatePlayer();
        p.Progression.Experience = 50;
        var credited = p.Progression.CreditExperience(6, 50, combatExperience: 6);
        Assert.Equal(0, credited);
        Assert.Equal(50, p.Progression.Experience);
        Assert.Equal(6, p.Progression.Foundation);
        Assert.Equal(6, p.Progression.CombatExperience);
        Assert.Equal(6, p.Progression.LifetimeExperience);
    }

    /// <summary>非法入账参数（负量/NaN）在边界快速失败。</summary>
    [Fact]
    public void CreditExperience_RejectsInvalidInputs()
    {
        var p = CreatePlayer();
        Assert.Throws<DomainException>(() => p.Progression.CreditExperience(-1, 10));
        Assert.Throws<DomainException>(() => p.Progression.CreditExperience(double.NaN, 10));
        Assert.Throws<DomainException>(() => p.Progression.CreditExperience(1, -5));
        Assert.Throws<DomainException>(() => p.Progression.CreditExperience(1, 10, -1));
    }

    // ── 模拟管线集成 ──

    /// <summary>连续修为收益：逐秒统一结算，满层后全额转底蕴；非战斗行动不增加战斗经验。</summary>
    [Fact]
    public void ContinuousYield_OverflowSettlesIntoFoundation()
    {
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("test.exp-flow"), "修为流", ActionKind.Cultivation, 1,
                new Dictionary<ItemId, double>(),
                [new ActionYield(ActionYieldKind.Experience, ActionYieldPeriod.PerSecond, 2)])
        ]);
        var gameData = CatalogWithRealm(actions, expToNext: 10);
        var player = CreatePlayer();
        player.Enqueue(new ActionIntent(new ActionId("test.exp-flow"), -1), Baseline, actions);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), gameData);

        // 10 秒 × 2/秒 = 20 增幅量：先填满 10 修为，余 10 沉淀为底蕴；打坐修炼不增加战斗经验（仍为 0）。
        Assert.Equal(10, player.Progression.Experience);
        Assert.Equal(10, player.Progression.Foundation);
        Assert.Equal(0, player.Progression.CombatExperience);
        Assert.Equal(20, player.Progression.LifetimeExperience);
    }

    /// <summary>引气入体打坐修炼结算：产出修为与灵力，但绝对不增加战斗经验。</summary>
    [Fact]
    public void CultivationDrawQi_DoesNotIncreaseCombatExperience()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer();
        var action = player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 1), Baseline, catalog.Actions);

        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog);

        Assert.Equal(10, player.Progression.Experience);
        Assert.Equal(0, player.Progression.CombatExperience);
        Assert.Equal(10, player.Progression.LifetimeExperience);
        var report = player.Duel.ReportFor(action.Id) ?? player.Duel.Reports.FirstOrDefault(r => r.ActionId == "cultivation.draw-qi");
        Assert.NotNull(report);
        Assert.Equal(10, report.Experience);
        Assert.Equal(0, report.CombatExperience);
    }

    /// <summary>突破后底蕴留存并自动兑换：沉淀池在下一层按 ≤3× 加速消耗。</summary>
    [Fact]
    public void FoundationSurvivesBreakthroughAndBoostsNextLayer()
    {
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("test.exp-flow"), "修为流", ActionKind.Cultivation, 1,
                new Dictionary<ItemId, double>(),
                [new ActionYield(ActionYieldKind.Experience, ActionYieldPeriod.PerSecond, 5)])
        ]);
        var gameData = CatalogWithRealm(actions, 10, 100);
        var player = CreatePlayer();
        player.Enqueue(new ActionIntent(new ActionId("test.exp-flow"), -1), Baseline, actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(4), gameData);   // 修为 10 满层 + 底蕴 10
        Assert.Equal(10, player.Progression.Foundation);

        player.Breakthrough(gameData.RealmProgression);
        Assert.Equal(0, player.Progression.Experience);
        Assert.Equal(10, player.Progression.Foundation);                      // 突破不清空底蕴

        // 再推 1 秒：amp=5，credited = min(3×5, 5+10, 100) = 15（底蕴 10 全额兑出额外 10）
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(5), gameData);
        Assert.Equal(2, player.Progression.RealmLevel);
        Assert.Equal(15, player.Progression.Experience);
        Assert.Equal(0, player.Progression.Foundation);
    }

    /// <summary>快照导出/恢复往返保留底蕴、战斗经验与累计修为。</summary>
    [Fact]
    public void SnapshotRoundTrip_PreservesNewPools()
    {
        var player = CreatePlayer("往返");
        player.Progression.Foundation = 42;
        player.Progression.CombatExperience = 7;
        player.Progression.LifetimeExperience = 128;
        var restored = PlayerAggregate.Restore(player.ExportState());
        Assert.Equal(42, restored.Progression.Foundation);
        Assert.Equal(7, restored.Progression.CombatExperience);
        Assert.Equal(128, restored.Progression.LifetimeExperience);
    }

    // ── 战斗经验对抗（§7.7.6）──

    /// <summary>双方战斗经验相等或均 ≤0 时中立：等效输入不变。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    public void Contest_Neutral_NoChange(double attackerExp, double defenderExp)
    {
        var (accuracy, evasion) = DeterministicCombatEngine.ApplyCombatExperienceContest(100, 80, attackerExp, defenderExp);
        Assert.Equal(100, accuracy);
        Assert.Equal(80, evasion);
    }

    /// <summary>攻方 5 倍：命中 ×1.5、守方闪避 ×0.75（规格示例）。</summary>
    [Fact]
    public void Contest_FiveFoldAdvantage_150Hit75Evasion()
    {
        var (accuracy, evasion) = DeterministicCombatEngine.ApplyCombatExperienceContest(100, 80, 50, 10);
        Assert.Equal(150, accuracy);
        Assert.Equal(60, evasion);
    }

    /// <summary>镜像：守方 5 倍时攻守修正互换（命中 ×0.75、闪避 ×1.5）。</summary>
    [Fact]
    public void Contest_FiveFoldDisadvantage_Mirrored()
    {
        var (accuracy, evasion) = DeterministicCombatEngine.ApplyCombatExperienceContest(100, 80, 10, 50);
        Assert.Equal(75, accuracy);
        Assert.Equal(120, evasion);
    }

    /// <summary>倍率按 10 倍封顶、修正按 2 倍/0.5 倍封顶（r≥9 即饱和）。</summary>
    [Fact]
    public void Contest_ClampsAtTwoTimes()
    {
        var (acc9, eva9) = DeterministicCombatEngine.ApplyCombatExperienceContest(100, 80, 90, 10);
        Assert.Equal(200, acc9);
        Assert.Equal(40, eva9);
        var (accInf, evaInf) = DeterministicCombatEngine.ApplyCombatExperienceContest(100, 80, 1e9, 0.001);
        Assert.Equal(200, accInf);
        Assert.Equal(40, evaInf);
    }

    /// <summary>单边为 0：对方有经验时按 10 倍上限对抗，不产生 NaN/Inf。</summary>
    [Fact]
    public void Contest_ZeroSide_UsesRatioCap()
    {
        var (accA, evaA) = DeterministicCombatEngine.ApplyCombatExperienceContest(100, 80, 0, 50);
        Assert.True(accA is >= 50 and <= 100 && evaA is >= 80 and <= 160);
        Assert.True(double.IsFinite(accA) && double.IsFinite(evaA));
    }

    // ── 怪物击杀奖励派生（总纲 §7.10.3）──

    /// <summary>expToNext[level]/1000×0.9^(level-1)×层次倍率：Lv1 基准 1/1000，精英 3×、妖王 10×。</summary>
    [Fact]
    public void MonsterKillExperience_DerivesFromCurveAndRank()
    {
        var realm = new RealmProgressionCatalog([10_000, 20_000, 30_000, 0], [80]);
        Assert.Equal(10, realm.MonsterKillExperience(1, CombatMonsterRank.Minion), 6);
        Assert.Equal(18, realm.MonsterKillExperience(2, CombatMonsterRank.Minion), 6);
        Assert.Equal(72.9, realm.MonsterKillExperience(3, CombatMonsterRank.Elite), 6);
        Assert.Equal(243, realm.MonsterKillExperience(3, CombatMonsterRank.Boss), 6);
    }

    /// <summary>等级超出配表上界（末档 expToNext=0 的满级段）时按末档非零修为条派生，衰减仍按真实等级。</summary>
    [Fact]
    public void MonsterKillExperience_BeyondCap_UsesLastNonZeroCap()
    {
        var realm = new RealmProgressionCatalog([10_000, 20_000, 30_000, 0], [80]);
        // Lv4 配表已满级：钳到 expToNext[3]=30000，衰减 0.9^3=0.729 → 21.87。
        Assert.Equal(21.87, realm.MonsterKillExperience(4, CombatMonsterRank.Minion), 3);
    }
}
