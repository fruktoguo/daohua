// ==============================================================================
// 文件名称: DaoHuaSimulationClock.cs
// 主要职责: 游戏模拟单调时钟，提供绝对单调递增的 UTC 时间基准
// 核心逻辑 / 定义内容:
//   1. 基于统一时间源与高精度 Stopwatch 时间戳偏移量计算单调 UTC 时间。
//   2. 免疫外部 NTP 授时校准或系统时钟回拨，杜绝战斗与模拟事件倒退。
//   3. 提供基于统一时间源的异步延迟等待（DelayAsync）抽象。
// ==============================================================================

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
