namespace DaoHua.ComputeNode;

/// <summary>
/// ComputeNode 客户端模式：主动向主服拨出 WebSocket 长连接（compute-link.v1），
/// 注册即进入动态节点组，接收批次、执行确定性推进、回传结果。
/// 链路中断按指数退避自动重连（1s→30s 封顶 ±20% 抖动）；配表指纹不一致时
/// 主服拒绝握手，节点保持退避重试直到被重部署为匹配版本。
/// 节点在 NAT/防火墙后也可工作：只需要出方向 443/80 连通主服，无任何入站端口要求。
/// </summary>
public sealed class UpstreamLinkWorker(
    string upstreamUrl,
    string token,
    string nodeName,
    DeterministicBatchEngine engine,
    GameDataFingerprint fingerprint,
    ComputeMetrics metrics,
    int maxJobsPerBatch,
    int maxConcurrentBatches,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private readonly ILogger _logger = loggerFactory.CreateLogger("ComputeNode.Upstream");
    private readonly SemaphoreSlim _batchGate = new(maxConcurrentBatches, maxConcurrentBatches);

    /// <summary>节点名称（hello 自报，主服仅用于诊断展示）。</summary>
    public string NodeName { get; } = string.IsNullOrWhiteSpace(nodeName) ? "compute-node" : nodeName.Trim();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = MinRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(stoppingToken);
                retryDelay = MinRetryDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning("主服链路中断（{Error}），{Delay:N1}s 后重连。",
                    exception.Message, retryDelay.TotalSeconds);
            }

            try
            {
                var jitter = 1 + (Random.Shared.NextDouble() - 0.5) * 0.4;
                await Task.Delay(TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * jitter), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            retryDelay = TimeSpan.FromMilliseconds(
                Math.Min(MaxRetryDelay.TotalMilliseconds, retryDelay.TotalMilliseconds * 2));
        }
    }

    /// <summary>一次完整链路会话：连接 → hello 握手 → 收发双循环直到断开。</summary>
    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(ComputeLinkContract.TokenHeader, token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var uri = BuildWebSocketUri(upstreamUrl);
        await socket.ConnectAsync(uri, cancellationToken);

        await socket.SendAsync(
            LinkCodec.Encode(new LinkHello(
                ComputeLinkContract.Version, NodeName, maxConcurrentBatches, fingerprint.Value)),
            WebSocketMessageType.Text, true, cancellationToken);

        var first = await ReceiveMessageAsync(socket, cancellationToken)
            .WaitAsync(ComputeLinkContract.WelcomeTimeout, cancellationToken);
        switch (first)
        {
            case LinkError error:
                throw new InvalidOperationException($"主服拒绝接入 {error.Code}: {error.Message}");
            case LinkWelcome welcome:
                if (!string.Equals(welcome.ContractVersion, ComputeLinkContract.Version, StringComparison.Ordinal))
                    throw new InvalidDataException($"主服链路契约版本不匹配：{welcome.ContractVersion}。");
                break;
            default:
                throw new InvalidDataException("主服首帧必须是 welcome。");
        }
        _logger.LogInformation("已接入主服 node={NodeName} upstream={Upstream}", NodeName, uri);

        var outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { SingleReader = true });
        var sendTask = SendLoopAsync(socket, outbound.Reader, cancellationToken);
        var receiveTask = ReceiveLoopAsync(socket, outbound.Writer, cancellationToken);
        await Task.WhenAny(sendTask, receiveTask);
        outbound.Writer.TryComplete();
        // 任一循环退出即链路不可用：等待另一路收尾并把会话交回重连逻辑。
        try { await Task.WhenAll(sendTask, receiveTask); } catch { }
        throw new InvalidOperationException("主服链路已断开。");
    }

    /// <summary>接收循环：batch 帧交给受闸批次任务；welcome/error/其他帧一律判协议违规断开。</summary>
    private async Task ReceiveLoopAsync(WebSocket socket, ChannelWriter<byte[]> outbound, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var message = await ReceiveMessageAsync(socket, token);
            switch (message)
            {
                case LinkBatch batch:
                    // 在接收循环内先占闸再派生：节点满负荷时对主服形成自然背压。
                    await _batchGate.WaitAsync(token);
                    _ = ProcessBatchAsync(batch, outbound, token);
                    break;
                case LinkError error:
                    throw new InvalidOperationException($"主服关闭链路 {error.Code}: {error.Message}");
                default:
                    throw new InvalidDataException($"链路不接受该帧类型：{message.GetType().Name}。");
            }
        }
    }

    /// <summary>发送循环：消费出站队列并写入 socket。</summary>
    private static async Task SendLoopAsync(WebSocket socket, ChannelReader<byte[]> outbound, CancellationToken token)
    {
        await foreach (var frame in outbound.ReadAllAsync(token))
            await socket.SendAsync(frame, WebSocketMessageType.Text, true, token);
    }

    /// <summary>执行一个批次：契约校验 + 指纹比对 + 逐任务推进，结果经出站队列回传。</summary>
    private async Task ProcessBatchAsync(LinkBatch batch, ChannelWriter<byte[]> outbound, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        metrics.BeginBatch();
        try
        {
            List<AdvanceJobResult> results;
            var validation = ComputeContract.Validate(batch.Request, maxJobsPerBatch);
            if (validation is not null)
            {
                // 批次级契约违规：按任务维度回 Failed，主服按不可重试落 dead 保留证据。
                results = (batch.Request.Jobs ?? [])
                    .Select(job => job is null
                        ? new AdvanceJobResult(Guid.Empty, Guid.Empty, false, 0, 0, null, null,
                            "INVALID_BATCH", "批次契约校验失败。", false)
                        : AdvanceJobResult.Failed(job, "INVALID_BATCH", "批次契约校验失败。"))
                    .ToList();
            }
            else if (!string.IsNullOrWhiteSpace(batch.Request.GameDataFingerprint)
                && !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(batch.Request.GameDataFingerprint),
                    Encoding.UTF8.GetBytes(fingerprint.Value)))
            {
                // 指纹漂移意味着主服已升级而节点二进制过期：断链重连触发 hello 重新裁决，
                // 节点保持退避直到被重部署，避免把新配表任务算成旧口径。
                throw new InvalidDataException(
                    $"配表指纹不匹配 expected={fingerprint.Value} actual={batch.Request.GameDataFingerprint}");
            }
            else
            {
                results = new List<AdvanceJobResult>(batch.Request.Jobs.Count);
                foreach (var job in batch.Request.Jobs)
                {
                    token.ThrowIfCancellationRequested();
                    var result = engine.Advance(job, fingerprint.Value);
                    results.Add(result);
                    if (result.Success) metrics.RecordCompletedJob();
                    else metrics.RecordFailedJob();
                }
            }

            await outbound.WriteAsync(
                LinkCodec.Encode(new LinkResult(batch.BatchId, new AdvanceBatchResponse(
                    ComputeContract.Version,
                    fingerprint.Value,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    results))),
                token);
            metrics.RecordCompletedBatch(Stopwatch.GetElapsedTime(started));
        }
        finally
        {
            metrics.EndBatch();
            _batchGate.Release();
        }
    }

    /// <summary>读取一条完整文本帧（处理分片），超过单帧上限即抛异常。</summary>
    private static async Task<object> ReceiveMessageAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        var scratch = new byte[64 * 1024];
        while (true)
        {
            var received = await socket.ReceiveAsync(scratch, token);
            if (received.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("主服关闭了链路。");
            if (received.MessageType == WebSocketMessageType.Binary)
                throw new InvalidDataException("链路只接受文本帧。");
            if (buffer.WrittenCount + received.Count > ComputeLinkContract.MaxMessageBytes)
                throw new InvalidDataException("链路帧超过大小上限。");
            buffer.Write(scratch.AsSpan(0, received.Count));
            if (received.EndOfMessage) break;
        }
        return LinkCodec.Decode(buffer.WrittenMemory);
    }

    /// <summary>把 http(s)/ws(s) 上游地址归一化为 WebSocket URI。</summary>
    private static Uri BuildWebSocketUri(string upstream)
    {
        if (!Uri.TryCreate(upstream.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"ComputeNode:UpstreamUrl 不是合法地址：{upstream}。");
        var builder = new UriBuilder(uri);
        builder.Scheme = uri.Scheme switch
        {
            "ws" => "ws",
            "wss" => "wss",
            "http" => "ws",
            "https" => "wss",
            var scheme => throw new InvalidOperationException(
                $"ComputeNode:UpstreamUrl 只支持 ws/wss/http/https：{scheme}。")
        };
        builder.Port = uri.IsDefaultPort ? -1 : uri.Port;
        return builder.Uri;
    }
}
