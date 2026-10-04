using System.Diagnostics;
using System.Diagnostics.Metrics;
using Domain.Common;

namespace Server.Clean;

/// <summary>
/// 基准进程级遥测读取器。显式创建后监听 Npgsql 和可选领域 Meter，默认宿主不启用。
/// 数据库标签不保存；领域仅保存固定阶段/场景名，不记录玩家或节点标识。
/// </summary>
public sealed class BenchmarkTelemetry : IDisposable
{
    private readonly MeterListener _listener;
    private readonly Process _process;
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _npgsqlSums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _npgsqlSamples = new(StringComparer.Ordinal);
    // 每个阶段固定 1025 桶，百分位为桶上界；相邻有限桶最多相差 2%。
    /// <summary>
    /// 时长直方图的桶上界序列（毫秒）：1024 个按 1.02 倍几何递增的有限桶（相邻桶差 ≤2%）
    /// 加一个 +∞ 兜底桶，共 1025 项；随 /metrics 原样下发，供压测端按同口径还原百分位。
    /// </summary>
    public static readonly double[] HistogramUpperBoundsMs = Enumerable.Range(0, 1024)
        .Select(index => 0.001 * Math.Pow(1.02, index)).Append(double.MaxValue).ToArray();
    private readonly Dictionary<string, DurationAggregate> _durations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private long _listenerTicks;
    /// <summary>是否监听领域运行时 Meter（RuntimeInstrumentation）；false 时只收 Npgsql/db.client 指标，用于消融归因。</summary>
    public bool ObservabilityEnabled { get; }
    private readonly long _startAllocatedBytes;
    private readonly TimeSpan _startCpu;
    private readonly TimeSpan _startPause;
    private bool _disposed;
    private bool _sawNpgsqlMetric;
    private bool _sawDuration;
    private bool _sawRequest;
    private bool _sawSentBytes;
    private bool _sawReceivedBytes;

