using Protocol;

namespace Server.Clean;

/// <summary>
/// 单个客户端 WebSocket 连接会话封装。
/// 核心机制：
/// 1. 使用有界通道（BoundedChannel）管理下行数据包，避免网络慢客户端造成服务端内存无限膨胀；
/// 2. 线程安全递增下行 Sequence 帧序号；
/// 3. 维护上次推送的分段校验和/资产余额基线，用于增量计算；基线只在帧真正入队后提交，
///    可丢弃帧被丢弃时基线不前移，客户端不会永久缺失该分段内容；
/// 4. 可丢弃推送持续丢帧达到阈值时按慢消费者主动关闭（4009），由客户端重连拉取全量快照。
/// </summary>
public sealed class WebSocketSession(Guid playerId, int capacity)
{
    /// <summary>慢消费者阈值：连续丢弃帧数上限。有界队列满时实时推送逐帧丢弃，连续饱和视为无法消化。</summary>
    public const int SlowConsumerMaxDrops = 32;
    /// <summary>慢消费者阈值：自首次丢弃起的持续饱和时长上限，与帧数阈值先到先判。</summary>
    internal static readonly TimeSpan SlowConsumerSaturationWindow = TimeSpan.FromSeconds(15);
    /// <summary>慢消费者关闭码：4009 落在自定义私有段且未被防多开占用，前端走默认自动重连恢复全量快照。</summary>
    public const int SlowConsumerCloseCode = 4009;

    private readonly Channel<byte[]> _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly TaskCompletionSource _teardownCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _outboundSequence;
    private IReadOnlyList<AssetBalanceDto>? _lastPushedAssets;
    private IReadOnlyDictionary<string, string>? _lastPushedSectionChecksums;
    private volatile UnitInspectDto? _inspectedUnit;
    /// <summary>观察目标的属性面板尚未随成功入队的帧交付：武装于登记，交付后解除，避免观察期间逐拍重复下发。</summary>
    private volatile bool _inspectDeliveryPending;
    private int _consecutiveDrops;
    private long _firstDropUtcTicks;

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
    /// <summary>客户端经 UnitInspect 火忘帧登记的单位详情观察目标；会话级浏览态，断开即销毁。</summary>
    public UnitInspectDto? InspectedUnit => _inspectedUnit;
    /// <summary>观察目标解析出的敌方阵型下标：快照按需投影的属性面板只对命中下标的敌怪/斗法镜像下发。</summary>
    public int? InspectedEnemyIndex => _inspectedUnit is { Side: "enemy", Index: >= 0 } t ? t.Index : null;
    /// <summary>待交付的观察目标下标：仅登记后属性面板尚未交付期间返回下标；交付后快照不再携带属性面板（前端按 enemyId 缓存复用）。</summary>
    public int? PendingInspectedEnemyIndex => _inspectDeliveryPending ? InspectedEnemyIndex : null;
    /// <summary>
    /// 更新观察目标（null 清除）并重新武装一次性属性交付；写于接收循环、读于推送 OutboundSync，引用字段 volatile 原子可见。
    /// </summary>
    public void SetInspectedUnit(UnitInspectDto? target)
    {
        _inspectedUnit = target;
        _inspectDeliveryPending = target is not null;
    }
    /// <summary>
    /// 属性面板已随入队成功的帧交付则解除武装；若登记目标在编码后已变更则保留武装，避免吞掉新目标的交付。
    /// </summary>
    public void MarkInspectedAttributesDelivered(int? deliveredIndex)
    {
        if (deliveredIndex == InspectedEnemyIndex) _inspectDeliveryPending = false;
    }
    /// <summary>
    /// 下行推送的构建-发送-基线提交串行化锁：增量推送可由模拟循环与斗法撮合等多线程并发触发，
    /// 同一会话的「读基线 → 编码 → 入队 → 提交基线」必须整体串行，否则交错会把未送达内容的
    /// 校验和误记为已推送基线，造成客户端永久漂移。
    /// </summary>
    public object OutboundSync { get; } = new();
    /// <summary>
    /// 获取并递增下一个下行信封帧序列号。
    /// 注意：仅在 <see cref="OutboundSync"/> 内与同锁的入队组合使用时才能保证「序号递增序 = 通道物理序」；
    /// 锁外取号与写入分离会让并发生产者把合法帧倒序入队，被客户端按序号丢弃。
    /// </summary>
    public long NextOutboundSequence() => Interlocked.Increment(ref _outboundSequence);
    /// <summary>向输出通道异步投递待发送的数据包（满时等待背压）</summary>
    public ValueTask SendAsync(byte[] packet, CancellationToken cancellationToken) => _outbound.Writer.WriteAsync(packet, cancellationToken);
    /// <summary>尝试非阻塞投递数据包（不区分可丢弃性，不进入慢消费者统计）</summary>
    public bool TrySend(byte[] packet) => _outbound.Writer.TryWrite(packet);

