using Protocol;

namespace Server.Clean;

/// <summary>
/// 单个客户端 WebSocket 连接会话封装。
/// 核心机制：
/// 1. 使用有界通道（BoundedChannel）管理下行数据包，避免网络慢客户端造成服务端内存无限膨胀；
/// 2. 线程安全递增下行 Sequence 帧序号；
/// 3. 维护上次推送的资产余额基线，用于增量计算（DiffAssets）。
/// </summary>
public sealed class WebSocketSession(Guid playerId, int capacity)
{
    private readonly Channel<byte[]> _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private long _outboundSequence;
    private IReadOnlyList<AssetBalanceDto>? _lastPushedAssets;

    /// <summary>会话绑定的修仙者角色 ID</summary>
    public Guid PlayerId { get; } = playerId;
    /// <summary>下行消息输出通道读取端</summary>
    public ChannelReader<byte[]> Outbound => _outbound.Reader;
    /// <summary>获取并递增下一个下行信封帧序列号</summary>
    public long NextOutboundSequence() => Interlocked.Increment(ref _outboundSequence);
    /// <summary>向输出通道异步投递待发送的数据包（满时等待背压）</summary>
    public ValueTask SendAsync(byte[] packet, CancellationToken cancellationToken) => _outbound.Writer.WriteAsync(packet, cancellationToken);
    /// <summary>尝试非阻塞投递数据包</summary>
    public bool TrySend(byte[] packet) => _outbound.Writer.TryWrite(packet);
    /// <summary>标记输出通道完成并关闭</summary>
    public void Complete() => _outbound.Writer.TryComplete();

    /// <summary>记录本次推送的资产并返回上次基线；增量回调由模拟循环串行驱动，无需加锁。</summary>
    public IReadOnlyList<AssetBalanceDto>? ExchangeAssetBaseline(IReadOnlyList<AssetBalanceDto> current)
    {
        var previous = _lastPushedAssets;
        _lastPushedAssets = current;
        return previous;
    }
}

/// <summary>
/// 在线 WebSocket 会话注册表。
/// 保证同一玩家角色同时只能存在一个活跃连接，后到连接将排他拒绝或踢出。
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<Guid, WebSocketSession> _sessions = new();
    public int Count => _sessions.Count;
    public bool TryAdd(WebSocketSession session) => _sessions.TryAdd(session.PlayerId, session);
    public void Remove(Guid playerId, WebSocketSession session)
    {
        ((ICollection<KeyValuePair<Guid, WebSocketSession>>)_sessions).Remove(new(playerId, session));
        session.Complete();
    }
}

