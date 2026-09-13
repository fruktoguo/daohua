using Domain.Player;
using Domain.Simulation;
using Domain.GameData;
using Domain.Common;
using Domain.Economy;
using Domain.World;
using Protocol;

namespace Server.Clean;

/// <summary>
/// 角色运行时只读快照。
/// 包含内存聚合不可变状态、数据库持久化排队动作与当前资产钱包余额。
/// </summary>
public sealed record PlayerRuntimeSnapshot(
    Guid PlayerId,
    string AccountId,
    string DisplayName,
    long StateVersion,
    DateTimeOffset LastSimulatedAt,
    PlayerAggregateState State,
    IReadOnlyList<StoredAction> Actions,
    IReadOnlyList<AssetBalanceRecord> Assets);

/// <summary>
/// 玩家指令执行回执契约包。
/// 包含执行成功标志、领域/协议错误代码、执行后最新快照以及产生的战报日志。
/// </summary>
public sealed record PlayerCommandResult(
    bool Success,
    string Code,
    string Message,
    PlayerRuntimeSnapshot Snapshot,
    SimulationResult? Simulation = null,
    IReadOnlyList<LogEventDto>? LogEvents = null);

/// <summary>
/// 在线玩家单写者运行时：内存持有完整领域聚合，跨边界只交换消息和 section 载荷。
/// 每个活跃角色对应一个单线程串行的 PlayerActor，由本地信号量保证指令与模拟推进的严格有序性。
/// </summary>
public sealed class PlayerRuntime(
    IPlayerStateRepository repository,
    IAssetBalanceRepository assetRepository,
    IEconomyTransactionStore economy,
    GameDataCatalog gameData,
    ILogger<PlayerRuntime> logger,
    MarketRuntime? marketRuntime = null,
    ICommandReceiptStore? commandReceipts = null,
    IPlayerOwnershipStore? ownership = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<PlayerActor>>> _actors = new();
    private readonly ActionCatalog _actions = gameData.Actions;
    private readonly Guid _ownerInstanceId = Guid.NewGuid();
    private readonly TimeSpan _ownershipLease = TimeSpan.FromSeconds(45);
    private readonly ConcurrentDictionary<Guid, PlayerLogBuffer> _logBuffers = new();

    /// <summary>每玩家战报日志环形缓冲容量：保留最近 200 条用于重连回放。</summary>
    private const int LogBufferCapacity = 200;

    /// <summary>当前已加载并在内存中活跃的玩家 Actor 实例数</summary>
    public int LoadedCount => _actors.Count;
    /// <summary>快照更新事件广播（供 WebSocket 推送订阅）</summary>
    public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated;

    /// <summary>玩家战报日志批次推送；参数为玩家 ID 与本批日志，订阅者必须按玩家过滤。</summary>
    public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted;

    /// <summary>加载并获取指定玩家的最新运行时快照</summary>
    public async ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        if (playerId == Guid.Empty) throw new ArgumentException("PlayerId 不能为空。", nameof(playerId));
        var actor = await GetOrAddActorAsync(playerId, cancellationToken);
        return await actor.SnapshotAsync(cancellationToken);
    }

    public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(playerId, command, payload, 0, TryReadRequestId(payload), cancellationToken);

    /// <summary>在玩家单写者锁内校验期望版本，避免 HTTP/WS 先读后写的竞态。</summary>
    public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, long expectedStateVersion, CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(playerId, command, payload, expectedStateVersion, TryReadRequestId(payload), cancellationToken);

    public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, long expectedStateVersion, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("RequestId 不能为空。", nameof(requestId));
        return ExecuteCoreAsync(playerId, command, payload, expectedStateVersion, requestId, cancellationToken);
    }

    private async ValueTask<PlayerCommandResult> ExecuteCoreAsync(Guid playerId, string command, JsonElement payload, long expectedStateVersion, Guid? requestId, CancellationToken cancellationToken)
    {
        var actor = await GetOrAddActorAsync(playerId, cancellationToken);
        var result = await actor.ExecuteAsync(command, payload, expectedStateVersion, requestId, cancellationToken);
        // 所有权失效的清理必须在命令返回后、锁外执行：在锁内触发释放会让同链路的
        // 下一条命令撞上已销毁的信号量。失效后后续命令走重新加载或拿到明确失败。
        if (result.Code == "PLAYER_OWNERSHIP_LOST") EvictOwnershipLostActor(playerId, actor);
        // 日志推送在单写者锁外进行，慢客户端不能反向阻塞命令与模拟推进。
        await EmitLogBatchAsync(playerId, result.LogEvents);
        return result;
    }

    private async ValueTask<PlayerActor> GetOrAddActorAsync(Guid playerId, CancellationToken cancellationToken)
    {
        // Lazy 工厂固定使用 CancellationToken.None：actor 创建与初始模拟推进不能被首个调用者的
        // 请求取消连带中断；调用方 token 只用于等待已缓存的任务。
        var lazy = _actors.GetOrAdd(playerId, id => new Lazy<Task<PlayerActor>>(() => CreateActorAsync(id, CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication));
        return await AwaitActorAsync(playerId, lazy, cancellationToken);
    }

    private async ValueTask<PlayerActor> AwaitActorAsync(Guid playerId, Lazy<Task<PlayerActor>> lazy, CancellationToken cancellationToken)
    {
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 等待被取消不代表创建失败：任务可能仍在执行，保留条目避免重复创建。
            throw;
        }
        catch
        {
            // 创建失败必须移除缓存的失败条目，否则该玩家后续所有命令会永久重放同一异常直到重启。
            _actors.TryRemove(new KeyValuePair<Guid, Lazy<Task<PlayerActor>>>(playerId, lazy));
            throw;
        }
    }

    public async ValueTask FlushAllAsync(bool force, CancellationToken cancellationToken = default)
    {
        foreach (var entry in _actors)
        {
            if (!entry.Value.IsValueCreated) continue;
            var actor = await AwaitActorAsync(entry.Key, entry.Value, cancellationToken);
            await actor.FlushAsync(force, cancellationToken);
        }
    }

    /// <summary>推进当前已加载玩家的确定性模拟；只在状态实际变化时向在线会话广播增量。</summary>
    public async ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
    {
        foreach (var entry in _actors)
        {
            if (!entry.Value.IsValueCreated) continue;
            var actor = await AwaitActorAsync(entry.Key, entry.Value, cancellationToken);
            var result = await actor.AdvanceSimulationAsync(target, cancellationToken);
            if (result is null) continue;
            // 模拟推进同样会撞上所有权失效：从缓存清除并继续推进其余玩家。
            if (result.Code == "PLAYER_OWNERSHIP_LOST")
            {
                EvictOwnershipLostActor(entry.Key, actor);
                continue;
            }
            await EmitLogBatchAsync(entry.Key, result.LogEvents);
            if (result.Simulation is not { Changed: true }) continue;
            var handler = SnapshotUpdated;
            if (handler is not null) await handler(result.Snapshot);
        }
    }

    public async ValueTask FlushAsync(Guid playerId, bool force, CancellationToken cancellationToken = default)
    {
        if (!_actors.TryGetValue(playerId, out var lazy) || !lazy.IsValueCreated) return;
        var actor = await AwaitActorAsync(playerId, lazy, cancellationToken);
        await actor.FlushAsync(force, cancellationToken);
    }

    public async ValueTask RemoveAsync(Guid playerId, bool flush, CancellationToken cancellationToken = default)
    {
        if (!_actors.TryGetValue(playerId, out var lazy) || !lazy.IsValueCreated) return;
        var actor = await AwaitActorAsync(playerId, lazy, cancellationToken);
        if (flush) await actor.FlushAsync(true, cancellationToken);
        _actors.TryRemove(new KeyValuePair<Guid, Lazy<Task<PlayerActor>>>(playerId, lazy));
        await actor.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var playerId in _actors.Keys.ToArray())
        {
            try { await RemoveAsync(playerId, flush: true, CancellationToken.None); }
            catch (Exception exception) { logger.LogError(exception, "玩家运行时关闭时释放失败 player={PlayerId}", playerId); }
        }
    }

    private async Task<PlayerActor> CreateActorAsync(Guid playerId, CancellationToken cancellationToken)
    {
        if (ownership is not null && !await ownership.AcquireAsync(playerId, _ownerInstanceId, _ownershipLease, cancellationToken))
            throw new PlayerOwnershipLostException(playerId);
        try
        {
            var bundle = await repository.LoadAsync(playerId, cancellationToken)
                ?? throw new InvalidOperationException($"玩家 {playerId} 不存在。");
        PlayerAggregate aggregate;
        if (bundle.Head.StateVersion == 0 && bundle.Sections.Count == 0)
        {
            aggregate = PlayerAggregate.Create(
                new PlayerId(bundle.Identity.PlayerId),
                new AccountId(bundle.Identity.AccountId),
                bundle.Identity.DisplayName,
                bundle.Identity.CreatedAt,
                gameData.Fingerprint);
        }
        else
        {
            var state = SectionCodec.DecodeAggregate(bundle);
            aggregate = PlayerAggregate.Restore(state);
            var previousFingerprint = aggregate.GameDataFingerprint;
            if (aggregate.RebindGameDataFingerprint(gameData.Fingerprint))
            {
                logger.LogInformation(
                    "玩家存档已切换到当前游戏数据版本 player={PlayerId} previousFingerprint={PreviousFingerprint} currentFingerprint={CurrentFingerprint}",
                    playerId,
                    previousFingerprint,
                    gameData.Fingerprint);
            }
            aggregate.SetPresence(PresenceState.Online);
        }
            var balances = await assetRepository.LoadBalancesAsync(playerId, cancellationToken);
            var wallet = new AssetWallet(new PlayerId(playerId));
            wallet.Restore(balances.Select(balance => new AssetBalance(
                new PlayerId(balance.PlayerId),
                new AssetKey(new ItemId(balance.AssetId), balance.Variant),
                balance.Quantity,
                balance.Version)));
            var logBuffer = _logBuffers.GetOrAdd(playerId, _ => new PlayerLogBuffer(LogBufferCapacity));
            var actor = new PlayerActor(aggregate, bundle, wallet, assetRepository, repository, economy, _actions, gameData, logBuffer, logger, marketRuntime, commandReceipts, ownership, _ownerInstanceId, _ownershipLease);
            var advanced = await actor.AdvanceSimulationAsync(DateTimeOffset.UtcNow, cancellationToken);
            // 创建时的初始推进可能结算离线行动，产生的战报日志同样在锁外推送。
            await EmitLogBatchAsync(playerId, advanced?.LogEvents);
            return actor;
        }
        catch
        {
            if (ownership is not null) await ownership.ReleaseAsync(playerId, _ownerInstanceId, CancellationToken.None);
            throw;
        }
    }

    /// <summary>所有权已转移到其他实例：把 actor 移出缓存并在锁外异步释放，
    /// 已解析到旧 actor 的并发命令会拿到明确的会话关闭错误，后续命令走重新加载。</summary>
    private void EvictOwnershipLostActor(Guid playerId, PlayerActor actor)
    {
        if (_actors.TryGetValue(playerId, out var lazy)
            && lazy.IsValueCreated
            && lazy.Value.Status == TaskStatus.RanToCompletion
            && ReferenceEquals(lazy.Value.Result, actor)
            && _actors.TryRemove(new KeyValuePair<Guid, Lazy<Task<PlayerActor>>>(playerId, lazy)))
        {
            _ = ReleaseActorQuietlyAsync(playerId, actor);
        }
    }

    private async Task ReleaseActorQuietlyAsync(Guid playerId, PlayerActor actor)
    {
        try
        {
            await actor.DisposeAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "所有权丢失后释放玩家 actor 失败 player={PlayerId}", playerId);
        }
    }

    private async ValueTask EmitLogBatchAsync(Guid playerId, IReadOnlyList<LogEventDto>? events)
    {
        if (events is not { Count: > 0 }) return;
        var handler = LogBatchEmitted;
        if (handler is not null) await handler(playerId, events);
    }

    /// <summary>读取玩家最近战报日志（用于重连回放）；缓冲在进程生命周期内保留。</summary>
    public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId) =>
        _logBuffers.TryGetValue(playerId, out var buffer) ? buffer.SnapshotRecent() : [];

    private static Guid? TryReadRequestId(JsonElement payload)
    {
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("requestId", out var value)
            && value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out var requestId)
            && requestId != Guid.Empty
            ? requestId
            : null;
    }

    private sealed class PlayerActor : IAsyncDisposable
    {
        private readonly PlayerAggregate aggregate;
        private readonly PlayerStateBundle bundle;
        private readonly AssetWallet wallet;
        private readonly IAssetBalanceRepository assetRepository;
        private readonly IPlayerStateRepository repository;
        private readonly IEconomyTransactionStore economy;
        private readonly ActionCatalog actions;
        private readonly GameDataCatalog gameData;
        private readonly PlayerLogBuffer logBuffer;
        private readonly ILogger logger;
        private readonly MarketRuntime? marketRuntime;
        private readonly ICommandReceiptStore? commandReceipts;
        private readonly IPlayerOwnershipStore? ownership;
        private readonly Guid ownerInstanceId;
        private readonly TimeSpan ownershipLease;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, DateTimeOffset> _dirtyAt = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, (string Command, string PayloadHash)> _recentRequests = new();
        private readonly Queue<Guid> _recentRequestOrder = new();
        private readonly List<CommandReceipt> _pendingReceipts = [];
        private readonly Dictionary<string, long> _sectionVersions;
        private readonly LogThrottle _walletErrorLog = new(TimeSpan.FromMinutes(1));
        private long _logicalVersion;
        private bool _walletReloadRequired;
        private bool _disposed;
        private int _disposeStarted;

        public PlayerActor(
            PlayerAggregate aggregate,
            PlayerStateBundle bundle,
            AssetWallet wallet,
            IAssetBalanceRepository assetRepository,
            IPlayerStateRepository repository,
            IEconomyTransactionStore economy,
            ActionCatalog actions,
            GameDataCatalog gameData,
            PlayerLogBuffer logBuffer,
            ILogger logger,
            MarketRuntime? marketRuntime,
            ICommandReceiptStore? commandReceipts,
            IPlayerOwnershipStore? ownership,
            Guid ownerInstanceId,
            TimeSpan ownershipLease)
        {
            this.aggregate = aggregate;
            this.bundle = bundle;
            this.wallet = wallet;
            this.assetRepository = assetRepository;
            this.repository = repository;
            this.economy = economy;
            this.actions = actions;
            this.gameData = gameData;
            this.logBuffer = logBuffer;
            this.logger = logger;
            this.marketRuntime = marketRuntime;
            this.commandReceipts = commandReceipts;
            this.ownership = ownership;
            this.ownerInstanceId = ownerInstanceId;
            this.ownershipLease = ownershipLease;
            _sectionVersions = bundle.Sections.ToDictionary(x => x.Key, x => x.Value.StateVersion, StringComparer.Ordinal);
            _logicalVersion = Math.Max(0, aggregate.StateVersion);
            foreach (var section in aggregate.DirtySections) _dirtyAt[SectionCodec.ToStorageKind(section)] = DateTimeOffset.UtcNow;
        }

        /// <summary>加锁读取快照；仅供未持有单写者锁的外部路径调用，锁内路径必须使用无锁 Snapshot()。</summary>
        public async ValueTask<PlayerRuntimeSnapshot> SnapshotAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try { return Snapshot(); }
            finally { _gate.Release(); }
        }

        /// <summary>无锁快照投影；调用方必须已持有单写者锁。</summary>
        private PlayerRuntimeSnapshot Snapshot() => new(
            aggregate.PlayerId.Value,
            aggregate.AccountId.Value.ToString("D"),
            aggregate.DisplayName,
            Math.Max(_logicalVersion, aggregate.StateVersion),
            aggregate.LastSimulatedAt,
            aggregate.ExportState() with { StateVersion = Math.Max(_logicalVersion, aggregate.StateVersion) },
            BuildStoredActions(),
            wallet.Balances
                .OrderBy(balance => balance.Asset.ItemId.Value, StringComparer.Ordinal)
                .ThenBy(balance => balance.Asset.Variant, StringComparer.Ordinal)
                .Select(balance => new AssetBalanceRecord(
                    balance.PlayerId.Value,
                    balance.Asset.ItemId.Value,
                    balance.Asset.Variant,
                    balance.Quantity,
                    balance.Version))
                .ToArray());

        public async ValueTask<PlayerCommandResult> ExecuteAsync(string command, JsonElement payload, long expectedStateVersion, Guid? requestId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PlayerActor));
                if (ownership is not null && !await ownership.RenewAsync(aggregate.PlayerId.Value, ownerInstanceId, ownershipLease, cancellationToken))
                    throw new PlayerOwnershipLostException(aggregate.PlayerId.Value);
                if (string.IsNullOrWhiteSpace(command) || command.Length > 96)
                    return Fail("INVALID_COMMAND", "命令名称无效。");
                if (requestId.HasValue)
                {
                    if (payload.ValueKind != JsonValueKind.Object
                        || !payload.TryGetProperty("requestId", out var requestElement)
                        || requestElement.ValueKind != JsonValueKind.String
                        || !Guid.TryParse(requestElement.GetString(), out var payloadRequestId)
                        || payloadRequestId != requestId.Value)
                        return Fail("REQUEST_ID_MISMATCH", "命令载荷中的请求 ID 无效或不一致。");
                }
                var payloadHash = requestId.HasValue ? ComputePayloadHash(payload) : string.Empty;
                if (requestId.HasValue && _recentRequests.TryGetValue(requestId.Value, out var previous))
                {
                    if (!string.Equals(previous.Command, command, StringComparison.Ordinal) || !string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
                        return Fail("REQUEST_ID_CONFLICT", "请求 ID 已用于其他命令。");
                    return new PlayerCommandResult(true, "IDEMPOTENT_REPLAY", "命令已处理。", Snapshot());
                }
                if (requestId.HasValue && commandReceipts is not null && ShouldPersistReceipt(command))
                {
                    var persisted = await commandReceipts.FindAsync(aggregate.PlayerId.Value, requestId.Value, cancellationToken);
                    if (persisted is not null)
                    {
                        if (!string.Equals(persisted.Command, command, StringComparison.Ordinal)
                            || !string.Equals(persisted.PayloadHash, payloadHash, StringComparison.Ordinal))
                            return Fail("REQUEST_ID_CONFLICT", "请求 ID 已用于其他命令。");
                        RememberRequest(requestId.Value, command, payloadHash);
                        return new PlayerCommandResult(true, "IDEMPOTENT_REPLAY", "命令已处理。", Snapshot());
                    }
                }
                if (expectedStateVersion > 0 && Math.Max(_logicalVersion, aggregate.StateVersion) != expectedStateVersion)
                    return Fail("STATE_VERSION_CONFLICT", "玩家状态版本已变化。");
                SimulationResult? simulation = null;
                switch (command)
                {
                    case "action.enqueue":
                    {
                        var request = JsonSerializer.Deserialize<ActionIntentRequest>(payload.GetRawText(), ProtocolJson.Options)
                            ?? throw new JsonException("行动请求为空。");
                        if (!Guid.TryParse(request.RequestId, out _)) return Fail("INVALID_REQUEST_ID", "请求 ID 无效。");
                        var intent = new ActionIntent(new ActionId(request.ActionId), request.RepeatCount, request.TargetNodeId, request.Parameters);
                        aggregate.Enqueue(intent, DateTimeOffset.UtcNow, actions);
                        Touch();
                        break;
                    }
                    case "action.remove":
                    {
                        if (!payload.TryGetProperty("actionInstanceId", out var id) || !Guid.TryParse(id.GetString(), out var actionId)) return Fail("INVALID_ACTION_ID", "行动实例 ID 无效。");
                        if (aggregate.Actions.Count > 0 && aggregate.Actions[0].Id == actionId)
                        {
                            var now = DateTimeOffset.UtcNow;
                            if (now > aggregate.LastSimulatedAt)
                            {
                                var preAdvanceSim = await AdvanceSimulationInternalAsync(now, cancellationToken);
                                if (preAdvanceSim.Changed) simulation = preAdvanceSim;
                            }
                        }
                        if (!aggregate.RemoveAction(actionId)) return Fail("ACTION_NOT_FOUND", "行动不存在。");
                        Touch();
                        break;
                    }
                    case "queue.configure":
                    {
                        if (!payload.TryGetProperty("loopEnabled", out var loopEnabled) || loopEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return Fail("INVALID_PAYLOAD", "循环设置无效。");
                        aggregate.SetLoopEnabled(loopEnabled.GetBoolean());
                        TouchDirtyOnly();
                        break;
                    }
                    case "queue.move":
                    {
                        if (!TryReadGuid(payload, "actionInstanceId", out var actionInstanceId)
                            || !payload.TryGetProperty("direction", out var directionElement)
                            || !directionElement.TryGetInt32(out var direction))
                            return Fail("INVALID_PAYLOAD", "行动移动参数无效。");
                        var now = DateTimeOffset.UtcNow;
                        if (now > aggregate.LastSimulatedAt)
                        {
                            var preAdvanceSim = await AdvanceSimulationInternalAsync(now, cancellationToken);
                            if (preAdvanceSim.Changed) simulation = preAdvanceSim;
                        }
                        if (!aggregate.MoveAction(actionInstanceId, direction)) return Fail("ACTION_NOT_FOUND", "行动不存在或已在边界位置。");
                        TouchDirtyOnly();
                        break;
                    }
                    case "queue.auto_use.configure":
                    {
                        var actionType = payload.TryGetProperty("actionType", out var actionTypeElement) ? actionTypeElement.GetString() : null;
                        var slotIndex = payload.TryGetProperty("slotIndex", out var slotIndexElement) && slotIndexElement.TryGetInt32(out var parsedSlot) ? parsedSlot : -1;
                        var itemId = payload.TryGetProperty("itemId", out var itemElement) && itemElement.ValueKind != JsonValueKind.Null ? itemElement.GetString() : null;
                        var quantity = payload.TryGetProperty("quantity", out var quantityElement) && quantityElement.TryGetDouble(out var parsedQuantity) ? parsedQuantity : 1d;
                        var enabled = payload.TryGetProperty("enabled", out var enabledElement) && enabledElement.ValueKind == JsonValueKind.True;
                        aggregate.ConfigureAutoUse(actionType ?? string.Empty, slotIndex, itemId, quantity, enabled);
                        TouchDirtyOnly();
                        break;
                    }
                    case "loadout.replace":
                    {
                        var instanceIds = ReadGuidList(payload, "equippedInstances");
                        var itemIds = ReadItemIdList(payload, "equippedItemIds");
                        if (payload.TryGetProperty("equipment", out var equipment) && equipment.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var entry in equipment.EnumerateArray())
                            {
                                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("itemId", out var item)) continue;
                                var value = item.GetString();
                                if (!string.IsNullOrWhiteSpace(value)) itemIds.Add(new ItemId(value));
                            }
                        }
                        var learned = ReadAbilityIdList(payload, "learnedAbilityIds");
                        if (payload.TryGetProperty("abilitySlots", out var abilitySlots) && abilitySlots.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var entry in abilitySlots.EnumerateArray())
                            {
                                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("abilityId", out var ability)) continue;
                                var value = ability.ValueKind == JsonValueKind.Null ? null : ability.GetString();
                                if (!string.IsNullOrWhiteSpace(value)) learned.Add(new AbilityId(value));
                            }
                        }
                        aggregate.ReplaceLoadout(instanceIds.Select(id => new InstanceId(id)), learned, itemIds);
                        TouchDirtyOnly();
                        break;
                    }
                    case "progression.breakthrough":
                    {
                        aggregate.Breakthrough();
                        TouchDirtyOnly();
                        break;
                    }
                    case "world.travel":
                    {
                        var targetNodeId = payload.TryGetProperty("targetNodeId", out var targetNode) ? targetNode.GetString() : null;
                        if (string.IsNullOrWhiteSpace(targetNodeId))
                            return Fail("INVALID_TARGET", "目标世界节点无效。");
                        targetNodeId = targetNodeId.Trim();

                        var worldGraph = gameData.WorldGraph;
                        if (!worldGraph.ContainsNode(targetNodeId))
                            return Fail("NODE_NOT_FOUND", $"目标世界节点不存在：{targetNodeId}。");

                        // 在入队移动前，先将玩家当前进度推进到 UtcNow，确保原行动已流逝的时间不被移动行动挪用
                        var now = DateTimeOffset.UtcNow;
                        if (now > aggregate.LastSimulatedAt)
                        {
                            var preAdvanceSim = await AdvanceSimulationInternalAsync(now, cancellationToken);
                            if (preAdvanceSim.Changed) simulation = preAdvanceSim;
                        }

                        // 若已经在目标节点且未处于边上，直接成功，并清除任何残留的同类移动行动
                        if (string.Equals(aggregate.World.NodeId, targetNodeId, StringComparison.Ordinal)
                            && string.IsNullOrWhiteSpace(aggregate.World.EdgeId))
                        {
                            for (var i = 0; i < aggregate.Actions.Count; i++)
                            {
                                if (aggregate.Actions[i].Intent.ActionId.Value == "world.travel")
                                {
                                    aggregate.RemoveAction(aggregate.Actions[i].Id);
                                    break;
                                }
                            }
                            TouchDirtyOnly();
                            break;
                        }

                        // 从当前位置（包含处于某条边中间的情况）规划到达 targetNodeId 的最短路径
                        var plan = worldGraph.PlanFromPosition(
                            aggregate.World.EdgeId,
                            aggregate.World.EdgeProgress,
                            aggregate.World.NodeId,
                            targetNodeId);

                        if (plan.Segments.Count == 0 && plan.TotalTravelSeconds <= 0)
                        {
                            for (var i = 0; i < aggregate.Actions.Count; i++)
                            {
                                if (aggregate.Actions[i].Intent.ActionId.Value == "world.travel")
                                {
                                    aggregate.RemoveAction(aggregate.Actions[i].Id);
                                    break;
                                }
                            }
                            if (worldGraph.TryGetNode(targetNodeId, out var targetNodeObj))
                            {
                                aggregate.TravelTo(targetNodeId, targetNodeObj.X, targetNodeObj.Y);
                            }
                            TouchDirtyOnly();
                            break;
                        }

                        var travelState = new TravelActionState(
                            targetNodeId,
                            plan.TotalTravelSeconds,
                            plan.EdgeIds,
                            plan.Segments);

                        var travelEnqueuedAt = aggregate.LastSimulatedAt > now ? aggregate.LastSimulatedAt : now;
                        var travelIntent = new ActionIntent(new ActionId("world.travel"), 1, targetNodeId);
                        aggregate.EnqueuePriority(travelIntent, travelEnqueuedAt, actions, travelState);
                        Touch();
                        break;
                    }
                    case "market.place":
                    {
                        if (marketRuntime is null) return Fail("MARKET_UNAVAILABLE", "市场运行时未装配。");
                        await EnsureWalletFreshAsync(cancellationToken);
                        if (!TryReadGuid(payload, "orderId", out var orderId)) return Fail("INVALID_ORDER", "订单标识无效。");
                        var assetId = payload.TryGetProperty("itemId", out var itemElement) ? itemElement.GetString() : null;
                        var sideText = payload.TryGetProperty("side", out var sideElement) ? sideElement.GetString() : null;
                        var variant = payload.TryGetProperty("variant", out var variantElement) && variantElement.ValueKind != JsonValueKind.Null ? variantElement.GetString() ?? string.Empty : string.Empty;
                        var price = payload.TryGetProperty("price", out var priceElement) && priceElement.TryGetDouble(out var parsedPrice) ? parsedPrice : double.NaN;
                        var quantity = payload.TryGetProperty("quantity", out var quantityElement) && quantityElement.TryGetDouble(out var parsedQuantity) ? parsedQuantity : double.NaN;
                        if (string.IsNullOrWhiteSpace(assetId) || !Enum.TryParse<MarketSide>(sideText, true, out var side)) return Fail("INVALID_ORDER", "订单品种或方向无效。");
                        await marketRuntime.PlaceAsync(new PlaceOrderRequest(new OrderId(orderId), aggregate.PlayerId, new MarketInstrument(new ItemId(assetId), variant), side, price, quantity, DateTimeOffset.UtcNow, new IdempotencyKey($"market:{aggregate.PlayerId.Value:D}:{orderId:D}")), cancellationToken);
                        await RefreshWalletAfterDurableMutationAsync(cancellationToken);
                        break;
                    }
                    case "market.cancel":
                    {
                        if (marketRuntime is null) return Fail("MARKET_UNAVAILABLE", "市场运行时未装配。");
                        await EnsureWalletFreshAsync(cancellationToken);
                        if (!TryReadGuid(payload, "orderId", out var orderId)) return Fail("INVALID_ORDER", "订单标识无效。");
                        var order = await marketRuntime.GetOrderAsync(orderId, aggregate.PlayerId, cancellationToken);
                        if (order is null) return Fail("ORDER_NOT_FOUND", "订单不存在。");
                        await marketRuntime.CancelAsync(new OrderId(orderId), aggregate.PlayerId, order.Instrument, $"market-cancel:{aggregate.PlayerId.Value:D}:{orderId:D}", cancellationToken);
                        await RefreshWalletAfterDurableMutationAsync(cancellationToken);
                        break;
                    }
                    case "market.claim":
                    {
                        if (marketRuntime is null) return Fail("MARKET_UNAVAILABLE", "市场运行时未装配。");
                        if (marketRuntime.PartitionCount != 1) return Fail("MARKET_CLAIM_UNAVAILABLE", "多市场分片尚未提供跨分片结算领取路由。");
                        if (!requestId.HasValue) return Fail("INVALID_REQUEST_ID", "领取结算必须提供请求 ID。");
                        await marketRuntime.ClaimSettlementAsync(aggregate.PlayerId, requestId.Value, cancellationToken);
                        await ReloadWalletAsync(cancellationToken);
                        break;
                    }
                    case "player.rename":
                    {
                        var name = payload.TryGetProperty("displayName", out var nameElement) ? nameElement.GetString() : null;
                        aggregate.Rename(name ?? string.Empty);
                        Touch();
                        break;
                    }
                    case "simulation.advance":
                    {
                        var target = payload.TryGetProperty("targetTime", out var targetElement) && targetElement.TryGetDateTimeOffset(out var parsed)
                            ? parsed : DateTimeOffset.UtcNow;
                        simulation = await AdvanceSimulationInternalAsync(target, cancellationToken);
                        break;
                    }
                    case "player.presence":
                    {
                        var value = payload.TryGetProperty("presence", out var presence) ? presence.GetString() : null;
                        aggregate.SetPresence(value?.ToLowerInvariant() switch
                        {
                            "online" => PresenceState.Online,
                            "suspended" => PresenceState.Suspended,
                            _ => PresenceState.Offline
                        });
                        Touch();
                        break;
                    }
                    default:
                        return Fail("UNKNOWN_COMMAND", "未知领域命令。");
                }

                if (requestId.HasValue)
                {
                    RememberRequest(requestId.Value, command, payloadHash);
                    if (ShouldPersistReceipt(command))
                    {
                        var createdAt = DateTimeOffset.UtcNow;
                        _pendingReceipts.Add(new CommandReceipt(
                            aggregate.PlayerId.Value,
                            requestId.Value,
                            command,
                            payloadHash,
                            Math.Max(_logicalVersion, aggregate.StateVersion),
                            createdAt,
                            createdAt.AddMinutes(30)));
                    }
                }
                // 锁内只完成模板映射与缓冲写入；网络推送由外层在锁外触发。
                IReadOnlyList<LogEventDto>? logEvents = null;
                if (simulation is { Events.Count: > 0 })
                    logEvents = SimulationLogMapper.ToLogEvents(simulation.Events, gameData, logBuffer);
                return new PlayerCommandResult(true, "OK", "命令已提交。", Snapshot(), simulation, logEvents);
            }
            catch (DomainException ex)
            {
                return Fail(ex.Code.ToString().ToUpperInvariant(), ex.Message);
            }
            catch (EconomyInsufficientBalanceException)
            {
                return Fail("INSUFFICIENT_BALANCE", "资产余额不足。");
            }
            catch (MarketLeaseLostException)
            {
                return Fail("MARKET_LEASE_LOST", "市场分片写者租约已失效。");
            }
            catch (PlayerOwnershipLostException)
            {
                // 所有权已丢失后无法在本实例恢复：返回明确错误，缓存清理由运行时在
                // 命令返回后、锁外执行（见 ExecuteCoreAsync），不在锁内触发释放或标记销毁。
                return Fail("PLAYER_OWNERSHIP_LOST", "玩家当前由其他服务实例持有，请稍后重试。");
            }
            catch (JsonException ex)
            {
                return Fail("INVALID_PAYLOAD", ex.Message);
            }
            finally { _gate.Release(); }
        }

        private async ValueTask<SimulationResult> AdvanceSimulationInternalAsync(DateTimeOffset target, CancellationToken cancellationToken)
        {
            await EnsureWalletFreshAsync(cancellationToken);
            var before = aggregate.ExportState();
            var walletBefore = wallet.Balances.ToArray();
            var dirtyBefore = aggregate.DirtySections.ToArray();
            var dirtyAtBefore = _dirtyAt.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var logicalVersionBefore = _logicalVersion;
            var economyCommitted = false;
            try
            {
                // 战斗遭遇与持续消耗品都需要完整配表；钱包仅作开封库存判断，实际扣减仍随 AssetDeltas 原子提交。
                var result = SimulationEngine.AdvanceTo(aggregate, target, gameData, wallet);
                if (result.AssetDeltas.Count > 0)
                {
                    var transaction = new EconomyTransactionRequest(
                        Guid.NewGuid(),
                        $"simulation:{aggregate.PlayerId.Value:D}:{result.StateVersion}:{result.To.ToUnixTimeMilliseconds()}",
                        "simulation.reward",
                        aggregate.PlayerId.Value,
                        result.AssetDeltas.Select(delta => new Server.Clean.AssetDelta(
                            delta.PlayerId.Value,
                            delta.Asset.ItemId.Value,
                            delta.Asset.Variant,
                            delta.QuantityDelta)).ToArray(),
                        result.To,
                        JsonSerializer.SerializeToUtf8Bytes(new { result.StateVersion, result.To }));
                    var committed = await economy.CommitAsync(transaction, cancellationToken);
                    if (!committed.Applied)
                        throw new InvalidOperationException("模拟资产事务未提交。");
                    economyCommitted = true;
                    try
                    {
                        await ReloadWalletAsync(cancellationToken);
                    }
                    catch (Exception reloadException)
                    {
                        // 资产事务已 durable commit，不能回滚 LastSimulatedAt，否则下一次目标时间
                        // 会把同一段奖励再次结算。保留聚合进度，标记钱包待刷新；后续资产命令
                        // 会先强制从 PostgreSQL 读取，期间尽量用本地 delta 保持界面可见值。
                        _walletReloadRequired = true;
                        foreach (var delta in result.AssetDeltas)
                        {
                            try { wallet.Apply(delta); }
                            catch (Exception applyException)
                            {
                                if (_walletErrorLog.TryAcquire())
                                    logger.LogError(applyException, "模拟资产已提交但本地钱包无法应用 delta，等待下次刷新 player={PlayerId}", aggregate.PlayerId.Value);
                                break;
                            }
                        }
                        if (reloadException is not OperationCanceledException)
                        {
                            if (_walletErrorLog.TryAcquire())
                                logger.LogError(reloadException, "模拟资产已提交但钱包刷新失败 player={PlayerId}", aggregate.PlayerId.Value);
                        }
                        if (reloadException is OperationCanceledException) throw;
                    }
                }
                _logicalVersion = Math.Max(_logicalVersion, aggregate.StateVersion);
                TouchDirtyOnly();
                return result;
            }
            catch
            {
                if (!economyCommitted)
                {
                    RestoreSimulationState(before, walletBefore, dirtyBefore, dirtyAtBefore, logicalVersionBefore);
                }
                throw;
            }
        }

        public async ValueTask<PlayerCommandResult?> AdvanceSimulationAsync(DateTimeOffset target, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.SerializeToElement(new { targetTime = target }, ServerJson.Options);
            var result = await ExecuteAsync("simulation.advance", payload, 0, null, cancellationToken);
            return result.Simulation is null ? null : result;
        }

        public async ValueTask FlushAsync(bool force, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (ownership is not null && !await ownership.RenewAsync(aggregate.PlayerId.Value, ownerInstanceId, ownershipLease, cancellationToken))
                {
                    // 所有权已转移给其他实例：本实例内存脏状态不再权威，跳过本次刷盘；
                    // actor 由运行时在下一次命令或推进时失效移除，durable 队列中已入队的写不受影响。
                    logger.LogError("所有权已丢失，跳过玩家刷盘 player={PlayerId}", aggregate.PlayerId.Value);
                    return;
                }
                var dirty = aggregate.DirtySections;
                if (dirty.Count == 0 && _pendingReceipts.Count == 0) return;
                var now = DateTimeOffset.UtcNow;
                var receiptPending = _pendingReceipts.Count > 0;
                var eligibleAll = receiptPending || force;
                PlayerSectionKind[] eligible;
                if (eligibleAll)
                {
                    eligible = dirty.Count == 0 ? [] : dirty.ToArray();
                }
                else
                {
                    var list = new List<PlayerSectionKind>(dirty.Count);
                    foreach (var section in dirty)
                    {
                        if (!_dirtyAt.TryGetValue(SectionCodec.ToStorageKind(section), out var at) || now - at >= TimeSpan.FromSeconds(15))
                        {
                            list.Add(section);
                        }
                    }
                    eligible = list.Count == 0 ? [] : list.ToArray();
                }

                // 需要跨重连去重的低频写命令必须把本次所有脏 section 与 receipt
                // 一起进入 durable queue，避免回执先落盘而其他状态仍停留在内存。
                if (eligible.Length == 0 && dirty.Count > 0 && !receiptPending) return;

                // EncodeAggregate consumes all dirty flags; restore flags for sections not yet eligible.
                var encoded = SectionCodec.EncodeAggregate(aggregate, now, Math.Max(1, _logicalVersion));
                IReadOnlyList<SectionWrite> writes;
                if (eligibleAll || eligible.Length == encoded.Count)
                {
                    writes = encoded;
                }
                else
                {
                    var filteredWrites = new List<SectionWrite>(encoded.Count);
                    for (var i = 0; i < encoded.Count; i++)
                    {
                        var write = encoded[i];
                        var kind = SectionCodec.FromStorageKind(write.SectionKind);
                        var isEligible = false;
                        for (var j = 0; j < eligible.Length; j++)
                        {
                            if (eligible[j] == kind)
                            {
                                isEligible = true;
                                break;
                            }
                        }

                        if (isEligible)
                        {
                            filteredWrites.Add(write);
                        }
                        else
                        {
                            aggregate.MarkDirty(kind);
                        }
                    }
                    writes = filteredWrites;
                }

                if (writes.Count == 0 && _pendingReceipts.Count == 0) return;
                try
                {
                    await repository.EnqueueSectionsAsync(writes, _pendingReceipts.Count == 0 ? [] : _pendingReceipts.ToArray(), cancellationToken);
                }
                catch
                {
                    // 队列写入失败时恢复所有刚刚消费的 dirty 标记，确保下一周期仍会重试。
                    foreach (var write in encoded)
                    {
                        aggregate.MarkDirty(SectionCodec.FromStorageKind(write.SectionKind));
                        _dirtyAt[write.SectionKind] = now.Subtract(TimeSpan.FromSeconds(15));
                    }

                    throw;
                }
                foreach (var write in writes)
                {
                    _sectionVersions[write.SectionKind] = write.StateVersion;
                    _dirtyAt.Remove(write.SectionKind);
                }
                _pendingReceipts.Clear();
            }
            finally { _gate.Release(); }
        }

        private PlayerCommandResult Fail(string code, string message) => new(false, code, message, Snapshot());

        private void RememberRequest(Guid requestId, string command, string payloadHash)
        {
            _recentRequests[requestId] = (command, payloadHash);
            _recentRequestOrder.Enqueue(requestId);
            while (_recentRequestOrder.Count > 1024)
            {
                var expired = _recentRequestOrder.Dequeue();
                _recentRequests.Remove(expired);
            }
        }

        private static string ComputePayloadHash(JsonElement payload) =>
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, ServerJson.Options))).ToLowerInvariant();

        private static bool ShouldPersistReceipt(string command) => command is
            "action.enqueue" or "action.remove" or
            "queue.configure" or "queue.move" or "queue.auto_use.configure" or
            "loadout.replace" or "progression.breakthrough" or "world.travel" or "player.rename" or "market.claim";

        private async ValueTask ReloadWalletAsync(CancellationToken cancellationToken)
        {
            var refreshed = await assetRepository.LoadBalancesAsync(aggregate.PlayerId.Value, cancellationToken);
            wallet.Restore(refreshed.Select(balance => new AssetBalance(
                new PlayerId(balance.PlayerId),
                new AssetKey(new ItemId(balance.AssetId), balance.Variant),
                balance.Quantity,
                    balance.Version)));
            _walletReloadRequired = false;
        }

        private async ValueTask EnsureWalletFreshAsync(CancellationToken cancellationToken)
        {
            if (_walletReloadRequired) await ReloadWalletAsync(cancellationToken);
        }

        private async ValueTask RefreshWalletAfterDurableMutationAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ReloadWalletAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                _walletReloadRequired = true;
                if (exception is OperationCanceledException) throw;
                // 市场事务已经提交，不能把成功状态改报成失败并让客户端以另一订单重试；
                // 标记钱包 stale，下一次资产相关命令会先强制刷新。
                if (_walletErrorLog.TryAcquire())
                    logger.LogError(exception, "市场资产事务已提交但本地钱包刷新失败 player={PlayerId}", aggregate.PlayerId.Value);
            }
        }

        private void RestoreSimulationState(
            PlayerAggregateState state,
            IReadOnlyCollection<AssetBalance> balances,
            IReadOnlyCollection<PlayerSectionKind> dirtySections,
            IReadOnlyDictionary<string, DateTimeOffset> dirtyAt,
            long logicalVersion)
        {
            aggregate.RestoreFrom(state);
            foreach (var section in dirtySections) aggregate.MarkDirty(section);
            _dirtyAt.Clear();
            foreach (var pair in dirtyAt) _dirtyAt[pair.Key] = pair.Value;
            _logicalVersion = logicalVersion;
            wallet.Restore(balances);
        }

        private static bool TryReadGuid(JsonElement payload, string property, out Guid value)
        {
            value = Guid.Empty;
            return payload.TryGetProperty(property, out var element)
                && element.ValueKind == JsonValueKind.String
                && Guid.TryParse(element.GetString(), out value)
                && value != Guid.Empty;
        }

        private static List<Guid> ReadGuidList(JsonElement payload, string property)
        {
            var result = new List<Guid>();
            if (!payload.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array) return result;
            foreach (var value in element.EnumerateArray())
                if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty) result.Add(id);
            return result;
        }

        private static List<ItemId> ReadItemIdList(JsonElement payload, string property)
        {
            var result = new List<ItemId>();
            if (!payload.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array) return result;
            foreach (var value in element.EnumerateArray())
                if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) result.Add(new ItemId(value.GetString()!));
            return result;
        }

        private static List<AbilityId> ReadAbilityIdList(JsonElement payload, string property)
        {
            var result = new List<AbilityId>();
            if (!payload.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array) return result;
            foreach (var value in element.EnumerateArray())
                if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) result.Add(new AbilityId(value.GetString()!));
            return result;
        }

        private void Touch()
        {
            _logicalVersion = Math.Max(_logicalVersion, aggregate.StateVersion);
            TouchDirtyOnly();
        }

        private void TouchDirtyOnly()
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var section in aggregate.DirtySections) _dirtyAt.TryAdd(SectionCodec.ToStorageKind(section), now);
        }

        private IReadOnlyList<StoredAction> BuildStoredActions()
        {
            return aggregate.Actions
                .Select((action, index) => new StoredAction(
                    aggregate.PlayerId.Value,
                    index,
                    action.Intent.ActionId.Value,
                    JsonSerializer.SerializeToUtf8Bytes(action.Intent, ServerJson.Options),
                    Math.Max(1, aggregate.StateVersion),
                    action.Status.ToString().ToLowerInvariant(),
                    null,
                    $"action:{action.Id:D}"))
                .ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            // 所有权丢失清理与会话移除可能并发触发释放，幂等退出。
            if (Interlocked.Exchange(ref _disposeStarted, 1) == 1) return;
            _disposed = true;
            // 先等待在途命令完成再销毁信号量，避免并发命令在已释放的 SemaphoreSlim 上抛 ObjectDisposedException。
            await _gate.WaitAsync(CancellationToken.None);
            _gate.Dispose();
            if (ownership is not null) await ownership.ReleaseAsync(aggregate.PlayerId.Value, ownerInstanceId, CancellationToken.None);
        }
    }
}

