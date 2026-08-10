namespace Xiuxian.Server.Runtime;

/// <summary>
/// 为游戏模拟提供单调递增的 UTC 时间轴。
/// 系统时间校准或回拨不会让已经排定的战斗事件倒退或重复结算。
/// </summary>
public sealed class XiuxianSimulationClock
{
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _originUtc;
    private readonly long _originTimestamp;

    public XiuxianSimulationClock()
        : this(TimeProvider.System)
    {
    }

    internal XiuxianSimulationClock(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _originUtc = timeProvider.GetUtcNow();
        _originTimestamp = timeProvider.GetTimestamp();
    }

    public DateTimeOffset UtcNow =>
        _originUtc + _timeProvider.GetElapsedTime(_originTimestamp, _timeProvider.GetTimestamp());

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, _timeProvider, cancellationToken);
    }
}
