// ==============================================================================
// 文件名称: DaoHuaSimulationScheduleSignal.cs
// 主要职责: 模拟事件调度唤醒信号量，驱动后台调度循环重新评估截止时间
// 核心逻辑 / 定义内容:
//   1. 当玩家上线、离线或指令变更导致下次事件截止时间改变时发送唤醒脉冲。
//   2. 基于容量为 1 的 SemaphoreSlim 实现信号合并防抖（Debounce），避免高频唤醒惊群。
//   3. 提供带超时的异步信号等待接口（WaitAsync）。
// ==============================================================================

namespace DaoHua.Server.Runtime;

/// <summary>
/// 模拟事件调度唤醒信号量 (DaoHuaSimulationScheduleSignal)。
/// <para>
/// 职责：
/// 1. 当玩家在线状态、行动队列变更或执行指令导致下次跃迁时间变化时，主动唤醒模拟后台调度循环。
/// 2. 基于最大容量为 1 的 SemaphoreSlim 实现合并防抖（Debounce）：短时间内的高频触发仅产生一次调度器重新评估，防止无意义的惊群唤醒。
/// </para>
/// </summary>
public sealed class DaoHuaSimulationScheduleSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>
    /// 发送唤醒脉冲信号；若已有未消费的信号则自动忽略（幂等合并）。
    /// </summary>
    public void Pulse()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已有待处理唤醒，调度器重新计算时会覆盖本次变化。
        }
    }

    /// <summary>
    /// 异步等待调度信号触发或超时。
    /// </summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _signal.WaitAsync(timeout, cancellationToken);
    }
}