    /// <summary>
    /// 必达 JSON 帧的有序投递：取号-编码-首次入队尝试在 <see cref="OutboundSync"/> 内整体完成，
    /// 与并发推送的基线临界区互斥，保证通道物理序严格等于序号递增序；
    /// 满队列时退回锁外阻塞发送（必达帧不丢，慢消费者由可丢弃帧与踢线阈值兜底）。
    /// </summary>
    public ValueTask SendJsonAsync<T>(WireMessageType messageType, Guid requestId, T payload, CancellationToken cancellationToken)
    {
        byte[] packet;
        bool enqueued;
        lock (OutboundSync)
        {
            packet = WireEnvelopeCodec.EncodeJson(messageType, requestId, Math.Max(0, NextOutboundSequence()), payload);
            enqueued = TrySend(packet);
        }
        return enqueued ? ValueTask.CompletedTask : SendAsync(packet, cancellationToken);
    }

    /// <summary>
    /// 可丢弃 JSON 帧的有序投递：取号-编码-入队在 <see cref="OutboundSync"/> 内原子完成；
    /// 满队列丢弃并累计慢消费者证据（语义同 <see cref="TrySendDroppable"/>）。
    /// </summary>
    /// <returns>帧已进入发送队列返回 true；被丢弃（或会话已在关闭流程）返回 false。</returns>
    public bool TrySendJsonDroppable<T>(WireMessageType messageType, Guid requestId, T payload)
    {
        lock (OutboundSync)
            return TrySendDroppable(WireEnvelopeCodec.EncodeJson(messageType, requestId, Math.Max(0, NextOutboundSequence()), payload));
    }

    /// <summary>
    /// 共享载荷字节的可丢弃广播帧：多收件人复用同一段已序列化载荷，取号-编码-入队仍在 <see cref="OutboundSync"/> 内原子完成。
    /// </summary>
    public bool TrySendDroppableEnvelope(WireMessageType messageType, Guid requestId, ReadOnlyMemory<byte> payload)
    {
        lock (OutboundSync)
            return TrySendDroppable(WireEnvelopeCodec.Encode(
                WireEnvelope.Create(messageType, requestId, Math.Max(0, NextOutboundSequence()), payload)));
    }

    /// <summary>
    /// 尝试非阻塞投递一帧可丢弃的实时推送：满队列丢弃时累计慢消费者证据，
    /// 连续丢帧或持续饱和达到阈值即按 4009 请求关闭（前端默认重连拉全量）。
    /// </summary>
    /// <returns>帧已进入发送队列返回 true；被丢弃（或会话已在关闭流程）返回 false。</returns>
    public bool TrySendDroppable(byte[] packet)
    {
        if (CloseHint is not null) return false;
        if (_outbound.Writer.TryWrite(packet))
        {
            // 队列恢复可写：清零连续丢弃窗口，慢消费者证据随消化能力恢复失效。
            if (Volatile.Read(ref _consecutiveDrops) != 0)
            {
                Interlocked.Exchange(ref _consecutiveDrops, 0);
                Interlocked.Exchange(ref _firstDropUtcTicks, 0L);
            }
            return true;
        }
        var drops = Interlocked.Increment(ref _consecutiveDrops);
        var nowTicks = DateTimeOffset.UtcNow.Ticks;
        Interlocked.CompareExchange(ref _firstDropUtcTicks, nowTicks, 0L);
        var saturatedTicks = nowTicks - Interlocked.Read(ref _firstDropUtcTicks);
        if (drops >= SlowConsumerMaxDrops || saturatedTicks >= SlowConsumerSaturationWindow.Ticks)
            RequestClose(SlowConsumerCloseCode, "slow consumer");
        return false;
    }
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

    /// <summary>
    /// 读取上次「已确认入队」的推送基线：分段校验和与资产余额成对返回。
    /// 增量构建必须在 <see cref="OutboundSync"/> 内与发送、提交串行完成。
    /// </summary>
    public (IReadOnlyDictionary<string, string>? SectionChecksums, IReadOnlyList<AssetBalanceDto>? Assets) ReadPushBaseline()
    {
        lock (OutboundSync) return (_lastPushedSectionChecksums, _lastPushedAssets);
    }

