namespace Domain.World;

/// <summary>世界地图节点（如宗门、村落、秘境洞府、灵脉矿区）。</summary>
public sealed record WorldNode(string Id, string Name, double X, double Y)
{
    public static WorldNode Create(string id, string name, double x, double y)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !double.IsFinite(x) || !double.IsFinite(y))
            throw new DomainException(DomainErrorCode.InvalidArgument, "世界节点无效。");
        return new WorldNode(id.Trim(), name.Trim(), x, y);
    }
}

/// <summary>世界两节点间的连通路径（有向边），包含空间物理距离与通行耗时。</summary>
public sealed record WorldEdge(string Id, string FromNodeId, string ToNodeId, double Distance, double TravelSeconds)
{
    public static WorldEdge Create(string id, string fromNodeId, string toNodeId, double distance, double travelSeconds)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(fromNodeId) || string.IsNullOrWhiteSpace(toNodeId) ||
            fromNodeId == toNodeId || !double.IsFinite(distance) || distance <= 0 || !double.IsFinite(travelSeconds) || travelSeconds <= 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "世界边无效。");
        return new WorldEdge(id.Trim(), fromNodeId.Trim(), toNodeId.Trim(), distance, travelSeconds);
    }
}

/// <summary>
/// 纯内存世界拓扑图，提供邻接表管理与 Dijkstra 最短时间路径规划。
/// </summary>
public sealed class WorldGraph
{
    private readonly Dictionary<string, WorldNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorldEdge> _edges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<WorldEdge>> _outgoing = new(StringComparer.Ordinal);

    public IReadOnlyCollection<WorldNode> Nodes => _nodes.Values;
    public IReadOnlyCollection<WorldEdge> Edges => _edges.Values;

    /// <summary>向世界图中注册节点</summary>
    public void AddNode(WorldNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_nodes.TryAdd(node.Id, node)) throw new DomainException(DomainErrorCode.Duplicate, $"节点已存在：{node.Id}。");
        _outgoing[node.Id] = [];
    }

    /// <summary>
    /// 向世界图中注册有向边，并按边 ID 维护出边的稳定顺序，消除寻路时的无序震荡。
    /// </summary>
    public void AddEdge(WorldEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (!_nodes.ContainsKey(edge.FromNodeId) || !_nodes.ContainsKey(edge.ToNodeId))
            throw new DomainException(DomainErrorCode.NotFound, "世界边引用了不存在的节点。");
        if (!_edges.TryAdd(edge.Id, edge)) throw new DomainException(DomainErrorCode.Duplicate, $"世界边已存在：{edge.Id}。");
        var list = _outgoing[edge.FromNodeId];
        list.Add(edge);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
    }

    /// <summary>
    /// 使用带优先队列的 Dijkstra 算法规划从起点到终点的最短耗时路径。
    /// </summary>
    public TravelPlan Plan(string fromNodeId, string toNodeId)
    {
        if (!_nodes.ContainsKey(fromNodeId) || !_nodes.ContainsKey(toNodeId)) throw new DomainException(DomainErrorCode.NotFound, "起点或终点不存在。");
        if (fromNodeId == toNodeId) return new TravelPlan(fromNodeId, toNodeId, [], 0, 0);
        var distance = new Dictionary<string, double>(StringComparer.Ordinal) { [fromNodeId] = 0 };
        var previous = new Dictionary<string, WorldEdge>(StringComparer.Ordinal);
        var queue = new PriorityQueue<string, double>();
        queue.Enqueue(fromNodeId, 0);
        while (queue.TryDequeue(out var current, out var currentDistance))
        {
            if (current == toNodeId) break;
            if (currentDistance > distance[current] + 1e-12) continue;
            var edgeList = _outgoing[current];
            for (var i = 0; i < edgeList.Count; i++)
            {
                var edge = edgeList[i];
                var nextDistance = currentDistance + edge.TravelSeconds;
                if (!distance.TryGetValue(edge.ToNodeId, out var known) || nextDistance < known - 1e-12)
                {
                    distance[edge.ToNodeId] = nextDistance;
                    previous[edge.ToNodeId] = edge;
                    queue.Enqueue(edge.ToNodeId, nextDistance);
                }
            }
        }
        if (!distance.ContainsKey(toNodeId)) throw new DomainException(DomainErrorCode.NotFound, "节点之间不存在可行路径。");
        var edges = new List<WorldEdge>();
        for (var current = toNodeId; current != fromNodeId; current = previous[current].FromNodeId) edges.Add(previous[current]);
        edges.Reverse();
        var edgeIds = new string[edges.Count];
        var totalTravelSeconds = 0d;
        var totalDistance = 0d;
        for (var i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            edgeIds[i] = edge.Id;
            totalTravelSeconds += edge.TravelSeconds;
            totalDistance += edge.Distance;
        }
        return new TravelPlan(fromNodeId, toNodeId, edgeIds, totalTravelSeconds, totalDistance);
    }

    /// <summary>根据 ID 查找指定世界边</summary>
    public WorldEdge GetEdge(string id) => _edges.TryGetValue(id, out var edge) ? edge : throw new DomainException(DomainErrorCode.NotFound, $"世界边不存在：{id}。");
}

