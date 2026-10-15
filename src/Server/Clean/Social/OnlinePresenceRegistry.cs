using Protocol;

namespace Server.Clean.Social;

/// <summary>
/// 在线修士存在感注册表：记录每个在线会话的玩家 ID、道号与当前地图节点。
/// 用途：
/// 1. 世界频道广播（全部在线玩家）与附近频道广播（同节点在线玩家）的收件人解析；
/// 2. 好友/队伍在线状态标记与附近修士列表投影；
/// 3. 节点变化时通知受影响玩家刷新社交状态（旧节点、新节点与本人）。
/// 该注册表只保存展示所需的轻量字段，权威位置仍以玩家聚合为准。
/// </summary>
public sealed class OnlinePresenceRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    /// <summary>节点 → 在线玩家集合的倒排索引；与 <see cref="_entries"/> 同锁维护，只索引非空节点。</summary>
    private readonly Dictionary<string, HashSet<Guid>> _nodeIndex = [];

    /// <summary>存在感变化事件：参数为附近列表可能发生变化的玩家 ID 集合。</summary>
    public event Func<IReadOnlyList<Guid>, ValueTask>? PresenceChanged;

    /// <summary>
    /// 节点变化事件：仅携带移动者本人（Unregister 不触发——离线保留讨伐席位，
    /// 逐出由 48h 扫描负责；总纲 §21.13 全员在节点条件由社交层据此裁决）。
    /// </summary>
    public event Func<Guid, ValueTask>? NodeChanged;

    private readonly record struct Entry(string DisplayName, string? NodeId, int RealmLevel, double Valuation);

    /// <summary>登记或刷新在线玩家；节点变化时返回受影响的玩家集合。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="displayName">玩家道号。</param>
    /// <param name="nodeId">当前地图节点 ID；行进在边上时为 null。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public ValueTask RegisterAsync(Guid playerId, string displayName, string? nodeId, int realmLevel = 1, double valuation = 0, CancellationToken cancellationToken = default)
    {
        List<Guid> affected;
        bool nodeChanged;
        lock (_gate)
        {
            _entries.TryGetValue(playerId, out var previous);
            _entries[playerId] = new Entry(displayName, nodeId, realmLevel, valuation);
            nodeChanged = previous.NodeId != nodeId;
            if (nodeChanged)
            {
                RemoveFromNodeIndex(previous.NodeId, playerId);
                AddToNodeIndex(nodeId, playerId);
            }
            affected = CollectAffected(previous.NodeId, nodeId);
        }
        return PublishAsync(affected, nodeChanged ? playerId : null);
    }

    /// <summary>更新玩家当前节点；未登记的玩家（已离线）为空操作。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="nodeId">新的节点 ID；行进在边上时为 null。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public ValueTask UpdateNodeAsync(Guid playerId, string? nodeId, CancellationToken cancellationToken = default)
    {
        List<Guid> affected;
        lock (_gate)
        {
            if (!_entries.TryGetValue(playerId, out var previous)) return ValueTask.CompletedTask;
            if (previous.NodeId == nodeId) return ValueTask.CompletedTask;
            _entries[playerId] = previous with { NodeId = nodeId };
            RemoveFromNodeIndex(previous.NodeId, playerId);
            AddToNodeIndex(nodeId, playerId);
            affected = CollectAffected(previous.NodeId, nodeId);
        }
        return PublishAsync(affected, playerId);
    }

    /// <summary>移除在线登记（会话关闭）。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public ValueTask UnregisterAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        List<Guid> affected;
        lock (_gate)
        {
            if (!_entries.Remove(playerId, out var previous)) return ValueTask.CompletedTask;
            // 索引先行摘除：CollectAffected 看到的旧节点集合已不含本人。
            RemoveFromNodeIndex(previous.NodeId, playerId);
            affected = CollectAffected(previous.NodeId, null);
        }
        return PublishAsync(affected);
    }

    /// <summary>判断玩家当前是否在线。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <returns>在线返回 true。</returns>
    public bool IsOnline(Guid playerId)
    {
        lock (_gate) return _entries.ContainsKey(playerId);
    }

    /// <summary>读取在线玩家 ID 快照（世界频道广播收件人）。</summary>
    /// <returns>在线玩家 ID 列表。</returns>
    public IReadOnlyList<Guid> SnapshotOnlinePlayerIds()
    {
        lock (_gate) return [.. _entries.Keys];
    }

    /// <summary>读取指定节点上的在线修士（含道号），按道号排序；nodeId 为空时返回空列表。</summary>
    /// <param name="nodeId">地图节点 ID。</param>
    /// <returns>同节点在线修士列表。</returns>
    public IReadOnlyList<NearbyPlayerDto> SnapshotAtNode(string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return [];
        List<NearbyPlayerDto> result;
        lock (_gate)
        {
            // 倒排索引直达节点成员：锁内只做 O(节点人数) 的投影，不再全表扫描在线玩家。
            if (!_nodeIndex.TryGetValue(nodeId, out var members) || members.Count == 0) return [];
            result = new List<NearbyPlayerDto>(members.Count);
            foreach (var playerId in members)
            {
                // 索引与主表同锁维护，命中集合中的玩家必定仍登记在 _entries。
                var entry = _entries[playerId];
                result.Add(new NearbyPlayerDto(playerId.ToString("D"), entry.DisplayName, entry.RealmLevel, entry.Valuation));
            }
        }
        // 排序移出锁外：避免长时占锁阻塞上下线与换节点。
        result.Sort(static (left, right) => string.CompareOrdinal(left.DisplayName, right.DisplayName));
        return result;
    }

    /// <summary>读取玩家当前节点；未在线返回 null。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <returns>节点 ID 或 null。</returns>
    public string? GetNodeId(Guid playerId)
    {
        lock (_gate) return _entries.TryGetValue(playerId, out var entry) ? entry.NodeId : null;
    }

    /// <summary>把玩家挂到节点索引上（nodeId 为空时不索引）。</summary>
    private void AddToNodeIndex(string? nodeId, Guid playerId)
    {
        if (nodeId is null) return;
        if (!_nodeIndex.TryGetValue(nodeId, out var members))
            _nodeIndex[nodeId] = members = [];
        members.Add(playerId);
    }

    /// <summary>把玩家从节点索引摘除；集合腾空后删除节点键避免空桶累积。</summary>
    private void RemoveFromNodeIndex(string? nodeId, Guid playerId)
    {
        if (nodeId is null) return;
        if (!_nodeIndex.TryGetValue(nodeId, out var members)) return;
        members.Remove(playerId);
        if (members.Count == 0) _nodeIndex.Remove(nodeId);
    }

    /// <summary>在锁内计算受影响玩家：旧节点与新节点上的在线玩家，外加移动者本人。</summary>
    private List<Guid> CollectAffected(string? previousNode, string? currentNode)
    {
        var affected = new HashSet<Guid>();
        if (previousNode is not null && _nodeIndex.TryGetValue(previousNode, out var before))
            affected.UnionWith(before);
        if (currentNode is not null && _nodeIndex.TryGetValue(currentNode, out var after))
            affected.UnionWith(after);
        return [.. affected];
    }

    private async ValueTask PublishAsync(List<Guid> affected, Guid? movedPlayer = null)
    {
        // 附近集合为空不代表无事发生：移动者本人仍需经 NodeChanged 触发组队讨伐仲裁。
        if (affected.Count > 0)
        {
            var handler = PresenceChanged;
            if (handler is not null) await handler(affected);
        }
        if (movedPlayer is { } mover)
        {
            var nodeHandler = NodeChanged;
            if (nodeHandler is not null) await nodeHandler(mover);
        }
    }
}
