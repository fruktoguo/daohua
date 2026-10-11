using System.Collections.Concurrent;
using Domain.Combat;
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

    /// <summary>构造分片路由运行时：创建 shardCount 个独立 World/Fiber 分片并接管进程级 System 异常开关。</summary>
    /// <param name="gameData">配表目录；缺省使用内置 BuiltIn 目录。</param>
    /// <param name="startAt">世界时钟起点；缺省 UnixEpoch，各分片 EcsClockComponent 由此起算。</param>
    /// <param name="shardCount">分片数量（1..64）；每名玩家按稳定哈希在进程生命周期内固定路由。</param>
    /// <param name="duelAdvance">斗法推进选项；仅在线主服启用撮合，离线补算与 ComputeNode 传 null。</param>
    public EcsGameRuntime(GameDataCatalog? gameData = null, DateTimeOffset? startAt = null, int shardCount = 1,
        DuelAdvanceOptions? duelAdvance = null)
    {
        if (shardCount is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(shardCount), "ECS shard 数量必须在 1..64。");
        InlineCombatTriggerRegistry.EnsureInitialized();
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
                shards.Add(new EcsWorldShard(GameData, StartAt, $"daohua-ecs-shard-{index}", index == 0, ownCloseSystemTry: false, duelAdvance));
            _shards = shards;
        }
        catch
        {
            foreach (var shard in shards) shard.Dispose();
            YuoWorld.CloseSystemTry = _previousCloseSystemTry;
            throw;
        }
    }

    /// <summary>本运行时全部分片共享的配表目录。</summary>
    public GameDataCatalog GameData { get; }
    /// <summary>世界时钟起点（构造参数，缺省 UnixEpoch）。</summary>
    public DateTimeOffset StartAt { get; }
    /// <summary>分片数量（构造时固定）。</summary>
    public int ShardCount => _shards.Count;

    /// <summary>单分片兼容入口；多分片业务必须按玩家使用 GetWorld。</summary>
    public YuoWorld World => _shards[0].World;

    /// <summary>单分片兼容入口；多分片业务必须按玩家使用 GetFiber。</summary>
    public YuoFiber Fiber => _shards[0].Fiber;

    /// <summary>全部分片已加载的玩家实体总数。</summary>
    public int PlayerCount => _shards.Sum(shard => shard.PlayerCount);
    /// <summary>兼容入口分片（0 号）的地图节点实体数；各分片节点集由同一配表构建，数量一致。</summary>
    public int MapNodeCount => _shards[0].MapNodeCount;

    /// <summary>按节点 ID 解析长期存在的地图节点实体（单分片入口；多分片各 shard 持有自己的节点实体集）。</summary>
    public long? GetMapNodeEntityId(string nodeId) => _shards[0].GetMapNodeEntityId(nodeId);

    /// <summary>按玩家路由取其所在分片的 World；未加载玩家抛异常。</summary>
    public YuoWorld GetWorld(Guid playerId) => GetShard(playerId).World;
    /// <summary>按玩家路由取其所在分片的 Fiber；未加载玩家抛异常。</summary>
    public YuoFiber GetFiber(Guid playerId) => GetShard(playerId).Fiber;

    /// <summary>把宿主侧公共建筑不可变投影写入玩家世界分段；实际变化推进版本并标脏 World。</summary>
    public bool ApplyNodeBuildingProjection(Guid playerId, EcsNodeBuildingProjection projection, DateTimeOffset now)
        => GetShard(playerId).ApplyNodeBuildingProjection(playerId, projection, now);

    /// <summary>取出并清空该玩家的模拟事件缓冲；顺序与领域原语和战斗边界的产生顺序一致。</summary>
    public IReadOnlyList<SimulationEvent> DrainSimulationEvents(Guid playerId)
        => GetShard(playerId).DrainSimulationEvents(playerId);

    /// <summary>取出该玩家最近一次推进的完整结果（宿主按原始增量原序提交经济事务）；无待消费结果返回 null。</summary>
    public SimulationResult? DrainSimulationResult(Guid playerId)
        => GetShard(playerId).DrainSimulationResult(playerId);

    /// <summary>读取该玩家当前待持久化的分段集合。</summary>
    public PlayerSectionKind[] GetDirtySections(Guid playerId)
        => GetShard(playerId).GetDirtySections(playerId);

    /// <summary>读取分段脏流水号快照；宿主以「前后差集」判定本次请求实际触及的分段。</summary>
    public IReadOnlyDictionary<PlayerSectionKind, long> GetDirtyMarks(Guid playerId)
        => GetShard(playerId).GetDirtyMarks(playerId);

    /// <summary>把指定分段加入该玩家的待持久化集合。</summary>
    public void MarkDirty(Guid playerId, PlayerSectionKind section)
        => GetShard(playerId).MarkDirty(playerId, section);

    /// <summary>宿主刷盘成功后从待持久化集合移除已确认分段。</summary>
    public void AcknowledgeDirtySections(Guid playerId, IEnumerable<PlayerSectionKind> sections)
        => GetShard(playerId).AcknowledgeDirtySections(playerId, sections);

    /// <summary>持久化事务失败时恢复请求前的脏分段集合，避免旧脏段在恢复组件后丢失。</summary>
    public void RestoreDirtySections(Guid playerId, IEnumerable<PlayerSectionKind> sections)
        => GetShard(playerId).RestoreDirtySections(playerId, sections);

    /// <summary>以数据库提交回执的余额为准覆写资产组件；不重建其它玩家组件。</summary>
    public void SyncAssetBalances(Guid playerId, IReadOnlyCollection<AssetBalance> balances)
        => GetShard(playerId).SyncAssetBalances(playerId, balances);

    /// <summary>外部来源（市场结算入账等）改动了玩家客户端可见状态：仅推进版本不伪造分段脏。</summary>
    public void MarkExternalStateChanged(Guid playerId)
        => GetShard(playerId).MarkExternalStateChanged(playerId);

    /// <summary>按稳定哈希把玩家路由到分片，在分片 Fiber 内按完整领域快照创建实体；重复创建同 ID 玩家抛异常。</summary>
    public Guid CreatePlayer(PlayerAggregateState state, IReadOnlyCollection<AssetBalance>? balances = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var playerId = state.PlayerId.Value;
        var shardIndex = ShardIndex(playerId);
        var result = _shards[shardIndex].CreatePlayer(state, balances);
        _playerShards[playerId] = shardIndex;
        return result;
    }

    /// <summary>聚合对象重载：导出状态后走状态重载。</summary>
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

    /// <summary>创建怪物实体；提供父节点实体时落在持有该实体的分片（保持父子同分片），否则建在 0 号分片。</summary>
    public long CreateMonster(string monsterDefinitionId, long? parentNodeEntityId = null)
    {
        // 父实体在哪个分片，怪物就建在哪个分片；找不到父实体时退回 0 号分片。
        var shard = parentNodeEntityId is { } parent
            ? _shards.FirstOrDefault(candidate => candidate.World.GetEntity(parent) is not null) ?? _shards[0]
            : _shards[0];
        return shard.CreateMonster(monsterDefinitionId, parentNodeEntityId);
    }

    /// <summary>在玩家所在分片创建真实战斗实体（玩家/怪物 Combatant 与长期 Encounter 实体同 World）。</summary>
    public long CreateEncounter(Guid playerId, string encounterId, long seed)
        => GetShard(playerId).CreateEncounter(playerId, encounterId, seed);

    /// <summary>向玩家行动队列按序追加行动；实例 ID 必须显式提供，作为回放与账本对账的确定性输入。</summary>
    public void EnqueueAction(Guid playerId, ActionId actionId, double repeatCount = 1, Guid? actionInstanceId = null)
        => GetShard(playerId).EnqueueAction(playerId, actionId, repeatCount, actionInstanceId);

    /// <summary>把全部分片按固定顺序推进到 target；分片间暂不并发，避免把调度变化混入结果一致性验证。</summary>
    public ValueTask RunOnceAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
        => RunAllShardsAsync(target, cancellationToken);

    /// <summary>只推进玩家所在分片，分片之间不共享 Fiber owner gate。</summary>
    public ValueTask RunPlayerOnceAsync(Guid playerId, DateTimeOffset target, CancellationToken cancellationToken = default)
        => GetShard(playerId).RunPlayerOnceAsync(playerId, target, cancellationToken);

    /// <summary>导出玩家完整领域快照与资产余额（只读，不改状态）。</summary>
    public EcsPlayerSnapshot ExportSnapshot(Guid playerId)
        => GetShard(playerId).ExportSnapshot(playerId);

    /// <summary>导出全部分片全部玩家快照，按 PlayerId 升序排列保证确定性。</summary>
    public EcsPlayerSnapshot[] ExportSnapshots()
        => _shards.SelectMany(shard => shard.ExportSnapshots()).OrderBy(snapshot => snapshot.PlayerId).ToArray();

    /// <summary>查询玩家下一个可见行动边界（只读）；空闲/受阻返回 null。</summary>
    public DateTimeOffset? NextAdvanceBoundary(Guid playerId)
        => GetShard(playerId).NextAdvanceBoundary(playerId);

    /// <summary>全部分片已加载玩家的最早可见边界；供宿主后台时钟动态休眠。</summary>
    public DateTimeOffset? NextAdvanceBoundaryUtc
        => _shards.Select(shard => shard.NextAdvanceBoundaryUtc).Where(value => value is not null).Min();

    /// <summary>卸载玩家实体及其运行中战斗实体；未加载返回 false。</summary>
    public bool RemovePlayer(Guid playerId)
    {
        if (!_playerShards.TryGetValue(playerId, out var shardIndex)) return false;
        var shard = _shards[shardIndex];
        var removed = shard.RemovePlayer(playerId);
        if (removed) _playerShards.TryRemove(playerId, out _);
        return removed;
    }

    /// <summary>更新玩家在线状态并维护离线锚点；切到离线时重置离线战报并推进状态版本。</summary>
    public bool SetPresence(Guid playerId, PresenceState presence, DateTimeOffset at)
        => GetShard(playerId).SetPresence(playerId, presence, at);

    /// <summary>资产事务失败或宿主回滚时，在原实体上恢复完整快照与钱包。</summary>
    public void RestorePlayer(Guid playerId, PlayerAggregateState state, IReadOnlyCollection<AssetBalance> balances)
        => GetShard(playerId).RestorePlayer(playerId, state, balances);

    /// <summary>显式取消指定行动实例；队首战斗实体与战报一并按领域移除语义清理。</summary>
    public bool RemoveAction(Guid playerId, Guid actionInstanceId)
        => GetShard(playerId).RemoveAction(playerId, actionInstanceId);

    /// <summary>命令级玩家变更通道（与原聚合运行时命令语义对齐）；返回本次新增的脏标记差集。</summary>
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
        // FNV-1a 32 位哈希：无锁、分布均匀，进程生命周期内同一 PlayerId 恒路由到同一分片。
        Span<byte> bytes = stackalloc byte[16];
        playerId.TryWriteBytes(bytes);
        unchecked
        {
            uint hash = 2166136261;
            foreach (var value in bytes) hash = (hash ^ value) * 16777619;
            return (int)(hash % (uint)_shards.Count);
        }
    }

    /// <summary>释放全部分片并恢复进程级 System 异常开关。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var shard in _shards) shard.Dispose();
        _playerShards.Clear();
        YuoWorld.CloseSystemTry = _previousCloseSystemTry;
    }

    /// <summary>异步释放入口；与 <see cref="Dispose"/> 等价。</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
