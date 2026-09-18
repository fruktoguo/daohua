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
    private readonly TaskCompletionSource _teardownCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _outboundSequence;
    private IReadOnlyList<AssetBalanceDto>? _lastPushedAssets;
    private Dictionary<string, string>? _lastPushedSectionChecksums;

    /// <summary>会话绑定的修仙者角色 ID</summary>
    public Guid PlayerId { get; } = playerId;
    /// <summary>下行消息输出通道读取端</summary>
    public ChannelReader<byte[]> Outbound => _outbound.Reader;
    /// <summary>
    /// 会话被服务端主动终止时应使用的 WebSocket 关闭码与原因；
    /// 由防多开准入模块在踢出/拒绝路径上设置（先写字段再 Complete 通道，发送循环完成即建立可见性）。
    /// </summary>
    public (int CloseCode, string Reason)? CloseHint { get; private set; }
    /// <summary>准入时登记的设备指纹；未启用设备配额或未通过准入时为 null</summary>
    public string? DeviceFingerprint { get; set; }
    /// <summary>会话拆除（含事件退订、注册表释放与必要的 Actor 卸载）全部完成后置位</summary>
    public Task TeardownCompleted => _teardownCompleted.Task;
    /// <summary>获取并递增下一个下行信封帧序列号</summary>
    public long NextOutboundSequence() => Interlocked.Increment(ref _outboundSequence);
    /// <summary>向输出通道异步投递待发送的数据包（满时等待背压）</summary>
    public ValueTask SendAsync(byte[] packet, CancellationToken cancellationToken) => _outbound.Writer.WriteAsync(packet, cancellationToken);
    /// <summary>尝试非阻塞投递数据包</summary>
    public bool TrySend(byte[] packet) => _outbound.Writer.TryWrite(packet);
    /// <summary>标记输出通道完成并关闭</summary>
    public void Complete() => _outbound.Writer.TryComplete();

    /// <summary>
    /// 请求服务端主动终止本会话：可选地先把一个通知帧排入发送队列，再完成输出通道，
    /// 由所属 HandleAsync 在收尾阶段按 CloseHint 发送 WebSocket 关闭帧。可重复调用，幂等。
    /// </summary>
    /// <param name="closeCode">自定义关闭码（4000-4999 私有段）</param>
    /// <param name="reason">关闭原因</param>
    /// <param name="noticePacket">可选的下行通知帧（如 Error 包），在关闭前随队列余量一并冲刷</param>
    public void RequestClose(int closeCode, string reason, byte[]? noticePacket = null)
    {
        if (noticePacket is not null) TrySend(noticePacket);
        CloseHint = (closeCode, reason);
        Complete();
    }

    /// <summary>标记会话拆除全部完成，唤醒正在等待顶替他位的后续会话</summary>
    public void MarkTeardownCompleted() => _teardownCompleted.TrySetResult();

    /// <summary>记录本次推送的资产并返回上次基线；增量回调由模拟循环串行驱动，无需加锁。</summary>
    public IReadOnlyList<AssetBalanceDto>? ExchangeAssetBaseline(IReadOnlyList<AssetBalanceDto> current)
    {
        var previous = _lastPushedAssets;
        _lastPushedAssets = current;
        return previous;
    }

    /// <summary>记录本次推送的分段校验和并返回上次基线；增量回调由模拟循环串行驱动，无需加锁。</summary>
    public IReadOnlyDictionary<string, string>? ExchangeSectionBaseline(IReadOnlyList<PlayerSectionDto> current)
    {
        var previous = _lastPushedSectionChecksums;
        var map = new Dictionary<string, string>(current.Count, StringComparer.Ordinal);
        for (var i = 0; i < current.Count; i++)
        {
            map[current[i].Section] = current[i].Checksum;
        }
        _lastPushedSectionChecksums = map;
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
    /// <summary>当前在线会话数。</summary>
    public int Count => _sessions.Count;
    /// <summary>在线会话玩家 ID 快照（本地运维面板只读视图）。</summary>
    public IReadOnlyList<Guid> SnapshotPlayerIds() => _sessions.Keys.ToArray();
    /// <summary>
    /// 互斥语义登记：玩家无在线会话时登记成功；已有在线会话时返回 false，调用方负责拒绝新连接。
    /// </summary>
    /// <param name="session">待登记的新会话。</param>
    public bool TryAdd(WebSocketSession session) => _sessions.TryAdd(session.PlayerId, session);

    /// <summary>
    /// 互踢语义登记：原子地把会话注册为指定玩家的当前会话；
    /// 若已有旧会话则完成替换并通过 <paramref name="kicked"/> 输出，由调用方负责通知与终止它。
    /// </summary>
    /// <param name="session">要登记的新会话</param>
    /// <param name="kicked">被顶替下来的旧会话；不存在时为 null</param>
    /// <returns>登记始终成功，返回 true</returns>
    public bool AddOrKick(WebSocketSession session, out WebSocketSession? kicked)
    {
        while (true)
        {
            if (_sessions.TryGetValue(session.PlayerId, out var existing))
            {
                if (ReferenceEquals(existing, session))
                {
                    kicked = null;
                    return true;
                }
                if (_sessions.TryUpdate(session.PlayerId, session, existing))
                {
                    kicked = existing;
                    return true;
                }
                continue;
            }
            if (_sessions.TryAdd(session.PlayerId, session))
            {
                kicked = null;
                return true;
            }
        }
    }

    /// <summary>
    /// 按实例配对移除会话：只有登记的仍是同一个会话实例时才真正移除，
    /// 被顶替的旧会话拆除时不会误摘新会话的注册位。
    /// </summary>
    /// <param name="playerId">玩家唯一标识。</param>
    /// <param name="session">期望移除的会话实例，按引用与注册表中的当前会话比对。</param>
    /// <returns>该会话仍为注册表中的当前会话且已移除时返回 true</returns>
    public bool Remove(Guid playerId, WebSocketSession session)
    {
        var removed = ((ICollection<KeyValuePair<Guid, WebSocketSession>>)_sessions).Remove(new(playerId, session));
        session.Complete();
        return removed;
    }
}