/// <summary>世界漫游路径方案，记录有序的边列表与总耗时/总里程。</summary>
public sealed record TravelPlan(string FromNodeId, string ToNodeId, IReadOnlyList<string> EdgeIds, double TotalTravelSeconds, double TotalDistance);

/// <summary>角色沿路径行进过程中的动态进度状态机。</summary>
public sealed class TravelProgress
{
    public TravelProgress(TravelPlan plan)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    /// <summary>所依附的路径规划方案</summary>
    public TravelPlan Plan { get; }
    /// <summary>已完全走过的边段数量</summary>
    public int CompletedEdges { get; internal set; }
    /// <summary>当前所在边段内已推进的耗时秒数</summary>
    public double CurrentEdgeElapsedSeconds { get; internal set; }
    /// <summary>整个路径累计已推进的总耗时秒数</summary>
    public double ElapsedSeconds { get; internal set; }
    /// <summary>是否已抵达最终终点</summary>
    public bool Completed => CompletedEdges >= Plan.EdgeIds.Count;
}

/// <summary>路径行进单步推进结果帧。</summary>
public sealed record TravelAdvanceResult(
    string CurrentNodeId,
    double ElapsedSeconds,
    double DistanceTravelled,
    bool Completed,
    int CompletedEdges);

/// <summary>
/// 纯确定性路径漫游步进引擎。
/// </summary>
public static class TravelEngine
{
    /// <summary>
    /// 推进角色旅行进度 deltaSeconds 秒，逐边更新当前所在节点与完成标志。
    /// </summary>
    public static TravelAdvanceResult Advance(WorldGraph graph, TravelProgress progress, double deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(progress);
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0) throw new DomainException(DomainErrorCode.InvalidArgument, "旅行推进时长无效。");
        var remaining = deltaSeconds;
        while (remaining > 0 && !progress.Completed)
        {
            var edge = graph.GetEdge(progress.Plan.EdgeIds[progress.CompletedEdges]);
            var edgeRemaining = edge.TravelSeconds - progress.CurrentEdgeElapsedSeconds;
            var step = Math.Min(remaining, edgeRemaining);
            progress.CurrentEdgeElapsedSeconds += step;
            progress.ElapsedSeconds += step;
            remaining -= step;
            if (progress.CurrentEdgeElapsedSeconds + 1e-12 >= edge.TravelSeconds)
            {
                progress.CompletedEdges += 1;
                progress.CurrentEdgeElapsedSeconds = 0;
            }
        }
        var distance = progress.Plan.TotalTravelSeconds <= 0
            ? progress.Plan.TotalDistance
            : progress.Plan.TotalDistance * Math.Clamp(progress.ElapsedSeconds / progress.Plan.TotalTravelSeconds, 0, 1);
        var current = progress.Completed
            ? progress.Plan.ToNodeId
            : graph.GetEdge(progress.Plan.EdgeIds[progress.CompletedEdges]).FromNodeId;
        return new TravelAdvanceResult(current, progress.ElapsedSeconds, distance, progress.Completed, progress.CompletedEdges);
    }
}

