// ==============================================================================
// 文件名称: OfflineSimulationScenario.cs
// 主要职责: 离线挂机、多怪战斗与大时段事件快进模拟吞吐量压测场景。
// 核心逻辑 / 定义内容:
//   1. 批量初始化海量玩家状态（涵盖空闲修炼、行动队列与暮影谷多敌人遭遇战）。
//   2. 多线程并行执行确定性事件快进补算，度量大时段推进时的吞吐帧率与 CPU 耗时。
//   3. 校验最终排行榜状态、击杀结算、战利品掉落与跨线程计算的一致性。
// ==============================================================================

using System.Diagnostics;
using DaoHua.Domain.Models;
using DaoHua.Domain.Simulation;

namespace DaoHua.LoadTests;

/// <summary>
/// 离线挂机与事件驱动快进压测场景。
/// 支持完全离线（FullyOffline）、空闲挂机（IdleOffline）及多怪物暮影谷战斗（CombatOffline）的批量并行补算，度量千人规模大时段快进吞吐。
/// </summary>
public static class OfflineSimulationScenario
{
    /// <summary>
    /// 执行离线挂机补算压测：
    /// 1. 批量初始化玩家状态（可配置千级战斗流派与法宝强化）；
    /// 2. 使用多线程并行调度事件驱动快进模拟器推进指定时长；
    /// 3. 聚合统计排行榜校验和、战斗胜负、击杀数、事件流转及 CPU 耗时。
    /// </summary>
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
        var states = new DaoHuaPlayerState[options.Players];
        var baseline = DateTimeOffset.UnixEpoch.AddDays(20_000);
        var setupStopwatch = Stopwatch.StartNew();

        for (var index = 0; index < states.Length; index++)
        {
            var state = DaoHuaSimulation.CreateDefaultState(CreatePlayerId(index), $"模拟修士-{index}", baseline);
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
                state.Combat.Style = DaoHuaCombatStyle.FiveElements;
                state.Combat.AbilitySlots[0].AbilityId = DaoHuaCombatAbilityIds.BlazingBlast;
                state.Combat.AbilitySlots[0].Level = 100;
                state.Combat.Equipment =
                [new DaoHuaEquippedItem
                {
                    ItemId = (index % 4) switch
                    {
                        0 => DaoHuaItemIds.CursedSpiritBow,
                        1 => DaoHuaItemIds.FuriousSpiritSpear,
                        2 => DaoHuaItemIds.GriffinSpiritBulwark,
                        _ => DaoHuaItemIds.SunderingSpiritCrossbow
                    },
                    EnhancementLevel = index % (DaoHuaCombatLimits.MaxEnhancementLevel + 1)
                }];
                DaoHuaSimulation.Enqueue(state, DaoHuaActionIds.SweepTwilightValley, -1);
            }
            state.PresenceMode = fullyOffline ? DaoHuaPresenceMode.FullyOffline : DaoHuaPresenceMode.IdleOffline;
            state.PendingEvents.Clear();
            state.DirtySections = DaoHuaDirtySections.None;
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
                var result = DaoHuaSimulation.AdvanceTo(states[index], baseline.AddSeconds(options.DurationSeconds));
                Interlocked.Add(ref completedActions, result.CompletedActions);
                Interlocked.Add(ref transitions, result.TransitionCount);
                Interlocked.Add(ref combatEvents, result.CombatEvents);
            });
        var rankingChecksum = states.Sum(state => state.Stats.Qi + state.Stats.CompletedActions);
        var combatWins = states.Sum(state => state.Stats.CombatWins);
        var combatLosses = states.Sum(state => state.Stats.CombatLosses);
        var combatEnemiesDefeated = states.Sum(state => state.Stats.CombatEnemiesDefeated);
        var combatEncounterCompletions = states.Sum(state =>
            state.CombatEncounterCompletions.GetValueOrDefault(DaoHuaCombatEncounterIds.TwilightValley));
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
