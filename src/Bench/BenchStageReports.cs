using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaoHua.Bench;

public sealed record BenchStageMeasurement(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("scenario")] string Scenario,
    [property: JsonPropertyName("layer")] string Layer,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("count")] long? Count,
    [property: JsonPropertyName("total_ms")] double? TotalMs,
    [property: JsonPropertyName("p50_ms")] double? P50Ms,
    [property: JsonPropertyName("p95_ms")] double? P95Ms,
    [property: JsonPropertyName("p99_ms")] double? P99Ms,
    [property: JsonPropertyName("max_ms")] double? MaxMs,
    [property: JsonPropertyName("percentile_method")] string? PercentileMethod)
{
    [JsonPropertyName("mean_ms")]
    public double? MeanMs => Count is > 0 && TotalMs is { } total ? total / Count : null;
}

public sealed record BenchCounterMeasurement(
    [property: JsonPropertyName("counter")] string Counter,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("count")] double? Count);

public static class BenchStageReports
{
    private sealed record StageSpec(string Name, string Layer, bool Oop, bool Ecs, bool Comparable = false);

    private static readonly StageSpec[] Known =
    [
        new("core.simulation", "core", true, true, true),
        new("domain.resource.prepare", "domain", true, true, true),
        new("domain.resource.regen", "domain", true, true, true),
        new("domain.action.start", "domain", true, true, true),
        new("domain.action.prepare", "domain", true, true, true),
        new("domain.action.advance", "domain", true, true, true),
        new("domain.action.yield", "domain", true, true, true),
        new("domain.combat.prepare", "domain", true, true, true),
        new("domain.combat.event", "domain", true, true, true),
        new("domain.combat.yield", "domain", true, true, true),
        new("domain.combat.reward", "domain", true, true, true),
        new("oop.logic.resource_window", "oop", true, false),
        new("oop.logic.action_dispatch", "oop", true, false),
        new("oop.logic.combat_dispatch", "oop", true, false),
        new("ecs.system_tick", "ecs", false, true),
        new("ecs.owner_hold", "ecs", false, true),
        new("ecs.mailbox.post", "ecs", false, true),
        new("ecs.system.run.ActionClockSystem", "ecs", false, true),
        new("ecs.system.run.ActionQueueSystem", "ecs", false, true),
        new("ecs.system.run.GatheringActionSystem", "ecs", false, true),
        new("ecs.system.run.CraftingActionSystem", "ecs", false, true),
        new("ecs.system.run.ActionBoundarySystem", "ecs", false, true),
        new("ecs.system.run.TravelActionSystem", "ecs", false, true),
        new("ecs.system.run.TimedActionYieldSystem", "ecs", false, true),
        new("ecs.system.run.CombatActionBoundarySystem", "ecs", false, true),
        new("ecs.system.run.CombatActionYieldSystem", "ecs", false, true),
        new("ecs.system.run.PlayerClockCommitSystem", "ecs", false, true),
        new("ecs.system.run.CombatEventAdvanceSystem", "ecs", false, true),
        new("ecs.system.run.CombatTargetSystem", "ecs", false, true),
        new("ecs.system.run.CombatEventSystem", "ecs", false, true),
        new("queue.actor_wait", "queue", true, true),
        new("queue.owner_wait", "queue", false, true),
        new("snapshot.export", "snapshot", true, true),
        new("lifecycle.player_load", "lifecycle", true, true),
        new("lifecycle.entity_create", "lifecycle", false, true)
    ];

    public static IReadOnlyList<BenchStageMeasurement> Rows(BenchReport report)
    {
        var measurements = new Dictionary<(string Stage, string Scenario), BenchLatencySummary>();
        var source = report.Metric.Mode == "offline-login"
            ? report.Metric.OfflineLoginServerMeasurements ?? report.Metric.ServerMeasurements
            : report.Metric.ServerMeasurements;
        if (source is { } measured)
            foreach (var (key, value) in measured)
            {
                var split = key.LastIndexOf('|');
                var stage = CanonicalStage(split < 0 ? key : key[..split]);
                var scenario = split < 0 ? "all" : key[(split + 1)..];
                measurements[(stage, scenario)] = value;
            }

        var keys = new HashSet<(string Stage, string Scenario)>(measurements.Keys);
        foreach (var spec in Known)
            if (!keys.Any(key => key.Stage == spec.Name))
                keys.Add((spec.Name, report.Metric.Scenario));

        return keys.OrderBy(key => key.Stage, StringComparer.Ordinal)
            .ThenBy(key => key.Scenario, StringComparer.Ordinal)
            .Select(key => MakeRow(report, key.Stage, key.Scenario, measurements.GetValueOrDefault(key)))
            .ToArray();
    }

