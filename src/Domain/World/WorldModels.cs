namespace Domain.World;

public sealed record WorldNode(string Id, string Name, double X, double Y)
{
    public static WorldNode Create(string id, string name, double x, double y)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !double.IsFinite(x) || !double.IsFinite(y))
            throw new DomainException(DomainErrorCode.InvalidArgument, "世界节点无效。");
        return new WorldNode(id.Trim(), name.Trim(), x, y);
    }
}

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

public sealed class WorldGraph
{
    private readonly Dictionary<string, WorldNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorldEdge> _edges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<WorldEdge>> _outgoing = new(StringComparer.Ordinal);

    public IReadOnlyCollection<WorldNode> Nodes => _nodes.Values;
    public IReadOnlyCollection<WorldEdge> Edges => _edges.Values;

    public void AddNode(WorldNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_nodes.TryAdd(node.Id, node)) throw new DomainException(DomainErrorCode.Duplicate, $"节点已存在：{node.Id}。");
        _outgoing[node.Id] = [];
    }

    public void AddEdge(WorldEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (!_nodes.ContainsKey(edge.FromNodeId) || !_nodes.ContainsKey(edge.ToNodeId))
            throw new DomainException(DomainErrorCode.NotFound, "世界边引用了不存在的节点。");
        if (!_edges.TryAdd(edge.Id, edge)) throw new DomainException(DomainErrorCode.Duplicate, $"世界边已存在：{edge.Id}。");
        _outgoing[edge.FromNodeId].Add(edge);
    }

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
            foreach (var edge in _outgoing[current].OrderBy(edge => edge.Id, StringComparer.Ordinal))
            {
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
        return new TravelPlan(fromNodeId, toNodeId, edges.Select(edge => edge.Id).ToArray(), edges.Sum(edge => edge.TravelSeconds), edges.Sum(edge => edge.Distance));
    }

    public WorldEdge GetEdge(string id) => _edges.TryGetValue(id, out var edge) ? edge : throw new DomainException(DomainErrorCode.NotFound, $"世界边不存在：{id}。");
}

public sealed record TravelPlan(string FromNodeId, string ToNodeId, IReadOnlyList<string> EdgeIds, double TotalTravelSeconds, double TotalDistance);

public sealed class TravelProgress
{
    public TravelProgress(TravelPlan plan)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    public TravelPlan Plan { get; }
    public int CompletedEdges { get; internal set; }
    public double CurrentEdgeElapsedSeconds { get; internal set; }
    public double ElapsedSeconds { get; internal set; }
    public bool Completed => CompletedEdges >= Plan.EdgeIds.Count;
}

public sealed record TravelAdvanceResult(
    string CurrentNodeId,
    double ElapsedSeconds,
    double DistanceTravelled,
    bool Completed,
    int CompletedEdges);

public static class TravelEngine
{
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
