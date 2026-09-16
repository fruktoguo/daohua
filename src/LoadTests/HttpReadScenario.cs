namespace DaoHua.LoadTests;

/// <summary>
/// HTTP 只读接口压测场景。
/// 以 <c>--concurrency</c> 个并发 worker 在 <c>--duration-seconds</c> 秒内持续轮询
/// /healthz、/readyz、/api/v1/meta 三个公开只读端点，统计吞吐与延迟分位数。
/// </summary>
public static class HttpReadScenario
{
    /// <summary>错误率门禁（非 2xx/3xx 与传输错误占比上限）。</summary>
    public const double MaxErrorRate = 0.01;
    /// <summary>单请求延迟 P99 预算（毫秒）。</summary>
    public const double P99BudgetMilliseconds = 2_000;
    /// <summary>单请求超时。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    /// <summary>参与轮询的只读端点（均为无鉴权公开接口）。</summary>
    private static readonly string[] Endpoints = ["/healthz", "/readyz", "/api/v1/meta"];

    /// <summary>
    /// 执行 HTTP 读压测并输出结构化报告。
    /// </summary>
    /// <param name="options">压测选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>压测报告；服务不可达且未指定 --require-server 时返回 Failure。</returns>
    public static async Task<LoadTestReport> RunAsync(LoadTestOptions options, CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var started = Stopwatch.GetTimestamp();
        var warnings = new List<string>();

        var baseUri = DeriveHttpBaseUri(options.ServerUri);
        using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = options.Concurrency };
        using var client = new HttpClient(handler) { BaseAddress = baseUri, Timeout = RequestTimeout };

        var deadline = DateTimeOffset.UtcNow.AddSeconds(options.DurationSeconds);
        var stats = new HttpReadStats();
        var workers = new Task[options.Concurrency];
        for (var index = 0; index < options.Concurrency; index++)
        {
            var captured = index;
            workers[index] = Task.Run(() => RunWorkerAsync(client, captured, deadline, stats, cancellationToken), cancellationToken);
        }

        await Task.WhenAll(workers);

        var totalRequests = Interlocked.Read(ref stats.Requests);
        var succeeded = Interlocked.Read(ref stats.Succeeded);
        var failed = Interlocked.Read(ref stats.Failed);
        if (totalRequests > 0 && succeeded == 0 && !options.RequireServer)
        {
            return LoadTestReport.Failure(
                "http-read",
                $"服务不可达：{totalRequests} 次请求全部失败（{stats.LastError ?? "无错误详情"}），目标 {baseUri}。指定 --require-server 可输出完整指标报告。");
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var errorRate = totalRequests == 0 ? 1 : (double)failed / totalRequests;
        var p99 = LatencySummary.P99(stats.Latencies);
        var errors = 0;
        if (errorRate >= MaxErrorRate) errors++;
        if (p99 > P99BudgetMilliseconds) errors++;
        if (totalRequests == 0)
        {
            errors++;
            warnings.Add("压测窗口内未发出任何请求。");
        }

        if (succeeded == 0 && totalRequests > 0)
        {
            warnings.Add($"全部请求失败（{stats.LastError ?? "无错误详情"}）。");
        }

        process.Refresh();
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        return new LoadTestReport(
            "loadtest.v1",
            "http-read",
            options.Players,
            options.DurationSeconds,
            elapsed.TotalMilliseconds,
            (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            managedBefore,
            GC.GetTotalMemory(forceFullCollection: false),
            process.WorkingSet64,
            Interlocked.Read(ref stats.SentBytes),
            Interlocked.Read(ref stats.ReceivedBytes),
            totalRequests,
            succeeded,
            errors,
            errors == 0,
            new Dictionary<string, object?>
            {
                ["target"] = new
                {
                    url = baseUri.ToString(),
                    endpoints = Endpoints,
                    concurrency = options.Concurrency,
                    durationSeconds = options.DurationSeconds
                },
                ["requests"] = new
                {
                    total = totalRequests,
                    succeeded,
                    failed,
                    errorRate = Math.Round(errorRate, 4),
                    throughputPerSecond = Math.Round(totalRequests / seconds, 2)
                },
                ["statusCodes"] = stats.StatusCodes
                    .OrderBy(pair => pair.Key)
                    .ToDictionary(pair => pair.Key.ToString(), pair => (object?)pair.Value),
                ["latencyMs"] = LatencySummary.Describe(stats.Latencies),
                ["gate"] = new
                {
                    maxErrorRate = MaxErrorRate,
                    p99Milliseconds = P99BudgetMilliseconds
                }
            },
            warnings);
    }

    /// <summary>
    /// 单个并发 worker：按序号轮转端点，在截止时间前尽可能多发请求。
    /// </summary>
    private static async Task RunWorkerAsync(
        HttpClient client,
        int workerIndex,
        DateTimeOffset deadline,
        HttpReadStats stats,
        CancellationToken cancellationToken)
    {
        var sequence = workerIndex;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var endpoint = Endpoints[sequence++ % Endpoints.Length];
            var requestStart = Stopwatch.GetTimestamp();
            try
            {
                using var response = await client.GetAsync(endpoint, cancellationToken);
                var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                Interlocked.Increment(ref stats.Requests);
                Interlocked.Add(ref stats.ReceivedBytes, body.Length);
                Interlocked.Add(ref stats.SentBytes, endpoint.Length + 64); // 应用层估算：请求行 + 基础头
                stats.Latencies.Add(Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds);
                stats.StatusCodes.AddOrUpdate((int)response.StatusCode, 1, (_, count) => count + 1);
                if ((int)response.StatusCode < 400)
                {
                    Interlocked.Increment(ref stats.Succeeded);
                }
                else
                {
                    Interlocked.Increment(ref stats.Failed);
                    stats.LastError = $"HTTP {(int)response.StatusCode} {endpoint}";
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) break;
                Interlocked.Increment(ref stats.Requests);
                Interlocked.Increment(ref stats.Failed);
                stats.Latencies.Add(Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds);
                stats.LastError = exception.Message;
            }
        }
    }

    /// <summary>
    /// 将 --url 归一化为 http(s) 基地址（ws/wss 输入自动降级为对应 http 方案，仅保留主机与端口）。
    /// </summary>
    private static Uri DeriveHttpBaseUri(Uri serverUri) => new UriBuilder(serverUri)
    {
        Scheme = serverUri.Scheme is "wss" or "https" ? "https" : "http",
        Path = string.Empty,
        Query = string.Empty,
        Fragment = string.Empty
    }.Uri;

    /// <summary>
    /// HTTP 读场景的线程安全统计容器。
    /// </summary>
    private sealed class HttpReadStats
    {
        public long Requests;
        public long Succeeded;
        public long Failed;
        public long SentBytes;
        public long ReceivedBytes;
        public volatile string? LastError;
        public readonly ConcurrentBag<double> Latencies = new();
        public readonly ConcurrentDictionary<int, int> StatusCodes = new();
    }
}
