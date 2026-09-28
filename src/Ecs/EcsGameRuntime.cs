using System.Collections.Concurrent;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using YuoCore.Ecs;
using YuoCore.Runtime;

namespace DaoHua.Ecs;

/// <summary>
/// ECS 游戏运行时的分片路由。默认一个分片以保持现有公开 API；压测可固定配置多个
/// World/Fiber，每名玩家在进程生命周期内由稳定 PlayerId 哈希到一个分片。
/// </summary>
public sealed class EcsGameRuntime : IAsyncDisposable, IDisposable
{
    private readonly IReadOnlyList<EcsWorldShard> _shards;
    private readonly ConcurrentDictionary<Guid, int> _playerShards = new();
    private readonly bool _previousCloseSystemTry;
    private bool _disposed;

    public EcsGameRuntime(GameDataCatalog? gameData = null, DateTimeOffset? startAt = null, int shardCount = 1)
    {
        if (shardCount is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(shardCount), "ECS shard 数量必须在 1..64。");
        GameData = gameData ?? GameDataCatalog.BuiltIn;
        StartAt = startAt ?? DateTimeOffset.UnixEpoch;
        _previousCloseSystemTry = YuoWorld.CloseSystemTry;
        // 业务运行时不允许 YuoCore 把 System 异常隔离成成功请求；所有分片共用这个
        // 进程级开关，不能让某个分片的 Dispose 提前恢复它。
        YuoWorld.CloseSystemTry = Environment.GetEnvironmentVariable("DAOHUA_ECS_CLOSE_SYS_TRY") != "0";
        var shards = new List<EcsWorldShard>(shardCount);
        try
        {
            for (var index = 0; index < shardCount; index++)
                shards.Add(new EcsWorldShard(GameData, StartAt, $"daohua-ecs-shard-{index}", index == 0, ownCloseSystemTry: false));
            _shards = shards;
        }
        catch
        {
            foreach (var shard in shards) shard.Dispose();
            YuoWorld.CloseSystemTry = _previousCloseSystemTry;
            throw;
        }
    }

    public GameDataCatalog GameData { get; }
    public DateTimeOffset StartAt { get; }
    public int ShardCount => _shards.Count;

    /// <summary>单分片兼容入口；多分片业务必须按玩家使用 GetWorld。</summary>
    public YuoWorld World => _shards[0].World;

    /// <summary>单分片兼容入口；多分片业务必须按玩家使用 GetFiber。</summary>
    public YuoFiber Fiber => _shards[0].Fiber;

    public int PlayerCount => _shards.Sum(shard => shard.PlayerCount);
    public int MapNodeCount => _shards[0].MapNodeCount;

    public YuoWorld GetWorld(Guid playerId) => GetShard(playerId).World;
    public YuoFiber GetFiber(Guid playerId) => GetShard(playerId).Fiber;

    public bool ApplyNodeBuildingProjection(Guid playerId, EcsNodeBuildingProjection projection, DateTimeOffset now)
        => GetShard(playerId).ApplyNodeBuildingProjection(playerId, projection, now);

    public IReadOnlyList<SimulationEvent> DrainSimulationEvents(Guid playerId)
        => GetShard(playerId).DrainSimulationEvents(playerId);

    public SimulationResult? DrainSimulationResult(Guid playerId)
        => GetShard(playerId).DrainSimulationResult(playerId);

    public PlayerSectionKind[] GetDirtySections(Guid playerId)
        => GetShard(playerId).GetDirtySections(playerId);

    public IReadOnlyDictionary<PlayerSectionKind, long> GetDirtyMarks(Guid playerId)
        => GetShard(playerId).GetDirtyMarks(playerId);

    public void MarkDirty(Guid playerId, PlayerSectionKind section)
        => GetShard(playerId).MarkDirty(playerId, section);

    public void AcknowledgeDirtySections(Guid playerId, IEnumerable<PlayerSectionKind> sections)
        => GetShard(playerId).AcknowledgeDirtySections(playerId, sections);

    public void RestoreDirtySections(Guid playerId, IEnumerable<PlayerSectionKind> sections)
        => GetShard(playerId).RestoreDirtySections(playerId, sections);

    public void SyncAssetBalances(Guid playerId, IReadOnlyCollection<AssetBalance> balances)
        => GetShard(playerId).SyncAssetBalances(playerId, balances);

    public Guid CreatePlayer(PlayerAggregateState state, IReadOnlyCollection<AssetBalance>? balances = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var playerId = state.PlayerId.Value;
        var shardIndex = ShardIndex(playerId);
        var result = _shards[shardIndex].CreatePlayer(state, balances);
        _playerShards[playerId] = shardIndex;
        return result;
    }

