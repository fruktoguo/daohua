using System.Diagnostics;
using Xiuxian.Domain.Models;
using Xiuxian.Domain.Simulation;

namespace Xiuxian.LoadTests;

public static class OfflineSimulationScenario
{
    public static LoadTestReport Run(
        LoadTestOptions options,
        bool fullyOffline,
        bool includeCombat = false)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var process = Process.GetCurrentProcess();
        var cpuBeforeSetup = process.TotalProcessorTime;
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var states = new XiuxianPlayerState[options.Players];
        var baseline = DateTimeOffset.UnixEpoch.AddDays(20_000);
        var setupStopwatch = Stopwatch.StartNew();

        for (var index = 0; index < states.Length; index++)
        {
            var state = XiuxianSimulation.CreateDefaultState(CreatePlayerId(index), $"模拟修士-{index}", baseline);
            if (includeCombat)
            {
                state.Queue.Entries.Clear();
                state.Queue.LoopEnabled = true;
                state.Combat.StaminaLevel = 1_000;
                state.Combat.IntelligenceLevel = 1_000;
                state.Combat.AttackLevel = 1_000;
                state.Combat.SwordsmanshipLevel = 1_000;
                state.Combat.DefenseLevel = 1_000;
                state.Combat.TalismanLevel = 1_000;
                state.Combat.FiveElementsLevel = 1_000;
                state.Combat.Style = XiuxianCombatStyle.FiveElements;
                state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.BlazingBlast;
                state.Combat.AbilitySlots[0].Level = 100;
                state.Combat.Equipment =
                [new XiuxianEquippedItem
                {
                    ItemId = (index % 4) switch
                    {
                        0 => XiuxianItemIds.CursedSpiritBow,
                        1 => XiuxianItemIds.FuriousSpiritSpear,
                        2 => XiuxianItemIds.GriffinSpiritBulwark,
                        _ => XiuxianItemIds.SunderingSpiritCrossbow
                    },
                    EnhancementLevel = index % (XiuxianCombatLimits.MaxEnhancementLevel + 1)
                }];
                XiuxianSimulation.Enqueue(state, XiuxianActionIds.SweepTwilightValley, -1);
            }
            state.PresenceMode = fullyOffline ? XiuxianPresenceMode.FullyOffline : XiuxianPresenceMode.IdleOffline;
            state.PendingEvents.Clear();
            state.DirtySections = XiuxianDirtySections.None;
            states[index] = state;
        }

        setupStopwatch.Stop();
        process.Refresh();
        var cpuAfterSetup = process.TotalProcessorTime;
        var managedAfterSetup = GC.GetTotalMemory(forceFullCollection: false);
        var workingSetAfterSetup = process.WorkingSet64;

        long completedActions = 0;
        long transitions = 0;
        long combatEvents = 0;
        var cpuBeforeSimulation = process.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        Parallel.For(
            0,
            states.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            index =>
            {
                var result = XiuxianSimulation.AdvanceTo(states[index], baseline.AddSeconds(options.DurationSeconds));
                Interlocked.Add(ref completedActions, result.CompletedActions);
                Interlocked.Add(ref transitions, result.TransitionCount);
                Interlocked.Add(ref combatEvents, result.CombatEvents);
            });
        var rankingChecksum = states.Sum(state => state.Stats.Qi + state.Stats.CompletedActions);
        var combatWins = states.Sum(state => state.Stats.CombatWins);
        var combatLosses = states.Sum(state => state.Stats.CombatLosses);
        var combatEnemiesDefeated = states.Sum(state => state.Stats.CombatEnemiesDefeated);
        var combatEncounterCompletions = states.Sum(state =>
            state.CombatEncounterCompletions.GetValueOrDefault(XiuxianCombatEncounterIds.TwilightValley));
        var pendingEventCount = states.Sum(state => state.PendingEvents.Count);
        stopwatch.Stop();

        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var managedAfter = GC.GetTotalMemory(forceFullCollection: false);
        return new LoadTestReport(
            fullyOffline ? "fully-offline" : includeCombat ? "combat-offline" : "idle-offline",
            options.Players,
            options.DurationSeconds,
            stopwatch.Elapsed.TotalMilliseconds,
            (cpuAfter - cpuBeforeSimulation).TotalMilliseconds,
            managedBefore,
            managedAfter,
            process.WorkingSet64,
            0,
            0,
            0,
            0,
            completedActions,
            transitions,
            0,
            new
            {
                rankingChecksum,
                initializationElapsedMilliseconds = setupStopwatch.Elapsed.TotalMilliseconds,
                initializationCpuMilliseconds = (cpuAfterSetup - cpuBeforeSetup).TotalMilliseconds,
                initializedManagedBytes = managedAfterSetup - managedBefore,
                initializedWorkingSetBytes = workingSetAfterSetup,
                combatEvents,
                combatWins,
                combatLosses,
                combatEnemiesDefeated,
                combatEncounterCompletions,
                pendingEventCount
            },
            fullyOffline
                ? "完全离线账号不推进队列，仅参与一次排行榜聚合校验。"
                : includeCombat
                    ? "离线挂机使用正式行动队列和多敌人事件边界模拟器补算暮影谷；玩家使用固定千级流派、100 级群攻功法，并按固定分布携带四类法器与 0-20 级强化，不按秒生成战斗帧。"
                    : "离线挂机使用与服务端相同的事件边界快进算法，一次补算完整时段。" );
    }

    private static Guid CreatePlayerId(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index + 1);
        bytes[15] = 0x7A;
        return new Guid(bytes);
    }

}
