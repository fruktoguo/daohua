// ==============================================================================
// 文件名称: MarketReadLoadScenario.cs
// 主要职责: 灵市 HTTP/1.1 只读接口高并发轮询与 ETag 缓存收益压测场景。
// 核心逻辑 / 定义内容:
//   1. 模拟数百名读者并发使用 ETag/If-None-Match 条件请求高频轮询订单簿、个人挂单与市场指标。
//   2. 验证 304 Not Modified 缓存命中率对网络带宽与服务端计算压力的减免效果。
//   3. 统计各 HTTP 接口的响应延迟分位数（P50/P95/P99）、错误率与网络吞吐指标。
// ==============================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DaoHua.LoadTests;

/// <summary>
/// 灵市 HTTP 读接口高并发轮询压测场景。
/// 模拟 500 名读者并发使用 HTTP/1.1 ETag 条件请求周期性轮询盘口（3s）、个人挂单（5s）与市场指标（10s），检验 304 缓存收益与服务端吞吐。
/// </summary>
public static class MarketReadLoadScenario
{
    private const int BookIntervalSeconds = 3;
    private const int OrdersIntervalSeconds = 5;
    private const int MetricsIntervalSeconds = 10;

    /// <summary>
    /// 异步执行灵市只读请求并发压测：
    /// 1. 初始化并发 HTTP 客户端连接池；
    /// 2. 按 3s/5s/10s 节奏并发轮询各端点，携带 If-None-Match 验证 ETag 命中；
    /// 3. 统计请求数、200/304 响应分布、P50/P95/P99 延迟分位数及带宽流量。
    /// </summary>
    public static async Task<LoadTestReport> RunAsync(LoadTestOptions options)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var counters = new MarketReadCounters();
        var readers = Enumerable.Range(0, options.Players)
            .Select(index => new MarketReader(CreatePlayerId(index)))
            .ToArray();
        var baseUri = CreateHttpBase(options.ServerUri);
        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = options.ConnectConcurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var serverMetricsBefore = await ReadServerMetricsAsync(client);
        var startedAt = Stopwatch.GetTimestamp();
        for (var second = 0; second < options.DurationSeconds; second++)
        {
            var scheduledAt = TimeSpan.FromSeconds(second);
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (elapsed < scheduledAt)
            {
                await Task.Delay(scheduledAt - elapsed);
            }

            if (second % BookIntervalSeconds != 0
                && second % OrdersIntervalSeconds != 0
                && second % MetricsIntervalSeconds != 0)
            {
                continue;
            }

            await Parallel.ForEachAsync(
                readers,
                new ParallelOptions { MaxDegreeOfParallelism = options.ConnectConcurrency },
                async (reader, cancellationToken) =>
                {
                    try
                    {
                        await reader.PollAsync(second, client, counters, cancellationToken);
                    }
                    catch (Exception exception) when (exception is HttpRequestException
                                                       or TaskCanceledException
                                                       or InvalidDataException)
                    {
                        counters.RecordError();
                    }
                });
        }

        var elapsedTime = Stopwatch.GetElapsedTime(startedAt);
        var serverMetricsAfter = await ReadServerMetricsAsync(client);
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var managedAfter = GC.GetTotalMemory(forceFullCollection: false);
        var latency = counters.GetLatencySummary();
        var requestCount = counters.RequestCount;
        var responseCount = counters.ResponseCount;

