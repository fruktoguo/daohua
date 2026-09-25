using System.Diagnostics;
using System.Diagnostics.Metrics;
using Domain.GameData;

namespace Domain.Common;

/// <summary>纯观测入口；没有 Meter 监听器时不读取时钟、不保留样本或玩家数据。</summary>
public static class RuntimeInstrumentation
{
    public const string MeterName = "DaoHua.Runtime";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("daohua.runtime.duration", "ms");
    private static readonly Counter<long> Events = Meter.CreateCounter<long>("daohua.runtime.events");

    public static bool Enabled => Duration.Enabled;

    public static Measurement Measure(string stage, string scenario = "all")
        => Duration.Enabled ? new(stage, scenario, Stopwatch.GetTimestamp()) : default;

    public static void Count(string name, long count = 1)
    {
        if (Events.Enabled) Events.Add(count, new KeyValuePair<string, object?>("name", name));
    }

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
            if (startedAt != 0)
                Duration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    new KeyValuePair<string, object?>("stage", stage),
                    new KeyValuePair<string, object?>("scenario", scenario));
        }
    }
}