    /// <summary>
    /// 创建遥测读取器并开始监听 Meter；同时记录进程起点（累计分配字节/CPU/GC 暂停），
    /// 供 <see cref="Snapshot"/> 按差值输出本基准窗口的用量。
    /// </summary>
    /// <param name="observabilityEnabled">是否监听领域运行时 Meter；消融实验传 false 以剥离插桩开销。</param>
    public BenchmarkTelemetry(bool observabilityEnabled = true)
    {
        ObservabilityEnabled = observabilityEnabled;
        _process = Process.GetCurrentProcess();
        _startAllocatedBytes = GC.GetTotalAllocatedBytes(false);
        _startCpu = _process.TotalProcessorTime;
        _startPause = GC.GetTotalPauseDuration();

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                // Npgsql 10 使用 db.client.* 约定；仅接收数据库客户端指标且不保留标签。
                if ((observabilityEnabled && instrument.Meter.Name == RuntimeInstrumentation.MeterName)
                    || instrument.Meter.Name.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase)
                    || instrument.Name.StartsWith("db.client.", StringComparison.OrdinalIgnoreCase))
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.Start();
    }

    /// <summary>读取自创建以来的进程和数据库指标；未发布对应 Meter 时保持 null。</summary>
    public BenchmarkTelemetrySnapshot Snapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _process.Refresh();
        lock (_gate)
        {
            return new BenchmarkTelemetrySnapshot(
                AllocatedBytes: Math.Max(0, GC.GetTotalAllocatedBytes(false) - _startAllocatedBytes),
                Gc0: GC.CollectionCount(0),
                Gc1: GC.CollectionCount(1),
                Gc2: GC.CollectionCount(2),
                GcPauseMilliseconds: Math.Max(0, (GC.GetTotalPauseDuration() - _startPause).TotalMilliseconds),
                CpuMilliseconds: Math.Max(0, (_process.TotalProcessorTime - _startCpu).TotalMilliseconds),
                RssBytes: Math.Max(0, _process.WorkingSet64),
                NpgsqlRequestCount: _sawRequest ? SampleCount("db.client.operation.duration") : null,
                NpgsqlDurationMilliseconds: _sawDuration ? Sum("db.client.operation.duration") : null,
                NpgsqlBytesSent: _sawSentBytes ? Sum("db.client.operation.npgsql.bytes_written") : null,
                NpgsqlBytesReceived: _sawReceivedBytes ? Sum("db.client.operation.npgsql.bytes_read") : null,
                NpgsqlMeasurementCount: _sawNpgsqlMetric ? _npgsqlSamples.Values.Sum() : null,
                ObservabilityEnabled,
                InstrumentationListenerMilliseconds: _listenerTicks * 1000d / Stopwatch.Frequency,
                Measurements: _durations.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot()),
                Counters: new Dictionary<string, long>(_counters));
        }
    }

    /// <summary>停止 Meter 监听并释放进程句柄；之后调用 <see cref="Snapshot"/> 抛 <see cref="ObjectDisposedException"/>。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener.Dispose();
        _process.Dispose();
    }

    private void OnMeasurement(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => Record(instrument, measurement, tags);

    private void OnMeasurement(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        => Record(instrument, measurement, tags);

    private void Record(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (!double.IsFinite(measurement)) return;
        var startedAt = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (instrument.Meter.Name == RuntimeInstrumentation.MeterName)
            {
                string? stage = null, name = null;
                var scenario = "all";
                foreach (var tag in tags)
                {
                    if (tag.Key == "stage") stage = tag.Value as string;
                    else if (tag.Key == "scenario") scenario = tag.Value as string ?? "all";
                    else if (tag.Key == "name") name = tag.Value as string;
                }
                if (instrument.Name == "daohua.runtime.duration" && stage is not null)
                {
                    var key = stage + "|" + scenario;
                    if (!_durations.TryGetValue(key, out var duration)) _durations[key] = duration = new();
                    duration.Record(measurement);
                }
                else if (instrument.Name == "daohua.runtime.events" && name is not null)
                    _counters[name] = _counters.GetValueOrDefault(name) + (long)measurement;
            }
            else
            {
                var name = instrument.Name;
                var normalized = NormalizeUnit(instrument.Unit, measurement);
                _sawNpgsqlMetric = true;
                _npgsqlSums[name] = _npgsqlSums.GetValueOrDefault(name) + normalized;
                _npgsqlSamples[name] = _npgsqlSamples.GetValueOrDefault(name) + 1;
                if (name.Equals("db.client.operation.duration", StringComparison.Ordinal))
                {
                    _sawDuration = true;
                    _sawRequest = true;
                }
                else if (name.Equals("db.client.operation.npgsql.bytes_written", StringComparison.Ordinal))
                    _sawSentBytes = true;
                else if (name.Equals("db.client.operation.npgsql.bytes_read", StringComparison.Ordinal))
                    _sawReceivedBytes = true;
            }
            _listenerTicks += Stopwatch.GetTimestamp() - startedAt;
        }
    }

    private sealed class DurationAggregate
    {
        private readonly long[] _buckets = new long[HistogramUpperBoundsMs.Length];
        private long _count;
        private double _total, _max;

        public void Record(double milliseconds)
        {
            var index = Array.BinarySearch(HistogramUpperBoundsMs, milliseconds);
            if (index < 0) index = ~index;
            _buckets[index]++;
            _count++;
            _total += milliseconds;
            _max = Math.Max(_max, milliseconds);
        }

        public BenchmarkDurationSnapshot Snapshot() => new(_count, _total,
            Percentile(.5), Percentile(.95), Percentile(.99), _max, (long[])_buckets.Clone());

        private double Percentile(double quantile)
        {
            var threshold = (long)Math.Ceiling(_count * quantile);
            long seen = 0;
            for (var index = 0; index < _buckets.Length; index++)
            {
                seen += _buckets[index];
                if (seen >= threshold) return Math.Min(_max, HistogramUpperBoundsMs[index]);
            }
            return _max;
        }
    }

    private double Sum(string name)
        => _npgsqlSums.GetValueOrDefault(name);

    private long SampleCount(string name)
        => _npgsqlSamples.GetValueOrDefault(name);

    private static double NormalizeUnit(string? unit, double value)
        => unit?.Trim().ToLowerInvariant() switch
        {
            "s" or "sec" or "second" or "seconds" => value * 1000,
            "us" or "µs" => value / 1000,
            "ns" => value / 1_000_000,
            _ => value
        };

}

/// <summary>进程遥测快照；Npgsql 指标未发布或未被观测时字段为 null。</summary>
public sealed record BenchmarkTelemetrySnapshot(
    long AllocatedBytes,
    int Gc0,
    int Gc1,
    int Gc2,
    double GcPauseMilliseconds,
    double CpuMilliseconds,
    long RssBytes,
    long? NpgsqlRequestCount,
    double? NpgsqlDurationMilliseconds,
    double? NpgsqlBytesSent,
    double? NpgsqlBytesReceived,
    long? NpgsqlMeasurementCount,
    bool ObservabilityEnabled,
    double InstrumentationListenerMilliseconds,
    IReadOnlyDictionary<string, BenchmarkDurationSnapshot> Measurements,
    IReadOnlyDictionary<string, long> Counters);

/// <summary>单个阶段/场景的时长分布快照，由固定桶直方图聚合而来。</summary>
/// <param name="Count">记录的样本总数。</param>
/// <param name="TotalMs">样本总时长（毫秒）。</param>
/// <param name="P50Ms">第 50 百分位（毫秒，桶上界近似）。</param>
/// <param name="P95Ms">第 95 百分位（毫秒，桶上界近似）。</param>
/// <param name="P99Ms">第 99 百分位（毫秒，桶上界近似）。</param>
/// <param name="MaxMs">最大样本时长（毫秒）。</param>
/// <param name="Buckets">各桶样本计数，下标对应 <see cref="BenchmarkTelemetry.HistogramUpperBoundsMs"/> 的桶上界。</param>
public sealed record BenchmarkDurationSnapshot(long Count, double TotalMs, double P50Ms, double P95Ms,
    double P99Ms, double MaxMs, long[] Buckets);