        return new LoadTestReport(
            "market-read",
            options.Players,
            options.DurationSeconds,
            elapsedTime.TotalMilliseconds,
            (cpuAfter - cpuBefore).TotalMilliseconds,
            managedBefore,
            managedAfter,
            process.WorkingSet64,
            counters.EstimatedRequestBytes,
            checked(counters.ResponseHeaderBytes + counters.ResponseBodyBytes),
            requestCount,
            responseCount,
            0,
            0,
            checked((int)Math.Min(int.MaxValue, counters.ErrorCount)),
            new
            {
                baseUri,
                requestCount,
                responseCount,
                counters.OkResponseCount,
                counters.NotModifiedResponseCount,
                counters.UnexpectedResponseCount,
                counters.BookRequestCount,
                counters.OrdersRequestCount,
                counters.MetricsRequestCount,
                counters.EstimatedRequestBytes,
                counters.ResponseHeaderBytes,
                counters.ResponseBodyBytes,
                averageRequestBytes = requestCount == 0
                    ? 0
                    : counters.EstimatedRequestBytes / (double)requestCount,
                averageResponseBytes = responseCount == 0
                    ? 0
                    : (counters.ResponseHeaderBytes + counters.ResponseBodyBytes) / (double)responseCount,
                requestsPerSecond = elapsedTime.TotalSeconds <= 0
                    ? 0
                    : requestCount / elapsedTime.TotalSeconds,
                latency,
                before = serverMetricsBefore,
                after = serverMetricsAfter,
                serverCpuDeltaMilliseconds = Difference(
                    serverMetricsBefore,
                    serverMetricsAfter,
                    "processCpuMilliseconds"),
                serverManagedMemoryDeltaBytes = Difference(
                    serverMetricsBefore,
                    serverMetricsAfter,
                    "managedMemoryBytes"),
                serverWorkingSetDeltaBytes = Difference(
                    serverMetricsBefore,
                    serverMetricsAfter,
                    "workingSetBytes")
            },
            "500 个读者默认按页面的 3/5/10 秒节奏读取盘口、我的挂单和指标；首轮接收正式 JSON，后续携带服务端 ETag。字节数为未压缩 HTTP/1.1 应用层估算，不包含 TCP/IP 开销。" );
    }

    private static Uri CreateHttpBase(Uri configuredUri)
    {
        var builder = new UriBuilder(configuredUri)
        {
            Scheme = configuredUri.Scheme switch
            {
                "wss" => "https",
                "ws" => "http",
                _ => configuredUri.Scheme
            },
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private static async Task<JsonElement?> ReadServerMetricsAsync(HttpClient client)
    {
        try
        {
            using var response = await client.GetAsync("api/metrics");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<JsonElement>(stream);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or TaskCanceledException
                                           or JsonException)
        {
            return null;
        }
    }

    private static double? Difference(JsonElement? before, JsonElement? after, string propertyName)
    {
        if (before is not { } beforeValue
            || after is not { } afterValue
            || !beforeValue.TryGetProperty(propertyName, out var beforeProperty)
            || !afterValue.TryGetProperty(propertyName, out var afterProperty)
            || !beforeProperty.TryGetDouble(out var beforeNumber)
            || !afterProperty.TryGetDouble(out var afterNumber))
        {
            return null;
        }

        return afterNumber - beforeNumber;
    }

    private static Guid CreatePlayerId(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index + 1);
        bytes[15] = 0x52;
        return new Guid(bytes);
    }

    private sealed class MarketReader
    {
        private readonly RequestState _book = new(
            "api/market/book?itemId=item.spirit-herb&enhancementLevel=0&depth=20",
            MarketReadEndpoint.Book);
        private readonly RequestState _metrics = new("api/market/metrics", MarketReadEndpoint.Metrics);
        private readonly RequestState _orders;

        public MarketReader(Guid playerId)
        {
            _orders = new RequestState(
                $"api/players/{playerId:D}/market/orders",
                MarketReadEndpoint.Orders);
        }

        public Task PollAsync(
            int second,
            HttpClient client,
            MarketReadCounters counters,
            CancellationToken cancellationToken)
        {
            var requests = new List<Task>(3);
            if (second % BookIntervalSeconds == 0)
            {
                requests.Add(SendAsync(client, _book, counters, cancellationToken));
            }
            if (second % OrdersIntervalSeconds == 0)
            {
                requests.Add(SendAsync(client, _orders, counters, cancellationToken));
            }
            if (second % MetricsIntervalSeconds == 0)
            {
                requests.Add(SendAsync(client, _metrics, counters, cancellationToken));
            }

            return Task.WhenAll(requests);
        }

        private static async Task SendAsync(
            HttpClient client,
            RequestState state,
            MarketReadCounters counters,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, state.Path)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            if (state.ETag is { Length: > 0 })
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", state.ETag);
            }

            counters.RecordRequest(
                state.Endpoint,
                EstimateRequestBytes(client.BaseAddress!, request));
            var startedAt = Stopwatch.GetTimestamp();
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            counters.RecordLatency(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            var responseHeaderBytes = EstimateResponseHeaderBytes(response);
            var etag = response.Headers.ETag?.ToString();
            if (!string.IsNullOrWhiteSpace(etag)) state.ETag = etag;

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                {
                    var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (body.Length == 0 || string.IsNullOrWhiteSpace(state.ETag))
                    {
                        counters.RecordUnexpected(responseHeaderBytes, body.Length);
                        return;
                    }

                    counters.RecordOk(responseHeaderBytes, body.Length);
                    return;
                }
                case HttpStatusCode.NotModified:
                    counters.RecordNotModified(responseHeaderBytes);
                    return;
                default:
                {
                    var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    counters.RecordUnexpected(responseHeaderBytes, body.Length);
                    return;
                }
            }
        }

        private static long EstimateRequestBytes(Uri baseAddress, HttpRequestMessage request)
        {
            var path = request.RequestUri?.IsAbsoluteUri == true
                ? request.RequestUri.PathAndQuery
                : new Uri(baseAddress, request.RequestUri!).PathAndQuery;
            var bytes = Encoding.ASCII.GetByteCount(
                $"{request.Method.Method} {path} HTTP/{request.Version.Major}.{request.Version.Minor}\r\n");
            bytes += Encoding.ASCII.GetByteCount($"Host: {baseAddress.Authority}\r\n");
            bytes += Encoding.ASCII.GetByteCount("Accept: application/json\r\n");
            foreach (var header in request.Headers)
            {
                if (string.Equals(header.Key, "Accept", StringComparison.OrdinalIgnoreCase)) continue;
                bytes += Encoding.ASCII.GetByteCount($"{header.Key}: {string.Join(", ", header.Value)}\r\n");
            }

            return bytes + 2;
        }

        private static long EstimateResponseHeaderBytes(HttpResponseMessage response)
        {
            var bytes = Encoding.ASCII.GetByteCount(
                $"HTTP/{response.Version.Major}.{response.Version.Minor} {(int)response.StatusCode} {response.ReasonPhrase}\r\n");
            foreach (var header in response.Headers)
            {
                bytes += Encoding.ASCII.GetByteCount($"{header.Key}: {string.Join(", ", header.Value)}\r\n");
            }
            foreach (var header in response.Content.Headers)
            {
                bytes += Encoding.ASCII.GetByteCount($"{header.Key}: {string.Join(", ", header.Value)}\r\n");
            }

            return bytes + 2;
        }
    }

    private sealed class RequestState(string path, MarketReadEndpoint endpoint)
    {
        public string Path { get; } = path;
        public MarketReadEndpoint Endpoint { get; } = endpoint;
        public string? ETag { get; set; }
    }

    private enum MarketReadEndpoint
    {
        Book,
        Orders,
        Metrics
    }

    private sealed class MarketReadCounters
    {
        private readonly ConcurrentBag<double> _latencies = [];
        private long _requestCount;
        private long _responseCount;
        private long _okResponseCount;
        private long _notModifiedResponseCount;
        private long _unexpectedResponseCount;
        private long _bookRequestCount;
        private long _ordersRequestCount;
        private long _metricsRequestCount;
        private long _estimatedRequestBytes;
        private long _responseHeaderBytes;
        private long _responseBodyBytes;
        private long _errorCount;

        public long RequestCount => Volatile.Read(ref _requestCount);
        public long ResponseCount => Volatile.Read(ref _responseCount);
        public long OkResponseCount => Volatile.Read(ref _okResponseCount);
        public long NotModifiedResponseCount => Volatile.Read(ref _notModifiedResponseCount);
        public long UnexpectedResponseCount => Volatile.Read(ref _unexpectedResponseCount);
        public long BookRequestCount => Volatile.Read(ref _bookRequestCount);
        public long OrdersRequestCount => Volatile.Read(ref _ordersRequestCount);
        public long MetricsRequestCount => Volatile.Read(ref _metricsRequestCount);
        public long EstimatedRequestBytes => Volatile.Read(ref _estimatedRequestBytes);
        public long ResponseHeaderBytes => Volatile.Read(ref _responseHeaderBytes);
        public long ResponseBodyBytes => Volatile.Read(ref _responseBodyBytes);
        public long ErrorCount => Volatile.Read(ref _errorCount);

        public void RecordRequest(MarketReadEndpoint endpoint, long estimatedBytes)
        {
            Interlocked.Increment(ref _requestCount);
            Interlocked.Add(ref _estimatedRequestBytes, estimatedBytes);
            switch (endpoint)
            {
                case MarketReadEndpoint.Book:
                    Interlocked.Increment(ref _bookRequestCount);
                    break;
                case MarketReadEndpoint.Orders:
                    Interlocked.Increment(ref _ordersRequestCount);
                    break;
                case MarketReadEndpoint.Metrics:
                    Interlocked.Increment(ref _metricsRequestCount);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(endpoint));
            }
        }

        public void RecordLatency(double milliseconds) => _latencies.Add(milliseconds);

        public void RecordOk(long headerBytes, long bodyBytes)
        {
            Interlocked.Increment(ref _responseCount);
            Interlocked.Increment(ref _okResponseCount);
            Interlocked.Add(ref _responseHeaderBytes, headerBytes);
            Interlocked.Add(ref _responseBodyBytes, bodyBytes);
        }

        public void RecordNotModified(long headerBytes)
        {
            Interlocked.Increment(ref _responseCount);
            Interlocked.Increment(ref _notModifiedResponseCount);
            Interlocked.Add(ref _responseHeaderBytes, headerBytes);
        }

        public void RecordUnexpected(long headerBytes, long bodyBytes)
        {
            Interlocked.Increment(ref _responseCount);
            Interlocked.Increment(ref _unexpectedResponseCount);
            Interlocked.Increment(ref _errorCount);
            Interlocked.Add(ref _responseHeaderBytes, headerBytes);
            Interlocked.Add(ref _responseBodyBytes, bodyBytes);
        }

        public void RecordError() => Interlocked.Increment(ref _errorCount);

        public object GetLatencySummary()
        {
            var values = _latencies.Order().ToArray();
            return new
            {
                samples = values.Length,
                p50Milliseconds = Percentile(values, 0.50),
                p95Milliseconds = Percentile(values, 0.95),
                p99Milliseconds = Percentile(values, 0.99),
                maxMilliseconds = values.Length == 0 ? 0 : values[^1]
            };
        }

        private static double Percentile(double[] values, double percentile)
        {
            if (values.Length == 0) return 0;
            var index = Math.Clamp(
                (int)Math.Ceiling(percentile * values.Length) - 1,
                0,
                values.Length - 1);
            return values[index];
        }
    }
}
