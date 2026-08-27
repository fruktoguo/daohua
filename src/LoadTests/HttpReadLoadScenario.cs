namespace DaoHua.LoadTests;

public static class HttpReadLoadScenario
{
    public static async Task<LoadTestReport> RunAsync(LoadTestOptions options, CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var cpuBefore = process.TotalProcessorTime;
        var warnings = new ConcurrentBag<string>();
        var errors = 0;
        var requests = 0L;
        var successes = 0L;
        var notModified = 0L;
        var requestBytes = 0L;
        var responseBytes = 0L;
        var latency = new ConcurrentBag<double>();
        var baseUri = ToHttpBase(options.ServerUri);
        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = options.Concurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var sessionToken = Environment.GetEnvironmentVariable("DAOHUA_LOADTEST_SESSION_TOKEN");
        if (!string.IsNullOrWhiteSpace(sessionToken)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);

        var started = Stopwatch.GetTimestamp();
        for (var second = 0; second < options.DurationSeconds; second++)
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, options.Players),
                new ParallelOptions { MaxDegreeOfParallelism = options.Concurrency, CancellationToken = cancellationToken },
                async (index, token) =>
                {
                    var path = $"api/v1/market/book?assetId=asset.spirit-herb&depth=20&reader={index % 500}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, path);
                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        Interlocked.Increment(ref requests);
                        Interlocked.Add(ref requestBytes, Encoding.UTF8.GetByteCount(path) + 128);
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                        stopwatch.Stop();
                        latency.Add(stopwatch.Elapsed.TotalMilliseconds);
                        if (response.StatusCode == HttpStatusCode.NotModified)
                        {
                            Interlocked.Increment(ref notModified);
                            Interlocked.Increment(ref successes);
                            return;
                        }

                        await using var stream = await response.Content.ReadAsStreamAsync(token);
                        var body = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                        var bytes = response.Content.Headers.ContentLength ?? Encoding.UTF8.GetByteCount(body.RootElement.GetRawText());
                        Interlocked.Add(ref responseBytes, bytes);
                        if (response.IsSuccessStatusCode) Interlocked.Increment(ref successes);
                        else Interlocked.Increment(ref errors);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
                    {
                        Interlocked.Increment(ref errors);
                        warnings.Add(exception.Message);
                    }
                });
        }

        process.Refresh();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var latencyValues = latency.OrderBy(value => value).ToArray();
        if (options.RequireServer && requests == 0) errors++;
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
            requestBytes,
            responseBytes,
            requests,
            successes,
            (int)Math.Min(int.MaxValue, errors),
            errors == 0,
            new Dictionary<string, object?>
            {
                ["baseUri"] = baseUri,
                ["requests"] = requests,
                ["successes"] = successes,
                ["notModified"] = notModified,
                ["errorRate"] = requests == 0 ? 0 : errors / (double)requests,
                ["requestsPerSecond"] = elapsed.TotalSeconds <= 0 ? 0 : requests / elapsed.TotalSeconds,
                ["p50Milliseconds"] = Percentile(latencyValues, 0.50),
                ["p95Milliseconds"] = Percentile(latencyValues, 0.95),
                ["p99Milliseconds"] = Percentile(latencyValues, 0.99)
            },
            warnings.Distinct().Take(20).ToArray());
    }

    private static Uri ToHttpBase(Uri configured)
    {
        var builder = new UriBuilder(configured)
        {
            Scheme = configured.Scheme switch
            {
                "ws" => "http",
                "wss" => "https",
                _ => configured.Scheme
            },
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private static double Percentile(double[] values, double quantile)
    {
        if (values.Length == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(values.Length * quantile) - 1, 0, values.Length - 1);
        return values[index];
    }
}
