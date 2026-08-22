using System.Collections.Concurrent;
using System.Text.Json;
using Daoxuan.Domain.Market;
using Daoxuan.Domain.Models;
using Daoxuan.Domain.Simulation;
using Daoxuan.Protocol;
using Daoxuan.Server.Infrastructure;
using YuoCore.Ecs;
using YuoCore.Persistence;
using YuoCore.Runtime;

namespace Daoxuan.Server.Runtime;

public sealed class DaoxuanGameHost : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan ColdPlayerRetention = TimeSpan.FromSeconds(30);
    private readonly IYuoSnapshotStore _snapshotStore;
    private readonly IDaoxuanPersistenceQueue _persistenceQueue;
    private readonly DaoxuanMarketRuntime _marketRuntime;
    private readonly DaoxuanSimulationScheduleSignal _scheduleSignal;
    private readonly DaoxuanSimulationClock _clock;
    private readonly YuoCoreRuntime _runtime = new();
    private readonly YuoFiber _fiber;
    private readonly SemaphoreSlim _ownerGate = new(1, 1);
    private readonly Dictionary<Guid, YuoEntity> _players = [];
    // 冷玩家仍保留最新序列化快照，避免断开连接后再次请求必须阻塞 Fiber 做磁盘读取。
    // 容量上限依据：平均每份快照数 KB，2 万份对应数十 MB 内存量级，可长期驻留而不失控。
    // 安全性：冷快照只是内存缓存，每次写入 _coldSnapshots 的同时都会 EnqueueAsync(SaveSnapshotCommand)
    // 异步落盘（断线路径与 EvictIdlePlayersAsync 均如此）；被 LRU 淘汰的玩家重连时经 EnsurePlayerAsync
    // 从磁盘快照加载，并由确定性模拟 DaoxuanSimulation.AdvanceTo(state, now) 补算到当前时刻，
    // 结果与一直驻留内存一致——这是本项目确定性模拟设计的直接推论。淘汰仅移除内存缓存，
    // 不落盘、不打错误日志。
    private const int MaxColdSnapshotCount = 20_000;
    private readonly Dictionary<Guid, byte[]> _coldSnapshots = [];
    // LRU 顺序结构：First 为最旧、Last 为最新；与 _coldSnapshots 同在 owner Fiber 上串行访问，无需加锁。
    private readonly LinkedList<Guid> _coldSnapshotLru = [];
    private readonly Dictionary<Guid, LinkedListNode<Guid>> _coldSnapshotLruNodes = [];
    private readonly HashSet<Guid> _onlinePlayers = [];
    private readonly PriorityQueue<ScheduledPlayerTransition, DateTimeOffset> _onlineTransitionQueue = new();
    private readonly Dictionary<Guid, long> _onlineTransitionGenerations = [];
    private readonly ConcurrentDictionary<Guid, byte> _knownPlayers = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastPlayerAccess = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, DaoxuanSession>> _sessions = new();
    private readonly ConcurrentDictionary<string, Guid> _takenPlayerNames = new(StringComparer.OrdinalIgnoreCase);
    private long _maxSequenceId;
    private long _entitySequence = 10_000;
    private long _packetSequence;
    private long _simulationRevision;
    private long _transitionGeneration;
    private int _loadedPlayerCount;
    private bool _disposed;

    public DaoxuanGameHost(
        IYuoSnapshotStore snapshotStore,
        IDaoxuanPersistenceQueue persistenceQueue,
        DaoxuanMarketRuntime marketRuntime,
        DaoxuanSimulationScheduleSignal scheduleSignal,
        DaoxuanSimulationClock clock)
    {
        _snapshotStore = snapshotStore;
        _persistenceQueue = persistenceQueue;
        _marketRuntime = marketRuntime;
        _scheduleSignal = scheduleSignal;
        _clock = clock;
        _fiber = _runtime.Fibers.CreateFiber(new YuoFiberOptions
        {
            Id = 1,
            Name = "Daoxuan-PlayerShard-0",
            SetWorldAsDefault = false,
            MaxMailboxMessagesPerTick = 100_000
        });
    }

    public long SimulationRevision => Volatile.Read(ref _simulationRevision);
    public int LoadedPlayerCount => Volatile.Read(ref _loadedPlayerCount);
    public int ConnectedSessionCount => _sessions.Values.Sum(group => group.Count);

    public bool IsPlayerNameTaken(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _takenPlayerNames.ContainsKey(name.Trim());
    }

    public long GetNextSequenceId()
    {
        return Interlocked.Increment(ref _maxSequenceId);
    }

    public async Task EnsureRegisteredPlayerSnapshotAsync(
        Guid playerId,
        string name,
        string account,
        long sequenceId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("玩家 ID 不能为空。", nameof(playerId));
        }

        if (sequenceId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceId), "账号序列号必须为正数。 ");
        }

        var snapshotId = playerId.ToString("D");
        if (await _snapshotStore.LoadAsync("player", snapshotId, cancellationToken).ConfigureAwait(false) is not null)
        {
            ObserveSequenceId(sequenceId);
            return;
        }

        var timestamp = createdAt == default ? _clock.UtcNow : createdAt;
        var state = DaoxuanSimulation.CreateDefaultState(playerId, name, timestamp, account);
        state.SequenceId = sequenceId;
        state.CreatedAt = timestamp;
        state.LastSimulatedAt = timestamp;
        state.PresenceMode = DaoxuanPresenceMode.IdleOffline;
        state.DirtySections = DaoxuanDirtySections.None;
        ObserveSequenceId(sequenceId);
        _takenPlayerNames[state.Name] = playerId;

        await _snapshotStore.SaveAsync(new YuoSnapshotRecord(
            "player",
            snapshotId,
            DaoxuanStateSerializer.Serialize(state),
            _clock.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> IsPlayerLocallyResidentAsync(Guid playerId, CancellationToken cancellationToken)
    {
        return ExecuteOnOwnerAsync(_ => _players.ContainsKey(playerId) || _coldSnapshots.ContainsKey(playerId), cancellationToken);
    }

    public Task<DaoxuanSession> RegisterSessionAsync(
        Guid playerId,
        string? name,
        CancellationToken cancellationToken)
    {
        return RegisterSessionAsync(playerId, name, null, cancellationToken);
    }

    public async Task<DaoxuanSession> RegisterSessionAsync(
        Guid playerId,
        string? name,
        string? account,
        CancellationToken cancellationToken)
    {
        await EnsurePlayerAsync(playerId, name, account, cancellationToken);
        var session = new DaoxuanSession(playerId);
        var snapshot = await ExecuteOnOwnerAsync(_ =>
        {
            var state = GetState(playerId);
            DaoxuanSimulation.SetPresence(state, DaoxuanPresenceMode.Online, _clock.UtcNow);
            _onlinePlayers.Add(playerId);
            ScheduleNextTransition(state);
            state.DirtySections = DaoxuanDirtySections.None;
            var result = DaoxuanStateMapper.ToSnapshot(state, _clock.UtcNow);
            state.PendingEvents.Clear();
            return result;
        }, cancellationToken);

        _sessions.GetOrAdd(playerId, _ => new ConcurrentDictionary<Guid, DaoxuanSession>())[session.SessionId] = session;
        session.TryEnqueueSnapshot(DaoxuanPacketCodec.Encode(
            DaoxuanPacketKinds.Snapshot,
            string.Empty,
            NextPacketSequence(),
            snapshot));
        _scheduleSignal.Pulse();
        return session;
    }

    public async Task UnregisterSessionAsync(DaoxuanSession session, CancellationToken cancellationToken)
    {
        session.Complete();
        if (_sessions.TryGetValue(session.PlayerId, out var sessions))
        {
            sessions.TryRemove(session.SessionId, out _);
            if (sessions.IsEmpty)
            {
                _sessions.TryRemove(session.PlayerId, out _);
                var record = await ExecuteOnOwnerAsync(_ =>
                {
                    var state = GetState(session.PlayerId);
                    _onlinePlayers.Remove(session.PlayerId);
                    InvalidateScheduledTransition(session.PlayerId);
                    if (state.PresenceMode == DaoxuanPresenceMode.Online)
                    {
                        DaoxuanSimulation.SetPresence(state, DaoxuanPresenceMode.IdleOffline, _clock.UtcNow);
                    }

                    return ToSnapshotRecord(state);
                }, CancellationToken.None);
                // 断开连接的清理令牌通常已被取消，但这里的状态已经发生变化；
                // 继续把快照写入队列，再由 Fiber 安全地转为冷存档，不能留下半个在线实体。
                var coldRecord = await ExecuteOnOwnerAsync(
                    _ => EvictPlayerToColdSnapshot(session.PlayerId),
                    CancellationToken.None);
                await _persistenceQueue.EnqueueAsync(
                    new SaveSnapshotCommand(coldRecord ?? record),
                    CancellationToken.None);
                _scheduleSignal.Pulse();
            }
        }
    }

    public async Task<DaoxuanCommandOutcome> HandleCommandAsync(
        Guid playerId,
        DaoxuanIncomingPacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Kind != DaoxuanPacketKinds.Command)
        {
            return DaoxuanCommandOutcome.Failed("INVALID_PACKET_KIND", "这里只接受 command 数据包。");
        }

        if (!packet.Payload.TryGetProperty("command", out var commandElement)
            || commandElement.GetString() is not { Length: > 0 } command
            || !packet.Payload.TryGetProperty("payload", out var payload))
        {
            return DaoxuanCommandOutcome.Failed("INVALID_COMMAND", "命令包缺少 command 或 payload。");
        }

        await EnsurePlayerAsync(playerId, null, cancellationToken);
        if (command is "market_place" or "market_cancel" or "market_claim")
        {
            return await HandleMarketCommandAsync(playerId, command, payload, cancellationToken);
        }

        DaoxuanPlayerPatchDto? patch = null;
        byte[]? snapshotPacket = null;
        YuoSnapshotRecord? record = null;
        var commandAt = _clock.UtcNow;
        var result = await ExecuteOnOwnerAsync(_ =>
        {
            var state = GetState(playerId);
            DaoxuanSimulation.AdvanceTo(state, commandAt);
            var commandResult = ApplyCommand(state, command, payload, commandAt);
            if (commandResult.Success)
            {
                DaoxuanSimulation.AdvanceTo(state, commandAt);
                if (state.PresenceMode == DaoxuanPresenceMode.Online)
                {
                    _onlinePlayers.Add(playerId);
                }
                else
                {
                    _onlinePlayers.Remove(playerId);
                }
            }
            ScheduleNextTransition(state);

            var sections = state.DirtySections;
            if (sections != DaoxuanDirtySections.None)
            {
                patch = DaoxuanStateMapper.ToPatch(state, sections, commandAt);
                if (HasSessionNeedingResync(playerId))
                {
                    snapshotPacket = DaoxuanPacketCodec.Encode(
                        DaoxuanPacketKinds.Snapshot,
                        string.Empty,
                        NextPacketSequence(),
                        DaoxuanStateMapper.ToSnapshot(state, commandAt));
                }

                record = ToSnapshotRecord(state);
                state.PendingEvents.Clear();
                state.DirtySections = DaoxuanDirtySections.None;
            }

            return commandResult;
        }, cancellationToken);

        if (record is not null)
        {
            await _persistenceQueue.EnqueueAsync(new SaveSnapshotCommand(record), cancellationToken);
        }

        if (patch is not null)
        {
            PublishUpdate(new PlayerUpdate(
                playerId,
                DaoxuanPacketCodec.Encode(DaoxuanPacketKinds.Delta, string.Empty, NextPacketSequence(), patch),
                snapshotPacket,
                record));
        }

        _scheduleSignal.Pulse();
        return new DaoxuanCommandOutcome(result, patch);
    }

    private async Task<DaoxuanCommandOutcome> HandleMarketCommandAsync(
        Guid playerId,
        string command,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (command)
            {
                case "market_place":
                {
                    var request = payload.Deserialize<DaoxuanMarketPlaceRequest>(DaoxuanPacketCodec.JsonOptions);
                    if (request is null || !TryParseMarketSide(request.Side, out var side))
                    {
                        return DaoxuanCommandOutcome.Failed("INVALID_MARKET_ORDER", "市场订单参数无效。");
                    }

                    var outcome = await PlaceMarketOrderAsync(
                        playerId,
                        new DaoxuanMarketOrderRequest(
                            request.OrderId,
                            playerId,
                            new DaoxuanMarketInstrument(request.ItemId, request.EnhancementLevel),
                            side,
                            request.Price,
                            request.Quantity),
                        cancellationToken);
                    return new DaoxuanCommandOutcome(outcome.Result, null);
                }
                case "market_cancel":
                {
                    var request = payload.Deserialize<DaoxuanMarketCancelRequest>(DaoxuanPacketCodec.JsonOptions);
                    if (request is null)
                    {
                        return DaoxuanCommandOutcome.Failed("INVALID_ORDER_ID", "缺少订单 ID。");
                    }

                    var outcome = await CancelMarketOrderAsync(playerId, request.OrderId, cancellationToken);
                    return new DaoxuanCommandOutcome(outcome.Result, null);
                }
                case "market_claim":
                {
                    var outcome = await ClaimMarketAssetsAsync(playerId, cancellationToken);
                    return new DaoxuanCommandOutcome(outcome.Result, null);
                }
                default:
                    return DaoxuanCommandOutcome.Failed("COMMAND_NOT_FOUND", "未知市场命令。");
            }
        }
        catch (JsonException)
        {
            return DaoxuanCommandOutcome.Failed("INVALID_PAYLOAD", "市场命令参数格式错误。");
        }
    }

    public async Task<DaoxuanPlayerSnapshotDto> GetSnapshotAsync(
        Guid playerId,
        CancellationToken cancellationToken)
    {
        await EnsurePlayerAsync(playerId, null, cancellationToken);
        return await ExecuteOnOwnerAsync(
            _ => DaoxuanStateMapper.ToSnapshot(GetState(playerId), _clock.UtcNow),
            cancellationToken);
    }

    public async Task<DaoxuanMarketCommandOutcome> PlaceMarketOrderAsync(
        Guid playerId,
        DaoxuanMarketOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!_marketRuntime.IsReady)
        {
            return DaoxuanMarketCommandOutcome.Failed("MARKET_NOT_READY", "市场账本尚未完成恢复。");
        }

        if (request is null || request.OwnerId != playerId)
        {
            return DaoxuanMarketCommandOutcome.Failed("ORDER_FORBIDDEN", "订单所有者必须是当前玩家。");
        }

        if (!_marketRuntime.Owns(request.Instrument))
        {
            return DaoxuanMarketCommandOutcome.Failed(
                "MARKET_WRONG_PARTITION",
                $"该市场品种由主分片 {_marketRuntime.GetOwnerPartition(request.Instrument)} 负责。");
        }

        await EnsurePlayerAsync(playerId, null, cancellationToken);
        using var writeLease = await _marketRuntime.AcquireWriteAsync(cancellationToken);
        DaoxuanPlayerPatchDto? patch = null;
        DaoxuanPlayerSnapshotDto? snapshot = null;
        YuoSnapshotRecord? record = null;
        var result = await ExecuteOnOwnerAsync(_ =>
        {
            var state = GetState(playerId);
            var placement = _marketRuntime.Coordinator.Place(state, request);
            if (placement.Success)
            {
                ScheduleNextTransition(state);
                CapturePlayerMutation(state, out patch, out snapshot, out record);
            }

            return placement;
        }, cancellationToken);

        if (!result.Success) return DaoxuanMarketCommandOutcome.From(result, snapshot);
        await PersistPlayerAndMarketAsync(playerId, patch, snapshot, record, cancellationToken);
        _scheduleSignal.Pulse();
        return DaoxuanMarketCommandOutcome.From(result, snapshot);
    }

    public async Task<DaoxuanMarketCommandOutcome> CancelMarketOrderAsync(
        Guid playerId,
        string orderId,
        CancellationToken cancellationToken)
    {
        if (!_marketRuntime.IsReady)
        {
            return DaoxuanMarketCommandOutcome.Failed("MARKET_NOT_READY", "市场账本尚未完成恢复。");
        }

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return DaoxuanMarketCommandOutcome.Failed("INVALID_ORDER_ID", "订单 ID 不能为空。");
        }

        await EnsurePlayerAsync(playerId, null, cancellationToken);
        using var writeLease = await _marketRuntime.AcquireWriteAsync(cancellationToken);
        DaoxuanPlayerPatchDto? patch = null;
        DaoxuanPlayerSnapshotDto? snapshot = null;
        YuoSnapshotRecord? record = null;
        var result = await ExecuteOnOwnerAsync(_ =>
        {
            var state = GetState(playerId);
            var cancellation = _marketRuntime.Coordinator.Cancel(state, orderId);
            if (cancellation.Success)
            {
                ScheduleNextTransition(state);
                CapturePlayerMutation(state, out patch, out snapshot, out record);
            }

            return cancellation;
        }, cancellationToken);

        if (!result.Success) return DaoxuanMarketCommandOutcome.From(result, snapshot);
        await PersistPlayerAndMarketAsync(playerId, patch, snapshot, record, cancellationToken);
        _scheduleSignal.Pulse();
        return DaoxuanMarketCommandOutcome.From(result, snapshot);
    }

    public async Task<DaoxuanMarketCommandOutcome> ClaimMarketAssetsAsync(
        Guid playerId,
        CancellationToken cancellationToken)
    {
        if (!_marketRuntime.IsReady)
        {
            return DaoxuanMarketCommandOutcome.Failed("MARKET_NOT_READY", "市场账本尚未完成恢复。");
        }

        await EnsurePlayerAsync(playerId, null, cancellationToken);
        using var writeLease = await _marketRuntime.AcquireWriteAsync(cancellationToken);
        DaoxuanPlayerPatchDto? patch = null;
        DaoxuanPlayerSnapshotDto? snapshot = null;
        YuoSnapshotRecord? record = null;
        var changed = await ExecuteOnOwnerAsync(_ =>
        {
            var state = GetState(playerId);
            var claimed = _marketRuntime.Coordinator.Claim(state);
            if (claimed)
            {
                ScheduleNextTransition(state);
                CapturePlayerMutation(state, out patch, out snapshot, out record);
            }

            return claimed;
        }, cancellationToken);

        if (!changed)
        {
            return DaoxuanMarketCommandOutcome.Failed("NOTHING_TO_CLAIM", "当前没有待领取的市场资产。");
        }

        await PersistPlayerAndMarketAsync(playerId, patch, snapshot, record, cancellationToken);
        _scheduleSignal.Pulse();
        return DaoxuanMarketCommandOutcome.Ok("市场资产已领取。", snapshot);
    }

    public async Task<DaoxuanPlayerSnapshotDto> FastForwardAsync(
        Guid playerId,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(30))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "快进时长必须大于零且不超过 30 天。");
        }

        await EnsurePlayerAsync(playerId, null, cancellationToken);
        YuoSnapshotRecord? record = null;
        var snapshot = await ExecuteOnOwnerAsync(_ =>
        {
            var state = GetState(playerId);
            if (state.PresenceMode == DaoxuanPresenceMode.FullyOffline)
            {
                DaoxuanSimulation.SetPresence(state, DaoxuanPresenceMode.IdleOffline, state.LastSimulatedAt);
            }

            DaoxuanSimulation.AdvanceTo(state, state.LastSimulatedAt.Add(duration));
            ScheduleNextTransition(state);
            record = ToSnapshotRecord(state);
            state.DirtySections = DaoxuanDirtySections.None;
            return DaoxuanStateMapper.ToSnapshot(state, state.LastSimulatedAt);
        }, cancellationToken);

        if (record is not null)
        {
            await _persistenceQueue.EnqueueAsync(new SaveSnapshotCommand(record), cancellationToken);
        }

        _scheduleSignal.Pulse();
        return snapshot;
    }

    public async Task<DateTimeOffset?> GetNextOnlineTransitionAtAsync(CancellationToken cancellationToken)
    {
        return await ExecuteOnOwnerAsync(_ =>
        {
            return PeekNextTransitionAt();
        }, cancellationToken);
    }

    public async Task<DaoxuanAdvanceReport> AdvanceDueOnlinePlayersAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var updates = await ExecuteOnOwnerAsync(_ =>
        {
            var results = new List<PlayerUpdate>();
            foreach (var playerId in TakeDuePlayers(now))
            {
                if (!_players.TryGetValue(playerId, out var entity)) continue;
                var state = entity.GetComponent<DaoxuanPlayerComponent>().State;

                var simulation = DaoxuanSimulation.AdvanceTo(state, now);
                var sections = state.DirtySections | simulation.DirtySections;
                ScheduleNextTransition(state);
                if (sections == DaoxuanDirtySections.None) continue;

                var patch = DaoxuanStateMapper.ToPatch(state, sections, now);
                byte[]? snapshotPacket = null;
                if (HasSessionNeedingResync(state.PlayerId))
                {
                    snapshotPacket = DaoxuanPacketCodec.Encode(
                        DaoxuanPacketKinds.Snapshot,
                        string.Empty,
                        NextPacketSequence(),
                        DaoxuanStateMapper.ToSnapshot(state, now));
                }

                results.Add(new PlayerUpdate(
                    state.PlayerId,
                    DaoxuanPacketCodec.Encode(DaoxuanPacketKinds.Delta, string.Empty, NextPacketSequence(), patch),
                    snapshotPacket,
                    simulation.CompletedActions > 0 ? ToSnapshotRecord(state) : null));
                state.PendingEvents.Clear();
                state.DirtySections = DaoxuanDirtySections.None;
            }

            return results;
        }, cancellationToken);

        foreach (var update in updates)
        {
            PublishUpdate(update);
            if (update.SnapshotRecord is not null)
            {
                await _persistenceQueue.EnqueueAsync(new SaveSnapshotCommand(update.SnapshotRecord), cancellationToken);
            }
        }

        if (updates.Count > 0)
        {
            Interlocked.Increment(ref _simulationRevision);
        }
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
        return new DaoxuanAdvanceReport(SimulationRevision, updates.Count, elapsed.TotalMilliseconds);
    }

    public async Task CheckpointOnlinePlayersAsync(CancellationToken cancellationToken)
    {
        var records = await ExecuteOnOwnerAsync(_ => _onlinePlayers
            .Where(_players.ContainsKey)
            .Select(playerId => ToSnapshotRecord(
                _players[playerId].GetComponent<DaoxuanPlayerComponent>().State))
            .ToArray(), cancellationToken);
        if (records.Length == 0) return;
        await _persistenceQueue.EnqueueBatchAsync(
            records.Select(record => (YuoPersistenceCommand)new SaveSnapshotCommand(record)).ToArray(),
            cancellationToken);
    }

    public async Task EvictIdlePlayersAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var records = await ExecuteOnOwnerAsync(
            _ => EvictIdlePlayersToColdSnapshots(now),
            cancellationToken);
        foreach (var record in records)
        {
            await _persistenceQueue.EnqueueAsync(new SaveSnapshotCommand(record), cancellationToken);
        }
    }

    private void ScheduleNextTransition(DaoxuanPlayerState state)
    {
        if (state.PresenceMode != DaoxuanPresenceMode.Online
            || !_onlinePlayers.Contains(state.PlayerId))
        {
            InvalidateScheduledTransition(state.PlayerId);
            return;
        }

        var generation = ++_transitionGeneration;
        _onlineTransitionGenerations[state.PlayerId] = generation;
        var deadline = DaoxuanSimulation.GetNextTransitionAt(state);
        if (deadline is not null)
        {
            _onlineTransitionQueue.Enqueue(
                new ScheduledPlayerTransition(state.PlayerId, generation),
                deadline.Value);
        }

        CompactTransitionQueueIfNeeded();
    }

    private void InvalidateScheduledTransition(Guid playerId)
    {
        _onlineTransitionGenerations.Remove(playerId);
    }

    private DateTimeOffset? PeekNextTransitionAt()
    {
        while (_onlineTransitionQueue.TryPeek(out var scheduled, out var deadline))
        {
            if (IsCurrentSchedule(scheduled)) return deadline;
            _onlineTransitionQueue.Dequeue();
        }

        return null;
    }

    private IReadOnlyList<Guid> TakeDuePlayers(DateTimeOffset now)
    {
        var due = new List<Guid>();
        while (_onlineTransitionQueue.TryPeek(out var scheduled, out var deadline)
               && deadline <= now)
        {
            _onlineTransitionQueue.Dequeue();
            if (!IsCurrentSchedule(scheduled)) continue;
            _onlineTransitionGenerations.Remove(scheduled.PlayerId);
            due.Add(scheduled.PlayerId);
        }

        return due;
    }

    private bool IsCurrentSchedule(ScheduledPlayerTransition scheduled)
    {
        return _onlinePlayers.Contains(scheduled.PlayerId)
            && _onlineTransitionGenerations.TryGetValue(scheduled.PlayerId, out var generation)
            && generation == scheduled.Generation;
    }

    private void CompactTransitionQueueIfNeeded()
    {
        var liveCount = _onlineTransitionGenerations.Count;
        if (_onlineTransitionQueue.Count <= Math.Max(1_024, liveCount * 4)) return;

        _onlineTransitionQueue.Clear();
        foreach (var playerId in _onlinePlayers)
        {
            if (!_players.TryGetValue(playerId, out var entity)) continue;
            var state = entity.GetComponent<DaoxuanPlayerComponent>().State;
            var generation = ++_transitionGeneration;
            _onlineTransitionGenerations[playerId] = generation;
            var deadline = DaoxuanSimulation.GetNextTransitionAt(state);
            if (deadline is not null)
            {
                _onlineTransitionQueue.Enqueue(
                    new ScheduledPlayerTransition(playerId, generation),
                    deadline.Value);
            }
        }
    }

    public async Task FlushAllAsync(CancellationToken cancellationToken)
    {
        var records = await ExecuteOnOwnerAsync(_ => _players.Values
            .Select(entity => ToSnapshotRecord(entity.GetComponent<DaoxuanPlayerComponent>().State))
            .ToArray(), cancellationToken);
        await _persistenceQueue.EnqueueBatchAsync(records.Select(record => (YuoPersistenceCommand)new SaveSnapshotCommand(record)).ToArray(), cancellationToken);
    }

    private void CapturePlayerMutation(
        DaoxuanPlayerState state,
        out DaoxuanPlayerPatchDto? patch,
        out DaoxuanPlayerSnapshotDto? snapshot,
        out YuoSnapshotRecord? record)
    {
        var sections = state.DirtySections;
        if (sections == DaoxuanDirtySections.None)
        {
            patch = null;
            snapshot = null;
            record = null;
            return;
        }

        var now = _clock.UtcNow;
        patch = DaoxuanStateMapper.ToPatch(state, sections, now);
        snapshot = DaoxuanStateMapper.ToSnapshot(state, now);
        record = ToSnapshotRecord(state);
        state.PendingEvents.Clear();
        state.DirtySections = DaoxuanDirtySections.None;
    }

    private async Task PersistPlayerAndMarketAsync(
        Guid playerId,
        DaoxuanPlayerPatchDto? patch,
        DaoxuanPlayerSnapshotDto? snapshot,
        YuoSnapshotRecord? record,
        CancellationToken cancellationToken)
    {
        // 到这里领域状态已经成功修改。客户端断开时请求令牌可能被取消，不能因此丢失
        // 已扣除的库存、已撮合的成交或待领取资产；持久化队列本身负责背压，停服时由
        // DaoxuanPersistenceHostedService drain 已入队命令，市场运行时也会写最终账本。
        _ = cancellationToken;
        if (record is not null && !_marketRuntime.PersistsPlayerSnapshotAtomically)
        {
            await _persistenceQueue.EnqueueAsync(new SaveSnapshotCommand(record), CancellationToken.None);
        }

        await _marketRuntime.PersistUnderWriteLockAsync(
            _marketRuntime.PersistsPlayerSnapshotAtomically ? record : null,
            CancellationToken.None);
        if (patch is null) return;

        byte[]? snapshotPacket = null;
        if (snapshot is not null && HasSessionNeedingResync(playerId))
        {
            snapshotPacket = DaoxuanPacketCodec.Encode(
                DaoxuanPacketKinds.Snapshot,
                string.Empty,
                NextPacketSequence(),
                snapshot);
        }

        PublishUpdate(new PlayerUpdate(
            playerId,
            DaoxuanPacketCodec.Encode(
                DaoxuanPacketKinds.Delta,
                string.Empty,
                NextPacketSequence(),
                patch),
            snapshotPacket,
            record));
    }

    public DaoxuanServerMetrics GetMetrics()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        return new DaoxuanServerMetrics(
            SimulationRevision,
            LoadedPlayerCount,
            ConnectedSessionCount,
            _persistenceQueue.GetStats(),
            process.TotalProcessorTime.TotalMilliseconds,
            GC.GetTotalMemory(forceFullCollection: false),
            process.WorkingSet64);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var session in _sessions.Values.SelectMany(group => group.Values))
        {
            session.Complete();
        }

        _runtime.Dispose();
        _ownerGate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private Task EnsurePlayerAsync(Guid playerId, string? name, CancellationToken cancellationToken)
    {
        return EnsurePlayerAsync(playerId, name, null, cancellationToken);
    }

    private async Task EnsurePlayerAsync(
        Guid playerId,
        string? name,
        string? account,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        _lastPlayerAccess[playerId] = now;
        if (_knownPlayers.ContainsKey(playerId)) return;
        var stored = await _snapshotStore.LoadAsync("player", playerId.ToString("D"), cancellationToken);
        var restored = stored is null ? null : DaoxuanStateSerializer.Deserialize(stored.Payload);
        YuoSnapshotRecord? migrationRecord = null;

        await ExecuteOnOwnerAsync(fiber =>
        {
            if (_players.ContainsKey(playerId)) return true;
            var state = restored ?? DaoxuanSimulation.CreateDefaultState(playerId, name, now, account);
            state.PlayerId = playerId;

            if (state.SequenceId <= 0)
            {
                state.SequenceId = Interlocked.Increment(ref _maxSequenceId);
                migrationRecord = ToSnapshotRecord(state);
            }
            else
            {
                var currentMax = Volatile.Read(ref _maxSequenceId);
                while (state.SequenceId > currentMax)
                {
                    var previous = Interlocked.CompareExchange(ref _maxSequenceId, state.SequenceId, currentMax);
                    if (previous == currentMax) break;
                    currentMax = previous;
                }
            }


            if (state.CreatedAt == default)
            {
                state.CreatedAt = now;
                migrationRecord = ToSnapshotRecord(state);
            }

            if (string.IsNullOrWhiteSpace(state.Account))
            {
                state.Account = string.IsNullOrWhiteSpace(account) ? state.Name : account.Trim();
                migrationRecord = ToSnapshotRecord(state);
            }

            if (restored is not null)
            {
                if (DaoxuanSimulation.NormalizeState(state))
                {
                    migrationRecord = ToSnapshotRecord(state);
                }
            }

            if (state.PresenceMode != DaoxuanPresenceMode.FullyOffline)
            {
                state.PresenceMode = DaoxuanPresenceMode.IdleOffline;
                DaoxuanSimulation.AdvanceTo(state, now);
            }

            CreatePlayerEntity(fiber, state);
            _knownPlayers[playerId] = 0;
            return true;
        }, cancellationToken);

        if (migrationRecord is not null)
        {
            await _persistenceQueue.EnqueueAsync(new SaveSnapshotCommand(migrationRecord), cancellationToken);
        }
    }

    private void ObserveSequenceId(long sequenceId)
    {
        if (sequenceId <= 0) return;
        var currentMax = Volatile.Read(ref _maxSequenceId);
        while (sequenceId > currentMax)
        {
            var previous = Interlocked.CompareExchange(ref _maxSequenceId, sequenceId, currentMax);
            if (previous == currentMax) break;
            currentMax = previous;
        }
    }

    private DaoxuanPlayerState GetState(Guid playerId)
    {
        if (_players.TryGetValue(playerId, out var entity))
        {
            _lastPlayerAccess[playerId] = _clock.UtcNow;
            return entity.GetComponent<DaoxuanPlayerComponent>().State;
        }

        if (!_coldSnapshots.TryGetValue(playerId, out var payload))
        {
            throw new KeyNotFoundException($"玩家不存在: {playerId:D}");
        }

        var state = DaoxuanStateSerializer.Deserialize(payload)
            ?? throw new InvalidDataException($"玩家冷存档无法解析: {playerId:D}");
        DaoxuanSimulation.NormalizeState(state);
        if (state.PresenceMode != DaoxuanPresenceMode.FullyOffline)
        {
            state.PresenceMode = DaoxuanPresenceMode.IdleOffline;
            DaoxuanSimulation.AdvanceTo(state, _clock.UtcNow);
        }

        RemoveColdSnapshot(playerId);
        CreatePlayerEntity(_fiber, state);
        _knownPlayers[playerId] = 0;
        _lastPlayerAccess[playerId] = _clock.UtcNow;
        return state;
    }

    private void CreatePlayerEntity(YuoFiber fiber, DaoxuanPlayerState state)
    {
        var entity = new YuoEntity(fiber.World, Interlocked.Increment(ref _entitySequence))
        {
            EntityName = $"player:{state.PlayerId:D}"
        };
        entity.AddComponent<DaoxuanPlayerComponent>().State = state;
        _players[state.PlayerId] = entity;
        Interlocked.Increment(ref _loadedPlayerCount);
    }

    private YuoSnapshotRecord? EvictPlayerToColdSnapshot(Guid playerId)
    {
        if (_onlinePlayers.Contains(playerId)
            || (_sessions.TryGetValue(playerId, out var sessions) && !sessions.IsEmpty))
        {
            return null;
        }

        if (!_players.Remove(playerId, out var entity))
        {
            return null;
        }

        var state = entity.GetComponent<DaoxuanPlayerComponent>().State;
        var record = ToSnapshotRecord(state);
        AddColdSnapshot(playerId, record.Payload.ToArray());
        _lastPlayerAccess.TryRemove(playerId, out _);
        entity.Dispose();
        Interlocked.Decrement(ref _loadedPlayerCount);
        return record;
    }

    private IReadOnlyList<YuoSnapshotRecord> EvictIdlePlayersToColdSnapshots(DateTimeOffset now)
    {
        if (_players.Count == 0) return [];

        var records = new List<YuoSnapshotRecord>();
        foreach (var playerId in _players.Keys.ToArray())
        {
            if (_onlinePlayers.Contains(playerId)
                || (_sessions.TryGetValue(playerId, out var sessions) && !sessions.IsEmpty)
                || !_lastPlayerAccess.TryGetValue(playerId, out var lastAccess)
                || now - lastAccess < ColdPlayerRetention)
            {
                continue;
            }

            if (!_players.Remove(playerId, out var entity)) continue;
            var state = entity.GetComponent<DaoxuanPlayerComponent>().State;
            var record = ToSnapshotRecord(state);
            AddColdSnapshot(playerId, record.Payload.ToArray());
            _lastPlayerAccess.TryRemove(playerId, out _);
            entity.Dispose();
            Interlocked.Decrement(ref _loadedPlayerCount);
            records.Add(record);
        }

        return records;
    }

    // 冷快照缓存的统一维护入口。GetState / EvictPlayerToColdSnapshot /
    // EvictIdlePlayersToColdSnapshots 全部经由 ExecuteOnOwnerAsync 在同一 owner Fiber 上
    // 串行执行，以下方法不存在并发访问，无需加锁。
    private void AddColdSnapshot(Guid playerId, byte[] payload)
    {
        if (_coldSnapshotLruNodes.TryGetValue(playerId, out var existing))
        {
            // 同一玩家重复转冷：替换载荷并刷新到最新端。
            _coldSnapshots[playerId] = payload;
            _coldSnapshotLru.Remove(existing);
            _coldSnapshotLru.AddLast(existing);
            return;
        }

        _coldSnapshots[playerId] = payload;
        _coldSnapshotLruNodes[playerId] = _coldSnapshotLru.AddLast(playerId);
        EvictOverflowColdSnapshots();
    }

    private void RemoveColdSnapshot(Guid playerId)
    {
        if (_coldSnapshots.Remove(playerId) && _coldSnapshotLruNodes.Remove(playerId, out var node))
        {
            _coldSnapshotLru.Remove(node);
        }
    }

    // 超限时从最旧端淘汰：只移除内存缓存，不落盘也不打错误日志；
    // 玩家重连时会经 EnsurePlayerAsync 从磁盘快照恢复并补算（见字段处安全性说明）。
    private void EvictOverflowColdSnapshots()
    {
        while (_coldSnapshots.Count > MaxColdSnapshotCount && _coldSnapshotLru.First is not null)
        {
            RemoveColdSnapshot(_coldSnapshotLru.First.Value);
        }
    }

    private DaoxuanCommandResult ApplyCommand(
        DaoxuanPlayerState state,
        string command,
        JsonElement payload,
        DateTimeOffset commandAt)
    {
        try
        {
            return command switch
            {
                "rename" => ApplyRename(state, payload.Deserialize<RenameRequest>(DaoxuanPacketCodec.JsonOptions), commandAt),
                "enqueue_action" => ApplyEnqueue(state, payload.Deserialize<EnqueueActionRequest>(DaoxuanPacketCodec.JsonOptions)),
                "start_combat" => ApplyStartCombat(state, payload.Deserialize<StartCombatRequest>(DaoxuanPacketCodec.JsonOptions)),
                "remove_action" => ApplyRemove(state, payload.Deserialize<RemoveActionRequest>(DaoxuanPacketCodec.JsonOptions)),
                "move_action" => ApplyMove(state, payload.Deserialize<MoveActionRequest>(DaoxuanPacketCodec.JsonOptions)),
                "travel_to" => ApplyTravel(state, payload.Deserialize<TravelToRequest>(DaoxuanPacketCodec.JsonOptions)),
                "set_auto_use" => ApplyAutoUse(state, payload.Deserialize<SetAutoUseRequest>(DaoxuanPacketCodec.JsonOptions)),
                "use_item" => ApplyUseItem(state, payload.Deserialize<UseItemRequest>(DaoxuanPacketCodec.JsonOptions)),
                "set_combat_loadout" => ApplyCombatLoadout(state, payload.Deserialize<SetCombatLoadoutRequest>(DaoxuanPacketCodec.JsonOptions)),
                "set_loop" => ApplyLoop(state, payload.Deserialize<SetLoopRequest>(DaoxuanPacketCodec.JsonOptions)),
                "breakthrough" => ApplyBreakthrough(state),
                _ => DaoxuanCommandResult.Fail("COMMAND_NOT_FOUND", "未知命令。")
            };
        }
        catch (JsonException)
        {
            return DaoxuanCommandResult.Fail("INVALID_PAYLOAD", "命令参数格式错误。");
        }
    }

    private DaoxuanCommandResult ApplyRename(
        DaoxuanPlayerState state,
        RenameRequest? request,
        DateTimeOffset commandAt)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return DaoxuanCommandResult.Fail("INVALID_NAME", "修士道号不能为空。");
        }

        var newName = request.Name.Trim();
        if (newName.Length is < 2 or > 12)
        {
            return DaoxuanCommandResult.Fail("INVALID_NAME_LENGTH", "修士道号长度必须在 2 到 12 个字符之间。");
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(newName, @"^[\u4e00-\u9fa5a-zA-Z0-9_]+$"))
        {
            return DaoxuanCommandResult.Fail("INVALID_NAME_CHARACTERS", "修士道号仅支持中文、英文字母、数字和下划线，不支持特殊字符。");
        }

        if (string.Equals(state.Name, newName, StringComparison.Ordinal))
        {
            return DaoxuanCommandResult.Fail("SAME_NAME", "新道号不能与当前道号相同。");
        }

        if (state.LastRenamedAt.HasValue && commandAt - state.LastRenamedAt.Value < TimeSpan.FromHours(24))
        {
            var remaining = TimeSpan.FromHours(24) - (commandAt - state.LastRenamedAt.Value);
            var hours = (int)remaining.TotalHours;
            var minutes = remaining.Minutes;
            return DaoxuanCommandResult.Fail("RENAME_COOLDOWN", $"改名冷却中，还需等待 {hours} 小时 {minutes} 分。");
        }

        if (_takenPlayerNames.TryGetValue(newName, out var ownerId) && ownerId != state.PlayerId)
        {
            return DaoxuanCommandResult.Fail("NAME_ALREADY_EXISTS", "该道号已被其他修士使用，请更换其他名称。");
        }

        _takenPlayerNames.TryRemove(state.Name, out _);
        _takenPlayerNames[newName] = state.PlayerId;

        var oldName = state.Name;
        state.Name = newName;
        state.LastRenamedAt = commandAt;
        state.DirtySections |= DaoxuanDirtySections.Stats;
        DaoxuanSimulation.AddEvent(state, $"修士道号已由【{oldName}】修改为【{newName}】。");
        return DaoxuanCommandResult.Ok($"道号已成功变更为【{newName}】。");
    }

    private static DaoxuanCommandResult ApplyEnqueue(DaoxuanPlayerState state, EnqueueActionRequest? request)
    {
        return request is null
            ? DaoxuanCommandResult.Fail("INVALID_PAYLOAD", "缺少行动参数。")
            : DaoxuanSimulation.Enqueue(state, request.ActionId, request.Repeats);
    }

    private static DaoxuanCommandResult ApplyStartCombat(DaoxuanPlayerState state, StartCombatRequest? request)
    {
        return request is null
            ? DaoxuanCommandResult.Fail("INVALID_PAYLOAD", "缺少历练参数。")
            : DaoxuanSimulation.StartCombat(state, request.ActionId, request.Repeats);
    }

    private static DaoxuanCommandResult ApplyRemove(DaoxuanPlayerState state, RemoveActionRequest? request)
    {
        return request is not null && Guid.TryParse(request.EntryId, out var entryId)
            ? DaoxuanSimulation.Remove(state, entryId)
            : DaoxuanCommandResult.Fail("INVALID_ENTRY_ID", "行动 ID 无效。");
    }

    private static DaoxuanCommandResult ApplyMove(DaoxuanPlayerState state, MoveActionRequest? request)
    {
        return request is not null && Guid.TryParse(request.EntryId, out var entryId)
            ? DaoxuanSimulation.Move(state, entryId, request.Direction)
            : DaoxuanCommandResult.Fail("INVALID_ENTRY_ID", "行动 ID 无效。");
    }

    private static DaoxuanCommandResult ApplyTravel(DaoxuanPlayerState state, TravelToRequest? request)
    {
        return request is null || string.IsNullOrWhiteSpace(request.DestinationNodeId)
            ? DaoxuanCommandResult.Fail("INVALID_DESTINATION", "目标地点无效。")
            : DaoxuanSimulation.TravelTo(state, request.DestinationNodeId);
    }

    private static DaoxuanCommandResult ApplyAutoUse(DaoxuanPlayerState state, SetAutoUseRequest? request)
    {
        if (request is null || !TryParseActionType(request.ActionType, out var actionType))
        {
            return DaoxuanCommandResult.Fail("INVALID_ACTION_TYPE", "行动类型无效。");
        }

        var triggers = ParseTriggers(request.Triggers, out var triggerError);
        if (!string.IsNullOrWhiteSpace(triggerError))
        {
            return DaoxuanCommandResult.Fail("INVALID_TRIGGERS", triggerError);
        }

        return DaoxuanSimulation.SetAutoUse(
            state,
            actionType,
            request.ItemId,
            request.Quantity,
            request.Enabled,
            request.SlotIndex,
            triggers.Count > 0 ? triggers : null);
    }

    private static DaoxuanCommandResult ApplyUseItem(DaoxuanPlayerState state, UseItemRequest? request)
    {
        return request is not null && TryParseActionType(request.ActionType, out var actionType)
            ? DaoxuanSimulation.UseItem(state, request.ItemId, actionType, request.Quantity)
            : DaoxuanCommandResult.Fail("INVALID_ACTION_TYPE", "行动类型无效。");
    }

    private static DaoxuanCommandResult ApplyLoop(DaoxuanPlayerState state, SetLoopRequest? request)
    {
        return request is null
            ? DaoxuanCommandResult.Fail("INVALID_PAYLOAD", "缺少循环参数。")
            : DaoxuanSimulation.SetLoop(state, request.Enabled);
    }

    private static DaoxuanCommandResult ApplyCombatLoadout(
        DaoxuanPlayerState state,
        SetCombatLoadoutRequest? request)
    {
        if (request is null)
        {
            return DaoxuanCommandResult.Fail("INVALID_PAYLOAD", "缺少战斗配置参数。");
        }

        DaoxuanCombatStyle? style = null;
        if (!string.IsNullOrWhiteSpace(request.Style))
        {
            if (!TryParseCombatStyle(request.Style, out var parsedStyle))
            {
                return DaoxuanCommandResult.Fail("INVALID_COMBAT_STYLE", "战斗流派无效。");
            }

            style = parsedStyle;
        }

        DaoxuanDamageType? damageType = null;
        if (!string.IsNullOrWhiteSpace(request.DamageType))
        {
            if (!TryParseDamageType(request.DamageType, out var parsedDamageType))
            {
                return DaoxuanCommandResult.Fail("INVALID_DAMAGE_TYPE", "伤害类型无效。");
            }

            damageType = parsedDamageType;
        }

        DaoxuanCombatAbilitySlot[]? abilities = null;
        if (request.AbilitySlots is not null)
        {
            var normalized = new List<DaoxuanCombatAbilitySlot>(request.AbilitySlots.Count);
            foreach (var slot in request.AbilitySlots)
            {
                var triggers = ParseTriggers(slot.Triggers, out var triggerError);
                if (!string.IsNullOrWhiteSpace(triggerError))
                {
                    return DaoxuanCommandResult.Fail("INVALID_ABILITY_TRIGGERS", triggerError);
                }

                normalized.Add(new DaoxuanCombatAbilitySlot
                {
                    AbilityId = slot.AbilityId,
                    Level = slot.Level,
                    Triggers = triggers
                });
            }

            abilities = normalized.ToArray();
        }

        DaoxuanConsumableSlot[]? consumables = null;
        if (request.ConsumableSlots is not null)
        {
            var normalized = new List<DaoxuanConsumableSlot>(request.ConsumableSlots.Count);
            foreach (var slot in request.ConsumableSlots)
            {
                var triggers = ParseTriggers(slot.Triggers, out var triggerError);
                if (!string.IsNullOrWhiteSpace(triggerError))
                {
                    return DaoxuanCommandResult.Fail("INVALID_CONSUMABLE_TRIGGERS", triggerError);
                }

                normalized.Add(new DaoxuanConsumableSlot
                {
                    ItemId = slot.ItemId,
                    Quantity = slot.Quantity,
                    Enabled = slot.Enabled,
                    Triggers = triggers
                });
            }

            consumables = normalized.ToArray();
        }

        DaoxuanEquippedItem[]? equipment = null;
        if (request.Equipment is not null)
        {
            equipment = request.Equipment
                .Select(item => new DaoxuanEquippedItem
                {
                    ItemId = item.ItemId,
                    EnhancementLevel = item.EnhancementLevel
                })
                .ToArray();
        }

        return DaoxuanSimulation.SetCombatLoadout(
            state,
            style,
            damageType,
            abilities,
            consumables,
            request.CombatDropRate,
            request.CombatDropQuantity,
            request.CombatRareFind,
            request.Tenacity,
            request.SearchSpeedRatio,
            equipment);
    }

    private static DaoxuanCommandResult ApplyBreakthrough(DaoxuanPlayerState state)
    {
        return DaoxuanSimulation.Breakthrough(state);
    }

    private static List<DaoxuanCombatTrigger> ParseTriggers(
        IReadOnlyList<DaoxuanCombatTriggerDto>? source,
        out string error)
    {
        error = string.Empty;
        if (source is null) return [];
        if (source.Count > DaoxuanCombatLimits.TriggerCountPerSlot)
        {
            error = "单个战斗槽位最多配置 4 条触发条件。";
            return [];
        }

        var result = new List<DaoxuanCombatTrigger>(source.Count);
        foreach (var dto in source)
        {
            if (!TryParseTrigger(dto, out var trigger))
            {
                error = "战斗触发条件无效。";
                return [];
            }

            result.Add(trigger);
        }

        return result;
    }

    private static bool TryParseTrigger(
        DaoxuanCombatTriggerDto dto,
        out DaoxuanCombatTrigger trigger)
    {
        trigger = new DaoxuanCombatTrigger();
        if (!TryParseTriggerDependency(dto.Dependency, out var dependency)
            || !TryParseTriggerCondition(dto.Condition, out var condition)
            || !TryParseTriggerComparator(dto.Comparator, out var comparator)
            || double.IsNaN(dto.Value)
            || double.IsInfinity(dto.Value))
        {
            return false;
        }

        trigger = new DaoxuanCombatTrigger
        {
            Dependency = dependency,
            Condition = condition,
            Comparator = comparator,
            Value = dto.Value,
            BuffId = string.IsNullOrWhiteSpace(dto.BuffId) ? null : dto.BuffId
        };
        return true;
    }

    private static bool TryParseTriggerDependency(
        string? value,
        out DaoxuanCombatTriggerDependency dependency)
    {
        dependency = value?.Trim().ToLowerInvariant() switch
        {
            "self" => DaoxuanCombatTriggerDependency.Self,
            "target-enemy" => DaoxuanCombatTriggerDependency.TargetEnemy,
            "ally-lowest-hp" => DaoxuanCombatTriggerDependency.AllyLowestHitpoints,
            "ally-highest-hp" => DaoxuanCombatTriggerDependency.AllyHighestHitpoints,
            "enemy-lowest-hp" => DaoxuanCombatTriggerDependency.EnemyLowestHitpoints,
            "enemy-highest-hp" => DaoxuanCombatTriggerDependency.EnemyHighestHitpoints,
            _ => (DaoxuanCombatTriggerDependency)(-1)
        };
        return Enum.IsDefined(dependency);
    }

    private static bool TryParseTriggerCondition(
        string? value,
        out DaoxuanCombatTriggerCondition condition)
    {
        condition = value?.Trim().ToLowerInvariant() switch
        {
            "current-hp" => DaoxuanCombatTriggerCondition.CurrentHitpoints,
            "current-mana" => DaoxuanCombatTriggerCondition.CurrentManapoints,
            "missing-hp" => DaoxuanCombatTriggerCondition.MissingHitpoints,
            "missing-mana" => DaoxuanCombatTriggerCondition.MissingManapoints,
            "buff-stacks" => DaoxuanCombatTriggerCondition.BuffStacks,
            "stun-status" => DaoxuanCombatTriggerCondition.StunStatus,
            "blind-status" => DaoxuanCombatTriggerCondition.BlindStatus,
            "silence-status" => DaoxuanCombatTriggerCondition.SilenceStatus,
            "current-hp-percentage" => DaoxuanCombatTriggerCondition.CurrentHitpointsPercentage,
            "missing-hp-percentage" => DaoxuanCombatTriggerCondition.MissingHitpointsPercentage,
            "current-mana-percentage" => DaoxuanCombatTriggerCondition.CurrentManapointsPercentage,
            "missing-mana-percentage" => DaoxuanCombatTriggerCondition.MissingManapointsPercentage,
            _ => (DaoxuanCombatTriggerCondition)(-1)
        };
        return Enum.IsDefined(condition);
    }

    private static bool TryParseTriggerComparator(
        string? value,
        out DaoxuanCombatTriggerComparator comparator)
    {
        comparator = value?.Trim().ToLowerInvariant() switch
        {
            "less-than-equal" => DaoxuanCombatTriggerComparator.LessThanOrEqual,
            "greater-than-equal" => DaoxuanCombatTriggerComparator.GreaterThanOrEqual,
            "less-than" => DaoxuanCombatTriggerComparator.LessThan,
            "greater-than" => DaoxuanCombatTriggerComparator.GreaterThan,
            "is-active" => DaoxuanCombatTriggerComparator.IsActive,
            "is-inactive" => DaoxuanCombatTriggerComparator.IsInactive,
            _ => (DaoxuanCombatTriggerComparator)(-1)
        };
        return Enum.IsDefined(comparator);
    }

    private static bool TryParseActionType(string value, out DaoxuanActionType type)
    {
        type = value switch
        {
            "cultivation" => DaoxuanActionType.Cultivation,
            "exploration" => DaoxuanActionType.Exploration,
            "alchemy" => DaoxuanActionType.Alchemy,
            "combat" => DaoxuanActionType.Combat,
            _ => (DaoxuanActionType)(-1)
        };
        return Enum.IsDefined(type);
    }

    private static bool TryParseMarketSide(string? value, out DaoxuanMarketSide side)
    {
        side = value?.Trim().ToLowerInvariant() switch
        {
            "buy" => DaoxuanMarketSide.Buy,
            "sell" => DaoxuanMarketSide.Sell,
            _ => (DaoxuanMarketSide)(-1)
        };
        return Enum.IsDefined(side);
    }

    private static bool TryParseCombatStyle(string value, out DaoxuanCombatStyle style)
    {
        style = value switch
        {
            "sword" => DaoxuanCombatStyle.Sword,
            "blade" => DaoxuanCombatStyle.Blade,
            "talisman" => DaoxuanCombatStyle.Talisman,
            "five-elements" => DaoxuanCombatStyle.FiveElements,
            "body" => DaoxuanCombatStyle.Body,
            _ => (DaoxuanCombatStyle)(-1)
        };
        return Enum.IsDefined(style);
    }

    private static bool TryParseDamageType(string value, out DaoxuanDamageType type)
    {
        type = value switch
        {
            "physical" => DaoxuanDamageType.Physical,
            "water" => DaoxuanDamageType.Water,
            "nature" => DaoxuanDamageType.Nature,
            "fire" => DaoxuanDamageType.Fire,
            _ => (DaoxuanDamageType)(-1)
        };
        return Enum.IsDefined(type);
    }

    private YuoSnapshotRecord ToSnapshotRecord(DaoxuanPlayerState state)
    {
        return new YuoSnapshotRecord(
            "player",
            state.PlayerId.ToString("D"),
            DaoxuanStateSerializer.Serialize(state),
            _clock.UtcNow);
    }

    private void PublishUpdate(PlayerUpdate update)
    {
        if (!_sessions.TryGetValue(update.PlayerId, out var sessions)) return;
        foreach (var session in sessions.Values)
        {
            if (session.NeedsResync)
            {
                if (update.SnapshotPacket is not null)
                {
                    session.TryEnqueueSnapshot(update.SnapshotPacket);
                }

                continue;
            }

            session.TryEnqueue(update.DeltaPacket);
        }
    }

    private bool HasSessionNeedingResync(Guid playerId)
    {
        return _sessions.TryGetValue(playerId, out var sessions)
            && sessions.Values.Any(session => session.NeedsResync);
    }

    private long NextPacketSequence() => Interlocked.Increment(ref _packetSequence);

    private async Task<T> ExecuteOnOwnerAsync<T>(
        Func<YuoFiber, T> action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _ownerGate.WaitAsync(cancellationToken);
        try
        {
            var call = _fiber.CallAsync(
                (fiber, _) => ValueTask.FromResult(action(fiber)),
                cancellationToken).AsTask();
            await _fiber.TickAsync(cancellationToken);
            return await call;
        }
        finally
        {
            _ownerGate.Release();
        }
    }

    private sealed record PlayerUpdate(
        Guid PlayerId,
        byte[] DeltaPacket,
        byte[]? SnapshotPacket,
        YuoSnapshotRecord? SnapshotRecord);
}

