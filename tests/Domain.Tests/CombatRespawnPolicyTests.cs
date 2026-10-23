using DaoHua.Ecs.Systems;
using Domain.Combat;

namespace Domain.Tests;

/// <summary>
/// 组队复活时机策略门禁（总纲 §7.9.4.1）：
/// 直接复活 / 全员可复活或存活 / 指定队友存活三分支的起身时机、
/// 条件不成立时的时钟推进（绝不允许空转）、以及等待硬上限兜底。
/// </summary>
public sealed class CombatRespawnPolicyTests
{
    /// <summary>
    /// 缺省策略即直接复活：读条到点立刻起身，与既有单人/组队行为逐位一致。
    /// </summary>
    [Fact]
    public void DefaultPolicyRespawnsImmediatelyAtDeadline()
    {
        var encounter = CreateTwoAllyEncounter(respawnSeconds: 10);
        DownAlly(encounter, 0);

        // 直接复活策略下门禁恒通过：读条到点即可起身。
        Assert.True(encounter.CanRespawn(0, out _));

        DeterministicCombatEngine.AdvanceTo(encounter, 11.5);
        Assert.True(encounter.PlayerAllies[0].IsAlive);
        Assert.Equal(double.PositiveInfinity, encounter.PlayerAllies[0].RespawnAtSeconds);
    }

    /// <summary>
    /// 「全员可复活或存活」：另一名队友存活时条件成立，读条到点即起身。
    /// </summary>
    [Fact]
    public void AllAlliesReadyRespawnsWhenEveryAllyIsAliveOrPending()
    {
        var encounter = CreateTwoAllyEncounter(respawnSeconds: 10);
        encounter.PlayerAllies[0].RespawnPolicy = CombatRespawnPolicy.AllAlliesReady;
        DownAlly(encounter, 0);

        // 队友 1 存活 → 条件成立，直接按读条起身。
        Assert.True(encounter.PlayerAllies[1].IsAlive);
        DeterministicCombatEngine.AdvanceTo(encounter, 11.5);
        Assert.True(encounter.PlayerAllies[0].IsAlive);
    }

    /// <summary>
    /// 「全员可复活或存活」在队友彻底出局（无待触发复活点）时不成立：
    /// 读条到点后进入等待态，且战斗时钟必须继续推进——不得在同一过期时刻空转到事件上限。
    /// </summary>
    [Fact]
    public void AllAlliesReadyWaitsWhilePeerIsPermanentlyOutAndClockStillAdvances()
    {
        var encounter = CreateTwoAllyEncounter(respawnSeconds: 10);
        encounter.PlayerAllies[0].RespawnPolicy = CombatRespawnPolicy.AllAlliesReady;
        DownAlly(encounter, 0);
        // 队友 1 彻底出局：阵亡且无待触发复活点。
        KillPermanently(encounter, 1);

        Assert.False(encounter.CanRespawn(0, out var retryAt));
        Assert.True(retryAt > encounter.ElapsedSeconds);

        // 推进越过读条点：盟友 0 仍倒地（条件不成立），但时钟确实推进而非卡死。
        DeterministicCombatEngine.AdvanceTo(encounter, 12.5);
        Assert.False(encounter.PlayerAllies[0].IsAlive);
        Assert.Equal(12.5, encounter.ElapsedSeconds, 9);
        // 等待态标记可供表现层区分「读条未满」与「等待条件」。
        Assert.True(double.IsFinite(encounter.PlayerAllies[0].RespawnWaitingUntilSeconds));
    }

    /// <summary>
    /// 等待硬上限兜底：条件永久不成立时，到「阵亡时刻 + 2 × 读条」强制起身，战场必然推进。
    /// </summary>
    [Fact]
    public void WaitDeadlineForcesRespawnWhenConditionNeverHolds()
    {
        var encounter = CreateTwoAllyEncounter(respawnSeconds: 10);
        encounter.PlayerAllies[0].RespawnPolicy = CombatRespawnPolicy.AllAlliesReady;
        DownAlly(encounter, 0);
        KillPermanently(encounter, 1);
        // 硬上限 = 阵亡时刻 + 2 × 读条（此处阵亡发生在 t=0，读条 10 → 20）。
        Assert.Equal(20, encounter.PlayerAllies[0].RespawnDeadlineSeconds, 9);

        DeterministicCombatEngine.AdvanceTo(encounter, 21);
        Assert.True(encounter.PlayerAllies[0].IsAlive);
    }

