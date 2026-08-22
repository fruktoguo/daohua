using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using DaoHua.Domain.Market;
using DaoHua.Domain.Models;
using DaoHua.Domain.Simulation;
using DaoHua.Protocol;
using DaoHua.Server.Infrastructure;
using YuoCore.Ecs;
using YuoCore.Persistence;
using YuoCore.Runtime;

namespace DaoHua.Server.Runtime;


/// <summary>
/// 玩家 Fiber 分片配置。未显式配置时按当前进程可见逻辑 CPU 数创建分片，使 2c/4c/更多核心默认参与玩家状态推进。
/// </summary>
public sealed class DaoHuaPlayerShardOptions
{
    public const int MaxShardCount = 256;

    public int ShardCount { get; init; } = ResolveDefaultShardCount();

    public static DaoHuaPlayerShardOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration.GetValue<int?>("Runtime:PlayerShardCount")
                         ?? configuration.GetValue<int?>("Server:PlayerShardCount")
                         ?? 0;
        return new DaoHuaPlayerShardOptions
        {
            ShardCount = configured > 0
                ? Math.Clamp(configured, 1, MaxShardCount)
                : ResolveDefaultShardCount()
        };
    }

    private static int ResolveDefaultShardCount() => Math.Clamp(Environment.ProcessorCount, 1, MaxShardCount);
}

internal sealed class DaoHuaSharedGameHostState
{
    public ConcurrentDictionary<string, Guid> TakenPlayerNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    private long _maxSequenceId;
    private long _packetSequence;
    private long _simulationRevision;

    public long SimulationRevision => Volatile.Read(ref _simulationRevision);

    public long GetNextSequenceId() => Interlocked.Increment(ref _maxSequenceId);

    public long NextPacketSequence() => Interlocked.Increment(ref _packetSequence);

    public long IncrementSimulationRevision() => Interlocked.Increment(ref _simulationRevision);

    public void ObserveSequenceId(long sequenceId)
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
}


/// <summary>
/// 多分片玩家运行时宿主。玩家按 GUID 稳定哈希路由到固定 PlayerShard；每个 shard 内部仍保持单 Fiber 串行，
/// shard 之间通过 Task.WhenAll 并行推进、检查点、冷淘汰和停服刷盘，默认按逻辑 CPU 数创建分片。
/// </summary>
public sealed class DaoHuaGameHost : IAsyncDisposable, IDisposable
{
    private readonly DaoHuaPlayerShardHost[] _shards;
    private readonly DaoHuaSharedGameHostState _sharedState = new();
    private readonly IDaoHuaPersistenceQueue _persistenceQueue;
    private bool _disposed;

    public DaoHuaGameHost(
        DaoHuaPlayerShardOptions shardOptions,
        IYuoSnapshotStore snapshotStore,
        IDaoHuaPersistenceQueue persistenceQueue,
        DaoHuaMarketRuntime marketRuntime,
        DaoHuaSimulationScheduleSignal scheduleSignal,
        DaoHuaSimulationClock clock)
    {
        ArgumentNullException.ThrowIfNull(shardOptions);
        ArgumentNullException.ThrowIfNull(snapshotStore);
        _persistenceQueue = persistenceQueue ?? throw new ArgumentNullException(nameof(persistenceQueue));
        var shardCount = Math.Clamp(shardOptions.ShardCount, 1, DaoHuaPlayerShardOptions.MaxShardCount);
        _shards = Enumerable.Range(0, shardCount)
            .Select(index => new DaoHuaPlayerShardHost(
                index,
                _sharedState,
                snapshotStore,
                persistenceQueue,
                marketRuntime,
                scheduleSignal,
                clock))
            .ToArray();
    }

    public int PlayerShardCount => _shards.Length;

    public long SimulationRevision => _sharedState.SimulationRevision;

    public int LoadedPlayerCount => _shards.Sum(shard => shard.LoadedPlayerCount);

    public int ConnectedSessionCount => _shards.Sum(shard => shard.ConnectedSessionCount);

    public bool IsPlayerNameTaken(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _sharedState.TakenPlayerNames.ContainsKey(name.Trim());
    }

    public long GetNextSequenceId() => _sharedState.GetNextSequenceId();

    public Task EnsureRegisteredPlayerSnapshotAsync(
        Guid playerId,
        string name,
        string account,
        long sequenceId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken) =>
        GetShard(playerId).EnsureRegisteredPlayerSnapshotAsync(
            playerId,
            name,
            account,
            sequenceId,
            createdAt,
            cancellationToken);

    public Task<bool> IsPlayerLocallyResidentAsync(Guid playerId, CancellationToken cancellationToken) =>
        GetShard(playerId).IsPlayerLocallyResidentAsync(playerId, cancellationToken);

    public Task<DaoHuaSession> RegisterSessionAsync(Guid playerId, string? name, CancellationToken cancellationToken) =>
        GetShard(playerId).RegisterSessionAsync(playerId, name, cancellationToken);

    public Task<DaoHuaSession> RegisterSessionAsync(Guid playerId, string? name, string? account, CancellationToken cancellationToken) =>
        GetShard(playerId).RegisterSessionAsync(playerId, name, account, cancellationToken);

    public Task UnregisterSessionAsync(DaoHuaSession session, CancellationToken cancellationToken) =>
        GetShard(session.PlayerId).UnregisterSessionAsync(session, cancellationToken);

    public Task<DaoHuaCommandOutcome> HandleCommandAsync(
        Guid playerId,
        DaoHuaIncomingPacket packet,
        CancellationToken cancellationToken) =>
        GetShard(playerId).HandleCommandAsync(playerId, packet, cancellationToken);

    public Task<DaoHuaPlayerSnapshotDto> GetSnapshotAsync(Guid playerId, CancellationToken cancellationToken) =>
        GetShard(playerId).GetSnapshotAsync(playerId, cancellationToken);

    public Task<DaoHuaMarketCommandOutcome> PlaceMarketOrderAsync(
        Guid playerId,
        DaoHuaMarketOrderRequest request,
        CancellationToken cancellationToken) =>
        GetShard(playerId).PlaceMarketOrderAsync(playerId, request, cancellationToken);

    public Task<DaoHuaMarketCommandOutcome> CancelMarketOrderAsync(
        Guid playerId,
        string orderId,
        CancellationToken cancellationToken) =>
        GetShard(playerId).CancelMarketOrderAsync(playerId, orderId, cancellationToken);

    public Task<DaoHuaMarketCommandOutcome> ClaimMarketAssetsAsync(Guid playerId, CancellationToken cancellationToken) =>
        GetShard(playerId).ClaimMarketAssetsAsync(playerId, cancellationToken);

    public Task<DaoHuaPlayerSnapshotDto> FastForwardAsync(
        Guid playerId,
        TimeSpan duration,
        CancellationToken cancellationToken) =>
        GetShard(playerId).FastForwardAsync(playerId, duration, cancellationToken);

    public async Task<DateTimeOffset?> GetNextOnlineTransitionAtAsync(CancellationToken cancellationToken)
    {
        var deadlines = await Task.WhenAll(_shards.Select(shard => shard.GetNextOnlineTransitionAtAsync(cancellationToken))).ConfigureAwait(false);
        DateTimeOffset? next = null;
        foreach (var deadline in deadlines)
        {
            if (deadline is null) continue;
            if (next is null || deadline < next) next = deadline;
        }

        return next;
    }

