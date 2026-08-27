namespace DaoHua.LoadTests;

/// <summary>
/// 使用 wire.v1 JSON 调试载荷验证真实 WebSocket 边界。
/// 压测工具不引用 Server/Protocol 实现，便于独立验证连接、背压和网络预算。
/// </summary>
public static class WebSocketLoadScenario
{
    public static async Task<LoadTestReport> RunAsync(LoadTestOptions options, CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var cpuBefore = process.TotalProcessorTime;
        var clients = new ConcurrentBag<LoadClient>();
        var warnings = new ConcurrentBag<string>();
        var errors = 0;
        var started = Stopwatch.GetTimestamp();
        var sessionTokens = LoadSessionTokens();
        if (options.RequireServer && sessionTokens.Length < options.Players)
        {
            warnings.Add($"真实 WebSocket 门禁需要每个玩家一个独立会话令牌，当前仅提供 {sessionTokens.Length}/{options.Players} 个。");
            errors++;
        }

        await Parallel.ForEachAsync(
            Enumerable.Range(0, options.Players),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.Concurrency,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                var tokenValue = index < sessionTokens.Length ? sessionTokens[index] : null;
                var client = new LoadClient(CapacityTargetScenarioPlayerId(index), options.ServerUri, tokenValue);
                try
                {
                    await client.ConnectAsync(token);
                    clients.Add(client);
                }
                catch (Exception exception) when (exception is WebSocketException or HttpRequestException or OperationCanceledException)
                {
                    Interlocked.Increment(ref errors);
                    warnings.Add($"连接 {index} 失败: {exception.Message}");
                    await client.DisposeAsync();
                }
            });

        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiveTasks = clients.Select(client => client.ReceiveLoopAsync(receiveCts.Token)).ToArray();
        var workloadStarted = Stopwatch.GetTimestamp();
        try
        {
            for (var second = 0; second < options.DurationSeconds; second++)
            {
                await Parallel.ForEachAsync(
                    clients,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = options.Concurrency,
                        CancellationToken = cancellationToken
                    },
                    async (client, token) =>
                    {
                        try
                        {
                            await client.SendAsync(WireEnvelope.Command(client.NextRequestId(), second), token);
                        }
                        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
                        {
                            Interlocked.Increment(ref errors);
                            warnings.Add($"发送失败: {exception.Message}");
                        }
                    });
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            warnings.Add("WebSocket 压测被取消。");
        }

        foreach (var client in clients)
        {
            await client.CloseAsync();
        }

        receiveCts.Cancel();
        try
        {
            await Task.WhenAll(receiveTasks);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            // 连接关闭产生的异常属于预期清理路径。
        }

        foreach (var client in clients)
        {
            await client.DisposeAsync();
            errors += client.Errors;
        }

        process.Refresh();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var workloadElapsed = Stopwatch.GetElapsedTime(workloadStarted);
        var sentBytes = clients.Sum(client => client.SentBytes);
        var receivedBytes = clients.Sum(client => client.ReceivedBytes);
        var connected = clients.Count;
        if (options.RequireServer && connected != options.Players) errors++;

        return new LoadTestReport(
            "loadtest.v1",
            "websocket",
            options.Players,
            options.DurationSeconds,
            elapsed.TotalMilliseconds,
            (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            managedBefore,
            GC.GetTotalMemory(forceFullCollection: false),
            process.WorkingSet64,
            sentBytes,
            receivedBytes,
            clients.Sum(client => client.SentFrames),
            clients.Sum(client => client.ReceivedFrames),
            errors,
            errors == 0,
            new Dictionary<string, object?>
            {
                ["connectedClients"] = connected,
                ["connectionSuccessRate"] = options.Players == 0 ? 0 : connected / (double)options.Players,
                ["workloadElapsedMilliseconds"] = workloadElapsed.TotalMilliseconds,
                ["sentBytesPerSecond"] = workloadElapsed.TotalSeconds <= 0 ? 0 : sentBytes / workloadElapsed.TotalSeconds,
                ["receivedBytesPerSecond"] = workloadElapsed.TotalSeconds <= 0 ? 0 : receivedBytes / workloadElapsed.TotalSeconds,
                ["averageInitialFrames"] = connected == 0 ? 0 : clients.Sum(client => client.ReceivedFrames) / (double)connected
            },
            warnings.Distinct().Take(20).ToArray());
    }