public sealed record DaoxuanCommandOutcome(
    DaoxuanCommandResult Result,
    DaoxuanPlayerPatchDto? Patch)
{
    public static DaoxuanCommandOutcome Failed(string code, string message) =>
        new(DaoxuanCommandResult.Fail(code, message), null);
}

public sealed record DaoxuanMarketCommandOutcome(
    bool Success,
    string Code,
    string Message,
    DaoxuanMarketOrderView? Order,
    IReadOnlyList<DaoxuanMarketFill> Fills,
    DaoxuanPlayerSnapshotDto? Snapshot)
{
    public DaoxuanCommandResult Result => new(Success, Code, Message);

    public static DaoxuanMarketCommandOutcome From(
        DaoxuanMarketPlacementResult result,
        DaoxuanPlayerSnapshotDto? snapshot) =>
        new(result.Success, result.Code, result.Message, result.Order, result.Fills, snapshot);

    public static DaoxuanMarketCommandOutcome From(
        DaoxuanMarketCancellationResult result,
        DaoxuanPlayerSnapshotDto? snapshot) =>
        new(result.Success, result.Code, result.Message, result.Order, [], snapshot);

    public static DaoxuanMarketCommandOutcome Failed(string code, string message) =>
        new(false, code, message, null, [], null);

    public static DaoxuanMarketCommandOutcome Ok(string message, DaoxuanPlayerSnapshotDto? snapshot) =>
        new(true, "OK", message, null, [], snapshot);
}

public sealed record DaoxuanAdvanceReport(long Revision, int UpdatedPlayers, double ElapsedMilliseconds);

internal readonly record struct ScheduledPlayerTransition(Guid PlayerId, long Generation);

public sealed record DaoxuanServerMetrics(
    long SimulationRevision,
    int LoadedPlayers,
    int ConnectedSessions,
    YuoPersistenceQueueStats Persistence,
    double ProcessCpuMilliseconds,
    long ManagedMemoryBytes,
    long WorkingSetBytes);