    public async Task<DaoHuaAdvanceReport> AdvanceDueOnlinePlayersAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var reports = await Task.WhenAll(_shards.Select(shard => shard.AdvanceDueOnlinePlayersAsync(now, cancellationToken))).ConfigureAwait(false);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
        return new DaoHuaAdvanceReport(
            SimulationRevision,
            reports.Sum(report => report.UpdatedPlayers),
            elapsed.TotalMilliseconds);
    }

    public async Task CheckpointOnlinePlayersAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_shards.Select(shard => shard.CheckpointOnlinePlayersAsync(cancellationToken))).ConfigureAwait(false);
    }

    public async Task EvictIdlePlayersAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await Task.WhenAll(_shards.Select(shard => shard.EvictIdlePlayersAsync(now, cancellationToken))).ConfigureAwait(false);
    }

    public async Task FlushAllAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_shards.Select(shard => shard.FlushAllAsync(cancellationToken))).ConfigureAwait(false);
    }

    public DaoHuaServerMetrics GetMetrics()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        return new DaoHuaServerMetrics(
            SimulationRevision,
            LoadedPlayerCount,
            ConnectedSessionCount,
            PlayerShardCount,
            Environment.ProcessorCount,
            _persistenceQueue.GetStats(),
            process.TotalProcessorTime.TotalMilliseconds,
            GC.GetTotalMemory(forceFullCollection: false),
            process.WorkingSet64);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var shard in _shards)
        {
            shard.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var shard in _shards)
        {
            await shard.DisposeAsync().ConfigureAwait(false);
        }
    }

    private DaoHuaPlayerShardHost GetShard(Guid playerId) => _shards[GetShardIndex(playerId, _shards.Length)];

    internal static int GetShardIndex(Guid playerId, int shardCount)
    {
        if (shardCount <= 0) throw new ArgumentOutOfRangeException(nameof(shardCount));
        Span<byte> bytes = stackalloc byte[16];
        playerId.TryWriteBytes(bytes);
        var hash = BinaryPrimitives.ReadUInt64LittleEndian(bytes)
                   ^ BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        return (int)(hash % (uint)shardCount);
    }
}

/// <summary>
/// 游戏运行时核心宿主 (DaoHuaGameHost)。
/// <para>
/// 职责：
/// 1. 管理所有在线与活跃玩家的 ECS 实体与状态生命周期，确保在专属 Fiber 上串行调度与确定性快进。
/// 2. 维护热实体与冷快照 LRU 缓存，平衡内存开销与读盘 I/O。
/// 3. 管理玩家网络会话 (DaoHuaSession)，广播增量补丁 (Delta) 与全量快照 (Snapshot)。
/// 4. 路由并执行玩家指令（历练、修炼、背包、突破、改名等）以及市场挂单/撤单/提取资产。
/// 5. 驱动在线玩家状态定时推进 (AdvanceDueOnlinePlayersAsync) 与持久化刷盘 (Checkpoint/Flush)。
/// </para>
/// </summary>
internal sealed class DaoHuaPlayerShardHost : IAsyncDisposable, IDisposable
{
    /// <summary>冷玩家在内存中保留的空闲时长阈值（超过后移入冷快照 LRU 缓存并卸载 ECS 实体）。</summary>
    private static readonly TimeSpan ColdPlayerRetention = TimeSpan.FromSeconds(30);

    private readonly IYuoSnapshotStore _snapshotStore;
    private readonly IDaoHuaPersistenceQueue _persistenceQueue;
    private readonly DaoHuaMarketRuntime _marketRuntime;
    private readonly DaoHuaSimulationScheduleSignal _scheduleSignal;
    private readonly DaoHuaSimulationClock _clock;
    private readonly DaoHuaSharedGameHostState _sharedState;
    private readonly int _shardIndex;
    private readonly YuoCoreRuntime _runtime = new();
    private readonly YuoFiber _fiber;
    private readonly SemaphoreSlim _ownerGate = new(1, 1);
    private readonly Dictionary<Guid, YuoEntity> _players = [];

    // 冷玩家仍保留最新序列化快照，避免断开连接后再次请求必须阻塞 Fiber 做磁盘读取。
    // 容量上限依据：平均每份快照数 KB，2 万份对应数十 MB 内存量级，可长期驻留而不失控。
    // 安全性：冷快照只是内存缓存，每次写入 _coldSnapshots 的同时都会 EnqueueAsync(SaveSnapshotCommand)
    // 异步落盘（断线路径与 EvictIdlePlayersAsync 均如此）；被 LRU 淘汰的玩家重连时经 EnsurePlayerAsync
    // 从磁盘快照加载，并由确定性模拟 DaoHuaSimulation.AdvanceTo(state, now) 补算到当前时刻，
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
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, DaoHuaSession>> _sessions = new();
    private readonly ConcurrentDictionary<string, Guid> _takenPlayerNames;
    private long _entitySequence = 10_000;
    private long _transitionGeneration;
    private int _loadedPlayerCount;
    private bool _disposed;

    /// <summary>
    /// 初始化游戏运行时宿主并创建专属调度 Fiber。
    /// </summary>
    public DaoHuaPlayerShardHost(
        int shardIndex,
        DaoHuaSharedGameHostState sharedState,
        IYuoSnapshotStore snapshotStore,
        IDaoHuaPersistenceQueue persistenceQueue,
        DaoHuaMarketRuntime marketRuntime,
        DaoHuaSimulationScheduleSignal scheduleSignal,
        DaoHuaSimulationClock clock)
    {
        if (shardIndex < 0) throw new ArgumentOutOfRangeException(nameof(shardIndex));
        _shardIndex = shardIndex;
        _sharedState = sharedState ?? throw new ArgumentNullException(nameof(sharedState));
        _takenPlayerNames = _sharedState.TakenPlayerNames;
        _snapshotStore = snapshotStore;
        _persistenceQueue = persistenceQueue;
        _marketRuntime = marketRuntime;
        _scheduleSignal = scheduleSignal;
        _clock = clock;
        _fiber = _runtime.Fibers.CreateFiber(new YuoFiberOptions
        {
            Id = shardIndex + 1,
            Name = $"DaoHua-PlayerShard-{shardIndex}",
            SetWorldAsDefault = false,
            MaxMailboxMessagesPerTick = 100_000
        });
    }

    /// <summary>模拟状态演进全局修订版本号。</summary>
    public long SimulationRevision => _sharedState.SimulationRevision;

    /// <summary>当前在内存中加载的 ECS 实体玩家总数。</summary>
    public int LoadedPlayerCount => Volatile.Read(ref _loadedPlayerCount);

    /// <summary>当前所有已连接的客户端 WebSocket 会话总数。</summary>
    public int ConnectedSessionCount => _sessions.Values.Sum(group => group.Count);

    /// <summary>
    /// 检查指定角色道号是否已被占用。
    /// </summary>
    public bool IsPlayerNameTaken(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _takenPlayerNames.ContainsKey(name.Trim());
    }

    /// <summary>
    /// 分配并获取下一个全局自增账号序列号。
    /// </summary>
    public long GetNextSequenceId()
    {
        return _sharedState.GetNextSequenceId();
    }

