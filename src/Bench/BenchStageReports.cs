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
    [property: JsonPropertyName("percentile_method")] string? PercentileMethod);

public sealed record BenchStageRatios(
    [property: JsonPropertyName("current_to_oop_total")] double? CurrentToOopTotal,
    [property: JsonPropertyName("optimized_to_oop_total")] double? OptimizedToOopTotal,
    [property: JsonPropertyName("optimized_to_current_total")] double? OptimizedToCurrentTotal,
    [property: JsonPropertyName("current_to_oop_p95")] double? CurrentToOopP95,
    [property: JsonPropertyName("optimized_to_oop_p95")] double? OptimizedToOopP95,
    [property: JsonPropertyName("optimized_to_current_p95")] double? OptimizedToCurrentP95);

public sealed record BenchStageComparison(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("scenario")] string Scenario,
    [property: JsonPropertyName("layer")] string Layer,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("oop")] BenchStageMeasurement? Oop,
    [property: JsonPropertyName("ecs_current")] BenchStageMeasurement? EcsCurrent,
    [property: JsonPropertyName("ecs_optimized")] BenchStageMeasurement? EcsOptimized,
    [property: JsonPropertyName("ratios")] BenchStageRatios? Ratios);

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
        if (report.Metric.ServerMeasurements is { } source)
            foreach (var (key, value) in source)
            {
                var split = key.LastIndexOf('|');
                var stage = split < 0 ? key : key[..split];
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

    public static BenchComparison Compare(IReadOnlyList<BenchReport> reports)
    {
        var differences = Validate(reports);
        var ratiosAllowed = differences.Count == 0;
        var byVariant = reports.GroupBy(report => report.Metric.Variant, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var rowSets = byVariant.ToDictionary(pair => pair.Key,
            pair => Rows(pair.Value).ToDictionary(row => (row.Stage, row.Scenario)), StringComparer.Ordinal);
        var keys = rowSets.Values.SelectMany(rows => rows.Keys).Distinct()
            .OrderBy(key => key.Stage, StringComparer.Ordinal).ThenBy(key => key.Scenario, StringComparer.Ordinal);
        var stages = new List<BenchStageComparison>();
        foreach (var key in keys)
        {
            BenchStageMeasurement? Row(string variant)
            {
                if (!byVariant.TryGetValue(variant, out var report)) return null;
                return rowSets[variant].TryGetValue(key, out var row) ? row : MakeRow(report, key.Stage, key.Scenario);
            }
            var oop = Row("oop");
            var current = Row("ecs-current");
            var optimized = Row("ecs-optimized");
            var spec = SpecFor(key.Stage);
            var measured = oop?.Status == "measured" && current?.Status == "measured" && optimized?.Status == "measured";
            var countMismatch = spec.Comparable && measured && (oop!.Count != current!.Count || oop.Count != optimized!.Count);
            var comparable = ratiosAllowed && spec.Comparable && key.Scenario != "all" && measured && !countMismatch;
            var ratios = comparable ? new BenchStageRatios(
                Divide(current!.TotalMs, oop!.TotalMs), Divide(optimized!.TotalMs, oop.TotalMs),
                Divide(optimized.TotalMs, current.TotalMs), Divide(current.P95Ms, oop.P95Ms),
                Divide(optimized.P95Ms, oop.P95Ms), Divide(optimized.P95Ms, current.P95Ms)) : null;
            stages.Add(new(key.Stage, key.Scenario, spec.Layer,
                countMismatch ? "count_mismatch" : comparable ? "comparable" : "not_comparable",
                oop, current, optimized, ratios));
        }
        return new(reports, differences, stages, ratiosAllowed,
            ["报告未记录 ECS shardCount；计算倍率前须外部核对 ecs-current 与 ecs-optimized 的实际分片数相同。",
             "instrumentation_listener_ms 是监听器耗时下界，完整埋点开销需相同负载的观测开关对照实验。"]);
    }

    public static string ToStageCsv(BenchReport report)
    {
        var csv = new StringBuilder("variant,scenario,stage,layer,status,count,total_ms,p50_ms,p95_ms,p99_ms,max_ms,percentile_method\n");
        foreach (var row in Rows(report))
            CsvLine(csv, report.Metric.Variant, row.Scenario, row.Stage, row.Layer, row.Status,
                Value(row.Count), Value(row.TotalMs), Value(row.P50Ms), Value(row.P95Ms), Value(row.P99Ms),
                Value(row.MaxMs), row.PercentileMethod);
        return csv.ToString();
    }

    public static string ToComparisonCsv(IReadOnlyList<BenchStageComparison> stages)
    {
        var csv = new StringBuilder("stage,scenario,layer,status");
        foreach (var variant in new[] { "oop", "ecs_current", "ecs_optimized" })
            foreach (var field in new[] { "status", "count", "total_ms", "p50_ms", "p95_ms", "p99_ms", "max_ms" })
                csv.Append(',').Append(variant).Append('_').Append(field);
        csv.Append(",current_to_oop_total,optimized_to_oop_total,optimized_to_current_total,current_to_oop_p95,optimized_to_oop_p95,optimized_to_current_p95\n");
        foreach (var stage in stages)
        {
            var cells = new List<string?> { stage.Stage, stage.Scenario, stage.Layer, stage.Status };
            foreach (var row in new[] { stage.Oop, stage.EcsCurrent, stage.EcsOptimized })
                cells.AddRange([row?.Status, Value(row?.Count), Value(row?.TotalMs), Value(row?.P50Ms),
                    Value(row?.P95Ms), Value(row?.P99Ms), Value(row?.MaxMs)]);
            cells.AddRange([Value(stage.Ratios?.CurrentToOopTotal), Value(stage.Ratios?.OptimizedToOopTotal),
                Value(stage.Ratios?.OptimizedToCurrentTotal), Value(stage.Ratios?.CurrentToOopP95),
                Value(stage.Ratios?.OptimizedToOopP95), Value(stage.Ratios?.OptimizedToCurrentP95)]);
            CsvLine(csv, cells.ToArray());
        }
        return csv.ToString();
    }

    private static BenchStageMeasurement MakeRow(BenchReport report, string stage, string scenario,
        BenchLatencySummary? summary = null)
    {
        var spec = SpecFor(stage);
        var applicable = report.Metric.Variant == "oop" ? spec.Oop : spec.Ecs;
        var observed = summary is { Count: > 0 };
        var available = report.Metric.ServerMeasurements is not null || ObservabilityEnabled(report.ServerMetricsBefore)
            && ObservabilityEnabled(report.ServerMetricsAfter);
        var status = !applicable ? "not_applicable" : observed ? "measured" : available ? "not_observed" : "unavailable";
        return new(stage, scenario, spec.Layer, status,
            observed ? summary!.Count : null, observed ? summary!.TotalMs : null,
            observed ? summary!.P50Ms : null, observed ? summary!.P95Ms : null,
            observed ? summary!.P99Ms : null, observed ? summary!.MaxMs : null,
            observed ? summary!.PercentileMethod : null);
    }

    private static List<string> Validate(IReadOnlyList<BenchReport> reports)
    {
        var differences = new List<string>();
        if (reports.Count != 3 || reports.Select(report => report.Metric.Variant).Distinct(StringComparer.Ordinal).Count() != 3
            || new[] { "oop", "ecs-current", "ecs-optimized" }.Except(reports.Select(report => report.Metric.Variant), StringComparer.Ordinal).Any())
            differences.Add("report 必须恰有 oop、ecs-current、ecs-optimized 各一份报告。");
        if (reports.Select(report => report.Metric.Scenario).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            differences.Add("三版场景不一致。");
        if (reports.Select(report => report.Metric.Players).Distinct().Count() != 1)
            differences.Add("三版玩家数量不一致。");
        if (!SameNonBlank(reports.Select(report => report.ManifestSha256)))
            differences.Add("三版 manifest SHA-256 缺失或不一致。");
        if (!SameNonBlank(reports.Select(report => report.GameDataFingerprint)))
            differences.Add("三版游戏数据指纹缺失或不一致。");
        if (!SameNonBlank(reports.Select(report => report.Metric.Mode)))
            differences.Add("三版运行模式缺失或不一致。");
        else if (reports[0].Metric.Mode == "replay")
        {
            if (reports.Any(report => report.Metric.SimulatedHours is null)
                || reports.Select(report => report.Metric.SimulatedHours).Distinct().Count() != 1)
                differences.Add("三版离线补算目标时长缺失或不一致。");
        }
        else if (reports[0].Metric.Mode == "online")
        {
            if (reports.Any(report => report.Metric.RequestedDurationSeconds is null)
                || reports.Select(report => report.Metric.RequestedDurationSeconds).Distinct().Count() != 1
                || reports.Any(report => report.Metric.CommandIntervalMs is null)
                || reports.Select(report => report.Metric.CommandIntervalMs).Distinct().Count() != 1)
                differences.Add("三版在线持续时长或命令间隔缺失或不一致。");
        }
        else differences.Add("report 只接受 replay 或 online 的真实工作负载。");
        if (reports.Any(report => report.Metric.ConcurrencyLimit is null)
            || reports.Select(report => report.Metric.ConcurrencyLimit).Distinct().Count() != 1)
            differences.Add("三版并发限制缺失或不一致。");
        if (reports.Any(report => report.Metric.SuccessfulCommands is null)
            || reports.Select(report => report.Metric.SuccessfulCommands).Distinct().Count() != 1)
            differences.Add("三版成功命令数缺失或不一致。");
        if (reports.Any(report => report.Metric.Errors != 0 || report.Metric.MeasurementStatus != "measured"))
            differences.Add("至少一个版本存在错误或阶段测量窗口不完整。");
        return differences;
    }

    private static StageSpec SpecFor(string stage)
        => Known.FirstOrDefault(spec => spec.Name == stage)
            ?? new(stage, stage.Split('.')[0], !stage.StartsWith("ecs.", StringComparison.Ordinal),
                !stage.StartsWith("oop.", StringComparison.Ordinal), stage.StartsWith("domain.", StringComparison.Ordinal));

    private static bool SameNonBlank(IEnumerable<string?> values)
    {
        var array = values.ToArray();
        return array.Length == 3 && array.All(value => !string.IsNullOrWhiteSpace(value))
            && array.Distinct(StringComparer.Ordinal).Count() == 1;
    }

    private static bool ObservabilityEnabled(JsonElement? value)
        => value is { } json && json.TryGetProperty("observabilityEnabled", out var enabled)
            && enabled.ValueKind == JsonValueKind.True;

    private static double? Divide(double? numerator, double? denominator)
        => numerator is { } a && denominator is > 0 ? a / denominator : null;

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
