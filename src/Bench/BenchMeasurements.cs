using System.Text.Json;

namespace DaoHua.Bench;

public sealed record BenchLatencySummary(long Count, double TotalMs, double? P50Ms, double? P95Ms, double? P99Ms, double? MaxMs, string PercentileMethod);
public sealed record BenchProcessSample(double ElapsedSeconds, int ProcessId, double CpuMs, long AllocatedBytes, long Gc0, long Gc1, long Gc2, double RssMb, JsonElement Raw);

/// <summary>固定内存直方图；持续一小时的数百万回执不会在压测客户端累积样本数组。</summary>
internal sealed class BenchLatencyHistogram
{
    private const double BaseMilliseconds = 0.001;
    private const double Factor = 1.05;
    private readonly long[] _buckets = new long[512];
    private readonly object _gate = new();
    private long _count;
    private double _total;
    private double _max;

    public void Record(double milliseconds)
    {
        var index = milliseconds <= BaseMilliseconds ? 0 : Math.Min(_buckets.Length - 1, (int)Math.Ceiling(Math.Log(milliseconds / BaseMilliseconds, Factor)));
        lock (_gate)
        {
            _buckets[index]++;
            _count++;
            _total += milliseconds;
            _max = Math.Max(_max, milliseconds);
        }
    }

    public BenchLatencySummary Snapshot()
    {
        lock (_gate)
            return new(_count, _total, Percentile(.50), Percentile(.95), Percentile(.99), _count == 0 ? null : _max,
                "1us 起点、1.05 倍桶上界；近似百分位，最大值为精确观测");
    }

    private double? Percentile(double fraction)
    {
        if (_count == 0) return null;
        var needed = (long)Math.Ceiling(_count * fraction);
        long accumulated = 0;
        for (var i = 0; i < _buckets.Length; i++)
        {
            accumulated += _buckets[i];
            if (accumulated >= needed) return Math.Min(_max, BaseMilliseconds * Math.Pow(Factor, i));
        }
        return _max;
    }
}

internal static class BenchMeasurements
{
    public static double? Number(JsonElement? value, string name)
        => value is { } json && json.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetDouble() : null;
    public static long? Integer(JsonElement? value, string name)
        => value is { } json && json.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number ? item.GetInt64() : null;
    public static double? Delta(double? before, double? after) => before is { } a && after is { } b && b >= a ? b - a : null;
    public static long? Delta(long? before, long? after) => before is { } a && after is { } b && b >= a ? b - a : null;

    public static IReadOnlyDictionary<string, BenchLatencySummary> MeasurementDeltas(JsonElement? before, JsonElement? after)
    {
        var result = new Dictionary<string, BenchLatencySummary>(StringComparer.Ordinal);
        if (after is not { } end || !end.TryGetProperty("measurements", out var measurements)
            || !end.TryGetProperty("histogramUpperBoundsMs", out var upperBounds)) return result;
        var bounds = upperBounds.EnumerateArray().Select(value => value.GetDouble()).ToArray();
        JsonElement baseline = default;
        var hasBaseline = before is { } start && start.TryGetProperty("measurements", out baseline);
        foreach (var measurement in measurements.EnumerateObject())
        {
            var current = measurement.Value;
            JsonElement previous = default;
            var hadPrevious = hasBaseline && baseline.TryGetProperty(measurement.Name, out previous);
            var count = current.GetProperty("count").GetInt64() - (hadPrevious ? previous.GetProperty("count").GetInt64() : 0);
            if (count <= 0) continue;
            var buckets = current.GetProperty("buckets").EnumerateArray().Select(value => value.GetInt64()).ToArray();
            if (hadPrevious)
            {
                var previousBuckets = previous.GetProperty("buckets").EnumerateArray().Select(value => value.GetInt64()).ToArray();
                if (previousBuckets.Length != buckets.Length) throw new InvalidOperationException("服务端指标桶在测量期间发生变化。");
                for (var i = 0; i < buckets.Length; i++) buckets[i] -= previousBuckets[i];
            }
            if (buckets.Length != bounds.Length || buckets.Any(value => value < 0))
                throw new InvalidOperationException("服务端指标桶差分无效，可能发生了进程重启。");
            double? Percentile(double fraction)
            {
                var threshold = (long)Math.Ceiling(count * fraction);
                long accumulated = 0;
                for (var i = 0; i < buckets.Length; i++)
                {
                    accumulated += buckets[i];
                    if (accumulated >= threshold) return bounds[i];
                }
                return null;
            }
            // 累计 max 不能相减：如果本窗口未超过旧 max，就无法得出窗口精确最大值。
            var currentMax = Number(current, "maxMs");
            var previousMax = hadPrevious ? Number(previous, "maxMs") : null;
            result[measurement.Name] = new(count,
                current.GetProperty("totalMs").GetDouble() - (hadPrevious ? previous.GetProperty("totalMs").GetDouble() : 0),
                Percentile(.5), Percentile(.95), Percentile(.99),
                !hadPrevious || currentMax > previousMax ? currentMax : null,
                "服务端测量窗口直方图差分桶上界；累计百分位不作相减");
        }
        return result;
    }

    public static IReadOnlyDictionary<string, double?> CounterDeltas(JsonElement? before, JsonElement? after)
    {
        var result = new Dictionary<string, double?>(StringComparer.Ordinal);
        if (after is not { } end || !end.TryGetProperty("counters", out var counters)) return result;
        JsonElement baseline = default;
        var hasBaseline = before is { } start && start.TryGetProperty("counters", out baseline);
        foreach (var counter in counters.EnumerateObject())
        {
            var previous = hasBaseline && baseline.TryGetProperty(counter.Name, out var item) ? item.GetDouble() : 0;
            result[counter.Name] = Delta(previous, counter.Value.GetDouble());
        }
        return result;
    }
}
