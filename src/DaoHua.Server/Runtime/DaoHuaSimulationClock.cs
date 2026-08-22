namespace DaoHua.Server.Runtime;

/// <summary>
/// 游戏模拟单调时钟 (DaoHuaSimulationClock)。
/// <para>
/// 职责：
/// 1. 为游戏领域模拟提供绝对单调递增的 UTC 时间基准（基于系统时间源与高精度 Stopwatch 时间戳偏移量）。
/// 2. 免疫外部 NTP 授时校准或系统时钟回拨，确保战斗事件、修炼结算与状态跃迁永不发生倒退或重复结算。
/// 3. 提供统一可注入/可测试的异步延迟调度方法。
/// </para>
/// </summary>
public sealed class DaoHuaSimulationClock
{
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _originUtc;
    private readonly long _originTimestamp;

    /// <summary>
    /// 使用系统默认时间源初始化单调模拟时钟。
    /// </summary>
    public DaoHuaSimulationClock()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// 使用指定时间源初始化单调模拟时钟（测试用）。
    /// </summary>
    internal DaoHuaSimulationClock(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _originUtc = timeProvider.GetUtcNow();
        _originTimestamp = timeProvider.GetTimestamp();
    }

    /// <summary>
    /// 获取当前单调递增的 UTC 时间。
    /// </summary>
    public DateTimeOffset UtcNow =>
        _originUtc + _timeProvider.GetElapsedTime(_originTimestamp, _timeProvider.GetTimestamp());

    /// <summary>
    /// 基于统一时间源进行异步延迟等待。
    /// </summary>
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, _timeProvider, cancellationToken);
    }
}