/// <summary>每玩家战报日志环形缓冲与单调递增 sequence；仅进程内存，不持久化。连续完全相同模板日志自动合并且仅占用 1 条容量配额。</summary>
public sealed class PlayerLogBuffer(int capacity)
{
    private readonly object _lock = new();
    private readonly List<LogEventDto> _events = [];
    private long _sequence;

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    public void Append(LogEventDto logEvent)
    {
        lock (_lock)
        {
            if (_events.Count > 0 && _events[^1].Template == logEvent.Template)
            {
                var last = _events[^1];
                _events[^1] = last with
                {
                    Sequence = logEvent.Sequence,
                    TimestampUnixMilliseconds = Math.Max(last.TimestampUnixMilliseconds, logEvent.TimestampUnixMilliseconds),
                    Count = last.Count + logEvent.Count
                };
                return;
            }

            _events.Add(logEvent);
            while (_events.Count > capacity)
            {
                _events.RemoveAt(0);
            }
        }
    }

    public IReadOnlyList<LogEventDto> SnapshotRecent()
    {
        lock (_lock) return _events.ToArray();
    }
}

public sealed class PlayerFlushHostedService(PlayerRuntime runtime, ServerOptions options, ILogger<PlayerFlushHostedService> logger) : BackgroundService
{
    private readonly LogThrottle _loopErrorLog = new(TimeSpan.FromMinutes(1));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await runtime.FlushAllAsync(false, stoppingToken); await Task.Delay(options.SectionFlushInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_loopErrorLog.TryAcquire()) logger.LogError(ex, "在线玩家 section 刷新失败。");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}

/// <summary>在线玩家模拟时钟。推进只发生在内存单写者内，section 仍按低频批次落盘。</summary>
public sealed class PlayerSimulationHostedService(PlayerRuntime runtime, ILogger<PlayerSimulationHostedService> logger) : BackgroundService
{
    private readonly LogThrottle _loopErrorLog = new(TimeSpan.FromMinutes(1));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await runtime.AdvanceAllAsync(DateTimeOffset.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (_loopErrorLog.TryAcquire()) logger.LogError(ex, "在线玩家模拟推进失败。");
            }
        }
    }
}
