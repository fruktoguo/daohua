using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace DaoHua.Bench;

/// <summary>经真实鉴权会话、长连接和命令回执执行基准；在线墙钟与离线模拟时长分别记录。</summary>
public static class BenchWorkload
{
    public static async Task<BenchReport> RunAsync(BenchVariant variant, BenchOptions options,
        IReadOnlyList<TestCharacter> characters, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var total = Stopwatch.StartNew();
        var warnings = new ConcurrentQueue<string>();
        var sessions = new ConcurrentBag<BenchWebSocketSession>();
        var processSamples = new List<BenchProcessSample>();
        var processSamplesGate = new object();
        var rtt = new BenchLatencyHistogram();
        var queue = new BenchLatencyHistogram();
        JsonElement? before = null, after = null;
        long attempts = 0, completed = 0;
        var successfulPlayers = new ConcurrentDictionary<Guid, byte>();
        var errors = 0;
        var inFlight = 0;
        var peakInFlight = 0;
        var setupMs = 0d;
        var flushMs = 0d;
        var measured = new Stopwatch();
        var successfulConnections = 0;
        long receivedFrames = 0, receivedBytes = 0, sentBytes = 0;
        if (characters.Any(character => character.StartAt != characters[0].StartAt))
            throw new InvalidOperationException("manifest 角色必须共享同一业务起点。");
        try
        {
            await Parallel.ForEachAsync(characters, new ParallelOptions
            { MaxDegreeOfParallelism = options.Concurrency, CancellationToken = cancellationToken }, async (character, token) =>
            {
                try { sessions.Add(await BenchWebSocketSession.ConnectAsync(character, options, token)); }
                catch (Exception exception) when (IsExpectedFailure(exception))
                {
                    Interlocked.Increment(ref errors);
                    if (warnings.Count < 20) warnings.Enqueue($"{character.DisplayName} 建连失败：{exception.Message}");
                }
            });
            successfulConnections = sessions.Count;
            setupMs = total.Elapsed.TotalMilliseconds;
            if (successfulConnections != characters.Count) throw new InvalidOperationException($"只建立 {successfulConnections}/{characters.Count} 个会话，拒绝以缩水人数计时。");
            var control = sessions.First().Client;
            before = await ReadMetricsAsync(control, cancellationToken);
            if (before is { } initial)
            {
                if (initial.TryGetProperty("benchmarkMode", out var mode) && !string.Equals(mode.GetString(), options.Mode, StringComparison.Ordinal))
                    throw new InvalidOperationException("服务端 benchmarkMode 与客户端不一致。");
            }
            else
            {
                warnings.Enqueue("服务端未提供基准指标；仍执行真实 WS workload，进程/数据库分段指标保持 null。");
            }
            if (options.Mode == "replay")
            {
                using var clock = await control.PostAsJsonAsync("api/debug/bench/simulation-clock",
                    new { targetTime = characters[0].StartAt.AddHours(options.TargetHours), advance = false }, cancellationToken);
                clock.EnsureSuccessStatusCode();
            }
            using var semaphore = new SemaphoreSlim(options.Concurrency);
            using var sampling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            measured.Start();
            if (before is { } initialSample) AddSample(initialSample);
            var sampleTask = SampleAsync();
            try
            {
                await Task.WhenAll(sessions.Select(async session =>
                {
                    long ordinal = 0;
                    while (options.Mode == "replay" ? ordinal == 0 : measured.Elapsed.TotalSeconds < options.DurationSeconds)
                    {
                        var waitingAt = Stopwatch.GetTimestamp();
                        await semaphore.WaitAsync(cancellationToken);
                        if (options.Mode == "online" && measured.Elapsed.TotalSeconds >= options.DurationSeconds)
                        {
                            semaphore.Release();
                            break;
                        }
                        queue.Record(Stopwatch.GetElapsedTime(waitingAt).TotalMilliseconds);
                        var current = Interlocked.Increment(ref inFlight);
                        int oldPeak;
                        do { oldPeak = Volatile.Read(ref peakInFlight); }
                        while (current > oldPeak && Interlocked.CompareExchange(ref peakInFlight, current, oldPeak) != oldPeak);
                        var iterationStart = Stopwatch.GetTimestamp();
                        try
                        {
                            Interlocked.Increment(ref attempts);
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
                            var target = options.Mode == "replay" ? session.Character.StartAt.AddHours(options.TargetHours) : DateTimeOffset.UtcNow;
                            rtt.Record(await session.AdvanceAsync(target, ++ordinal, timeout.Token));
                            Interlocked.Increment(ref completed);
                            successfulPlayers.TryAdd(session.Character.PlayerId, 0);
                        }
                        catch (Exception exception) when (IsExpectedFailure(exception))
                        {
                            Interlocked.Increment(ref errors);
                            if (warnings.Count < 20) warnings.Enqueue($"{session.Character.DisplayName} 命令失败：{exception.Message}");
                            break;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref inFlight);
                            semaphore.Release();
                        }
                        if (options.Mode == "replay") break;
                        var delay = Math.Min(options.IntervalMs - Stopwatch.GetElapsedTime(iterationStart).TotalMilliseconds,
                            (options.DurationSeconds - measured.Elapsed.TotalSeconds) * 1000);
                        if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken);
                    }
                }));
            }
            finally
            {
                measured.Stop();
                sampling.Cancel();
                await sampleTask;
            }
            var flushWatch = Stopwatch.StartNew();
            using var flush = await control.PostAsync("api/debug/bench/flush", null, cancellationToken);
            flush.EnsureSuccessStatusCode();
            flushMs = flushWatch.Elapsed.TotalMilliseconds;
            // 终态指标必须在强制刷盘完成后读取，确保数据库计数和持久化阶段包含在报告窗口内。
            after = await ReadMetricsAsync(control, cancellationToken);
            if (after is not { } final)
            {
                warnings.Enqueue("服务端未提供终态基准指标；进程/数据库差分保持 null。");
            }
            else
            {
                var initialProcessId = before is { } initialForProcess ? BenchMeasurements.Integer(initialForProcess, "processId") : null;
                var finalProcessId = BenchMeasurements.Integer(final, "processId");
                if (initialProcessId is { } initialPid && finalProcessId is { } finalPid && initialPid != finalPid)
                {
                    warnings.Enqueue("测量期间服务端进程发生更换，进程/数据库差分保持 null。");
                    after = null;
                }
                else AddSample(final);
            }

