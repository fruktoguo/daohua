using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Market;
using Domain.Persistence;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using Protocol;
using Server.Clean;
using Server.Clean.Social;

namespace Server;

/// <summary>
/// Clean 宿主到 ECS 世界的单写者适配边界。鉴权、协议、SectionCodec、资产事务和
/// durable outbox 仍由宿主提供；Entity 只保存长期领域状态，所有请求都经玩家锁串行进入 Fiber。
/// </summary>
public sealed partial class EcsPlayerRuntimeAdapter(
    EcsGameRuntime ecs,
    IPlayerStateRepository sections,
    IAssetBalanceRepository assets,
    IEconomyTransactionStore economy,
    GameDataCatalog gameData,
    ILogger<EcsPlayerRuntimeAdapter> logger,
    IPlayerSimulationClock? simulationClock = null,
    ICommandReceiptStore? commandReceipts = null,
    ISimulationTransactionIdentity? simulationTransactionIdentity = null,
    NodeBuildingRegistry? nodeBuildings = null,
    MarketRuntime? marketRuntime = null,
    IAccountRepository? accounts = null,
    IPlayerOwnershipStore? ownership = null,
    Server.Clean.Social.IInvitationRepository? invitations = null,
    Server.Clean.Social.ISocialRepository? socialRepository = null,
    IActionParticipationRuntime? participationRuntime = null,
    IParticipationSessionStore? participationSessions = null,
    Server.Clean.Social.OnlinePresenceRegistry? presence = null) : IPlayerRuntime
{
    private readonly ConcurrentDictionary<Guid, PlayerRuntimeSnapshot> _loaded = new();
    // 钱包以存储余额为权威：ECS 库存组件由它同步重建，市场等外部入账经失效标记触发重读。
    private readonly ConcurrentDictionary<Guid, Dictionary<AssetKey, AssetBalanceRecord>> _wallets = new();
    // 每分段首次置脏时刻：FlushOwnedAsync 按 15s 脏龄去抖，没到龄的分段不进 durable 队列。
    private readonly ConcurrentDictionary<Guid, Dictionary<PlayerSectionKind, DateTimeOffset>> _dirtyAt = new();
    // 已提交命令待落库的回执：随下一次 section 入队同事务写入，保证回执不先于状态生效。
    private readonly ConcurrentDictionary<Guid, List<CommandReceipt>> _pendingReceipts = new();
    // 钱包失效标记（值无意义，键存在即需重读）：市场外部入账 NotifyMarketWalletCredited 只负责打标。
    private readonly ConcurrentDictionary<Guid, byte> _walletReloadRequired = new();
    // COMMIT 无法精确重放确认时保留租约与错误证据，拒绝装载、推进和刷盘，防止不确定终态覆盖 durable 状态。
    private readonly ConcurrentDictionary<Guid, EconomyCommitOutcomeUnknownException> _fencedCommits = new();
    private readonly ConcurrentDictionary<Guid, PlayerLogBuffer> _logs = new();
    // 玩家级单写者门：命令/装载/刷盘/卸载统一经此串行进入 Fiber，不设全局加载门。
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _playerGates = new();
    // 进程内请求幂等窗（上限 1024/玩家）：跨重连去重由持久化 command_receipts 兜底。
    private readonly ConcurrentDictionary<Guid, RecentRequests> _recentRequests = new();
    // 上次推送时的属性流水号：命令后用差值判定 WireAttributesChanged，驱动网络层是否发属性增量。
    private readonly ConcurrentDictionary<Guid, long> _attributeRevisions = new();
    // 近期已发布版本滑窗（上限 RecentPublished.Keep）：乐观并发校验在命令收包瞬间取样，
    // 客户端 expected 命中窗口内任一已发布版本即放行——玩家门等待与门内对账期间的服务端推进
    // 都不构成过期证据；只有声称从未发布的更晚版本、或落后滑窗的真正过期请求才判冲突。
    private readonly ConcurrentDictionary<Guid, RecentPublished> _publishedVersions = new();

    /// <summary>单玩家近期已发布版本集合：单调记录 Latest，滑窗保留最近 Keep 条供命中判断。</summary>
    private sealed class RecentPublished
    {
        public const int Keep = 4;
        public long Latest;
        private readonly Queue<long> _order = new();
        private readonly HashSet<long> _set = new();

        /// <summary>登记一条已发布版本；可对同版本幂等，可容忍乱序（Latest 永不回退）。</summary>
        public void Record(long version)
        {
            lock (_order)
            {
                Latest = Math.Max(Latest, version);
                if (_set.Add(version)) _order.Enqueue(version);
                while (_order.Count > Keep) _set.Remove(_order.Dequeue());
            }
        }

        /// <summary>判断期望版本是否为仍在滑窗内的已发布版本。</summary>
        public bool Contains(long version)
        {
            lock (_order) return _set.Contains(version);
        }
    }
    // 本实例在 player_runtime_leases 上的持有者标识：租约获取/续租/归还都以它 fence 防误删他实例租约。
    private readonly Guid _ownerInstanceId = Guid.NewGuid();
    private static readonly TimeSpan OwnershipLease = TimeSpan.FromSeconds(45);
    /// <summary>本实例已获取租约玩家的上次成功续租时间；键存在即表示持有租约。</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _leaseRenewedAt = new();
    // 不设全局加载门：玩家级 gate 与 shard owner gate 已提供单写者隔离。
    private readonly IPlayerSimulationClock _simulationClock = simulationClock ?? PlayerSimulationClock.FromEnvironment();
    private readonly ILogger<EcsPlayerRuntimeAdapter> _logger = logger;
    private const int LogBufferCapacity = 200;
    /// <summary>
    /// 单次批量研读的本数硬上限（同时受当前持有量约束）：仅作数域护栏，
    /// 结算按总量一次完成，本数不再影响执行代价，此处不承担性能意义。
    /// </summary>
    private const double MaxLearnQuantity = 1e12;
    private bool _disposed;

    private sealed record RuntimeNotification(Guid PlayerId, IReadOnlyList<LogEventDto>? Logs,
        PlayerRuntimeSnapshot? Snapshot = null);

    private sealed class RecentRequests
    {
        public Dictionary<Guid, (string Command, string PayloadHash)> Entries { get; } = [];
        public Queue<Guid> Order { get; } = new();

        public void Remember(Guid requestId, string command, string payloadHash)
        {
            Entries[requestId] = (command, payloadHash);
            Order.Enqueue(requestId);
            while (Order.Count > 1024) Entries.Remove(Order.Dequeue());
        }
    }

    /// <inheritdoc />
    public int LoadedCount => _loaded.Count;
    /// <inheritdoc />
    public int CountNodeDuelCombatTeams(string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return 0;
        var count = 0;
        var accountedSessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in _loaded.Values)
        {
            var state = snapshot.State;
            if (state.World.EdgeId is not null || state.World.NodeId != nodeId)
                continue;
            if (state.Actions.Count == 0)
                continue;
            var head = state.Actions[0];
            if (head.Status != ActionQueueStatus.Running || !head.Intent.PvpEnabled)
                continue;
            if (head.Participation is { Reference.SessionId: { Length: > 0 } sessionId } participation)
            {
                if (participation.EndsAtUnixSeconds > snapshot.LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d)
                {
                    if (accountedSessions.Add(sessionId))
                        count++;
                }
            }
            else if (head.Expedition is not null)
            {
                count++;
            }
        }
        return count;
    }
    /// <inheritdoc />
    public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated;
    /// <inheritdoc />
    public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted;
    /// <inheritdoc />
    public DateTimeOffset? NextAdvanceBoundaryUtc
    {
        get
        {
            var playerBoundary = ecs.NextAdvanceBoundaryUtc;
            var sessionBoundary = participationRuntime?.NextAdvanceBoundaryUtc;
            return playerBoundary is null ? sessionBoundary : sessionBoundary is null ? playerBoundary
                : playerBoundary < sessionBoundary ? playerBoundary : sessionBoundary;
        }
    }

    /// <summary>
    /// 装载玩家到内存：经玩家 gate 串行，先获取所有权租约（被他实例持有则 fail-closed），
    /// 再读取持久化分段与余额重建 ECS 实体、补算离线欠债后才转在线；玩家不存在抛 <see cref="KeyNotFoundException"/>。
    /// </summary>
    public async ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var notifications = new List<RuntimeNotification>();
        var gate = _playerGates.GetOrAdd(playerId, static _ => new SemaphoreSlim(1, 1));
        using (RuntimeInstrumentation.Measure("queue.actor_wait"))
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadOwnedAsync(playerId, notifications, cancellationToken).ConfigureAwait(false); }
        finally
        {
            gate.Release();
            await EmitNotificationsAsync(notifications).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 真实连接建立（WebSocket 握手）：玩家门内装载（含离线补算）后把在线状态置为 Online
    /// 并清空离线锚点，刷新驻留快照与已发布版本滑窗；与 LoadAsync 同样经玩家 gate 串行。
    /// </summary>
    public async ValueTask<PlayerRuntimeSnapshot> AttachConnectionAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var notifications = new List<RuntimeNotification>();
        var gate = _playerGates.GetOrAdd(playerId, static _ => new SemaphoreSlim(1, 1));
        using (RuntimeInstrumentation.Measure("queue.actor_wait"))
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await LoadOwnedAsync(playerId, notifications, cancellationToken).ConfigureAwait(false);
            if (_walletReloadRequired.ContainsKey(playerId))
            {
                await ReloadWalletAsync(playerId, cancellationToken).ConfigureAwait(false);
                snapshot = ToSnapshot(ecs.ExportSnapshot(playerId));
                _loaded[playerId] = snapshot;
            }
            if (snapshot.State.Presence != PresenceState.Online)
            {
                ecs.SetPresence(playerId, PresenceState.Online, _simulationClock.UtcNow);
                TrackDirty(playerId);
                snapshot = ToSnapshot(ecs.ExportSnapshot(playerId));
                _loaded[playerId] = snapshot;
                _publishedVersions.GetOrAdd(playerId, static _ => new RecentPublished()).Record(snapshot.StateVersion);
            }
            return snapshot;
        }
        finally
        {
            gate.Release();
            await EmitNotificationsAsync(notifications).ConfigureAwait(false);
        }
    }

    /// <summary>读取已加载玩家的最新快照；未加载返回 null（不触发装载）。</summary>
    public PlayerRuntimeSnapshot? TryGetLoaded(Guid playerId) =>
        _loaded.TryGetValue(playerId, out var snapshot) ? snapshot : null;

    private async ValueTask<PlayerRuntimeSnapshot> LoadOwnedAsync(Guid playerId,
        List<RuntimeNotification> notifications, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (playerId == Guid.Empty) throw new ArgumentException("PlayerId 不能为空。", nameof(playerId));
        ThrowIfCommitFenced(playerId);
        if (_loaded.TryGetValue(playerId, out var cached)) return cached;

        // 多实例写保护闸门：未抢到 player_runtime_leases 租约即 fail-closed，不进入加载。
        if (ownership is not null
            && !await ownership.AcquireAsync(playerId, _ownerInstanceId, OwnershipLease, cancellationToken).ConfigureAwait(false))
            throw new PlayerOwnershipLostException(playerId);
        _leaseRenewedAt[playerId] = DateTimeOffset.UtcNow;
        using var loadMeasurement = RuntimeInstrumentation.Measure("lifecycle.player_load");
        try
        {
            // 租约取得后的读取、解码与实体装配都属于加载边界，任何一步失败都必须清理部分状态。
            var bundle = await sections.LoadAsync(playerId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"玩家不存在：{playerId:D}");
            var state = bundle.Head.StateVersion > 0 || bundle.Sections.Count > 0
                ? SectionCodec.DecodeAggregate(bundle)
                : PlayerAggregate.Create(new PlayerId(playerId), new AccountId(bundle.Identity.AccountId),
                    bundle.Identity.DisplayName, bundle.Identity.CreatedAt, gameData.Fingerprint).ExportState();
            var balanceRows = await assets.LoadBalancesAsync(playerId, cancellationToken).ConfigureAwait(false);
            var balances = balanceRows.Select(row => new AssetBalance(
                new PlayerId(playerId), new AssetKey(new ItemId(row.AssetId), row.Variant), row.Quantity, row.Version)).ToArray();
            ecs.CreatePlayer(state, balances);
            if (bundle.Sections.Count == 0)
                foreach (var section in Enum.GetValues<PlayerSectionKind>()) ecs.MarkDirty(playerId, section);
            _wallets[playerId] = balanceRows.ToDictionary(row => new AssetKey(new ItemId(row.AssetId), row.Variant));
            _dirtyAt[playerId] = [];
            _pendingReceipts[playerId] = [];
            var before = ToSnapshot(ecs.ExportSnapshot(playerId));
            IReadOnlyList<LogEventDto>? logs = null;
            var target = _simulationClock.UtcNow;
            await RefreshNodeBuildingsAsync(playerId, before.State.World, target, cancellationToken).ConfigureAwait(false);
            before = ToSnapshot(ecs.ExportSnapshot(playerId));
            // 先消费会话权威结算，再推进个人行动；成员从不恢复同场战斗。
            await ReconcileParticipationOwnedAsync(playerId, target, notifications, cancellationToken, initialLoad: true).ConfigureAwait(false);
            before = ToSnapshot(ecs.ExportSnapshot(playerId));
            TrackDirty(playerId);
            // 守卫必须用对账后的实体时刻：收件箱消费已把 LastSimulatedAt 推进到最新结算时刻，
            // 可能已超过 target——若仍按装载前的持久化时刻判断，补推会因目标倒退而抛异常。
            if (target > before.State.LastSimulatedAt)
            {
                var loadDirtyBefore = ecs.GetDirtySections(playerId);
                await ecs.RunPlayerOnceAsync(playerId, target, cancellationToken).ConfigureAwait(false);
                var advanced = ToSnapshot(ecs.ExportSnapshot(playerId));
                logs = (await CommitSimulationAsync(playerId, before, advanced, null, loadDirtyBefore, cancellationToken).ConfigureAwait(false)).Logs;
            }
            // 离线收益先完成事务；装载本身不改变在线状态，上线由 AttachConnectionAsync 驱动。
            TrackDirty(playerId);
            var snapshot = ToSnapshot(ecs.ExportSnapshot(playerId));
            _loaded[playerId] = snapshot;
            // 装载快照经握手回执即对外可见：记入已发布滑窗，避免重连后首条命令撞上批推进窗口。
            _publishedVersions.GetOrAdd(playerId, static _ => new RecentPublished()).Record(snapshot.StateVersion);
            _attributeRevisions[playerId] = ecs.AttributeRevision(playerId);
            RuntimeInstrumentation.Count("lifecycle.player_loaded");
            notifications.Add(new RuntimeNotification(playerId, logs));
            return snapshot;
        }
        catch (Exception loadError)
        {
            if (_fencedCommits.ContainsKey(playerId)) throw;
            ecs.RemovePlayer(playerId);
            // 成功提交或装载对账可能已暂存 _loaded；不能留下“缓存已加载、实体已移除”的假状态。
            _loaded.TryRemove(playerId, out _);
            _wallets.TryRemove(playerId, out _);
            _logs.TryRemove(playerId, out _);
            _dirtyAt.TryRemove(playerId, out _);
            _pendingReceipts.TryRemove(playerId, out _);
            _walletReloadRequired.TryRemove(playerId, out _);
            _recentRequests.TryRemove(playerId, out _);
            _attributeRevisions.TryRemove(playerId, out _);
            _publishedVersions.TryRemove(playerId, out _);
            if (ownership is not null)
            {
                try { await ownership.ReleaseAsync(playerId, _ownerInstanceId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception releaseError) { throw new AggregateException("玩家装载失败且租约归还失败。", loadError, releaseError); }
            }
            _leaseRenewedAt.TryRemove(playerId, out _);
            throw;
        }
    }

    /// <summary>
    /// 执行一条玩家领域命令：经玩家 gate 串行进入 Fiber，依次做幂等去重（进程内窗口 + 持久化回执）、
    /// 乐观版本校验、命令分发与持久化提交；失败不抛异常而以错误码返回（领域违例、所有权丢失等）。
    /// </summary>
    /// <param name="playerId">目标玩家。</param>
    /// <param name="command">领域命令名（如 action.enqueue、market.place、player.presence）。</param>
    /// <param name="payload">命令载荷；含 requestId 字段时必须与 <paramref name="requestId"/> 一致。</param>
    /// <param name="expectedStateVersion">调用方期望的状态版本；大于 0 且与当前不符时返回 STATE_VERSION_CONFLICT。</param>
    /// <param name="requestId">请求幂等标识；空 GUID 直接返回 INVALID_REQUEST_ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload,
        long expectedStateVersion, Guid requestId, CancellationToken cancellationToken = default)
    {
        // 收包瞬间的已发布版本滑窗是客户端视野上限：玩家门等待与门内对账期间的一切推进
        // 都发生在命令到达之后，不能把进门时刻的最新版本当作客户端本应看到的基线。
        var publishedAtReceipt = _publishedVersions.TryGetValue(playerId, out var published)
            ? published : null;
        var notifications = new List<RuntimeNotification>();
        try
        {
            var result = await ExecuteOwnedAsync(playerId, command, payload, expectedStateVersion, requestId,
                publishedAtReceipt, notifications, cancellationToken).ConfigureAwait(false);
            // 所有权失效的清理必须在命令返回后、锁外执行，避免同链路的下一条命令撞上已释放的 gate。
            if (result.Code == "PLAYER_OWNERSHIP_LOST") await EvictOwnershipLostAsync(playerId).ConfigureAwait(false);
            return result;
        }
        finally { await EmitNotificationsAsync(notifications).ConfigureAwait(false); }
    }

    private async ValueTask<PlayerCommandResult> ExecuteOwnedAsync(Guid playerId, string command, JsonElement payload,
        long expectedStateVersion, Guid requestId, RecentPublished? publishedAtReceipt,
        List<RuntimeNotification> notifications, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            var emptyRequestSnapshot = await LoadAsync(playerId, cancellationToken).ConfigureAwait(false);
            return new PlayerCommandResult(false, "INVALID_REQUEST_ID", "请求标识不能为空。", emptyRequestSnapshot);
        }
        var gate = _playerGates.GetOrAdd(playerId, static _ => new SemaphoreSlim(1, 1));
        using (RuntimeInstrumentation.Measure("queue.actor_wait"))
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await LoadOwnedAsync(playerId, notifications, cancellationToken).ConfigureAwait(false);
            try
            {
                await RenewOwnershipLeaseIfDueAsync(playerId, cancellationToken).ConfigureAwait(false);
            }
            catch (PlayerOwnershipLostException)
            {
                // 所有权已转移给其他实例：返回明确错误；实体逐出在锁外由 ExecuteAsync 执行。
                return new PlayerCommandResult(false, "PLAYER_OWNERSHIP_LOST", "玩家当前由其他服务实例持有，请稍后重试。", before);
            }
            await EnsureWalletFreshAsync(playerId, cancellationToken).ConfigureAwait(false);
            // 乐观并发基线取收包瞬间的已发布版本滑窗：随后的会话对账与批推进可能继续推高
            // StateVersion——那属于本命令管线之后的合法推进与在途推送；只有期望版本落在
            // 滑窗外的（声称未发布版本或落后整个窗口）才判定客户端真正过期。
            var entryStateVersion = before.StateVersion;
            await ReconcileParticipationOwnedAsync(playerId, _simulationClock.UtcNow, notifications, cancellationToken).ConfigureAwait(false);
            before = ToSnapshot(ecs.ExportSnapshot(playerId));
            if (payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("requestId", out var requestElement)
                && (requestElement.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(requestElement.GetString(), out var payloadRequestId)
                    || payloadRequestId != requestId))
                return new PlayerCommandResult(false, "REQUEST_ID_MISMATCH", "命令载荷中的请求 ID 无效或不一致。", before);
            var payloadHash = Convert.ToHexStringLower(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(payload, ServerJson.Options)));
            var recent = _recentRequests.GetOrAdd(playerId, static _ => new RecentRequests());
            if (recent.Entries.TryGetValue(requestId, out var remembered))
                return remembered.Command == command && remembered.PayloadHash == payloadHash
                    ? new PlayerCommandResult(true, "IDEMPOTENT_REPLAY", SuccessNotice(command), before)
                    : new PlayerCommandResult(false, "REQUEST_ID_CONFLICT", "请求 ID 已用于其他命令。", before);
            if (commandReceipts is not null && ShouldPersistReceipt(command))
            {
                var persisted = await commandReceipts.FindAsync(playerId, requestId, cancellationToken).ConfigureAwait(false);
                if (persisted is not null)
                {
                    if (persisted.Command != command || persisted.PayloadHash != payloadHash)
                        return new PlayerCommandResult(false, "REQUEST_ID_CONFLICT", "请求 ID 已用于其他命令。", before);
                    recent.Remember(requestId, command, payloadHash);
                    return new PlayerCommandResult(true, "IDEMPOTENT_REPLAY", SuccessNotice(command), before);
                }
            }
            if (expectedStateVersion > 0 && (publishedAtReceipt is { } published
                    ? !published.Contains(expectedStateVersion)
                    : entryStateVersion != expectedStateVersion))
                return new PlayerCommandResult(false, "STATE_VERSION_CONFLICT", "玩家状态版本已变化。", before);

            try
            {
                // 操作前分段脏流水号快照：命令结束后差集得出「本次操作触及分段」，
                // 供网络增量窄化编码范围（不替代持久化脏集合语义；戳号不复用，差集不会漏报）。
                var dirtyMarksBefore = ecs.GetDirtyMarks(playerId);
                var dirtySectionsBefore = ecs.GetDirtySections(playerId);
                IReadOnlySet<PlayerSectionKind>? touched = null;
                (Guid DuelId, Guid PeerId)? forfeit = null;
                IReadOnlyList<Server.Clean.Social.InvitationMilestoneGrant>? invitationGrants = null;
                List<LogEventDto>? tutorialSkipLogs = null;
                var receiptAlreadyCommitted = false;
                var wallet = WalletOf(playerId);
                switch (command)
                {
                    case "action.enqueue":
                    {
                        var request = JsonSerializer.Deserialize<ActionIntentRequest>(payload, ProtocolJson.Options)
                            ?? throw new JsonException("行动载荷为空。");
                        if (!Guid.TryParse(request.RequestId, out var actionRequestId) || actionRequestId != requestId)
                            throw new DomainException(DomainErrorCode.InvalidArgument, "请求标识不一致。");
                        var intent = new ActionIntent(new ActionId(request.ActionId), request.RepeatCount,
                            request.TargetNodeId, request.Parameters, request.PvpEnabled)
                            with { TargetItemId = request.TargetItemId, AuxItemId = request.AuxItemId };
                        // 行动参与会话由组织适配器投递，客户端只能入队普通行动。
                        // 参与期间仍禁止手动进入斗法撮合。
                        // 会话进行中禁止斗法撮合（总纲 §21.13：阵容冻结期间不可进入斗法）。
                        if (request.PvpEnabled && IsActionParticipationActive(before.State))
                            return new PlayerCommandResult(false, "IN_PARTY_EXPEDITION", "组队讨伐进行中，无法参与斗法撮合。", before);
                        if (gameData.Actions.TryGet(intent.ActionId, out var actionDefinition))
                        {
                            // 行动/配方须满足对应 T 级解锁等级要求（如 T2 需 18 级；境界与技艺等级二者满足其一即放行）
                            var techniqueId = actionDefinition.Technique;
                            var techniqueLevel = !string.IsNullOrEmpty(techniqueId)
                                && before.State.Loadout is { TechniqueExperiences: { } techExperiences }
                                && techExperiences.TryGetValue(techniqueId, out var exp)
                                    ? TechniqueCatalog.LevelFor(exp, gameData.RealmProgression)
                                    : 0;
                            var currentRealmLevel = before.State.Progression?.RealmLevel ?? 1;
                            if (!CraftingRecipeRules.IsActionUnlocked(actionDefinition, currentRealmLevel, techniqueLevel, gameData, out var requiredLevel))
                            {
                                var isRecipe = actionDefinition.Kind == ActionKind.Alchemy
                                    || actionDefinition.Technique is "alchemy" or "forging" or "talisman" or "formation";
                                var errorCode = isRecipe ? "RECIPE_LOCKED" : "ACTION_LOCKED";
                                var techniqueName = techniqueId switch
                                {
                                    "alchemy" => "炼丹",
                                    "forging" => "炼器",
                                    "talisman" => "符箓",
                                    "formation" => "阵法",
                                    "gathering" => "采集",
                                    "mining" => "采矿",
                                    _ => null
                                };
                                var techHint = techniqueName is not null ? $"或{techniqueName}达到 {requiredLevel} 级" : string.Empty;
                                var errorMessage = isRecipe
                                    ? $"尚未解锁该制造图鉴（需境界达到 {requiredLevel} 级{techHint}）。"
                                    : $"尚未达到该行动所需等级（需境界达到 {requiredLevel} 级{techHint}）。";
                                return new PlayerCommandResult(false, errorCode, errorMessage, before);
                            }
                            // 强化行动必须指定目标装备；缺 targetItemId 载荷无效，不入队。
                            if (actionDefinition.Kind == ActionKind.Enhancement && string.IsNullOrWhiteSpace(request.TargetItemId))
                                return new PlayerCommandResult(false, "INVALID_PAYLOAD", "强化行动必须指定目标装备。", before);
                            if (actionDefinition.AllowedNodeIds is { Count: > 0 }
                                && (before.State.World.EdgeId is not null || !actionDefinition.AllowedNodeIds.Contains(before.State.World.NodeId)))
                                return new PlayerCommandResult(false, "WRONG_LOCATION", "当前位置不允许执行该行动。", before);
                            // 黑名单节点同样拒入队（常驻行动按地点裁剪）。
                            if (actionDefinition.ExcludedNodeIds is { Count: > 0 }
                                && before.State.World.EdgeId is null && actionDefinition.ExcludedNodeIds.Contains(before.State.World.NodeId))
                                return new PlayerCommandResult(false, "WRONG_LOCATION", "当前位置不允许执行该行动。", before);
                        }
                        var now = _simulationClock.UtcNow;
                        if (before.State.Actions.Count > 0 && now > before.State.LastSimulatedAt)
                            await ecs.RunPlayerOnceAsync(playerId, now, cancellationToken).ConfigureAwait(false);
                        touched = ecs.MutatePlayer(playerId, aggregate => aggregate.Enqueue(intent, now, gameData.Actions));
                        break;
                    }
                    case "action.remove":
                    {
                        if (!TryReadGuid(payload, "actionInstanceId", out var actionId))
                            throw new DomainException(DomainErrorCode.InvalidArgument, "行动实例标识无效。");
                        var wasHead = before.State.Actions.Count > 0 && before.State.Actions[0].Id == actionId;
                        if (!wasHead && before.State.Actions.All(action => action.Id != actionId))
                            return new PlayerCommandResult(false, "ACTION_NOT_FOUND", "行动不存在。", before);
                        // 进行中的讨伐会话行动只能由社交仲裁写散场时刻收尾，客户端不得摘除（总纲 §21.13）。
                        if (wasHead && IsActionParticipationActive(before.State))
                            return new PlayerCommandResult(false, "IN_PARTY_EXPEDITION", "组队讨伐进行中，不能取消会话行动。", before);
                        // 沿袭历史 OOP 运行时的次序基线：移除队首先把已流逝模拟结算掉，再进行弃战判定与移除。
                        if (wasHead && _simulationClock.UtcNow > before.State.LastSimulatedAt)
                            await ecs.RunPlayerOnceAsync(playerId, _simulationClock.UtcNow, cancellationToken).ConfigureAwait(false);
                        touched = ecs.MutatePlayer(playerId, aggregate =>
                        {
                            if (aggregate.Actions.Count > 0 && aggregate.Actions[0].Id == actionId)
                                forfeit = aggregate.ApplyDuelForfeit(_simulationClock.UtcNow, gameData.Duel);
                            aggregate.RemoveAction(actionId);
                        }, destroyEncounters: wasHead);
                        break;
                    }
                    case "queue.configure":
                    {
                        var hasLoop = payload.TryGetProperty("loopEnabled", out var loopEnabled);
                        var hasAutoTravel = payload.TryGetProperty("autoTravelEnabled", out var autoTravelEnabled);
                        var hasPaused = payload.TryGetProperty("paused", out var paused);
                        if ((hasLoop && loopEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            || (hasAutoTravel && autoTravelEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            || (hasPaused && paused.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            || (!hasLoop && !hasAutoTravel && !hasPaused))
                            return new PlayerCommandResult(false, "INVALID_PAYLOAD", "队列设置无效。", before);
                        if (hasPaused && paused.GetBoolean() != before.State.Settings.QueuePaused
                            && _simulationClock.UtcNow > before.State.LastSimulatedAt)
                            await ecs.RunPlayerOnceAsync(playerId, _simulationClock.UtcNow, cancellationToken).ConfigureAwait(false);
                        touched = ecs.MutatePlayer(playerId, aggregate =>
                        {
                            if (hasLoop) aggregate.SetLoopEnabled(loopEnabled.GetBoolean());
                            if (hasAutoTravel) aggregate.SetAutoTravelEnabled(autoTravelEnabled.GetBoolean());
                            if (hasPaused) aggregate.SetQueuePaused(paused.GetBoolean());
                        });
                        break;
                    }
                    case "queue.move":
                    {
                        if (!TryReadGuid(payload, "actionInstanceId", out var actionInstanceId)
                            || !payload.TryGetProperty("direction", out var directionElement)
                            || !directionElement.TryGetInt32(out var direction))
                            return new PlayerCommandResult(false, "INVALID_PAYLOAD", "行动移动参数无效。", before);
                        // 会话进行中不得把队首会话行动挤下/移走（会话行动必须在队首才有推进语义）。
                        if (IsActionParticipationActive(before.State)
                            && (before.State.Actions[0].Id == actionInstanceId
                                || (direction < 0 && before.State.Actions.Count > 1
                                    && before.State.Actions[1].Id == actionInstanceId)))
                            return new PlayerCommandResult(false, "IN_PARTY_EXPEDITION", "组队讨伐进行中，不能调整会话行动次序。", before);
                        // 沿袭历史 OOP 运行时的次序基线：先推进到当前，再按是否挪动队首做弃战判定。
                        if (_simulationClock.UtcNow > before.State.LastSimulatedAt)
                            await ecs.RunPlayerOnceAsync(playerId, _simulationClock.UtcNow, cancellationToken).ConfigureAwait(false);
                        var moved = false;
                        touched = ecs.MutatePlayer(playerId, aggregate =>
                        {
                            if (aggregate.Actions.Count > 0 && aggregate.Actions[0].Id == actionInstanceId)
                                forfeit = aggregate.ApplyDuelForfeit(_simulationClock.UtcNow, gameData.Duel);
                            moved = aggregate.MoveAction(actionInstanceId, direction);
                        });
                        if (!moved) return new PlayerCommandResult(false, "ACTION_NOT_FOUND", "行动不存在或已在边界位置。", before);
                        break;
                    }
                    case "queue.clear":
                    {
                        if (before.State.Actions.Count == 0)
                            return new PlayerCommandResult(false, "QUEUE_EMPTY", "队列已为空。", before);
                        // 会话进行中清空等于摘除会话行动：与 action.remove 同口径拒绝。
                        if (IsActionParticipationActive(before.State))
                            return new PlayerCommandResult(false, "IN_PARTY_EXPEDITION", "组队讨伐进行中，不能清空行动队列。", before);
                        // 与 action.remove 同序：先把已流逝模拟结算掉，再对队首斗法做弃战判定并整队清空。
                        if (_simulationClock.UtcNow > before.State.LastSimulatedAt)
                            await ecs.RunPlayerOnceAsync(playerId, _simulationClock.UtcNow, cancellationToken).ConfigureAwait(false);
                        touched = ecs.MutatePlayer(playerId, aggregate =>
                        {
                            forfeit = aggregate.ApplyDuelForfeit(_simulationClock.UtcNow, gameData.Duel);
                            aggregate.ClearActions();
                        }, destroyEncounters: true);
                        break;
                    }
                    case "queue.auto_use.configure":
                    {
                        var actionType = payload.TryGetProperty("actionType", out var actionTypeElement) ? actionTypeElement.GetString() : null;
                        var slotIndex = payload.TryGetProperty("slotIndex", out var slotIndexElement) && slotIndexElement.TryGetInt32(out var parsedSlot) ? parsedSlot : -1;
                        var itemId = payload.TryGetProperty("itemId", out var itemElement) && itemElement.ValueKind != JsonValueKind.Null ? itemElement.GetString() : null;
                        var quantity = payload.TryGetProperty("quantity", out var quantityElement) && quantityElement.TryGetDouble(out var parsedQuantity) ? parsedQuantity : 1d;
                        var enabled = payload.TryGetProperty("enabled", out var enabledElement) && enabledElement.ValueKind == JsonValueKind.True;
                        var group = payload.TryGetProperty("group", out var groupElement) ? groupElement.GetString() : null;
                        var barId = actionType?.Trim().ToLowerInvariant();
                        if (!Domain.GameData.AutoUseBar.IsKnown(barId))
                            return new PlayerCommandResult(false, "INVALID_ACTION_TYPE", "物品栏类型无效。", before);
                        ItemDefinition? slotItem = null;
                        // 自动使用槽仅收自动消耗品（category=consumable）：手动消耗品（秘籍/宝箱/礼包等）
                        // 即使误配 usableInActionTypes 也一律拒绝，防止永不触发的死槽位配置。
                        if (itemId is not null && (!gameData.Items.TryGetValue(new ItemId(itemId), out slotItem)
                            || slotItem.Category != ItemDefinition.CategoryConsumable
                            || slotItem.Consumable is null || !slotItem.Consumable.UsableInBars.Contains(barId!)))
                            return new PlayerCommandResult(false, "INVALID_CONSUMABLE", "物品不适用于该自动使用槽。", before);
                        if (group == AutoUseSlotState.SlotGroupPotion
                            && (barId != Domain.GameData.AutoUseBar.Combat
                                || itemId is not null && (slotItem!.DisplaySubCategory != ConsumableDefinition.CategoryPill
                                    || slotItem.Consumable!.IsContinuous)))
                            return new PlayerCommandResult(false, "INVALID_CONSUMABLE", "药品栏只能装配药品类即时消耗品。", before);
                        var autoActionType = barId!;
                        touched = ecs.MutatePlayer(playerId, aggregate =>
                            aggregate.ConfigureAutoUse(autoActionType, slotIndex, itemId, quantity, enabled, group));
                        break;
                    }
                    case "loadout.replace":
                    {
                        touched = ecs.MutatePlayer(playerId, aggregate => LoadoutCommand.Apply(payload, aggregate, wallet, gameData));
                        break;
                    }
                    case "ability.learn":
                    {
                        var itemId = payload.TryGetProperty("itemId", out var manualElement) && manualElement.ValueKind == JsonValueKind.String
                            ? manualElement.GetString() : null;
                        var isTome = itemId?.StartsWith("item.tome.", StringComparison.Ordinal) == true;
                        var isFragment = itemId?.StartsWith("item.fragment.", StringComparison.Ordinal) == true;
                        if (itemId is null || (!isTome && !isFragment) || !gameData.RawItems.ContainsKey(itemId))
                            return new PlayerCommandResult(false, "INVALID_MANUAL", "请选择完整功法秘籍或残卷。", before);
                        // 批量研读：quantity 缺省 1，语义与逐本点击等价（同一笔事务内按总量一次结算）。
                        var learnQuantity = payload.TryGetProperty("quantity", out var learnQuantityElement)
                            && learnQuantityElement.TryGetDouble(out var parsedLearnQuantity)
                            ? parsedLearnQuantity : 1d;
                        if (!double.IsFinite(learnQuantity) || learnQuantity < 1 || learnQuantity != Math.Floor(learnQuantity)
                            || learnQuantity > MaxLearnQuantity)
                            return new PlayerCommandResult(false, "INVALID_QUANTITY", "研读数量无效。", before);
                        var prefix = isTome ? "item.tome." : "item.fragment.";
                        var abilityId = new AbilityId("ability." + itemId[prefix.Length..]);
                        if (!gameData.Abilities.ContainsKey(abilityId.Value))
                            return new PlayerCommandResult(false, "INVALID_ABILITY", "秘籍对应功法不存在。", before);
                        var alreadyLearned = before.State.LearnedAbilities.Contains(abilityId);
                        if (!isTome && !alreadyLearned)
                            return new PlayerCommandResult(false, "ABILITY_NOT_LEARNED", "必须先习得对应功法才能研读残卷。", before);
                        await EnsureWalletFreshAsync(playerId, cancellationToken).ConfigureAwait(false);
                        // 重载后重建钱包视图：持有量校验必须读取刚同步的权威余额，不能用命令入口的旧快照。
                        wallet = WalletOf(playerId);
                        // 持有量前置校验：必须先于任何内存变更拒绝，否则会先跑完整批结算、再在事务提交时才发现余额不足。
                        if (wallet.Get(new AssetKey(new ItemId(itemId))) + 1e-9 < learnQuantity)
                            return new PlayerCommandResult(false, "INSUFFICIENT_ITEM", "秘籍或残卷数量不足。", before);
                        var experiencePerBook = isTome ? 10_000d : 500d;
                        touched = ecs.MutatePlayer(playerId, aggregate =>
                        {
                            if (!alreadyLearned) aggregate.LearnAbility(abilityId);
                            // 与逐本等价的总量注入：等级只由累计经验派生，N 本一次结算与 N 次单本注入同值。
                            // 执行代价恒为 O(1)——quantity 来自客户端，绝不能决定分片所有权门闩内的循环次数。
                            var experienceBooks = alreadyLearned ? learnQuantity : learnQuantity - 1;
                            if (experienceBooks > 0)
                                aggregate.GrantAbilityExperienceTotal(abilityId, experienceBooks * experiencePerBook);
                        });
                        var mutated = ToSnapshot(ecs.ExportSnapshot(playerId));
                        var learnReceipt = CreateReceipt(playerId, requestId, command, payloadHash, mutated.StateVersion);
                        await CommitPlayerAssetsAsync(playerId, before, dirtySectionsBefore,
                            new EconomyTransactionRequest(
                                requestId, $"ability-learn:{playerId:D}:{requestId:D}", command, playerId,
                                [new Server.Clean.AssetDelta(playerId, itemId, string.Empty, -learnQuantity)], DateTimeOffset.UtcNow,
                                JsonSerializer.SerializeToUtf8Bytes(new { abilityId = abilityId.Value, quantity = learnQuantity })),
                            learnReceipt, cancellationToken).ConfigureAwait(false);
                        receiptAlreadyCommitted = true;
                        break;
                    }
                    case "trait.set":
                    {
                        var traitIds = new List<string>();
                        if (payload.TryGetProperty("traitIds", out var traitIdsElem) && traitIdsElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var elem in traitIdsElem.EnumerateArray())
                            {
                                if (elem.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(elem.GetString()))
                                    traitIds.Add(elem.GetString()!.Trim());
                            }
                        }
                        touched = ecs.MutatePlayer(playerId, aggregate => aggregate.Progression.SetTraits(traitIds));
                        break;
                    }
                    case "item.use":
                    {
                        var useItemId = payload.TryGetProperty("itemId", out var useItemElem) && useItemElem.ValueKind == JsonValueKind.String
                            ? useItemElem.GetString() : null;
                        var useQuantity = payload.TryGetProperty("quantity", out var useQtyElem) && useQtyElem.TryGetInt32(out var parsedQty)
                            ? parsedQty : 1;
                        if (string.IsNullOrWhiteSpace(useItemId) || useQuantity <= 0)
                            return new PlayerCommandResult(false, "INVALID_PAYLOAD", "物品或数量无效。", before);

                        if (string.Equals(useItemId, "item.awakening-stone", StringComparison.Ordinal))
                        {
                            var stoneKey = new ItemId("item.awakening-stone");
                            var ownedStones = wallet.Get(new AssetKey(stoneKey));
                            if (ownedStones + 1e-9 < useQuantity)
                                return new PlayerCommandResult(false, "INSUFFICIENT_RESOURCE", "觉醒石数量不足。", before);

                            touched = ecs.MutatePlayer(playerId, aggregate =>
                            {
                                for (var i = 0; i < useQuantity; i++)
                                {
                                    aggregate.Progression.AwakenTrait(Random.Shared);
                                }
                            });

                            var mutated = ToSnapshot(ecs.ExportSnapshot(playerId));
                            var useReceipt = CreateReceipt(playerId, requestId, command, payloadHash, mutated.StateVersion);
                            await CommitPlayerAssetsAsync(playerId, before, dirtySectionsBefore,
                                new EconomyTransactionRequest(
                                    requestId, $"item-use:{playerId:D}:{requestId:D}", command, playerId,
                                    [new Server.Clean.AssetDelta(playerId, stoneKey.Value, string.Empty, -useQuantity)], DateTimeOffset.UtcNow,
                                    JsonSerializer.SerializeToUtf8Bytes(new { itemId = stoneKey.Value, quantity = useQuantity })),
                                useReceipt, cancellationToken).ConfigureAwait(false);
                            receiptAlreadyCommitted = true;
                            break;
                        }

                        return new PlayerCommandResult(false, "UNSUPPORTED_ITEM", "该物品不可直接使用。", before);
                    }
                    case "progression.breakthrough":
                    {
                        touched = ecs.MutatePlayer(playerId, aggregate => aggregate.Breakthrough(gameData.RealmProgression));
                        // 邀请里程碑：突破成功后按新境界等级发放到期奖励（仓储内幂等+同事务翻转标记）。
                        if (invitations is not null)
                        {
                            var realmAfter = ecs.ExportSnapshot(playerId).State.Progression.RealmLevel;
                            invitationGrants = await GrantInvitationMilestonesAsync(playerId, realmAfter, cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    }
                    case "world.travel":
                    {
                        var targetNodeId = payload.TryGetProperty("targetNodeId", out var targetNode) ? targetNode.GetString() : null;
                        if (string.IsNullOrWhiteSpace(targetNodeId))
                            return new PlayerCommandResult(false, "INVALID_TARGET", "目标世界节点无效。", before);
                        targetNodeId = targetNodeId.Trim();
                        var worldGraph = gameData.WorldGraph;
                        if (!worldGraph.ContainsNode(targetNodeId))
                            return new PlayerCommandResult(false, "NODE_NOT_FOUND", $"目标世界节点不存在：{targetNodeId}。", before);
                        if (worldGraph.TryGetNode(targetNodeId, out var targetNodeObj) && targetNodeObj.Hidden && targetNodeObj.RequiredConsciousness > 0)
                        {
                            var levels = before.State.Loadout.CoreAttributes.Levels(gameData.RealmProgression);
                            if (levels.Consciousness <= targetNodeObj.RequiredConsciousness)
                            {
                                return new PlayerCommandResult(false, "CONSCIOUSNESS_TOO_LOW",
                                    $"神识不足，无法感应并前往该地点（需神识大于 {targetNodeObj.RequiredConsciousness:0} 点）。", before);
                            }
                        }
                        var travelNow = _simulationClock.UtcNow;
                        if ((!string.Equals(before.State.World.NodeId, targetNodeId, StringComparison.Ordinal)
                                || before.State.World.EdgeId is not null)
                            && before.State.Actions.FirstOrDefault()?.Participation is { } travelParticipation)
                        {
                            await (participationRuntime ?? throw new InvalidOperationException("未装配行动参与权威宿主。"))
                                .EndAsync(travelParticipation.SessionId, travelNow, "member-node-changed", cancellationToken).ConfigureAwait(false);
                            await ReconcileParticipationOwnedAsync(playerId, travelNow, notifications, cancellationToken,
                                advanceSession: false).ConfigureAwait(false);
                            before = ToSnapshot(ecs.ExportSnapshot(playerId));
                            dirtySectionsBefore = ecs.GetDirtySections(playerId);
                            wallet = WalletOf(playerId);
                        }
                        if (travelNow > before.State.LastSimulatedAt)
                            await ecs.RunPlayerOnceAsync(playerId, travelNow, cancellationToken).ConfigureAwait(false);
                        touched = ecs.MutatePlayer(playerId, aggregate =>
                        {
                            static void RemoveStaleTravel(PlayerAggregate aggregate)
                            {
                                for (var i = 0; i < aggregate.Actions.Count; i++)
                                    if (aggregate.Actions[i].Intent.ActionId.Value == "world.travel")
                                    {
                                        aggregate.RemoveAction(aggregate.Actions[i].Id);
                                        break;
                                    }
                            }
                            if (string.Equals(aggregate.World.NodeId, targetNodeId, StringComparison.Ordinal)
                                && string.IsNullOrWhiteSpace(aggregate.World.EdgeId))
                            {
                                RemoveStaleTravel(aggregate);
                                return;
                            }
                            var plan = worldGraph.PlanFromPosition(aggregate.World.EdgeId,
                                aggregate.World.EdgeProgress, aggregate.World.NodeId, targetNodeId);
                            if (plan.Segments.Count == 0 && plan.TotalTravelSeconds <= 0)
                            {
                                RemoveStaleTravel(aggregate);
                                if (worldGraph.TryGetNode(targetNodeId, out var targetNodeObject))
                                    aggregate.TravelTo(targetNodeId, targetNodeObject.X, targetNodeObject.Y);
                                return;
                            }
                            var travelState = new TravelActionState(targetNodeId, plan.TotalTravelSeconds,
                                plan.EdgeIds, plan.Segments);
                            forfeit = aggregate.ApplyDuelForfeit(travelNow, gameData.Duel);
                            var enqueuedAt = aggregate.LastSimulatedAt > travelNow ? aggregate.LastSimulatedAt : travelNow;
                            aggregate.EnqueuePriority(new ActionIntent(new ActionId("world.travel"), 1, targetNodeId),
                                enqueuedAt, gameData.Actions, travelState);
                        });
                        break;
                    }
                    case "building.build":
                    {
                        if (nodeBuildings is null)
                            return new PlayerCommandResult(false, "BUILDING_UNAVAILABLE", "建筑服务未装配。", before);
                        var buildingId = payload.TryGetProperty("buildingId", out var buildingElement) && buildingElement.ValueKind == JsonValueKind.String
                            ? buildingElement.GetString() : null;
                        if (string.IsNullOrWhiteSpace(buildingId) || !gameData.Buildings.TryGetValue(buildingId, out var buildingDefinition))
                            return new PlayerCommandResult(false, "BUILDING_NOT_FOUND", "建筑定义不存在。", before);
                        if (before.State.World.EdgeId is not null)
                            return new PlayerCommandResult(false, "WRONG_LOCATION", "旅途中无法建造建筑。", before);
                        if (buildingDefinition.AvailableNodeIds is { Count: > 0 } && !buildingDefinition.AvailableNodeIds.Contains(before.State.World.NodeId))
                            return new PlayerCommandResult(false, "WRONG_LOCATION", "此地无法建造该建筑。", before);
                        if (before.State.Progression.RealmLevel < buildingDefinition.RequiredRealmLevel)
                            return new PlayerCommandResult(false, "BUILDING_LOCKED", "境界不足，尚未解锁该建筑。", before);
                        var buildNow = DateTimeOffset.UtcNow;
                        var buildResult = await nodeBuildings.StartConstructionAsync(before.State.World.NodeId, buildingDefinition, buildNow, cancellationToken).ConfigureAwait(false);
                        if (!buildResult.Success)
                            return new PlayerCommandResult(false, buildResult.ErrorCode ?? "BUILD_FAILED", buildResult.Message ?? "建造发起失败。", before);
                        await RefreshNodeBuildingsAsync(playerId, before.State.World, buildNow, cancellationToken).ConfigureAwait(false);
                        TrackDirty(playerId);
                        break;
                    }
                    case "building.upgrade":
                    {
                        if (nodeBuildings is null)
                            return new PlayerCommandResult(false, "BUILDING_UNAVAILABLE", "建筑服务未装配。", before);
                        var buildingId = payload.TryGetProperty("buildingId", out var upgradeElement) && upgradeElement.ValueKind == JsonValueKind.String
                            ? upgradeElement.GetString() : null;
                        if (string.IsNullOrWhiteSpace(buildingId) || !gameData.Buildings.ContainsKey(buildingId))
                            return new PlayerCommandResult(false, "BUILDING_NOT_FOUND", "建筑定义不存在。", before);
                        if (before.State.World.EdgeId is not null)
                            return new PlayerCommandResult(false, "WRONG_LOCATION", "旅途中无法升级建筑。", before);
                        var upgradeNow = DateTimeOffset.UtcNow;
                        var upgradeResult = await nodeBuildings.StartUpgradeAsync(before.State.World.NodeId, buildingId, upgradeNow, cancellationToken).ConfigureAwait(false);
                        if (!upgradeResult.Success)
                            return new PlayerCommandResult(false, upgradeResult.ErrorCode ?? "UPGRADE_FAILED", upgradeResult.Message ?? "升级发起失败。", before);
                        await RefreshNodeBuildingsAsync(playerId, before.State.World, upgradeNow, cancellationToken).ConfigureAwait(false);
                        TrackDirty(playerId);
                        break;
                    }
                    case "building.contribute":
                    {
                        if (nodeBuildings is null)
                            return new PlayerCommandResult(false, "BUILDING_UNAVAILABLE", "建筑服务未装配。", before);
                        var buildingId = payload.TryGetProperty("buildingId", out var contributeElement) && contributeElement.ValueKind == JsonValueKind.String
                            ? contributeElement.GetString() : null;
                        if (string.IsNullOrWhiteSpace(buildingId) || !gameData.Buildings.ContainsKey(buildingId))
                            return new PlayerCommandResult(false, "BUILDING_NOT_FOUND", "建筑定义不存在。", before);
                        if (before.State.World.EdgeId is not null)
                            return new PlayerCommandResult(false, "WRONG_LOCATION", "旅途中无法提交建筑材料。", before);
                        var requested = new Dictionary<string, double>(StringComparer.Ordinal);
                        if (payload.TryGetProperty("materials", out var materialsElement) && materialsElement.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var entry in materialsElement.EnumerateObject())
                            {
                                if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetDouble(out var rawQuantity)
                                    && double.IsFinite(rawQuantity))
                                {
                                    var quantity = Math.Floor(rawQuantity);
                                    if (quantity > 0)
                                        requested[entry.Name] = quantity;
                                }
                            }
                        }
                        if (requested.Count == 0)
                            return new PlayerCommandResult(false, "INVALID_MATERIALS", "提交材料为空。", before);
                        await EnsureWalletFreshAsync(playerId, cancellationToken).ConfigureAwait(false);
                        var contributeContext = new NodeBuildingContributionTransaction(
                            playerId, requestId, $"building-contribute:{playerId:D}:{requestId:D}", command,
                            DateTimeOffset.UtcNow,
                            Receipts: [CreateReceipt(playerId, requestId, command, payloadHash, before.StateVersion)]);
                        NodeBuildingMutationResult contributeResult;
                        try
                        {
                            contributeResult = await nodeBuildings.ContributeAsync(
                                before.State.World.NodeId, buildingId, requested, contributeContext, cancellationToken).ConfigureAwait(false);
                        }
                        catch (EconomyCommitOutcomeUnknownException unknown)
                        {
                            _fencedCommits[playerId] = unknown;
                            _logger.LogCritical(unknown, "建筑贡献结局无法确认，保留玩家租约 player={PlayerId}", playerId);
                            throw;
                        }
                        var contributeCommit = contributeResult.Economy;
                        if (!contributeResult.Success)
                            return new PlayerCommandResult(false, contributeResult.ErrorCode ?? "CONTRIBUTE_FAILED", contributeResult.Message ?? "材料提交失败。", before);
                        receiptAlreadyCommitted = contributeCommit is { Applied: true };
                        if (contributeCommit is { } committedContribute)
                            await SyncWalletAfterCommitAsync(playerId, committedContribute, cancellationToken).ConfigureAwait(false);
                        else
                            await ReloadWalletAsync(playerId, cancellationToken).ConfigureAwait(false);
                        await RefreshNodeBuildingsAsync(playerId, before.State.World, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                        TrackDirty(playerId);
                        break;
                    }
                    case "market.place":
                    {
                        if (marketRuntime is null)
                            return new PlayerCommandResult(false, "MARKET_UNAVAILABLE", "市场运行时未装配。", before);
                        await EnsureWalletFreshAsync(playerId, cancellationToken).ConfigureAwait(false);
                        if (!TryReadGuid(payload, "orderId", out var orderId))
                            return new PlayerCommandResult(false, "INVALID_ORDER", "订单标识无效。", before);
                        var assetId = payload.TryGetProperty("itemId", out var itemElement) ? itemElement.GetString() : null;
                        var sideText = payload.TryGetProperty("side", out var sideElement) ? sideElement.GetString() : null;
                        var variant = payload.TryGetProperty("variant", out var variantElement) && variantElement.ValueKind != JsonValueKind.Null ? variantElement.GetString() ?? string.Empty : string.Empty;
                        var price = payload.TryGetProperty("price", out var priceElement) && priceElement.TryGetDouble(out var parsedPrice) ? parsedPrice : double.NaN;
                        var quantity = payload.TryGetProperty("quantity", out var quantityElement) && quantityElement.TryGetDouble(out var parsedQuantity) ? parsedQuantity : double.NaN;
                        if (string.IsNullOrWhiteSpace(assetId) || !Enum.TryParse<MarketSide>(sideText, true, out var side)
                            || !double.IsFinite(price) || price <= 0 || price != Math.Floor(price)
                            || !double.IsFinite(quantity) || quantity <= 0 || quantity != Math.Floor(quantity))
                            return new PlayerCommandResult(false, "INVALID_ORDER", "订单品种、方向、价格或数量无效。", before);
                        // 强化变体校验："+N" 独立盘口仅限装备且等级不超上限；
                        // "+0"/空白归一为基础簿，"+03" 等非规范写法归一为 "+N"，避免同一簿被异形键拆串。
                        if (EnhancementRules.ParseLevel(variant) is not { } variantLevel
                            || variantLevel > EnhancementRules.MaxLevel
                            || (variantLevel > 0 && !gameData.RawEquipments.ContainsKey(assetId)))
                            return new PlayerCommandResult(false, "INVALID_VARIANT", "订单变体无效。", before);
                        variant = EnhancementRules.KeyFor(new ItemId(assetId), variantLevel).Variant;
                        // 与 item.discard/item.sell 同一防线但收窄到同级匹配：钱包按变体行冻结，
                        // 扣除同 itemId 且同强化等级的已装备件后为可用余额，仅在可支配量不足时拦截，多余备件放行。
                        if (side == MarketSide.Sell)
                        {
                            var equippedCount = before.State.Loadout.Equipment.Count(slot => slot.ItemId == assetId && (int)Math.Floor(slot.EnhancementLevel) == variantLevel);
                            if (equippedCount > 0)
                            {
                                var totalHeld = wallet.Get(new AssetKey(new ItemId(assetId), variant));
                                if (totalHeld - equippedCount < quantity)
                                    return new PlayerCommandResult(false, "ITEM_EQUIPPED", "已装备的物品无法挂单出售，请先卸下。", before);
                            }
                        }
                        // 绑定物品（isTradable=false，如功德·绑定）不得进入坊市挂单。
                        if (gameData.Items.TryGetValue(new ItemId(assetId), out var instrumentItem) && !instrumentItem.IsTradable)
                            return new PlayerCommandResult(false, "ITEM_NOT_TRADABLE", "绑定物品无法交易。", before);
                        await marketRuntime.PlaceAsync(new PlaceOrderRequest(new OrderId(orderId), new PlayerId(playerId),
                            new MarketInstrument(new ItemId(assetId), variant), side, price, quantity,
                            DateTimeOffset.UtcNow, new IdempotencyKey($"market:{playerId:D}:{orderId:D}")), cancellationToken).ConfigureAwait(false);
                        await ReloadWalletAsync(playerId, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case "market.cancel":
                    {
                        if (marketRuntime is null)
                            return new PlayerCommandResult(false, "MARKET_UNAVAILABLE", "市场运行时未装配。", before);
                        await EnsureWalletFreshAsync(playerId, cancellationToken).ConfigureAwait(false);
                        if (!TryReadGuid(payload, "orderId", out var orderId))
                            return new PlayerCommandResult(false, "INVALID_ORDER", "订单标识无效。", before);
                        var order = await marketRuntime.GetOrderAsync(orderId, new PlayerId(playerId), cancellationToken).ConfigureAwait(false);
                        if (order is null)
                            return new PlayerCommandResult(false, "ORDER_NOT_FOUND", "订单不存在。", before);
                        await marketRuntime.CancelAsync(new OrderId(orderId), new PlayerId(playerId), order.Instrument,
                            $"market-cancel:{playerId:D}:{orderId:D}", cancellationToken).ConfigureAwait(false);
                        await ReloadWalletAsync(playerId, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case "market.claim":
                    {
                        if (marketRuntime is null)
                            return new PlayerCommandResult(false, "MARKET_UNAVAILABLE", "市场运行时未装配。", before);
                        if (marketRuntime.PartitionCount != 1)
                            return new PlayerCommandResult(false, "MARKET_CLAIM_UNAVAILABLE", "多市场分片尚未提供跨分片结算领取路由。", before);
                        await marketRuntime.ClaimSettlementAsync(new PlayerId(playerId), requestId, cancellationToken).ConfigureAwait(false);
                        await ReloadWalletAsync(playerId, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case "item.discard":
                    case "item.sell":
                    {
                        var disposeItemId = payload.TryGetProperty("itemId", out var disposeItemElement)
                            && disposeItemElement.ValueKind == JsonValueKind.String
                            ? disposeItemElement.GetString() : null;
                        var disposeQuantity = payload.TryGetProperty("quantity", out var disposeQuantityElement)
                            && disposeQuantityElement.TryGetDouble(out var parsedDisposeQuantity)
                            ? parsedDisposeQuantity : double.NaN;
                        var variant = payload.TryGetProperty("variant", out var variantElement)
                            && variantElement.ValueKind == JsonValueKind.String
                            ? variantElement.GetString() : string.Empty;
                        if (string.IsNullOrWhiteSpace(disposeItemId) || !double.IsFinite(disposeQuantity) || disposeQuantity <= 0
                            || disposeQuantity != Math.Floor(disposeQuantity))
                            return new PlayerCommandResult(false, "INVALID_PAYLOAD", "物品或数量无效。", before);
                        var hasItemDefinition = gameData.Items.TryGetValue(new ItemId(disposeItemId), out var disposeDefinition);
                        var isEquipment = gameData.RawEquipments.ContainsKey(disposeItemId);
                        if (!hasItemDefinition && !isEquipment)
                            return new PlayerCommandResult(false, "ITEM_NOT_FOUND", "物品不存在。", before);

                        // 强化变体校验与归一化：装备按 "+N" 解析，普通物品不允许非空变体
                        var variantLevel = 0;
                        if (isEquipment)
                        {
                            if (EnhancementRules.ParseLevel(variant) is not { } parsedLevel
                                || parsedLevel > EnhancementRules.MaxLevel)
                                return new PlayerCommandResult(false, "INVALID_VARIANT", "装备变体无效。", before);
                            variantLevel = parsedLevel;
                            variant = EnhancementRules.KeyFor(new ItemId(disposeItemId), variantLevel).Variant;
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(variant))
                                return new PlayerCommandResult(false, "INVALID_VARIANT", "该物品不支持变体处置。", before);
                            variant = string.Empty;
                        }

                        await EnsureWalletFreshAsync(playerId, cancellationToken).ConfigureAwait(false);
                        var assetKey = new AssetKey(new ItemId(disposeItemId), variant);
                        var totalHeld = wallet.Get(assetKey);

                        // 已穿戴件数防护：收窄至同级已穿戴占用，放行多余未穿戴备件
                        if (isEquipment)
                        {
                            var equippedCount = before.State.Loadout.Equipment.Count(slot =>
                                string.Equals(slot.ItemId, disposeItemId, StringComparison.Ordinal)
                                && (int)Math.Floor(slot.EnhancementLevel) == variantLevel);
                            if (equippedCount > 0 && totalHeld - equippedCount < disposeQuantity)
                                return new PlayerCommandResult(false, "ITEM_EQUIPPED", "已装备的物品无法处置，请先卸下。", before);
                        }

                        if (totalHeld + 1e-9 < disposeQuantity)
                            return new PlayerCommandResult(false, "INSUFFICIENT_ITEM", "物品数量不足。", before);

                        var spiritCredit = 0d;
                        if (command == "item.sell")
                        {
                            if (disposeItemId.StartsWith("currency.", StringComparison.Ordinal))
                                return new PlayerCommandResult(false, "ITEM_NOT_SELLABLE", "货币不支持系统回收。", before);

                            double unitSellPrice;
                            if (isEquipment)
                            {
                                var baseSellPrice = EnhancementRules.ComputeEquipmentBaseSellPrice(gameData, disposeItemId);
                                if (baseSellPrice is not { } basePrice || basePrice <= 0)
                                    return new PlayerCommandResult(false, "ITEM_NOT_SELLABLE", "该装备不支持系统回收。", before);
                                unitSellPrice = EnhancementRules.CalculateEquipmentSellPrice(basePrice, variantLevel);
                            }
                            else
                            {
                                if (!hasItemDefinition || disposeDefinition!.SellPrice <= 0)
                                    return new PlayerCommandResult(false, "ITEM_NOT_SELLABLE", "该物品不支持系统回收。", before);
                                unitSellPrice = disposeDefinition.SellPrice;
                            }
                            spiritCredit = unitSellPrice * disposeQuantity;
                        }

                        var disposeNow = DateTimeOffset.UtcNow;
                        var disposeDeltas = new List<Server.Clean.AssetDelta>
                        {
                            new(playerId, disposeItemId, variant, -disposeQuantity)
                        };
                        if (spiritCredit > 0)
                            disposeDeltas.Add(new(playerId, "currency.spirit-stone", string.Empty, spiritCredit));
                        var disposeResult = await economy.CommitAsync(new EconomyTransactionRequest(
                            requestId,
                            $"{command}:{playerId:D}:{requestId:D}",
                            command,
                            playerId,
                            disposeDeltas,
                            disposeNow,
                            JsonSerializer.SerializeToUtf8Bytes(new { itemId = disposeItemId, variant, quantity = disposeQuantity }),
                            Receipts: [CreateReceipt(playerId, requestId, command, payloadHash, before.StateVersion)]),
                            cancellationToken).ConfigureAwait(false);
                        if (!disposeResult.Applied)
                            return new PlayerCommandResult(false, "TRANSACTION_REJECTED", "物品处置事务未提交。", before);
                        await SyncWalletAfterCommitAsync(playerId, disposeResult, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case "shop.buy":
                    {
                        var shopOfferId = payload.TryGetProperty("offerId", out var offerElement) && offerElement.ValueKind == JsonValueKind.String
                            ? offerElement.GetString() : null;
                        var buyQuantity = payload.TryGetProperty("quantity", out var quantityElement) && quantityElement.TryGetInt32(out var parsedBuyQuantity)
                            ? parsedBuyQuantity : 0;
                        if (string.IsNullOrWhiteSpace(shopOfferId) || !ShopPurchaseRules.IsQuantityValid(buyQuantity))
                            return new PlayerCommandResult(false, "INVALID_PAYLOAD", "商品或数量无效。", before);
                        if (!gameData.ShopOffers.TryGet(shopOfferId, out var offer))
                            return new PlayerCommandResult(false, "SHOP_OFFER_NOT_FOUND", "商品不存在或已下架。", before);
                        // 限购商品一次买多件必然超配额：提前拒绝，避免走到事务内才靠守卫回滚。
                        if (offer.DailyLimit is { } dailyLimit && buyQuantity > dailyLimit)
                            return new PlayerCommandResult(false, "SHOP_LIMIT_REACHED", "该商品今日限购已达上限。", before);
                        var totalPrice = offer.UnitPrice * buyQuantity;
                        if (!double.IsFinite(totalPrice) || totalPrice <= 0)
                            return new PlayerCommandResult(false, "INVALID_PAYLOAD", "商品价格无效。", before);

                        await EnsureWalletFreshAsync(playerId, cancellationToken).ConfigureAwait(false);
                        wallet = WalletOf(playerId);
                        const string boundMeritItemId = "item.gongde-bound";
                        const string normalMeritItemId = "item.gongde";
                        var boundBalance = wallet.Get(new AssetKey(new ItemId(boundMeritItemId)));
                        var normalBalance = wallet.Get(new AssetKey(new ItemId(normalMeritItemId)));
                        var totalMerit = boundBalance + normalBalance;
                        if (totalMerit + 1e-9 < totalPrice)
                            return new PlayerCommandResult(false, "INSUFFICIENT_RESOURCE", "功德不足。", before);

                        var deductBound = Math.Min(boundBalance, totalPrice);
                        var deductNormal = totalPrice - deductBound;
                        // 扣功德（优先扣绑定功德）与发物同行落账；限购商品另带同事务配额认领，超限即整体回滚。
                        var shopDeltas = new List<Server.Clean.AssetDelta>();
                        if (deductBound > 0)
                            shopDeltas.Add(new(playerId, boundMeritItemId, string.Empty, -deductBound));
                        if (deductNormal > 0)
                            shopDeltas.Add(new(playerId, normalMeritItemId, string.Empty, -deductNormal));
                        shopDeltas.Add(new(playerId, offer.ItemId, string.Empty, buyQuantity));
                        var shopNow = DateTimeOffset.UtcNow;
                        var shopClaim = offer.DailyLimit is { } claimLimit
                            ? new ShopPurchaseClaim(playerId, offer.Id, ShopDailyWindow.DayIndexOf(shopNow), buyQuantity, claimLimit)
                            : null;
                        EconomyTransactionResult shopResult;
                        try
                        {
                            shopResult = await economy.CommitAsync(new EconomyTransactionRequest(
                                requestId,
                                $"shop.buy:{playerId:D}:{requestId:D}",
                                command,
                                playerId,
                                shopDeltas,
                                shopNow,
                                JsonSerializer.SerializeToUtf8Bytes(new { offerId = offer.Id, itemId = offer.ItemId, unitPrice = offer.UnitPrice, quantity = buyQuantity }),
                                Receipts: [CreateReceipt(playerId, requestId, command, payloadHash, before.StateVersion)],
                                ShopPurchase: shopClaim),
                                cancellationToken).ConfigureAwait(false);
                        }
                        catch (ShopDailyLimitExceededException)
                        {
                            return new PlayerCommandResult(false, "SHOP_LIMIT_REACHED", "该商品今日限购已达上限。", before);
                        }
                        catch (EconomyInsufficientBalanceException)
                        {
                            return new PlayerCommandResult(false, "INSUFFICIENT_RESOURCE", "功德不足。", before);
                        }
                        if (!shopResult.Applied)
                            return new PlayerCommandResult(false, "TRANSACTION_REJECTED", "购买事务未提交。", before);
                        await SyncWalletAfterCommitAsync(playerId, shopResult, cancellationToken).ConfigureAwait(false);
                        receiptAlreadyCommitted = true;
                        break;
                    }
                    case "player.rename":
                    {
                        var name = payload.TryGetProperty("displayName", out var nameElement) ? nameElement.GetString() : null;
                        var renameNow = DateTimeOffset.UtcNow;
                        PlayerAggregate.Restore(before.State).EnsureCanRename(name ?? string.Empty, renameNow);
                        if (accounts is not null
                            && !string.Equals(before.DisplayName, name?.Trim(), StringComparison.Ordinal)
                            && !await accounts.RenamePlayerAsync(playerId, name!.Trim(), cancellationToken).ConfigureAwait(false))
                            return new PlayerCommandResult(false, "NAME_TAKEN", "该道号已被占用。", before);
                        var newName = name ?? string.Empty;
                        touched = ecs.MutatePlayer(playerId, aggregate => aggregate.Rename(newName, renameNow));
                        break;
                    }
                    case "player.set_avatar":
                    {
                        var avatar = payload.TryGetProperty("avatar", out var avatarElement) ? avatarElement.GetString() : null;
                        var normalized = PlayerAggregate.NormalizeAvatar(avatar);
                        if (!before.State.Settings.IsAvatarUnlocked(normalized))
                            return new PlayerCommandResult(false, "AVATAR_LOCKED", "该形象尚未解锁。", before);
                        touched = ecs.MutatePlayer(playerId, aggregate => aggregate.SetAvatar(normalized));
                        if (presence is not null)
                            await presence.UpdateAvatarAsync(playerId, normalized, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case "player.unlock_avatar":
                    {
                        var avatar = payload.TryGetProperty("avatar", out var avatarElement) ? avatarElement.GetString() : null;
                        var normalized = PlayerAggregate.NormalizeAvatar(avatar);
                        if (before.State.Settings.IsAvatarUnlocked(normalized))
                        {
                            touched = ecs.MutatePlayer(playerId, aggregate => aggregate.SetAvatar(normalized));
                            if (presence is not null)
                                await presence.UpdateAvatarAsync(playerId, normalized, cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        const double unlockCost = 5000d;
                        const string boundMeritItemId = "item.gongde-bound";
                        const string normalMeritItemId = "item.gongde";

                        await EnsureWalletFreshAsync(playerId, cancellationToken).ConfigureAwait(false);
                        var boundBalance = wallet.Get(new AssetKey(new ItemId(boundMeritItemId)));
                        var normalBalance = wallet.Get(new AssetKey(new ItemId(normalMeritItemId)));
                        var totalMerit = boundBalance + normalBalance;

                        if (totalMerit + 1e-9 < unlockCost)
                            return new PlayerCommandResult(false, "INSUFFICIENT_MERIT", "功德不足，解锁形象需消耗 5000 功德。", before);

                        var deductBound = Math.Min(boundBalance, unlockCost);
                        var deductNormal = unlockCost - deductBound;

                        var deltas = new List<Server.Clean.AssetDelta>(2);
                        if (deductBound > 0)
                            deltas.Add(new(playerId, boundMeritItemId, string.Empty, -deductBound));
                        if (deductNormal > 0)
                            deltas.Add(new(playerId, normalMeritItemId, string.Empty, -deductNormal));

                        touched = ecs.MutatePlayer(playerId, aggregate =>
                        {
                            aggregate.UnlockAvatar(normalized);
                            aggregate.SetAvatar(normalized);
                        });

                        var mutated = ToSnapshot(ecs.ExportSnapshot(playerId));
                        var unlockReceipt = CreateReceipt(playerId, requestId, command, payloadHash, mutated.StateVersion);
                        await CommitPlayerAssetsAsync(playerId, before, dirtySectionsBefore,
                            new EconomyTransactionRequest(
                                requestId,
                                $"avatar-unlock:{playerId:D}:{requestId:D}",
                                command,
                                playerId,
                                deltas,
                                DateTimeOffset.UtcNow,
                                JsonSerializer.SerializeToUtf8Bytes(new { avatar = normalized, cost = unlockCost, deductBound, deductNormal }),
                                Receipts: [unlockReceipt]),
                            unlockReceipt,
                            cancellationToken).ConfigureAwait(false);
                        receiptAlreadyCommitted = true;

                        if (presence is not null)
                            await presence.UpdateAvatarAsync(playerId, normalized, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case "simulation.advance":
                    {
                        var now = _simulationClock.UtcNow;
                        var target = payload.TryGetProperty("targetTime", out var targetElement)
                                     && targetElement.TryGetDateTimeOffset(out var parsed) ? parsed.ToUniversalTime() : now;
                        if (!_simulationClock.IsDeterministic && target > now.AddSeconds(30)) target = now;
                        await RefreshNodeBuildingsAsync(playerId, before.State.World, target, cancellationToken).ConfigureAwait(false);
                        before = ToSnapshot(ecs.ExportSnapshot(playerId));
                        TrackDirty(playerId);
                        await ecs.RunPlayerOnceAsync(playerId, target, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case "player.presence":
                    {
                        var value = payload.TryGetProperty("presence", out var presence) ? presence.GetString() : null;
                        var state = value?.ToLowerInvariant() switch
                        {
                            "online" => PresenceState.Online,
                            "suspended" => PresenceState.Suspended,
                            "offline" => PresenceState.Offline,
                            _ => throw new DomainException(DomainErrorCode.InvalidArgument, "Presence 状态无效。")
                        };
                        ecs.SetPresence(playerId, state, _simulationClock.UtcNow);
                        break;
                    }
                    case "tutorial.skip":
                    {
                        // 跳过引导：步骤收敛到终态，剩余未领赠送/终奖在命令事务内一次性补发
                        // （奖励游标兜底——重复跳过或已领过的里程碑不再重复入账）。
                        IReadOnlyDictionary<ItemId, double> skipGrants = new Dictionary<ItemId, double>();
                        touched = ecs.MutatePlayer(playerId, aggregate =>
                        {
                            skipGrants = DaoHua.Ecs.Systems.TutorialRules.PendingSkipGrants(aggregate.Settings);
                            aggregate.SkipTutorial();
                        });
                        var skipSnapshot = ToSnapshot(ecs.ExportSnapshot(playerId));
                        await CommitPlayerAssetsAsync(playerId, before, dirtySectionsBefore,
                            new EconomyTransactionRequest(requestId, $"tutorial-skip:{playerId:D}:{requestId:D}", command, playerId,
                                skipGrants.Select(pair => new Server.Clean.AssetDelta(playerId, pair.Key.Value, string.Empty, pair.Value)).ToArray(),
                                DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(new { granted = skipGrants.Count })),
                            CreateReceipt(playerId, requestId, command, payloadHash, skipSnapshot.StateVersion), cancellationToken).ConfigureAwait(false);
                        receiptAlreadyCommitted = true;
                        if (skipGrants.Count > 0) tutorialSkipLogs = [AppendTutorialSkipLog(playerId, skipGrants)];
                        break;
                    }
                    case "tutorial.restart":
                    {
                        // 重新学习引导：里程碑重置为起点，奖励游标保持不动——重走不重复发已领奖励。
                        // 若玩家当前不在出生点【云溪村】，自动规划并插队一条前往云溪村的移动行动，待抵达后开始引导。
                        var restartNow = _simulationClock.UtcNow;
                        var homeNodeId = DaoHua.Ecs.Systems.TutorialRules.VillageNodeId;
                        if ((!string.Equals(before.State.World.NodeId, homeNodeId, StringComparison.Ordinal)
                                || before.State.World.EdgeId is not null)
                            && before.State.Actions.FirstOrDefault()?.Participation is { } restartParticipation)
                        {
                            await (participationRuntime ?? throw new InvalidOperationException("未装配行动参与权威宿主。"))
                                .EndAsync(restartParticipation.SessionId, restartNow, "member-node-changed", cancellationToken).ConfigureAwait(false);
                            await ReconcileParticipationOwnedAsync(playerId, restartNow, notifications, cancellationToken,
                                advanceSession: false).ConfigureAwait(false);
                            before = ToSnapshot(ecs.ExportSnapshot(playerId));
                            dirtySectionsBefore = ecs.GetDirtySections(playerId);
                            wallet = WalletOf(playerId);
                        }
                        if (restartNow > before.State.LastSimulatedAt)
                            await ecs.RunPlayerOnceAsync(playerId, restartNow, cancellationToken).ConfigureAwait(false);
                        touched = ecs.MutatePlayer(playerId, aggregate =>
                        {
                            aggregate.RestartTutorial();
                            if (string.Equals(aggregate.World.NodeId, homeNodeId, StringComparison.Ordinal)
                                && string.IsNullOrWhiteSpace(aggregate.World.EdgeId))
                                return;

                            for (var i = aggregate.Actions.Count - 1; i >= 0; i--)
                            {
                                if (aggregate.Actions[i].Intent.ActionId.Value == "world.travel")
                                    aggregate.RemoveAction(aggregate.Actions[i].Id);
                            }
                            var worldGraph = gameData.WorldGraph;
                            var plan = worldGraph.PlanFromPosition(aggregate.World.EdgeId,
                                aggregate.World.EdgeProgress, aggregate.World.NodeId, homeNodeId);
                            if (plan.Segments.Count == 0 && plan.TotalTravelSeconds <= 0)
                            {
                                if (worldGraph.TryGetNode(homeNodeId, out var targetNodeObj))
                                    aggregate.TravelTo(homeNodeId, targetNodeObj.X, targetNodeObj.Y);
                                return;
                            }
                            var travelState = new TravelActionState(homeNodeId, plan.TotalTravelSeconds,
                                plan.EdgeIds, plan.Segments);
                            forfeit = aggregate.ApplyDuelForfeit(restartNow, gameData.Duel);
                            var enqueuedAt = aggregate.LastSimulatedAt > restartNow ? aggregate.LastSimulatedAt : restartNow;
                            aggregate.EnqueuePriority(new ActionIntent(new ActionId("world.travel"), 1, homeNodeId),
                                enqueuedAt, gameData.Actions, travelState);
                        });
                        break;
                    }
                    default:
                        return new PlayerCommandResult(false, "UNKNOWN_COMMAND", "未知领域命令。", before);
                }

                var after = ToSnapshot(ecs.ExportSnapshot(playerId));
                var receipt = receiptAlreadyCommitted ? null
                    : ShouldPersistReceipt(command) ? CreateReceipt(playerId, requestId, command, payloadHash, after.StateVersion) : null;
                var outcome = await CommitSimulationAsync(playerId, before, after, receipt, dirtySectionsBefore, cancellationToken).ConfigureAwait(false);
                after = ToSnapshot(ecs.ExportSnapshot(playerId));
                var revisionNow = ecs.AttributeRevision(playerId);
                var revisionBefore = _attributeRevisions.GetValueOrDefault(playerId, revisionNow);
                after = after with
                {
                    // MutatePlayer 返回聚合侧精确差集；未走变更通道的命令用实体流水号差集兜底，
                    // 成功命令的触及集必为非空集合（零触及=空集，语义不同于"未知"的 null）。
                    TouchedSections = touched ?? TouchedSectionsSince(dirtyMarksBefore, ecs.GetDirtyMarks(playerId)),
                    WireAttributesChanged = revisionNow != revisionBefore
                };
                _attributeRevisions[playerId] = revisionNow;
                _loaded[playerId] = after;
                recent.Remember(requestId, command, payloadHash);
                // 版本未推进的命令（market.*/item.* 钱包类）不附加快照推送：
                // 同版本推送会被客户端版本门禁丢弃，却前移会话推送基线、把回执的资产差集烧空；
                // 此类差额只能由必达的命令回执 delta 携带（客户端按同版本回执合入资产）。
                // 跳过引导的补发入账日志：与本命令推送同帧下发，不依赖下一次模拟结算。
                var mergedLogs = tutorialSkipLogs is null ? outcome.Logs
                    : outcome.Logs is null ? tutorialSkipLogs
                    : [.. outcome.Logs, .. tutorialSkipLogs];
                notifications.Add(new RuntimeNotification(playerId, mergedLogs,
                    after.StateVersion != before.StateVersion ? after : null));
                // 邀请里程碑入账日志：排在突破战报之后；在线邀请者附钱包失效 + 日志推送，离线者下次装载读余额。
                if (invitationGrants is { Count: > 0 })
                    foreach (var grant in invitationGrants)
                    {
                        notifications.Add(new RuntimeNotification(playerId,
                            [AppendInvitationLog(playerId, Server.Clean.Social.InvitationRewards.InviteeRewardLogTemplate(grant.Milestone))]));
                        if (_loaded.ContainsKey(grant.InviterId))
                        {
                            _walletReloadRequired[grant.InviterId] = 0;
                            notifications.Add(new RuntimeNotification(grant.InviterId,
                                [AppendInvitationLog(grant.InviterId, Server.Clean.Social.InvitationRewards.InviterRewardLogTemplate(grant.Milestone, before.DisplayName))]));
                        }
                    }
                if (forfeit is { } resolvedForfeit)
                    await RouteDuelForfeitAsync(resolvedForfeit, notifications, cancellationToken).ConfigureAwait(false);
                return new PlayerCommandResult(true, "OK", SuccessNotice(command), after, outcome.Simulation, outcome.Logs);
            }
            catch (DomainException exception)
            {
                return new PlayerCommandResult(false, exception.Code.ToString().ToUpperInvariant(), exception.Message, before);
            }
            catch (Exception exception) when (exception is KeyNotFoundException or JsonException or FormatException)
            {
                return new PlayerCommandResult(false, "INVALID_PAYLOAD", exception.Message, before);
            }
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public async ValueTask<PlayerRuntimeSnapshot> MutateOwnedAsync(Guid playerId, Action<PlayerAggregate> mutate, CancellationToken cancellationToken = default)
    {
        // 社交仲裁通道：与命令路径同构——玩家门内装载（离线成员强制装载+补算）、就地变更、
        // 标脏入刷盘轨道、刷新驻留快照，版本推进时在锁外推送。不附加战报/回执（无结算语义）。
        var notifications = new List<RuntimeNotification>();
        var gate = _playerGates.GetOrAdd(playerId, static _ => new SemaphoreSlim(1, 1));
        using (RuntimeInstrumentation.Measure("queue.actor_wait"))
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await LoadOwnedAsync(playerId, notifications, cancellationToken).ConfigureAwait(false);
            await RenewOwnershipLeaseIfDueAsync(playerId, cancellationToken, force: true).ConfigureAwait(false);
            await ReconcileParticipationOwnedAsync(playerId, _simulationClock.UtcNow, notifications, cancellationToken).ConfigureAwait(false);
            before = ToSnapshot(ecs.ExportSnapshot(playerId));
            var dirtyMarksBefore = ecs.GetDirtyMarks(playerId);
            var touched = ecs.MutatePlayer(playerId, mutate);
            TrackDirty(playerId);
            var after = ToSnapshot(ecs.ExportSnapshot(playerId));
            var revisionNow = ecs.AttributeRevision(playerId);
            var revisionBefore = _attributeRevisions.GetValueOrDefault(playerId, revisionNow);
            after = after with
            {
                TouchedSections = touched,
                WireAttributesChanged = revisionNow != revisionBefore
            };
            _attributeRevisions[playerId] = revisionNow;
            _loaded[playerId] = after;
            if (after.StateVersion != before.StateVersion)
                notifications.Add(new RuntimeNotification(playerId, null, after));
            return after;
        }
        finally
        {
            gate.Release();
            await EmitNotificationsAsync(notifications).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(Guid playerId, bool force, CancellationToken cancellationToken = default)
        => FlushLoadedAsync(playerId, force, cancellationToken);

    /// <inheritdoc />
    public async ValueTask FlushAllAsync(bool force, CancellationToken cancellationToken = default)
    {
        var failures = new ConcurrentQueue<Exception>();
        await Parallel.ForEachAsync(_loaded.Keys.ToArray(), new ParallelOptions
        { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (playerId, token) =>
        {
            try { await FlushLoadedAsync(playerId, force, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { failures.Enqueue(exception); }
        }).ConfigureAwait(false);
        if (failures.IsEmpty) return;
        if (failures.Count == 1 && failures.TryDequeue(out var single)) ExceptionDispatchInfo.Capture(single).Throw();
        throw new AggregateException(failures);
    }

    /// <summary>
    /// 把全部已加载玩家统一推进到 target：按 ID 稳定排序一次性获取全部玩家 gate（避免与单玩家命令交叉死锁），
    /// 取得 gate 后重检加载成员，完成租约续期与失效钱包重读，仅推进本批持有 gate 且目标未过时的玩家。
    /// 会话推进与个人收件失败按所有权隔离，健康玩家继续；个人模拟提交失败仍回滚本批尚未提交状态。
    /// </summary>
    public async ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
    {
        // 会话先独占推进并 durable 投递，随后成员在自己的 gate 内原子消费。
        var failures = new List<Exception>();
        if (participationRuntime is not null)
            try { await participationRuntime.AdvanceOnlineAsync(target, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { failures.Add(exception); }
        var ids = _loaded.Keys.OrderBy(id => id).ToArray();
        var gates = ids.Select(id => _playerGates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1))).ToArray();
        var notifications = new List<RuntimeNotification>();
        Dictionary<Guid, PlayerRuntimeSnapshot>? before = null;
        Dictionary<Guid, PlayerSectionKind[]>? dirtyBefore = null;
        var uncommitted = ids.ToHashSet();
        var acquired = 0;
        try
        {
            for (; acquired < gates.Length; acquired++)
                using (RuntimeInstrumentation.Measure("queue.actor_wait"))
                    await gates[acquired].WaitAsync(cancellationToken).ConfigureAwait(false);
            // 等待 gate 期间会话可能已经卸载；原 gate 集合仍在 finally 释放，后续只操作实际加载交集。
            ids = ids.Where(_loaded.ContainsKey).ToArray();
            uncommitted.IntersectWith(ids);
            // 批量推进同样续租校验：租约已失的玩家逐出并从本批推进中剔除。
            if (ownership is not null)
            {
                var lost = new List<Guid>();
                foreach (var playerId in ids)
                    try { await RenewOwnershipLeaseIfDueAsync(playerId, cancellationToken).ConfigureAwait(false); }
                    catch (PlayerOwnershipLostException) { lost.Add(playerId); }
                if (lost.Count > 0)
                {
                    var lostSet = lost.ToHashSet();
                    foreach (var playerId in lost)
                    {
                        EvictOwnershipLostOwned(playerId);
                        uncommitted.Remove(playerId);
                    }
                    // gates/acquired 仍覆盖原始集合，由 finally 统一释放全部已持有 gate。
                    ids = ids.Where(id => !lostSet.Contains(id)).ToArray();
                }
            }
            // fenced 玩家仍参加上面的续租，但不允许进入任何变更、推进或刷盘路径。
            var fenced = ids.Where(_fencedCommits.ContainsKey).ToHashSet();
            uncommitted.ExceptWith(fenced);
            ids = ids.Where(id => !fenced.Contains(id)).ToArray();
            // 市场等外部来源的入账使缓存钱包失效：重读并收集余额真实变化的玩家，
            // 随后推进其版本让本轮推送把资产差量带给客户端（资产不属分段编码轴）。
            var walletsSynced = new HashSet<Guid>();
            foreach (var playerId in ids)
                if (await EnsureWalletFreshAsync(playerId, cancellationToken).ConfigureAwait(false)) walletsSynced.Add(playerId);
            var buildingsChanged = new HashSet<Guid>();
            foreach (var playerId in ids)
            {
                // 目标取样早于等待 gate；新命令已推进到更晚时刻时不能用过期时间更新建筑投影。
                if (target > _loaded[playerId].LastSimulatedAt
                    && await RefreshNodeBuildingsAsync(playerId, _loaded[playerId].State.World, target, cancellationToken).ConfigureAwait(false))
                    buildingsChanged.Add(playerId);
                TrackDirty(playerId);
            }
            var failedMembers = new HashSet<Guid>();
            foreach (var playerId in ids)
                try { await ReconcileParticipationOwnedAsync(playerId, target, notifications, cancellationToken, advanceSession: false).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failedMembers.Add(playerId);
                    failures.Add(new InvalidOperationException($"成员会话结算失败 playerId={playerId:D}。", exception));
                }
            ids = ids.Where(id => !failedMembers.Contains(id)).ToArray();
            uncommitted.ExceptWith(failedMembers);
            before = ids.ToDictionary(id => id, id => ToSnapshot(ecs.ExportSnapshot(id)));
            var marksBefore = ids.ToDictionary(id => id, ecs.GetDirtyMarks);
            dirtyBefore = ids.ToDictionary(id => id, ecs.GetDirtySections);
            foreach (var playerId in walletsSynced.Except(failedMembers)) ecs.MarkExternalStateChanged(playerId);
            // World 可能已有本批取样后刚创建、仍在装载中的实体；全世界 Tick 会越过其玩家 gate。
            // 正在读条的活跃玩家若无外部钱包/建筑投影变更且尚未到达下一个可见事件边界，跳过半途切片，避免每秒推送与整秒截断引发前端进度条抖动。
            foreach (var playerId in ids)
            {
                if (target <= before[playerId].LastSimulatedAt) continue;
                var head = before[playerId].State.Actions.FirstOrDefault();
                if (head is not null
                    && !before[playerId].State.Settings.QueuePaused
                    && head.Status == ActionQueueStatus.Running
                    && head.Expedition?.Phase != CombatExpeditionPhase.DuelPending
                    && !walletsSynced.Contains(playerId)
                    && !buildingsChanged.Contains(playerId)
                    && ecs.NextAdvanceBoundary(playerId) is { } nextBoundary
                    && target < nextBoundary)
                    continue;
                await ecs.RunPlayerOnceAsync(playerId, target, cancellationToken).ConfigureAwait(false);
            }
            // 斗法撮合（总纲 §7.10.1）：推进完成后的同一 gate 窗口内配对等待者，
            // 预约/镜像/挂载全部落在本轮提交窗口，快照推送复用下方既有提交流程。
            MatchDuelsOwned(target, ids, notifications);
            foreach (var playerId in ids)
            {
                var after = ToSnapshot(ecs.ExportSnapshot(playerId));
                if (after.StateVersion == before[playerId].StateVersion && after.LastSimulatedAt == before[playerId].LastSimulatedAt)
                {
                    ecs.DrainSimulationResult(playerId);
                    uncommitted.Remove(playerId);
                    continue;
                }
                var outcome = await CommitSimulationAsync(playerId, before[playerId], after, null, dirtyBefore[playerId], cancellationToken).ConfigureAwait(false);
                after = ToSnapshot(ecs.ExportSnapshot(playerId));
                var advanceRevision = ecs.AttributeRevision(playerId);
                var advanceRevisionBefore = _attributeRevisions.GetValueOrDefault(playerId, advanceRevision);
                after = after with
                {
                    WireAttributesChanged = advanceRevision != advanceRevisionBefore,
                    TouchedSections = TouchedSectionsSince(marksBefore[playerId], ecs.GetDirtyMarks(playerId))
                };
                _attributeRevisions[playerId] = advanceRevision;
                _loaded[playerId] = after;
                uncommitted.Remove(playerId);
                // 行为基线（沿袭历史 OOP 运行时口径）：日志无条件下发，快照仅在模拟真实变更（Changed）时推送，
                // 空闲玩家仅时间前进不产生 ChangedSections，不得收到空增量。
                // 例外：外部入账（市场结算等）已重读钱包并推进版本——Changed=false 时也必须推送，
                // 否则余额差量无回执可携带，在线对手方要等重连才能看到入账。
                var snapshotChanged = outcome.Simulation is { Changed: true } || walletsSynced.Contains(playerId);
                if (outcome.Logs is { Count: > 0 } || snapshotChanged)
                    notifications.Add(new RuntimeNotification(playerId, outcome.Logs, snapshotChanged ? after : null));
            }
        }
        catch
        {
            // 本批已推进但尚未提交的玩家必须恢复，避免下一轮发送失败批次遗留的日志。
            if (before is not null)
                foreach (var playerId in uncommitted)
                {
                    // 已 durable 提交但随后的钱包读取取消时，Commit 已保留新时钟；不能重复回滚。
                    if (_fencedCommits.ContainsKey(playerId)
                        || (_walletReloadRequired.ContainsKey(playerId)
                            && _loaded[playerId].LastSimulatedAt > before[playerId].LastSimulatedAt)) continue;
                    RestoreUncommittedPlayer(playerId, before[playerId], dirtyBefore![playerId]);
                }
            throw;
        }
        finally
        {
            while (acquired > 0) gates[--acquired].Release();
            await EmitNotificationsAsync(notifications).ConfigureAwait(false);
        }
        if (failures.Count > 0) throw new AggregateException("会话或成员结算失败，健康玩家已完成当批推进。", failures);
    }

    /// <summary>
    /// 斗法撮合（总纲 §7.10.1）：在已加载玩家中给处于匹配等待相位的寻求者配同一有效地图节点（World.NodeId 相同且未赶路）
    /// 且实力 ±PowerBandRatio 带内的候选，双方先预约后冻结镜像、一次性推演确定性剧本并各自挂载回放；
    /// 任一步失败全部回滚，无候选时退出等待静默转正常波次。仅在全体玩家 gate 持有的推进窗口内调用。
    /// </summary>
    private void MatchDuelsOwned(DateTimeOffset target, IReadOnlyList<Guid> ids, List<RuntimeNotification> notifications)
    {
        var nowUnix = target.ToUnixTimeMilliseconds() / 1000d;
        var band = gameData.Duel.PowerBandRatio;
        // 候选池先行快照：门禁内 MutatePlayer 的原子判据仍是最终裁决，池只承担初选与随机。
        // 严格同节点约束：排除移动中（EdgeId 非空）或节点未知的候选人。
        var candidates = new List<(Guid PlayerId, double Power, string NodeId)>();
        foreach (var id in ids)
        {
            (Guid PlayerId, double Power, string NodeId)? info = null;
            ecs.MutatePlayer(id, aggregate =>
            {
                if (aggregate.CanJoinDuel(nowUnix)
                    && aggregate.World.EdgeId is null
                    && !string.IsNullOrEmpty(aggregate.World.NodeId))
                {
                    info = (id, aggregate.DuelPower(gameData), aggregate.World.NodeId);
                }
            });
            if (info is { } entry) candidates.Add(entry);
        }
        if (candidates.Count == 0) return;
        foreach (var seekerId in ids)
        {
            double seekerPower = 0;
            string? seekerNodeId = null;
            var pending = false;
            ecs.MutatePlayer(seekerId, aggregate =>
            {
                if (aggregate.IsDuelPending
                    && aggregate.World.EdgeId is null
                    && !string.IsNullOrEmpty(aggregate.World.NodeId))
                {
                    pending = true;
                    seekerPower = aggregate.DuelPower(gameData);
                    seekerNodeId = aggregate.World.NodeId;
                }
            });
            if (!pending || seekerNodeId is null) continue;
            var pool = candidates
                .Where(candidate => candidate.PlayerId != seekerId
                    && candidate.NodeId == seekerNodeId
                    && candidate.Power >= seekerPower * (1 - band) && candidate.Power <= seekerPower * (1 + band))
                .ToList();
            if (pool.Count == 0)
            {
                AbortDuelSeekOwned(seekerId);
                continue;
            }
            var picked = pool[Random.Shared.Next(pool.Count)];
            var duelId = Guid.NewGuid();
            DuelCombatant? initiatorMirror = null;
            ecs.MutatePlayer(seekerId, aggregate =>
            {
                if (aggregate.World.NodeId == seekerNodeId && aggregate.World.EdgeId is null && aggregate.TryReserveDuel(duelId, nowUnix))
                    initiatorMirror = aggregate.BuildDuelMirror(gameData);
            });
            if (initiatorMirror is null)
            {
                AbortDuelSeekOwned(seekerId);
                continue;
            }
            DuelCombatant? defenderMirror = null;
            ecs.MutatePlayer(picked.PlayerId, aggregate =>
            {
                if (aggregate.World.NodeId == seekerNodeId && aggregate.World.EdgeId is null && aggregate.TryReserveDuel(duelId, nowUnix))
                    defenderMirror = aggregate.BuildDuelMirror(gameData);
            });
            if (defenderMirror is null)
            {
                ecs.MutatePlayer(seekerId, aggregate => aggregate.ReleaseDuelReservation(duelId));
                AbortDuelSeekOwned(seekerId);
                continue;
            }
            var seed = BitConverter.ToInt64(SHA256.HashData(duelId.ToByteArray()));
            var script = DuelScriptEngine.Resolve(initiatorMirror, defenderMirror, seed, gameData.Duel);
            var duelIdText = duelId.ToString("N");
            var initiatorAttached = AttachDuelOwned(seekerId, duelId,
                DuelPlaybackState.ForViewer(script, duelIdText, initiatorMirror, defenderMirror, viewerIsInitiator: true));
            if (!initiatorAttached)
            {
                ecs.MutatePlayer(seekerId, aggregate => aggregate.ReleaseDuelReservation(duelId));
                ecs.MutatePlayer(picked.PlayerId, aggregate => aggregate.ReleaseDuelReservation(duelId));
                AbortDuelSeekOwned(seekerId);
                continue;
            }
            var defenderAttached = AttachDuelOwned(picked.PlayerId, duelId,
                DuelPlaybackState.ForViewer(script, duelIdText, initiatorMirror, defenderMirror, viewerIsInitiator: false));
            if (!defenderAttached)
            {
                ecs.MutatePlayer(picked.PlayerId, aggregate => aggregate.ReleaseDuelReservation(duelId));
                ecs.MutatePlayer(seekerId, aggregate => aggregate.DetachDuel(duelId));
                continue;
            }
            // 应战方挂载成功即退出候选池：斗法期间不能被第二场撮合重复拉走。
            candidates.RemoveAll(candidate => candidate.PlayerId == picked.PlayerId);
            // 撮合成立才补「斗法遭遇」日志：匹配等待与静默回退阶段不下发提示（总纲 §7.10.2）。
            notifications.Add(new RuntimeNotification(seekerId,
                [AppendDuelMatchedLog(seekerId, defenderMirror.DisplayName)]));
            notifications.Add(new RuntimeNotification(picked.PlayerId,
                [AppendDuelMatchedLog(picked.PlayerId, initiatorMirror.DisplayName)]));
        }
    }

    /// <summary>挂载斗法回放并推进版本（撮合挂载必须产生快照推送）；领域校验失败返回 false 供回滚。</summary>
    private bool AttachDuelOwned(Guid playerId, Guid duelId, DuelPlaybackState playback)
    {
        try
        {
            ecs.MutatePlayer(playerId, aggregate =>
            {
                aggregate.AttachDuel(playback);
                aggregate.Touch(PlayerSectionKind.ActionQueue);
            });
            return true;
        }
        catch (DomainException exception)
        {
            _logger.LogWarning(exception, "斗法回放挂载失败，预约侧将回滚 duelId={DuelId} player={PlayerId}", duelId, playerId);
            return false;
        }
    }

    /// <summary>撮合无可用对手：退出匹配等待相位、本波转正常怪物波次；仅标脏不推版本（无玩家可见差异）。</summary>
    private void AbortDuelSeekOwned(Guid playerId)
        => ecs.MutatePlayer(playerId, aggregate => aggregate.AbortDuelSeek());

    /// <summary>撮合成功通知：以玩家战报日志通道写一条「斗法遭遇」；缓冲不存在时跳过（与旧口径一致）。</summary>
    private LogEventDto AppendDuelMatchedLog(Guid playerId, string opponentName)
    {
        var buffer = _logs.GetOrAdd(playerId, static _ => new PlayerLogBuffer(LogBufferCapacity));
        var logEvent = new LogEventDto(buffer.NextSequence(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            $"斗法遭遇：{opponentName} 与你狭路相逢。", 1, "system");
        buffer.Append(logEvent);
        return logEvent;
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(Guid playerId, bool flush, DateTimeOffset? offlineSince, CancellationToken cancellationToken = default)
    {
        if (!_loaded.TryGetValue(playerId, out var snapshot)) return;
        var gate = _playerGates.GetOrAdd(playerId, static _ => new SemaphoreSlim(1, 1));
        using (RuntimeInstrumentation.Measure("queue.actor_wait"))
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded.TryGetValue(playerId, out snapshot)) return;
            ThrowIfCommitFenced(playerId);
            await RenewOwnershipLeaseIfDueAsync(playerId, cancellationToken).ConfigureAwait(false);
            if (flush)
            {
                if (snapshot.State.Presence == PresenceState.Online)
                    ecs.SetPresence(playerId, PresenceState.Offline, offlineSince ?? _simulationClock.UtcNow);
                ecs.MarkDirty(playerId, PlayerSectionKind.Progression);
                var offline = ToSnapshot(ecs.ExportSnapshot(playerId));
                _loaded[playerId] = snapshot = offline;
                TrackDirty(playerId);
                await FlushOwnedAsync(playerId, force: true, cancellationToken).ConfigureAwait(false);
            }
            if (!ecs.RemovePlayer(playerId)) throw new InvalidOperationException($"ECS 玩家实体不存在：{playerId:D}");
            RuntimeInstrumentation.Count("lifecycle.player_unloaded");
            _loaded.TryRemove(playerId, out _);
            _wallets.TryRemove(playerId, out _);
            _logs.TryRemove(playerId, out _);
            _recentRequests.TryRemove(playerId, out _);
            _dirtyAt.TryRemove(playerId, out _);
            _pendingReceipts.TryRemove(playerId, out _);
            _walletReloadRequired.TryRemove(playerId, out _);
            _attributeRevisions.TryRemove(playerId, out _);
            _publishedVersions.TryRemove(playerId, out _);
            // 归还租约：仅当本实例确实持有时才释放，避免误删他实例租约。
            if (_leaseRenewedAt.TryRemove(playerId, out _) && ownership is not null)
                await ownership.ReleaseAsync(playerId, _ownerInstanceId, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc />
    public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId)
        => _logs.TryGetValue(playerId, out var log) ? log.SnapshotRecent() : [];

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<PlayerRuntimeSnapshot>>(_loaded.Values.OrderBy(value => value.PlayerId).ToArray());

    /// <summary>
    /// 邀请奖励的外部入账通知（注册结成/里程碑）：在线玩家标记钱包失效并向其会话推送
    /// 结构化入账日志；未加载玩家直接返回——余额已由持久层入账，下次装载自然读到。
    /// </summary>
    public async ValueTask NotifyInvitationRewardAsync(Guid playerId, string logTemplate)
    {
        if (!_loaded.ContainsKey(playerId)) return;
        _walletReloadRequired[playerId] = 0;
        var logEvent = AppendInvitationLog(playerId, logTemplate);
        if (LogBatchEmitted is { } handler) await handler(playerId, [logEvent]).ConfigureAwait(false);
    }

    /// <summary>
    /// 突破命令内的邀请里程碑结算：仓储内部幂等 + 钱与标记同事务；
    /// 发放失败只记日志不阻断已生效的突破——领取标记未翻转，下次突破自动幂等重试。
    /// </summary>
    private async ValueTask<IReadOnlyList<Server.Clean.Social.InvitationMilestoneGrant>?> GrantInvitationMilestonesAsync(
        Guid playerId, int realmLevel, CancellationToken cancellationToken)
    {
        try
        {
            return await invitations!.GrantMilestonesAsync(playerId, realmLevel, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "邀请里程碑奖励发放失败，下次突破将幂等重试 player={PlayerId} realm={RealmLevel}", playerId, realmLevel);
            return null;
        }
    }

    /// <summary>追加一条邀请入账日志到玩家回放缓冲；返回事件供当前会话实时推送。</summary>
    private LogEventDto AppendInvitationLog(Guid playerId, string template)
    {
        var buffer = _logs.GetOrAdd(playerId, static _ => new PlayerLogBuffer(LogBufferCapacity));
        var logEvent = new LogEventDto(buffer.NextSequence(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), template, 1, "system");
        buffer.Append(logEvent);
        return logEvent;
    }

    /// <summary>跳过引导的补发入账日志：主句 + {{item:id|名称|数量}} 引用列表（与 SimulationLogMapper 同语法）。</summary>
    private LogEventDto AppendTutorialSkipLog(Guid playerId, IReadOnlyDictionary<ItemId, double> grants)
    {
        var builder = new StringBuilder("新手引导已跳过，剩余奖励入账：");
        var first = true;
        foreach (var pair in grants.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal))
        {
            if (!first) builder.Append('、');
            var name = gameData.Items.TryGetValue(pair.Key, out var def) ? def.Name : pair.Key.Value;
            builder.Append("{{item:").Append(pair.Key.Value).Append('|').Append(name).Append('|')
                .AppendFormat(CultureInfo.InvariantCulture, "{0:0.######}", pair.Value).Append("}}");
            first = false;
        }
        var buffer = _logs.GetOrAdd(playerId, static _ => new PlayerLogBuffer(LogBufferCapacity));
        var logEvent = new LogEventDto(buffer.NextSequence(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), builder.ToString(), 1, "system");
        buffer.Append(logEvent);
        return logEvent;
    }

    /// <summary>
    /// 市场结算把资产计入该玩家余额（成交对手方/撤单与到期退款/结算领取）：
    /// 在线玩家的缓存钱包标记失效，下一次玩家门内推进重读存储余额并推进版本，
    /// 推送以新版本差量把入账带给客户端；未加载玩家的余额由下次装载读取，无需标记。
    /// 回调约束：仅做字典标记，不阻塞市场写门闩、不抛异常。
    /// </summary>
    public void NotifyMarketWalletCredited(Guid playerId)
    {
        if (_loaded.ContainsKey(playerId)) _walletReloadRequired[playerId] = 0;
    }

    /// <summary>
    /// 优雅停机：解除市场入账订阅后逐玩家强制落盘并卸载（单个失败只记日志不阻断其余玩家），
    /// 最后释放玩家 gate 并销毁 ECS 世界；重复调用幂等。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (marketRuntime is not null) marketRuntime.WalletCredited -= NotifyMarketWalletCredited;
        if (participationRuntime is not null) participationRuntime.WalletChanged -= NotifyMarketWalletCredited;
        foreach (var playerId in _loaded.Keys.ToArray())
        {
            try { await RemoveAsync(playerId, true, null, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) { _logger.LogError(exception, "ECS 玩家运行时关闭时释放失败 player={PlayerId}", playerId); }
        }
        _disposed = true;
        foreach (var gate in _playerGates.Values) gate.Dispose();
        await ecs.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask FlushLoadedAsync(Guid playerId, bool force, CancellationToken cancellationToken)
    {
        if (!_loaded.ContainsKey(playerId)) return;
        var gate = _playerGates.GetOrAdd(playerId, static _ => new SemaphoreSlim(1, 1));
        using (RuntimeInstrumentation.Measure("queue.actor_wait"))
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded.ContainsKey(playerId)) await FlushOwnedAsync(playerId, force, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async ValueTask FlushOwnedAsync(Guid playerId, bool force, CancellationToken cancellationToken)
    {
        try
        {
            await RenewOwnershipLeaseIfDueAsync(playerId, cancellationToken).ConfigureAwait(false);
            ThrowIfCommitFenced(playerId);
        }
        catch (PlayerOwnershipLostException)
        {
            // 所有权已转移：本实例内存脏状态不再权威，跳过本次刷盘；实体由命令/推进路径逐出。
            _logger.LogError("所有权已丢失，跳过玩家刷盘 player={PlayerId}", playerId);
            return;
        }
        TrackDirty(playerId);
        var dirty = ecs.GetDirtySections(playerId);
        var pending = _pendingReceipts[playerId];
        if (dirty.Length == 0 && pending.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var eligible = dirty.Where(section => force || pending.Count > 0
            || now - _dirtyAt[playerId][section] >= TimeSpan.FromSeconds(15)).ToHashSet();
        if (eligible.Count == 0 && pending.Count == 0) return;
        var snapshot = ToSnapshot(ecs.ExportSnapshot(playerId));
        var writes = SectionCodec.EncodeAggregate(snapshot.State, eligible, now);
        try
        {
            await sections.EnqueueSectionsAsync(writes, pending.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // durable queue 失败时保留原脏集合和 receipt，下一次非强制刷新也能立即重试。
            foreach (var section in eligible) _dirtyAt[playerId][section] = now.Subtract(TimeSpan.FromSeconds(15));
            throw;
        }
        ecs.AcknowledgeDirtySections(playerId, eligible);
        foreach (var section in eligible) _dirtyAt[playerId].Remove(section);
        pending.Clear();
    }

    private void TrackDirty(Guid playerId)
    {
        var now = DateTimeOffset.UtcNow;
        var times = _dirtyAt[playerId];
        foreach (var section in ecs.GetDirtySections(playerId)) times.TryAdd(section, now);
    }

    /// <summary>
    /// 所有权续租去抖：距上次成功续期不足租约 1/3（45s 租约 ≈ 15s）时跳过本次 UPDATE；
    /// 仅成功续期才刷新时间戳，到期续租返回 false 按 fail-closed 抛出 <see cref="PlayerOwnershipLostException"/>。
    /// 必须在玩家级 gate 内调用。
    /// </summary>
    private async ValueTask RenewOwnershipLeaseIfDueAsync(Guid playerId, CancellationToken cancellationToken, bool force = false)
    {
        if (ownership is null) return;
        if (!_leaseRenewedAt.TryGetValue(playerId, out var renewedAt)) throw new PlayerOwnershipLostException(playerId);
        if (!force && DateTimeOffset.UtcNow - renewedAt < OwnershipLease / 3) return;
        if (!await ownership.RenewAsync(playerId, _ownerInstanceId, OwnershipLease, cancellationToken).ConfigureAwait(false))
            throw new PlayerOwnershipLostException(playerId);
        _leaseRenewedAt[playerId] = DateTimeOffset.UtcNow;
    }

    /// <summary>锁外逐出：命令返回后释放 gate，再重新拿锁检查并移除——避免与并发命令竞态。</summary>
    private async ValueTask EvictOwnershipLostAsync(Guid playerId)
    {
        var gate = _playerGates.GetOrAdd(playerId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { if (_loaded.ContainsKey(playerId)) EvictOwnershipLostOwned(playerId); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// 租约已被其他实例接管时逐出玩家：本实例内存状态不再权威——直接移除 ECS 实体与全部
    /// 缓存，不刷盘（脏数据可能过期）、不归还租约（已非持有者）。调用方必须持有该玩家 gate。
    /// </summary>
    private void EvictOwnershipLostOwned(Guid playerId)
    {
        ecs.RemovePlayer(playerId);
        _loaded.TryRemove(playerId, out _);
        _wallets.TryRemove(playerId, out _);
        _dirtyAt.TryRemove(playerId, out _);
        _pendingReceipts.TryRemove(playerId, out _);
        _walletReloadRequired.TryRemove(playerId, out _);
        _logs.TryRemove(playerId, out _);
        _recentRequests.TryRemove(playerId, out _);
        _attributeRevisions.TryRemove(playerId, out _);
        _publishedVersions.TryRemove(playerId, out _);
        _leaseRenewedAt.TryRemove(playerId, out _);
        _logger.LogError("玩家运行时租约已失效，逐出 ECS 实体 player={PlayerId}", playerId);
    }

    /// <summary>队首行动参与会话是否进行中。</summary>
    private static bool IsActionParticipationActive(PlayerAggregateState state) =>
        state.Actions.Count > 0 && state.Actions[0].Participation is { } binding
            && binding.EndsAtUnixSeconds > state.LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d;

    /// <summary>按分段脏流水号差集得出本次操作触及分段；空集表示零触及（与"不可判定"的 null 区分）。</summary>
    private static HashSet<PlayerSectionKind> TouchedSectionsSince(
        IReadOnlyDictionary<PlayerSectionKind, long> marksBefore,
        IReadOnlyDictionary<PlayerSectionKind, long> marksAfter)
    {
        var touched = new HashSet<PlayerSectionKind>();
        foreach (var (section, mark) in marksAfter)
            if (!marksBefore.TryGetValue(section, out var previous) || previous != mark) touched.Add(section);
        return touched;
    }

    /// <summary>
    /// 成功回执的浮动气泡文案：只有需要文字确认的命令才给文本；高频操作或界面已自证
    /// （队列/装备即时刷新、market.place/cancel 前端发送时已弹方向专属提示）返回空串静默，
    /// 前端对空 message 不弹气泡。失败提示不走此表，仍在各分支内就地返回。
    /// </summary>
    private static string SuccessNotice(string command) => command switch
    {
        "ability.learn" => "功法参悟完毕。",
        "progression.breakthrough" => "突破成功，境界晋升。",
        "world.travel" => "已启程赶路。",
        "building.build" => "建造已发起。",
        "building.upgrade" => "升级已发起。",
        "building.contribute" => "材料已提交。",
        "market.claim" => "结算领取已提交。",
        "item.discard" => "物品已丢弃。",
        "item.sell" => "物品已售出。",
        "player.rename" => "道号已更新。",
        "player.set_avatar" => "本命形象已更新。",
        "player.unlock_avatar" => "本命形象已解锁并更新。",
        "trait.set" => "特质装配已保存。",
        "item.use" => "觉醒石使用成功，特质已觉醒或提升！",
        "shop.buy" => "购买成功，货物已入储物袋。",
        _ => string.Empty,
    };

    private async ValueTask<bool> RefreshNodeBuildingsAsync(Guid playerId, PlayerWorldStateDocument world,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (nodeBuildings is null) return false;
        if (!string.IsNullOrWhiteSpace(world.EdgeId) || string.IsNullOrWhiteSpace(world.NodeId))
            return ecs.ApplyNodeBuildingProjection(playerId, new(0, [], BuildingEffectSet.Empty, null, []), now);
        var nowUnixSeconds = now.ToUnixTimeMilliseconds() / 1000d;
        var view = await nodeBuildings.BuildingsForAsync(world.NodeId, now, cancellationToken).ConfigureAwait(false);
        var counts = await nodeBuildings.UsageForAsync(world.NodeId, now, cancellationToken).ConfigureAwait(false);
        var effects = NodeBuildingRules.AggregateEffects(view.Buildings, gameData.Buildings, nowUnixSeconds, counts);
        var usages = nodeBuildings.UsagesFor(view.Buildings, counts, nowUnixSeconds);
        return ecs.ApplyNodeBuildingProjection(playerId, new(view.Version, view.Buildings, effects, world.NodeId, usages), now);
    }

    private sealed record CommitOutcome(IReadOnlyList<LogEventDto>? Logs, SimulationResult? Simulation);

    private async ValueTask<CommitOutcome> CommitSimulationAsync(Guid playerId, PlayerRuntimeSnapshot before,
        PlayerRuntimeSnapshot after, CommandReceipt? receipt, IEnumerable<PlayerSectionKind> dirtySectionsBefore,
        CancellationToken cancellationToken)
    {
        var simulation = ecs.DrainSimulationResult(playerId);
        if (simulation is not null && (after.LastSimulatedAt != before.LastSimulatedAt
            || after.StateVersion != before.StateVersion || simulation.AssetDeltas.Count > 0))
            if (simulation.AssetDeltas.Count > 0)
            {
                var transaction = SimulationEconomyTransaction.Create(playerId, simulation, simulationTransactionIdentity);
                await CommitPlayerAssetsAsync(playerId, before, dirtySectionsBefore, transaction, receipt, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // 纯时钟与状态推进（零资产变动）：直接分段与回执队列落盘，严禁向不可变资产流水表（economy.transactions）写入空流水。
                await CommitPlayerSectionsAsync(playerId, before, dirtySectionsBefore, receipt, cancellationToken).ConfigureAwait(false);
            }
        else
        {
            if (receipt is not null) _pendingReceipts[playerId].Add(receipt);
            TrackDirty(playerId);
        }
        // Mapper 推进日志 sequence 与回放缓冲，只能在原子提交成功后执行。
        var events = ecs.DrainSimulationEvents(playerId);
        var logs = events.Count == 0 ? null : SimulationLogMapper.ToLogEvents(events, gameData,
            _logs.GetOrAdd(playerId, static _ => new PlayerLogBuffer(LogBufferCapacity)), LogBufferCapacity);
        return new(logs, simulation);
    }

    /// <summary>纯时钟/分段推进提交点：直接走分段与回执持久化队列，避免向不可变资产表（economy.transactions）写入空流水。</summary>
    private async ValueTask CommitPlayerSectionsAsync(Guid playerId,
        PlayerRuntimeSnapshot before, IEnumerable<PlayerSectionKind> dirtySectionsBefore,
        CommandReceipt? receipt, CancellationToken cancellationToken)
    {
        var dirtyBefore = dirtySectionsBefore.ToArray();
        var dirty = ecs.GetDirtySections(playerId).ToHashSet();
        var after = ToSnapshot(ecs.ExportSnapshot(playerId));
        if (after.LastSimulatedAt != before.LastSimulatedAt)
        {
            if (after.StateVersion == before.StateVersion)
            {
                ecs.MarkExternalStateChanged(playerId);
                after = ToSnapshot(ecs.ExportSnapshot(playerId));
            }
            dirty.Add(PlayerSectionKind.Progression);
        }
        var writes = SectionCodec.EncodeAggregate(after.State, dirty, DateTimeOffset.UtcNow);
        var receipts = _pendingReceipts[playerId].ToList();
        if (receipt is not null) receipts.Add(receipt);
        try
        {
            await sections.EnqueueSectionsAsync(writes, receipts.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RestoreUncommittedPlayer(playerId, before, dirtyBefore);
            throw;
        }
        var writtenKinds = writes.Select(write => SectionCodec.FromStorageKind(write.SectionKind)).ToArray();
        ecs.AcknowledgeDirtySections(playerId, writtenKinds);
        foreach (var kind in writtenKinds) _dirtyAt[playerId].Remove(kind);
        _pendingReceipts[playerId].Clear();
        _loaded[playerId] = after;
    }

    /// <summary>学习、引导和模拟共用的提交点：请求前快照/脏集仅用于已知未提交的恢复，提交后才确认脏段。</summary>
    private async ValueTask<EconomyTransactionResult> CommitPlayerAssetsAsync(Guid playerId,
        PlayerRuntimeSnapshot before, IEnumerable<PlayerSectionKind> dirtySectionsBefore,
        EconomyTransactionRequest request, CommandReceipt? receipt, CancellationToken cancellationToken)
    {
        var dirtyBefore = dirtySectionsBefore.ToArray();
        var dirty = ecs.GetDirtySections(playerId).ToHashSet();
        var after = ToSnapshot(ecs.ExportSnapshot(playerId));
        if (after.LastSimulatedAt != before.LastSimulatedAt)
        {
            if (after.StateVersion == before.StateVersion)
            {
                ecs.MarkExternalStateChanged(playerId);
                after = ToSnapshot(ecs.ExportSnapshot(playerId));
            }
            dirty.Add(PlayerSectionKind.Progression);
        }
        var writes = SectionCodec.EncodeAggregate(after.State, dirty, DateTimeOffset.UtcNow);
        var receipts = _pendingReceipts[playerId].ToList();
        if (receipt is not null) receipts.Add(receipt);
        var stableRequest = request with { Sections = writes, Receipts = receipts.ToArray() };
        EconomyTransactionResult result;
        try
        {
            result = await CommitWithConfirmationAsync(playerId, stableRequest, cancellationToken).ConfigureAwait(false);
            if (!result.Applied) throw new InvalidOperationException("玩家资产与状态事务未提交。");
        }
        catch (EconomyCommitOutcomeUnknownException) { throw; }
        catch
        {
            RestoreUncommittedPlayer(playerId, before, dirtyBefore);
            throw;
        }
        var writtenKinds = writes.Select(write => SectionCodec.FromStorageKind(write.SectionKind)).ToArray();
        ecs.AcknowledgeDirtySections(playerId, writtenKinds);
        foreach (var kind in writtenKinds) _dirtyAt[playerId].Remove(kind);
        _pendingReceipts[playerId].Clear();
        _loaded[playerId] = after;
        try { await SyncWalletAfterCommitAsync(playerId, result, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception)
        {
            _walletReloadRequired[playerId] = 0;
            _logger.LogError(exception, "玩家原子事务已提交但钱包同步失败 player={PlayerId} transaction={TransactionId}", playerId, result.TransactionId);
            throw;
        }
        return result;
    }

    private async ValueTask<EconomyTransactionResult> CommitWithConfirmationAsync(Guid playerId,
        EconomyTransactionRequest request, CancellationToken cancellationToken)
    {
        try { return await economy.CommitAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (EconomyCommitOutcomeUnknownException unknown)
        {
            // 原请求精确重放：同一事务标识、幂等键、分段校验和和回执；不重算资产，也不受原客户端取消影响。
            try
            {
                using var confirmation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var result = await economy.CommitAsync(request, confirmation.Token).ConfigureAwait(false);
                if (!result.Applied) throw new InvalidOperationException("提交结局核验未获得确定成功结果。");
                return result;
            }
            catch (Exception confirmationError)
            {
                unknown.Data["ConfirmationError"] = confirmationError;
                _fencedCommits[playerId] = unknown;
                _loaded[playerId] = ToSnapshot(ecs.ExportSnapshot(playerId));
                _logger.LogCritical(unknown, "资产提交结局无法确认，保留玩家租约并禁止后续写入 player={PlayerId} transaction={TransactionId}", playerId, request.TransactionId);
                throw unknown;
            }
        }
    }

    private void ThrowIfCommitFenced(Guid playerId)
    {
        if (_fencedCommits.TryGetValue(playerId, out var unknown)) throw unknown;
    }

    /// <summary>失效标记存在时重读钱包；返回缓存余额是否真实变化（市场外部入账检测用）。</summary>
    private async ValueTask<bool> EnsureWalletFreshAsync(Guid playerId, CancellationToken cancellationToken)
    {
        if (!_walletReloadRequired.ContainsKey(playerId)) return false;
        return await ReloadWalletAsync(playerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>从存储权威重读钱包并同步 ECS 库存组件；返回重读前后余额是否变化。</summary>
    private async ValueTask<bool> ReloadWalletAsync(Guid playerId, CancellationToken cancellationToken)
    {
        _walletReloadRequired.TryRemove(playerId, out _);
        try
        {
            var rows = await assets.LoadBalancesAsync(playerId, cancellationToken).ConfigureAwait(false);
            var changed = WalletChanged(playerId, rows);
            _wallets[playerId] = rows.ToDictionary(row => new AssetKey(new ItemId(row.AssetId), row.Variant));
            SyncEcsWallet(playerId);
            return changed;
        }
        catch
        {
            _walletReloadRequired[playerId] = 0;
            throw;
        }
    }

    /// <summary>比较重读结果与缓存钱包：数量增减、条目新增/移除任一差异即视为变化（版本号不参与判定）。</summary>
    private bool WalletChanged(Guid playerId, IReadOnlyList<AssetBalanceRecord> rows)
    {
        if (!_wallets.TryGetValue(playerId, out var cached) || cached.Count != rows.Count) return true;
        foreach (var row in rows)
        {
            if (!cached.TryGetValue(new AssetKey(new ItemId(row.AssetId), row.Variant), out var existing)
                || existing.Quantity != row.Quantity) return true;
        }
        return false;
    }

    private void SyncEcsWallet(Guid playerId)
        => ecs.SyncAssetBalances(playerId, _wallets[playerId].Select(pair =>
            new AssetBalance(new PlayerId(playerId), pair.Key, pair.Value.Quantity, pair.Value.Version)).ToArray());

    /// <summary>把宿主钱包重建为领域 AssetWallet 视图：装备校验与余额判定只读取，不经持久化。</summary>
    private AssetWallet WalletOf(Guid playerId)
    {
        var wallet = new AssetWallet(new PlayerId(playerId));
        wallet.Restore(_wallets[playerId].Select(pair =>
            new AssetBalance(new PlayerId(playerId), pair.Key, pair.Value.Quantity, pair.Value.Version)));
        return wallet;
    }

    /// <summary>经济事务提交后的钱包同步：返回余额则合并版本，否则强制下次从存储重读。</summary>
    private async ValueTask SyncWalletAfterCommitAsync(Guid playerId, EconomyTransactionResult result, CancellationToken cancellationToken)
    {
        if (result.Balances is { } returned)
        {
            foreach (var balance in returned)
            {
                var key = new AssetKey(new ItemId(balance.AssetId), balance.Variant);
                if (!_wallets[playerId].TryGetValue(key, out var current) || current.Version <= balance.Version)
                    _wallets[playerId][key] = balance;
            }
            SyncEcsWallet(playerId);
            return;
        }
        await ReloadWalletAsync(playerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>本侧弃战产生的对手判胜通知：对手已加载时在其锁内记账，并广播对方快照（次序沿袭历史 OOP 运行时基线）。</summary>
    private async ValueTask RouteDuelForfeitAsync((Guid DuelId, Guid PeerId) forfeit,
        List<RuntimeNotification> notifications, CancellationToken cancellationToken)
    {
        if (forfeit.PeerId == Guid.Empty || !_loaded.ContainsKey(forfeit.PeerId)) return;
        var peerGate = _playerGates.GetOrAdd(forfeit.PeerId, static _ => new SemaphoreSlim(1, 1));
        await peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RenewOwnershipLeaseIfDueAsync(forfeit.PeerId, cancellationToken, force: true).ConfigureAwait(false);
            ThrowIfCommitFenced(forfeit.PeerId);
            var applied = false;
            ecs.MutatePlayer(forfeit.PeerId, aggregate => applied = aggregate.ApplyPeerForfeit(forfeit.DuelId));
            if (!applied) return;
            // 对手门外的标脏必须进刷盘轨道：若对方此后无操作，判胜结果不能丢在内存脏集里。
            TrackDirty(forfeit.PeerId);
            var peerAfter = ToSnapshot(ecs.ExportSnapshot(forfeit.PeerId));
            _loaded[forfeit.PeerId] = peerAfter;
            notifications.Add(new RuntimeNotification(forfeit.PeerId, null, peerAfter));
        }
        finally { peerGate.Release(); }
    }

    /// <summary>命令回执持久化清单（口径沿袭历史 OOP 运行时基线）；market.place/cancel 走订单幂等键不入回执。</summary>
    private static bool ShouldPersistReceipt(string command) => command is
        "action.enqueue" or "action.remove" or
        "queue.configure" or "queue.move" or "queue.clear" or "queue.auto_use.configure" or
        "loadout.replace" or "ability.learn" or "progression.breakthrough" or "world.travel" or
        "building.build" or "building.upgrade" or "building.contribute" or "player.rename" or "player.set_avatar" or "player.unlock_avatar" or "market.claim" or
        "item.discard" or "item.sell" or "trait.set" or "item.use" or "tutorial.skip" or "tutorial.restart" or "simulation.advance";

    private void RestoreUncommittedPlayer(Guid playerId, PlayerRuntimeSnapshot before,
        IEnumerable<PlayerSectionKind> dirtySectionsBefore)
    {
        var balances = before.Assets.Select(row => new AssetBalance(new PlayerId(playerId),
            new AssetKey(new ItemId(row.AssetId), row.Variant), row.Quantity, row.Version)).ToArray();
        ecs.RestorePlayer(playerId, before.State, balances);
        var dirty = dirtySectionsBefore.ToArray();
        ecs.RestoreDirtySections(playerId, dirty);
        var dirtySet = dirty.ToHashSet();
        foreach (var kind in _dirtyAt[playerId].Keys.Where(kind => !dirtySet.Contains(kind)).ToArray())
            _dirtyAt[playerId].Remove(kind);
        TrackDirty(playerId);
        // 恢复实体即可：宿主已发布快照尚未前移，必须保留其触及分段/属性增量元数据；装载失败也不应创建缓存。
        ecs.DrainSimulationResult(playerId);
        ecs.DrainSimulationEvents(playerId);
    }

    private async ValueTask EmitNotificationsAsync(List<RuntimeNotification> notifications)
    {
        // 网络订阅者只能在玩家锁外执行；缓冲与快照已经在锁内一起前移。
        // 先登记本次派发的全部已发布版本：同一派发内同玩家的多条快照（对账中段与命令后态）
        // 都计入滑窗——版本可能因锁内后移的未推送推进而乱序，集合语义天然容忍。
        foreach (var notification in notifications)
            if (notification.Snapshot is { } snapshot)
                _publishedVersions.GetOrAdd(notification.PlayerId, static _ => new RecentPublished())
                    .Record(snapshot.StateVersion);
        foreach (var notification in notifications)
        {
            if (notification.Logs is { Count: > 0 } logs && LogBatchEmitted is { } logHandler)
                await logHandler(notification.PlayerId, logs).ConfigureAwait(false);
            if (notification.Snapshot is { } snapshot && SnapshotUpdated is { } snapshotHandler)
                await snapshotHandler(snapshot).ConfigureAwait(false);
        }
    }

    private static CommandReceipt CreateReceipt(Guid playerId, Guid requestId, string command,
        string payloadHash, long stateVersion)
    {
        var now = DateTimeOffset.UtcNow;
        return new CommandReceipt(playerId, requestId, command, payloadHash, stateVersion, now, now.AddMinutes(30));
    }

    private static bool TryReadGuid(JsonElement payload, string property, out Guid value)
    {
        value = Guid.Empty;
        return payload.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out value);
    }

    private PlayerRuntimeSnapshot ToSnapshot(EcsPlayerSnapshot snapshot)
        => new(snapshot.PlayerId, snapshot.State.AccountId.Value.ToString("D"), snapshot.State.DisplayName,
            snapshot.State.StateVersion, snapshot.State.LastSimulatedAt, snapshot.State, snapshot.State.Actions.Count,
            snapshot.Assets.Select(item => new AssetBalanceRecord(snapshot.PlayerId, item.Key.ItemId.Value,
                item.Key.Variant, item.Value, _wallets[snapshot.PlayerId].GetValueOrDefault(item.Key)?.Version ?? 0)).ToArray());
}
