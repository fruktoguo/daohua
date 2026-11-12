namespace DaoHua.Ecs;

/// <summary>
/// 模拟墙钟的 tick 级换算助手。
/// DateTimeOffset.AddSeconds 内部 AddUnits 先取整部、再把分部乘 ticksPerUnit 后截断，
/// 大秒数值的分部浮点误差会系统性少计 1 tick；统一改为「秒 × ticksPerSecond 四舍五入后 AddTicks」，
/// 保证 OOP 引擎、ECS 管线与模拟器三条推进路径的自然边界逐 tick 一致。
/// </summary>
public static class SimulationClock
{
    /// <summary>把 seconds 秒按最近 tick 贴齐后加到 at 上（AwayFromZero 对称处理负值）。</summary>
    public static DateTimeOffset AddSecondsExact(this DateTimeOffset at, double seconds)
        => at.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero));
}