            async Task SampleAsync()
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.MetricsIntervalSeconds));
                    while (await timer.WaitForNextTickAsync(sampling.Token))
                    {
                        var sample = await ReadMetricsAsync(control, sampling.Token);
                        if (sample is { } value) AddSample(value);
                    }
                }
                catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
                catch (Exception exception) when (IsExpectedFailure(exception))
                {
                    Interlocked.Increment(ref errors);
                    warnings.Enqueue($"进程采样失败：{exception.Message}");
                }
            }
            void AddSample(JsonElement sample)
            {
                var processId = BenchMeasurements.Integer(sample, "processId");
                var cpuMs = BenchMeasurements.Number(sample, "cpuMs");
                var allocatedBytes = BenchMeasurements.Integer(sample, "allocatedBytes");
                var gc0 = BenchMeasurements.Integer(sample, "gc0");
                var gc1 = BenchMeasurements.Integer(sample, "gc1");
                var gc2 = BenchMeasurements.Integer(sample, "gc2");
                var rssBytes = BenchMeasurements.Integer(sample, "rssBytes");
                // 进程端点扩展字段缺失时不造零值；调用方仍可使用前后原始 JSON 和可用字段差分。
                if (processId is null || cpuMs is null || allocatedBytes is null || gc0 is null || gc1 is null || gc2 is null || rssBytes is null)
                {
                    warnings.Enqueue("服务端进程指标字段不完整，跳过该进程采样并保持缺失指标为 null。");
                    return;
                }
                lock (processSamplesGate)
                {
                    // 时间戳与追加共享锁，确保定时采样和边界采样保持同一顺序。
                    processSamples.Add(new(measured.Elapsed.TotalSeconds, (int)processId.Value, cpuMs.Value,
                        allocatedBytes.Value, gc0.Value, gc1.Value, gc2.Value,
                        rssBytes.Value / (1024d * 1024d), sample));
                }
            }
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            errors++;
            warnings.Enqueue(exception.Message);
        }
        finally
        {
            measured.Stop();
            foreach (var session in sessions)
            {
                receivedFrames += session.ReceivedFrames;
                receivedBytes += session.ReceivedBytes;
                sentBytes += session.SentBytes;
                await session.DisposeAsync();
            }
        }
        total.Stop();
        BenchProcessSample[] capturedProcessSamples;
        lock (processSamplesGate) capturedProcessSamples = processSamples.ToArray();
        var requestTiming = rtt.Snapshot();
        var core = BenchMeasurements.MeasurementDeltas(before, after);
        var counters = BenchMeasurements.CounterDeltas(before, after);
        var queueTiming = queue.Snapshot();
        double? NumberDelta(string name) => BenchMeasurements.Delta(BenchMeasurements.Number(before, name), BenchMeasurements.Number(after, name));
        long? IntegerDelta(string name) => BenchMeasurements.Delta(BenchMeasurements.Integer(before, name), BenchMeasurements.Integer(after, name));
        double? Sum(string prefix)
        {
            var selected = core.Where(pair => pair.Key == prefix || pair.Key.StartsWith(prefix + "|", StringComparison.Ordinal)).ToArray();
            return selected.Length == 0 ? null : selected.Sum(pair => pair.Value.TotalMs);
        }
        var cpu = NumberDelta("cpuMs");
        var allocated = IntegerDelta("allocatedBytes");
        var rss = BenchMeasurements.Number(after, "rssBytes") / (1024d * 1024d);
        var hasProcessDeltas = cpu is not null || allocated is not null || NumberDelta("gcPauseMs") is not null
            || new[] { "gc0", "gc1", "gc2" }.Any(name => IntegerDelta(name) is not null);
        var hasDatabaseDeltas = IntegerDelta("databaseRequests") is not null
            || new[] { "databaseMs", "databaseBytesSent", "databaseBytesReceived" }.Any(name => NumberDelta(name) is not null);
        var hasServerEvidence = core.Count > 0 || counters.Values.Any(value => value is not null)
            || hasProcessDeltas || hasDatabaseDeltas;
        var measurementStatus = errors != 0 ? "failed"
            : before is null || after is null || !hasServerEvidence ? "partial" : "measured";
        if (measurementStatus == "partial")
            warnings.Enqueue("宿主指标窗口缺失或阶段/事件、进程与数据库差分全部不可用；保留真实 WS 结果，测量状态标为 partial。");
        warnings.Enqueue("p50_ms/p95_ms/p99_ms 为成功命令 RTT；core.* 为服务端真实测量，二者不可互换。CPU/GC/分配为测量窗口进程总量，RSS 包含宿主固定开销；人均分母为成功建立连接的玩家数。");
        warnings.Enqueue(options.Mode == "online"
            ? "online 使用真实墙钟与后台模拟；每版本实际时刻不同，终态逐位一致性由独立 replay 同时钟实验验证。"
            : "replay 使用固定业务截止时间；simulated_hours 为补算跨度，duration 为实际测量秒数。");
        var metric = new BenchMetricRecord(variant.Name, options.Scenario?.ToLowerInvariant() ?? "mixed", characters.Count,
            measured.Elapsed.TotalSeconds, total.Elapsed.TotalMilliseconds, cpu, Sum("core.simulation"), Sum("lifecycle.entity_create"),
            Sum("ecs.system_tick"), Sum("snapshot.export"), NumberDelta("databaseMs"), Sum("network.send"), allocated,
            IntegerDelta("gc0"), IntegerDelta("gc1"), IntegerDelta("gc2"), rss,
            requestTiming.P50Ms, requestTiming.P95Ms, requestTiming.P99Ms, requestTiming.MaxMs,
            measurementStatus, warnings.ToArray(), errors)
        {
            Mode = options.Mode, SimulatedHours = options.Mode == "replay" ? options.TargetHours : null,
            RequestedDurationSeconds = options.Mode == "online" ? options.DurationSeconds : null,
            SampleCount = checked((int)requestTiming.Count), MeasuredWallMs = measured.Elapsed.TotalMilliseconds,
            SetupWallMs = setupMs, FlushWallMs = flushMs, ConcurrencyLimit = options.Concurrency,
            PeakInflightRequests = peakInFlight == 0 ? null : peakInFlight,
            ConnectedPlayers = successfulConnections == 0 ? null : successfulConnections,
            SuccessfulPlayers = successfulPlayers.Count == 0 ? null : successfulPlayers.Count,
            CommandIntervalMs = options.Mode == "online" ? options.IntervalMs : null,
            AttemptedCommands = attempts == 0 ? null : attempts,
            SuccessfulCommands = completed == 0 ? null : completed,
            CommandsPerSecond = completed == 0 || measured.Elapsed.TotalSeconds <= 0 ? null : completed / measured.Elapsed.TotalSeconds,
            RequestRtt = requestTiming.Count == 0 ? null : requestTiming,
            ClientQueue = queueTiming.Count == 0 ? null : queueTiming,
            ServerMeasurements = core.Count == 0 ? null : core,
            ServerCounters = counters.Count == 0 ? null : counters,
            MetricScope = "process-total-measurement-window", PlayerDenominator = successfulConnections,
            CpuMsPerPlayer = successfulConnections > 0 ? cpu / successfulConnections : null,
            AllocatedBytesPerPlayer = successfulConnections > 0 ? (double?)allocated / successfulConnections : null,
            RssMbPerPlayer = successfulConnections > 0 ? rss / successfulConnections : null,
            PeakRssMb = capturedProcessSamples.Length > 0 ? capturedProcessSamples.Max(sample => sample.RssMb) : null,
            GcPauseMs = NumberDelta("gcPauseMs"), InstrumentationListenerMs = NumberDelta("instrumentationListenerMs"),
            DatabaseRequests = IntegerDelta("databaseRequests"),
            DatabaseBytesSent = NumberDelta("databaseBytesSent"), DatabaseBytesReceived = NumberDelta("databaseBytesReceived"),
            ReceivedFrames = receivedFrames, ReceivedBytes = receivedBytes, SentBytes = sentBytes
        };
        return new(metric, started, DateTimeOffset.UtcNow, characters[0].GameDataFingerprint,
            BenchMeasurements.Integer(after, "processId")?.ToString(CultureInfo.InvariantCulture), variant.ArtifactDirectory,
            new("not_measured", options.Mode, ["manifest", "HelloAck fingerprint", "WS command receipts"], ["terminal persistence parity"]))
        {
            ManifestSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(characters, BenchJson.Options))).ToLowerInvariant(),
            ProcessSamples = capturedProcessSamples, ServerMetricsBefore = before, ServerMetricsAfter = after
        };
    }

    private static async Task<JsonElement?> ReadMetricsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("api/debug/bench/metrics", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    private static bool IsExpectedFailure(Exception exception)
        => exception is HttpRequestException or OperationCanceledException or WebSocketException or JsonException or InvalidOperationException;
}
