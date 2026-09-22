using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Domain.World;

/// <summary>世界地图节点（如宗门、村落、秘境洞府、灵脉矿区）。</summary>
public sealed record WorldNode(string Id, string Name, double X, double Y)
{
    /// <summary>节点栖息生态引用的遭遇 ID 列表；战斗行动按当前节点解析实际遭遇。</summary>
    public IReadOnlyList<string> EncounterIds { get; init; } = [];

    /// <summary>创建世界节点并校验标识与坐标有限性。</summary>
    /// <param name="id">节点唯一标识。</param>
    /// <param name="name">节点名称。</param>
    /// <param name="x">地图坐标 X。</param>
    /// <param name="y">地图坐标 Y。</param>
    /// <param name="encounterIds">节点生态遭遇 ID 列表，可为空。</param>
    /// <returns>通过校验的节点实例。</returns>
    public static WorldNode Create(string id, string name, double x, double y, IReadOnlyList<string>? encounterIds = null)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !double.IsFinite(x) || !double.IsFinite(y))
            throw new DomainException(DomainErrorCode.InvalidArgument, "世界节点无效。");
        return new WorldNode(id.Trim(), name.Trim(), x, y)
        {
            EncounterIds = encounterIds ?? []
        };
    }
}

/// <summary>世界两节点间的连通路径（有向/无向边），包含空间物理距离与通行耗时。</summary>
public sealed record WorldEdge(string Id, string FromNodeId, string ToNodeId, double Distance, double TravelSeconds)
{
    /// <summary>创建世界边并校验端点、距离与耗时；禁止自环。</summary>
    /// <param name="id">边唯一标识。</param>
    /// <param name="fromNodeId">起点节点 ID。</param>
    /// <param name="toNodeId">终点节点 ID。</param>
    /// <param name="distance">空间距离（正数）。</param>
    /// <param name="travelSeconds">通行耗时秒数（正数）。</param>
    /// <returns>通过校验的边实例。</returns>
    public static WorldEdge Create(string id, string fromNodeId, string toNodeId, double distance, double travelSeconds)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(fromNodeId) || string.IsNullOrWhiteSpace(toNodeId) ||
            fromNodeId == toNodeId || !double.IsFinite(distance) || distance <= 0 || !double.IsFinite(travelSeconds) || travelSeconds <= 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "世界边无效。");
        return new WorldEdge(id.Trim(), fromNodeId.Trim(), toNodeId.Trim(), distance, travelSeconds);
    }
}

/// <summary>拓扑出边信息，记录连接边标识、当前起点、终点、距离与耗时。</summary>
public sealed record WorldOutgoingEdge(string EdgeId, string FromNodeId, string ToNodeId, double Distance, double TravelSeconds);

/// <summary>旅行路径中的单个具体行进路段（支持双向通行与部分路段起止点）。</summary>
public sealed record TravelSegment(
    string EdgeId,
    string FromNodeId,
    string ToNodeId,
    double StartProgress,
    double EndProgress,
    double DurationSeconds)
{
    /// <summary>路段字段满足取值域约束（进度 0~1、耗时非负）。</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(EdgeId) &&
        !string.IsNullOrWhiteSpace(FromNodeId) &&
        !string.IsNullOrWhiteSpace(ToNodeId) &&
        double.IsFinite(StartProgress) && StartProgress >= 0 && StartProgress <= 1 &&
        double.IsFinite(EndProgress) && EndProgress >= 0 && EndProgress <= 1 &&
        double.IsFinite(DurationSeconds) && DurationSeconds >= 0;
}

