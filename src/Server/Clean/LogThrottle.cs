namespace Server.Clean;

/// <summary>
/// 进程内错误日志节流。数据库故障时状态证据仍写入队列/账本，
/// 但不允许每个请求或每轮 poll 把同一异常刷满本地磁盘。
/// </summary>
internal sealed class LogThrottle(TimeSpan interval)
{
    private readonly long _intervalTicks = interval > TimeSpan.Zero ? interval.Ticks : TimeSpan.TicksPerMinute;
    private long _nextAllowedUtcTicks;

    /// <summary>
    /// 距上次放行超过配置间隔时放行一次。并发下同一窗口内只有一个调用者拿到 true。
    /// </summary>
    /// <returns>本次调用被放行返回 true，窗口期内重复调用返回 false</returns>
    public bool TryAcquire()
    {
        var now = DateTimeOffset.UtcNow.Ticks;
        while (true)
        {
            var next = Interlocked.Read(ref _nextAllowedUtcTicks);
            if (now < next) return false;
            if (Interlocked.CompareExchange(ref _nextAllowedUtcTicks, checked(now + _intervalTicks), next) == next)
                return true;
        }
    }
}