    public static IReadOnlyList<BenchCounterMeasurement> CounterRows(BenchReport report)
    {
        if (report.Metric.ServerCounters is not { Count: > 0 } counters) return [];
        return counters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new BenchCounterMeasurement(pair.Key,
                pair.Value is { } ? "measured" : "unavailable", pair.Value))
            .ToArray();
    }

    public static string ToStageCsv(BenchReport report)
    {
        var csv = new StringBuilder("variant,scenario,stage,layer,status,count,total_ms,mean_ms,p50_ms,p95_ms,p99_ms,max_ms,percentile_method\n");
        foreach (var row in Rows(report))
            CsvLine(csv, report.Metric.Variant, row.Scenario, row.Stage, row.Layer, row.Status,
                Value(row.Count), Value(row.TotalMs), Value(row.MeanMs), Value(row.P50Ms),
                Value(row.P95Ms), Value(row.P99Ms), Value(row.MaxMs), row.PercentileMethod);
        return csv.ToString();
    }

    public static string ToCounterCsv(BenchReport report)
    {
        var csv = new StringBuilder("variant,scenario,counter,status,count\n");
        foreach (var row in CounterRows(report))
            CsvLine(csv, report.Metric.Variant, report.Metric.Scenario, row.Counter, row.Status, Value(row.Count));
        return csv.ToString();
    }

    private static BenchStageMeasurement MakeRow(BenchReport report, string stage, string scenario,
        BenchLatencySummary? summary = null)
    {
        var spec = SpecFor(stage);
        var applicable = report.Metric.Variant == "oop" ? spec.Oop : spec.Ecs;
        var observed = summary is { Count: > 0 };
        var available = report.Metric.Mode == "offline-login"
            ? report.Metric.OfflineLoginServerMeasurements is not null
                || ObservabilityEnabled(report.ServerMetricsBefore) && ObservabilityEnabled(report.ServerMetricsAfterLogin)
            : report.Metric.ServerMeasurements is not null
                || ObservabilityEnabled(report.ServerMetricsBefore) && ObservabilityEnabled(report.ServerMetricsAfter);
        var status = !applicable ? "not_applicable" : observed ? "measured" : available ? "not_observed" : "unavailable";
        return new(stage, scenario, spec.Layer, status,
            observed ? summary!.Count : null, observed ? summary!.TotalMs : null,
            observed ? summary!.P50Ms : null, observed ? summary!.P95Ms : null,
            observed ? summary!.P99Ms : null, observed ? summary!.MaxMs : null,
            observed ? summary!.PercentileMethod : null);
    }

    private static StageSpec SpecFor(string stage)
    {
        stage = CanonicalStage(stage);
        return Known.FirstOrDefault(spec => spec.Name == stage)
            ?? new(stage, stage.Split('.')[0],
                !stage.StartsWith("ecs.", StringComparison.Ordinal)
                    && !stage.StartsWith("yuocore.", StringComparison.Ordinal),
                !stage.StartsWith("oop.", StringComparison.Ordinal)
                    && !stage.StartsWith("yuocore.", StringComparison.Ordinal),
                stage.StartsWith("domain.", StringComparison.Ordinal));
    }

    private static string CanonicalStage(string stage)
        => stage.StartsWith("yuocore.", StringComparison.Ordinal)
            ? "ecs.core." + stage[8..]
            : stage;

    private static bool ObservabilityEnabled(JsonElement? value)
        => value is { } json && json.TryGetProperty("observabilityEnabled", out var enabled)
            && enabled.ValueKind == JsonValueKind.True;

    private static string? Value<T>(T? value) where T : struct
        => value is { } number ? Convert.ToString(number, CultureInfo.InvariantCulture) : null;

    private static void CsvLine(StringBuilder csv, params string?[] values)
        => csv.AppendJoin(',', values.Select(Escape)).Append('\n');

    private static string Escape(string? value)
    {
        if (value is null) return string.Empty;
        return value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : '"' + value.Replace("\"", "\"\"") + '"';
    }
}