    /// <summary>
    /// 「指定队友存活」：读条走完后才判定条件。目标仍在倒地读条中时继续等待，
    /// 目标起身后门禁复查通过并立即起身。
    /// </summary>
    [Fact]
    public void TargetAllyAliveWaitsUntilTargetStandsUp()
    {
        var encounter = CreateTwoAllyEncounter(respawnSeconds: 10);
        encounter.PlayerAllies[0].RespawnPolicy = CombatRespawnPolicy.TargetAllyAlive;
        encounter.PlayerAllies[0].RespawnTargetIndex = 1;
        // 本人读条在 t=5，目标队友读条在 t=10：本人到点时目标仍在倒地，必须继续等。
        DownAlly(encounter, 0, respawnAtSeconds: 5);
        DownAlly(encounter, 1, respawnAtSeconds: 10);

        Assert.False(encounter.CanRespawn(0, out _));

        // t=5 本人读条走完但目标未存活 → 倒地等待，不误起身。
        DeterministicCombatEngine.AdvanceTo(encounter, 6);
        Assert.False(encounter.PlayerAllies[0].IsAlive);
        Assert.True(double.IsFinite(encounter.PlayerAllies[0].RespawnWaitingUntilSeconds));

        // t=10 目标起身；下一次判定点本人条件成立即起身。
        DeterministicCombatEngine.AdvanceTo(encounter, 12);
        Assert.True(encounter.PlayerAllies[1].IsAlive);
        Assert.True(encounter.PlayerAllies[0].IsAlive);
    }

    /// <summary>
    /// 「指定队友存活」在读条走完时目标已存活：条件成立，读条到点直接起身。
    /// </summary>
    [Fact]
    public void TargetAllyAliveRespawnsImmediatelyWhenTargetAlreadyAlive()
    {
        var encounter = CreateTwoAllyEncounter(respawnSeconds: 10);
        encounter.PlayerAllies[0].RespawnPolicy = CombatRespawnPolicy.TargetAllyAlive;
        encounter.PlayerAllies[0].RespawnTargetIndex = 1;
        DownAlly(encounter, 0, respawnAtSeconds: 10);

        // 目标队友始终存活（未倒地）。
        DeterministicCombatEngine.AdvanceTo(encounter, 11);
        Assert.True(encounter.PlayerAllies[0].IsAlive);
    }

    /// <summary>
    /// 「指定队友存活」目标下标失效时退化为直接复活：读条到点立即起身，不引入任何等待。
    /// </summary>
    [Fact]
    public void TargetAllyAliveFallsBackToImmediateWhenTargetIsUnset()
    {
        var encounter = CreateTwoAllyEncounter(respawnSeconds: 10);
        encounter.PlayerAllies[0].RespawnPolicy = CombatRespawnPolicy.TargetAllyAlive;
        encounter.PlayerAllies[0].RespawnTargetIndex = -1;
        DownAlly(encounter, 0);

        DeterministicCombatEngine.AdvanceTo(encounter, 11.5);
        Assert.True(encounter.PlayerAllies[0].IsAlive);
    }

    /// <summary>
    /// 策略字段随战斗快照往返不漂移：崩溃恢复后等待条件与硬上限输入完整保留。
    /// </summary>
    [Fact]
    public void RespawnPolicySurvivesSnapshotRoundTrip()
    {
        var encounter = CreateTwoAllyEncounter(respawnSeconds: 10);
        encounter.PlayerAllies[0].RespawnPolicy = CombatRespawnPolicy.TargetAllyAlive;
        encounter.PlayerAllies[0].RespawnTargetIndex = 1;
        DownAlly(encounter, 0);

        var snapshot = CombatEncounterState.Snapshot(encounter.PlayerAllies[0]);
        Assert.Equal(CombatRespawnPolicy.TargetAllyAlive, snapshot.RespawnPolicy);
        Assert.Equal(1, snapshot.RespawnTargetIndex);
        Assert.NotNull(snapshot.RespawnDeadlineSeconds);

        var restored = CombatEncounterState.RestoreAllied(encounter.EncounterId, encounter.Seed,
            [new CombatantTemplate(encounter.PlayerAllies[0].Stats, encounter.PlayerAllies[0].Skills),
             new CombatantTemplate(encounter.PlayerAllies[1].Stats, encounter.PlayerAllies[1].Skills)],
            [snapshot, CombatEncounterState.Snapshot(encounter.PlayerAllies[1])],
            selfIndex: 0,
            [new CombatantTemplate(encounter.Enemies[0].Stats, encounter.Enemies[0].Skills)],
            [CombatEncounterState.Snapshot(encounter.Enemies[0])],
            encounter.ElapsedSeconds, CombatOutcome.Running, encounter.Sequence, playerRespawnSeconds: 10);

        Assert.Equal(CombatRespawnPolicy.TargetAllyAlive, restored.PlayerAllies[0].RespawnPolicy);
        Assert.Equal(1, restored.PlayerAllies[0].RespawnTargetIndex);
        Assert.Equal(encounter.PlayerAllies[0].RespawnDeadlineSeconds,
            restored.PlayerAllies[0].RespawnDeadlineSeconds, 9);
    }