    private static Guid CapacityTargetScenarioPlayerId(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index + 1);
        bytes[15] = 0xD3;
        return new Guid(bytes);
    }

    private sealed class LoadClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly Uri _endpoint;
        private long _sentBytes;
        private long _receivedBytes;
        private long _sentFrames;
        private long _receivedFrames;
        private int _errors;

        public LoadClient(Guid playerId, Uri configuredUri, string? sessionToken)
        {
            PlayerId = playerId;
            _endpoint = ToWebSocketUri(configuredUri);
            if (!string.IsNullOrWhiteSpace(sessionToken))
            {
                if (sessionToken.Contains('=', StringComparison.Ordinal))
                    _socket.Options.SetRequestHeader("Cookie", sessionToken);
                else
                    _socket.Options.SetRequestHeader("Authorization", $"Bearer {sessionToken}");
            }
        }

        public Guid PlayerId { get; }
        public int Errors => Volatile.Read(ref _errors);
        public long SentBytes => Volatile.Read(ref _sentBytes);
        public long ReceivedBytes => Volatile.Read(ref _receivedBytes);
        public long SentFrames => Volatile.Read(ref _sentFrames);
        public long ReceivedFrames => Volatile.Read(ref _receivedFrames);

        public string NextRequestId() => Guid.NewGuid().ToString("D");

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _socket.ConnectAsync(_endpoint, timeout.Token);
            await SendAsync(WireEnvelope.Hello(NextRequestId(), PlayerId), timeout.Token);
        }

        public async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            await _socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken);
            Interlocked.Add(ref _sentBytes, payload.Length);
            Interlocked.Increment(ref _sentFrames);
        }

        public async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var result = await _socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    Interlocked.Add(ref _receivedBytes, result.Count);
                    if (result.EndOfMessage) Interlocked.Increment(ref _receivedFrames);
                }
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
            {
                Interlocked.Increment(ref _errors);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public async Task CloseAsync()
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "loadtest", timeout.Token);
                }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
                {
                    Interlocked.Increment(ref _errors);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _socket.Dispose();
            return ValueTask.CompletedTask;
        }

        private static Uri ToWebSocketUri(Uri configured)
        {
            var builder = new UriBuilder(configured)
            {
                Scheme = configured.Scheme switch
                {
                    "http" => "ws",
                    "https" => "wss",
                    _ => configured.Scheme
                },
                Path = string.IsNullOrWhiteSpace(configured.AbsolutePath) || configured.AbsolutePath == "/"
                    ? "/ws"
                    : configured.AbsolutePath,
                Query = string.Empty
            };
            return builder.Uri;
        }
    }

    private static class WireEnvelope
    {
        public static byte[] Hello(string requestId, Guid playerId)
        {
            var request = new HelloRequest("daohua-loadtests");
            return Encode(WireMessageType.Hello, Guid.Parse(requestId), request);
        }

        public static byte[] Command(string requestId, int second)
        {
            var envelope = Protocol.WireEnvelope.Json(
                WireMessageType.Command,
                Guid.Parse(requestId),
                second + 2L,
                new CommandEnvelopeDto(
                    requestId,
                    "player.presence",
                    JsonSerializer.SerializeToElement(new { presence = "online", requestId }, ProtocolJson.Options),
                    0));
            return WireEnvelopeCodec.Encode(envelope);
        }

        private static byte[] Encode(WireMessageType type, Guid requestId, object payload) =>
            WireEnvelopeCodec.Encode(Protocol.WireEnvelope.Json(type, requestId, 1, payload));
    }

    private static string[] LoadSessionTokens()
    {
        var raw = Environment.GetEnvironmentVariable("DAOHUA_LOADTEST_SESSION_TOKENS")
                  ?? Environment.GetEnvironmentVariable("DAOHUA_LOADTEST_SESSION_TOKEN")
                  ?? string.Empty;
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