    public Guid CreatePlayer(PlayerAggregate aggregate, IReadOnlyCollection<AssetBalance>? balances = null)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return CreatePlayer(aggregate.ExportState(), balances);
    }

    /// <summary>地图节点是每个分片的长期只读投影，返回兼容入口分片的实体 ID。</summary>
    public long CreateMapNode(WorldNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var first = 0L;
        foreach (var shard in _shards)
        {
            var entityId = shard.CreateMapNode(node);
            if (first == 0) first = entityId;
        }
        return first;
    }

    public long CreateMonster(string monsterDefinitionId, long? parentNodeEntityId = null)
    {
        var shard = parentNodeEntityId is { } parent
            ? _shards.FirstOrDefault(candidate => candidate.World.GetEntity(parent) is not null) ?? _shards[0]
            : _shards[0];
        return shard.CreateMonster(monsterDefinitionId, parentNodeEntityId);
    }

    public long CreateEncounter(Guid playerId, string encounterId, long seed)
        => GetShard(playerId).CreateEncounter(playerId, encounterId, seed);

    public void EnqueueAction(Guid playerId, ActionId actionId, double repeatCount = 1, Guid? actionInstanceId = null)
        => GetShard(playerId).EnqueueAction(playerId, actionId, repeatCount, actionInstanceId);

    public ValueTask RunOnceAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
        => RunAllShardsAsync(target, cancellationToken);

    /// <summary>只推进玩家所在分片，分片之间不共享 Fiber owner gate。</summary>
    public ValueTask RunPlayerOnceAsync(Guid playerId, DateTimeOffset target, CancellationToken cancellationToken = default)
        => GetShard(playerId).RunPlayerOnceAsync(playerId, target, cancellationToken);

    public EcsPlayerSnapshot ExportSnapshot(Guid playerId)
        => GetShard(playerId).ExportSnapshot(playerId);

    public EcsPlayerSnapshot[] ExportSnapshots()
        => _shards.SelectMany(shard => shard.ExportSnapshots()).OrderBy(snapshot => snapshot.PlayerId).ToArray();

    public DateTimeOffset? NextAdvanceBoundary(Guid playerId)
        => GetShard(playerId).NextAdvanceBoundary(playerId);

    public DateTimeOffset? NextAdvanceBoundaryUtc
        => _shards.Select(shard => shard.NextAdvanceBoundaryUtc).Where(value => value is not null).Min();

    public bool RemovePlayer(Guid playerId)
    {
        if (!_playerShards.TryGetValue(playerId, out var shardIndex)) return false;
        var shard = _shards[shardIndex];
        var removed = shard.RemovePlayer(playerId);
        if (removed) _playerShards.TryRemove(playerId, out _);
        return removed;
    }

    public bool SetPresence(Guid playerId, PresenceState presence)
        => GetShard(playerId).SetPresence(playerId, presence);

    public void RestorePlayer(Guid playerId, PlayerAggregateState state, IReadOnlyCollection<AssetBalance> balances)
        => GetShard(playerId).RestorePlayer(playerId, state, balances);

    public bool RemoveAction(Guid playerId, Guid actionInstanceId)
        => GetShard(playerId).RemoveAction(playerId, actionInstanceId);

    /// <summary>命令级玩家变更通道（与 OOP PlayerActor 命令语义对齐）；返回本次新增的脏标记差集。</summary>
    public IReadOnlySet<PlayerSectionKind> MutatePlayer(Guid playerId, Action<PlayerAggregate> mutate,
        bool destroyEncounters = false)
        => GetShard(playerId).MutatePlayer(playerId, mutate, destroyEncounters);

    /// <summary>属性输入修订号：命令/推进后与原值不一致即等价 OOP 的 WireAttributesChanged。</summary>
    public long AttributeRevision(Guid playerId)
        => GetShard(playerId).AttributeRevision(playerId);

    private async ValueTask RunAllShardsAsync(DateTimeOffset target, CancellationToken cancellationToken)
    {
        // 先保持分片顺序确定；分片间并发属于后续单独基准项，避免把调度变化混入
        // 本阶段的结果一致性验证。
        foreach (var shard in _shards)
            await shard.RunOnceAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private EcsWorldShard GetShard(Guid playerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_playerShards.TryGetValue(playerId, out var shardIndex))
            throw new KeyNotFoundException($"ECS 玩家尚未加载：{playerId:D}");
        return _shards[shardIndex];
    }

    private int ShardIndex(Guid playerId)
    {
        Span<byte> bytes = stackalloc byte[16];
        playerId.TryWriteBytes(bytes);
        unchecked
        {
            uint hash = 2166136261;
            foreach (var value in bytes) hash = (hash ^ value) * 16777619;
            return (int)(hash % (uint)_shards.Count);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var shard in _shards) shard.Dispose();
        _playerShards.Clear();
        YuoWorld.CloseSystemTry = _previousCloseSystemTry;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
