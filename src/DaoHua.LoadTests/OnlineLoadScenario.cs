using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using DaoHua.Protocol;

namespace DaoHua.LoadTests;

/// <summary>
/// 在线多长连接 WebSocket 实时交互负载压测场景。
/// 模拟真实 ClientWebSocket 并发长连接建立、首包快照同步，并在稳态阶段周期性下发二进制命令，度量吞吐帧率与网络收发。
/// </summary>
public static class OnlineLoadScenario
{
    /// <summary>
    /// 异步执行在线客户端长连接压测：
    /// 1. 并发建立指定数量的 WebSocket 长连接，等待首帧全量快照同步；
    /// 2. 周期性（每秒）分发业务指令（hello、set_auto_use、set_loop）；
    /// 3. 统计网络收发字节、帧数、服务端 CPU/内存抖动与错误连接数。
    /// </summary>
    public static async Task<LoadTestReport> RunAsync(LoadTestOptions options)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var clients = new ConcurrentBag<OnlineClient>();
        var errors = 0;
        var connectGate = new SemaphoreSlim(options.ConnectConcurrency, options.ConnectConcurrency);
        var runId = Guid.NewGuid();
        var serverMetricsBeforeConnect = await ReadServerMetricsAsync(options.ServerUri);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, options.Players),
            new ParallelOptions { MaxDegreeOfParallelism = options.ConnectConcurrency },
            async (index, cancellationToken) =>
            {
                await connectGate.WaitAsync(cancellationToken);
                try
                {
                    var client = new OnlineClient(CreatePlayerId(index, runId), options.ServerUri);
                    await client.ConnectAsync(cancellationToken);
                    clients.Add(client);
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }
                finally
                {
                    connectGate.Release();
                }
            });

        using var receiveCts = new CancellationTokenSource();
        var receiveTasks = clients.Select(client => client.ReceiveLoopAsync(receiveCts.Token)).ToArray();
        using (var initialSyncCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            try
            {
                await Task.WhenAll(clients.Select(client => client.WaitForInitialSnapshotAsync(initialSyncCts.Token)));
            }
            catch (OperationCanceledException) when (initialSyncCts.IsCancellationRequested)
            {
                errors += clients.Count(client => !client.HasInitialSnapshot);
            }
        }

        var initialReceivedBytes = clients.Sum(client => client.ReceivedBytes);
        var initialReceivedFrames = clients.Sum(client => client.ReceivedFrames);
        var sentBytesBeforeWorkload = clients.Sum(client => client.SentBytes);
        var sentFramesBeforeWorkload = clients.Sum(client => client.SentFrames);
        var serverMetricsBeforeWorkload = await ReadServerMetricsAsync(options.ServerUri);
        var stopwatch = Stopwatch.StartNew();
        using var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.DurationSeconds));
        var second = 0;
        try
        {
            while (!runCts.IsCancellationRequested)
            {
                var command = BuildCommand(second++);
                await Parallel.ForEachAsync(clients, runCts.Token, async (client, token) =>
                {
                    try
                    {
                        await client.SendAsync(command(client), token);
                    }
                    catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
                    {
                        Interlocked.Increment(ref errors);
                    }
                });
                await Task.Delay(TimeSpan.FromSeconds(1), runCts.Token);
            }
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested)
        {
            // 达到测试时长。
        }

        var sentBytesAtWorkloadEnd = clients.Sum(client => client.SentBytes);
        var receivedBytesAtWorkloadEnd = clients.Sum(client => client.ReceivedBytes);
        var sentFramesAtWorkloadEnd = clients.Sum(client => client.SentFrames);
        var receivedFramesAtWorkloadEnd = clients.Sum(client => client.ReceivedFrames);
        var serverMetricsAfterWorkload = await ReadServerMetricsAsync(options.ServerUri);
        stopwatch.Stop();

        await Task.WhenAll(clients.Select(client => client.CloseAsync()));
        var allReceivers = Task.WhenAll(receiveTasks.Select(IgnoreCancellation));
        if (await Task.WhenAny(allReceivers, Task.Delay(TimeSpan.FromSeconds(3))) != allReceivers)
        {
            receiveCts.Cancel();
        }

        await allReceivers;
        await Task.WhenAll(clients.Select(client => client.DisposeAsync().AsTask()));
        // 等待服务端完成会话清理，再读取 connectedSessions 等最终指标。
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        var sentBytes = sentBytesAtWorkloadEnd - sentBytesBeforeWorkload;
        var receivedBytes = receivedBytesAtWorkloadEnd - initialReceivedBytes;
        var sentFrames = sentFramesAtWorkloadEnd - sentFramesBeforeWorkload;
        var receivedFrames = receivedFramesAtWorkloadEnd - initialReceivedFrames;
        var serverMetricsAfterDisconnect = await ReadServerMetricsAsync(options.ServerUri);
        object serverMetrics = new
        {
            runId,
            initialSync = new { receivedBytes = initialReceivedBytes, receivedFrames = initialReceivedFrames },
            beforeConnect = serverMetricsBeforeConnect,
            beforeWorkload = serverMetricsBeforeWorkload,
            afterWorkload = serverMetricsAfterWorkload,
            afterDisconnect = serverMetricsAfterDisconnect,
            connectionCpuDeltaMilliseconds = Difference(serverMetricsBeforeConnect, serverMetricsBeforeWorkload, "processCpuMilliseconds"),
            workloadCpuDeltaMilliseconds = Difference(serverMetricsBeforeWorkload, serverMetricsAfterWorkload, "processCpuMilliseconds"),
            disconnectCpuDeltaMilliseconds = Difference(serverMetricsAfterWorkload, serverMetricsAfterDisconnect, "processCpuMilliseconds"),
            managedMemoryDeltaBytes = Difference(serverMetricsBeforeConnect, serverMetricsAfterDisconnect, "managedMemoryBytes"),
            workingSetDeltaBytes = Difference(serverMetricsBeforeConnect, serverMetricsAfterDisconnect, "workingSetBytes")
        };
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var managedAfter = GC.GetTotalMemory(forceFullCollection: false);

        return new LoadTestReport(
            "online",
            options.Players,
            options.DurationSeconds,
            stopwatch.Elapsed.TotalMilliseconds,
            (cpuAfter - cpuBefore).TotalMilliseconds,
            managedBefore,
            managedAfter,
            process.WorkingSet64,
            sentBytes,
            receivedBytes,
            sentFrames,
            receivedFrames,
            0,
            0,
            errors,
            serverMetrics,
            "每个客户端使用真实 ClientWebSocket；初始快照单独统计，稳态阶段通过生产 DaoHuaPacketCodec 发送相同且有界的命令节奏。" );
    }

    private static Func<OnlineClient, byte[]> BuildCommand(int second)
    {
        return (second % 4) switch
        {
            0 => client => DaoHuaPacketCodec.EncodeHello(client.NextRequestId(), new HelloRequest()),
            1 => client => DaoHuaPacketCodec.EncodeCommand(
                client.NextRequestId(),
                "set_auto_use",
                new SetAutoUseRequest("cultivation", "item.qi-gathering-pill", 1, true)),
            2 => client => DaoHuaPacketCodec.EncodeCommand(
                client.NextRequestId(),
                "set_loop",
                new SetLoopRequest(false)),
            _ => client => DaoHuaPacketCodec.EncodeCommand(
                client.NextRequestId(),
                "set_loop",
                new SetLoopRequest(true))
        };
    }

    private static async Task<JsonElement?> ReadServerMetricsAsync(Uri webSocketUri)
    {
        try
        {
            var scheme = webSocketUri.Scheme == "wss" ? "https" : "http";
            using var httpClient = new HttpClient();
            var uri = new UriBuilder(webSocketUri) { Scheme = scheme, Path = "/api/metrics", Query = string.Empty }.Uri;
            var json = await httpClient.GetStringAsync(uri);
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
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

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            // 客户端关闭后的预期退出。
        }
    }

    private static Guid CreatePlayerId(int index, Guid runId)
    {
        Span<byte> bytes = stackalloc byte[16];
        runId.TryWriteBytes(bytes);
        BitConverter.TryWriteBytes(bytes, index + 1);
        return new Guid(bytes);
    }

    private sealed class OnlineClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly Uri _endpoint;
        private long _requestSequence;
        private long _sentBytes;
        private long _receivedBytes;
        private long _sentFrames;
        private long _receivedFrames;
        private readonly TaskCompletionSource _initialSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OnlineClient(Guid playerId, Uri baseUri)
        {
            PlayerId = playerId;
            var builder = new UriBuilder(baseUri);
            var separator = string.IsNullOrEmpty(builder.Query) ? string.Empty : "&";
            builder.Query = $"{builder.Query.TrimStart('?')}{separator}playerId={playerId:D}&name=负载修士";
            _endpoint = builder.Uri;
        }

        public Guid PlayerId { get; }
        public long SentBytes => Volatile.Read(ref _sentBytes);
        public long ReceivedBytes => Volatile.Read(ref _receivedBytes);
        public long SentFrames => Volatile.Read(ref _sentFrames);
        public long ReceivedFrames => Volatile.Read(ref _receivedFrames);
        public bool HasInitialSnapshot => _initialSnapshot.Task.IsCompletedSuccessfully;

        public string NextRequestId() => $"{PlayerId:N}-{Interlocked.Increment(ref _requestSequence)}";

        public Task ConnectAsync(CancellationToken cancellationToken) => _socket.ConnectAsync(_endpoint, cancellationToken);

        public Task WaitForInitialSnapshotAsync(CancellationToken cancellationToken) =>
            _initialSnapshot.Task.WaitAsync(cancellationToken);

        public async Task SendAsync(byte[] packet, CancellationToken cancellationToken)
        {
            await _socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);
            Interlocked.Add(ref _sentBytes, packet.Length);
            Interlocked.Increment(ref _sentFrames);
        }

        public async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            MemoryStream? initialPacket = new();
            try
            {
                while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var result = await _socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    Interlocked.Add(ref _receivedBytes, result.Count);
                    initialPacket?.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;

                    Interlocked.Increment(ref _receivedFrames);
                    if (initialPacket is not null)
                    {
                        if (DaoHuaPacketCodec.TryDecode(initialPacket.ToArray(), out var packet)
                            && packet.Kind == DaoHuaPacketKinds.Snapshot)
                        {
                            _initialSnapshot.TrySetResult();
                            initialPacket.Dispose();
                            initialPacket = null;
                        }
                        else
                        {
                            initialPacket.SetLength(0);
                        }
                    }
                }
            }
            finally
            {
                initialPacket?.Dispose();
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public async Task CloseAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "load-complete", closeCts.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
                {
                    _socket.Abort();
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
