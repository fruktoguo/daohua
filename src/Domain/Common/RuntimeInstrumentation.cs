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
    /// <summary>Meter 名称；监听端按它订阅运行时指标流。</summary>
    public const string MeterName = "DaoHua.Runtime";

#if !DAOHUA_OBSERVE_OFF
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("daohua.runtime.duration", "ms");
    private static readonly Counter<long> Events = Meter.CreateCounter<long>("daohua.runtime.events");
    private static readonly AsyncLocal<string?> ActiveScenario = new();

    /// <summary>观测是否启用；关闭时全部入口为零成本空实现，调用点无需判空。</summary>
    public static bool Enabled => Duration.Enabled;

    /// <summary>开始一段计时（显式指定场景名）；返回的 Measurement 在 Dispose 时记录耗时。</summary>
    public static Measurement Measure(string stage, string scenario = "all")
        => Duration.Enabled ? new(stage, scenario, Stopwatch.GetTimestamp()) : default;

    /// <summary>开始一段计时（沿用 BeginScenario 设置的当前场景）；未设置时归入 all。</summary>
    public static Measurement MeasureCurrent(string stage)
        => Duration.Enabled ? new(stage, ActiveScenario.Value ?? "all", Stopwatch.GetTimestamp()) : default;

    /// <summary>手动计时起点（时间戳）；与 EndCurrent 成对使用，适合跨 await/分支的路径。</summary>
    public static long BeginCurrent(string stage)
        => Duration.Enabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>手动计时终点：记录起点到当前的毫秒耗时；起点为 0（未启用）时忽略。</summary>
    public static void EndCurrent(string stage, long startedAt)
    {
        if (startedAt != 0)
            Duration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("stage", stage),
                new KeyValuePair<string, object?>("scenario", ActiveScenario.Value ?? "all"));
    }

    /// <summary>进入命名场景：期间 MeasureCurrent 记录的样本都归入该场景；返回的作用域 Dispose 时恢复原场景。</summary>
    public static ScenarioScope BeginScenario(string scenario)
    {
        if (!Duration.Enabled) return default;
        var previous = ActiveScenario.Value;
        ActiveScenario.Value = scenario;
        return new(previous, true);
    }

    /// <summary>事件计数器：按名称累加计数（默认 1）；观测关闭时零成本。</summary>
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

    /// <summary>行动类型到观测场景名的映射：历练→gathering、炼丹/制造→crafting、战斗→combat，其余→other。</summary>
    public static string Scenario(ActionKind kind) => kind switch
    {
        ActionKind.Exploration => "gathering",
        ActionKind.Alchemy => "crafting",
        ActionKind.Combat => "combat",
        _ => "other"
    };

    /// <summary>计时作用域：构造时冻结起点，Dispose 时把耗时记入直方图；未启用的实例 Dispose 为空操作。</summary>
    /// <param name="stage">阶段名（如 domain.action.advance）。</param>
    /// <param name="scenario">场景名（gathering/crafting/combat/other/all）。</param>
    /// <param name="startedAt">Stopwatch 起点时间戳；0 表示未启用。</param>
    public readonly struct Measurement(string stage, string scenario, long startedAt) : IDisposable
    {
        /// <summary>记录起点到当前的耗时并释放作用域；观测关闭时无操作。</summary>
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

    /// <summary>场景作用域：Dispose 时把 AsyncLocal 场景名恢复为进入前的值；未启用的实例为空操作。</summary>
    /// <param name="previous">进入前的场景名（无则为 null）。</param>
    /// <param name="active">是否实际进入了场景（未启用观测时为 false）。</param>
    public readonly struct ScenarioScope(string? previous, bool active) : IDisposable
    {
        /// <summary>恢复进入前的场景名；观测关闭时无操作。</summary>
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
