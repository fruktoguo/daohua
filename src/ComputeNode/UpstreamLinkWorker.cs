namespace DaoHua.ComputeNode;

/// <summary>
/// ComputeNode 客户端模式：主动向主服拨出 WebSocket 长连接（compute-link.v1.3），
/// 注册即进入动态节点组，接收批次、执行确定性推进、回传结果。
/// 上游支持多入口列表（按列表序优先）：当前上游断线/握手失败时按既有指数退避换下一个，
/// 全部失败绕回头部继续退避，成功后持续使用该上游直到再次断线——多主服多入口下
/// 单点故障/滚动升级可自动转移落点。
/// 链路中断按指数退避自动重连（1s→30s 封顶 ±20% 抖动）；连续健康存活超过
/// <see cref="HealthySessionThreshold"/> 的会话断开时退避立即复位，快断快连则保持翻倍。
/// 配表指纹不一致时主服拒绝握手，节点保持退避重试直到被重部署为匹配版本。
/// 节点在 NAT/防火墙后也可工作：只需要出方向 443/80 连通主服，无任何入站端口要求。
/// </summary>
public sealed class UpstreamLinkWorker(
    IReadOnlyList<string> upstreamUrls,
    string token,
    string nodeName,
    DeterministicBatchEngine engine,
    GameDataCatalog gameData,
    ComputeMetrics metrics,
    SemaphoreSlim batchGate,
    int maxJobsPerBatch,
    int maxConcurrentBatches,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly IReadOnlyList<string> _upstreams = upstreamUrls is { Count: > 0 }
        ? upstreamUrls
        : throw new ArgumentException("至少配置一个主服上游地址（ComputeNode:UpstreamUrls）。", nameof(upstreamUrls));
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    /// <summary>会话健康存活阈值：超过该时长的链路断开视为偶发断线，退避复位而非继续翻倍。</summary>
    private static readonly TimeSpan HealthySessionThreshold = TimeSpan.FromSeconds(60);
    /// <summary>节点停机排空窗口：上报 draining 后给在飞批次跑完回传的有界时间，窗口外取消按租约退回。</summary>
    private static readonly TimeSpan NodeDrainWindow = TimeSpan.FromSeconds(10);
    private readonly ILogger _logger = loggerFactory.CreateLogger("ComputeNode.Upstream");
    /// <summary>主服下发 drain 后置位：本链路在飞批次跑完即主动断链，消费后复位并立即重连。</summary>
    private int _drainRequested;
    /// <summary>在飞批次数（ProcessBatchAsync 簿记）：排空等待以此判定"手头算力已清空"。</summary>
    private int _inFlightBatches;

    /// <summary>节点名称（hello 自报，主服仅用于诊断展示）。</summary>
    public string NodeName { get; } = string.IsNullOrWhiteSpace(nodeName) ? "compute-node" : nodeName.Trim();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = MinRetryDelay;
        var upstreamIndex = 0;
        // 上游地址不是凭证（token 只走请求头）：启动即记录完整候选列表与每次选择，便于多入口排障。
        _logger.LogInformation("主服上游列表共 {Count} 个（按优先级序）：{Upstreams}",
            _upstreams.Count, string.Join(" -> ", _upstreams.Select(DescribeUpstream)));
        while (!stoppingToken.IsCancellationRequested)
        {
            var upstream = _upstreams[upstreamIndex];
            var sessionStarted = Stopwatch.GetTimestamp();
            try
            {
                _logger.LogInformation("拨入上游 {Index}/{Count} upstream={Upstream}",
                    upstreamIndex + 1, _upstreams.Count, DescribeUpstream(upstream));
                await RunSessionAsync(upstream, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // 主服排空断链不算故障：完成在飞后主动断开，立即重连（落点可能是已恢复/其他主服实例）。
                var drained = Interlocked.Exchange(ref _drainRequested, 0) != 0;
                // 存活超过健康阈值的链路断开视为偶发断线：退避复位到最小档，不空等满退避；
                // 快断快连的会话保留当前退避并继续翻倍，避免弱网设备刷重连压垮主服握手。
                if (drained || Stopwatch.GetElapsedTime(sessionStarted) >= HealthySessionThreshold)
                    retryDelay = MinRetryDelay;
                // 断线/握手失败/主服排空一律按列表序换下一个上游，末尾绕回头部继续退避：
                // 多入口拓扑下不钉死故障上游，单入口时 index 不变等价原重连行为。
                upstreamIndex = (upstreamIndex + 1) % _upstreams.Count;
                if (drained)
                    _logger.LogInformation("主服链路排空完成，改拨下一上游 {Index}/{Count} upstream={Upstream}。",
                        upstreamIndex + 1, _upstreams.Count, DescribeUpstream(_upstreams[upstreamIndex]));
                else if (stoppingToken.IsCancellationRequested)
                    _logger.LogInformation("节点停机，链路关闭。");
                else
                    _logger.LogWarning("上游 upstream={Upstream} 链路中断（{Error}），{Delay:N1}s 后拨下一上游 {Index}/{Count} upstream={NextUpstream}。",
                        DescribeUpstream(upstream), exception.Message, retryDelay.TotalSeconds,
                        upstreamIndex + 1, _upstreams.Count, DescribeUpstream(_upstreams[upstreamIndex]));
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
    private async Task RunSessionAsync(string upstream, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(ComputeLinkContract.TokenHeader, token);
        // 显式 keep-alive：20s 探测 + 60s 判定死亡，弱网半开连接可在分钟级内被发现并重连，
        // 与主服 UseWebSockets 的全局口径一致。
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(60);
        var uri = BuildWebSocketUri(upstream);
        await socket.ConnectAsync(uri, cancellationToken);

        await socket.SendAsync(
            LinkCodec.Encode(new LinkHello(
                ComputeLinkContract.Version, NodeName, maxConcurrentBatches, gameData.Fingerprint,
                maxJobsPerBatch, ComputeContract.MaxBatchBytes, NodeRuntime.Fingerprint)),
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
        _logger.LogInformation("已接入主服 node={NodeName} upstream={Upstream}", NodeName, DescribeUri(uri));

        var outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { SingleReader = true });
        // 链路环与在飞批次用两个取消源：session 控制收发循环，workCts 控制在飞计算。
        // 断链/协议违规时两者一起取消；节点停机时先入排空流程（上报 draining + 在飞窗口内跑完），
        // 不随 stoppingToken 直接取消批次，避免把主服已派出去的活白算丢弃。
        using var session = new CancellationTokenSource();
        using var workCts = new CancellationTokenSource();
        var sessionToken = session.Token;
        using var stopRegistration = cancellationToken.Register(
            () => BeginNodeDrain(session, workCts, outbound.Writer));
        var sendTask = SendLoopAsync(socket, outbound.Reader, sessionToken);
        var receiveTask = ReceiveLoopAsync(socket, outbound.Writer, session, workCts);
        await Task.WhenAny(sendTask, receiveTask);
        session.Cancel();
        workCts.Cancel();
        outbound.Writer.TryComplete();
        // 任一循环退出即链路不可用：等待另一路收尾并把会话交回重连逻辑。
        try { await Task.WhenAll(sendTask, receiveTask); } catch { }
        throw new InvalidOperationException("主服链路已断开。");
    }

    /// <summary>
    /// 节点停机排空（stoppingToken 触发，经注册回调在取消线程上同步起跳）：
    /// 先上报 draining 让主服立即停派新批次；在飞批次给有界窗口跑完回传后才断链，
    /// 窗口外残余批次取消，未完结任务按租约语义由主服重派。
    /// </summary>
    private void BeginNodeDrain(CancellationTokenSource session, CancellationTokenSource workCts, ChannelWriter<byte[]> outbound)
    {
        outbound.TryWrite(LinkCodec.Encode(new LinkDraining("node shutdown")));
        _ = Task.Run(async () =>
        {
            var deadline = Stopwatch.GetTimestamp() + (long)(NodeDrainWindow.TotalSeconds * Stopwatch.Frequency);
            while (Volatile.Read(ref _inFlightBatches) > 0 && Stopwatch.GetTimestamp() < deadline)
                await Task.Delay(100).ConfigureAwait(false);
            workCts.Cancel();
            session.Cancel();
        });
    }

    /// <summary>接收循环：batch 帧交给受闸批次任务；drain 帧进入排空；welcome/error/其他帧一律判协议违规断开。</summary>
    private async Task ReceiveLoopAsync(
        WebSocket socket, ChannelWriter<byte[]> outbound, CancellationTokenSource session, CancellationTokenSource workCts)
    {
        var token = session.Token;
        while (!token.IsCancellationRequested)
        {
            var message = await ReceiveMessageAsync(socket, token);
            switch (message)
            {
                case LinkBatch batch:
                    // 在接收循环内先占闸再派生：节点满负荷时对主服形成自然背压。
                    await batchGate.WaitAsync(token);
                    Interlocked.Increment(ref _inFlightBatches);
                    _ = ProcessBatchAsync(batch, outbound, session, workCts);
                    break;
                case LinkDrain drain:
                    // 主服排空/下线：不再接新批次（主服侧已停派），在飞跑完即主动断链重连。
                    if (Interlocked.Exchange(ref _drainRequested, 1) == 0)
                        _logger.LogInformation("主服请求排空 reason={Reason}：在飞批次跑完后断链重连。",
                            drain.Reason ?? "未说明");
                    if (Volatile.Read(ref _inFlightBatches) == 0) session.Cancel();
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

    /// <summary>执行一个批次：契约校验 + 指纹比对 + 逐任务推进，结果经出站队列回传。批次取消挂 workCts：断链立即取消，停机排空窗口内照常跑完。</summary>
    private async Task ProcessBatchAsync(
        LinkBatch batch, ChannelWriter<byte[]> outbound, CancellationTokenSource session, CancellationTokenSource workCts)
    {
        var token = workCts.Token;
        var started = Stopwatch.GetTimestamp();
        metrics.BeginBatch();
        try
        {
            List<AdvanceJobResult> results;
            var violation = ComputeContract.ValidateBatch(batch.Request, maxJobsPerBatch);
            if (violation is not null)
            {
                // 批次级契约违规是主服装配/链路问题，与单任务载荷无关：全部按可重试回传，
                // 由主服退避重派——不可整批 non-retryable 判死（dead 任务会被冷扫描再生产，
                // 形成「再生产→整批判死」循环）。
                results = (batch.Request.Jobs ?? [])
                    .Select(job => job is null
                        ? new AdvanceJobResult(Guid.Empty, Guid.Empty, false, 0, 0, null, null, null,
                            violation.Code, violation.Message, true)
                        : AdvanceJobResult.Failed(job, violation.Code, violation.Message, retryable: true))
                    .ToList();
            }
            else if (!string.IsNullOrWhiteSpace(batch.Request.GameDataFingerprint)
                && !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(batch.Request.GameDataFingerprint),
                    Encoding.UTF8.GetBytes(gameData.Fingerprint)))
            {
                // 指纹漂移意味着主服已升级而节点二进制过期：主动断开整条链路，
                // 重连时 hello 重新裁决、节点保持退避直到被重部署，避免把新配表任务算成旧口径。
                // 本批次不回传结果，主服按租约超时退回改派。
                _logger.LogWarning(
                    "批次配表指纹不匹配，断开链路重新握手 expected={Expected} actual={Actual}",
                    gameData.Fingerprint, batch.Request.GameDataFingerprint);
                metrics.RecordCancelledBatch();
                workCts.Cancel();
                session.Cancel();
                return;
            }
            else
            {
                results = new List<AdvanceJobResult>(batch.Request.Jobs.Count);
                foreach (var job in batch.Request.Jobs)
                {
                    token.ThrowIfCancellationRequested();
                    // 任务级契约违规只判死该任务自身（non-retryable），不连坐批内其他任务。
                    var jobViolation = ComputeContract.ValidateJob(job);
                    var result = job is null
                        ? new AdvanceJobResult(Guid.Empty, Guid.Empty, false, 0, 0, null, null, null,
                            jobViolation?.Code ?? "INVALID_JOB_ID", jobViolation?.Message ?? "任务为空。", false)
                        : jobViolation is not null
                            ? AdvanceJobResult.Failed(job, jobViolation.Code, jobViolation.Message)
                            : await engine.AdvanceAsync(job, token);
                    results.Add(result);
                    if (result.Success) metrics.RecordCompletedJob();
                    else metrics.RecordFailedJob();
                }
            }

            await outbound.WriteAsync(
                LinkCodec.Encode(new LinkResult(batch.BatchId, new AdvanceBatchResponse(
                    ComputeContract.Version,
                    gameData.Fingerprint,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    results))),
                token);
            metrics.RecordCompletedBatch(Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 会话结束/断链取消在飞计算：批次结果不再回传，主服按租约超时退回重派。
            metrics.RecordCancelledBatch();
            throw;
        }
        finally
        {
            metrics.EndBatch();
            batchGate.Release();
            // 排空等待点：主服 drain 后在飞清空即断链，交给重连逻辑落到新上游。
            if (Interlocked.Decrement(ref _inFlightBatches) == 0 && _drainRequested != 0)
                session.Cancel();
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
            throw new InvalidOperationException($"ComputeNode:UpstreamUrls 含非法上游地址：{upstream}。");
        var builder = new UriBuilder(uri);
        builder.Scheme = uri.Scheme switch
        {
            "ws" => "ws",
            "wss" => "wss",
            "http" => "ws",
            "https" => "wss",
            var scheme => throw new InvalidOperationException(
                $"ComputeNode:UpstreamUrls 只支持 ws/wss/http/https：{scheme}。")
        };
        builder.Port = uri.IsDefaultPort ? -1 : uri.Port;
        return builder.Uri;
    }

    /// <summary>上游地址的日志口径：剥离 userinfo 段防凭证误记（token 只走请求头，本不出现在地址里）。</summary>
    private static string DescribeUpstream(string upstream) =>
        Uri.TryCreate(upstream.Trim(), UriKind.Absolute, out var uri) ? DescribeUri(uri) : upstream.Trim();

    private static string DescribeUri(Uri uri) =>
        new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString();
}
