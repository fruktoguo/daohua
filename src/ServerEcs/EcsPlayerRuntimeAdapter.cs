using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using DaoHua.Ecs;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using Protocol;
using Server.Clean;

namespace ServerEcs;

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
    NodeBuildingRegistry? nodeBuildings = null) : IPlayerRuntime
{
    private readonly ConcurrentDictionary<Guid, PlayerRuntimeSnapshot> _loaded = new();
    private readonly ConcurrentDictionary<Guid, Dictionary<AssetKey, AssetBalanceRecord>> _wallets = new();
    private readonly ConcurrentDictionary<Guid, Dictionary<PlayerSectionKind, DateTimeOffset>> _dirtyAt = new();
    private readonly ConcurrentDictionary<Guid, List<CommandReceipt>> _pendingReceipts = new();
    private readonly ConcurrentDictionary<Guid, byte> _walletReloadRequired = new();
    private readonly ConcurrentDictionary<Guid, PlayerLogBuffer> _logs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _playerGates = new();
    private readonly ConcurrentDictionary<Guid, RecentRequests> _recentRequests = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
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

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        finally { _loadGate.Release(); }
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
            if (commandReceipts is not null && command is "action.enqueue" or "action.remove")
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
                switch (command)
                {
                    case "action.enqueue":
                    {
                        var request = JsonSerializer.Deserialize<ActionIntentRequest>(payload, ProtocolJson.Options)
                            ?? throw new JsonException("行动载荷为空。");
                        if (!Guid.TryParse(request.RequestId, out var actionRequestId) || actionRequestId != requestId)
                            throw new DomainException(DomainErrorCode.InvalidArgument, "请求标识不一致。");
                        var existing = before.State.Actions.FirstOrDefault(action => action.Id == requestId);
                        if (existing is not null)
                        {
                            if (existing.Intent.ActionId.Value != request.ActionId || existing.Intent.RepeatCount != request.RepeatCount)
                                return new PlayerCommandResult(false, "REQUEST_ID_CONFLICT", "请求 ID 已用于其他行动。", before);
                            return new PlayerCommandResult(true, "IDEMPOTENT_REPLAY", "命令已处理。", before);
                        }
                        ecs.EnqueueAction(playerId, new ActionId(request.ActionId), request.RepeatCount, requestId);
                        break;
                    }
                    case "action.remove":
                    {
                        if (!TryReadGuid(payload, "actionInstanceId", out var actionId))
                            throw new DomainException(DomainErrorCode.InvalidArgument, "行动实例标识无效。");
                        if (!ecs.RemoveAction(playerId, actionId))
                            return new PlayerCommandResult(false, "ACTION_NOT_FOUND", "行动不存在。", before);
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
                var receipt = command is "action.enqueue" or "action.remove"
                    ? CreateReceipt(playerId, requestId, command, payloadHash, after.StateVersion)
                    : null;
                var outcome = await CommitSimulationAsync(playerId, before, after, receipt, cancellationToken).ConfigureAwait(false);
                after = ToSnapshot(ecs.ExportSnapshot(playerId));
                _loaded[playerId] = after;
                recent.Remember(requestId, command, payloadHash);
                notifications.Add(new RuntimeNotification(playerId, outcome.Logs, after));
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
                _loaded[playerId] = after;
                uncommitted.Remove(playerId);
                notifications.Add(new RuntimeNotification(playerId, outcome.Logs, after));
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
        _loadGate.Dispose();
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
