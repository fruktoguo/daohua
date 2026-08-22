// ==============================================================================
// 文件名称: CapacityTargetScenario.cs
// 主要职责: 2c4g/40g 目标硬件规格下的服务器容量指标与性能门禁压测场景。
// 核心逻辑 / 定义内容:
//   1. 验证 5000+ 注册账号与玩家全量快照的序列化体积与内存存储预算（<=256MB）。
//   2. 验证 500 高峰并发命令二进制编码吞吐与网络带宽预算（<=512KB/s）。
//   3. 验证 2000 冷挂机玩家 1 小时离线进度并行补算的 CPU 执行耗时（<=30s）。
//   4. 监控常驻进程物理工作集内存（<=768MB），生成容量门禁验收指标报告。
// ==============================================================================

using System.Diagnostics;
using System.Text.Json;
using Domain.Models;
using Domain.Simulation;
using Protocol;

namespace LoadTests;

/// <summary>
/// 2c4g/40g 服务器硬件目标容量门禁压测场景。
/// 验证 5000+ 注册账号存储预算（<=256MB）、500 高峰命令网络吞吐（<=512KB/s）、2000 冷挂机 1 小时补算耗时（<=30s）以及常驻工作集内存（<=768MB）。
/// </summary>
public static class CapacityTargetScenario
{
    private const int RegisteredAccountTarget = 5_000;
    private const int SustainedNetworkTarget = 100;
    private const int PeakNetworkTarget = 500;
    private const int IdleComputeTarget = 2_000;
    private const long StorageBudgetBytes = 256L * 1024 * 1024;
    private const long PeakNetworkBudgetBytesPerSecond = 512L * 1024;
    private const double IdleComputeBudgetMilliseconds = 30_000;
    private const long WorkingSetBudgetBytes = 768L * 1024 * 1024;

    /// <summary>
    /// 执行容量门禁全项验收：
    /// 1. 序列化并统计 5000 个玩家账号与全量快照体积，核验存储预算；
    /// 2. 编码 500 个并发命令包体，核验峰值带宽预算；
    /// 3. 并行多线程快进推进 2000 个离线挂机玩家 1 小时进度，核验 CPU 补算耗时；
    /// 4. 统计物理工作集内存（WorkingSet64），核验 2c4g 内存预算。
    /// </summary>
    public static LoadTestReport Run(LoadTestOptions options)
    {
        _ = options;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var process = Process.GetCurrentProcess();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var cpuBefore = process.TotalProcessorTime;
        var baseline = DateTimeOffset.UnixEpoch.AddDays(20_000);
        var stopwatch = Stopwatch.StartNew();

        var accountPayloadBytes = 0L;
        var snapshotPayloadBytes = 0L;
        for (var index = 0; index < RegisteredAccountTarget; index++)
        {
            var state = SimulationEngine.CreateDefaultState(CreatePlayerId(index), $"修士-{index}", baseline, $"account-{index}");
            state.SequenceId = index + 1;
            state.PresenceMode = PresenceMode.IdleOffline;
            state.PendingEvents.Clear();
            state.DirtySections = DirtySections.None;
            snapshotPayloadBytes += JsonSerializer.SerializeToUtf8Bytes(state, PacketCodec.JsonOptions).LongLength;
            accountPayloadBytes += 16 + 32 + 128 + 48 + 32 + sizeof(long) + 16;
        }

        var commandPacketBytes = 0L;
        for (var index = 0; index < PeakNetworkTarget; index++)
        {
            commandPacketBytes += PacketCodec.Encode(
                PacketKinds.Command,
                $"capacity-{index}",
                index,
                new
                {
                    command = "set_loop",
                    payload = new { enabled = index < SustainedNetworkTarget }
                }).LongLength;
        }

        var states = new PlayerState[IdleComputeTarget];
        for (var index = 0; index < states.Length; index++)
        {
            var state = SimulationEngine.CreateDefaultState(CreatePlayerId(index + RegisteredAccountTarget), $"挂机修士-{index}", baseline);
            state.PresenceMode = PresenceMode.IdleOffline;
            state.PendingEvents.Clear();
            state.DirtySections = DirtySections.None;
            states[index] = state;
        }

        var simulationStarted = Stopwatch.GetTimestamp();
        long completedActions = 0;
        long transitions = 0;
        Parallel.For(
            0,
            states.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            index =>
            {
                var result = SimulationEngine.AdvanceTo(states[index], baseline.AddSeconds(3600));
                Interlocked.Add(ref completedActions, result.CompletedActions);
                Interlocked.Add(ref transitions, result.TransitionCount);
            });
        var idleComputeMilliseconds = Stopwatch.GetElapsedTime(simulationStarted).TotalMilliseconds;

        stopwatch.Stop();
        process.Refresh();
        var managedAfter = GC.GetTotalMemory(forceFullCollection: false);
        var workingSet = process.WorkingSet64;
        var storageBytes = accountPayloadBytes + snapshotPayloadBytes;
        var errors = 0;
        if (storageBytes > StorageBudgetBytes) errors++;
        if (commandPacketBytes > PeakNetworkBudgetBytesPerSecond) errors++;
        if (idleComputeMilliseconds > IdleComputeBudgetMilliseconds) errors++;
        if (workingSet > WorkingSetBudgetBytes) errors++;

        return new LoadTestReport(
            "capacity-target",
            RegisteredAccountTarget,
            3600,
            stopwatch.Elapsed.TotalMilliseconds,
            (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            managedBefore,
            managedAfter,
            workingSet,
            commandPacketBytes,
            0,
            PeakNetworkTarget,
            0,
            completedActions,
            transitions,
            errors,
            new
            {
                target = new
                {
                    registeredAccounts = RegisteredAccountTarget,
                    sustainedNetworkPlayers = SustainedNetworkTarget,
                    peakNetworkPlayers = PeakNetworkTarget,
                    idleComputePlayers = IdleComputeTarget,
                    serverClass = "2c4g-40g"
                },
                budgets = new
                {
                    storageBytes = StorageBudgetBytes,
                    peakNetworkBytesPerSecond = PeakNetworkBudgetBytesPerSecond,
                    idleComputeMilliseconds = IdleComputeBudgetMilliseconds,
                    workingSetBytes = WorkingSetBudgetBytes
                },
                actual = new
                {
                    estimatedAccountBytes = accountPayloadBytes,
                    estimatedSnapshotBytes = snapshotPayloadBytes,
                    estimatedStorageBytes = storageBytes,
                    peakCommandBytesPerSecond = commandPacketBytes,
                    idleComputeMilliseconds,
                    workingSetBytes = workingSet
                }
            },
            errors == 0
                ? "容量门禁通过：5000+ 账号存储、500 高峰命令包体、2000 冷挂机补算均在 2c4g/40g 目标预算内。"
                : "容量门禁失败：至少一项超出 2c4g/40g 目标预算。" );
    }

    private static Guid CreatePlayerId(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index + 1);
        bytes[15] = 0xD0;
        return new Guid(bytes);
    }
}
