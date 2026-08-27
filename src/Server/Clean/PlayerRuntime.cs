using Domain.Player;
using Domain.Simulation;
using Domain.GameData;
using Domain.Common;
using Domain.Economy;
using Protocol;

namespace Server.Clean;

public sealed record PlayerRuntimeSnapshot(
    Guid PlayerId,
    string AccountId,
    string DisplayName,
    long StateVersion,
    DateTimeOffset LastSimulatedAt,
    PlayerAggregateState State,
    IReadOnlyList<StoredAction> Actions,
    IReadOnlyList<AssetBalanceRecord> Assets);

public sealed record PlayerCommandResult(
    bool Success,
    string Code,
    string Message,
    PlayerRuntimeSnapshot Snapshot,
    SimulationResult? Simulation = null);

/// <summary>
/// 在线玩家单写者运行时：内存持有完整领域聚合，跨边界只交换消息和 section 载荷。
/// </summary>
public sealed class PlayerRuntime(
    IPlayerStateRepository repository,
    IAssetBalanceRepository assetRepository,
    IEconomyTransactionStore economy,
    GameDataCatalog gameData,
    ILogger<PlayerRuntime> logger,
    MarketRuntime? marketRuntime = null,
    ICommandReceiptStore? commandReceipts = null)
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<PlayerActor>>> _actors = new();
    private readonly ActionCatalog _actions = gameData.Actions;

    public int LoadedCount => _actors.Count;

    public async ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        if (playerId == Guid.Empty) throw new ArgumentException("PlayerId 不能为空。", nameof(playerId));
        var lazy = _actors.GetOrAdd(playerId, id => new Lazy<Task<PlayerActor>>(() => CreateActorAsync(id, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
        try { return (await lazy.Value).Snapshot(); }
        catch { _actors.TryRemove(playerId, out _); throw; }
    }

    public async ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, CancellationToken cancellationToken = default)
    {
        var lazy = _actors.GetOrAdd(playerId, id => new Lazy<Task<PlayerActor>>(() => CreateActorAsync(id, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
        return await (await lazy.Value).ExecuteAsync(command, payload, expectedStateVersion: 0, TryReadRequestId(payload), cancellationToken);
    }

    /// <summary>在玩家单写者锁内校验期望版本，避免 HTTP/WS 先读后写的竞态。</summary>
    public async ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, long expectedStateVersion, CancellationToken cancellationToken = default)
    {
        var lazy = _actors.GetOrAdd(playerId, id => new Lazy<Task<PlayerActor>>(() => CreateActorAsync(id, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
        return await (await lazy.Value).ExecuteAsync(command, payload, expectedStateVersion, TryReadRequestId(payload), cancellationToken);
    }

    public async ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, long expectedStateVersion, Guid requestId, CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("RequestId 不能为空。", nameof(requestId));
        var lazy = _actors.GetOrAdd(playerId, id => new Lazy<Task<PlayerActor>>(() => CreateActorAsync(id, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
        return await (await lazy.Value).ExecuteAsync(command, payload, expectedStateVersion, requestId, cancellationToken);
    }

    public async ValueTask FlushAllAsync(bool force, CancellationToken cancellationToken = default)
    {
        foreach (var entry in _actors)
        {
            if (!entry.Value.IsValueCreated) continue;
            await (await entry.Value.Value).FlushAsync(force, cancellationToken);
        }
    }

    public async ValueTask FlushAsync(Guid playerId, bool force, CancellationToken cancellationToken = default)
    {
        if (!_actors.TryGetValue(playerId, out var lazy) || !lazy.IsValueCreated) return;
        await (await lazy.Value).FlushAsync(force, cancellationToken);
    }

    public async ValueTask RemoveAsync(Guid playerId, bool flush, CancellationToken cancellationToken = default)
    {
        if (!_actors.TryGetValue(playerId, out var lazy) || !lazy.IsValueCreated) return;
        var actor = await lazy.Value;
        if (flush) await actor.FlushAsync(true, cancellationToken);
        _actors.TryRemove(new KeyValuePair<Guid, Lazy<Task<PlayerActor>>>(playerId, lazy));
        await actor.DisposeAsync();
    }

    private async Task<PlayerActor> CreateActorAsync(Guid playerId, CancellationToken cancellationToken)
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
            var state = SectionCodec.DecodeAggregate(bundle, gameData.Fingerprint);
            aggregate = PlayerAggregate.Restore(state);
            aggregate.SetPresence(PresenceState.Online);
        }
        var balances = await assetRepository.LoadBalancesAsync(playerId, cancellationToken);
        var wallet = new AssetWallet(new PlayerId(playerId));
        wallet.Restore(balances.Select(balance => new AssetBalance(
            new PlayerId(balance.PlayerId),
            new AssetKey(new ItemId(balance.AssetId), balance.Variant),
            balance.Quantity,
            balance.Version)));
        return new PlayerActor(aggregate, bundle, wallet, assetRepository, repository, economy, _actions, logger, marketRuntime, commandReceipts);
    }

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
        private readonly ILogger logger;
        private readonly MarketRuntime? marketRuntime;
        private readonly ICommandReceiptStore? commandReceipts;
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

        public PlayerActor(
            PlayerAggregate aggregate,
            PlayerStateBundle bundle,
            AssetWallet wallet,
            IAssetBalanceRepository assetRepository,
            IPlayerStateRepository repository,
            IEconomyTransactionStore economy,
            ActionCatalog actions,
            ILogger logger,
            MarketRuntime? marketRuntime,
            ICommandReceiptStore? commandReceipts)
        {
            this.aggregate = aggregate;
            this.bundle = bundle;
            this.wallet = wallet;
            this.assetRepository = assetRepository;
            this.repository = repository;
            this.economy = economy;
            this.actions = actions;
            this.logger = logger;
            this.marketRuntime = marketRuntime;
            this.commandReceipts = commandReceipts;
            _sectionVersions = bundle.Sections.ToDictionary(x => x.Key, x => x.Value.StateVersion, StringComparer.Ordinal);
            _logicalVersion = Math.Max(0, aggregate.StateVersion);
            foreach (var section in aggregate.DirtySections) _dirtyAt[SectionCodec.ToStorageKind(section)] = DateTimeOffset.UtcNow;
        }

        public PlayerRuntimeSnapshot Snapshot() => new(
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
                        var nodeId = payload.TryGetProperty("targetNodeId", out var targetNode) ? targetNode.GetString() : null;
                        var x = payload.TryGetProperty("x", out var xElement) && xElement.TryGetDouble(out var parsedX) ? parsedX : aggregate.World.X;
                        var y = payload.TryGetProperty("y", out var yElement) && yElement.TryGetDouble(out var parsedY) ? parsedY : aggregate.World.Y;
                        aggregate.TravelTo(nodeId ?? string.Empty, x, y);
                        TouchDirtyOnly();
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
                        var before = aggregate.ExportState();
                        var walletBefore = wallet.Balances.ToArray();
                        var dirtyBefore = aggregate.DirtySections.ToArray();
                        var dirtyAtBefore = _dirtyAt.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                        var logicalVersionBefore = _logicalVersion;
                        var target = payload.TryGetProperty("targetTime", out var targetElement) && targetElement.TryGetDateTimeOffset(out var parsed)
                            ? parsed : DateTimeOffset.UtcNow;
                        var economyCommitted = false;
                        try
                        {
                            simulation = SimulationEngine.AdvanceTo(aggregate, target, actions);
                            if (simulation.AssetDeltas.Count > 0)
                            {
                                var transaction = new EconomyTransactionRequest(
                                    Guid.NewGuid(),
                                    $"simulation:{aggregate.PlayerId.Value:D}:{simulation.StateVersion}:{simulation.To.ToUnixTimeMilliseconds()}",
                                    "simulation.reward",
                                    aggregate.PlayerId.Value,
                                    simulation.AssetDeltas.Select(delta => new Server.Clean.AssetDelta(
                                        delta.PlayerId.Value,
                                        delta.Asset.ItemId.Value,
                                        delta.Asset.Variant,
                                        delta.QuantityDelta)).ToArray(),
                                    simulation.To,
                                    JsonSerializer.SerializeToUtf8Bytes(new { simulation.StateVersion, simulation.To }));
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
                                    foreach (var delta in simulation.AssetDeltas)
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
                        }
                        catch
                        {
                            if (!economyCommitted)
                            {
                                RestoreSimulationState(before, walletBefore, dirtyBefore, dirtyAtBefore, logicalVersionBefore);
                            }
                            throw;
                        }
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
                return new PlayerCommandResult(true, "OK", "命令已提交。", Snapshot(), simulation);
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
            catch (JsonException ex)
            {
                return Fail("INVALID_PAYLOAD", ex.Message);
            }
            finally { _gate.Release(); }
        }

        public async ValueTask FlushAsync(bool force, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var dirty = aggregate.DirtySections;
                if (dirty.Count == 0 && _pendingReceipts.Count == 0) return;
                var now = DateTimeOffset.UtcNow;
                var receiptPending = _pendingReceipts.Count > 0;
                var eligible = receiptPending
                    ? dirty.ToArray()
                    : dirty.Where(section =>
                        force
                        || !_dirtyAt.TryGetValue(SectionCodec.ToStorageKind(section), out var at)
                        || now - at >= TimeSpan.FromSeconds(15)).ToArray();
                // 需要跨重连去重的低频写命令必须把本次所有脏 section 与 receipt
                // 一起进入 durable queue，避免回执先落盘而其他状态仍停留在内存。
                if (eligible.Length == 0 && dirty.Count > 0 && !receiptPending) return;

                // EncodeAggregate consumes all dirty flags; restore flags for sections not yet eligible.
                var encoded = SectionCodec.EncodeAggregate(aggregate, now, Math.Max(1, _logicalVersion));
                var writes = encoded.Where(write => eligible.Contains(SectionCodec.FromStorageKind(write.SectionKind))).ToArray();
                foreach (var write in encoded.Where(write => !eligible.Contains(SectionCodec.FromStorageKind(write.SectionKind)))) aggregate.MarkDirty(SectionCodec.FromStorageKind(write.SectionKind));
                if (writes.Length == 0 && _pendingReceipts.Count == 0) return;
                try
                {
                    await repository.EnqueueSectionsAsync(writes, _pendingReceipts.ToArray(), cancellationToken);
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
            "loadout.replace" or "progression.breakthrough" or "world.travel" or "player.rename";

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

        public ValueTask DisposeAsync() { _disposed = true; _gate.Dispose(); return ValueTask.CompletedTask; }
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
