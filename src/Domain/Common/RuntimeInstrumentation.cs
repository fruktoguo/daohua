using System.Diagnostics;
using System.Diagnostics.Metrics;
using Domain.GameData;

namespace Domain.Common;

/// <summary>
/// 纯观测入口；没有 Meter 监听器时不读取时钟、不保留样本或玩家数据。
/// 生产镜像以 <c>DaoHuaObservability=false</c> 发布时（<c>DAOHUA_OBSERVE_OFF</c>），
/// 全部入口编译为 default 结构体返回，调用点在 JIT 后无任何残留指令。
/// </summary>
public static class RuntimeInstrumentation
{
    public const string MeterName = "DaoHua.Runtime";

#if !DAOHUA_OBSERVE_OFF
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("daohua.runtime.duration", "ms");
    private static readonly Counter<long> Events = Meter.CreateCounter<long>("daohua.runtime.events");
    private static readonly AsyncLocal<string?> ActiveScenario = new();

    public static bool Enabled => Duration.Enabled;

    public static Measurement Measure(string stage, string scenario = "all")
        => Duration.Enabled ? new(stage, scenario, Stopwatch.GetTimestamp()) : default;

    public static Measurement MeasureCurrent(string stage)
        => Duration.Enabled ? new(stage, ActiveScenario.Value ?? "all", Stopwatch.GetTimestamp()) : default;

    public static long BeginCurrent(string stage)
        => Duration.Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void EndCurrent(string stage, long startedAt)
    {
        if (startedAt != 0)
            Duration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("stage", stage),
                new KeyValuePair<string, object?>("scenario", ActiveScenario.Value ?? "all"));
    }

    public static ScenarioScope BeginScenario(string scenario)
    {
        if (!Duration.Enabled) return default;
        var previous = ActiveScenario.Value;
        ActiveScenario.Value = scenario;
        return new(previous, true);
    }

    public static void Count(string name, long count = 1)
    {
        if (Events.Enabled) Events.Add(count, new KeyValuePair<string, object?>("name", name));
    }
#else
    public static bool Enabled => false;

    public static Measurement Measure(string stage, string scenario = "all") => default;

    public static Measurement MeasureCurrent(string stage) => default;

    public static long BeginCurrent(string stage) => 0;

    public static void EndCurrent(string stage, long startedAt) { }

    public static ScenarioScope BeginScenario(string scenario) => default;

    public static void Count(string name, long count = 1) { }
#endif

    public static string Scenario(ActionKind kind) => kind switch
    {
        ActionKind.Exploration => "gathering",
        ActionKind.Alchemy => "crafting",
        ActionKind.Combat => "combat",
        _ => "other"
    };

    public readonly struct Measurement(string stage, string scenario, long startedAt) : IDisposable
    {
        public void Dispose()
        {
#if !DAOHUA_OBSERVE_OFF
            if (startedAt != 0)
                Duration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    new KeyValuePair<string, object?>("stage", stage),
                    new KeyValuePair<string, object?>("scenario", scenario));
#else
            _ = stage; _ = scenario; _ = startedAt;
#endif
        }
    }

    public readonly struct ScenarioScope(string? previous, bool active) : IDisposable
    {
        public void Dispose()
        {
#if !DAOHUA_OBSERVE_OFF
            if (active) ActiveScenario.Value = previous;
#else
            _ = previous; _ = active;
#endif
        }
    }
}
