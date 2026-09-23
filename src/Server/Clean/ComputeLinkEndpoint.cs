namespace Server.Clean;

/// <summary>
/// /internal/compute-link 端点：计算节点反向拨入的 WebSocket 握手与收发循环。
/// 流程：升级校验 → X-Compute-Token 固定时间比对 → accept → 首帧 hello
/// （契约版本 + 配表指纹校验）→ 注册节点 → 发送循环（Outbound 队列）与
/// 接收循环（result 帧路由回注册表）→ 断开即注销并在飞批次全部失败。
/// </summary>
public static class ComputeLinkEndpoint
{
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
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ComputeLinkEndpoint");

        var presented = context.Request.Headers[ComputeLinkContract.TokenHeader].ToString();
        var expected = options.ComputeNodeToken ?? string.Empty;
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        if (expectedBytes.Length == 0
            || presentedBytes.Length != expectedBytes.Length
            || !CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { code = "UNAUTHORIZED", message = "需要有效的内部计算令牌。" });
            return;
        }

        if (registry.NodeCount >= ComputeLinkContract.MaxConnectedNodes)
        {
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
            // 首帧必须是 hello：契约版本、节点容量与配表指纹都在这里裁决。
            var first = await ReceiveMessageAsync(socket, token)
                .WaitAsync(ComputeLinkContract.HelloTimeout, token);
            if (first is not ComputeLinkHello hello)
                throw new InvalidDataException("首帧必须是 hello。");
            if (!string.Equals(hello.ContractVersion, ComputeLinkContract.Version, StringComparison.Ordinal))
                throw new InvalidDataException($"链路契约版本不支持：{hello.ContractVersion}。");
            if (hello.MaxConcurrentBatches is < 1 or > ComputeLinkContract.MaxNodeConcurrency)
                throw new InvalidDataException("节点并发容量无效。");
            var fingerprintBytes = Encoding.UTF8.GetBytes(hello.GameDataFingerprint ?? string.Empty);
            var expectedFingerprint = Encoding.UTF8.GetBytes(gameData.Fingerprint);
            if (fingerprintBytes.Length != expectedFingerprint.Length
                || !CryptographicOperations.FixedTimeEquals(fingerprintBytes, expectedFingerprint))
            {
                await SendCloseAsync(socket, "GAME_DATA_FINGERPRINT_MISMATCH",
                    $"配表指纹不匹配，节点需与主服同版本：expected={gameData.Fingerprint}", token);
                return;
            }

            var name = string.IsNullOrWhiteSpace(hello.NodeName)
                ? $"node-{context.Connection.RemoteIpAddress}"
                : hello.NodeName.Trim();
            if (name.Length > ComputeLinkContract.MaxNodeNameLength)
                name = name[..ComputeLinkContract.MaxNodeNameLength];
            node = new ComputeLinkNode(name, hello.MaxConcurrentBatches);
            if (!registry.TryRegister(node))
            {
                await SendCloseAsync(socket, "NODE_LIMIT", "在线计算节点数已达上限。", token);
                return;
            }
            logger.LogInformation("计算节点接入 node={Node} name={Name} capacity={Capacity} remote={Remote}",
                node.NodeId, node.Name, node.Capacity, context.Connection.RemoteIpAddress);

            await socket.SendAsync(
                ComputeLinkCodec.Encode(new ComputeLinkWelcome(
                    ComputeLinkContract.Version, node.NodeId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
                WebSocketMessageType.Text, true, token);

            var sendTask = SendLoopAsync(socket, node, token);
            var receiveTask = ReceiveLoopAsync(socket, node, registry, token);
            await Task.WhenAny(sendTask, receiveTask);
            linked.Cancel();
            try { await Task.WhenAll(sendTask, receiveTask); } catch { /* 断开清理路径忽略残余异常 */ }
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

    /// <summary>接收循环：result 帧路由回节点在飞表；其余帧类型一律判协议违规断开。</summary>
    private static async Task ReceiveLoopAsync(
        WebSocket socket, ComputeLinkNode node, ComputeLinkRegistry registry, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var message = await ReceiveMessageAsync(socket, token);
            switch (message)
            {
                case ComputeLinkResult result:
                    if (result.Response is null || !node.CompleteResult(result.BatchId, result.Response))
                        throw new InvalidDataException($"未知或已了结的批次结果：{result.BatchId}。");
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