/// <summary>
/// WebSocket 通信端点处理器。
/// 负责协议升级、握手认证、收发双工管道与异常恢复。
/// </summary>
public static class WebSocketEndpoint
{
    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { code = "WEBSOCKET_REQUIRED", message = "需要 WebSocket 升级。" });
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true || !Guid.TryParse(context.User.FindFirstValue("player_id"), out var playerId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var options = context.RequestServices.GetRequiredService<ServerOptions>();
        var registry = context.RequestServices.GetRequiredService<SessionRegistry>();
        var runtime = context.RequestServices.GetRequiredService<PlayerRuntime>();
        var gameData = context.RequestServices.GetRequiredService<Domain.GameData.GameDataCatalog>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebSocketEndpoint");
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var session = new WebSocketSession(playerId, options.WebSocketOutboundCapacity);
        if (!registry.TryAdd(session))
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "玩家已有连接。", context.RequestAborted);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var token = linked.Token;
        ValueTask OnSnapshotUpdated(PlayerRuntimeSnapshot snapshot) => QueueJsonAsync(
            session,
            WireMessageType.SnapshotDelta,
            Guid.NewGuid(),
            BuildDelta(session, snapshot, gameData),
            token);
        ValueTask OnLogBatchEmitted(Guid eventPlayerId, IReadOnlyList<LogEventDto> events) => eventPlayerId != session.PlayerId
            ? ValueTask.CompletedTask
            : QueueJsonAsync(session, WireMessageType.LogBatch, Guid.NewGuid(), new LogBatchDto(events), token);
        runtime.SnapshotUpdated += OnSnapshotUpdated;
        runtime.LogBatchEmitted += OnLogBatchEmitted;
        try
        {
            var sendTask = SendLoopAsync(socket, session, token);
            var receiveTask = ReceiveLoopAsync(socket, session, runtime, gameData, token, options.MaxWebSocketMessageBytes, logger);
            await Task.WhenAny(sendTask, receiveTask);
            linked.Cancel();
            try { await Task.WhenAll(sendTask, receiveTask); } catch (OperationCanceledException) { }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "WebSocket 会话异常 playerId={PlayerId}", playerId);
            if (socket.State == WebSocketState.Open)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.InternalServerError, "服务器错误。", CancellationToken.None); } catch { socket.Abort(); }
            }
        }
        finally
        {
            runtime.SnapshotUpdated -= OnSnapshotUpdated;
            runtime.LogBatchEmitted -= OnLogBatchEmitted;
            registry.Remove(playerId, session);
            // 关闭连接时使用独立取消令牌：请求已断开并不应中断权威状态刷盘。
            // 若数据库暂时不可用，RemoveAsync 保留 actor 与脏标记，后台 flush worker 会继续重试。
            await runtime.RemoveAsync(playerId, true, CancellationToken.None);
        }
    }

    /// <summary>构建增量：以会话上次推送的资产为基线对比，携带余额发生变化的品种。</summary>
    private static PlayerSnapshotDeltaDto BuildDelta(WebSocketSession session, PlayerRuntimeSnapshot snapshot, Domain.GameData.GameDataCatalog gameData)
    {
        var current = ProtocolSnapshotMapper.ToSnapshot(snapshot, gameData.Fingerprint, gameData);
        var changedAssets = ProtocolSnapshotMapper.DiffAssets(session.ExchangeAssetBaseline(current.Assets), current.Assets);
        return new PlayerSnapshotDeltaDto(current.PlayerId, current.StateVersion, current.Sections, changedAssets.Count == 0 ? null : changedAssets);
    }

    private static byte[] EncodeJson<T>(WireMessageType messageType, Guid requestId, long sequence, T payload) =>
        WireEnvelopeCodec.EncodeJson(messageType, requestId, Math.Max(0, sequence), payload);

    private static async Task SendLoopAsync(WebSocket socket, WebSocketSession session, CancellationToken cancellationToken)
    {
        await foreach (var packet in session.Outbound.ReadAllAsync(cancellationToken))
            await socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);
    }

    private static async Task ReceiveLoopAsync(
        WebSocket socket,
        WebSocketSession session,
        PlayerRuntime runtime,
        Domain.GameData.GameDataCatalog gameData,
        CancellationToken cancellationToken,
        int maxMessageBytes,
        ILogger logger)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maxMessageBytes, 64 * 1024));
        long lastInboundSequence = 0;
        var handshaken = false;
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Binary) throw new WebSocketException("只接受二进制协议帧。");
                    if (stream.Length + result.Count > maxMessageBytes) throw new WebSocketException("消息超过大小限制。");
                    stream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (!WireEnvelopeCodec.TryDecode(stream.ToArray(), out var envelope))
                {
                    await QueueJsonAsync(session, WireMessageType.Error, Guid.NewGuid(), new ErrorDto("INVALID_PACKET", "无法解析协议帧。"), cancellationToken);
                    continue;
                }
                if (envelope.ProtocolVersion != ProtocolConstants.CurrentVersion || envelope.Sequence <= lastInboundSequence)
                {
                    await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("INVALID_SEQUENCE", "协议版本或消息序号无效。"), cancellationToken);
                    continue;
                }
                lastInboundSequence = envelope.Sequence;

                switch (envelope.MessageType)
                {
                    case WireMessageType.Hello:
                        if (handshaken)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("HELLO_ALREADY_COMPLETED", "当前连接已完成握手。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        if (!WireEnvelopeCodec.TryDeserializeJson<HelloRequest>(envelope, out var hello) || hello is null)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("INVALID_HELLO", "握手载荷无效。"), cancellationToken);
                            continue;
                        }
                        if (!string.IsNullOrWhiteSpace(hello.GameDataFingerprint)
                            && !string.Equals(hello.GameDataFingerprint, gameData.Fingerprint, StringComparison.Ordinal))
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("GAME_DATA_MISMATCH", "客户端游戏数据版本不匹配。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        var serverVersion = typeof(WebSocketEndpoint).Assembly
                            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                            .FirstOrDefault()?.InformationalVersion ?? "unknown";
                        var helloAckSequence = session.NextOutboundSequence();
                        await session.SendAsync(EncodeJson(WireMessageType.HelloAck, envelope.RequestId, helloAckSequence,
                            new HelloResponse(serverVersion, ProtocolConstants.CurrentVersion, gameData.Fingerprint, helloAckSequence)), cancellationToken);
                        var initial = await runtime.LoadAsync(session.PlayerId, cancellationToken);
                        var initialSnapshot = ProtocolSnapshotMapper.ToSnapshot(initial, gameData.Fingerprint, gameData);
                        await QueueJsonAsync(session, WireMessageType.Snapshot, Guid.NewGuid(), initialSnapshot, cancellationToken);
                        session.ExchangeAssetBaseline(initialSnapshot.Assets);
                        // 重连回放：把环形缓冲内的最近战报一次性补发给本会话。
                        var recentLogs = runtime.GetRecentLogEvents(session.PlayerId);
                        if (recentLogs.Count > 0)
                            await QueueJsonAsync(session, WireMessageType.LogBatch, Guid.NewGuid(), new LogBatchDto(recentLogs), cancellationToken);
                        handshaken = true;
                        break;
                    case WireMessageType.Ping:
                        if (!handshaken)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("HANDSHAKE_REQUIRED", "请先完成 hello 握手。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        await QueueJsonAsync(session, WireMessageType.Pong, envelope.RequestId, new { serverTime = DateTimeOffset.UtcNow }, cancellationToken);
                        break;
                    case WireMessageType.ActionIntent:
                        if (!handshaken)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("HANDSHAKE_REQUIRED", "请先完成 hello 握手。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        if (!WireEnvelopeCodec.TryDeserializeJson<ActionIntentRequest>(envelope, out var action) || action is null)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("INVALID_ACTION", "行动载荷无效。"), cancellationToken);
                            continue;
                        }
                        if (!string.Equals(action.RequestId, envelope.RequestId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("REQUEST_ID_MISMATCH", "行动请求 ID 与协议信封不一致。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        var actionPayload = JsonSerializer.SerializeToElement(action, ProtocolJson.Options);
                        PlayerCommandResult actionResult;
                        try
                        {
                            actionResult = await runtime.ExecuteAsync(session.PlayerId, "action.enqueue", actionPayload, 0, Guid.Parse(action.RequestId), cancellationToken);
                        }
                        catch (ObjectDisposedException)
                        {
                            // actor 已随旧会话销毁：返回明确错误，后续命令会触发重新加载。
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("SESSION_CLOSED", "玩家会话已关闭，请重新连接。"), cancellationToken);
                            continue;
                        }
                        var actionResponse = new ActionIntentResponse(
                            action.RequestId,
                            actionResult.Snapshot.State.Actions.LastOrDefault()?.Id.ToString("D") ?? string.Empty,
                            action.ActionId,
                            DateTimeOffset.UtcNow,
                            actionResult.Snapshot.State.Actions.FirstOrDefault()?.RemainingRepeats ?? action.RepeatCount);
                        await QueueJsonAsync(session, WireMessageType.ActionResult, envelope.RequestId, actionResponse, cancellationToken);
                        break;
                    case WireMessageType.Command:
                        if (!handshaken)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("HANDSHAKE_REQUIRED", "请先完成 hello 握手。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        if (!WireEnvelopeCodec.TryDeserializeJson<CommandEnvelopeDto>(envelope, out var commandEnvelope) || commandEnvelope is null)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("INVALID_COMMAND", "通用命令载荷无效。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        if (!Guid.TryParse(commandEnvelope.RequestId, out var commandRequestId)
                            || commandRequestId != envelope.RequestId
                            || string.IsNullOrWhiteSpace(commandEnvelope.Command)
                            || commandEnvelope.ExpectedStateVersion < 0)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("INVALID_COMMAND", "通用命令标识、名称或期望版本无效。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        PlayerCommandResult commandResult;
                        try
                        {
                            commandResult = await runtime.ExecuteAsync(session.PlayerId, commandEnvelope.Command, commandEnvelope.Payload, commandEnvelope.ExpectedStateVersion, commandRequestId, cancellationToken);
                        }
                        catch (ObjectDisposedException)
                        {
                            // actor 已随旧会话销毁：返回明确错误，后续命令会触发重新加载。
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("SESSION_CLOSED", "玩家会话已关闭，请重新连接。"), cancellationToken);
                            continue;
                        }
                        var commandDto = new CommandResultDto(
                            commandEnvelope.RequestId,
                            commandResult.Success,
                            commandResult.Code,
                            commandResult.Message,
                            commandResult.Snapshot.StateVersion,
                            JsonSerializer.SerializeToElement(ProtocolSnapshotMapper.ToSnapshot(commandResult.Snapshot, gameData.Fingerprint, gameData), ProtocolJson.Options));
                        await QueueJsonAsync(session, WireMessageType.CommandResult, envelope.RequestId, commandDto, cancellationToken);
                        break;
                    default:
                        await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("UNSUPPORTED_MESSAGE", "不支持的消息类型。"), cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WebSocketException ex) { logger.LogDebug(ex, "WebSocket disconnected playerId={PlayerId}", session.PlayerId); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static ValueTask QueueJsonAsync<T>(
        WebSocketSession session,
        WireMessageType messageType,
        Guid requestId,
        T payload,
        CancellationToken cancellationToken)
    {
        var packet = EncodeJson(messageType, requestId, session.NextOutboundSequence(), payload);
        // Snapshot 增量与战报日志属于可丢弃的实时推送，慢客户端不能反向阻塞玩家模拟/持久化。
        // 队列已满时丢弃该帧，客户端可通过下一次命令响应或重连重新获取完整快照与最近日志；
        // 命令结果、错误和握手响应仍保留等待语义，确保关键协议不静默丢失。
        if (messageType is WireMessageType.SnapshotDelta or WireMessageType.LogBatch)
        {
            session.TrySend(packet);
            return ValueTask.CompletedTask;
        }
        return session.SendAsync(packet, cancellationToken);
    }
}