    /// <summary>
    /// 为新注册账号创建初始玩家状态快照并落盘。
    /// </summary>
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
        var state = DaoHuaSimulation.CreateDefaultState(playerId, name, timestamp, account);
        state.SequenceId = sequenceId;
        state.CreatedAt = timestamp;
        state.LastSimulatedAt = timestamp;
        state.PresenceMode = DaoHuaPresenceMode.IdleOffline;
        state.DirtySections = DaoHuaDirtySections.None;
        ObserveSequenceId(sequenceId);
        _takenPlayerNames[state.Name] = playerId;

        await _snapshotStore.SaveAsync(new YuoSnapshotRecord(
            "player",
            snapshotId,
            DaoHuaStateSerializer.Serialize(state),
            _clock.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 判断指定玩家当前是否常驻于本地内存（实体或冷快照）。
    /// </summary>
    public Task<bool> IsPlayerLocallyResidentAsync(Guid playerId, CancellationToken cancellationToken)
    {
        return ExecuteOnOwnerAsync(_ => _players.ContainsKey(playerId) || _coldSnapshots.ContainsKey(playerId), cancellationToken);
    }

    /// <summary>
    /// 注册客户端会话（默认无指定账号名）。
    /// </summary>
    public Task<DaoHuaSession> RegisterSessionAsync(
        Guid playerId,
        string? name,
        CancellationToken cancellationToken)
    {
        return RegisterSessionAsync(playerId, name, null, cancellationToken);
    }

    /// <summary>
    /// 注册客户端 WebSocket 会话：确保玩家状态加载、切换为在线状态、调度下次状态跃迁，并下发初始全量快照。
    /// </summary>
    public async Task<DaoHuaSession> RegisterSessionAsync(
        Guid playerId,
        string? name,
        string? account,
        CancellationToken cancellationToken)
    {
        await EnsurePlayerAsync(playerId, name, account, cancellationToken);
        var session = new DaoHuaSession(playerId);
        var snapshot = await ExecuteOnOwnerAsync(_ =>
        {
            var state = GetState(playerId);
            DaoHuaSimulation.SetPresence(state, DaoHuaPresenceMode.Online, _clock.UtcNow);
            _onlinePlayers.Add(playerId);
            ScheduleNextTransition(state);
            state.DirtySections = DaoHuaDirtySections.None;
            var result = DaoHuaStateMapper.ToSnapshot(state, _clock.UtcNow);
            state.PendingEvents.Clear();
            return result;
        }, cancellationToken);

        _sessions.GetOrAdd(playerId, _ => new ConcurrentDictionary<Guid, DaoHuaSession>())[session.SessionId] = session;
        session.TryEnqueueSnapshot(DaoHuaPacketCodec.Encode(
            DaoHuaPacketKinds.Snapshot,
            string.Empty,
            NextPacketSequence(),
            snapshot));
        _scheduleSignal.Pulse();
        return session;
    }

    /// <summary>
    /// 注销客户端会话：在没有其他活动连接时将玩家状态置为离线闲置，转为冷快照并提交持久化落盘。
    /// </summary>
    public async Task UnregisterSessionAsync(DaoHuaSession session, CancellationToken cancellationToken)
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
                    if (state.PresenceMode == DaoHuaPresenceMode.Online)
                    {
                        DaoHuaSimulation.SetPresence(state, DaoHuaPresenceMode.IdleOffline, _clock.UtcNow);
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

    /// <summary>
    /// 处理客户端发来的业务指令（历练、修炼、改名、战斗流派配置、市场交易等），执行确定性模拟并广播增量。
    /// </summary>
    public async Task<DaoHuaCommandOutcome> HandleCommandAsync(
        Guid playerId,
        DaoHuaIncomingPacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Kind != DaoHuaPacketKinds.Command)
        {
            return DaoHuaCommandOutcome.Failed("INVALID_PACKET_KIND", "这里只接受 command 数据包。");
        }

        if (!packet.Payload.TryGetProperty("command", out var commandElement)
            || commandElement.GetString() is not { Length: > 0 } command
            || !packet.Payload.TryGetProperty("payload", out var payload))
        {
            return DaoHuaCommandOutcome.Failed("INVALID_COMMAND", "命令包缺少 command 或 payload。");
        }

        await EnsurePlayerAsync(playerId, null, cancellationToken);
        if (command is "market_place" or "market_cancel" or "market_claim")
        {
            return await HandleMarketCommandAsync(playerId, command, payload, cancellationToken);
        }

        DaoHuaPlayerPatchDto? patch = null;
        byte[]? snapshotPacket = null;
        YuoSnapshotRecord? record = null;
        var commandAt = _clock.UtcNow;
        var result = await ExecuteOnOwnerAsync(_ =>
        {
            var state = GetState(playerId);
            DaoHuaSimulation.AdvanceTo(state, commandAt);
            var commandResult = ApplyCommand(state, command, payload, commandAt);
            if (commandResult.Success)
            {
                DaoHuaSimulation.AdvanceTo(state, commandAt);
                if (state.PresenceMode == DaoHuaPresenceMode.Online)
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
            if (sections != DaoHuaDirtySections.None)
            {
                patch = DaoHuaStateMapper.ToPatch(state, sections, commandAt);
                if (HasSessionNeedingResync(playerId))
                {
                    snapshotPacket = DaoHuaPacketCodec.Encode(
                        DaoHuaPacketKinds.Snapshot,
                        string.Empty,
                        NextPacketSequence(),
                        DaoHuaStateMapper.ToSnapshot(state, commandAt));
                }

                record = ToSnapshotRecord(state);
                state.PendingEvents.Clear();
                state.DirtySections = DaoHuaDirtySections.None;
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
                DaoHuaPacketCodec.Encode(DaoHuaPacketKinds.Delta, string.Empty, NextPacketSequence(), patch),
                snapshotPacket,
                record));
        }

        _scheduleSignal.Pulse();
        return new DaoHuaCommandOutcome(result, patch);
    }

    /// <summary>
    /// 处理来自 WebSocket 的市场挂单、撤单与提取资产指令。
    /// </summary>
    private async Task<DaoHuaCommandOutcome> HandleMarketCommandAsync(
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
                    var request = payload.Deserialize<DaoHuaMarketPlaceRequest>(DaoHuaPacketCodec.JsonOptions);
                    if (request is null || !TryParseMarketSide(request.Side, out var side))
                    {
                        return DaoHuaCommandOutcome.Failed("INVALID_MARKET_ORDER", "市场订单参数无效。");
                    }

                    var outcome = await PlaceMarketOrderAsync(
                        playerId,
                        new DaoHuaMarketOrderRequest(
                            request.OrderId,
                            playerId,
                            new DaoHuaMarketInstrument(request.ItemId, request.EnhancementLevel),
                            side,
                            request.Price,
                            request.Quantity),
                        cancellationToken);
                    return new DaoHuaCommandOutcome(outcome.Result, null);
                }
                case "market_cancel":
                {
                    var request = payload.Deserialize<DaoHuaMarketCancelRequest>(DaoHuaPacketCodec.JsonOptions);
                    if (request is null)
                    {
                        return DaoHuaCommandOutcome.Failed("INVALID_ORDER_ID", "缺少订单 ID。");
                    }

                    var outcome = await CancelMarketOrderAsync(playerId, request.OrderId, cancellationToken);
                    return new DaoHuaCommandOutcome(outcome.Result, null);
                }
                case "market_claim":
                {
                    var outcome = await ClaimMarketAssetsAsync(playerId, cancellationToken);
                    return new DaoHuaCommandOutcome(outcome.Result, null);
                }
                default:
                    return DaoHuaCommandOutcome.Failed("COMMAND_NOT_FOUND", "未知市场命令。");
            }
        }
        catch (JsonException)
        {
            return DaoHuaCommandOutcome.Failed("INVALID_PAYLOAD", "市场命令参数格式错误。");
        }
    }

