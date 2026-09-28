using Domain.Combat;
using Domain.Common;
using System.Diagnostics.Metrics;

namespace Domain.Tests;

/// <summary>
/// 单事件边界 API 的确定性门禁：逐边界推进必须与现有 AdvanceTo 的整段推进逐位一致。
/// 该测试只验证 Domain 原语，不把 ECS 宿主或网络/存储实现带入领域程序集。
/// </summary>
public sealed class CombatBoundaryTests
{
    [Fact]
    public void AdvancingOneBoundaryAtATimeMatchesAdvanceTo()
    {
        var observedBoundaries = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RuntimeInstrumentation.MeterName)
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            if (instrument.Name != "daohua.runtime.duration") return;
            var stageMatches = false;
            var scenarioMatches = false;
            foreach (var tag in tags)
            {
                stageMatches |= tag.Key == "stage" && Equals(tag.Value, "domain.combat.event");
                scenarioMatches |= tag.Key == "scenario" && Equals(tag.Value, "combat");
            }
            if (stageMatches && scenarioMatches) Interlocked.Increment(ref observedBoundaries);
        });
        listener.Start();
        using var scenario = RuntimeInstrumentation.BeginScenario("combat");

        var player = CombatantStats.Create(500, 80, 0, 1.7) with { MaxManapoints = 500, MpRegen = 2 };
        var enemy = CombatantStats.Create(700, 45, 0, 2.1) with { HpRegen = 1 };
        var whole = DeterministicCombatEngine.Start("boundary", 12345, player, enemy, playerRespawnSeconds: 5);
        var stepped = DeterministicCombatEngine.Start("boundary", 12345, player, enemy, playerRespawnSeconds: 5);
        stepped.Enemies[0].Buffs.Add(new CombatBuffState("short", 3.5, 1, HpRegen: 4));
        whole.Enemies[0].Buffs.Add(new CombatBuffState("short", 3.5, 1, HpRegen: 4));

        const double target = 20;
        var expected = DeterministicCombatEngine.AdvanceTo(whole, target);
        var events = new List<CombatEvent>();
        var deathTimes = new List<double>();
        var respawnTimes = new List<double>();
        while (stepped.ElapsedSeconds < target - 1e-12 && stepped.Outcome == CombatOutcome.Running)
        {
            var next = DeterministicCombatEngine.NextBoundarySeconds(stepped, target);
            Assert.InRange(next, stepped.ElapsedSeconds, target);
            var result = DeterministicCombatEngine.AdvanceOneBoundary(stepped, target);
            events.AddRange(result.Events);
            if (result.PlayerDeathTimestamps is not null) deathTimes.AddRange(result.PlayerDeathTimestamps);
            if (result.PlayerRespawnTimestamps is not null) respawnTimes.AddRange(result.PlayerRespawnTimestamps);
            Assert.True(stepped.ElapsedSeconds >= next - 1e-12);
        }

        Assert.Equal(expected.Outcome, stepped.Outcome);
        Assert.Equal(expected.Sequence, stepped.Sequence);
        Assert.Equal(whole.ElapsedSeconds, stepped.ElapsedSeconds, 12);
        Assert.Equal(CombatEncounterState.Snapshot(whole.Player), CombatEncounterState.Snapshot(stepped.Player));
        Assert.Equal(CombatEncounterState.Snapshot(whole.Enemies[0]), CombatEncounterState.Snapshot(stepped.Enemies[0]));
        Assert.Equal(expected.Events, events);
        Assert.Equal(expected.PlayerDeathTimestamps ?? [], deathTimes);
        Assert.Equal(expected.PlayerRespawnTimestamps ?? [], respawnTimes);
        Assert.True(observedBoundaries >= 2);
    }

    [Fact]
    public void TargetBeforeNextBoundaryOnlyAdvancesClock()
    {
        var whole = DeterministicCombatEngine.Start("partial", 77,
            CombatantStats.Create(500, 1, 0, 10), CombatantStats.Create(500, 1, 0, 10));
        var stepped = DeterministicCombatEngine.Start("partial", 77,
            CombatantStats.Create(500, 1, 0, 10), CombatantStats.Create(500, 1, 0, 10));

        var expected = DeterministicCombatEngine.AdvanceTo(whole, 0.25);
        var result = DeterministicCombatEngine.AdvanceOneBoundary(stepped, 0.25);

        Assert.Empty(result.Events);
        Assert.Equal(expected.ToSeconds, result.ToSeconds, 12);
        Assert.Equal(expected.ToSeconds, stepped.ElapsedSeconds, 12);
        Assert.Equal(expected.Sequence, stepped.Sequence);
        Assert.Equal(CombatEncounterState.Snapshot(whole.Player), CombatEncounterState.Snapshot(stepped.Player));
    }

    [Fact]
    public void BuffExpiryAtAttackBoundaryKeepsSequenceAndEventOrderStable()
    {
        // 双方首次普攻同刻，Buff 也在该时刻到期；到期必须先于同刻出手，
        // 且逐边界路径与整段路径的事件顺序、生命和序号保持一致。
        var player = CombatantStats.Create(500, 50, 0, 2);
        var enemy = CombatantStats.Create(500, 50, 0, 2);
        var whole = DeterministicCombatEngine.Start("same-boundary", 88, player, enemy);
        var stepped = DeterministicCombatEngine.Start("same-boundary", 88, player, enemy);
        whole.Player.Buffs.Add(new CombatBuffState("expiry", 2));
        stepped.Player.Buffs.Add(new CombatBuffState("expiry", 2));

        var expected = DeterministicCombatEngine.AdvanceTo(whole, 2);
        var actual = DeterministicCombatEngine.AdvanceOneBoundary(stepped, 2);

        Assert.Equal(2, actual.ToSeconds, 12);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.Events, actual.Events);
        Assert.Empty(stepped.Player.Buffs);
        Assert.Equal(CombatEncounterState.Snapshot(whole.Player), CombatEncounterState.Snapshot(stepped.Player));
        Assert.Equal(CombatEncounterState.Snapshot(whole.Enemies[0]), CombatEncounterState.Snapshot(stepped.Enemies[0]));
    }

    [Fact]
    public void DownedRespawnBoundaryIsReturnedAsOneBoundary()
    {
        var player = CombatantStats.Create(20, 1, 0, 10);
        var enemy = CombatantStats.Create(500, 100, 0, 1);
        var whole = DeterministicCombatEngine.Start("respawn-boundary", 91, player, enemy, playerRespawnSeconds: 3);
        var stepped = DeterministicCombatEngine.Start("respawn-boundary", 91, player, enemy, playerRespawnSeconds: 3);

        var expected = DeterministicCombatEngine.AdvanceTo(whole, 4);
        CombatAdvanceResult? last = null;
        var events = new List<CombatEvent>();
        var deathTimes = new List<double>();
        var respawnTimes = new List<double>();
        while (stepped.ElapsedSeconds < 4 - 1e-12 && stepped.Outcome == CombatOutcome.Running)
        {
            last = DeterministicCombatEngine.AdvanceOneBoundary(stepped, 4);
            events.AddRange(last.Events);
            if (last.PlayerDeathTimestamps is not null) deathTimes.AddRange(last.PlayerDeathTimestamps);
            if (last.PlayerRespawnTimestamps is not null) respawnTimes.AddRange(last.PlayerRespawnTimestamps);
        }

        Assert.NotNull(last);
        Assert.Equal(expected.Outcome, stepped.Outcome);
        Assert.Equal(expected.Sequence, stepped.Sequence);
        Assert.Equal(expected.Events, events);
        Assert.Equal(expected.PlayerDeathTimestamps ?? [], deathTimes);
        Assert.Equal(expected.PlayerRespawnTimestamps ?? [], respawnTimes);
        Assert.Equal(CombatEncounterState.Snapshot(whole.Player), CombatEncounterState.Snapshot(stepped.Player));
        Assert.Equal(CombatEncounterState.Snapshot(whole.Enemies[0]), CombatEncounterState.Snapshot(stepped.Enemies[0]));
    }
}
