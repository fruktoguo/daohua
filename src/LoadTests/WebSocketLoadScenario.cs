namespace DaoHua.LoadTests;

/// <summary>
/// 使用 wire.v1 JSON 调试载荷验证真实 WebSocket 边界。
/// 压测工具不引用 Server/Protocol 实现，便于独立验证连接、背压和网络预算。
/// </summary>
public static class WebSocketLoadScenario
{
    /// <summary>
    /// 执行 WebSocket 负载压测。
    /// </summary>
    /// <param name="options">压测选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>WebSocket 压测报告。</returns>
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

    /// <summary>
    /// 基于索引生成 WebSocket 压测的玩家 ID。
    /// </summary>
    /// <param name="index">玩家索引。</param>
    /// <returns>生成的测试 <see cref="Guid"/>。</returns>
    private static Guid CapacityTargetScenarioPlayerId(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index + 1);
        bytes[15] = 0xD3;
        return new Guid(bytes);
    }

    /// <summary>
    /// WebSocket 压测客户端。
    /// </summary>
    private sealed class LoadClient : IAsyncDisposable
    {
        /// <summary>底层 WebSocket 连接。</summary>
        private readonly ClientWebSocket _socket = new();
        /// <summary>连接目标地址。</summary>
        private readonly Uri _endpoint;
        /// <summary>已发送字节数。</summary>
        private long _sentBytes;
        /// <summary>已接收字节数。</summary>
        private long _receivedBytes;
        /// <summary>已发送帧数。</summary>
        private long _sentFrames;
        /// <summary>已接收帧数。</summary>
        private long _receivedFrames;
        /// <summary>错误计数。</summary>
        private int _errors;

        /// <summary>
        /// 初始化客户端并配置鉴权 Cookie 或 Bearer 头。
        /// </summary>
        /// <param name="playerId">玩家 ID。</param>
        /// <param name="configuredUri">配置地址。</param>
        /// <param name="sessionToken">会话令牌，可为 <see langword="null"/>。</param>
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

        /// <summary>获取玩家 ID。</summary>
        public Guid PlayerId { get; }
        /// <summary>获取错误计数。</summary>
        public int Errors => Volatile.Read(ref _errors);
        /// <summary>获取已发送字节数。</summary>
        public long SentBytes => Volatile.Read(ref _sentBytes);
        /// <summary>获取已接收字节数。</summary>
        public long ReceivedBytes => Volatile.Read(ref _receivedBytes);
        /// <summary>获取已发送帧数。</summary>
        public long SentFrames => Volatile.Read(ref _sentFrames);
        /// <summary>获取已接收帧数。</summary>
        public long ReceivedFrames => Volatile.Read(ref _receivedFrames);

        /// <summary>
        /// 生成下一个请求 ID。
        /// </summary>
        /// <returns>新的请求标识。</returns>
        public string NextRequestId() => Guid.NewGuid().ToString("D");

        /// <summary>
        /// 建立 WebSocket 连接并发送 Hello 请求。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>表示连接建立的任务。</returns>
        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _socket.ConnectAsync(_endpoint, timeout.Token);
            await SendAsync(WireEnvelope.Hello(NextRequestId(), PlayerId), timeout.Token);
        }

        /// <summary>
        /// 发送二进制载荷。
        /// </summary>
        /// <param name="payload">要发送的字节。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>表示发送完成的任务。</returns>
        public async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            await _socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken);
            Interlocked.Add(ref _sentBytes, payload.Length);
            Interlocked.Increment(ref _sentFrames);
        }

        /// <summary>
        /// 循环接收消息直到连接关闭或取消。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>表示接收循环的任务。</returns>
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

        /// <summary>
        /// 正常关闭 WebSocket 连接。
        /// </summary>
        /// <returns>表示关闭操作的任务。</returns>
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

        /// <summary>
        /// 释放底层 WebSocket 资源。
        /// </summary>
        /// <returns>表示释放完成的任务。</returns>
        public ValueTask DisposeAsync()
        {
            _socket.Dispose();
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// 将 http/https 地址转换为 ws/wss 地址。
        /// </summary>
        /// <param name="configured">配置地址。</param>
        /// <returns>WebSocket 地址。</returns>
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

    /// <summary>
    /// wire.v1 调试协议包编码辅助类。
    /// </summary>
    private static class WireEnvelope
    {
        /// <summary>
        /// 编码 Hello 请求包。
        /// </summary>
        /// <param name="requestId">请求 ID。</param>
        /// <param name="playerId">玩家 ID。</param>
        /// <returns>编码后的二进制包。</returns>
        public static byte[] Hello(string requestId, Guid playerId)
        {
            var request = new HelloRequest("daohua-loadtests");
            return Encode(WireMessageType.Hello, Guid.Parse(requestId), request);
        }

        /// <summary>
        /// 编码一个玩家在线命令包。
        /// </summary>
        /// <param name="requestId">请求 ID。</param>
        /// <param name="second">当前秒数，用于生成递增逻辑时间。</param>
        /// <returns>编码后的二进制包。</returns>
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

        /// <summary>
        /// 将通用载荷编码为 wire.v1 二进制包。
        /// </summary>
        /// <param name="type">消息类型。</param>
        /// <param name="requestId">请求 ID。</param>
        /// <param name="payload">载荷对象。</param>
        /// <returns>编码后的二进制包。</returns>
        private static byte[] Encode(WireMessageType type, Guid requestId, object payload) =>
            WireEnvelopeCodec.Encode(Protocol.WireEnvelope.Json(type, requestId, 1, payload));
    }

    /// <summary>
    /// 从环境变量加载以逗号分隔的会话令牌列表。
    /// </summary>
    /// <returns>会话令牌数组。</returns>
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