    /// <summary>
    /// 获取指定玩家在当前时刻的完整状态快照 DTO。
    /// </summary>
    public async Task<DaoHuaPlayerSnapshotDto> GetSnapshotAsync(
        Guid playerId,
        CancellationToken cancellationToken)
    {
        await EnsurePlayerAsync(playerId, null, cancellationToken);
        return await ExecuteOnOwnerAsync(
            _ => DaoHuaStateMapper.ToSnapshot(GetState(playerId), _clock.UtcNow),
            cancellationToken);
    }

    /// <summary>
    /// 在市场引擎上提交限价订单：校验主分片权属、获取市场写租约、扣减玩家资产/物品并撮合交易，最后持久化玩家快照与市场账本。
    /// </summary>
    public async Task<DaoHuaMarketCommandOutcome> PlaceMarketOrderAsync(
        Guid playerId,
        DaoHuaMarketOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!_marketRuntime.IsReady)
        {
            return DaoHuaMarketCommandOutcome.Failed("MARKET_NOT_READY", "市场账本尚未完成恢复。");
        }

        if (request is null || request.OwnerId != playerId)
        {
            return DaoHuaMarketCommandOutcome.Failed("ORDER_FORBIDDEN", "订单所有者必须是当前玩家。");
        }

        if (!_marketRuntime.Owns(request.Instrument))
        {
            return DaoHuaMarketCommandOutcome.Failed(
                "MARKET_WRONG_PARTITION",
                $"该市场品种由主分片 {_marketRuntime.GetOwnerPartition(request.Instrument)} 负责。");
        }

        await EnsurePlayerAsync(playerId, null, cancellationToken);
        using var writeLease = await _marketRuntime.AcquireWriteAsync(cancellationToken);
        DaoHuaPlayerPatchDto? patch = null;
        DaoHuaPlayerSnapshotDto? snapshot = null;
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

