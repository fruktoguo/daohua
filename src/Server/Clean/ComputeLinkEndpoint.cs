namespace Server.Clean;

/// <summary>
/// /internal/compute-link 端点：计算节点反向拨入的 WebSocket 握手与收发循环。
/// 流程：升级校验 → 节点数上限 → accept → 首帧 hello（解析节点名 → X-Compute-Token
/// 按 ComputeNode:Tokens 映射/共享令牌固定时间比对 → 契约版本 + 配表指纹校验）→
/// 注册节点 → 发送循环（Outbound 队列）与接收循环（result 帧路由回注册表）→
/// 断开即注销并在飞批次全部失败。
/// 凭证裁决必须在 hello 之后：节点名只随 hello 帧到达，命中 ComputeNode:Tokens 映射的
/// 节点只认其映射令牌（共享令牌失效），未命中节点回退共享令牌。
/// </summary>
public static class ComputeLinkEndpoint
{
    /// <summary>握手令牌长度上限：与节点侧 TokenValidator.MaxTokenLength 对齐的防御边界。</summary>
    private const int MaxTokenLength = 512;

    // 拒绝路径日志各自独立节流：拨入方重连刷量时保留证据但不被同一类拒绝刷满磁盘。
    private static readonly LogThrottle TokenRejectLog = new(TimeSpan.FromSeconds(30));
    private static readonly LogThrottle NodeLimitLog = new(TimeSpan.FromSeconds(30));
    private static readonly LogThrottle FingerprintRejectLog = new(TimeSpan.FromSeconds(30));
    private static readonly LogThrottle HandshakeRejectLog = new(TimeSpan.FromSeconds(30));

