using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Domain.Player;

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
        var loginLatency = new BenchLatencyHistogram();
        var queue = new BenchLatencyHistogram();
        JsonElement? before = null, afterLogin = null, after = null;
        long attempts = 0, completed = 0;
        var successfulPlayers = new ConcurrentDictionary<Guid, byte>();
        var errors = 0;
        var inFlight = 0;
        var peakInFlight = 0;
        var setupMs = 0d;
        var loginWallMs = 0d;
        var flushMs = 0d;
        var measured = new Stopwatch();
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? sampleTask = null;
        HttpClient? preclockClient = null;
        HttpClient? sampleClient = null;
        var successfulConnections = 0;
        long receivedFrames = 0, receivedBytes = 0, sentBytes = 0;
        if (characters.Any(character => character.StartAt != characters[0].StartAt))
            throw new InvalidOperationException("manifest 角色必须共享同一业务起点。");
        try
        {
            if (options.Mode == "offline-login")
            {
                preclockClient = await BenchWebSocketSession.CreateControlClientAsync(characters[0], options, cancellationToken);
                using var clock = await preclockClient.PostAsJsonAsync("api/debug/bench/simulation-clock",
                    new { targetTime = characters[0].StartAt.AddHours(options.TargetHours), advance = false }, cancellationToken);
                clock.EnsureSuccessStatusCode();
                sampleClient = preclockClient;
                before = await ReadMetricsAsync(sampleClient, cancellationToken);
                ValidateBenchmarkMode(before, "replay");
                if (BenchMeasurements.Integer(before, "sessions") is not 0
                    || BenchMeasurements.Integer(before, "loadedPlayers") is not 0)
                    throw new InvalidOperationException("离线上线测试开始前服务端仍有会话或已加载玩家。");
                measured.Start();
                if (before is { } initialSample) AddSample(initialSample);
                sampleTask = SampleAsync();
            }
            await Parallel.ForEachAsync(characters, new ParallelOptions
            { MaxDegreeOfParallelism = options.Concurrency, CancellationToken = cancellationToken }, async (character, token) =>
            {
                var loginStarted = Stopwatch.GetTimestamp();
                try
                {
                    sessions.Add(await BenchWebSocketSession.ConnectAsync(character, options, token));
                    if (options.Mode == "offline-login") loginLatency.Record(Stopwatch.GetElapsedTime(loginStarted).TotalMilliseconds);
                }
                catch (Exception exception) when (IsExpectedFailure(exception))
                {
                    Interlocked.Increment(ref errors);
                    if (warnings.Count < 20) warnings.Enqueue($"{character.DisplayName} 建连失败：{exception.Message}");
                }
            });
            successfulConnections = sessions.Count;
            setupMs = total.Elapsed.TotalMilliseconds;
            if (options.Mode == "offline-login")
            {
                loginWallMs = measured.Elapsed.TotalMilliseconds;
                measured.Stop();
                sampling.Cancel();
                if (sampleTask is not null) await sampleTask;
            }
            if (successfulConnections != characters.Count) throw new InvalidOperationException($"只建立 {successfulConnections}/{characters.Count} 个会话，拒绝以缩水人数计时。");
            var control = sessions.First().Client;
            if (options.Mode == "offline-login")
            {
                afterLogin = await ReadMetricsAsync(control, cancellationToken);
                if (afterLogin is { } loginSample) AddSample(loginSample);
            }
            if (options.Mode != "offline-login")
            {
                sampleClient = control;
                before = await ReadMetricsAsync(control, cancellationToken);
                ValidateBenchmarkMode(before, options.Mode);
                if (options.Mode == "replay")
                {
                    using var clock = await control.PostAsJsonAsync("api/debug/bench/simulation-clock",
                        new { targetTime = characters[0].StartAt.AddHours(options.TargetHours), advance = false }, cancellationToken);
                    clock.EnsureSuccessStatusCode();
                }
                measured.Start();
                if (before is { } initialSample) AddSample(initialSample);
                sampleTask = SampleAsync();
            }
            using var semaphore = new SemaphoreSlim(options.Concurrency);
            try
            {
                if (options.Mode != "offline-login") await Task.WhenAll(sessions.Select(async session =>
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
                if (sampleTask is not null) await sampleTask;
            }
            // 先统一收尾并等待连接卸载，否则 OOP 的卸载路径会在最终刷盘后追加 Progression 队列行。
            // 下线和关闭本身也会触发服务端的单玩家刷盘。若 500 个会话同时收尾，
            // 会把压测连接池瞬时打满，导致命令已经完成却因清理失败被误标为 failed。
            // 收尾并不属于 measured 窗口，因此只限制并发，不改变命令阶段的并发口径。
            var cleanupConcurrency = Math.Min(options.Concurrency, 8);
            await ForEachSessionAsync(
                sessions,
                cleanupConcurrency,
                (session, token) => session.SetPresenceAsync(PresenceState.Offline, options.Mode, token),
                cancellationToken);
            await ForEachSessionAsync(
                sessions,
                cleanupConcurrency,
                (session, _) => session.CloseWebSocketAsync(),
                cancellationToken);
            using (var unloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                unloadTimeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
                while (true)
                {
                    var state = await ReadMetricsAsync(control, unloadTimeout.Token)
                        ?? throw new InvalidOperationException("连接卸载期间服务端指标不可用。");
                    if (BenchMeasurements.Integer(state, "sessions") == 0
                        && BenchMeasurements.Integer(state, "loadedPlayers") == 0) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(25), unloadTimeout.Token);
                }
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

            void ValidateBenchmarkMode(JsonElement? metrics, string expected)
            {
                if (metrics is { } value)
                {
                    if (value.TryGetProperty("benchmarkMode", out var mode)
                        && !string.Equals(mode.GetString(), expected, StringComparison.Ordinal))
                        throw new InvalidOperationException("服务端 benchmarkMode 与客户端不一致。");
                }
                else warnings.Enqueue("服务端未提供基准指标；仍执行真实 WS workload，进程/数据库分段指标保持 null。");
            }

            async Task SampleAsync()
            {
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.MetricsIntervalSeconds));
                    while (await timer.WaitForNextTickAsync(sampling.Token))
                    {
                        var sample = await ReadMetricsAsync(sampleClient!, sampling.Token);
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
            sampling.Cancel();
            if (sampleTask is not null) await sampleTask;
            preclockClient?.Dispose();
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
        var requestTiming = options.Mode == "offline-login" ? loginLatency.Snapshot() : rtt.Snapshot();
        var core = BenchMeasurements.MeasurementDeltas(before, after);
        var loginCore = BenchMeasurements.MeasurementDeltas(before, afterLogin);
        var counters = BenchMeasurements.CounterDeltas(before, after);
        var queueTiming = queue.Snapshot();
        double? NumberDelta(string name) => BenchMeasurements.Delta(BenchMeasurements.Number(before, name), BenchMeasurements.Number(after, name));
        long? IntegerDelta(string name) => BenchMeasurements.Delta(BenchMeasurements.Integer(before, name), BenchMeasurements.Integer(after, name));
        double? LoginNumberDelta(string name) => BenchMeasurements.Delta(BenchMeasurements.Number(before, name), BenchMeasurements.Number(afterLogin, name));
        long? LoginIntegerDelta(string name) => BenchMeasurements.Delta(BenchMeasurements.Integer(before, name), BenchMeasurements.Integer(afterLogin, name));
        double? Sum(string prefix)
        {
            var selected = core.Where(pair => pair.Key == prefix || pair.Key.StartsWith(prefix + "|", StringComparison.Ordinal)).ToArray();
            return selected.Length == 0 ? null : selected.Sum(pair => pair.Value.TotalMs);
        }
        double? LoginSum(string prefix)
        {
            var selected = loginCore.Where(pair => pair.Key == prefix || pair.Key.StartsWith(prefix + "|", StringComparison.Ordinal)).ToArray();
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
            : before is null || after is null || options.Mode == "offline-login" && afterLogin is null || !hasServerEvidence ? "partial" : "measured";
        if (measurementStatus == "partial")
            warnings.Enqueue("宿主指标窗口缺失或阶段/事件、进程与数据库差分全部不可用；保留真实 WS 结果，测量状态标为 partial。");
        warnings.Enqueue(options.Mode == "offline-login"
            ? "p50_ms/p95_ms/p99_ms 为成功登录耗时，包含鉴权、WS、Hello、初始快照和上线登记。"
            : "p50_ms/p95_ms/p99_ms 为成功命令 RTT；core.* 为服务端真实测量，二者不可互换。");
        warnings.Enqueue("顶层 CPU/GC/分配为服务端指标起点到最终刷盘的进程总量；offline_login_* 为登录后、清理前的差分。RSS 包含宿主固定开销；人均分母为成功建立连接的玩家数。");
        warnings.Enqueue(options.Mode switch
        {
            "online" => "online 使用真实墙钟与后台模拟；每版本实际时刻不同，终态逐位一致性由独立 replay 同时钟实验验证。",
            "offline-login" => "offline-login 在登录前固定业务时钟；measured_wall_ms 是并发登录墙钟，simulated_hours 是离线补算跨度；不发送 simulation.advance。",
            _ => "replay 使用固定业务截止时间；simulated_hours 为补算跨度，duration 为实际测量秒数。"
        });
        var metric = new BenchMetricRecord(variant.Name, options.Scenario?.ToLowerInvariant() ?? "mixed", characters.Count,
            measured.Elapsed.TotalSeconds, total.Elapsed.TotalMilliseconds, cpu, Sum("core.simulation"), Sum("lifecycle.entity_create"),
            Sum("ecs.system_tick"), Sum("snapshot.export"), NumberDelta("databaseMs"), Sum("network.send"), allocated,
            IntegerDelta("gc0"), IntegerDelta("gc1"), IntegerDelta("gc2"), rss,
            requestTiming.P50Ms, requestTiming.P95Ms, requestTiming.P99Ms, requestTiming.MaxMs,
            measurementStatus, warnings.ToArray(), errors)
        {
            Mode = options.Mode, SimulatedHours = options.Mode is "replay" or "offline-login" ? options.TargetHours : null,
            OfflineLoginWallMs = options.Mode == "offline-login" ? loginWallMs : null,
            LoginLatency = options.Mode == "offline-login" && requestTiming.Count > 0 ? requestTiming : null,
            OfflineLoginCpuMs = LoginNumberDelta("cpuMs"),
            OfflineLoginAllocatedBytes = LoginIntegerDelta("allocatedBytes"),
            OfflineLoginSimulationMs = LoginSum("core.simulation"),
            OfflineLoginSystemTickMs = LoginSum("ecs.system_tick"),
            OfflineLoginDatabaseMs = LoginNumberDelta("databaseMs"),
            OfflineLoginServerMeasurements = loginCore.Count == 0 ? null : loginCore,
            RequestedDurationSeconds = options.Mode == "online" ? options.DurationSeconds : null,
            SampleCount = checked((int)requestTiming.Count), MeasuredWallMs = measured.Elapsed.TotalMilliseconds,
            SetupWallMs = setupMs, FlushWallMs = flushMs, ConcurrencyLimit = options.Concurrency,
            PeakInflightRequests = peakInFlight == 0 ? null : peakInFlight,
            ConnectedPlayers = successfulConnections == 0 ? null : successfulConnections,
            SuccessfulPlayers = options.Mode == "offline-login" ? successfulConnections : successfulPlayers.Count == 0 ? null : successfulPlayers.Count,
            CommandIntervalMs = options.Mode == "online" ? options.IntervalMs : null,
            AttemptedCommands = attempts == 0 ? null : attempts,
            SuccessfulCommands = completed == 0 ? null : completed,
            CommandsPerSecond = completed == 0 || measured.Elapsed.TotalSeconds <= 0 ? null : completed / measured.Elapsed.TotalSeconds,
            RequestRtt = options.Mode == "offline-login" || requestTiming.Count == 0 ? null : requestTiming,
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
            ProcessSamples = capturedProcessSamples, ServerMetricsBefore = before,
            ServerMetricsAfterLogin = afterLogin, ServerMetricsAfter = after
        };
    }

    private static async Task<JsonElement?> ReadMetricsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("api/debug/bench/metrics", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    private static Task ForEachSessionAsync(
        IEnumerable<BenchWebSocketSession> sessions,
        int maxDegreeOfParallelism,
        Func<BenchWebSocketSession, CancellationToken, Task> action,
        CancellationToken cancellationToken)
        => Parallel.ForEachAsync(
            sessions,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                CancellationToken = cancellationToken
            },
            async (session, token) => await action(session, token));

    private static bool IsExpectedFailure(Exception exception)
        => exception is HttpRequestException or OperationCanceledException or WebSocketException or JsonException or InvalidOperationException;
}
