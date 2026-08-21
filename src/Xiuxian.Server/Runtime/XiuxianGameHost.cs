using System.Collections.Concurrent;
using System.Text.Json;
using Xiuxian.Domain.Market;
using Xiuxian.Domain.Models;
using Xiuxian.Domain.Simulation;
using Xiuxian.Protocol;
using Xiuxian.Server.Infrastructure;
using YuoCore.Ecs;
using YuoCore.Persistence;
using YuoCore.Runtime;

namespace Xiuxian.Server.Runtime;

public sealed class XiuxianGameHost : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan ColdPlayerRetention = TimeSpan.FromSeconds(30);
    private readonly IYuoSnapshotStore _snapshotStore;
    private readonly IXiuxianPersistenceQueue _persistenceQueue;
    private readonly XiuxianMarketRuntime _marketRuntime;
    private readonly XiuxianSimulationScheduleSignal _scheduleSignal;
    private readonly XiuxianSimulationClock _clock;
    private readonly YuoCoreRuntime _runtime = new();
    private readonly YuoFiber _fiber;
    private readonly SemaphoreSlim _ownerGate = new(1, 1);
    private readonly Dictionary<Guid, YuoEntity> _players = [];
    // 冷玩家仍保留最新序列化快照，避免断开连接后再次请求必须阻塞 Fiber 做磁盘读取。
    // 容量上限依据：平均每份快照数 KB，2 万份对应数十 MB 内存量级，可长期驻留而不失控。
    // 安全性：冷快照只是内存缓存，每次写入 _coldSnapshots 的同时都会 EnqueueAsync(SaveSnapshotCommand)
    // 异步落盘（断线路径与 EvictIdlePlayersAsync 均如此）；被 LRU 淘汰的玩家重连时经 EnsurePlayerAsync
    // 从磁盘快照加载，并由确定性模拟 XiuxianSimulation.AdvanceTo(state, now) 补算到当前时刻，
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
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, XiuxianSession>> _sessions = new();
    private readonly ConcurrentDictionary<string, Guid> _takenPlayerNames = new(StringComparer.OrdinalIgnoreCase);
    private long _maxSequenceId;
    private long _entitySequence = 10_000;
    private long _packetSequence;
    private long _simulationRevision;
    private long _transitionGeneration;
    private int _loadedPlayerCount;
    private bool _disposed;

    public XiuxianGameHost(
        IYuoSnapshotStore snapshotStore,
        IXiuxianPersistenceQueue persistenceQueue,
        XiuxianMarketRuntime marketRuntime,
        XiuxianSimulationScheduleSignal scheduleSignal,
        XiuxianSimulationClock clock)
    {
        _snapshotStore = snapshotStore;
        _persistenceQueue = persistenceQueue;
        _marketRuntime = marketRuntime;
        _scheduleSignal = scheduleSignal;
        _clock = clock;
        _fiber = _runtime.Fibers.CreateFiber(new YuoFiberOptions
        {
            Id = 1,
            Name = "Xiuxian-PlayerShard-0",
            SetWorldAsDefault = false,
            MaxMailboxMessagesPerTick = 100_000
        });
    }

    public long SimulationRevision => Volatile.Read(ref _simulationRevision);
    public int LoadedPlayerCount => Volatile.Read(ref _loadedPlayerCount);
    public int ConnectedSessionCount => _sessions.Values.Sum(group => group.Count);

    public Task<XiuxianSession> RegisterSessionAsync(
        Guid playerId,
        string? name,
        CancellationToken cancellationToken)
    {
        return RegisterSessionAsync(playerId, name, null, cancellationToken);
    }

    public async Task<XiuxianSession> RegisterSessionAsync(
        Guid playerId,
        string? name,
        string? account,
        CancellationToken cancellationToken)
    {
        await EnsurePlayerAsync(playerId, name, account, cancellationToken);
        var session = new XiuxianSession(playerId);
        var snapshot = await ExecuteOnOwnerAsync(_ =>
        {
            var state = GetState(playerId);
            XiuxianSimulation.SetPresence(state, XiuxianPresenceMode.Online, _clock.UtcNow);
            _onlinePlayers.Add(playerId);
            ScheduleNextTransition(state);
            state.DirtySections = XiuxianDirtySections.None;
            var result = XiuxianStateMapper.ToSnapshot(state, _clock.UtcNow);
            state.PendingEvents.Clear();
            return result;
        }, cancellationToken);

        _sessions.GetOrAdd(playerId, _ => new ConcurrentDictionary<Guid, XiuxianSession>())[session.SessionId] = session;
        session.TryEnqueueSnapshot(XiuxianPacketCodec.Encode(
            XiuxianPacketKinds.Snapshot,
            string.Empty,
            NextPacketSequence(),
            snapshot));
        _scheduleSignal.Pulse();
        return session;
    }

    public async Task UnregisterSessionAsync(XiuxianSession session, CancellationToken cancellationToken)
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
                    if (state.PresenceMode == XiuxianPresenceMode.Online)
                    {
                        XiuxianSimulation.SetPresence(state, XiuxianPresenceMode.IdleOffline, _clock.UtcNow);
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

    public async Task<XiuxianCommandOutcome> HandleCommandAsync(
        Guid playerId,
        XiuxianIncomingPacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Kind != XiuxianPacketKinds.Command)
        {
            return XiuxianCommandOutcome.Failed("INVALID_PACKET_KIND", "这里只接受 command 数据包。");
        }

        if (!packet.Payload.TryGetProperty("command", out var commandElement)
            || commandElement.GetString() is not { Length: > 0 } command
            || !packet.Payload.TryGetProperty("payload", out var payload))
        {
            return XiuxianCommandOutcome.Failed("INVALID_COMMAND", "命令包缺少 command 或 payload。");
        }

        await EnsurePlayerAsync(playerId, null, cancellationToken);
        if (command is "market_place" or "market_cancel" or "market_claim")
        {
            return await HandleMarketCommandAsync(playerId, command, payload, cancellationToken);
        }

        XiuxianPlayerPatchDto? patch = null;
        byte[]? snapshotPacket = null;
        YuoSnapshotRecord? record = null;
        var commandAt = _clock.UtcNow;
        var result = await ExecuteOnOwnerAsync(_ =>
        {
            var state = GetState(playerId);
            XiuxianSimulation.AdvanceTo(state, commandAt);
            var commandResult = ApplyCommand(state, command, payload, commandAt);
            if (commandResult.Success)
            {
                XiuxianSimulation.AdvanceTo(state, commandAt);
                if (state.PresenceMode == XiuxianPresenceMode.Online)
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
            if (sections != XiuxianDirtySections.None)
            {
                patch = XiuxianStateMapper.ToPatch(state, sections, commandAt);
                if (HasSessionNeedingResync(playerId))
                {
                    snapshotPacket = XiuxianPacketCodec.Encode(
                        XiuxianPacketKinds.Snapshot,
                        string.Empty,
                        NextPacketSequence(),
                        XiuxianStateMapper.ToSnapshot(state, commandAt));
                }

                record = ToSnapshotRecord(state);
                state.PendingEvents.Clear();
                state.DirtySections = XiuxianDirtySections.None;
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
                XiuxianPacketCodec.Encode(XiuxianPacketKinds.Delta, string.Empty, NextPacketSequence(), patch),
                snapshotPacket,
                record));
        }

        _scheduleSignal.Pulse();
        return new XiuxianCommandOutcome(result, patch);
    }

    private async Task<XiuxianCommandOutcome> HandleMarketCommandAsync(
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
                    var request = payload.Deserialize<XiuxianMarketPlaceRequest>(XiuxianPacketCodec.JsonOptions);
                    if (request is null || !TryParseMarketSide(request.Side, out var side))
                    {
                        return XiuxianCommandOutcome.Failed("INVALID_MARKET_ORDER", "市场订单参数无效。");
                    }

                    var outcome = await PlaceMarketOrderAsync(
                        playerId,
                        new XiuxianMarketOrderRequest(
                            request.OrderId,
                            playerId,
                            new XiuxianMarketInstrument(request.ItemId, request.EnhancementLevel),
                            side,
                            request.Price,
                            request.Quantity),
                        cancellationToken);
                    return new XiuxianCommandOutcome(outcome.Result, null);
                }
                case "market_cancel":
                {
                    var request = payload.Deserialize<XiuxianMarketCancelRequest>(XiuxianPacketCodec.JsonOptions);
                    if (request is null)
                    {
                        return XiuxianCommandOutcome.Failed("INVALID_ORDER_ID", "缺少订单 ID。");
                    }

                    var outcome = await CancelMarketOrderAsync(playerId, request.OrderId, cancellationToken);
                    return new XiuxianCommandOutcome(outcome.Result, null);
                }
                case "market_claim":
                {
                    var outcome = await ClaimMarketAssetsAsync(playerId, cancellationToken);
                    return new XiuxianCommandOutcome(outcome.Result, null);
                }
                default:
                    return XiuxianCommandOutcome.Failed("COMMAND_NOT_FOUND", "未知市场命令。");
            }
        }
        catch (JsonException)
        {
            return XiuxianCommandOutcome.Failed("INVALID_PAYLOAD", "市场命令参数格式错误。");
        }
    }

    public async Task<XiuxianPlayerSnapshotDto> GetSnapshotAsync(
        Guid playerId,
        CancellationToken cancellationToken)
    {
        await EnsurePlayerAsync(playerId, null, cancellationToken);
        return await ExecuteOnOwnerAsync(
            _ => XiuxianStateMapper.ToSnapshot(GetState(playerId), _clock.UtcNow),
            cancellationToken);
    }

    public async Task<XiuxianMarketCommandOutcome> PlaceMarketOrderAsync(
        Guid playerId,
        XiuxianMarketOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!_marketRuntime.IsReady)
        {
            return XiuxianMarketCommandOutcome.Failed("MARKET_NOT_READY", "市场账本尚未完成恢复。");
        }

        if (request is null || request.OwnerId != playerId)
        {
            return XiuxianMarketCommandOutcome.Failed("ORDER_FORBIDDEN", "订单所有者必须是当前玩家。");
        }

        if (!_marketRuntime.Owns(request.Instrument))
        {
            return XiuxianMarketCommandOutcome.Failed(
                "MARKET_WRONG_PARTITION",
                $"该市场品种由主分片 {_marketRuntime.GetOwnerPartition(request.Instrument)} 负责。");
        }

        await EnsurePlayerAsync(playerId, null, cancellationToken);
        using var writeLease = await _marketRuntime.AcquireWriteAsync(cancellationToken);
        XiuxianPlayerPatchDto? patch = null;
        XiuxianPlayerSnapshotDto? snapshot = null;
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

        if (!result.Success) return XiuxianMarketCommandOutcome.From(result, snapshot);
        await PersistPlayerAndMarketAsync(playerId, patch, snapshot, record, cancellationToken);
        _scheduleSignal.Pulse();
        return XiuxianMarketCommandOutcome.From(result, snapshot);
    }

    public async Task<XiuxianMarketCommandOutcome> CancelMarketOrderAsync(
        Guid playerId,
        string orderId,
        CancellationToken cancellationToken)
    {
        if (!_marketRuntime.IsReady)
        {
            return XiuxianMarketCommandOutcome.Failed("MARKET_NOT_READY", "市场账本尚未完成恢复。");
        }

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return XiuxianMarketCommandOutcome.Failed("INVALID_ORDER_ID", "订单 ID 不能为空。");
        }

        await EnsurePlayerAsync(playerId, null, cancellationToken);
        using var writeLease = await _marketRuntime.AcquireWriteAsync(cancellationToken);
        XiuxianPlayerPatchDto? patch = null;
        XiuxianPlayerSnapshotDto? snapshot = null;
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

        if (!result.Success) return XiuxianMarketCommandOutcome.From(result, snapshot);
        await PersistPlayerAndMarketAsync(playerId, patch, snapshot, record, cancellationToken);
        _scheduleSignal.Pulse();
        return XiuxianMarketCommandOutcome.From(result, snapshot);
    }

    public async Task<XiuxianMarketCommandOutcome> ClaimMarketAssetsAsync(
        Guid playerId,
        CancellationToken cancellationToken)
    {
        if (!_marketRuntime.IsReady)
        {
            return XiuxianMarketCommandOutcome.Failed("MARKET_NOT_READY", "市场账本尚未完成恢复。");
        }

        await EnsurePlayerAsync(playerId, null, cancellationToken);
        using var writeLease = await _marketRuntime.AcquireWriteAsync(cancellationToken);
        XiuxianPlayerPatchDto? patch = null;
        XiuxianPlayerSnapshotDto? snapshot = null;
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
            return XiuxianMarketCommandOutcome.Failed("NOTHING_TO_CLAIM", "当前没有待领取的市场资产。");
        }

        await PersistPlayerAndMarketAsync(playerId, patch, snapshot, record, cancellationToken);
        _scheduleSignal.Pulse();
        return XiuxianMarketCommandOutcome.Ok("市场资产已领取。", snapshot);
    }

    public async Task<XiuxianPlayerSnapshotDto> FastForwardAsync(
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
            if (state.PresenceMode == XiuxianPresenceMode.FullyOffline)
            {
                XiuxianSimulation.SetPresence(state, XiuxianPresenceMode.IdleOffline, state.LastSimulatedAt);
            }

            XiuxianSimulation.AdvanceTo(state, state.LastSimulatedAt.Add(duration));
            ScheduleNextTransition(state);
            record = ToSnapshotRecord(state);
            state.DirtySections = XiuxianDirtySections.None;
            return XiuxianStateMapper.ToSnapshot(state, state.LastSimulatedAt);
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

    public async Task<XiuxianAdvanceReport> AdvanceDueOnlinePlayersAsync(
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
                var state = entity.GetComponent<XiuxianPlayerComponent>().State;

                var simulation = XiuxianSimulation.AdvanceTo(state, now);
                var sections = state.DirtySections | simulation.DirtySections;
                ScheduleNextTransition(state);
                if (sections == XiuxianDirtySections.None) continue;

                var patch = XiuxianStateMapper.ToPatch(state, sections, now);
                byte[]? snapshotPacket = null;
                if (HasSessionNeedingResync(state.PlayerId))
                {
                    snapshotPacket = XiuxianPacketCodec.Encode(
                        XiuxianPacketKinds.Snapshot,
                        string.Empty,
                        NextPacketSequence(),
                        XiuxianStateMapper.ToSnapshot(state, now));
                }

                results.Add(new PlayerUpdate(
                    state.PlayerId,
                    XiuxianPacketCodec.Encode(XiuxianPacketKinds.Delta, string.Empty, NextPacketSequence(), patch),
                    snapshotPacket,
                    simulation.CompletedActions > 0 ? ToSnapshotRecord(state) : null));
                state.PendingEvents.Clear();
                state.DirtySections = XiuxianDirtySections.None;
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
        return new XiuxianAdvanceReport(SimulationRevision, updates.Count, elapsed.TotalMilliseconds);
    }

    public async Task CheckpointOnlinePlayersAsync(CancellationToken cancellationToken)
    {
        var records = await ExecuteOnOwnerAsync(_ => _onlinePlayers
            .Where(_players.ContainsKey)
            .Select(playerId => ToSnapshotRecord(
                _players[playerId].GetComponent<XiuxianPlayerComponent>().State))
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

    private void ScheduleNextTransition(XiuxianPlayerState state)
    {
        if (state.PresenceMode != XiuxianPresenceMode.Online
            || !_onlinePlayers.Contains(state.PlayerId))
        {
            InvalidateScheduledTransition(state.PlayerId);
            return;
        }

        var generation = ++_transitionGeneration;
        _onlineTransitionGenerations[state.PlayerId] = generation;
        var deadline = XiuxianSimulation.GetNextTransitionAt(state);
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
            var state = entity.GetComponent<XiuxianPlayerComponent>().State;
            var generation = ++_transitionGeneration;
            _onlineTransitionGenerations[playerId] = generation;
            var deadline = XiuxianSimulation.GetNextTransitionAt(state);
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
            .Select(entity => ToSnapshotRecord(entity.GetComponent<XiuxianPlayerComponent>().State))
            .ToArray(), cancellationToken);
        await _persistenceQueue.EnqueueBatchAsync(records.Select(record => (YuoPersistenceCommand)new SaveSnapshotCommand(record)).ToArray(), cancellationToken);
    }

    private void CapturePlayerMutation(
        XiuxianPlayerState state,
        out XiuxianPlayerPatchDto? patch,
        out XiuxianPlayerSnapshotDto? snapshot,
        out YuoSnapshotRecord? record)
    {
        var sections = state.DirtySections;
        if (sections == XiuxianDirtySections.None)
        {
            patch = null;
            snapshot = null;
            record = null;
            return;
        }

        var now = _clock.UtcNow;
        patch = XiuxianStateMapper.ToPatch(state, sections, now);
        snapshot = XiuxianStateMapper.ToSnapshot(state, now);
        record = ToSnapshotRecord(state);
        state.PendingEvents.Clear();
        state.DirtySections = XiuxianDirtySections.None;
    }

    private async Task PersistPlayerAndMarketAsync(
        Guid playerId,
        XiuxianPlayerPatchDto? patch,
        XiuxianPlayerSnapshotDto? snapshot,
        YuoSnapshotRecord? record,
        CancellationToken cancellationToken)
    {
        // 到这里领域状态已经成功修改。客户端断开时请求令牌可能被取消，不能因此丢失
        // 已扣除的库存、已撮合的成交或待领取资产；持久化队列本身负责背压，停服时由
        // XiuxianPersistenceHostedService drain 已入队命令，市场运行时也会写最终账本。
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
            snapshotPacket = XiuxianPacketCodec.Encode(
                XiuxianPacketKinds.Snapshot,
                string.Empty,
                NextPacketSequence(),
                snapshot);
        }

        PublishUpdate(new PlayerUpdate(
            playerId,
            XiuxianPacketCodec.Encode(
                XiuxianPacketKinds.Delta,
                string.Empty,
                NextPacketSequence(),
                patch),
            snapshotPacket,
            record));
    }

    public XiuxianServerMetrics GetMetrics()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        return new XiuxianServerMetrics(
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
        var restored = stored is null ? null : XiuxianStateSerializer.Deserialize(stored.Payload);
        YuoSnapshotRecord? migrationRecord = null;

        await ExecuteOnOwnerAsync(fiber =>
        {
            if (_players.ContainsKey(playerId)) return true;
            var state = restored ?? XiuxianSimulation.CreateDefaultState(playerId, name, now, account);
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

            _takenPlayerNames[state.Name] = playerId;

            if (restored is not null)
            {
                XiuxianSimulation.NormalizeState(state);
            }

            if (state.PresenceMode != XiuxianPresenceMode.FullyOffline)
            {
                state.PresenceMode = XiuxianPresenceMode.IdleOffline;
                XiuxianSimulation.AdvanceTo(state, now);
            }

            if (restored is not null && migrationRecord is null)
            {
                // 旧快照可能需要补齐战斗槽位或队列字段，恢复时立即排队保存迁移结果。
                migrationRecord = ToSnapshotRecord(state);
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

    private XiuxianPlayerState GetState(Guid playerId)
    {
        if (_players.TryGetValue(playerId, out var entity))
        {
            _lastPlayerAccess[playerId] = _clock.UtcNow;
            return entity.GetComponent<XiuxianPlayerComponent>().State;
        }

        if (!_coldSnapshots.TryGetValue(playerId, out var payload))
        {
            throw new KeyNotFoundException($"玩家不存在: {playerId:D}");
        }

        var state = XiuxianStateSerializer.Deserialize(payload)
            ?? throw new InvalidDataException($"玩家冷存档无法解析: {playerId:D}");
        XiuxianSimulation.NormalizeState(state);
        if (state.PresenceMode != XiuxianPresenceMode.FullyOffline)
        {
            state.PresenceMode = XiuxianPresenceMode.IdleOffline;
            XiuxianSimulation.AdvanceTo(state, _clock.UtcNow);
        }

        RemoveColdSnapshot(playerId);
        CreatePlayerEntity(_fiber, state);
        _knownPlayers[playerId] = 0;
        _lastPlayerAccess[playerId] = _clock.UtcNow;
        return state;
    }

    private void CreatePlayerEntity(YuoFiber fiber, XiuxianPlayerState state)
    {
        var entity = new YuoEntity(fiber.World, Interlocked.Increment(ref _entitySequence))
        {
            EntityName = $"player:{state.PlayerId:D}"
        };
        entity.AddComponent<XiuxianPlayerComponent>().State = state;
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

        var state = entity.GetComponent<XiuxianPlayerComponent>().State;
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
            var state = entity.GetComponent<XiuxianPlayerComponent>().State;
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

    private XiuxianCommandResult ApplyCommand(
        XiuxianPlayerState state,
        string command,
        JsonElement payload,
        DateTimeOffset commandAt)
    {
        try
        {
            return command switch
            {
                "rename" => ApplyRename(state, payload.Deserialize<RenameRequest>(XiuxianPacketCodec.JsonOptions), commandAt),
                "enqueue_action" => ApplyEnqueue(state, payload.Deserialize<EnqueueActionRequest>(XiuxianPacketCodec.JsonOptions)),
                "start_combat" => ApplyStartCombat(state, payload.Deserialize<StartCombatRequest>(XiuxianPacketCodec.JsonOptions)),
                "remove_action" => ApplyRemove(state, payload.Deserialize<RemoveActionRequest>(XiuxianPacketCodec.JsonOptions)),
                "move_action" => ApplyMove(state, payload.Deserialize<MoveActionRequest>(XiuxianPacketCodec.JsonOptions)),
                "travel_to" => ApplyTravel(state, payload.Deserialize<TravelToRequest>(XiuxianPacketCodec.JsonOptions)),
                "set_auto_use" => ApplyAutoUse(state, payload.Deserialize<SetAutoUseRequest>(XiuxianPacketCodec.JsonOptions)),
                "use_item" => ApplyUseItem(state, payload.Deserialize<UseItemRequest>(XiuxianPacketCodec.JsonOptions)),
                "set_combat_loadout" => ApplyCombatLoadout(state, payload.Deserialize<SetCombatLoadoutRequest>(XiuxianPacketCodec.JsonOptions)),
                "set_loop" => ApplyLoop(state, payload.Deserialize<SetLoopRequest>(XiuxianPacketCodec.JsonOptions)),
                "set_presence" => ApplyPresence(state, payload.Deserialize<SetPresenceRequest>(XiuxianPacketCodec.JsonOptions), commandAt),
                "breakthrough" => ApplyBreakthrough(state),
                _ => XiuxianCommandResult.Fail("COMMAND_NOT_FOUND", "未知命令。")
            };
        }
        catch (JsonException)
        {
            return XiuxianCommandResult.Fail("INVALID_PAYLOAD", "命令参数格式错误。");
        }
    }

    private XiuxianCommandResult ApplyRename(
        XiuxianPlayerState state,
        RenameRequest? request,
        DateTimeOffset commandAt)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return XiuxianCommandResult.Fail("INVALID_NAME", "修士道号不能为空。");
        }

        var newName = request.Name.Trim();
        if (newName.Length is < 2 or > 12)
        {
            return XiuxianCommandResult.Fail("INVALID_NAME_LENGTH", "修士道号长度必须在 2 到 12 个字符之间。");
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(newName, @"^[\u4e00-\u9fa5a-zA-Z0-9_]+$"))
        {
            return XiuxianCommandResult.Fail("INVALID_NAME_CHARACTERS", "修士道号仅支持中文、英文字母、数字和下划线，不支持特殊字符。");
        }

        if (string.Equals(state.Name, newName, StringComparison.Ordinal))
        {
            return XiuxianCommandResult.Fail("SAME_NAME", "新道号不能与当前道号相同。");
        }

        if (state.LastRenamedAt.HasValue && commandAt - state.LastRenamedAt.Value < TimeSpan.FromHours(24))
        {
            var remaining = TimeSpan.FromHours(24) - (commandAt - state.LastRenamedAt.Value);
            var hours = (int)remaining.TotalHours;
            var minutes = remaining.Minutes;
            return XiuxianCommandResult.Fail("RENAME_COOLDOWN", $"改名冷却中，还需等待 {hours} 小时 {minutes} 分。");
        }

        if (_takenPlayerNames.TryGetValue(newName, out var ownerId) && ownerId != state.PlayerId)
        {
            return XiuxianCommandResult.Fail("NAME_ALREADY_EXISTS", "该道号已被其他修士使用，请更换其他名称。");
        }

        _takenPlayerNames.TryRemove(state.Name, out _);
        _takenPlayerNames[newName] = state.PlayerId;

        var oldName = state.Name;
        state.Name = newName;
        state.LastRenamedAt = commandAt;
        state.DirtySections |= XiuxianDirtySections.Stats;
        XiuxianSimulation.AddEvent(state, $"修士道号已由【{oldName}】修改为【{newName}】。");
        return XiuxianCommandResult.Ok($"道号已成功变更为【{newName}】。");
    }

    private static XiuxianCommandResult ApplyEnqueue(XiuxianPlayerState state, EnqueueActionRequest? request)
    {
        return request is null
            ? XiuxianCommandResult.Fail("INVALID_PAYLOAD", "缺少行动参数。")
            : XiuxianSimulation.Enqueue(state, request.ActionId, request.Repeats);
    }

    private static XiuxianCommandResult ApplyStartCombat(XiuxianPlayerState state, StartCombatRequest? request)
    {
        return request is null
            ? XiuxianCommandResult.Fail("INVALID_PAYLOAD", "缺少历练参数。")
            : XiuxianSimulation.StartCombat(state, request.ActionId, request.Repeats);
    }

    private static XiuxianCommandResult ApplyRemove(XiuxianPlayerState state, RemoveActionRequest? request)
    {
        return request is not null && Guid.TryParse(request.EntryId, out var entryId)
            ? XiuxianSimulation.Remove(state, entryId)
            : XiuxianCommandResult.Fail("INVALID_ENTRY_ID", "行动 ID 无效。");
    }

    private static XiuxianCommandResult ApplyMove(XiuxianPlayerState state, MoveActionRequest? request)
    {
        return request is not null && Guid.TryParse(request.EntryId, out var entryId)
            ? XiuxianSimulation.Move(state, entryId, request.Direction)
            : XiuxianCommandResult.Fail("INVALID_ENTRY_ID", "行动 ID 无效。");
    }

    private static XiuxianCommandResult ApplyTravel(XiuxianPlayerState state, TravelToRequest? request)
    {
        return request is null || string.IsNullOrWhiteSpace(request.DestinationNodeId)
            ? XiuxianCommandResult.Fail("INVALID_DESTINATION", "目标地点无效。")
            : XiuxianSimulation.TravelTo(state, request.DestinationNodeId);
    }

    private static XiuxianCommandResult ApplyAutoUse(XiuxianPlayerState state, SetAutoUseRequest? request)
    {
        if (request is null || !TryParseActionType(request.ActionType, out var actionType))
        {
            return XiuxianCommandResult.Fail("INVALID_ACTION_TYPE", "行动类型无效。");
        }

        var triggers = ParseTriggers(request.Triggers, out var triggerError);
        if (!string.IsNullOrWhiteSpace(triggerError))
        {
            return XiuxianCommandResult.Fail("INVALID_TRIGGERS", triggerError);
        }

        return XiuxianSimulation.SetAutoUse(
            state,
            actionType,
            request.ItemId,
            request.Quantity,
            request.Enabled,
            request.SlotIndex,
            triggers.Count > 0 ? triggers : null);
    }

    private static XiuxianCommandResult ApplyUseItem(XiuxianPlayerState state, UseItemRequest? request)
    {
        return request is not null && TryParseActionType(request.ActionType, out var actionType)
            ? XiuxianSimulation.UseItem(state, request.ItemId, actionType, request.Quantity)
            : XiuxianCommandResult.Fail("INVALID_ACTION_TYPE", "行动类型无效。");
    }

    private static XiuxianCommandResult ApplyLoop(XiuxianPlayerState state, SetLoopRequest? request)
    {
        return request is null
            ? XiuxianCommandResult.Fail("INVALID_PAYLOAD", "缺少循环参数。")
            : XiuxianSimulation.SetLoop(state, request.Enabled);
    }

    private static XiuxianCommandResult ApplyCombatLoadout(
        XiuxianPlayerState state,
        SetCombatLoadoutRequest? request)
    {
        if (request is null)
        {
            return XiuxianCommandResult.Fail("INVALID_PAYLOAD", "缺少战斗配置参数。");
        }

        XiuxianCombatStyle? style = null;
        if (!string.IsNullOrWhiteSpace(request.Style))
        {
            if (!TryParseCombatStyle(request.Style, out var parsedStyle))
            {
                return XiuxianCommandResult.Fail("INVALID_COMBAT_STYLE", "战斗流派无效。");
            }

            style = parsedStyle;
        }

        XiuxianDamageType? damageType = null;
        if (!string.IsNullOrWhiteSpace(request.DamageType))
        {
            if (!TryParseDamageType(request.DamageType, out var parsedDamageType))
            {
                return XiuxianCommandResult.Fail("INVALID_DAMAGE_TYPE", "伤害类型无效。");
            }

            damageType = parsedDamageType;
        }

        XiuxianCombatAbilitySlot[]? abilities = null;
        if (request.AbilitySlots is not null)
        {
            var normalized = new List<XiuxianCombatAbilitySlot>(request.AbilitySlots.Count);
            foreach (var slot in request.AbilitySlots)
            {
                var triggers = ParseTriggers(slot.Triggers, out var triggerError);
                if (!string.IsNullOrWhiteSpace(triggerError))
                {
                    return XiuxianCommandResult.Fail("INVALID_ABILITY_TRIGGERS", triggerError);
                }

                normalized.Add(new XiuxianCombatAbilitySlot
                {
                    AbilityId = slot.AbilityId,
                    Level = slot.Level,
                    Triggers = triggers
                });
            }

            abilities = normalized.ToArray();
        }

        XiuxianConsumableSlot[]? consumables = null;
        if (request.ConsumableSlots is not null)
        {
            var normalized = new List<XiuxianConsumableSlot>(request.ConsumableSlots.Count);
            foreach (var slot in request.ConsumableSlots)
            {
                var triggers = ParseTriggers(slot.Triggers, out var triggerError);
                if (!string.IsNullOrWhiteSpace(triggerError))
                {
                    return XiuxianCommandResult.Fail("INVALID_CONSUMABLE_TRIGGERS", triggerError);
                }

                normalized.Add(new XiuxianConsumableSlot
                {
                    ItemId = slot.ItemId,
                    Quantity = slot.Quantity,
                    Enabled = slot.Enabled,
                    Triggers = triggers
                });
            }

            consumables = normalized.ToArray();
        }
        else if (request.FoodSlots is not null || request.DrinkSlots is not null)
        {
            // 旧版客户端：按先灵食后灵饮合并，空槽跳过，超过槽位上限的丢弃。
            var merged = new List<XiuxianConsumableSlot>(XiuxianCombatLimits.ConsumableSlotCount);
            foreach (var slot in (request.FoodSlots ?? []).Concat(request.DrinkSlots ?? []))
            {
                var triggers = ParseTriggers(slot.Triggers, out var triggerError);
                if (!string.IsNullOrWhiteSpace(triggerError))
                {
                    return XiuxianCommandResult.Fail("INVALID_CONSUMABLE_TRIGGERS", triggerError);
                }

                if (string.IsNullOrWhiteSpace(slot.ItemId)) continue;
                merged.Add(new XiuxianConsumableSlot
                {
                    ItemId = slot.ItemId,
                    Quantity = slot.Quantity,
                    Enabled = slot.Enabled,
                    Triggers = triggers
                });
                if (merged.Count >= XiuxianCombatLimits.ConsumableSlotCount) break;
            }

            consumables = merged.ToArray();
        }

        XiuxianEquippedItem[]? equipment = null;
        if (request.Equipment is not null)
        {
            equipment = request.Equipment
                .Select(item => new XiuxianEquippedItem
                {
                    ItemId = item.ItemId,
                    EnhancementLevel = item.EnhancementLevel
                })
                .ToArray();
        }

        return XiuxianSimulation.SetCombatLoadout(
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

    private static XiuxianCommandResult ApplyBreakthrough(XiuxianPlayerState state)
    {
        return XiuxianSimulation.Breakthrough(state);
    }

    private static List<XiuxianCombatTrigger> ParseTriggers(
        IReadOnlyList<XiuxianCombatTriggerDto>? source,
        out string error)
    {
        error = string.Empty;
        if (source is null) return [];
        if (source.Count > XiuxianCombatLimits.TriggerCountPerSlot)
        {
            error = "单个战斗槽位最多配置 4 条触发条件。";
            return [];
        }

        var result = new List<XiuxianCombatTrigger>(source.Count);
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
        XiuxianCombatTriggerDto dto,
        out XiuxianCombatTrigger trigger)
    {
        trigger = new XiuxianCombatTrigger();
        if (!TryParseTriggerDependency(dto.Dependency, out var dependency)
            || !TryParseTriggerCondition(dto.Condition, out var condition)
            || !TryParseTriggerComparator(dto.Comparator, out var comparator)
            || double.IsNaN(dto.Value)
            || double.IsInfinity(dto.Value))
        {
            return false;
        }

        trigger = new XiuxianCombatTrigger
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
        out XiuxianCombatTriggerDependency dependency)
    {
        dependency = value?.Trim().ToLowerInvariant() switch
        {
            "self" => XiuxianCombatTriggerDependency.Self,
            "target-enemy" => XiuxianCombatTriggerDependency.TargetEnemy,
            "ally-lowest-hp" => XiuxianCombatTriggerDependency.AllyLowestHitpoints,
            "ally-highest-hp" => XiuxianCombatTriggerDependency.AllyHighestHitpoints,
            "enemy-lowest-hp" => XiuxianCombatTriggerDependency.EnemyLowestHitpoints,
            "enemy-highest-hp" => XiuxianCombatTriggerDependency.EnemyHighestHitpoints,
            _ => (XiuxianCombatTriggerDependency)(-1)
        };
        return Enum.IsDefined(dependency);
    }

    private static bool TryParseTriggerCondition(
        string? value,
        out XiuxianCombatTriggerCondition condition)
    {
        condition = value?.Trim().ToLowerInvariant() switch
        {
            "current-hp" => XiuxianCombatTriggerCondition.CurrentHitpoints,
            "current-mana" => XiuxianCombatTriggerCondition.CurrentManapoints,
            "missing-hp" => XiuxianCombatTriggerCondition.MissingHitpoints,
            "missing-mana" => XiuxianCombatTriggerCondition.MissingManapoints,
            "buff-stacks" => XiuxianCombatTriggerCondition.BuffStacks,
            "stun-status" => XiuxianCombatTriggerCondition.StunStatus,
            "blind-status" => XiuxianCombatTriggerCondition.BlindStatus,
            "silence-status" => XiuxianCombatTriggerCondition.SilenceStatus,
            "current-hp-percentage" => XiuxianCombatTriggerCondition.CurrentHitpointsPercentage,
            "missing-hp-percentage" => XiuxianCombatTriggerCondition.MissingHitpointsPercentage,
            "current-mana-percentage" => XiuxianCombatTriggerCondition.CurrentManapointsPercentage,
            "missing-mana-percentage" => XiuxianCombatTriggerCondition.MissingManapointsPercentage,
            _ => (XiuxianCombatTriggerCondition)(-1)
        };
        return Enum.IsDefined(condition);
    }

    private static bool TryParseTriggerComparator(
        string? value,
        out XiuxianCombatTriggerComparator comparator)
    {
        comparator = value?.Trim().ToLowerInvariant() switch
        {
            "less-than-equal" => XiuxianCombatTriggerComparator.LessThanOrEqual,
            "greater-than-equal" => XiuxianCombatTriggerComparator.GreaterThanOrEqual,
            "less-than" => XiuxianCombatTriggerComparator.LessThan,
            "greater-than" => XiuxianCombatTriggerComparator.GreaterThan,
            "is-active" => XiuxianCombatTriggerComparator.IsActive,
            "is-inactive" => XiuxianCombatTriggerComparator.IsInactive,
            _ => (XiuxianCombatTriggerComparator)(-1)
        };
        return Enum.IsDefined(comparator);
    }

    private static XiuxianCommandResult ApplyPresence(
        XiuxianPlayerState state,
        SetPresenceRequest? request,
        DateTimeOffset commandAt)
    {
        return request is not null && TryParsePresence(request.Mode, out var mode)
            ? XiuxianSimulation.SetPresence(state, mode, commandAt)
            : XiuxianCommandResult.Fail("INVALID_PRESENCE", "在线状态无效。");
    }

    private static bool TryParseActionType(string value, out XiuxianActionType type)
    {
        type = value switch
        {
            "cultivation" => XiuxianActionType.Cultivation,
            "exploration" => XiuxianActionType.Exploration,
            "alchemy" => XiuxianActionType.Alchemy,
            "combat" => XiuxianActionType.Combat,
            _ => (XiuxianActionType)(-1)
        };
        return Enum.IsDefined(type);
    }

    private static bool TryParseMarketSide(string? value, out XiuxianMarketSide side)
    {
        side = value?.Trim().ToLowerInvariant() switch
        {
            "buy" => XiuxianMarketSide.Buy,
            "sell" => XiuxianMarketSide.Sell,
            _ => (XiuxianMarketSide)(-1)
        };
        return Enum.IsDefined(side);
    }

    private static bool TryParseCombatStyle(string value, out XiuxianCombatStyle style)
    {
        style = value switch
        {
            "sword" => XiuxianCombatStyle.Sword,
            "blade" => XiuxianCombatStyle.Blade,
            "talisman" => XiuxianCombatStyle.Talisman,
            "five-elements" => XiuxianCombatStyle.FiveElements,
            "body" => XiuxianCombatStyle.Body,
            _ => (XiuxianCombatStyle)(-1)
        };
        return Enum.IsDefined(style);
    }

    private static bool TryParseDamageType(string value, out XiuxianDamageType type)
    {
        type = value switch
        {
            "physical" => XiuxianDamageType.Physical,
            "water" => XiuxianDamageType.Water,
            "nature" => XiuxianDamageType.Nature,
            "fire" => XiuxianDamageType.Fire,
            _ => (XiuxianDamageType)(-1)
        };
        return Enum.IsDefined(type);
    }

    private static bool TryParsePresence(string value, out XiuxianPresenceMode mode)
    {
        mode = value switch
        {
            "online" => XiuxianPresenceMode.Online,
            "idle-offline" => XiuxianPresenceMode.IdleOffline,
            "fully-offline" => XiuxianPresenceMode.FullyOffline,
            _ => (XiuxianPresenceMode)(-1)
        };
        return Enum.IsDefined(mode);
    }

    private YuoSnapshotRecord ToSnapshotRecord(XiuxianPlayerState state)
    {
        return new YuoSnapshotRecord(
            "player",
            state.PlayerId.ToString("D"),
            XiuxianStateSerializer.Serialize(state),
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

public sealed record XiuxianCommandOutcome(
    XiuxianCommandResult Result,
    XiuxianPlayerPatchDto? Patch)
{
    public static XiuxianCommandOutcome Failed(string code, string message) =>
        new(XiuxianCommandResult.Fail(code, message), null);
}

public sealed record XiuxianMarketCommandOutcome(
    bool Success,
    string Code,
    string Message,
    XiuxianMarketOrderView? Order,
    IReadOnlyList<XiuxianMarketFill> Fills,
    XiuxianPlayerSnapshotDto? Snapshot)
{
    public XiuxianCommandResult Result => new(Success, Code, Message);

    public static XiuxianMarketCommandOutcome From(
        XiuxianMarketPlacementResult result,
        XiuxianPlayerSnapshotDto? snapshot) =>
        new(result.Success, result.Code, result.Message, result.Order, result.Fills, snapshot);

    public static XiuxianMarketCommandOutcome From(
        XiuxianMarketCancellationResult result,
        XiuxianPlayerSnapshotDto? snapshot) =>
        new(result.Success, result.Code, result.Message, result.Order, [], snapshot);

    public static XiuxianMarketCommandOutcome Failed(string code, string message) =>
        new(false, code, message, null, [], null);

    public static XiuxianMarketCommandOutcome Ok(string message, XiuxianPlayerSnapshotDto? snapshot) =>
        new(true, "OK", message, null, [], snapshot);
}

public sealed record XiuxianAdvanceReport(long Revision, int UpdatedPlayers, double ElapsedMilliseconds);

internal readonly record struct ScheduledPlayerTransition(Guid PlayerId, long Generation);

public sealed record XiuxianServerMetrics(
    long SimulationRevision,
    int LoadedPlayers,
    int ConnectedSessions,
    YuoPersistenceQueueStats Persistence,
    double ProcessCpuMilliseconds,
    long ManagedMemoryBytes,
    long WorkingSetBytes);