    /// <summary>端点主流程。</summary>
    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { code = "WEBSOCKET_REQUIRED", message = "需要 WebSocket 升级。" });
            return;
        }

        var options = context.RequestServices.GetRequiredService<ServerOptions>();
        var registry = context.RequestServices.GetRequiredService<ComputeLinkRegistry>();
        var gameData = context.RequestServices.GetRequiredService<Domain.GameData.GameDataCatalog>();
        var metrics = context.RequestServices.GetService<ComputeLinkMetrics>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ComputeLinkEndpoint");

        if (registry.NodeCount >= ComputeLinkContract.MaxConnectedNodes)
        {
            metrics?.RecordNodeLimitRejected();
            if (NodeLimitLog.TryAcquire())
                logger.LogWarning("compute-link 在线节点数已达上限 {Max}，拒绝拨入 remote={Remote}",
                    ComputeLinkContract.MaxConnectedNodes, context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new { code = "NODE_LIMIT", message = "在线计算节点数已达上限。" });
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var token = linked.Token;
        ComputeLinkNode? node = null;
        try
        {
            // 首帧必须是 hello：凭证、契约版本、节点容量与配表指纹都在这里裁决。
            var first = await ReceiveMessageAsync(socket, token)
                .WaitAsync(ComputeLinkContract.HelloTimeout, token);
            if (first is not ComputeLinkHello hello)
                throw new InvalidDataException("首帧必须是 hello。");
            var name = string.IsNullOrWhiteSpace(hello.NodeName)
                ? $"node-{context.Connection.RemoteIpAddress}"
                : hello.NodeName.Trim();
            if (name.Length > ComputeLinkContract.MaxNodeNameLength)
                name = name[..ComputeLinkContract.MaxNodeNameLength];

            // 凭证裁决先于版本/指纹协商：未授权拨入不泄露任何握手细节。
            // 节点名命中 ComputeNode:Tokens 映射时只认映射令牌，未命中回退共享令牌；
            // 双方各取 SHA256 定长摘要再固定时间比较，消除令牌长度侧信道（令牌本体绝不进日志）。
            var presented = context.Request.Headers[ComputeLinkContract.TokenHeader].ToString();
            var expected = options.ComputeNodeTokens.TryGetValue(name, out var mappedToken)
                ? mappedToken
                : options.ComputeNodeToken ?? string.Empty;
            var tokensMatch = expected.Length is > 0 and <= MaxTokenLength
                && presented.Length is > 0 and <= MaxTokenLength
                && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
            if (!tokensMatch)
            {
                metrics?.RecordTokenRejected();
                if (TokenRejectLog.TryAcquire())
                    logger.LogWarning("compute-link 令牌校验失败，拒绝拨入 name={Name} remote={Remote}",
                        name, context.Connection.RemoteIpAddress);
                await SendCloseAsync(socket, "UNAUTHORIZED", "需要有效的内部计算令牌。", token);
                return;
            }

            if (!string.Equals(hello.ContractVersion, ComputeLinkContract.Version, StringComparison.Ordinal))
                throw new InvalidDataException($"链路契约版本不支持：{hello.ContractVersion}。");
            if (hello.MaxConcurrentBatches is < 1 or > ComputeLinkContract.MaxNodeConcurrency)
                throw new InvalidDataException("节点并发容量无效。");
            // 批次承接能力：0 表示未声明（主服按自身配置装配）；超出契约硬上限的声明无意义，直接拒绝。
            if (hello.MaxJobsPerBatch is < 0 or > ComputeDispatchContract.HardMaxJobsPerBatch)
                throw new InvalidDataException("节点单批任务上限无效。");
            if (hello.MaxBatchBytes is < 0 or > ComputeDispatchContract.MaxBatchBytes)
                throw new InvalidDataException("节点批次字节上限无效。");

            var fingerprintBytes = Encoding.UTF8.GetBytes(hello.GameDataFingerprint ?? string.Empty);
            var expectedFingerprint = Encoding.UTF8.GetBytes(gameData.Fingerprint);
            if (fingerprintBytes.Length != expectedFingerprint.Length
                || !CryptographicOperations.FixedTimeEquals(fingerprintBytes, expectedFingerprint))
            {
                // 指纹是 hello 载荷内字段，长度不受节点名约束，进日志前截断防刷量。
                var actualFingerprint = hello.GameDataFingerprint is { Length: > 128 } longFingerprint
                    ? longFingerprint[..128]
                    : hello.GameDataFingerprint;
                metrics?.RecordFingerprintRejected();
                if (FingerprintRejectLog.TryAcquire())
                    logger.LogWarning(
                        "计算节点配表指纹不匹配，拒绝接入 name={Name} remote={Remote} expected={Expected} actual={Actual}",
                        name, context.Connection.RemoteIpAddress, gameData.Fingerprint, actualFingerprint);
                await SendCloseAsync(socket, "GAME_DATA_FINGERPRINT_MISMATCH",
                    $"配表指纹不匹配，节点需与主服同版本：expected={gameData.Fingerprint}", token);
                return;
            }

            // 运行时指纹仅作诊断展示：超长截断防刷量，绝不参与握手裁决。
            var runtimeFingerprint = hello.RuntimeFingerprint is { Length: > 256 } longRuntime
                ? longRuntime[..256]
                : hello.RuntimeFingerprint;
            node = new ComputeLinkNode(
                name, hello.MaxConcurrentBatches, hello.MaxJobsPerBatch, hello.MaxBatchBytes,
                runtimeFingerprint: runtimeFingerprint);
            if (!registry.TryRegister(node))
            {
                metrics?.RecordNodeLimitRejected();
                if (NodeLimitLog.TryAcquire())
                    logger.LogWarning("计算节点注册被拒绝：在线节点数已达上限 name={Name} remote={Remote}",
                        name, context.Connection.RemoteIpAddress);
                await SendCloseAsync(socket, "NODE_LIMIT", "在线计算节点数已达上限。", token);
                return;
            }
            logger.LogInformation(
                "计算节点接入 node={Node} name={Name} capacity={Capacity} maxJobs={MaxJobs} maxBytes={MaxBytes} runtime={Runtime} remote={Remote}",
                node.NodeId, node.Name, node.Capacity, node.MaxJobsPerBatch, node.MaxBatchBytes,
                node.RuntimeFingerprint ?? "unknown",
                context.Connection.RemoteIpAddress);

            await socket.SendAsync(
                ComputeLinkCodec.Encode(new ComputeLinkWelcome(
                    ComputeLinkContract.Version, node.NodeId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
                WebSocketMessageType.Text, true, token);

            var sendTask = SendLoopAsync(socket, node, token);
            var receiveTask = ReceiveLoopAsync(socket, node, registry, metrics, logger, token);
            await Task.WhenAny(sendTask, receiveTask);
            linked.Cancel();
            try { await Task.WhenAll(sendTask, receiveTask); } catch { /* 断开清理路径忽略残余异常 */ }
        }
        catch (ComputeNodeDisconnectedException)
        {
            // 对端主动关闭/心跳判定断开：属正常链路生命周期，finally 的离线行已含在飞数，
            // 无需 Warning 级异常堆栈——弱网节点闪断不再刷告警。
            logger.LogInformation("计算节点链路被对端关闭 name={Name} remote={Remote}",
                node?.Name ?? "handshake", context.Connection.RemoteIpAddress);
        }
        catch (InvalidDataException exception) when (node is null)
        {
            // 握手期协议违规（hello 缺失/契约版本不符/能力声明非法）：计数 + 节流留证据，
            // 沿用原 LINK_ERROR 关闭帧口径让节点拿到可读拒绝原因。
            metrics?.RecordHandshakeRejected();
            if (HandshakeRejectLog.TryAcquire())
                logger.LogWarning("计算节点握手协议违规，拒绝接入 remote={Remote} error={Error}",
                    context.Connection.RemoteIpAddress, exception.Message);
            try
            {
                await SendCloseAsync(socket, "LINK_ERROR", exception.Message, CancellationToken.None);
            }
            catch { }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "计算节点链路异常中止 remote={Remote}", context.Connection.RemoteIpAddress);
            try
            {
                await SendCloseAsync(socket, "LINK_ERROR", exception.Message, CancellationToken.None);
            }
            catch { }
        }
        finally
        {
            if (node is not null)
            {
                registry.Unregister(node);
                logger.LogInformation("计算节点离线 node={Node} name={Name} inFlight={InFlight}",
                    node.NodeId, node.Name, node.InFlight);
            }
        }
    }

    /// <summary>接收循环：result 帧路由回节点在飞表；draining 帧标记排空停派；迟到/未知结果丢弃不断链，其余帧类型判协议违规断开。</summary>
    private static async Task ReceiveLoopAsync(
        WebSocket socket, ComputeLinkNode node, ComputeLinkRegistry registry, ComputeLinkMetrics? metrics, ILogger logger, CancellationToken token)
    {
        // 迟到/未知结果丢弃日志按链路节流：弱网节点重放旧结果时留证据但不刷盘。
        var lateResultLog = new LogThrottle(TimeSpan.FromSeconds(30));
        while (!token.IsCancellationRequested)
        {
            var message = await ReceiveMessageAsync(socket, token);
            switch (message)
            {
                case ComputeLinkResult result:
                    // 批次多已被主服按超时/断线退回（迟到结果无害，写回有 CAS 兜底）。
                    // 若为单条迟到帧断开整条链路，该节点其他在飞批次会一并陪葬。
                    if (result.Response is null || !node.CompleteResult(result.BatchId, result.Response))
                    {
                        metrics?.RecordLateResultDropped();
                        if (lateResultLog.TryAcquire())
                            logger.LogWarning(
                                "丢弃迟到或未知的批次结果 node={Node} name={Name} batch={BatchId}",
                                node.NodeId, node.Name, result.BatchId);
                        break;
                    }
                    break;
                case ComputeLinkDraining draining:
                    // 节点主动排空（停机/重部署）：立即停派新批次；在飞批次结果照常经在飞表回收，
                    // 节点排空完成后自行断链，注销仍走统一断线路径。
                    if (!node.Draining)
                        logger.LogInformation("计算节点进入排空 node={Node} name={Name} reason={Reason}",
                            node.NodeId, node.Name, draining.Reason ?? "未说明");
                    registry.BeginDrain(node);
                    break;
                case ComputeLinkHello:
                    throw new InvalidDataException("重复 hello。");
                default:
                    throw new InvalidDataException($"链路不接受该帧类型：{message.GetType().Name}。");
            }
        }
    }

    /// <summary>发送循环：消费节点 Outbound 队列并写入 socket。</summary>
    private static async Task SendLoopAsync(WebSocket socket, ComputeLinkNode node, CancellationToken token)
    {
        await foreach (var frame in node.Outbound.Reader.ReadAllAsync(token))
            await socket.SendAsync(frame, WebSocketMessageType.Text, true, token);
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
                throw new ComputeNodeDisconnectedException("peer");
            if (received.MessageType == WebSocketMessageType.Binary)
                throw new InvalidDataException("链路只接受文本帧。");
            if (buffer.WrittenCount + received.Count > ComputeLinkContract.MaxMessageBytes)
                throw new InvalidDataException("链路帧超过大小上限。");
            buffer.Write(scratch.AsSpan(0, received.Count));
            if (received.EndOfMessage) break;
        }
        return ComputeLinkCodec.Decode(buffer.WrittenMemory);
    }

    /// <summary>先送 error 帧再发关闭帧，让节点拿到拒绝原因。</summary>
    private static async Task SendCloseAsync(WebSocket socket, string code, string message, CancellationToken token)
    {
        try
        {
            await socket.SendAsync(
                ComputeLinkCodec.Encode(new ComputeLinkError(code, message)),
                WebSocketMessageType.Text, true, token);
        }
        catch { }
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, code, CancellationToken.None);
        }
        catch { }
    }
}
