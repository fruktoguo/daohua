using Server.Clean;

namespace Ecs.Tests;

/// <summary>ECS 边界测试共用模拟时钟。</summary>
internal sealed class TestSimulationClock(
    DateTimeOffset? initial = null,
    bool ignoreAdvance = false) : IPlayerSimulationClock
{
    private DateTimeOffset _now = (initial ?? DateTimeOffset.UtcNow).ToUniversalTime();

    public DateTimeOffset UtcNow
    {
        get => _now;
        set => _now = value.ToUniversalTime();
    }

    public bool IsDeterministic => true;

    public void AdvanceTo(DateTimeOffset target)
    {
        if (!ignoreAdvance) _now = target.ToUniversalTime();
    }

    public static TestSimulationClock Fixed(DateTimeOffset value) => new(value, ignoreAdvance: true);
}
