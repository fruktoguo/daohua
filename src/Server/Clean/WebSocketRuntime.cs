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
public sealed class WebSocketSession(
    Guid playerId,
    int capacity,
    Guid accountId = default,
    byte[]? authSessionTokenHash = null,
    bool isBot = false,
    DateTimeOffset accountCreatedAt = default)
{
    /// <summary>慢消费者阈值：连续丢弃帧数上限。有界队列满时实时推送逐帧丢弃，连续饱和视为无法消化。</summary>
    public const int SlowConsumerMaxDrops = 32;
    /// <summary>慢消费者阈值：自首次丢弃起的持续饱和时长上限，与帧数阈值先到先判。</summary>
    internal static readonly TimeSpan SlowConsumerSaturationWindow = TimeSpan.FromSeconds(15);
    /// <summary>慢消费者关闭码：4009 落在自定义私有段且未被防多开占用，前端走默认自动重连恢复全量快照。</summary>
    public const int SlowConsumerCloseCode = 4009;

    // 会话创建即处于「握手快照未入队」状态：OnSnapshotUpdated 订阅先于握手完成，装载期与握手
    // 编码窗口内的模拟推进不得抢发增量（否则 delta 先于首帧全量到达，客户端尚未归属即抛错）。
    private volatile bool _handshakeSnapshotPending = true;

    private readonly Channel<byte[]> _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly TaskCompletionSource _teardownCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _outboundSequence;
    private int _reliableWaiters;
    private long _lastPushedStateVersion;
    private IReadOnlyList<AssetBalanceDto>? _lastPushedAssets;
    private IReadOnlyDictionary<string, string>? _lastPushedSectionChecksums;
    private volatile UnitInspectDto? _inspectedUnit;
    /// <summary>观察目标的属性面板尚未随成功入队的帧交付：武装于登记，交付后解除，避免观察期间逐拍重复下发。</summary>
    private volatile bool _inspectDeliveryPending;
    /// 快照增量帧被拥塞丢弃且尚未补投：基线停在旧内容上，静止态分段（如落地后的 world）可能再无推送事件触发重投。
    private volatile bool _deltaResyncPending;
    private int _consecutiveDrops;
    private long _firstDropUtcTicks;

    /// <summary>会话绑定的修仙者角色 ID</summary>
    public Guid PlayerId { get; } = playerId;
    /// <summary>会话绑定的账号 ID：登出/改密吊销会话后按账号定位需踢线的在线连接。</summary>
    public Guid AccountId { get; } = accountId;
    /// <summary>
    /// 握手 Cookie 会话令牌的 SHA-256 哈希：吊销踢线按哈希精确匹配本连接，
    /// 避免误杀同账号在其他设备上仍有效的会话。未携带 Cookie 升级（理论不可达）时为 null。
    /// </summary>
    public byte[]? AuthSessionTokenHash { get; } = authSessionTokenHash;
    /// <summary>是否为市场/系统机器人账号（群发传书通知与红点摘要推送强制屏蔽）。</summary>
    public bool IsBot { get; } = isBot;
    /// <summary>账号创建时刻（UTC），供全服群发历史角色门槛无库判定。</summary>
    public DateTimeOffset AccountCreatedAt { get; } = accountCreatedAt;
    /// <summary>下行消息输出通道读取端</summary>
    public ChannelReader<byte[]> Outbound => _outbound.Reader;
    /// <summary>
    /// 会话被服务端主动终止时应使用的 WebSocket 关闭码与原因；
    /// 由防多开准入模块在踢出/拒绝路径上设置（先写字段再 Complete 通道，发送循环完成即建立可见性）。
    /// </summary>
    public (int CloseCode, string Reason)? CloseHint { get; private set; }
    /// <summary>准入时登记的会话所有权键（设备键等）；未启用相应准入模块或未通过准入时为 null</summary>
    public string? AdmissionDeviceKey { get; set; }
    /// <summary>历史别名（OpsPanel等使用）</summary>
    public string? DeviceFingerprint => AdmissionDeviceKey;
    /// <summary>会话拆除（含事件退订、注册表释放与必要的 Actor 卸载）全部完成后置位</summary>
    public Task TeardownCompleted => _teardownCompleted.Task;
    /// <summary>客户端经 UnitInspect 火忘帧登记的单位详情观察目标；会话级浏览态，断开即销毁。</summary>
    public UnitInspectDto? InspectedUnit => _inspectedUnit;
    /// <summary>待交付的观察目标：仅登记后属性面板尚未交付期间返回目标；交付后快照不再携带属性面板（前端按身份缓存复用）。</summary>
    public UnitInspectDto? PendingInspectedUnit => _inspectDeliveryPending ? _inspectedUnit : null;
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
    public void MarkInspectedAttributesDelivered(UnitInspectDto? delivered)
    {
        if (delivered is not null && Equals(delivered, _inspectedUnit)) _inspectDeliveryPending = false;
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
    /// <summary>不区分可丢弃性的即时入队；生产者须持有出站锁完成取号和入队。</summary>
    public bool TrySend(byte[] packet)
    {
        lock (OutboundSync) return _outbound.Writer.TryWrite(packet);
    }

    public ValueTask SendJsonAsync<T>(WireMessageType type, Guid requestId, T payload, CancellationToken token) =>
        SendReliableAsync((type, requestId, payload), static (session, state) =>
            session.TrySend(WireEnvelopeCodec.EncodeJson(state.type, state.requestId, session.NextOutboundSequence(), state.payload)), token);

    /// <summary>必达帧只在取得队列位置后取号、编码和入队。等待时给必达帧留出下一空位，不阻塞模拟线程。</summary>
    internal async ValueTask SendReliableAsync<T>(T state, Action<WebSocketSession, T> enqueue, CancellationToken token)
    {
        lock (OutboundSync)
        {
            if (CloseHint is not null) return;
            token.ThrowIfCancellationRequested();
            if (_outbound.Reader.Count < capacity)
            {
                enqueue(this, state);
                return;
            }
            _reliableWaiters++;
        }
        var ownsReservation = true;
        try
        {
            while (await _outbound.Writer.WaitToWriteAsync(token).ConfigureAwait(false))
            {
                lock (OutboundSync)
                {
                    if (CloseHint is not null) return;
                    token.ThrowIfCancellationRequested();
                    if (_outbound.Reader.Count >= capacity) continue;
                    _reliableWaiters--;
                    ownsReservation = false;
                    enqueue(this, state);
                    return;
                }
            }
        }
        finally
        {
            if (ownsReservation)
            {
                lock (OutboundSync) _reliableWaiters--;
            }
        }
    }

    /// <summary>
    /// 可丢弃 JSON 帧的有序投递：取号-编码-入队在 <see cref="OutboundSync"/> 内原子完成；
    /// 满队列丢弃并累计慢消费者证据（语义同 <see cref="TrySendDroppable"/>）。
    /// </summary>
    /// <returns>帧已进入发送队列返回 true；被丢弃（或会话已在关闭流程）返回 false。</returns>
    public bool TrySendJsonDroppable<T>(WireMessageType messageType, Guid requestId, T payload)
    {
        lock (OutboundSync)
            return CanQueueDroppable() && TryWriteDroppable(WireEnvelopeCodec.EncodeJson(messageType, requestId, NextOutboundSequence(), payload));
    }

    /// <summary>
    /// 共享载荷字节的可丢弃广播帧：多收件人复用同一段已序列化载荷，取号-编码-入队仍在 <see cref="OutboundSync"/> 内原子完成。
    /// </summary>
    public bool TrySendDroppableEnvelope(WireMessageType messageType, Guid requestId, ReadOnlyMemory<byte> payload)
    {
        lock (OutboundSync)
            return CanQueueDroppable() && TryWriteDroppable(WireEnvelopeCodec.Encode(
                WireEnvelope.Create(messageType, requestId, NextOutboundSequence(), payload)));
    }

    /// <summary>
    /// 尝试非阻塞投递一帧可丢弃的实时推送：满队列丢弃时累计慢消费者证据，
    /// 连续丢帧或持续饱和达到阈值即按 4009 请求关闭（前端默认重连拉全量）。
    /// </summary>
    /// <returns>帧已进入发送队列返回 true；被丢弃（或会话已在关闭流程）返回 false。</returns>
    public bool TrySendDroppable(byte[] packet)
    {
        lock (OutboundSync) return CanQueueDroppable() && TryWriteDroppable(packet);
    }

    // 只在出站锁内检查；同一锁内生产者之外只有消费者，检查通过后空位不会消失。
    internal bool CanQueueDroppable()
    {
        if (CloseHint is not null) return false;
        if (_outbound.Reader.Count < capacity)
            return _reliableWaiters == 0;
        var nowTicks = DateTimeOffset.UtcNow.Ticks;
        if (_consecutiveDrops++ == 0) _firstDropUtcTicks = nowTicks;
        if (_consecutiveDrops >= SlowConsumerMaxDrops || nowTicks - _firstDropUtcTicks >= SlowConsumerSaturationWindow.Ticks)
            RequestClose(SlowConsumerCloseCode, "slow consumer");
        return false;
    }

    internal bool TryWriteDroppable(byte[] packet)
    {
        if (!_outbound.Writer.TryWrite(packet)) return false;
        _consecutiveDrops = 0;
        return true;
    }

    /// <summary>标记输出通道完成并关闭</summary>
    public void Complete()
    {
        lock (OutboundSync) _outbound.Writer.TryComplete();
    }

    /// <summary>
    /// 请求服务端主动终止本会话：可选地先把一个通知帧排入发送队列，再完成输出通道，
    /// 由所属 HandleAsync 在收尾阶段按 CloseHint 发送 WebSocket 关闭帧。可重复调用，幂等。
    /// </summary>
    /// <param name="closeCode">自定义关闭码（4000-4999 私有段）</param>
    /// <param name="reason">关闭原因</param>
    /// <param name="noticePacket">可选的下行通知帧（如 Error 包），在关闭前随队列余量一并冲刷</param>
    public void RequestClose(int closeCode, string reason, byte[]? noticePacket = null)
    {
        lock (OutboundSync)
        {
            if (CloseHint is not null) return;
            if (noticePacket is not null) TrySend(noticePacket);
            CloseHint = (closeCode, reason);
            Complete();
        }
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
    public void CommitPushBaseline(IReadOnlyDictionary<string, string> sectionChecksums, IReadOnlyList<AssetBalanceDto> assets, long stateVersion = 0)
    {
        lock (OutboundSync)
        {
            _lastPushedSectionChecksums = sectionChecksums;
            _lastPushedAssets = assets;
            _lastPushedStateVersion = stateVersion;
        }
    }

    internal bool IsOlderThanPushBaseline(long stateVersion) => stateVersion < _lastPushedStateVersion;

    /// 是否存在被拥塞丢弃且尚未补投的快照增量：为 true 时发送循环排空后应触发一次全段重投。
    public bool DeltaResyncPending => _deltaResyncPending;
    /// 快照增量入队失败时登记重投需求：只由快照推送路径调用，聊天/战报等其它可丢弃帧各有回放通道不置位。
    public void MarkDeltaDropped() => _deltaResyncPending = true;
    /// 全量已入队后才解除标记；窄化增量不覆盖被丢的未触及分段。
    public void CompleteDeltaResync() => _deltaResyncPending = false;
    /// 握手全量快照已入队（基线已提交）前禁止实时增量推送：装载期模拟推进改走丢帧重投通道。
    public bool HandshakeSnapshotPending => _handshakeSnapshotPending;
    /// <summary>入队全量快照后提交基线；首帧同时解除握手期间的增量抑制。</summary>
    public void CommitFullSnapshot(PlayerSnapshotDto snapshot)
    {
        var checksums = new Dictionary<string, string>(snapshot.Sections.Count, StringComparer.Ordinal);
        foreach (var section in snapshot.Sections) checksums[section.Section] = section.Checksum;
        lock (OutboundSync)
        {
            CommitPushBaseline(checksums, snapshot.Assets, snapshot.StateVersion);
            _handshakeSnapshotPending = false;
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
    /// <summary>当前全部在线会话引用快照（用于全服传书单帧旁路广播，避免按玩家二次字典查表）。</summary>
    public IReadOnlyList<WebSocketSession> SnapshotSessions() => _sessions.Values.ToArray();
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

    /// <summary>
    /// 按会话令牌哈希关闭匹配的在线会话（登出吊销踢线）：使命令通道与被吊销的 HTTP 会话同步失效。
    /// 返回被请求关闭的会话数。
    /// </summary>
    public int CloseByTokenHash(byte[] tokenHash, int closeCode, string reason)
    {
        var closed = 0;
        foreach (var session in _sessions.Values)
        {
            if (session.AuthSessionTokenHash is not { } hash || !CryptographicOperations.FixedTimeEquals(hash, tokenHash)) continue;
            session.RequestClose(closeCode, reason);
            closed++;
        }
        return closed;
    }

    /// <summary>
    /// 按账号关闭除豁免令牌哈希外的全部在线会话（改密吊销其他会话后的踢线）：
    /// 被吊销的挂机连接最长可存活数天且命令通道不再受鉴权约束，必须随吊销同步终止。
    /// 返回被请求关闭的会话数。
    /// </summary>
    public int CloseByAccountExcept(Guid accountId, byte[]? exceptTokenHash, int closeCode, string reason)
    {
        if (accountId == Guid.Empty) return 0;
        var closed = 0;
        foreach (var session in _sessions.Values)
        {
            if (session.AccountId != accountId) continue;
            if (exceptTokenHash is not null && session.AuthSessionTokenHash is { } hash
                && CryptographicOperations.FixedTimeEquals(hash, exceptTokenHash)) continue;
            session.RequestClose(closeCode, reason);
            closed++;
        }
        return closed;
    }
}

/// <summary>
/// WebSocket 通信端点处理器。
/// 负责协议升级、握手认证、收发双工管道与异常恢复。
/// </summary>
public static class WebSocketEndpoint
{
    internal static bool IsAllowedPlayerWebSocketOrigin(HttpContext context, ServerOptions options)
    {
        // 非浏览器工具可省略 Origin，但仍须先通过会话认证。
        // 浏览器自动附带的来源必须与入口同源，或命中完整来源白名单。
        if (!context.Request.Headers.TryGetValue("Origin", out var values)) return true;
        if (values.Count != 1 || ServerOptions.NormalizeWebSocketOrigin(values[0]) is not { } origin)
            return false;
        var requestOrigin = ServerOptions.NormalizeWebSocketOrigin($"{context.Request.Scheme}://{context.Request.Host}");
        return string.Equals(origin, requestOrigin, StringComparison.OrdinalIgnoreCase)
            || options.WebSocketAllowedOrigins.Contains(origin);
    }

    /// <summary>会话认证与来源核验通过后才升级，然后执行 Hello 准入和收发循环。</summary>
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
        if (!IsAllowedPlayerWebSocketOrigin(context, options))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { code = "WEBSOCKET_ORIGIN_REJECTED", message = "WebSocket 来源不被允许。" });
            return;
        }
        var admission = context.RequestServices.GetRequiredService<Admission.IHelloAdmissionHook>();
        var runtime = context.RequestServices.GetRequiredService<IPlayerRuntime>();
        var gameData = context.RequestServices.GetRequiredService<Domain.GameData.GameDataCatalog>();
        var sessions = context.RequestServices.GetRequiredService<SessionRegistry>();
        var chat = context.RequestServices.GetRequiredService<Social.ChatRuntime>();
        var social = context.RequestServices.GetRequiredService<Social.SocialRuntime>();
        var presence = context.RequestServices.GetRequiredService<Social.OnlinePresenceRegistry>();
        var grace = context.RequestServices.GetRequiredService<DisconnectGraceRegistry>();
        var presenceStats = context.RequestServices.GetRequiredService<Social.IPlayerPresenceStatsService>();
        var simulationClock = context.RequestServices.GetRequiredService<IPlayerSimulationClock>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebSocketEndpoint");
        var accountRisk = context.RequestServices.GetRequiredService<AccountAbuse.PostgresAccountRiskRepository>();
        var riskContext = context.RequestServices.GetRequiredService<AccountAbuse.AccountRiskRequestContext>();
        var accountId = Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var isBot = string.Equals(context.User.FindFirstValue("is_bot"), "true", StringComparison.OrdinalIgnoreCase);
        var accountCreatedAt = long.TryParse(context.User.FindFirstValue("account_created_at"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var createdAtMs)
            && createdAtMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs)
            : DateTimeOffset.MinValue;
        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            DangerousEnableCompression = true,
            DisableServerContextTakeover = false,
            ServerMaxWindowBits = 12
        });
        // 会话令牌哈希随会话登记：登出/改密吊销 HTTP 会话时按哈希精确踢线对应 WebSocket，
        // 避免被吊销的挂机连接继续持有可用的命令通道。
        byte[]? authSessionTokenHash = null;
        var wsSessionToken = SessionAuthenticationDefaults.ReadPresentedToken(context);
        if (!string.IsNullOrWhiteSpace(wsSessionToken))
            authSessionTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(wsSessionToken));
        var session = new WebSocketSession(playerId, options.WebSocketOutboundCapacity, accountId, authSessionTokenHash, isBot, accountCreatedAt);
        // 会话准入（单账号互踢/拒绝 + 设备配额）推迟到 Hello 处理时统一裁决：
        // 设备指纹只随 Hello 载荷到达，accept 阶段缺少裁决所需的全部输入。

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var token = linked.Token;
        // 静止角色可能不再推进；发送循环在队列排空时补投。
        // 全量帧可修复同 StateVersion 的分段漂移，增量帧可能被客户端版本门禁略过。
        void ResyncDroppedDelta()
        {
            lock (session.OutboundSync)
            {
                if (!session.DeltaResyncPending || session.HandshakeSnapshotPending || !session.CanQueueDroppable()) return;
                var snapshot = runtime.TryGetLoaded(session.PlayerId);
                if (snapshot is null || session.IsOlderThanPushBaseline(snapshot.StateVersion)) return;
                var inspect = session.PendingInspectedUnit;
                var attributesProjected = false;
                var full = ProtocolSnapshotMapper.ToSnapshot(snapshot, gameData.Fingerprint, gameData,
                    inspect, () => attributesProjected = true);
                if (!session.TryWriteDroppable(EncodeJson(WireMessageType.Snapshot, Guid.NewGuid(), session.NextOutboundSequence(), full))) return;
                session.CommitFullSnapshot(full);
                if (attributesProjected) session.MarkInspectedAttributesDelivered(inspect);
                session.CompleteDeltaResync();
            }
        }
        async ValueTask OnSnapshotUpdated(PlayerRuntimeSnapshot snapshot)
        {
            if (snapshot.PlayerId != session.PlayerId || token.IsCancellationRequested) return;
            // 权威位置随快照推进：迁地途中（EdgeId 非空）按「不在节点」上报——
            // 换节点会触发同节点修士的附近列表刷新，也是组队讨伐在场条件的仲裁触发源。
            try
            {
                await presence.UpdateNodeAsync(snapshot.PlayerId,
                    snapshot.State.World.EdgeId is null ? snapshot.State.World.NodeId : null, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            // 入队失败（或握手全量快照基线尚在途中）由 TryPushDelta 在 OutboundSync 锁内原子登记重投：静止态角色可能再无推送事件。
            if (!TryPushDelta(session, snapshot, gameData, snapshot.TouchedSections) && session.Outbound.Count == 0)
                ResyncDroppedDelta(); // 若发送循环在标记置位前已排空，生产者兜底唤起补投。
        }
        ValueTask OnLogBatchEmitted(Guid eventPlayerId, IReadOnlyList<LogEventDto> events)
            => eventPlayerId != session.PlayerId || token.IsCancellationRequested
                ? ValueTask.CompletedTask
                : QueueJsonAsync(session, WireMessageType.LogBatch, Guid.NewGuid(), new LogBatchDto(events), token);
        async ValueTask OnSocialStateChanged(Guid eventPlayerId)
        {
            if (eventPlayerId != session.PlayerId || token.IsCancellationRequested) return;
            try
            {
                var state = await social.BuildStateAsync(session.PlayerId, null, token);
                await QueueJsonAsync(session, WireMessageType.SocialState, Guid.NewGuid(), state, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        runtime.SnapshotUpdated += OnSnapshotUpdated;
        runtime.LogBatchEmitted += OnLogBatchEmitted;
        social.StateChanged += OnSocialStateChanged;
        try
        {
            var sendTask = SendLoopAsync(socket, session, ResyncDroppedDelta, token, logger);
            var receiveTask = ReceiveLoopAsync(socket, session, runtime, gameData, admission, options, token, logger, chat, social, presence, sessions,
                accountRisk, riskContext, context, accountId, isBot, grace, presenceStats);
            await Task.WhenAny(sendTask, receiveTask);
            // 服务端主动终止路径（被顶替/准入拒绝）：先冲刷通知帧再发关闭帧。
            // 顺序硬约束：取消令牌会中止阻塞中的 ReceiveAsync 并把 socket 置为 Aborted，
            // 此后任何 CloseAsync 都无效，客户端只能收到 1006——所以关闭帧必须先于 linked.Cancel()。
            if (session.CloseHint is { } hint)
            {
                try { await sendTask.WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { }
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try { await CloseSocketAsync(socket, (WebSocketCloseStatus)hint.CloseCode, hint.Reason); } catch { socket.Abort(); }
                }
            }
            linked.Cancel();
            try { await Task.WhenAll(sendTask, receiveTask); }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex)
            {
                logger.LogDebug(ex, "WebSocket disconnected playerId={PlayerId}", playerId);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (WebSocketException ex) when (socket.State is WebSocketState.Aborted or WebSocketState.Closed)
        {
            logger.LogDebug(ex, "WebSocket disconnected playerId={PlayerId}", playerId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WebSocket 会话异常 playerId={PlayerId}", playerId);
            if (socket.State == WebSocketState.Open)
            {
                try { await CloseSocketAsync(socket, WebSocketCloseStatus.InternalServerError, "服务器错误。"); } catch { socket.Abort(); }
            }
        }
        finally
        {
            runtime.SnapshotUpdated -= OnSnapshotUpdated;
            runtime.LogBatchEmitted -= OnLogBatchEmitted;
            social.StateChanged -= OnSocialStateChanged;
            // 会话释放按实例配对：被顶替的旧会话返回 false，不得触碰新会话已加载的 Actor。
            var owned = admission.Release(session);
            // 断线宽限（总纲18 §14.3）：在线位与实体常驻宽限期，到期才注销存在感并按断线时刻落盘卸载；
            // 宽限为 0 时等价于断开即卸载。被顶替的旧会话不做任何拆除，由新会话接管同一实体。
            if (owned) await grace.ScheduleAsync(playerId, simulationClock.UtcNow);
            // 兜底关闭：主流程未发出关闭帧时（如发送循环异常）仍尝试按 CloseHint 收尾。
            if (session.CloseHint is { } closeHint && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await CloseSocketAsync(socket, (WebSocketCloseStatus)closeHint.CloseCode, closeHint.Reason); } catch { socket.Abort(); }
            }
            // 关闭连接时使用独立取消令牌：请求已断开并不应中断权威状态刷盘。
            // 若数据库暂时不可用，RemoveAsync 保留 actor 与脏标记，后台 flush worker 会继续重试。
            session.MarkTeardownCompleted();
        }
    }

    /// <summary>有界发送关闭帧，不让不回应的客户端占住会话拆除。</summary>
    private static async Task CloseSocketAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await socket.CloseOutputAsync(status, reason, timeout.Token);
    }

    private static byte[] EncodeJson<T>(WireMessageType messageType, Guid requestId, long sequence, T payload) =>
        WireEnvelopeCodec.EncodeJson(messageType, requestId, Math.Max(0, sequence), payload);

    /// <summary>
    /// 增量推送共用路径：「读基线 → 按触及分段窄化编码 → 取号入队 → 提交基线」在会话锁内整体串行——
    /// 推送可由模拟循环与斗法撮合多线程并发触发，交错会把未送达内容误记为已推送基线。
    /// touchedSections 为 null 时检查所有分段，例如会话订阅的单位详情发生变化。
    /// </summary>
    /// <returns>帧已入队返回 true；拥塞或落后的快照不入队，并登记排空后补投。</returns>
    private static bool TryPushDelta(WebSocketSession session, PlayerRuntimeSnapshot snapshot,
        Domain.GameData.GameDataCatalog gameData, IReadOnlySet<PlayerSectionKind>? touchedSections)
    {
        lock (session.OutboundSync)
        {
            if (session.HandshakeSnapshotPending)
            {
                session.MarkDeltaDropped();
                return false;
            }
            if (session.IsOlderThanPushBaseline(snapshot.StateVersion))
            {
                session.MarkDeltaDropped();
                return false;
            }
            if (!session.CanQueueDroppable())
            {
                session.MarkDeltaDropped();
                return false;
            }
            var (previousSections, previousAssets) = session.ReadPushBaseline();
            // 按需属性为一次性交付：仅「登记后尚未送达」期间投影；本帧实际写入属性且入队成功才解除武装，
            // 被慢消费者丢弃时保留武装由下一拍重投，保证客户端最终拿到面板数据。
            var pendingInspect = session.PendingInspectedUnit;
            var attributesProjected = false;
            var delta = ProtocolSnapshotMapper.BuildDelta(
                snapshot, gameData.Fingerprint, gameData, touchedSections,
                previousSections, previousAssets,
                out var nextSections, out var nextAssets,
                pendingInspect, () => attributesProjected = true);
            var packet = EncodeJson(WireMessageType.SnapshotDelta, Guid.NewGuid(), session.NextOutboundSequence(), delta);
            // 可丢弃帧入队成功才前移基线并原子更新丢帧重投标记；被丢弃时标记重投，消除锁外竞态覆盖。
            if (!session.TryWriteDroppable(packet))
            {
                session.MarkDeltaDropped();
                return false;
            }
            if (attributesProjected) session.MarkInspectedAttributesDelivered(pendingInspect);
            session.CommitPushBaseline(nextSections, nextAssets, snapshot.StateVersion);
            return true;
        }
    }

    internal static async Task SendLoopAsync(WebSocket socket, WebSocketSession session, Action deltaResync, CancellationToken cancellationToken, ILogger logger)
    {
        try
        {
            await foreach (var packet in session.Outbound.ReadAllAsync(cancellationToken))
            {
                await socket.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, cancellationToken);
                // 被丢增量在通道排空（Count==0）时补投：仍拥塞时重投必然再丢且白费全段编码，
                // 排空是最早可补投时机；若竞态再满，则保留标记等待下一次排空。
                if (session.DeltaResyncPending && session.Outbound.Count == 0)
                    deltaResync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "WebSocket send disconnected playerId={PlayerId}", session.PlayerId);
        }
    }

    private static async Task ReceiveLoopAsync(
        WebSocket socket,
        WebSocketSession session,
        IPlayerRuntime runtime,
        Domain.GameData.GameDataCatalog gameData,
        Admission.IHelloAdmissionHook admission,
        ServerOptions options,
        CancellationToken cancellationToken,
        ILogger logger,
        Social.ChatRuntime chat,
        Social.SocialRuntime social,
        Social.OnlinePresenceRegistry presence,
        SessionRegistry sessions,
        AccountAbuse.PostgresAccountRiskRepository accountRisk,
        AccountAbuse.AccountRiskRequestContext riskContext,
        HttpContext context,
        Guid accountId,
        bool isBot,
        DisconnectGraceRegistry grace,
        Social.IPlayerPresenceStatsService presenceStats)
    {
        using var messageReader = new PlayerWebSocketMessageReader(options.MaxWebSocketMessageBytes);
        long lastInboundSequence = 0;
        var paceTracker = new CommandPaceTracker();
        var handshaken = false;
        using var handshakeDeadline = new CancellationTokenSource();
        var deadlineTask = Task.Delay(TimeSpan.FromSeconds(10), handshakeDeadline.Token).ContinueWith(task =>
        {
            if (!task.IsCanceled) session.RequestClose(1008, "握手超时。");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var displayName = string.Empty;
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await messageReader.ReadAsync(socket, cancellationToken);
                if (message is null) return;
                if (!WireEnvelopeCodec.TryDecodeBorrowed(message.Value, out var envelope))
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

                // 单条消息处理兜底：大厅/角色卡/社交/历史回放等只读后端路径的瞬时 IO 故障不得
                // 升级为整条连接断开（外层仅捕获取消与 WebSocket 异常）。回错误帧后继续接收；
                // 取消仍向外传播，出站信道已完成时静默收尾，由既有拆除流程统一处理。
                try
                {
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
                        // 握手期限只约束「客户端首次发言」：合法 Hello 到达即解除——
                        // 后续风险观测/准入/快照构建是服务端耗时，不能让期限在握手途中关通道
                        // （否则 RequestClose 与 QueueJsonAsync 竞态产生 ChannelClosedException 噪音）。
                        handshakeDeadline.Cancel();
                        // 防多开准入裁决：配额先于顶替，拒绝时通知帧随关闭码一并下发。
                        AccountAbuse.AccountRiskSignals riskSignals;
                        try
                        {
                            riskSignals = riskContext.Collect(context,
                                new AccountAbuse.AccountRiskClientSignals(hello.DeviceConfigHash, hello.DeviceId, null, hello.DeviceFingerprint), persistCookie: false);
                        }
                        catch (ArgumentException)
                        {
                            await accountRisk.RecordInvalidSignalsAsync("handshake", accountId, cancellationToken);
                            session.RequestClose(4007, "设备校验信息无效。");
                            return;
                        }
                        var riskPolicy = await accountRisk.LoadPolicyAsync(cancellationToken);
                        var riskDecision = await accountRisk.ObserveLoginAsync(accountId, riskSignals, cancellationToken, riskPolicy, "handshake");
                        if (!riskDecision.Allowed)
                        {
                            var closeCode = riskDecision.ErrorCode == AccountAbuse.AccountRiskErrorCodes.RiskSignalRequired ? 4007 : 4006;
                            session.RequestClose(closeCode, riskDecision.Message ?? "账号关联超限。");
                            return;
                        }
                        // 设备标识优先，避免相同配置的独立设备被合并为同一在线额度主体。
                        // Android 移动端硬件配置高度集中，严禁回退到 DeviceConfigHash，避免把全国同款手机塞入同一个在线配额桶。
                        var isAndroid = AccountAbuse.AccountRiskRequestContext.IsAndroid(context);
                        var quotaKey = riskSignals.DeviceId ?? (isAndroid ? null : riskSignals.DeviceConfigHash) ?? hello.DeviceFingerprint;
                        var admit = admission.Admit(session, quotaKey, hello.ModuleDeclaration, riskPolicy);
                        if (!admit.Accepted)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId,
                                new ErrorDto(admit.ErrorCode!, admit.Message ?? "连接被拒绝。", envelope.RequestId.ToString("D")), cancellationToken);
                            session.RequestClose(admit.CloseCode!.Value, admit.Message ?? string.Empty);
                            return;
                        }
                        if (admit.KickedSession is { } kicked)
                        {
                            // 踢线通知取号-编码-入队在被踢会话锁内完成：与其推送生产者的序号次序保持一致；
                            // 通知错误码/文案/关闭码由准入实现提供（主仓库不持有任何防线常量）。
                            lock (kicked.OutboundSync)
                            {
                                kicked.TrySend(EncodeJson(WireMessageType.Error, Guid.NewGuid(), kicked.NextOutboundSequence(),
                                    new ErrorDto(admit.KickNoticeErrorCode ?? "SESSION_REPLACED",
                                        admit.KickNoticeMessage ?? "当前连接已被其他连接替换。", null)));
                            }
                            kicked.RequestClose(admit.KickCloseCode ?? 1000, "Kicked by a newer session.");
                            // 等待旧会话完成拆除再加载 Actor：杜绝新旧会话并发驱动同一聚合、
                            // 以及旧会话 finally 误删新加载 Actor 的竞态。
                            try { await kicked.TeardownCompleted.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
                            catch (TimeoutException) { logger.LogWarning("被顶替会话拆除超时，继续准入 playerId={PlayerId}", session.PlayerId); }
                        }
                        var serverVersion = typeof(WebSocketEndpoint).Assembly
                            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                            .FirstOrDefault()?.InformationalVersion ?? "unknown";
                        var initialStats = await presenceStats.GetStatsAsync(cancellationToken);
                        var tabGuardConfig = admission.TabGuardEnabled(riskPolicy) ? new AntiMultiOpenConfigDto(true) : null;
                        // HelloAck 信封与载荷共用入队时分配的序号。
                        await session.SendReliableAsync((envelope.RequestId, serverVersion, gameData.Fingerprint, tabGuardConfig, initialStats),
                            static (target, data) =>
                            {
                                var sequence = target.NextOutboundSequence();
                                target.TrySend(EncodeJson(WireMessageType.HelloAck, data.RequestId, sequence,
                                    new HelloResponse(data.serverVersion, ProtocolConstants.CurrentVersion, data.Fingerprint, sequence,
                                        data.tabGuardConfig, data.initialStats)));
                            }, cancellationToken);
                        // 重连取消在途宽限：未触发的定时作废；收尾已在执行则等待完成，保证与装载串行（§14.3）。
                        await grace.CancelAsync(session.PlayerId, cancellationToken);
                        await using (var connectionInventory = await social.PrepareConnectionAsync(session.PlayerId, cancellationToken))
                        {
                        var initial = await runtime.AttachConnectionAsync(session.PlayerId, cancellationToken);
                        displayName = initial.DisplayName;
                        // 握手数据获取并行化：快照编码（CPU）、社交/聊天/存在感（独立 IO 与内存计算）互不依赖，
                        // 总耗时取最大而非求和；下行发送顺序保持 HelloAck → Snapshot → LogBatch → SocialState → ChatHistory 不变。
                        var handshakeInspect = session.PendingInspectedUnit;
                        var inspectAttributesProjected = false;
                        var snapshotTask = Task.Run(
                            () => ProtocolSnapshotMapper.ToSnapshot(initial, gameData.Fingerprint, gameData, handshakeInspect, () => inspectAttributesProjected = true),
                            cancellationToken);
                        var socialTask = social.BuildStateAsync(session.PlayerId, null, cancellationToken);
                        var worldChatTask = chat.LoadHistoryAsync(session.PlayerId, new ChatHistoryRequestDto(ChatChannels.World), cancellationToken);
                        var nearbyChatTask = chat.LoadHistoryAsync(session.PlayerId, new ChatHistoryRequestDto(ChatChannels.Nearby), cancellationToken);
                        var mail = context.RequestServices.GetService<Social.MailRuntime>();
                        var mailSummaryTask = mail is null
                            ? ValueTask.FromResult(new MailSummaryDto(0, 0, false))
                            : mail.GetSummaryAsync(session.PlayerId, isBot, session.AccountCreatedAt, cancellationToken);
                        var checkin = context.RequestServices.GetService<Checkin.CheckinRuntime>();
                        var checkinSummaryTask = checkin is null
                            ? ValueTask.FromResult(new CheckinSummaryDto(false, string.Empty, 0, 0, 0, null, IsBot: isBot))
                            : checkin.GetSummaryAsync(session.PlayerId, isBot, cancellationToken);
                        // 在线存在感登记：世界/附近广播与好友在线状态都依赖它；登记是独立副作用，与上方读取并行。
                        // 迁地途中（EdgeId 非空）按「不在节点」登记，与 UpdateNodeAsync 口径一致。
                        var presenceTask = presence.RegisterAsync(session.PlayerId, displayName,
                            initial.State.World.EdgeId is null ? initial.State.World.NodeId : null,
                            initial.State.Progression.RealmLevel, 0, initial.State.Settings.Avatar, isBot, cancellationToken);
                        var initialSnapshot = await snapshotTask;
                        await session.SendReliableAsync((initialSnapshot, inspectAttributesProjected, handshakeInspect),
                            static (target, data) =>
                            {
                                if (!target.TrySend(EncodeJson(WireMessageType.Snapshot, Guid.NewGuid(), target.NextOutboundSequence(), data.initialSnapshot))) return;
                                if (data.inspectAttributesProjected) target.MarkInspectedAttributesDelivered(data.handshakeInspect);
                                target.CommitFullSnapshot(data.initialSnapshot);
                            }, cancellationToken);
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
                        var mailSummary = await mailSummaryTask;
                        if (mail is not null)
                            await QueueJsonAsync(session, WireMessageType.MailSummary, Guid.NewGuid(), mailSummary, cancellationToken);
                        var checkinSummary = await checkinSummaryTask;
                        if (checkin is not null)
                            await QueueJsonAsync(session, WireMessageType.CheckinSummary, Guid.NewGuid(), checkinSummary, cancellationToken);
                        handshaken = true;
                        }
                        break;
                    case WireMessageType.Ping:
                        if (!handshaken)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("HANDSHAKE_REQUIRED", "请先完成 hello 握手。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        var pongStats = await presenceStats.GetStatsAsync(cancellationToken);
                        var playerState = runtime.TryGetLoaded(session.PlayerId)?.State;
                        var nodeDuelTeamsByBracket = playerState?.World.EdgeId is null && !string.IsNullOrEmpty(playerState?.World.NodeId)
                            ? runtime.CountNodeDuelCombatTeamsByBracket(playerState.World.NodeId)
                            : [0, 0, 0];
                        var currentDiff = playerState?.Actions.Count > 0 ? playerState.Actions[0].Intent.Difficulty : 0;
                        var nodeDuelTeams = nodeDuelTeamsByBracket[Domain.Combat.CombatDifficultyRules.GetBracket(currentDiff)];
                        await QueueJsonAsync(session, WireMessageType.Pong, envelope.RequestId,
                            new PongResponse(DateTimeOffset.UtcNow, pongStats.OnlineCount, pongStats.OfflineIdleCount, nodeDuelTeams, pongStats.OfflineCount, nodeDuelTeamsByBracket),
                            cancellationToken);
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
                        paceTracker = paceTracker.Record(System.Diagnostics.Stopwatch.GetTimestamp(), out var actionMachinePaced);
                        if (actionMachinePaced) await Task.Delay(200, cancellationToken);
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
                        // 优先插队落位队首、常规入队落位队尾：回执 Id 与剩余轮次必须取自同一目标实例。
                        var enqueuedAction = action.Priority
                            ? actionResult.Snapshot.State.Actions.FirstOrDefault()
                            : actionResult.Snapshot.State.Actions.LastOrDefault();
                        var actionResponse = actionResult.Success
                            ? new ActionIntentResponse(
                                action.RequestId,
                                enqueuedAction?.Id.ToString("D") ?? string.Empty,
                                action.ActionId,
                                DateTimeOffset.UtcNow,
                                enqueuedAction?.RemainingRepeats ?? action.RepeatCount,
                                Accepted: true,
                                Status: "queued",
                                Message: null)
                            : new ActionIntentResponse(
                                action.RequestId,
                                string.Empty,
                                action.ActionId,
                                DateTimeOffset.UtcNow,
                                0,
                                Accepted: false,
                                Status: "rejected",
                                Message: actionResult.Message);
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
                        paceTracker = paceTracker.Record(System.Diagnostics.Stopwatch.GetTimestamp(), out var commandMachinePaced);
                        if (commandMachinePaced) await Task.Delay(200, cancellationToken);
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
                        await session.SendReliableAsync((commandEnvelope.RequestId, EnvelopeId: envelope.RequestId, commandResult, gameData),
                            static (target, data) => EnqueueCommandResult(target, data.RequestId, data.EnvelopeId, data.commandResult, data.gameData),
                            cancellationToken);
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
                        var senderRealmLevel = runtime.TryGetLoaded(session.PlayerId)?.State.Progression.RealmLevel ?? 1;
                        var chatOutcome = await chat.SendAsync(session.PlayerId, displayName, chatSend, senderRealmLevel, cancellationToken);
                        if (chatOutcome.Status != Social.ChatSendStatus.Ok)
                        {
                            var (chatErrorCode, chatErrorMessage) = chatOutcome.Status switch
                            {
                                Social.ChatSendStatus.RateLimited => ("CHAT_RATE_LIMITED", "发言过于频繁，请稍后再试。"),
                                Social.ChatSendStatus.InvalidBody => ("CHAT_INVALID_BODY", "消息内容为空或超过长度上限。"),
                                Social.ChatSendStatus.QuoteUnavailable => ("CHAT_QUOTE_UNAVAILABLE", "引用的消息已不可用。"),
                                _ => ("CHAT_CHANNEL_UNAVAILABLE", "该传音频道当前不可用。")
                            };
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto(chatErrorCode, chatErrorMessage, envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        await CompleteChatSendAsync(session, sessions, envelope.RequestId, chatOutcome, cancellationToken);
                        break;
                    case WireMessageType.ChatLikeSet:
                        if (!handshaken)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("HANDSHAKE_REQUIRED", "请先完成 hello 握手。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        if (!WireEnvelopeCodec.TryDeserializeJson<ChatLikeSetDto>(envelope, out var chatLike) || chatLike is null)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("CHAT_LIKE_UNAVAILABLE", "无法为该消息设置点赞。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        var likeOutcome = await chat.SetLikeAsync(session.PlayerId, chatLike, cancellationToken);
                        if (likeOutcome.Update is not { } likeUpdate || likeOutcome.Recipients is not { } likeRecipients)
                        {
                            var (likeErrorCode, likeErrorMessage) = likeOutcome.Status == Social.ChatLikeStatus.RateLimited
                                ? ("CHAT_RATE_LIMITED", "点赞过于频繁，请稍后再试。")
                                : ("CHAT_LIKE_UNAVAILABLE", "无法为该消息设置点赞。");
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto(likeErrorCode, likeErrorMessage, envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        BroadcastChatPayload(sessions, WireMessageType.ChatLikeChanged, likeUpdate, likeRecipients, session.PlayerId, envelope.RequestId);
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
                                new ErrorDto(SocialErrorCode(socialOutcome.Failure), SocialErrorMessage(socialOutcome.Failure, socialOutcome.Message), envelope.RequestId.ToString("D")), cancellationToken);
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
                    case WireMessageType.PlayerCardRequest:
                        if (!handshaken)
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("HANDSHAKE_REQUIRED", "请先完成 hello 握手。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        if (!WireEnvelopeCodec.TryDeserializeJson<PlayerCardRequestDto>(envelope, out var cardRequest) || cardRequest is null
                            || !Guid.TryParse(cardRequest.RequestId, out var cardRequestId) || cardRequestId != envelope.RequestId
                            || !Guid.TryParse(cardRequest.PlayerId, out _))
                        {
                            await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("INVALID_PLAYER_CARD", "角色信息卡请求无效。", envelope.RequestId.ToString("D")), cancellationToken);
                            continue;
                        }
                        // 查询应答必达：弹窗加载态等不回可丢帧兜底；错误语义经载荷 error 字段下发。
                        await session.SendJsonAsync(WireMessageType.PlayerCard, envelope.RequestId,
                            await social.BuildPlayerCardAsync(session.PlayerId, cardRequest, cancellationToken), cancellationToken);
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
                        // 火忘帧无回执：登记武装一次性属性交付，并立即尝试投递一帧带按需属性的增量快照；
                        // 倒地停手或无战斗事件时立即交付，无需等待下一时钟边界；
                        // 帧被慢消费者丢弃则保留武装重投，客户端按 enemyId 缓存复用无需重复下发。
                        session.SetInspectedUnit(inspect.Side is null ? null : inspect);
                        if (inspect.Side is not null && !session.HandshakeSnapshotPending)
                        {
                            var inspectSnapshot = runtime.TryGetLoaded(session.PlayerId);
                            if (inspectSnapshot is not null)
                                TryPushDelta(session, inspectSnapshot, gameData, null);
                        }
                        break;
                    default:
                        await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId, new ErrorDto("UNSUPPORTED_MESSAGE", "不支持的消息类型。"), cancellationToken);
                        break;
                }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (ChannelClosedException) { }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "WebSocket 消息处理失败，已回错误帧并保持连接 playerId={PlayerId} messageType={MessageType}",
                        session.PlayerId, envelope.MessageType);
                    if (envelope.MessageType == WireMessageType.Hello)
                    {
                        // 握手阶段核心状态加载失败（如玩家实体装载崩溃），该会话无法继续保持合法状态；
                        // 必须主动关闭连接以防客户端进入未握手死循环，并促使客户端退避或重新连接。
                        session.RequestClose(1011, "握手处理失败。");
                        return;
                    }
                    try
                    {
                        await QueueJsonAsync(session, WireMessageType.Error, envelope.RequestId,
                            new ErrorDto("MESSAGE_FAILED", "该请求处理失败，请稍后重试。", envelope.RequestId.ToString("D")), cancellationToken);
                    }
                    catch (ChannelClosedException) { }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WebSocketException ex) { logger.LogDebug(ex, "WebSocket disconnected playerId={PlayerId}", session.PlayerId); }
        finally
        {
            handshakeDeadline.Cancel();
            await deadlineTask;
        }
    }

    /// <summary>普通发言成功：其他会话照常广播；发言者仅收到与原请求关联的可靠回显。</summary>
    internal static ValueTask CompleteChatSendAsync(WebSocketSession sender, SessionRegistry sessions, Guid requestId,
        Social.ChatSendOutcome outcome, CancellationToken cancellationToken)
    {
        BroadcastChat(sessions, outcome, skipPlayerId: sender.PlayerId);
        return QueueJsonAsync(sender, WireMessageType.ChatMessage, requestId, outcome.Message!, cancellationToken);
    }

    /// <summary>
    /// 把一条已落库的传音消息投递给作用域内未排除的在线会话。
    /// 同一消息载荷只序列化一次，全部收件人共享同一段载荷字节；requestId/sequence 属会话私有信封头。
    /// 投递走非阻塞 <see cref="WebSocketSession.TrySendDroppable"/>：慢客户端不能反向阻塞发言者的接收循环，
    /// 掉帧可由频道历史回放（最近 100 条）在重连或切频道时补齐。
    /// </summary>
    public static void BroadcastChat(SessionRegistry sessions, Social.ChatSendOutcome outcome, Guid? skipPlayerId = null)
    {
        if (outcome.Message is not { } message || outcome.Recipients is not { Count: > 0 } recipients) return;
        BroadcastChatPayload(sessions, WireMessageType.ChatMessage, message, recipients, skipPlayerId: skipPlayerId);
    }

    private static void BroadcastChatPayload<T>(SessionRegistry sessions, WireMessageType type, T payload, IReadOnlyList<Guid> recipients,
        Guid? actorId = null, Guid requestId = default, Guid? skipPlayerId = null)
    {
        // 载荷 JSON 与接收方无关：一次编码全体共享，避免按收件人重复序列化。
        var payloadBytes = (ReadOnlyMemory<byte>)JsonSerializer.SerializeToUtf8Bytes(payload, ProtocolJson.Options);
        foreach (var recipientId in recipients)
        {
            if (recipientId == skipPlayerId || !sessions.TryGet(recipientId, out var target)) continue;
            try { target.TrySendDroppableEnvelope(type, recipientId == actorId ? requestId : Guid.NewGuid(), payloadBytes); }
            catch { /* 单会话通道异常或已拆除，忽略以避免阻断剩余收件人 */ }
        }
    }

    /// <summary>必达回执在出站锁内编码、入队并更新基线；落后于基线的回执不能回退分段校验和。</summary>
    private static void EnqueueCommandResult(WebSocketSession session, string requestId, Guid envelopeRequestId,
        PlayerCommandResult result, Domain.GameData.GameDataCatalog gameData)
    {
        var snapshot = result.Snapshot;
        var pendingInspect = session.PendingInspectedUnit;
        var attributesProjected = false;
        var data = result.Data is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(result.Data, ProtocolJson.Options);
        var (previousSections, previousAssets) = session.ReadPushBaseline();
        if (snapshot.TouchedSections is { } touched && previousSections is not null && !session.IsOlderThanPushBaseline(snapshot.StateVersion))
        {
            var delta = ProtocolSnapshotMapper.BuildDelta(snapshot, gameData.Fingerprint, gameData, touched,
                previousSections, previousAssets, out var nextSections, out var nextAssets,
                pendingInspect, () => attributesProjected = true);
            if (Enqueue(CommandResultSnapshotForms.Delta, delta))
            {
                session.CommitPushBaseline(nextSections, nextAssets, snapshot.StateVersion);
                if (attributesProjected) session.MarkInspectedAttributesDelivered(pendingInspect);
            }
            return;
        }
        var full = ProtocolSnapshotMapper.ToSnapshot(snapshot, gameData.Fingerprint, gameData,
            pendingInspect, () => attributesProjected = true);
        if (!Enqueue(CommandResultSnapshotForms.Snapshot, full)) return;
        if (session.IsOlderThanPushBaseline(snapshot.StateVersion))
            session.MarkDeltaDropped();
        else
        {
            session.CommitFullSnapshot(full);
            session.CompleteDeltaResync();
            if (attributesProjected) session.MarkInspectedAttributesDelivered(pendingInspect);
        }

        bool Enqueue<T>(string form, T payload) => session.TrySend(EncodeJson(WireMessageType.CommandResult, envelopeRequestId,
            session.NextOutboundSequence(), new CommandResultDto(requestId, result.Success, result.Code, result.Message,
                snapshot.StateVersion, form, JsonSerializer.SerializeToElement(payload, ProtocolJson.Options), Data: data)));
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
        Social.SocialFailure.DouDiZhuUnavailable => "SOCIAL_DOUDIZHU_UNAVAILABLE",
        Social.SocialFailure.DouDiZhuInvalid => "SOCIAL_DOUDIZHU_INVALID",
        _ => "SOCIAL_FAILED"
    };

    /// <summary>社交失败原因到用户可读文案的映射。</summary>
    private static string SocialErrorMessage(Social.SocialFailure failure, string? detail = null) => failure switch
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
        Social.SocialFailure.ValuationTooLow => "道行未达到队伍门槛。",
        Social.SocialFailure.InvalidPartyProfile => "队伍配置无效。",
        Social.SocialFailure.SeekingNotFound => "你没有进行中的队伍匹配。",
        Social.SocialFailure.RateLimited => "操作过于频繁，请稍后再试。",
        Social.SocialFailure.ChannelUnavailable => "该传音频道当前不可用。",
        Social.SocialFailure.DouDiZhuUnavailable => detail ?? "对弈暂不可操作，请确认当前无进行中牌局且持有不少于十倍底注的灵石。",
        Social.SocialFailure.DouDiZhuInvalid => detail ?? "斗地主操作无效或牌局已更新，请重试。",
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
        // 重新获取完整快照、最近日志与社交状态。传音广播不经过此处；ChatMessage 在此仅用于发言者的可靠回显。
        // 取号-编码-入队由会话方法在 OutboundSync 内整体完成：通道物理序恒等于序号递增序。
        if (messageType is WireMessageType.SnapshotDelta or WireMessageType.LogBatch or WireMessageType.SocialState)
        {
            session.TrySendJsonDroppable(messageType, requestId, payload);
            return ValueTask.CompletedTask;
        }
        return session.SendJsonAsync(messageType, requestId, payload, cancellationToken);
    }

    /// <summary>
    /// 命令发包机械时钟即时监测器：统计连续指令到达间隔的标准差，抓出死循环 setInterval 脚本。
    /// </summary>
    private readonly struct CommandPaceTracker
    {
        private readonly long[] _timestamps;
        private readonly int _head;
        private readonly int _count;

        public CommandPaceTracker()
        {
            _timestamps = new long[6];
            _head = 0;
            _count = 0;
        }

        private CommandPaceTracker(long[] timestamps, int head, int count)
        {
            _timestamps = timestamps;
            _head = head;
            _count = count;
        }

        public CommandPaceTracker Record(long nowTicks, out bool isMachinePacing)
        {
            var nextTimestamps = _timestamps ?? new long[6];
            var nextHead = (_head + 1) % 6;
            nextTimestamps[nextHead] = nowTicks;
            var nextCount = Math.Min(6, _count + 1);

            isMachinePacing = false;
            if (nextCount >= 6)
            {
                Span<double> intervals = stackalloc double[5];
                double sum = 0;
                var prev = nextTimestamps[(_head + 6 - 5 + 1) % 6];
                for (var i = 0; i < 5; i++)
                {
                    var curr = nextTimestamps[(_head + 6 - 5 + 1 + i + 1) % 6];
                    var deltaMs = (curr - prev) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    intervals[i] = deltaMs;
                    sum += deltaMs;
                    prev = curr;
                }

                var mean = sum / 5.0;
                // 高频机械时钟区间（单次 50ms ~ 2500ms）
                if (mean <= 2500.0 && mean >= 50.0)
                {
                    double varianceSum = 0;
                    for (var i = 0; i < 5; i++)
                    {
                        var diff = intervals[i] - mean;
                        varianceSum += diff * diff;
                    }
                    var stdDev = Math.Sqrt(varianceSum / 5.0);
                    if (stdDev < 4.0)
                        isMachinePacing = true;
                }
            }

            return new CommandPaceTracker(nextTimestamps, nextHead, nextCount);
        }
    }
}