    /// <summary>
    /// 帧成功入队后提交本次推送基线：分段校验和与资产余额原子成对前移；
    /// 可丢弃帧被丢弃时不得调用，基线保持在上次已送达内容，下一次增量重新携带缺失分段。
    /// </summary>
    public void CommitPushBaseline(IReadOnlyDictionary<string, string> sectionChecksums, IReadOnlyList<AssetBalanceDto> assets)
    {
        lock (OutboundSync)
        {
            _lastPushedSectionChecksums = sectionChecksums;
            _lastPushedAssets = assets;
        }
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
    /// <summary>按玩家 ID 取出当前在线会话；不在线时返回 false（传音广播收件人解析）。</summary>
    /// <param name="playerId">玩家唯一标识。</param>
    /// <param name="session">命中的在线会话。</param>
    /// <returns>命中返回 true。</returns>
    public bool TryGet(Guid playerId, out WebSocketSession session) => _sessions.TryGetValue(playerId, out session!);
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
        var runtime = context.RequestServices.GetRequiredService<IPlayerRuntime>();
        var gameData = context.RequestServices.GetRequiredService<Domain.GameData.GameDataCatalog>();
        var sessions = context.RequestServices.GetRequiredService<SessionRegistry>();
        var chat = context.RequestServices.GetRequiredService<Social.ChatRuntime>();
        var social = context.RequestServices.GetRequiredService<Social.SocialRuntime>();
        var presence = context.RequestServices.GetRequiredService<Social.OnlinePresenceRegistry>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebSocketEndpoint");
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var session = new WebSocketSession(playerId, options.WebSocketOutboundCapacity);
        // 会话准入（单账号互踢/拒绝 + 设备配额）推迟到 Hello 处理时统一裁决：
        // 设备指纹只随 Hello 载荷到达，accept 阶段缺少裁决所需的全部输入。

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var token = linked.Token;
        async ValueTask OnSnapshotUpdated(PlayerRuntimeSnapshot snapshot)
        {
            if (snapshot.PlayerId != session.PlayerId) return;
            // 权威位置随快照推进：换节点会触发同节点修士的附近列表刷新。
            await presence.UpdateNodeAsync(snapshot.PlayerId, snapshot.State.World.NodeId, token);
            // 读基线 → 按触及分段窄化编码 → 入队 → 提交基线，在会话锁内整体串行：
            // 推送可由模拟循环与斗法撮合多线程并发触发，交错会把未送达内容误记为已推送基线。
            lock (session.OutboundSync)
            {
                var (previousSections, previousAssets) = session.ReadPushBaseline();
                // 按需属性为一次性交付：仅「登记后尚未送达」期间投影；本帧实际写入属性且入队成功才解除武装，
                // 被慢消费者丢弃时保留武装由下一拍重投，保证客户端最终拿到面板数据。
                var pendingInspectIndex = session.PendingInspectedEnemyIndex;
                var attributesProjected = false;
                var delta = ProtocolSnapshotMapper.BuildDelta(
                    snapshot, gameData.Fingerprint, gameData, snapshot.TouchedSections,
                    previousSections, previousAssets,
                    out var nextSections, out var nextAssets,
                    pendingInspectIndex, () => attributesProjected = true);
                var packet = EncodeJson(WireMessageType.SnapshotDelta, Guid.NewGuid(), session.NextOutboundSequence(), delta);
                // 可丢弃帧入队成功才前移基线；被丢弃时基线保持上次已送达内容，避免客户端永久漂移。
                if (!session.TrySendDroppable(packet)) return;
                if (attributesProjected) session.MarkInspectedAttributesDelivered(pendingInspectIndex);
                session.CommitPushBaseline(nextSections, nextAssets);
            }
        }
        ValueTask OnLogBatchEmitted(Guid eventPlayerId, IReadOnlyList<LogEventDto> events) => eventPlayerId != session.PlayerId
            ? ValueTask.CompletedTask
            : QueueJsonAsync(session, WireMessageType.LogBatch, Guid.NewGuid(), new LogBatchDto(events), token);
        async ValueTask OnSocialStateChanged(Guid eventPlayerId)
        {
            if (eventPlayerId != session.PlayerId) return;
            var state = await social.BuildStateAsync(session.PlayerId, null, token);
            await QueueJsonAsync(session, WireMessageType.SocialState, Guid.NewGuid(), state, token);
        }
        runtime.SnapshotUpdated += OnSnapshotUpdated;
        runtime.LogBatchEmitted += OnLogBatchEmitted;
        social.StateChanged += OnSocialStateChanged;
        try
        {
            var sendTask = SendLoopAsync(socket, session, token);
            var receiveTask = ReceiveLoopAsync(socket, session, runtime, gameData, admission, options, token, logger, chat, social, presence, sessions);
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
            social.StateChanged -= OnSocialStateChanged;
            // 会话释放按实例配对：被顶替的旧会话返回 false，不得触碰新会话已加载的 Actor。
            var owned = admission.Release(session);
            // 在线存在感以玩家为键：只有仍是当前会话的持有者才能摘除登记，
            // 否则被顶替的旧会话拆除时会把新会话的存在感一起抹掉。
            if (owned)
            {
                try { await presence.UnregisterAsync(playerId, CancellationToken.None); }
                catch (Exception exception) { logger.LogError(exception, "在线存在感注销失败 playerId={PlayerId}", playerId); }
            }
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
        IPlayerRuntime runtime,
        Domain.GameData.GameDataCatalog gameData,
        AntiMultiOpen.AntiMultiOpenAdmission admission,
        ServerOptions options,
        CancellationToken cancellationToken,
        ILogger logger,
        Social.ChatRuntime chat,
        Social.SocialRuntime social,
        Social.OnlinePresenceRegistry presence,
        SessionRegistry sessions)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(options.MaxWebSocketMessageBytes, 64 * 1024));
        long lastInboundSequence = 0;
        var handshaken = false;
        var displayName = string.Empty;
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
                            // 踢线通知取号-编码-入队在被踢会话锁内完成：与其推送生产者的序号次序保持一致。
                            lock (kicked.OutboundSync)
                            {
                                kicked.TrySend(EncodeJson(WireMessageType.Error, Guid.NewGuid(), kicked.NextOutboundSequence(),
                                    new ErrorDto(AntiMultiOpen.AntiMultiOpenConstants.SessionKickedErrorCode, "账号已在其他窗口或设备登录，当前连接已被替换。", null)));
                            }
                            kicked.RequestClose(AntiMultiOpen.AntiMultiOpenConstants.SessionKickedCloseCode, "Kicked by a newer session.");
                            // 等待旧会话完成拆除再加载 Actor：杜绝新旧会话并发驱动同一聚合、
                            // 以及旧会话 finally 误删新加载 Actor 的竞态。
                            try { await kicked.TeardownCompleted.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
                            catch (TimeoutException) { logger.LogWarning("被顶替会话拆除超时，继续准入 playerId={PlayerId}", session.PlayerId); }
                        }
                        var serverVersion = typeof(WebSocketEndpoint).Assembly
                            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                            .FirstOrDefault()?.InformationalVersion ?? "unknown";
                        // HelloAck 序号内嵌载荷（客户端据此对齐下行序）：取号-编码在同一会话锁内完成，
                        // 与可能并发的快照推送保持「序号递增序 = 通道物理序」，随后锁外阻塞投递保证必达。
                        byte[] helloAckPacket;
                        lock (session.OutboundSync)
                        {
                            var helloAckSequence = session.NextOutboundSequence();
                            helloAckPacket = EncodeJson(WireMessageType.HelloAck, envelope.RequestId, helloAckSequence,
                                new HelloResponse(serverVersion, ProtocolConstants.CurrentVersion, gameData.Fingerprint, helloAckSequence,
                                    new AntiMultiOpenConfigDto(options.AntiMultiOpen.TabGuardEnabled)));
                        }
                        await session.SendAsync(helloAckPacket, cancellationToken);
                        var initial = await runtime.LoadAsync(session.PlayerId, cancellationToken);
                        displayName = initial.DisplayName;
                        // 握手数据获取并行化：快照编码（CPU）、社交/聊天/存在感（独立 IO 与内存计算）互不依赖，
                        // 总耗时取最大而非求和；下行发送顺序保持 HelloAck → Snapshot → LogBatch → SocialState → ChatHistory 不变。
                        var handshakeInspectIndex = session.PendingInspectedEnemyIndex;
                        var inspectAttributesProjected = false;
                        var snapshotTask = Task.Run(
                            () => ProtocolSnapshotMapper.ToSnapshot(initial, gameData.Fingerprint, gameData, handshakeInspectIndex, () => inspectAttributesProjected = true),
                            cancellationToken);
                        var socialTask = social.BuildStateAsync(session.PlayerId, null, cancellationToken);
                        var worldChatTask = chat.LoadHistoryAsync(session.PlayerId, new ChatHistoryRequestDto(ChatChannels.World), cancellationToken);
                        var nearbyChatTask = chat.LoadHistoryAsync(session.PlayerId, new ChatHistoryRequestDto(ChatChannels.Nearby), cancellationToken);
                        // 在线存在感登记：世界/附近广播与好友在线状态都依赖它；登记是独立副作用，与上方读取并行。
                        var presenceTask = presence.RegisterAsync(session.PlayerId, displayName, initial.State.World.NodeId, cancellationToken);
                        var initialSnapshot = await snapshotTask;
                        await QueueJsonAsync(session, WireMessageType.Snapshot, Guid.NewGuid(), initialSnapshot, cancellationToken);
                        // 握手快照必达：属性面板实际写入即解除一次性交付武装。
                        if (inspectAttributesProjected) session.MarkInspectedAttributesDelivered(handshakeInspectIndex);
                        // 初始快照帧已进入发送队列：直接提交推送基线，后续增量以此裁决变更。
                        var initialSectionChecksums = new Dictionary<string, string>(initialSnapshot.Sections.Count, StringComparer.Ordinal);
                        foreach (var section in initialSnapshot.Sections)
                            initialSectionChecksums[section.Section] = section.Checksum;
                        session.CommitPushBaseline(initialSectionChecksums, initialSnapshot.Assets);
                        // 重连回放：把环形缓冲内的最近战报一次性补发给本会话。
                        var recentLogs = runtime.GetRecentLogEvents(session.PlayerId);
                        if (recentLogs.Count > 0)
                            await QueueJsonAsync(session, WireMessageType.LogBatch, Guid.NewGuid(), new LogBatchDto(recentLogs), cancellationToken);
                        await presenceTask;
                        await QueueJsonAsync(session, WireMessageType.SocialState, Guid.NewGuid(), await socialTask, cancellationToken);
                        // 握手时只回放世界与附近两个「无参数」频道；好友私聊与队伍频道由客户端按需请求。
                        if (await worldChatTask is { } worldHistory)
                            await QueueJsonAsync(session, WireMessageType.ChatHistory, Guid.NewGuid(), worldHistory, cancellationToken);
                        if (await nearbyChatTask is { } nearbyHistory)
                            await QueueJsonAsync(session, WireMessageType.ChatHistory, Guid.NewGuid(), nearbyHistory, cancellationToken);
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
                        // 回执快照双形态：触及差集精确且已有推送基线 → delta；否则全量兜底。
                        var receiptPacket = BuildCommandResultPacket(
                            session, commandEnvelope.RequestId, envelope.RequestId, commandResult, gameData, out var receiptEnqueued);
                        if (!receiptEnqueued) await session.SendAsync(receiptPacket, cancellationToken);
                        break;
                    case WireMessageType.ChatSend:
                        if (!handshaken)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("HANDSHAKE_REQUIRED", "请先完成 hello 握手。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        if (!WireEnvelopeCodec.TryDeserializeJson<ChatSendDto>(envelope, out var chatSend) || chatSend is null
                            || !Guid.TryParse(chatSend.RequestId, out var chatRequestId) || chatRequestId != envelope.RequestId)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("INVALID_CHAT", "传音载荷无效。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        var chatOutcome = await chat.SendAsync(session.PlayerId, displayName, chatSend, cancellationToken);
                        if (chatOutcome.Status != Social.ChatSendStatus.Ok)
                        {
                            var (chatErrorCode, chatErrorMessage) = chatOutcome.Status switch
                            {
                                Social.ChatSendStatus.RateLimited => ("CHAT_RATE_LIMITED", "发言过于频繁，请稍后再试。"),
                                Social.ChatSendStatus.InvalidBody => ("CHAT_INVALID_BODY", "消息内容为空或超过长度上限。"),
                                _ => ("CHAT_CHANNEL_UNAVAILABLE", "该传音频道当前不可用。")
                            };
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto(chatErrorCode, chatErrorMessage, envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        BroadcastChat(sessions, chatOutcome);
                        break;
                    case WireMessageType.ChatHistoryRequest:
                        if (!handshaken)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("HANDSHAKE_REQUIRED", "请先完成 hello 握手。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        if (!WireEnvelopeCodec.TryDeserializeJson<ChatHistoryRequestDto>(envelope, out var historyRequest) || historyRequest is null)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("INVALID_CHAT_HISTORY", "历史回放请求无效。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        var historyPayload = await chat.LoadHistoryAsync(session.PlayerId, historyRequest, cancellationToken);
                        if (historyPayload is null)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("CHAT_CHANNEL_UNAVAILABLE", "该传音频道当前不可用。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        await QueueJsonAsync(session, WireMessageType.ChatHistory, envelope.RequestId, historyPayload, cancellationToken);
                        break;
                    case WireMessageType.SocialAction:
                        if (!handshaken)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("HANDSHAKE_REQUIRED", "请先完成 hello 握手。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        if (!WireEnvelopeCodec.TryDeserializeJson<SocialActionDto>(envelope, out var socialAction) || socialAction is null
                            || !Guid.TryParse(socialAction.RequestId, out var socialRequestId) || socialRequestId != envelope.RequestId)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("INVALID_SOCIAL", "社交指令载荷无效。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        var socialOutcome = await social.ApplyAsync(session.PlayerId, socialAction, cancellationToken);
                        if (!socialOutcome.Success)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId,
                                new ErrorDto(SocialErrorCode(socialOutcome.Failure), SocialErrorMessage(socialOutcome.Failure), envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        // 社交操作的成功应答必达：它是发起方唯一的操作确认（StateChanged 广播排除发起方，
                        // 且广播形态在可丢弃清单内），丢失会让客户端永久看不到本次操作结果。
                        await session.SendJsonAsync(WireMessageType.SocialState, envelope.RequestId,
                            await social.BuildStateAsync(session.PlayerId, envelope.RequestId.ToString("D"), cancellationToken), cancellationToken);
                        break;
                    case WireMessageType.PartyLobbyRequest:
                        if (!handshaken)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("HANDSHAKE_REQUIRED", "请先完成 hello 握手。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        if (!WireEnvelopeCodec.TryDeserializeJson<PartyLobbyRequestDto>(envelope, out var lobbyRequest) || lobbyRequest is null
                            || !Guid.TryParse(lobbyRequest.RequestId, out var lobbyRequestId) || lobbyRequestId != envelope.RequestId)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("INVALID_PARTY_LOBBY", "组队大厅请求无效。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        await QueueJsonAsync(session, WireMessageType.PartyLobby, envelope.RequestId,
                            await social.BuildLobbyAsync(session.PlayerId, lobbyRequest, cancellationToken), cancellationToken);
                        break;
                    case WireMessageType.UnitInspect:
                        if (!handshaken)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("HANDSHAKE_REQUIRED", "请先完成 hello 握手。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        if (!WireEnvelopeCodec.TryDeserializeJson<UnitInspectDto>(envelope, out var inspect) || inspect is null
                            || (inspect.Side is null) != (inspect.Index is null)
                            || (inspect.Side is not null && inspect.Side != "enemy" && inspect.Side != "ally")
                            || (inspect.Index is { } inspectIdx && inspectIdx < 0))
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("INVALID_INSPECT", "单位观察目标无效。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        // 火忘帧无回执：登记武装一次性属性交付，由下一帧快照按命中下标投影一次后自动解除；
                        // 帧被慢消费者丢弃则保留武装重投，客户端按 enemyId 缓存复用无需重复下发。
                        session.SetInspectedUnit(inspect.Side is null ? null : inspect);
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

    /// <summary>
    /// 把一条已落库的传音消息投递给作用域内全部在线会话。
    /// 同一消息载荷只序列化一次，全部收件人共享同一段载荷字节；requestId/sequence 属会话私有信封头。
    /// 投递走非阻塞 <see cref="WebSocketSession.TrySendDroppable"/>：慢客户端不能反向阻塞发言者的接收循环，
    /// 掉帧可由频道历史回放（最近 100 条）在重连或切频道时补齐。
    /// </summary>
    public static void BroadcastChat(SessionRegistry sessions, Social.ChatSendOutcome outcome)
    {
        if (outcome.Message is not { } message || outcome.Recipients is not { Count: > 0 } recipients) return;
        // 载荷 JSON 与接收方无关：一次编码全体共享，避免按收件人重复序列化。
        var payloadBytes = (ReadOnlyMemory<byte>)JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJson.Options);
        foreach (var recipientId in recipients)
        {
            if (!sessions.TryGet(recipientId, out var target)) continue;
            target.TrySendDroppableEnvelope(WireMessageType.ChatMessage, Guid.NewGuid(), payloadBytes);
        }
    }

    /// <summary>
    /// 构造命令回执帧：成功路径的精确触及差集复用会话推送基线窄化为 delta 形态；
    /// 失败、幂等重放（TouchedSections 未知）或尚无推送基线时回退全量 snapshot 形态。
    /// delta 帧与实时推送共用同一条基线，「读基线 → 编码 → 入队 → 提交基线」在 <see cref="WebSocketSession.OutboundSync"/>
    /// 内整体串行：回执帧在通道中物理先于任何按新基线编码的并发推送，客户端按序合并后与全量状态逐位一致。
    /// 回执帧不可丢：入队成功才提交基线；满队列时退回锁外阻塞发送（enqueued=false），基线不前移让后续增量补发。
    /// </summary>
    /// <param name="enqueued">true 表示帧已进入发送队列；false 时调用方须在锁外 SendAsync 同一数据包。</param>
    public static byte[] BuildCommandResultPacket(
        WebSocketSession session,
        string requestId,
        Guid envelopeRequestId,
        PlayerCommandResult result,
        Domain.GameData.GameDataCatalog gameData,
        out bool enqueued)
    {
        if (result.Snapshot.TouchedSections is { } touched)
        {
            lock (session.OutboundSync)
            {
                var (previousSections, previousAssets) = session.ReadPushBaseline();
                if (previousSections is not null)
                {
                    var pendingInspectIndex = session.PendingInspectedEnemyIndex;
                    var attributesProjected = false;
                    var delta = ProtocolSnapshotMapper.BuildDelta(
                        result.Snapshot, gameData.Fingerprint, gameData, touched,
                        previousSections, previousAssets,
                        out var nextSections, out var nextAssets,
                        pendingInspectIndex, () => attributesProjected = true);
                    var packet = EncodeJson(WireMessageType.CommandResult, envelopeRequestId, session.NextOutboundSequence(),
                        new CommandResultDto(requestId, result.Success, result.Code, result.Message,
                            result.Snapshot.StateVersion, CommandResultSnapshotForms.Delta,
                            JsonSerializer.SerializeToElement(delta, ProtocolJson.Options)));
                    enqueued = session.TrySend(packet);
                    if (enqueued) session.CommitPushBaseline(nextSections, nextAssets);
                    // 回执帧必达：入队失败由调用方锁外 SendAsync 阻塞投递，属性交付直接记账。
                    if (attributesProjected) session.MarkInspectedAttributesDelivered(pendingInspectIndex);
                    return packet;
                }
            }
        }
        var pendingInspectIndexFull = session.PendingInspectedEnemyIndex;
        var attributesProjectedFull = false;
        var fullSnapshot = ProtocolSnapshotMapper.ToSnapshot(result.Snapshot, gameData.Fingerprint, gameData,
            pendingInspectIndexFull, () => attributesProjectedFull = true);
        if (attributesProjectedFull) session.MarkInspectedAttributesDelivered(pendingInspectIndexFull);
        var full = new CommandResultDto(
            requestId, result.Success, result.Code, result.Message,
            result.Snapshot.StateVersion, CommandResultSnapshotForms.Snapshot,
            JsonSerializer.SerializeToElement(fullSnapshot, ProtocolJson.Options));
        // 全量兜底回执同样取号-编码-入队在会话锁内完成，保持与并发推送的序号次序一致；
        // 满队列时 enqueued=false 由调用方锁外 SendAsync 阻塞投递。
        lock (session.OutboundSync)
        {
            var packet = EncodeJson(WireMessageType.CommandResult, envelopeRequestId, session.NextOutboundSequence(), full);
            enqueued = session.TrySend(packet);
            return packet;
        }
    }

    /// <summary>社交失败原因到协议错误码的映射。</summary>
    private static string SocialErrorCode(Social.SocialFailure failure) => failure switch
    {
        Social.SocialFailure.InvalidAction => "SOCIAL_INVALID_ACTION",
        Social.SocialFailure.TargetNotFound => "SOCIAL_TARGET_NOT_FOUND",
        Social.SocialFailure.SelfTarget => "SOCIAL_SELF_TARGET",
        Social.SocialFailure.AlreadyFriends => "SOCIAL_ALREADY_FRIENDS",
        Social.SocialFailure.NotFriends => "SOCIAL_NOT_FRIENDS",
        Social.SocialFailure.RequestExists => "SOCIAL_REQUEST_EXISTS",
        Social.SocialFailure.RequestNotFound => "SOCIAL_REQUEST_NOT_FOUND",
        Social.SocialFailure.TargetAlreadyInParty => "SOCIAL_TARGET_IN_PARTY",
        Social.SocialFailure.AlreadyInParty => "SOCIAL_ALREADY_IN_PARTY",
        Social.SocialFailure.NotInParty => "SOCIAL_NOT_IN_PARTY",
        Social.SocialFailure.PartyFull => "SOCIAL_PARTY_FULL",
        Social.SocialFailure.NotLeader => "SOCIAL_NOT_LEADER",
        Social.SocialFailure.InvitationNotFound => "SOCIAL_INVITATION_NOT_FOUND",
        Social.SocialFailure.PartyNotFound => "SOCIAL_PARTY_NOT_FOUND",
        Social.SocialFailure.ValuationTooLow => "SOCIAL_VALUATION_TOO_LOW",
        Social.SocialFailure.InvalidPartyProfile => "SOCIAL_INVALID_PARTY_PROFILE",
        Social.SocialFailure.SeekingNotFound => "SOCIAL_SEEKING_NOT_FOUND",
        Social.SocialFailure.RateLimited => "SOCIAL_RATE_LIMITED",
        Social.SocialFailure.ChannelUnavailable => "SOCIAL_CHANNEL_UNAVAILABLE",
        _ => "SOCIAL_FAILED"
    };

    /// <summary>社交失败原因到用户可读文案的映射。</summary>
    private static string SocialErrorMessage(Social.SocialFailure failure) => failure switch
    {
        Social.SocialFailure.InvalidAction => "不支持的社交指令。",
        Social.SocialFailure.TargetNotFound => "目标修士不存在。",
        Social.SocialFailure.SelfTarget => "不能对自己执行该操作。",
        Social.SocialFailure.AlreadyFriends => "你们已经是好友。",
        Social.SocialFailure.NotFriends => "你们还不是好友。",
        Social.SocialFailure.RequestExists => "已存在待处理的好友申请。",
        Social.SocialFailure.RequestNotFound => "好友申请不存在或已处理。",
        Social.SocialFailure.TargetAlreadyInParty => "对方已在其他队伍中。",
        Social.SocialFailure.AlreadyInParty => "你已经在队伍中。",
        Social.SocialFailure.NotInParty => "你当前不在队伍中。",
        Social.SocialFailure.PartyFull => "队伍人数已满。",
        Social.SocialFailure.NotLeader => "只有队长可以执行该操作。",
        Social.SocialFailure.InvitationNotFound => "没有待处理的队伍邀请。",
        Social.SocialFailure.PartyNotFound => "队伍不存在或已解散。",
        Social.SocialFailure.ValuationTooLow => "身价未达到队伍门槛。",
        Social.SocialFailure.InvalidPartyProfile => "队伍配置无效。",
        Social.SocialFailure.SeekingNotFound => "你没有进行中的队伍匹配。",
        Social.SocialFailure.RateLimited => "操作过于频繁，请稍后再试。",
        Social.SocialFailure.ChannelUnavailable => "该传音频道当前不可用。",
        _ => "社交操作失败。"
    };

    private static ValueTask QueueJsonAsync<T>(
        WebSocketSession session,
        WireMessageType messageType,
        Guid requestId,
        T payload,
        CancellationToken cancellationToken)
    {
        // Snapshot 增量、战报日志与传音/社交推送属于可丢弃的实时推送，慢客户端不能反向阻塞玩家模拟/持久化。
        // 队列已满时丢弃该帧并累计慢消费者证据，客户端可通过下一次命令响应、频道历史回放或重连
        // 重新获取完整快照、最近日志与社交状态；命令结果、错误、握手响应与历史回放仍保留等待语义。
        // 取号-编码-入队由会话方法在 OutboundSync 内整体完成：通道物理序恒等于序号递增序。
        if (messageType is WireMessageType.SnapshotDelta or WireMessageType.LogBatch
            or WireMessageType.ChatMessage or WireMessageType.SocialState)
        {
            session.TrySendJsonDroppable(messageType, requestId, payload);
            return ValueTask.CompletedTask;
        }
        return session.SendJsonAsync(messageType, requestId, payload, cancellationToken);
    }
}
