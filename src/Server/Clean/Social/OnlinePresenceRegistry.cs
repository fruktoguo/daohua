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

    /// <summary>存在感变化事件：参数为附近列表可能发生变化的玩家 ID 集合。</summary>
    public event Func<IReadOnlyList<Guid>, ValueTask>? PresenceChanged;

    private readonly record struct Entry(string DisplayName, string? NodeId);

    /// <summary>登记或刷新在线玩家；节点变化时返回受影响的玩家集合。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="displayName">玩家道号。</param>
    /// <param name="nodeId">当前地图节点 ID；行进在边上时为 null。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public ValueTask RegisterAsync(Guid playerId, string displayName, string? nodeId, CancellationToken cancellationToken = default)
    {
        List<Guid> affected;
        lock (_gate)
        {
            _entries.TryGetValue(playerId, out var previous);
            _entries[playerId] = new Entry(displayName, nodeId);
            affected = CollectAffected(previous.NodeId, nodeId);
        }
        return PublishAsync(affected);
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
            affected = CollectAffected(previous.NodeId, nodeId);
        }
        return PublishAsync(affected);
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
            affected = CollectAffected(previous.NodeId, null);
            affected.Remove(playerId);
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
        lock (_gate)
        {
            var result = new List<NearbyPlayerDto>();
            foreach (var pair in _entries)
            {
                if (pair.Value.NodeId == nodeId)
                    result.Add(new NearbyPlayerDto(pair.Key.ToString("D"), pair.Value.DisplayName));
            }
            result.Sort(static (left, right) => string.CompareOrdinal(left.DisplayName, right.DisplayName));
            return result;
        }
    }

    /// <summary>读取玩家当前节点；未在线返回 null。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <returns>节点 ID 或 null。</returns>
    public string? GetNodeId(Guid playerId)
    {
        lock (_gate) return _entries.TryGetValue(playerId, out var entry) ? entry.NodeId : null;
    }

    /// <summary>在锁内计算受影响玩家：旧节点与新节点上的在线玩家，外加移动者本人。</summary>
    private List<Guid> CollectAffected(string? previousNode, string? currentNode)
    {
        var affected = new HashSet<Guid>();
        foreach (var pair in _entries)
        {
            if (pair.Value.NodeId is null) continue;
            if (pair.Value.NodeId == previousNode || pair.Value.NodeId == currentNode) affected.Add(pair.Key);
        }
        return [.. affected];
    }

    private async ValueTask PublishAsync(List<Guid> affected)
    {
        if (affected.Count == 0) return;
        var handler = PresenceChanged;
        if (handler is not null) await handler(affected);
    }
}
