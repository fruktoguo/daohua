using Server.Clean;

namespace Server.Tests;

/// <summary>服务端测试共用模拟时钟，统一替代各测试类内部重复手写的假时钟实现。</summary>
internal sealed class TestSimulationClock(
    DateTimeOffset? initial = null,
    bool isDeterministic = true,
    bool throwOnAdvance = false,
    bool ignoreAdvance = false,
    bool liveRealtime = false) : IPlayerSimulationClock
{
    private DateTimeOffset _now = (initial ?? DateTimeOffset.UtcNow).ToUniversalTime();

    public DateTimeOffset UtcNow
    {
        get => liveRealtime ? DateTimeOffset.UtcNow : _now;
        set => _now = value.ToUniversalTime();
    }

    public bool IsDeterministic { get; } = isDeterministic;

    public void AdvanceTo(DateTimeOffset target)
    {
        if (throwOnAdvance) throw new NotSupportedException();
        if (!ignoreAdvance) _now = target.ToUniversalTime();
    }

    public void SetNow(DateTimeOffset value) => _now = value.ToUniversalTime();

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);

    public static TestSimulationClock Fixed(DateTimeOffset value, bool throwOnAdvance = false) =>
        new(value, isDeterministic: true, throwOnAdvance: throwOnAdvance, ignoreAdvance: !throwOnAdvance);

    public static TestSimulationClock Realtime() =>
        new(isDeterministic: false, throwOnAdvance: true, liveRealtime: true);
}
