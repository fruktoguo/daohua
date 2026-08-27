using Protocol;

namespace Server.Clean;

public sealed class WebSocketSession(Guid playerId, int capacity)
{
    private readonly Channel<byte[]> _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private long _outboundSequence;

    public Guid PlayerId { get; } = playerId;
    public ChannelReader<byte[]> Outbound => _outbound.Reader;
    public long NextOutboundSequence() => Interlocked.Increment(ref _outboundSequence);
    public ValueTask SendAsync(byte[] packet, CancellationToken cancellationToken) => _outbound.Writer.WriteAsync(packet, cancellationToken);
    public void Complete() => _outbound.Writer.TryComplete();
}

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
            registry.Remove(playerId, session);
            await runtime.RemoveAsync(playerId, true, CancellationToken.None);
        }
    }

    private static byte[] EncodeJson<T>(WireMessageType messageType, Guid requestId, long sequence, T payload) =>
        WireEnvelopeCodec.Encode(WireEnvelope.Json(messageType, requestId, Math.Max(0, sequence), payload));

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
                        await QueueJsonAsync(session, WireMessageType.Snapshot, Guid.NewGuid(), ProtocolSnapshotMapper.ToSnapshot(initial, gameData.Fingerprint), cancellationToken);
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
                        var actionResult = await runtime.ExecuteAsync(session.PlayerId, "action.enqueue", actionPayload, 0, Guid.Parse(action.RequestId), cancellationToken);
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
                        var commandResult = await runtime.ExecuteAsync(session.PlayerId, commandEnvelope.Command, commandEnvelope.Payload, commandEnvelope.ExpectedStateVersion, commandRequestId, cancellationToken);
                        var commandDto = new CommandResultDto(
                            commandEnvelope.RequestId,
                            commandResult.Success,
                            commandResult.Code,
                            commandResult.Message,
                            commandResult.Snapshot.StateVersion,
                            JsonSerializer.SerializeToElement(ProtocolSnapshotMapper.ToSnapshot(commandResult.Snapshot, gameData.Fingerprint), ProtocolJson.Options));
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
        CancellationToken cancellationToken) =>
        session.SendAsync(EncodeJson(messageType, requestId, session.NextOutboundSequence(), payload), cancellationToken);
}