    /// <summary>
    /// 观察对象离队后清理设置：指名到该队友的「指定队友存活」收敛为直接复活，
    /// 避免本人界面停在指向已不存在队友的策略上（战斗侧虽已退化，但设置必须同步收敛）。
    /// </summary>
    [Fact]
    public void ClearingDepartedTargetRevertsPolicyToImmediate()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "复活策略", DateTimeOffset.UnixEpoch, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(player, catalog);
        var teammate = Guid.NewGuid();

        player.SetRespawnPolicy(CombatRespawnPolicy.TargetAllyAlive, teammate);
        Assert.Equal(CombatRespawnPolicy.TargetAllyAlive, player.Settings.RespawnPolicy);
        Assert.Equal(teammate, player.Settings.RespawnTargetPlayerId);

        player.ClearRespawnTargetIf(teammate);
        Assert.Equal(CombatRespawnPolicy.Immediate, player.Settings.RespawnPolicy);
        Assert.Null(player.Settings.RespawnTargetPlayerId);
    }

    /// <summary>
    /// 清理只针对真正离队的观察对象：其他成员离队不得影响本人策略（否则多人队里任意人退出都会误伤）。
    /// </summary>
    [Fact]
    public void ClearingUnrelatedTargetLeavesPolicyUntouched()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "复活策略", DateTimeOffset.UnixEpoch, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(player, catalog);
        var target = Guid.NewGuid();

        player.SetRespawnPolicy(CombatRespawnPolicy.TargetAllyAlive, target);
        player.ClearRespawnTargetIf(Guid.NewGuid());

        Assert.Equal(CombatRespawnPolicy.TargetAllyAlive, player.Settings.RespawnPolicy);
        Assert.Equal(target, player.Settings.RespawnTargetPlayerId);
    }

    /// <summary>
    /// 双盟友场景：高血量敌人确保战斗不会因敌方全灭提前结束。
    /// </summary>
    private static CombatEncounterState CreateTwoAllyEncounter(double respawnSeconds)
    {
        var allies = new[]
        {
            new CombatantTemplate(CombatantStats.Create(500, 50, 1, 1.0)),
            new CombatantTemplate(CombatantStats.Create(500, 50, 1, 1.0)),
        };
        var enemy = CombatantStats.Create(1_000_000, 1, 0, 60);
        var encounter = CombatEncounterState.CreateAllied("party.respawn.policy", 97,
            allies, selfIndex: 0, [new CombatantTemplate(enemy)], respawnSeconds);
        CombatActSystem.DecideInitialActions(encounter);
        return encounter;
    }

    /// <summary>让指定盟友进入倒地读条（直接写倒地口径，等价于 CombatAllyDownedSystem 的赋值）。</summary>
    private static void DownAlly(CombatEncounterState encounter, int index, double? respawnAtSeconds = null)
    {
        var ally = encounter.PlayerAllies[index];
        ally.Hitpoints = 0;
        ally.RespawnAtSeconds = respawnAtSeconds ?? encounter.ElapsedSeconds + encounter.PlayerRespawnSeconds;
        // 等待硬上限与倒地相位同口径：阵亡时刻 + 2 × 读条。
        ally.RespawnDeadlineSeconds = encounter.ElapsedSeconds + encounter.PlayerRespawnSeconds * 2;
    }

    /// <summary>让指定盟友彻底出局：阵亡且无待触发复活点（团灭即败遭遇的形态）。</summary>
    private static void KillPermanently(CombatEncounterState encounter, int index)
    {
        var ally = encounter.PlayerAllies[index];
        ally.Hitpoints = 0;
        ally.RespawnAtSeconds = double.PositiveInfinity;
        ally.RespawnDeadlineSeconds = double.PositiveInfinity;
    }
}