/// <summary>队列移动行动的运行时状态，支持序列化落盘与跨帧平滑推进。</summary>
public sealed record TravelActionState(
    string TargetNodeId,
    double TotalDurationSeconds,
    IReadOnlyList<string> PathEdgeIds,
    IReadOnlyList<TravelSegment> Segments,
    int CurrentSegmentIndex = 0,
    double SegmentElapsedSeconds = 0,
    double ProgressSeconds = 0)
{
    /// <summary>移动状态满足持久化不变量（目标有效、进度非负、路段全部合法）。</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(TargetNodeId) &&
        double.IsFinite(TotalDurationSeconds) && TotalDurationSeconds >= 0 &&
        double.IsFinite(ProgressSeconds) && ProgressSeconds >= 0 &&
        double.IsFinite(SegmentElapsedSeconds) && SegmentElapsedSeconds >= 0 &&
        CurrentSegmentIndex >= 0 &&
        PathEdgeIds is not null &&
        Segments is not null &&
        Segments.All(s => s.IsValid);
}

/// <summary>
/// 纯内存世界拓扑图，提供邻接表管理与 Dijkstra 最短时间路径规划。
/// </summary>
public sealed class WorldGraph
{
    private readonly Dictionary<string, WorldNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorldEdge> _edges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<WorldOutgoingEdge>> _outgoing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string, string), TravelPlan> _planCache = new();

    /// <summary>全部已注册节点集合。</summary>
    public IReadOnlyCollection<WorldNode> Nodes => _nodes.Values;
    /// <summary>全部已注册边集合。</summary>
    public IReadOnlyCollection<WorldEdge> Edges => _edges.Values;

    /// <summary>向世界图中注册节点</summary>
    public void AddNode(WorldNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_nodes.TryAdd(node.Id, node)) throw new DomainException(DomainErrorCode.Duplicate, $"节点已存在：{node.Id}。");
        _outgoing[node.Id] = [];
        _planCache.Clear();
    }

    /// <summary>
    /// 向世界图中注册边，支持单向或双向，并按边 ID 维护出边的稳定顺序，消除寻路时的无序震荡。
    /// </summary>
    public void AddEdge(WorldEdge edge, bool bidirectional = false)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (!_nodes.ContainsKey(edge.FromNodeId) || !_nodes.ContainsKey(edge.ToNodeId))
            throw new DomainException(DomainErrorCode.NotFound, "世界边引用了不存在的节点。");
        if (!_edges.TryAdd(edge.Id, edge)) throw new DomainException(DomainErrorCode.Duplicate, $"世界边已存在：{edge.Id}。");

        var forwardList = _outgoing[edge.FromNodeId];
        forwardList.Add(new WorldOutgoingEdge(edge.Id, edge.FromNodeId, edge.ToNodeId, edge.Distance, edge.TravelSeconds));
        forwardList.Sort(static (a, b) => string.CompareOrdinal(a.EdgeId, b.EdgeId));

        if (bidirectional)
        {
            var reverseList = _outgoing[edge.ToNodeId];
            reverseList.Add(new WorldOutgoingEdge(edge.Id, edge.ToNodeId, edge.FromNodeId, edge.Distance, edge.TravelSeconds));
            reverseList.Sort(static (a, b) => string.CompareOrdinal(a.EdgeId, b.EdgeId));
        }

        _planCache.Clear();
    }

    /// <summary>向世界图中注册无向双向边</summary>
    public void AddUndirectedEdge(WorldEdge edge) => AddEdge(edge, bidirectional: true);

    /// <summary>节点是否存在</summary>
    public bool ContainsNode(string id) => _nodes.ContainsKey(id);

    /// <summary>安全获取节点</summary>
    public bool TryGetNode(string id, [NotNullWhen(true)] out WorldNode? node) => _nodes.TryGetValue(id, out node);

    /// <summary>获取节点（若不存在抛出 DomainException）</summary>
    public WorldNode GetNode(string id) => _nodes.TryGetValue(id, out var node) ? node : throw new DomainException(DomainErrorCode.NotFound, $"世界节点不存在：{id}。");

    /// <summary>边是否存在</summary>
    public bool ContainsEdge(string id) => _edges.ContainsKey(id);

    /// <summary>安全获取边</summary>
    public bool TryGetEdge(string id, [NotNullWhen(true)] out WorldEdge? edge) => _edges.TryGetValue(id, out edge);

    /// <summary>根据 ID 查找指定世界边</summary>
    public WorldEdge GetEdge(string id) => _edges.TryGetValue(id, out var edge) ? edge : throw new DomainException(DomainErrorCode.NotFound, $"世界边不存在：{id}。");

    /// <summary>
    /// 使用带优先队列的 Dijkstra 算法规划从起点到终点的最短耗时路径。
    /// 内部维护只读记忆化路径缓存，使高频重复寻路达到 O(1) 查表与零堆分配。
    /// </summary>
    public TravelPlan Plan(string fromNodeId, string toNodeId)
    {
        if (!_nodes.ContainsKey(fromNodeId) || !_nodes.ContainsKey(toNodeId)) throw new DomainException(DomainErrorCode.NotFound, "起点或终点不存在。");
        if (fromNodeId == toNodeId) return new TravelPlan(fromNodeId, toNodeId, [], 0, 0, []);
        if (_planCache.TryGetValue((fromNodeId, toNodeId), out var cached)) return cached;

        var distance = new Dictionary<string, double>(StringComparer.Ordinal) { [fromNodeId] = 0 };
        var previous = new Dictionary<string, WorldOutgoingEdge>(StringComparer.Ordinal);
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
        var legs = new List<WorldOutgoingEdge>();
        for (var current = toNodeId; current != fromNodeId; current = previous[current].FromNodeId) legs.Add(previous[current]);
        legs.Reverse();
        var edgeIds = new string[legs.Count];
        var segments = new List<TravelSegment>(legs.Count);
        var totalTravelSeconds = 0d;
        var totalDistance = 0d;
        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            edgeIds[i] = leg.EdgeId;
            totalTravelSeconds += leg.TravelSeconds;
            totalDistance += leg.Distance;
            var rawEdge = _edges[leg.EdgeId];
            var startProgress = string.Equals(leg.FromNodeId, rawEdge.FromNodeId, StringComparison.Ordinal) ? 0.0 : 1.0;
            var endProgress = string.Equals(leg.ToNodeId, rawEdge.ToNodeId, StringComparison.Ordinal) ? 1.0 : 0.0;
            segments.Add(new TravelSegment(leg.EdgeId, leg.FromNodeId, leg.ToNodeId, startProgress, endProgress, leg.TravelSeconds));
        }
        var plan = new TravelPlan(fromNodeId, toNodeId, edgeIds, totalTravelSeconds, totalDistance, segments);
        _planCache.TryAdd((fromNodeId, toNodeId), plan);
        return plan;
    }

    /// <summary>
    /// 从当前位置（支持处于某条边中间或处于节点上）规划到达目标节点的最短路径。
    /// 根据设计规范 §7.2：从边中间再次规划时，同时比较前往边两端的剩余时间，再选择总耗时更短的路线。
    /// </summary>
    public TravelPlan PlanFromPosition(string? edgeId, double edgeProgress, string currentNodeId, string targetNodeId)
    {
        if (string.IsNullOrWhiteSpace(targetNodeId)) throw new DomainException(DomainErrorCode.InvalidArgument, "目标节点无效。");
        if (!ContainsNode(targetNodeId)) throw new DomainException(DomainErrorCode.NotFound, $"目标节点不存在：{targetNodeId}。");

        // 若未处于边上，或处于非常靠近端点的位置，直接以节点为起点规划
        if (string.IsNullOrWhiteSpace(edgeId) || !TryGetEdge(edgeId, out var edge))
        {
            var startNode = ContainsNode(currentNodeId) ? currentNodeId : targetNodeId;
            return Plan(startNode, targetNodeId);
        }

        var clampedProgress = Math.Clamp(edgeProgress, 0, 1);
        if (clampedProgress <= 1e-6) return Plan(edge.FromNodeId, targetNodeId);
        if (clampedProgress >= 1.0 - 1e-6) return Plan(edge.ToNodeId, targetNodeId);

        // 如果目标节点就是当前边的某一端
        if (string.Equals(targetNodeId, edge.FromNodeId, StringComparison.Ordinal))
        {
            var reverseDuration = edge.TravelSeconds * clampedProgress;
            var dist = edge.Distance * clampedProgress;
            var seg = new TravelSegment(edge.Id, edge.ToNodeId, edge.FromNodeId, clampedProgress, 0.0, reverseDuration);
            return new TravelPlan(edge.ToNodeId, targetNodeId, [edge.Id], reverseDuration, dist, [seg]);
        }
        if (string.Equals(targetNodeId, edge.ToNodeId, StringComparison.Ordinal))
        {
            var forwardDuration = edge.TravelSeconds * (1.0 - clampedProgress);
            var dist = edge.Distance * (1.0 - clampedProgress);
            var seg = new TravelSegment(edge.Id, edge.FromNodeId, edge.ToNodeId, clampedProgress, 1.0, forwardDuration);
            return new TravelPlan(edge.FromNodeId, targetNodeId, [edge.Id], forwardDuration, dist, [seg]);
        }

        // 分别比较前往 edge.FromNodeId 与 edge.ToNodeId 哪条总路线耗时更短
        var reverseDelay = edge.TravelSeconds * clampedProgress;
        var forwardDelay = edge.TravelSeconds * (1.0 - clampedProgress);

        TravelPlan? planFrom = null;
        try { planFrom = Plan(edge.FromNodeId, targetNodeId); } catch (DomainException) { }

        TravelPlan? planTo = null;
        try { planTo = Plan(edge.ToNodeId, targetNodeId); } catch (DomainException) { }

        if (planFrom is null && planTo is null)
            throw new DomainException(DomainErrorCode.NotFound, "无法到达目标地点。");

        var timeViaFrom = planFrom is not null ? reverseDelay + planFrom.TotalTravelSeconds : double.PositiveInfinity;
        var timeViaTo = planTo is not null ? forwardDelay + planTo.TotalTravelSeconds : double.PositiveInfinity;

        if (timeViaTo <= timeViaFrom && planTo is not null)
        {
            var firstSeg = new TravelSegment(edge.Id, edge.FromNodeId, edge.ToNodeId, clampedProgress, 1.0, forwardDelay);
            var edgeIds = new List<string>(planTo.EdgeIds.Count + 1) { edge.Id };
            edgeIds.AddRange(planTo.EdgeIds);
            var segs = new List<TravelSegment>(planTo.Segments.Count + 1) { firstSeg };
            segs.AddRange(planTo.Segments);
            var totalDist = edge.Distance * (1.0 - clampedProgress) + planTo.TotalDistance;
            return new TravelPlan(edge.FromNodeId, targetNodeId, edgeIds, timeViaTo, totalDist, segs);
        }
        else if (planFrom is not null)
        {
            var firstSeg = new TravelSegment(edge.Id, edge.ToNodeId, edge.FromNodeId, clampedProgress, 0.0, reverseDelay);
            var edgeIds = new List<string>(planFrom.EdgeIds.Count + 1) { edge.Id };
            edgeIds.AddRange(planFrom.EdgeIds);
            var segs = new List<TravelSegment>(planFrom.Segments.Count + 1) { firstSeg };
            segs.AddRange(planFrom.Segments);
            var totalDist = edge.Distance * clampedProgress + planFrom.TotalDistance;
            return new TravelPlan(edge.ToNodeId, targetNodeId, edgeIds, timeViaFrom, totalDist, segs);
        }

        throw new DomainException(DomainErrorCode.NotFound, "无法到达目标地点。");
    }
}

/// <summary>世界漫游路径方案，记录有序的边列表与总耗时/总里程及路段进度。</summary>
public sealed record TravelPlan(
    string FromNodeId,
    string ToNodeId,
    IReadOnlyList<string> EdgeIds,
    double TotalTravelSeconds,
    double TotalDistance,
    IReadOnlyList<TravelSegment>? Segments = null)
{
    /// <summary>途经路段明细（含边内起止进度与耗时）。</summary>
    public IReadOnlyList<TravelSegment> Segments { get; init; } = Segments ?? [];
}
