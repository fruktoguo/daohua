using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using DaoHua.Ecs;
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

namespace Server;

/// <summary>
/// Clean 宿主到 ECS 世界的单写者适配边界。鉴权、协议、SectionCodec、资产事务和
/// durable outbox 仍由宿主提供；Entity 只保存长期领域状态，所有请求都经玩家锁串行进入 Fiber。
/// </summary>
public sealed class EcsPlayerRuntimeAdapter(
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
    IAccountRepository? accounts = null) : IPlayerRuntime
{
    private readonly ConcurrentDictionary<Guid, PlayerRuntimeSnapshot> _loaded = new();
    private readonly ConcurrentDictionary<Guid, Dictionary<AssetKey, AssetBalanceRecord>> _wallets = new();
    private readonly ConcurrentDictionary<Guid, Dictionary<PlayerSectionKind, DateTimeOffset>> _dirtyAt = new();
    private readonly ConcurrentDictionary<Guid, List<CommandReceipt>> _pendingReceipts = new();
    private readonly ConcurrentDictionary<Guid, byte> _walletReloadRequired = new();
    private readonly ConcurrentDictionary<Guid, PlayerLogBuffer> _logs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _playerGates = new();
    private readonly ConcurrentDictionary<Guid, RecentRequests> _recentRequests = new();
    private readonly ConcurrentDictionary<Guid, long> _attributeRevisions = new();
    // 不设全局加载门：玩家级 gate 与 shard owner gate 已提供单写者隔离。
    private readonly IPlayerSimulationClock _simulationClock = simulationClock ?? PlayerSimulationClock.FromEnvironment();
    private readonly ILogger<EcsPlayerRuntimeAdapter> _logger = logger;
    private const int LogBufferCapacity = 200;
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

    public int LoadedCount => _loaded.Count;
    public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated;
    public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted;
    public DateTimeOffset? NextAdvanceBoundaryUtc => ecs.NextAdvanceBoundaryUtc;

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

    private async ValueTask<PlayerRuntimeSnapshot> LoadOwnedAsync(Guid playerId,
        List<RuntimeNotification> notifications, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (playerId == Guid.Empty) throw new ArgumentException("PlayerId 不能为空。", nameof(playerId));
        if (_loaded.TryGetValue(playerId, out var cached)) return cached;

        if (_loaded.TryGetValue(playerId, out cached)) return cached;
        using var loadMeasurement = RuntimeInstrumentation.Measure("lifecycle.player_load");
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
        try
        {
            _wallets[playerId] = balanceRows.ToDictionary(row => new AssetKey(new ItemId(row.AssetId), row.Variant));
            _dirtyAt[playerId] = [];
            _pendingReceipts[playerId] = [];
            var before = ToSnapshot(ecs.ExportSnapshot(playerId));
            IReadOnlyList<LogEventDto>? logs = null;
            var target = _simulationClock.UtcNow;
            await RefreshNodeBuildingsAsync(playerId, before.State.World, target, cancellationToken).ConfigureAwait(false);
            before = ToSnapshot(ecs.ExportSnapshot(playerId));
            TrackDirty(playerId);
            if (target > state.LastSimulatedAt)
            {
                await ecs.RunPlayerOnceAsync(playerId, target, cancellationToken).ConfigureAwait(false);
                var advanced = ToSnapshot(ecs.ExportSnapshot(playerId));
                logs = (await CommitSimulationAsync(playerId, before, advanced, null, cancellationToken).ConfigureAwait(false)).Logs;
            }
            // 离线收益先完成事务，随后再转在线；未提交的加载结果不能进入日志回放。
            ecs.SetPresence(playerId, PresenceState.Online);
            TrackDirty(playerId);
            var snapshot = ToSnapshot(ecs.ExportSnapshot(playerId));
            _loaded[playerId] = snapshot;
            _attributeRevisions[playerId] = ecs.AttributeRevision(playerId);
            RuntimeInstrumentation.Count("lifecycle.player_loaded");
            notifications.Add(new RuntimeNotification(playerId, logs));
            return snapshot;
        }
        catch
        {
            ecs.RemovePlayer(playerId);
            _wallets.TryRemove(playerId, out _);
            _logs.TryRemove(playerId, out _);
            _dirtyAt.TryRemove(playerId, out _);
            _pendingReceipts.TryRemove(playerId, out _);
            throw;
        }
    }

    public async ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload,
        long expectedStateVersion, Guid requestId, CancellationToken cancellationToken = default)
    {
        var notifications = new List<RuntimeNotification>();
        try
        {
            return await ExecuteOwnedAsync(playerId, command, payload, expectedStateVersion, requestId,
                notifications, cancellationToken).ConfigureAwait(false);
        }
        finally { await EmitNotificationsAsync(notifications).ConfigureAwait(false); }
    }

    private async ValueTask<PlayerCommandResult> ExecuteOwnedAsync(Guid playerId, string command, JsonElement payload,
        long expectedStateVersion, Guid requestId, List<RuntimeNotification> notifications,
        CancellationToken cancellationToken)
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
            await EnsureWalletFreshAsync(playerId, cancellationToken).ConfigureAwait(false);
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
                    ? new PlayerCommandResult(true, "IDEMPOTENT_REPLAY", "命令已处理。", before)
                    : new PlayerCommandResult(false, "REQUEST_ID_CONFLICT", "请求 ID 已用于其他命令。", before);
            if (commandReceipts is not null && ShouldPersistReceipt(command))
            {
                var persisted = await commandReceipts.FindAsync(playerId, requestId, cancellationToken).ConfigureAwait(false);
                if (persisted is not null)
                {
                    if (persisted.Command != command || persisted.PayloadHash != payloadHash)
                        return new PlayerCommandResult(false, "REQUEST_ID_CONFLICT", "请求 ID 已用于其他命令。", before);
                    recent.Remember(requestId, command, payloadHash);
                    return new PlayerCommandResult(true, "IDEMPOTENT_REPLAY", "命令已处理。", before);
                }
            }
            if (expectedStateVersion > 0 && before.StateVersion != expectedStateVersion)
                return new PlayerCommandResult(false, "STATE_VERSION_CONFLICT", "玩家状态版本已变化。", before);

            try
            {
                // 操作前分段脏流水号快照：命令结束后差集得出「本次操作触及分段」，
                // 供网络增量窄化编码范围（不替代持久化脏集合语义；戳号不复用，差集不会漏报）。
                var dirtyMarksBefore = ecs.GetDirtyMarks(playerId);
                IReadOnlySet<PlayerSectionKind>? touched = null;
                (Guid DuelId, Guid PeerId)? forfeit = null;
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
                            request.TargetNodeId, request.Parameters, request.PvpEnabled);
                        if (gameData.Actions.TryGet(intent.ActionId, out var actionDefinition)
                            && actionDefinition.AllowedNodeIds is { Count: > 0 }
                            && (before.State.World.EdgeId is not null || !actionDefinition.AllowedNodeIds.Contains(before.State.World.NodeId)))
                            return new PlayerCommandResult(false, "WRONG_LOCATION", "当前位置不允许执行该行动。", before);
                        var now = _simulationClock.UtcNow;
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
                        // OOP 同序：移除队首先把已流逝模拟结算掉，再进行弃战判定与移除。
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
                        if (!payload.TryGetProperty("loopEnabled", out var loopEnabled)
                            || loopEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return new PlayerCommandResult(false, "INVALID_PAYLOAD", "循环设置无效。", before);
                        var enabled = loopEnabled.GetBoolean();
                        touched = ecs.MutatePlayer(playerId, aggregate => aggregate.SetLoopEnabled(enabled));
                        break;
                    }
                    case "queue.move":
                    {
                        if (!TryReadGuid(payload, "actionInstanceId", out var actionInstanceId)
                            || !payload.TryGetProperty("direction", out var directionElement)
                            || !directionElement.TryGetInt32(out var direction))
                            return new PlayerCommandResult(false, "INVALID_PAYLOAD", "行动移动参数无效。", before);
                        // OOP 同序：先推进到当前，再按是否挪动队首做弃战判定。
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
                    case "queue.auto_use.configure":
                    {
                        var actionType = payload.TryGetProperty("actionType", out var actionTypeElement) ? actionTypeElement.GetString() : null;
                        var slotIndex = payload.TryGetProperty("slotIndex", out var slotIndexElement) && slotIndexElement.TryGetInt32(out var parsedSlot) ? parsedSlot : -1;
                        var itemId = payload.TryGetProperty("itemId", out var itemElement) && itemElement.ValueKind != JsonValueKind.Null ? itemElement.GetString() : null;
                        var quantity = payload.TryGetProperty("quantity", out var quantityElement) && quantityElement.TryGetDouble(out var parsedQuantity) ? parsedQuantity : 1d;
                        var enabled = payload.TryGetProperty("enabled", out var enabledElement) && enabledElement.ValueKind == JsonValueKind.True;
                        var group = payload.TryGetProperty("group", out var groupElement) ? groupElement.GetString() : null;
                        if (!Enum.TryParse<ActionKind>(actionType, true, out var slotKind))
                            return new PlayerCommandResult(false, "INVALID_ACTION_TYPE", "自动使用行动类型无效。", before);
                        ItemDefinition? slotItem = null;
                        if (itemId is not null && (!gameData.Items.TryGetValue(new ItemId(itemId), out slotItem)
                            || slotItem.Consumable is null || !slotItem.Consumable.UsableInActionKinds.Contains(slotKind)))
                            return new PlayerCommandResult(false, "INVALID_CONSUMABLE", "物品不适用于该自动使用槽。", before);
                        if (group == AutoUseSlotState.SlotGroupPotion
                            && (slotKind != ActionKind.Combat
                                || itemId is not null && (slotItem!.Category != ConsumableDefinition.CategoryPill
                                    || slotItem.Consumable!.IsContinuous)))
                            return new PlayerCommandResult(false, "INVALID_CONSUMABLE", "药品栏只能装配药品类即时消耗品。", before);
                        var autoActionType = actionType ?? string.Empty;
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
                        var prefix = isTome ? "item.tome." : "item.fragment.";
                        var abilityId = new AbilityId("ability." + itemId[prefix.Length..]);
                        if (!gameData.Abilities.ContainsKey(abilityId.Value))
                            return new PlayerCommandResult(false, "INVALID_ABILITY", "秘籍对应功法不存在。", before);
                        var alreadyLearned = before.State.LearnedAbilities.Contains(abilityId);
                        if (!isTome && !alreadyLearned)
                            return new PlayerCommandResult(false, "ABILITY_NOT_LEARNED", "必须先习得对应功法才能研读残卷。", before);
                        await EnsureWalletFreshAsync(playerId, cancellationToken).ConfigureAwait(false);
                        var dirtyBefore = ecs.GetDirtySections(playerId);
                        touched = ecs.MutatePlayer(playerId, aggregate =>
                        {
                            if (alreadyLearned) aggregate.GrantAbilityExperience(abilityId, isTome ? 10_000 : 500);
                            else aggregate.LearnAbility(abilityId);
                        });
                        var mutated = ToSnapshot(ecs.ExportSnapshot(playerId));
                        var learnNow = DateTimeOffset.UtcNow;
                        var dirtyNow = ecs.GetDirtySections(playerId);
                        var writes = SectionCodec.EncodeAggregateFull(mutated.State, learnNow)
                            .Where(write => dirtyNow.Contains(SectionCodec.FromStorageKind(write.SectionKind))).ToArray();
                        var learnReceipt = CreateReceipt(playerId, requestId, command, payloadHash, mutated.StateVersion);
                        var learnResult = await economy.CommitAsync(new EconomyTransactionRequest(
                            requestId, $"ability-learn:{playerId:D}:{requestId:D}", command, playerId,
                            [new Server.Clean.AssetDelta(playerId, itemId, string.Empty, -1)], learnNow,
                            JsonSerializer.SerializeToUtf8Bytes(new { abilityId = abilityId.Value }), writes, [learnReceipt]),
                            cancellationToken).ConfigureAwait(false);
                        if (!learnResult.Applied)
                        {
                            ecs.RestoreDirtySections(playerId, dirtyBefore);
                            RestoreUncommittedPlayer(playerId, before);
                            return new PlayerCommandResult(false, "TRANSACTION_REJECTED", "秘籍学习事务未提交。", before);
                        }
                        TrackDirty(playerId);
                        var writtenKinds = writes.Select(write => SectionCodec.FromStorageKind(write.SectionKind)).ToArray();
                        ecs.AcknowledgeDirtySections(playerId, writtenKinds);
                        foreach (var kind in writtenKinds) _dirtyAt[playerId].Remove(kind);
                        await SyncWalletAfterCommitAsync(playerId, learnResult, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    case "progression.breakthrough":
                    {
                        touched = ecs.MutatePlayer(playerId, aggregate => aggregate.Breakthrough(gameData.RealmProgression));
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
                        var travelNow = _simulationClock.UtcNow;
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
                                if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetDouble(out var quantity)
                                    && double.IsFinite(quantity) && quantity > 0)
                                    requested[entry.Name] = quantity;
                            }
                        }
                        if (requested.Count == 0)
                            return new PlayerCommandResult(false, "INVALID_MATERIALS", "提交材料为空。", before);
                        await EnsureWalletFreshAsync(playerId, cancellationToken).ConfigureAwait(false);
                        EconomyTransactionResult? contributeCommit = null;
                        var contributeResult = await nodeBuildings.ContributeAsync(
                            before.State.World.NodeId,
                            buildingId,
                            requested,
                            async applied =>
                            {
                                foreach (var (contributedItemId, quantity) in applied)
                                    if (wallet.Get(new AssetKey(new ItemId(contributedItemId), string.Empty)) + 1e-9 < quantity)
                                        return false;
                                var debitNow = DateTimeOffset.UtcNow;
                                var committed = await economy.CommitAsync(new EconomyTransactionRequest(
                                    requestId,
                                    $"building-contribute:{playerId:D}:{requestId:D}",
                                    command,
                                    playerId,
                                    applied.Select(pair => new Server.Clean.AssetDelta(playerId, pair.Key, string.Empty, -pair.Value)).ToArray(),
                                    debitNow,
                                    JsonSerializer.SerializeToUtf8Bytes(new { nodeId = before.State.World.NodeId, buildingId })),
                                    cancellationToken).ConfigureAwait(false);
                                if (committed.Applied) contributeCommit = committed;
                                return committed.Applied;
                            },
                            cancellationToken).ConfigureAwait(false);
                        if (!contributeResult.Success)
                            return new PlayerCommandResult(false, contributeResult.ErrorCode ?? "CONTRIBUTE_FAILED", contributeResult.Message ?? "材料提交失败。", before);
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
                        if (string.IsNullOrWhiteSpace(assetId) || !Enum.TryParse<MarketSide>(sideText, true, out var side))
                            return new PlayerCommandResult(false, "INVALID_ORDER", "订单品种或方向无效。", before);
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
                        if (string.IsNullOrWhiteSpace(disposeItemId) || !double.IsFinite(disposeQuantity) || disposeQuantity <= 0)
                            return new PlayerCommandResult(false, "INVALID_PAYLOAD", "物品或数量无效。", before);
                        var hasItemDefinition = gameData.Items.TryGetValue(new ItemId(disposeItemId), out var disposeDefinition);
                        if (!hasItemDefinition && !gameData.RawEquipments.ContainsKey(disposeItemId))
                            return new PlayerCommandResult(false, "ITEM_NOT_FOUND", "物品不存在。", before);
                        if (before.State.Loadout.Equipment.Any(slot => slot.ItemId == disposeItemId))
                            return new PlayerCommandResult(false, "ITEM_EQUIPPED", "已装备的物品无法处置，请先卸下。", before);
                        var spiritCredit = 0d;
                        if (command == "item.sell")
                        {
                            if (!hasItemDefinition || disposeDefinition!.SellPrice <= 0
                                || disposeItemId.StartsWith("currency.", StringComparison.Ordinal))
                                return new PlayerCommandResult(false, "ITEM_NOT_SELLABLE", "该物品不支持系统回收。", before);
                            spiritCredit = disposeDefinition.SellPrice * disposeQuantity;
                        }
                        await EnsureWalletFreshAsync(playerId, cancellationToken).ConfigureAwait(false);
                        if (wallet.Get(new AssetKey(new ItemId(disposeItemId))) + 1e-9 < disposeQuantity)
                            return new PlayerCommandResult(false, "INSUFFICIENT_ITEM", "物品数量不足。", before);
                        var disposeNow = DateTimeOffset.UtcNow;
                        var disposeDeltas = new List<Server.Clean.AssetDelta>
                        {
                            new(playerId, disposeItemId, string.Empty, -disposeQuantity)
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
                            JsonSerializer.SerializeToUtf8Bytes(new { itemId = disposeItemId, quantity = disposeQuantity }),
                            Receipts: [CreateReceipt(playerId, requestId, command, payloadHash, before.StateVersion)]),
                            cancellationToken).ConfigureAwait(false);
                        if (!disposeResult.Applied)
                            return new PlayerCommandResult(false, "TRANSACTION_REJECTED", "物品处置事务未提交。", before);
                        await SyncWalletAfterCommitAsync(playerId, disposeResult, cancellationToken).ConfigureAwait(false);
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
                        ecs.SetPresence(playerId, state);
                        break;
                    }
                    default:
                        return new PlayerCommandResult(false, "UNKNOWN_COMMAND", "未知领域命令。", before);
                }

                var after = ToSnapshot(ecs.ExportSnapshot(playerId));
                var receipt = ShouldPersistReceipt(command)
                    ? CreateReceipt(playerId, requestId, command, payloadHash, after.StateVersion)
                    : null;
                var outcome = await CommitSimulationAsync(playerId, before, after, receipt, cancellationToken).ConfigureAwait(false);
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
                notifications.Add(new RuntimeNotification(playerId, outcome.Logs, after));
                if (forfeit is { } resolvedForfeit)
                    await RouteDuelForfeitAsync(resolvedForfeit, notifications, cancellationToken).ConfigureAwait(false);
                return new PlayerCommandResult(true, "OK", "命令已提交。", after, outcome.Simulation, outcome.Logs);
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

    public ValueTask FlushAsync(Guid playerId, bool force, CancellationToken cancellationToken = default)
        => FlushLoadedAsync(playerId, force, cancellationToken);

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

    public async ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
    {
        var ids = _loaded.Keys.OrderBy(id => id).ToArray();
        var gates = ids.Select(id => _playerGates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1))).ToArray();
        var notifications = new List<RuntimeNotification>();
        Dictionary<Guid, PlayerRuntimeSnapshot>? before = null;
        var uncommitted = ids.ToHashSet();
        var acquired = 0;
        try
        {
            for (; acquired < gates.Length; acquired++)
                using (RuntimeInstrumentation.Measure("queue.actor_wait"))
                    await gates[acquired].WaitAsync(cancellationToken).ConfigureAwait(false);
            foreach (var playerId in ids) await EnsureWalletFreshAsync(playerId, cancellationToken).ConfigureAwait(false);
            foreach (var playerId in ids)
            {
                await RefreshNodeBuildingsAsync(playerId, _loaded[playerId].State.World, target, cancellationToken).ConfigureAwait(false);
                TrackDirty(playerId);
            }
            before = ids.ToDictionary(id => id, id => ToSnapshot(ecs.ExportSnapshot(id)));
            var marksBefore = ids.ToDictionary(id => id, ecs.GetDirtyMarks);
            await ecs.RunOnceAsync(target, cancellationToken).ConfigureAwait(false);
            foreach (var playerId in ids)
            {
                var after = ToSnapshot(ecs.ExportSnapshot(playerId));
                if (after.StateVersion == before[playerId].StateVersion && after.LastSimulatedAt == before[playerId].LastSimulatedAt)
                {
                    ecs.DrainSimulationResult(playerId);
                    uncommitted.Remove(playerId);
                    continue;
                }
                var outcome = await CommitSimulationAsync(playerId, before[playerId], after, null, cancellationToken).ConfigureAwait(false);
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
                // 与 OOP PlayerRuntime 一致：日志无条件下发，快照仅在模拟真实变更（Changed）时推送，
                // 空闲玩家仅时间前进不产生 ChangedSections，不得收到空增量。
                if (outcome.Logs is { Count: > 0 } || outcome.Simulation is { Changed: true })
                    notifications.Add(new RuntimeNotification(playerId, outcome.Logs,
                        outcome.Simulation is { Changed: true } ? after : null));
            }
        }
        catch
        {
            // World 已统一推进，后续玩家尚未提交时也必须恢复，避免下一轮发送失败批次遗留的日志。
            if (before is not null)
                foreach (var playerId in uncommitted)
                {
                    // 已 durable 提交但随后的钱包读取取消时，Commit 已保留新时钟；不能重复回滚。
                    if (_walletReloadRequired.ContainsKey(playerId)
                        && _loaded[playerId].LastSimulatedAt > before[playerId].LastSimulatedAt) continue;
                    RestoreUncommittedPlayer(playerId, before[playerId]);
                }
            throw;
        }
        finally
        {
            while (acquired > 0) gates[--acquired].Release();
            await EmitNotificationsAsync(notifications).ConfigureAwait(false);
        }
    }

    public async ValueTask RemoveAsync(Guid playerId, bool flush, CancellationToken cancellationToken = default)
    {
        if (!_loaded.TryGetValue(playerId, out var snapshot)) return;
        var gate = _playerGates.GetOrAdd(playerId, static _ => new SemaphoreSlim(1, 1));
        using (RuntimeInstrumentation.Measure("queue.actor_wait"))
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded.TryGetValue(playerId, out snapshot)) return;
            if (flush)
            {
                if (snapshot.State.Presence == PresenceState.Online) ecs.SetPresence(playerId, PresenceState.Offline);
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
        }
        finally { gate.Release(); }
    }

    public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId)
        => _logs.TryGetValue(playerId, out var log) ? log.SnapshotRecent() : [];

    public ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<PlayerRuntimeSnapshot>>(_loaded.Values.OrderBy(value => value.PlayerId).ToArray());

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        foreach (var playerId in _loaded.Keys.ToArray())
        {
            try { await RemoveAsync(playerId, true, CancellationToken.None).ConfigureAwait(false); }
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
        TrackDirty(playerId);
        var dirty = ecs.GetDirtySections(playerId);
        var pending = _pendingReceipts[playerId];
        if (dirty.Length == 0 && pending.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var eligible = dirty.Where(section => force || pending.Count > 0
            || now - _dirtyAt[playerId][section] >= TimeSpan.FromSeconds(15)).ToHashSet();
        if (eligible.Count == 0 && pending.Count == 0) return;
        var snapshot = ToSnapshot(ecs.ExportSnapshot(playerId));
        var writes = SectionCodec.EncodeAggregateFull(snapshot.State, now)
            .Where(write => eligible.Contains(SectionCodec.FromStorageKind(write.SectionKind))).ToArray();
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

    private async ValueTask RefreshNodeBuildingsAsync(Guid playerId, PlayerWorldStateDocument world,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (nodeBuildings is null) return;
        if (!string.IsNullOrWhiteSpace(world.EdgeId) || string.IsNullOrWhiteSpace(world.NodeId))
        {
            ecs.ApplyNodeBuildingProjection(playerId, new(0, [], BuildingEffectSet.Empty, null), now);
            return;
        }
        var view = await nodeBuildings.BuildingsForAsync(world.NodeId, now, cancellationToken).ConfigureAwait(false);
        var effects = NodeBuildingRules.AggregateEffects(view.Buildings, gameData.Buildings,
            now.ToUnixTimeMilliseconds() / 1000d);
        ecs.ApplyNodeBuildingProjection(playerId, new(view.Version, view.Buildings, effects, world.NodeId), now);
    }

    private sealed record CommitOutcome(IReadOnlyList<LogEventDto>? Logs, SimulationResult? Simulation);

    private async ValueTask<CommitOutcome> CommitSimulationAsync(Guid playerId, PlayerRuntimeSnapshot before,
        PlayerRuntimeSnapshot after, CommandReceipt? receipt, CancellationToken cancellationToken)
    {
        var simulation = ecs.DrainSimulationResult(playerId);
        var committed = false;
        try
        {
            if (simulation is { AssetDeltas.Count: > 0 })
            {
                var transaction = SimulationEconomyTransaction.Create(playerId, simulation, simulationTransactionIdentity);
                var result = await economy.CommitAsync(transaction, cancellationToken).ConfigureAwait(false);
                if (!result.Applied) throw new InvalidOperationException("ECS 模拟资产事务未提交。");
                committed = true;
                if (result.Balances is { } returned)
                {
                    foreach (var balance in returned)
                        _wallets[playerId][new AssetKey(new ItemId(balance.AssetId), balance.Variant)] = balance;
                    SyncEcsWallet(playerId);
                }
                else
                {
                    try { await ReloadWalletAsync(playerId, cancellationToken).ConfigureAwait(false); }
                    catch (Exception exception)
                    {
                        // 事务已持久提交，时钟不能回滚；保留模拟终态并在下次命令前强制重读余额。
                        _walletReloadRequired[playerId] = 0;
                        var previous = _wallets[playerId];
                        var touched = simulation.AssetDeltas.Select(delta => delta.Asset).ToHashSet();
                        foreach (var balance in after.Assets)
                        {
                            var key = new AssetKey(new ItemId(balance.AssetId), balance.Variant);
                            var version = previous.TryGetValue(key, out var old) ? old.Version : 0;
                            previous[key] = balance with { Version = version + (touched.Contains(key) ? 1 : 0) };
                        }
                        _logger.LogError(exception, "ECS 模拟资产已提交但钱包刷新失败 player={PlayerId}", playerId);
                        if (exception is OperationCanceledException) throw;
                    }
                }
            }
            if (receipt is not null) _pendingReceipts[playerId].Add(receipt);
            TrackDirty(playerId);
        }
        catch
        {
            if (!committed) RestoreUncommittedPlayer(playerId, before);
            else
            {
                TrackDirty(playerId);
                _loaded[playerId] = ToSnapshot(ecs.ExportSnapshot(playerId));
            }
            throw;
        }
        // Mapper 推进日志 sequence 与回放缓冲，只能在经济事务成功后执行。
        var events = ecs.DrainSimulationEvents(playerId);
        var logs = events.Count == 0 ? null : SimulationLogMapper.ToLogEvents(events, gameData,
            _logs.GetOrAdd(playerId, static _ => new PlayerLogBuffer(LogBufferCapacity)), LogBufferCapacity);
        return new(logs, simulation);
    }

    private async ValueTask EnsureWalletFreshAsync(Guid playerId, CancellationToken cancellationToken)
    {
        if (_walletReloadRequired.ContainsKey(playerId)) await ReloadWalletAsync(playerId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ReloadWalletAsync(Guid playerId, CancellationToken cancellationToken)
    {
        var rows = await assets.LoadBalancesAsync(playerId, cancellationToken).ConfigureAwait(false);
        _wallets[playerId] = rows.ToDictionary(row => new AssetKey(new ItemId(row.AssetId), row.Variant));
        SyncEcsWallet(playerId);
        _walletReloadRequired.TryRemove(playerId, out _);
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
                _wallets[playerId][new AssetKey(new ItemId(balance.AssetId), balance.Variant)] = balance;
            SyncEcsWallet(playerId);
            return;
        }
        await ReloadWalletAsync(playerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>本侧弃战产生的对手判胜通知：对手已加载时在其锁内记账，并广播对方快照（对齐 OOP RouteDuelForfeitsAsync）。</summary>
    private async ValueTask RouteDuelForfeitAsync((Guid DuelId, Guid PeerId) forfeit,
        List<RuntimeNotification> notifications, CancellationToken cancellationToken)
    {
        if (forfeit.PeerId == Guid.Empty || !_loaded.ContainsKey(forfeit.PeerId)) return;
        var peerGate = _playerGates.GetOrAdd(forfeit.PeerId, static _ => new SemaphoreSlim(1, 1));
        await peerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var applied = false;
            ecs.MutatePlayer(forfeit.PeerId, aggregate => applied = aggregate.ApplyPeerForfeit(forfeit.DuelId));
            if (!applied) return;
            var peerAfter = ToSnapshot(ecs.ExportSnapshot(forfeit.PeerId));
            _loaded[forfeit.PeerId] = peerAfter;
            notifications.Add(new RuntimeNotification(forfeit.PeerId, null, peerAfter));
        }
        finally { peerGate.Release(); }
    }

    /// <summary>命令回执持久化清单（与 OOP ShouldPersistReceipt 一致）；market.place/cancel 走订单幂等键不入回执。</summary>
    private static bool ShouldPersistReceipt(string command) => command is
        "action.enqueue" or "action.remove" or
        "queue.configure" or "queue.move" or "queue.auto_use.configure" or
        "loadout.replace" or "ability.learn" or "progression.breakthrough" or "world.travel" or
        "building.build" or "building.upgrade" or "building.contribute" or "player.rename" or "market.claim" or
        "item.discard" or "item.sell";

    private void RestoreUncommittedPlayer(Guid playerId, PlayerRuntimeSnapshot before)
    {
        var balances = before.Assets.Select(row => new AssetBalance(new PlayerId(playerId),
            new AssetKey(new ItemId(row.AssetId), row.Variant), row.Quantity, row.Version)).ToArray();
        ecs.RestorePlayer(playerId, before.State, balances);
        ecs.RestoreDirtySections(playerId, _dirtyAt[playerId].Keys);
        ecs.DrainSimulationEvents(playerId);
    }

    private async ValueTask EmitNotificationsAsync(List<RuntimeNotification> notifications)
    {
        // 网络订阅者只能在玩家锁外执行；缓冲与快照已经在锁内一起前移。
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
