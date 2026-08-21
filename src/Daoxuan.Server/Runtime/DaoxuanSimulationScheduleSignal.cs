namespace Daoxuan.Server.Runtime;

/// <summary>
/// 当在线状态、行动队列或资源发生变化时唤醒截止时间调度器。
/// 容量为一，连续变化只需触发一次重新计算，不累积无意义的唤醒次数。
/// </summary>
public sealed class DaoxuanSimulationScheduleSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

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

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _signal.WaitAsync(timeout, cancellationToken);
    }
}