        if (!result.Success) return DaoHuaMarketCommandOutcome.From(result, snapshot);
        await PersistPlayerAndMarketAsync(playerId, patch, snapshot, record, cancellationToken);
        _scheduleSignal.Pulse();
        return DaoHuaMarketCommandOutcome.From(result, snapshot);
    }

    /// <summary>
    /// 撤销玩家在市场上的挂单：解冻未成交的资产/物品并持久化账本。
    /// </summary>
    public async Task<DaoHuaMarketCommandOutcome> CancelMarketOrderAsync(
        Guid playerId,
        string orderId,
        CancellationToken cancellationToken)
    {
        if (!_marketRuntime.IsReady)
        {
            return DaoHuaMarketCommandOutcome.Failed("MARKET_NOT_READY", "市场账本尚未完成恢复。");
        }

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return DaoHuaMarketCommandOutcome.Failed("INVALID_ORDER_ID", "订单 ID 不能为空。");
        }

        await EnsurePlayerAsync(playerId, null, cancellationToken);
        using var writeLease = await _marketRuntime.AcquireWriteAsync(cancellationToken);
        DaoHuaPlayerPatchDto? patch = null;
        DaoHuaPlayerSnapshotDto? snapshot = null;
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

        if (!result.Success) return DaoHuaMarketCommandOutcome.From(result, snapshot);
        await PersistPlayerAndMarketAsync(playerId, patch, snapshot, record, cancellationToken);
        _scheduleSignal.Pulse();
        return DaoHuaMarketCommandOutcome.From(result, snapshot);
    }

    /// <summary>
    /// 提取玩家在市场已成交或撤单退回的暂存资金与物品，合并入背包与灵石余额。
    /// </summary>
    public async Task<DaoHuaMarketCommandOutcome> ClaimMarketAssetsAsync(
        Guid playerId,
        CancellationToken cancellationToken)
    {
        if (!_marketRuntime.IsReady)
        {
            return DaoHuaMarketCommandOutcome.Failed("MARKET_NOT_READY", "市场账本尚未完成恢复。");
        }

        await EnsurePlayerAsync(playerId, null, cancellationToken);
        using var writeLease = await _marketRuntime.AcquireWriteAsync(cancellationToken);
        DaoHuaPlayerPatchDto? patch = null;
        DaoHuaPlayerSnapshotDto? snapshot = null;
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
            return DaoHuaMarketCommandOutcome.Failed("NOTHING_TO_CLAIM", "当前没有待领取的市场资产。");
        }

        await PersistPlayerAndMarketAsync(playerId, patch, snapshot, record, cancellationToken);
        _scheduleSignal.Pulse();
        return DaoHuaMarketCommandOutcome.Ok("市场资产已领取。", snapshot);
    }

    /// <summary>
    /// 快进指定玩家的模拟时间（调试接口或离线补算用），执行确定性推演并保存快照。
    /// </summary>
    public async Task<DaoHuaPlayerSnapshotDto> FastForwardAsync(
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
            if (state.PresenceMode == DaoHuaPresenceMode.FullyOffline)
            {
                DaoHuaSimulation.SetPresence(state, DaoHuaPresenceMode.IdleOffline, state.LastSimulatedAt);
            }

            DaoHuaSimulation.AdvanceTo(state, state.LastSimulatedAt.Add(duration));
            ScheduleNextTransition(state);
            record = ToSnapshotRecord(state);
            state.DirtySections = DaoHuaDirtySections.None;
            return DaoHuaStateMapper.ToSnapshot(state, state.LastSimulatedAt);
        }, cancellationToken);

        if (record is not null)
        {
            await _persistenceQueue.EnqueueAsync(new SaveSnapshotCommand(record), cancellationToken);
        }

        _scheduleSignal.Pulse();
        return snapshot;
    }

    /// <summary>
    /// 获取当前所有在线玩家中最早需要发生状态跃迁（行动完成、战斗回合、恢复等）的时间点。
    /// </summary>
    public async Task<DateTimeOffset?> GetNextOnlineTransitionAtAsync(CancellationToken cancellationToken)
    {
        return await ExecuteOnOwnerAsync(_ =>
        {
            return PeekNextTransitionAt();
        }, cancellationToken);
    }

    /// <summary>
    /// 批量推进所有已到达跃迁截止时间的在线玩家状态，生成差异补丁 (Delta) 广播给客户端并异步入队快照落盘。
    /// </summary>
    public async Task<DaoHuaAdvanceReport> AdvanceDueOnlinePlayersAsync(
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
                var state = entity.GetComponent<DaoHuaPlayerComponent>().State;

                var simulation = DaoHuaSimulation.AdvanceTo(state, now);
                var sections = state.DirtySections | simulation.DirtySections;
                ScheduleNextTransition(state);
                if (sections == DaoHuaDirtySections.None) continue;

                var patch = DaoHuaStateMapper.ToPatch(state, sections, now);
                byte[]? snapshotPacket = null;
                if (HasSessionNeedingResync(state.PlayerId))
                {
                    snapshotPacket = DaoHuaPacketCodec.Encode(
                        DaoHuaPacketKinds.Snapshot,
                        string.Empty,
                        NextPacketSequence(),
                        DaoHuaStateMapper.ToSnapshot(state, now));
                }

                results.Add(new PlayerUpdate(
                    state.PlayerId,
                    DaoHuaPacketCodec.Encode(DaoHuaPacketKinds.Delta, string.Empty, NextPacketSequence(), patch),
                    snapshotPacket,
                    simulation.CompletedActions > 0 ? ToSnapshotRecord(state) : null));
                state.PendingEvents.Clear();
                state.DirtySections = DaoHuaDirtySections.None;
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
            _sharedState.IncrementSimulationRevision();
        }
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
        return new DaoHuaAdvanceReport(SimulationRevision, updates.Count, elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// 对当前所有在线玩家执行周期性检查点快照落盘。
    /// </summary>
    public async Task CheckpointOnlinePlayersAsync(CancellationToken cancellationToken)
    {
        var records = await ExecuteOnOwnerAsync(_ => _onlinePlayers
            .Where(_players.ContainsKey)
            .Select(playerId => ToSnapshotRecord(
                _players[playerId].GetComponent<DaoHuaPlayerComponent>().State))
            .ToArray(), cancellationToken);
        if (records.Length == 0) return;
        await _persistenceQueue.EnqueueBatchAsync(
            records.Select(record => (YuoPersistenceCommand)new SaveSnapshotCommand(record)).ToArray(),
            cancellationToken);
    }

    /// <summary>
    /// 淘汰超时未活动的闲置玩家：将其实体卸载并转移至冷快照 LRU 缓存，同时异步入队落盘。
    /// </summary>
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

    private void ScheduleNextTransition(DaoHuaPlayerState state)
    {
        if (state.PresenceMode != DaoHuaPresenceMode.Online
            || !_onlinePlayers.Contains(state.PlayerId))
        {
            InvalidateScheduledTransition(state.PlayerId);
            return;
        }

        var generation = ++_transitionGeneration;
        _onlineTransitionGenerations[state.PlayerId] = generation;
        var deadline = DaoHuaSimulation.GetNextTransitionAt(state);
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
            var state = entity.GetComponent<DaoHuaPlayerComponent>().State;
            var generation = ++_transitionGeneration;
            _onlineTransitionGenerations[playerId] = generation;
            var deadline = DaoHuaSimulation.GetNextTransitionAt(state);
            if (deadline is not null)
            {
                _onlineTransitionQueue.Enqueue(
                    new ScheduledPlayerTransition(playerId, generation),
                    deadline.Value);
            }
        }
    }

    /// <summary>
    /// 强制将当前加载的所有玩家状态快照批量刷入持久化队列（用于优雅停服或维护触发）。
    /// </summary>
    public async Task FlushAllAsync(CancellationToken cancellationToken)
    {
        var records = await ExecuteOnOwnerAsync(_ => _players.Values
            .Select(entity => ToSnapshotRecord(entity.GetComponent<DaoHuaPlayerComponent>().State))
            .ToArray(), cancellationToken);
        await _persistenceQueue.EnqueueBatchAsync(records.Select(record => (YuoPersistenceCommand)new SaveSnapshotCommand(record)).ToArray(), cancellationToken);
    }

    private void CapturePlayerMutation(
        DaoHuaPlayerState state,
        out DaoHuaPlayerPatchDto? patch,
        out DaoHuaPlayerSnapshotDto? snapshot,
        out YuoSnapshotRecord? record)
    {
        var sections = state.DirtySections;
        if (sections == DaoHuaDirtySections.None)
        {
            patch = null;
            snapshot = null;
            record = null;
            return;
        }

        var now = _clock.UtcNow;
        patch = DaoHuaStateMapper.ToPatch(state, sections, now);
        snapshot = DaoHuaStateMapper.ToSnapshot(state, now);
        record = ToSnapshotRecord(state);
        state.PendingEvents.Clear();
        state.DirtySections = DaoHuaDirtySections.None;
    }

    private async Task PersistPlayerAndMarketAsync(
        Guid playerId,
        DaoHuaPlayerPatchDto? patch,
        DaoHuaPlayerSnapshotDto? snapshot,
        YuoSnapshotRecord? record,
        CancellationToken cancellationToken)
    {
        // 到这里领域状态已经成功修改。客户端断开时请求令牌可能被取消，不能因此丢失
        // 已扣除的库存、已撮合的成交或待领取资产；持久化队列本身负责背压，停服时由
        // DaoHuaPersistenceHostedService drain 已入队命令，市场运行时也会写最终账本。
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
            snapshotPacket = DaoHuaPacketCodec.Encode(
                DaoHuaPacketKinds.Snapshot,
                string.Empty,
                NextPacketSequence(),
                snapshot);
        }

        PublishUpdate(new PlayerUpdate(
            playerId,
            DaoHuaPacketCodec.Encode(
                DaoHuaPacketKinds.Delta,
                string.Empty,
                NextPacketSequence(),
                patch),
            snapshotPacket,
            record));
    }

    /// <summary>
    /// 获取服务器运行时指标（模拟修订号、在线玩家数、活跃连接、持久化队列与系统内存占用）。
    /// </summary>
    public DaoHuaServerMetrics GetMetrics()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        return new DaoHuaServerMetrics(
            SimulationRevision,
            LoadedPlayerCount,
            ConnectedSessionCount,
            1,
            Environment.ProcessorCount,
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

    /// <summary>
    /// 确保玩家已在内存中就绪（若未加载则从存储快照加载并进行版本迁移与离线时间补算）。
    /// </summary>
    private Task EnsurePlayerAsync(Guid playerId, string? name, CancellationToken cancellationToken)
    {
        return EnsurePlayerAsync(playerId, name, null, cancellationToken);
    }

    /// <summary>
    /// 确保玩家已加载：从快照存储反序列化、初始化默认字段与 SequenceId、规范化数据并按确定性规则补算离线演进。
    /// </summary>
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
        var restored = stored is null ? null : DaoHuaStateSerializer.Deserialize(stored.Payload);
        YuoSnapshotRecord? migrationRecord = null;

        await ExecuteOnOwnerAsync(fiber =>
        {
            if (_players.ContainsKey(playerId)) return true;
            var state = restored ?? DaoHuaSimulation.CreateDefaultState(playerId, name, now, account);
            state.PlayerId = playerId;

            if (state.SequenceId <= 0)
            {
                state.SequenceId = _sharedState.GetNextSequenceId();
                migrationRecord = ToSnapshotRecord(state);
            }
            else
            {
                _sharedState.ObserveSequenceId(state.SequenceId);
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
                if (DaoHuaSimulation.NormalizeState(state))
                {
                    migrationRecord = ToSnapshotRecord(state);
                }
            }

            if (state.PresenceMode != DaoHuaPresenceMode.FullyOffline)
            {
                state.PresenceMode = DaoHuaPresenceMode.IdleOffline;
                DaoHuaSimulation.AdvanceTo(state, now);
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

    private void ObserveSequenceId(long sequenceId) => _sharedState.ObserveSequenceId(sequenceId);

    /// <summary>
    /// 获取指定玩家的状态对象：若在实体中直接返回，若在冷快照中则恢复为实体并从 LRU 移除。
    /// </summary>
    private DaoHuaPlayerState GetState(Guid playerId)
    {
        if (_players.TryGetValue(playerId, out var entity))
        {
            _lastPlayerAccess[playerId] = _clock.UtcNow;
            return entity.GetComponent<DaoHuaPlayerComponent>().State;
        }

        if (!_coldSnapshots.TryGetValue(playerId, out var payload))
        {
            throw new KeyNotFoundException($"玩家不存在: {playerId:D}");
        }

        var state = DaoHuaStateSerializer.Deserialize(payload)
            ?? throw new InvalidDataException($"玩家冷存档无法解析: {playerId:D}");
        DaoHuaSimulation.NormalizeState(state);
        if (state.PresenceMode != DaoHuaPresenceMode.FullyOffline)
        {
            state.PresenceMode = DaoHuaPresenceMode.IdleOffline;
            DaoHuaSimulation.AdvanceTo(state, _clock.UtcNow);
        }

        RemoveColdSnapshot(playerId);
        CreatePlayerEntity(_fiber, state);
        _knownPlayers[playerId] = 0;
        _lastPlayerAccess[playerId] = _clock.UtcNow;
        return state;
    }

    /// <summary>
    /// 在指定的 Fiber ECS World 中创建并注册玩家实体。
    /// </summary>
    private void CreatePlayerEntity(YuoFiber fiber, DaoHuaPlayerState state)
    {
        var entity = new YuoEntity(fiber.World, Interlocked.Increment(ref _entitySequence))
        {
            EntityName = $"player:{state.PlayerId:D}"
        };
        entity.AddComponent<DaoHuaPlayerComponent>().State = state;
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

        var state = entity.GetComponent<DaoHuaPlayerComponent>().State;
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
            var state = entity.GetComponent<DaoHuaPlayerComponent>().State;
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

    /// <summary>
    /// 解析并分发玩家具体的业务指令。
    /// </summary>
    private DaoHuaCommandResult ApplyCommand(
        DaoHuaPlayerState state,
        string command,
        JsonElement payload,
        DateTimeOffset commandAt)
    {
        try
        {
            return command switch
            {
                "rename" => ApplyRename(state, payload.Deserialize<RenameRequest>(DaoHuaPacketCodec.JsonOptions), commandAt),
                "enqueue_action" => ApplyEnqueue(state, payload.Deserialize<EnqueueActionRequest>(DaoHuaPacketCodec.JsonOptions)),
                "start_combat" => ApplyStartCombat(state, payload.Deserialize<StartCombatRequest>(DaoHuaPacketCodec.JsonOptions)),
                "remove_action" => ApplyRemove(state, payload.Deserialize<RemoveActionRequest>(DaoHuaPacketCodec.JsonOptions)),
                "move_action" => ApplyMove(state, payload.Deserialize<MoveActionRequest>(DaoHuaPacketCodec.JsonOptions)),
                "travel_to" => ApplyTravel(state, payload.Deserialize<TravelToRequest>(DaoHuaPacketCodec.JsonOptions)),
                "set_auto_use" => ApplyAutoUse(state, payload.Deserialize<SetAutoUseRequest>(DaoHuaPacketCodec.JsonOptions)),
                "use_item" => ApplyUseItem(state, payload.Deserialize<UseItemRequest>(DaoHuaPacketCodec.JsonOptions)),
                "set_combat_loadout" => ApplyCombatLoadout(state, payload.Deserialize<SetCombatLoadoutRequest>(DaoHuaPacketCodec.JsonOptions)),
                "set_loop" => ApplyLoop(state, payload.Deserialize<SetLoopRequest>(DaoHuaPacketCodec.JsonOptions)),
                "breakthrough" => ApplyBreakthrough(state),
                _ => DaoHuaCommandResult.Fail("COMMAND_NOT_FOUND", "未知命令。")
            };
        }
        catch (JsonException)
        {
            return DaoHuaCommandResult.Fail("INVALID_PAYLOAD", "命令参数格式错误。");
        }
    }

    private DaoHuaCommandResult ApplyRename(
        DaoHuaPlayerState state,
        RenameRequest? request,
        DateTimeOffset commandAt)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return DaoHuaCommandResult.Fail("INVALID_NAME", "修士道号不能为空。");
        }

        var newName = request.Name.Trim();
        if (newName.Length is < 2 or > 12)
        {
            return DaoHuaCommandResult.Fail("INVALID_NAME_LENGTH", "修士道号长度必须在 2 到 12 个字符之间。");
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(newName, @"^[\u4e00-\u9fa5a-zA-Z0-9_]+$"))
        {
            return DaoHuaCommandResult.Fail("INVALID_NAME_CHARACTERS", "修士道号仅支持中文、英文字母、数字和下划线，不支持特殊字符。");
        }

        if (string.Equals(state.Name, newName, StringComparison.Ordinal))
        {
            return DaoHuaCommandResult.Fail("SAME_NAME", "新道号不能与当前道号相同。");
        }

        if (state.LastRenamedAt.HasValue && commandAt - state.LastRenamedAt.Value < TimeSpan.FromHours(24))
        {
            var remaining = TimeSpan.FromHours(24) - (commandAt - state.LastRenamedAt.Value);
            var hours = (int)remaining.TotalHours;
            var minutes = remaining.Minutes;
            return DaoHuaCommandResult.Fail("RENAME_COOLDOWN", $"改名冷却中，还需等待 {hours} 小时 {minutes} 分。");
        }

        if (_takenPlayerNames.TryGetValue(newName, out var ownerId) && ownerId != state.PlayerId)
        {
            return DaoHuaCommandResult.Fail("NAME_ALREADY_EXISTS", "该道号已被其他修士使用，请更换其他名称。");
        }

        _takenPlayerNames.TryRemove(state.Name, out _);
        _takenPlayerNames[newName] = state.PlayerId;

        var oldName = state.Name;
        state.Name = newName;
        state.LastRenamedAt = commandAt;
        state.DirtySections |= DaoHuaDirtySections.Stats;
        DaoHuaSimulation.AddEvent(state, $"修士道号已由【{oldName}】修改为【{newName}】。");
        return DaoHuaCommandResult.Ok($"道号已成功变更为【{newName}】。");
    }

    private static DaoHuaCommandResult ApplyEnqueue(DaoHuaPlayerState state, EnqueueActionRequest? request)
    {
        return request is null
            ? DaoHuaCommandResult.Fail("INVALID_PAYLOAD", "缺少行动参数。")
            : DaoHuaSimulation.Enqueue(state, request.ActionId, request.Repeats);
    }

    private static DaoHuaCommandResult ApplyStartCombat(DaoHuaPlayerState state, StartCombatRequest? request)
    {
        return request is null
            ? DaoHuaCommandResult.Fail("INVALID_PAYLOAD", "缺少历练参数。")
            : DaoHuaSimulation.StartCombat(state, request.ActionId, request.Repeats);
    }

    private static DaoHuaCommandResult ApplyRemove(DaoHuaPlayerState state, RemoveActionRequest? request)
    {
        return request is not null && Guid.TryParse(request.EntryId, out var entryId)
            ? DaoHuaSimulation.Remove(state, entryId)
            : DaoHuaCommandResult.Fail("INVALID_ENTRY_ID", "行动 ID 无效。");
    }

    private static DaoHuaCommandResult ApplyMove(DaoHuaPlayerState state, MoveActionRequest? request)
    {
        return request is not null && Guid.TryParse(request.EntryId, out var entryId)
            ? DaoHuaSimulation.Move(state, entryId, request.Direction)
            : DaoHuaCommandResult.Fail("INVALID_ENTRY_ID", "行动 ID 无效。");
    }

    private static DaoHuaCommandResult ApplyTravel(DaoHuaPlayerState state, TravelToRequest? request)
    {
        return request is null || string.IsNullOrWhiteSpace(request.DestinationNodeId)
            ? DaoHuaCommandResult.Fail("INVALID_DESTINATION", "目标地点无效。")
            : DaoHuaSimulation.TravelTo(state, request.DestinationNodeId);
    }

    private static DaoHuaCommandResult ApplyAutoUse(DaoHuaPlayerState state, SetAutoUseRequest? request)
    {
        if (request is null || !TryParseActionType(request.ActionType, out var actionType))
        {
            return DaoHuaCommandResult.Fail("INVALID_ACTION_TYPE", "行动类型无效。");
        }

        var triggers = ParseTriggers(request.Triggers, out var triggerError);
        if (!string.IsNullOrWhiteSpace(triggerError))
        {
            return DaoHuaCommandResult.Fail("INVALID_TRIGGERS", triggerError);
        }

        return DaoHuaSimulation.SetAutoUse(
            state,
            actionType,
            request.ItemId,
            request.Quantity,
            request.Enabled,
            request.SlotIndex,
            triggers.Count > 0 ? triggers : null);
    }

    private static DaoHuaCommandResult ApplyUseItem(DaoHuaPlayerState state, UseItemRequest? request)
    {
        return request is not null && TryParseActionType(request.ActionType, out var actionType)
            ? DaoHuaSimulation.UseItem(state, request.ItemId, actionType, request.Quantity)
            : DaoHuaCommandResult.Fail("INVALID_ACTION_TYPE", "行动类型无效。");
    }

    private static DaoHuaCommandResult ApplyLoop(DaoHuaPlayerState state, SetLoopRequest? request)
    {
        return request is null
            ? DaoHuaCommandResult.Fail("INVALID_PAYLOAD", "缺少循环参数。")
            : DaoHuaSimulation.SetLoop(state, request.Enabled);
    }

    private static DaoHuaCommandResult ApplyCombatLoadout(
        DaoHuaPlayerState state,
        SetCombatLoadoutRequest? request)
    {
        if (request is null)
        {
            return DaoHuaCommandResult.Fail("INVALID_PAYLOAD", "缺少战斗配置参数。");
        }

        DaoHuaCombatStyle? style = null;
        if (!string.IsNullOrWhiteSpace(request.Style))
        {
            if (!TryParseCombatStyle(request.Style, out var parsedStyle))
            {
                return DaoHuaCommandResult.Fail("INVALID_COMBAT_STYLE", "战斗流派无效。");
            }

            style = parsedStyle;
        }

        DaoHuaDamageType? damageType = null;
        if (!string.IsNullOrWhiteSpace(request.DamageType))
        {
            if (!TryParseDamageType(request.DamageType, out var parsedDamageType))
            {
                return DaoHuaCommandResult.Fail("INVALID_DAMAGE_TYPE", "伤害类型无效。");
            }

            damageType = parsedDamageType;
        }

        DaoHuaCombatAbilitySlot[]? abilities = null;
        if (request.AbilitySlots is not null)
        {
            var normalized = new List<DaoHuaCombatAbilitySlot>(request.AbilitySlots.Count);
            foreach (var slot in request.AbilitySlots)
            {
                var triggers = ParseTriggers(slot.Triggers, out var triggerError);
                if (!string.IsNullOrWhiteSpace(triggerError))
                {
                    return DaoHuaCommandResult.Fail("INVALID_ABILITY_TRIGGERS", triggerError);
                }

                normalized.Add(new DaoHuaCombatAbilitySlot
                {
                    AbilityId = slot.AbilityId,
                    Level = slot.Level,
                    Triggers = triggers
                });
            }

            abilities = normalized.ToArray();
        }

        DaoHuaConsumableSlot[]? consumables = null;
        if (request.ConsumableSlots is not null)
        {
            var normalized = new List<DaoHuaConsumableSlot>(request.ConsumableSlots.Count);
            foreach (var slot in request.ConsumableSlots)
            {
                var triggers = ParseTriggers(slot.Triggers, out var triggerError);
                if (!string.IsNullOrWhiteSpace(triggerError))
                {
                    return DaoHuaCommandResult.Fail("INVALID_CONSUMABLE_TRIGGERS", triggerError);
                }

                normalized.Add(new DaoHuaConsumableSlot
                {
                    ItemId = slot.ItemId,
                    Quantity = slot.Quantity,
                    Enabled = slot.Enabled,
                    Triggers = triggers
                });
            }

            consumables = normalized.ToArray();
        }

        DaoHuaEquippedItem[]? equipment = null;
        if (request.Equipment is not null)
        {
            equipment = request.Equipment
                .Select(item => new DaoHuaEquippedItem
                {
                    ItemId = item.ItemId,
                    EnhancementLevel = item.EnhancementLevel
                })
                .ToArray();
        }

        return DaoHuaSimulation.SetCombatLoadout(
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

    private static DaoHuaCommandResult ApplyBreakthrough(DaoHuaPlayerState state)
    {
        return DaoHuaSimulation.Breakthrough(state);
    }

    private static List<DaoHuaCombatTrigger> ParseTriggers(
        IReadOnlyList<DaoHuaCombatTriggerDto>? source,
        out string error)
    {
        error = string.Empty;
        if (source is null) return [];
        if (source.Count > DaoHuaCombatLimits.TriggerCountPerSlot)
        {
            error = "单个战斗槽位最多配置 4 条触发条件。";
            return [];
        }

        var result = new List<DaoHuaCombatTrigger>(source.Count);
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
        DaoHuaCombatTriggerDto dto,
        out DaoHuaCombatTrigger trigger)
    {
        trigger = new DaoHuaCombatTrigger();
        if (!TryParseTriggerDependency(dto.Dependency, out var dependency)
            || !TryParseTriggerCondition(dto.Condition, out var condition)
            || !TryParseTriggerComparator(dto.Comparator, out var comparator)
            || double.IsNaN(dto.Value)
            || double.IsInfinity(dto.Value))
        {
            return false;
        }

        trigger = new DaoHuaCombatTrigger
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
        out DaoHuaCombatTriggerDependency dependency)
    {
        dependency = value?.Trim().ToLowerInvariant() switch
        {
            "self" => DaoHuaCombatTriggerDependency.Self,
            "target-enemy" => DaoHuaCombatTriggerDependency.TargetEnemy,
            "ally-lowest-hp" => DaoHuaCombatTriggerDependency.AllyLowestHitpoints,
            "ally-highest-hp" => DaoHuaCombatTriggerDependency.AllyHighestHitpoints,
            "enemy-lowest-hp" => DaoHuaCombatTriggerDependency.EnemyLowestHitpoints,
            "enemy-highest-hp" => DaoHuaCombatTriggerDependency.EnemyHighestHitpoints,
            _ => (DaoHuaCombatTriggerDependency)(-1)
        };
        return Enum.IsDefined(dependency);
    }

    private static bool TryParseTriggerCondition(
        string? value,
        out DaoHuaCombatTriggerCondition condition)
    {
        condition = value?.Trim().ToLowerInvariant() switch
        {
            "current-hp" => DaoHuaCombatTriggerCondition.CurrentHitpoints,
            "current-mana" => DaoHuaCombatTriggerCondition.CurrentManapoints,
            "missing-hp" => DaoHuaCombatTriggerCondition.MissingHitpoints,
            "missing-mana" => DaoHuaCombatTriggerCondition.MissingManapoints,
            "buff-stacks" => DaoHuaCombatTriggerCondition.BuffStacks,
            "stun-status" => DaoHuaCombatTriggerCondition.StunStatus,
            "blind-status" => DaoHuaCombatTriggerCondition.BlindStatus,
            "silence-status" => DaoHuaCombatTriggerCondition.SilenceStatus,
            "current-hp-percentage" => DaoHuaCombatTriggerCondition.CurrentHitpointsPercentage,
            "missing-hp-percentage" => DaoHuaCombatTriggerCondition.MissingHitpointsPercentage,
            "current-mana-percentage" => DaoHuaCombatTriggerCondition.CurrentManapointsPercentage,
            "missing-mana-percentage" => DaoHuaCombatTriggerCondition.MissingManapointsPercentage,
            _ => (DaoHuaCombatTriggerCondition)(-1)
        };
        return Enum.IsDefined(condition);
    }

    private static bool TryParseTriggerComparator(
        string? value,
        out DaoHuaCombatTriggerComparator comparator)
    {
        comparator = value?.Trim().ToLowerInvariant() switch
        {
            "less-than-equal" => DaoHuaCombatTriggerComparator.LessThanOrEqual,
            "greater-than-equal" => DaoHuaCombatTriggerComparator.GreaterThanOrEqual,
            "less-than" => DaoHuaCombatTriggerComparator.LessThan,
            "greater-than" => DaoHuaCombatTriggerComparator.GreaterThan,
            "is-active" => DaoHuaCombatTriggerComparator.IsActive,
            "is-inactive" => DaoHuaCombatTriggerComparator.IsInactive,
            _ => (DaoHuaCombatTriggerComparator)(-1)
        };
        return Enum.IsDefined(comparator);
    }

    private static bool TryParseActionType(string value, out DaoHuaActionType type)
    {
        type = value switch
        {
            "cultivation" => DaoHuaActionType.Cultivation,
            "exploration" => DaoHuaActionType.Exploration,
            "alchemy" => DaoHuaActionType.Alchemy,
            "combat" => DaoHuaActionType.Combat,
            _ => (DaoHuaActionType)(-1)
        };
        return Enum.IsDefined(type);
    }

    private static bool TryParseMarketSide(string? value, out DaoHuaMarketSide side)
    {
        side = value?.Trim().ToLowerInvariant() switch
        {
            "buy" => DaoHuaMarketSide.Buy,
            "sell" => DaoHuaMarketSide.Sell,
            _ => (DaoHuaMarketSide)(-1)
        };
        return Enum.IsDefined(side);
    }

    private static bool TryParseCombatStyle(string value, out DaoHuaCombatStyle style)
    {
        style = value switch
        {
            "sword" => DaoHuaCombatStyle.Sword,
            "blade" => DaoHuaCombatStyle.Blade,
            "talisman" => DaoHuaCombatStyle.Talisman,
            "five-elements" => DaoHuaCombatStyle.FiveElements,
            "body" => DaoHuaCombatStyle.Body,
            _ => (DaoHuaCombatStyle)(-1)
        };
        return Enum.IsDefined(style);
    }

    private static bool TryParseDamageType(string value, out DaoHuaDamageType type)
    {
        type = value switch
        {
            "physical" => DaoHuaDamageType.Physical,
            "water" => DaoHuaDamageType.Water,
            "nature" => DaoHuaDamageType.Nature,
            "fire" => DaoHuaDamageType.Fire,
            _ => (DaoHuaDamageType)(-1)
        };
        return Enum.IsDefined(type);
    }

    private YuoSnapshotRecord ToSnapshotRecord(DaoHuaPlayerState state)
    {
        return new YuoSnapshotRecord(
            "player",
            state.PlayerId.ToString("D"),
            DaoHuaStateSerializer.Serialize(state),
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

    private long NextPacketSequence() => _sharedState.NextPacketSequence();

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

/// <summary>
/// 玩家指令执行产出物（包含领域执行结果与可选的增量补丁 DTO）。
/// </summary>
public sealed record DaoHuaCommandOutcome(
    DaoHuaCommandResult Result,
    DaoHuaPlayerPatchDto? Patch)
{
    public static DaoHuaCommandOutcome Failed(string code, string message) =>
        new(DaoHuaCommandResult.Fail(code, message), null);
}

/// <summary>
/// 市场指令执行产出物（包含订单视图、成交明细与全量快照）。
/// </summary>
public sealed record DaoHuaMarketCommandOutcome(
    bool Success,
    string Code,
    string Message,
    DaoHuaMarketOrderView? Order,
    IReadOnlyList<DaoHuaMarketFill> Fills,
    DaoHuaPlayerSnapshotDto? Snapshot)
{
    public DaoHuaCommandResult Result => new(Success, Code, Message);

    public static DaoHuaMarketCommandOutcome From(
        DaoHuaMarketPlacementResult result,
        DaoHuaPlayerSnapshotDto? snapshot) =>
        new(result.Success, result.Code, result.Message, result.Order, result.Fills, snapshot);

    public static DaoHuaMarketCommandOutcome From(
        DaoHuaMarketCancellationResult result,
        DaoHuaPlayerSnapshotDto? snapshot) =>
        new(result.Success, result.Code, result.Message, result.Order, [], snapshot);

    public static DaoHuaMarketCommandOutcome Failed(string code, string message) =>
        new(false, code, message, null, [], null);

    public static DaoHuaMarketCommandOutcome Ok(string message, DaoHuaPlayerSnapshotDto? snapshot) =>
        new(true, "OK", message, null, [], snapshot);
}

/// <summary>
/// 在线玩家状态推进耗时与更新数量报告。
/// </summary>
public sealed record DaoHuaAdvanceReport(long Revision, int UpdatedPlayers, double ElapsedMilliseconds);

/// <summary>
/// 计划在未来时刻发生状态跃迁的玩家调度条目。
/// </summary>
internal readonly record struct ScheduledPlayerTransition(Guid PlayerId, long Generation);

/// <summary>
/// 服务器性能与业务运行监控指标快照。
/// </summary>
public sealed record DaoHuaServerMetrics(
    long SimulationRevision,
    int LoadedPlayers,
    int ConnectedSessions,
    int PlayerShardCount,
    int ProcessorCount,
    YuoPersistenceQueueStats Persistence,
    double ProcessCpuMilliseconds,
    long ManagedMemoryBytes,
    long WorkingSetBytes);