/// <summary>
/// WebSocket 通信端点处理器。
/// 负责协议升级、握手认证、收发双工管道与异常恢复。
/// </summary>
public static class WebSocketEndpoint
{
    /// <summary>
    /// /ws 端点主流程：升级与鉴权校验 → accept socket → 订阅快照/战报事件 →
    /// 收发双循环（Hello 握手内完成防多开准入裁决）→ 按 CloseHint 发关闭帧 →
    /// 释放准入登记并卸载玩家 Actor。
    /// </summary>
    /// <param name="context">HTTP 请求上下文。</param>
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
        var admission = context.RequestServices.GetRequiredService<AntiMultiOpen.AntiMultiOpenAdmission>();
        var runtime = context.RequestServices.GetRequiredService<PlayerRuntime>();
        var gameData = context.RequestServices.GetRequiredService<Domain.GameData.GameDataCatalog>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebSocketEndpoint");
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var session = new WebSocketSession(playerId, options.WebSocketOutboundCapacity);
        // 会话准入（单账号互踢/拒绝 + 设备配额）推迟到 Hello 处理时统一裁决：
        // 设备指纹只随 Hello 载荷到达，accept 阶段缺少裁决所需的全部输入。

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var token = linked.Token;
        ValueTask OnSnapshotUpdated(PlayerRuntimeSnapshot snapshot)
        {
            if (snapshot.PlayerId != session.PlayerId) return ValueTask.CompletedTask;
            return QueueJsonAsync(
                session,
                WireMessageType.SnapshotDelta,
                Guid.NewGuid(),
                BuildDelta(session, snapshot, gameData),
                token);
        }
        ValueTask OnLogBatchEmitted(Guid eventPlayerId, IReadOnlyList<LogEventDto> events) => eventPlayerId != session.PlayerId
            ? ValueTask.CompletedTask
            : QueueJsonAsync(session, WireMessageType.LogBatch, Guid.NewGuid(), new LogBatchDto(events), token);
        runtime.SnapshotUpdated += OnSnapshotUpdated;
        runtime.LogBatchEmitted += OnLogBatchEmitted;
        try
        {
            var sendTask = SendLoopAsync(socket, session, token);
            var receiveTask = ReceiveLoopAsync(socket, session, runtime, gameData, admission, options, token, logger);
            await Task.WhenAny(sendTask, receiveTask);
            // 服务端主动终止路径（被顶替/准入拒绝）：先冲刷通知帧再发关闭帧。
            // 顺序硬约束：取消令牌会中止阻塞中的 ReceiveAsync 并把 socket 置为 Aborted，
            // 此后任何 CloseAsync 都无效，客户端只能收到 1006——所以关闭帧必须先于 linked.Cancel()。
            if (session.CloseHint is { } hint)
            {
                try { await sendTask.WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { }
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try { await socket.CloseAsync((WebSocketCloseStatus)hint.CloseCode, hint.Reason, CancellationToken.None); } catch { socket.Abort(); }
                }
            }
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
            // 会话释放按实例配对：被顶替的旧会话返回 false，不得触碰新会话已加载的 Actor。
            var owned = admission.Release(session);
            // 兜底关闭：主流程未发出关闭帧时（如发送循环异常）仍尝试按 CloseHint 收尾。
            if (session.CloseHint is { } closeHint && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseAsync((WebSocketCloseStatus)closeHint.CloseCode, closeHint.Reason, CancellationToken.None); } catch { socket.Abort(); }
            }
            // 关闭连接时使用独立取消令牌：请求已断开并不应中断权威状态刷盘。
            // 若数据库暂时不可用，RemoveAsync 保留 actor 与脏标记，后台 flush worker 会继续重试。
            if (owned) await runtime.RemoveAsync(playerId, true, CancellationToken.None);
            session.MarkTeardownCompleted();
        }
    }

    /// <summary>
    /// 构建增量：以会话上次推送的分段与资产为基线对比，仅携带发生变化的分段与资产品种。
    /// 同时携带权威模拟时刻 LastSimulatedAt，供浏览器在两次全量快照之间校准服务端时钟。
    /// </summary>
    private static PlayerSnapshotDeltaDto BuildDelta(WebSocketSession session, PlayerRuntimeSnapshot snapshot, Domain.GameData.GameDataCatalog gameData)
    {
        var current = ProtocolSnapshotMapper.ToSnapshot(snapshot, gameData.Fingerprint, gameData);
        var changedSections = ProtocolSnapshotMapper.DiffSections(session.ExchangeSectionBaseline(current.Sections), current.Sections);
        var changedAssets = ProtocolSnapshotMapper.DiffAssets(session.ExchangeAssetBaseline(current.Assets), current.Assets);
        return new PlayerSnapshotDeltaDto(current.PlayerId, current.StateVersion, current.LastSimulatedAt, changedSections, changedAssets.Count == 0 ? null : changedAssets);
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
        AntiMultiOpen.AntiMultiOpenAdmission admission,
        ServerOptions options,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(options.MaxWebSocketMessageBytes, 64 * 1024));
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
                    if (stream.Length + result.Count > options.MaxWebSocketMessageBytes) throw new WebSocketException("消息超过大小限制。");
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
                        // 防多开准入裁决：配额先于顶替，拒绝时通知帧随关闭码一并下发。
                        var admit = admission.Admit(session, hello.DeviceFingerprint);
                        if (!admit.Accepted)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId,
                                new ErrorDto(admit.ErrorCode!, admit.Message ?? "连接被拒绝。", envelope.RequestId.ToString("D")), cancellationToken);
                            session.RequestClose(admit.CloseCode!.Value, admit.Message ?? string.Empty);
                            return;
                        }
                        if (admit.KickedSession is { } kicked)
                        {
                            var kickNotice = EncodeJson(WireMessageType.Error, Guid.NewGuid(), kicked.NextOutboundSequence(),
                                new ErrorDto(AntiMultiOpen.AntiMultiOpenConstants.SessionKickedErrorCode, "账号已在其他窗口或设备登录，当前连接已被替换。", null));
                            kicked.RequestClose(AntiMultiOpen.AntiMultiOpenConstants.SessionKickedCloseCode, "Kicked by a newer session.", kickNotice);
                            // 等待旧会话完成拆除再加载 Actor：杜绝新旧会话并发驱动同一聚合、
                            // 以及旧会话 finally 误删新加载 Actor 的竞态。
                            try { await kicked.TeardownCompleted.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
                            catch (TimeoutException) { logger.LogWarning("被顶替会话拆除超时，继续准入 playerId={PlayerId}", session.PlayerId); }
                        }
                        var serverVersion = typeof(WebSocketEndpoint).Assembly
                            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                            .FirstOrDefault()?.InformationalVersion ?? "unknown";
                        var helloAckSequence = session.NextOutboundSequence();
                        await session.SendAsync(EncodeJson(WireMessageType.HelloAck, envelope.RequestId, helloAckSequence,
                            new HelloResponse(serverVersion, ProtocolConstants.CurrentVersion, gameData.Fingerprint, helloAckSequence,
                                new AntiMultiOpenConfigDto(options.AntiMultiOpen.TabGuardEnabled))), cancellationToken);
                        var initial = await runtime.LoadAsync(session.PlayerId, cancellationToken);
                        var initialSnapshot = ProtocolSnapshotMapper.ToSnapshot(initial, gameData.Fingerprint, gameData);
                        await QueueJsonAsync(session, WireMessageType.Snapshot, Guid.NewGuid(), initialSnapshot, cancellationToken);
                        session.ExchangeAssetBaseline(initialSnapshot.Assets);
                        session.ExchangeSectionBaseline(initialSnapshot.Sections);
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
