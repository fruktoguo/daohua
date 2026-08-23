// ==============================================================================
// 文件名称: WorldCatalog.cs
// 主要职责: 修真大世界大地图目录与寻路拓扑管理类。
// 核心逻辑 / 定义内容:
//   1. 大地图、地点节点（Node）与连线路径（Edge）拓扑配置检索。
//   2. 玩家大地图位置状态归一化（NormalizeWorldState）与移动进度推进。
//   3. 基于 Dijkstra 算法的最短路径寻路与坐标插值计算。
// ==============================================================================

using Domain.GameData;
using Domain.Models;


namespace Domain.Simulation;

/// <summary>
/// 修真大世界大地图目录与寻路拓扑管理类。
/// 负责地图节点、路径边检索、位置坐标插值、Dijktra 最短路径规划与移动进度推进。
/// </summary>
public static class WorldCatalog
{
    private const double PositionEpsilon = 0.000_001;

    /// <summary>获取所有大地图定义。</summary>
    public static IReadOnlyCollection<WorldMapDefinition> All => GameDataCatalog.Current.Maps.Values.ToArray();

    /// <summary>
    /// 根据地图 ID 获取地图拓扑定义。
    /// </summary>
    /// <param name="mapId">地图 ID。</param>
    /// <returns>地图定义对象。</returns>
    /// <exception cref="KeyNotFoundException">当地图 ID 不存在时抛出。</exception>
    public static WorldMapDefinition GetMap(string mapId)
    {
        return GameDataCatalog.Current.Maps.TryGetValue(mapId, out var map)
            ? map
            : throw new KeyNotFoundException($"未知地图: {mapId}");
    }

    /// <summary>
    /// 获取指定地图下的特定地点节点。
    /// </summary>
    /// <param name="mapId">地图 ID。</param>
    /// <param name="nodeId">节点 ID。</param>
    /// <returns>节点原型对象。</returns>
    /// <exception cref="KeyNotFoundException">当节点不存在时抛出。</exception>
    public static WorldNodeDefinition GetNode(string mapId, string nodeId)
    {
        return GetMap(mapId).Nodes.FirstOrDefault(node => node.Id == nodeId)
            ?? throw new KeyNotFoundException($"未知地图节点: {nodeId}");
    }

    /// <summary>
    /// 获取指定地图下的特定路径边。
    /// </summary>
    /// <param name="mapId">地图 ID。</param>
    /// <param name="edgeId">连线边 ID。</param>
    /// <returns>路径边原型对象。</returns>
    /// <exception cref="KeyNotFoundException">当路径边不存在时抛出。</exception>
    public static WorldEdgeDefinition GetEdge(string mapId, string edgeId)
    {
        return GetMap(mapId).Edges.FirstOrDefault(edge => edge.Id == edgeId)
            ?? throw new KeyNotFoundException($"未知地图连线: {edgeId}");
    }

    /// <summary>
    /// 归一化校验玩家大地图位置状态（纠正非法地图、孤立边或到达端点时落入节点）。
    /// </summary>
    /// <param name="world">玩家大地图位置状态。</param>
    /// <returns>如果有任何字段被修复则返回 true，否则返回 false。</returns>
    public static bool Normalize(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var changed = false;
        if (!GameDataCatalog.Current.Maps.TryGetValue(world.MapId, out var map))
        {
            world.MapId = WorldMapIds.QingyunMountains;
            map = GetMap(world.MapId);
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(world.NodeId)
            && map.Nodes.Any(node => node.Id == world.NodeId))
        {
            if (world.EdgeId is not null || world.EdgeProgress != 0)
            {
                world.EdgeId = null;
                world.EdgeProgress = 0;
                changed = true;
            }

            return changed;
        }

        if (!string.IsNullOrWhiteSpace(world.EdgeId)
            && map.Edges.FirstOrDefault(edge => edge.Id == world.EdgeId) is { } edge)
        {
            var progress = Math.Clamp(double.IsFinite(world.EdgeProgress) ? world.EdgeProgress : 0, 0, 1);
            if (progress <= PositionEpsilon)
            {
                world.NodeId = edge.FromNodeId;
                world.EdgeId = null;
                world.EdgeProgress = 0;
                return true;
            }

            if (progress >= 1 - PositionEpsilon)
            {
                world.NodeId = edge.ToNodeId;
                world.EdgeId = null;
                world.EdgeProgress = 0;
                return true;
            }

            if (world.NodeId is not null || Math.Abs(world.EdgeProgress - progress) > PositionEpsilon)
            {
                world.NodeId = null;
                world.EdgeProgress = progress;
                changed = true;
            }

            return changed;
        }

        world.NodeId = WorldNodeIds.YunxiVillage;
        world.EdgeId = null;
        world.EdgeProgress = 0;
        return true;
    }

    /// <summary>
    /// 校验玩家当前所处地点是否允许执行特定行动。
    /// </summary>
    /// <param name="world">玩家世界位置状态。</param>
    /// <param name="action">待执行的行动原型。</param>
    /// <returns>允许执行返回 true，否则返回 false。</returns>
    public static bool CanPerformAction(WorldState world, ActionDefinition action)
    {
        if (action.AvailableNodeIds.Count == 0) return true;
        return world.NodeId is not null && action.AvailableNodeIds.Contains(world.NodeId);
    }

    /// <summary>
    /// 获取玩家当前所在节点的修炼灵气倍率加成（如福地洞天增益）。
    /// </summary>
    /// <param name="world">玩家世界位置状态。</param>
    /// <returns>修炼倍率系数（在路径上时保底为 1.0）。</returns>
    public static double GetCultivationMultiplier(WorldState world)
    {
        if (world.NodeId is null) return 1;
        return GetNode(world.MapId, world.NodeId).CultivationMultiplier;
    }

    /// <summary>
    /// 获取玩家在当前大地图坐标系中的二维位置 (X, Y)（包含在路径边上的插值计算）。
    /// </summary>
    /// <param name="world">玩家世界位置状态。</param>
    /// <returns>二维坐标元组。</returns>
    public static (double X, double Y) GetPosition(WorldState world)
    {
        if (world.NodeId is not null)
        {
            var node = GetNode(world.MapId, world.NodeId);
            return (node.X, node.Y);
        }

        if (world.EdgeId is null) return (0, 0);
        var edge = GetEdge(world.MapId, world.EdgeId);
        var from = GetNode(world.MapId, edge.FromNodeId);
        var to = GetNode(world.MapId, edge.ToNodeId);
        var progress = Math.Clamp(world.EdgeProgress, 0, 1);
        return (
            from.X + (to.X - from.X) * progress,
            from.Y + (to.Y - from.Y) * progress);
    }

    /// <summary>
    /// 尝试规划从当前位置（节点或路径中间）前往目标地点的最短时间路径方案。
    /// </summary>
    /// <param name="world">玩家当前世界位置。</param>
    /// <param name="destinationNodeId">目标目的地节点 ID。</param>
    /// <param name="plan">输出规划好的旅行方案。</param>
    /// <returns>规划成功返回 true，目的地不存在或已在目的地则返回 false。</returns>
    public static bool TryPlanTravel(
        WorldState world,
        string destinationNodeId,
        out TravelPlan plan)
    {
        plan = null!;
        var map = GetMap(world.MapId);
        if (map.Nodes.All(node => node.Id != destinationNodeId)) return false;
        if (world.NodeId == destinationNodeId) return false;

        var candidates = CreateStartCandidates(world, map);
        TravelCandidate? best = null;
        foreach (var candidate in candidates)
        {
            if (!TryFindNodePath(map, candidate.NodeId, destinationNodeId, out var pathEdges)) continue;
            var segments = candidate.InitialSegments.Select(segment => segment.Clone()).ToList();
            var cursorNodeId = candidate.NodeId;
            foreach (var edge in pathEdges)
            {
                var forward = edge.FromNodeId == cursorNodeId;
                segments.Add(new TravelSegment
                {
                    EdgeId = edge.Id,
                    StartProgress = forward ? 0 : 1,
                    EndProgress = forward ? 1 : 0,
                    DurationSeconds = edge.DurationSeconds
                });
                cursorNodeId = forward ? edge.ToNodeId : edge.FromNodeId;
            }

            var duration = segments.Sum(segment => segment.DurationSeconds);
            if (duration <= 0) continue;
            var signature = string.Join('|', segments.Select(segment => $"{segment.EdgeId}:{segment.EndProgress:0}"));
            if (best is null
                || duration < best.DurationSeconds
                || (duration == best.DurationSeconds && string.CompareOrdinal(signature, best.Signature) < 0))
            {
                best = new TravelCandidate(candidate.NodeId, segments, duration, signature);
            }
        }

        if (best is null) return false;
        plan = new TravelPlan
        {
            MapId = world.MapId,
            DestinationNodeId = destinationNodeId,
            TotalDurationSeconds = best.DurationSeconds,
            Segments = best.InitialSegments.Select(segment => segment.Clone()).ToList()
        };
        return true;
    }

    /// <summary>
    /// 根据已过去的时间秒数，计算并应用玩家在路线方案上的实时位置。
    /// </summary>
    /// <param name="world">玩家世界位置状态。</param>
    /// <param name="plan">正在执行的旅行方案。</param>
    /// <param name="elapsedSeconds">该赶路行动已进行秒数。</param>
    public static void ApplyTravelProgress(WorldState world, TravelPlan plan, int elapsedSeconds)
    {

        if (plan.Segments.Count == 0 || elapsedSeconds >= plan.TotalDurationSeconds)
        {
            world.MapId = plan.MapId;
            world.NodeId = plan.DestinationNodeId;
            world.EdgeId = null;
            world.EdgeProgress = 0;
            return;
        }

        var remaining = Math.Max(0, elapsedSeconds);
        foreach (var segment in plan.Segments)
        {
            if (remaining >= segment.DurationSeconds)
            {
                remaining -= segment.DurationSeconds;
                SetSegmentEnd(world, plan.MapId, segment);
                continue;
            }

            var localProgress = segment.DurationSeconds <= 0
                ? 1
                : remaining / (double)segment.DurationSeconds;
            world.MapId = plan.MapId;
            world.NodeId = null;
            world.EdgeId = segment.EdgeId;
            world.EdgeProgress = segment.StartProgress
                + (segment.EndProgress - segment.StartProgress) * localProgress;
            return;
        }

        world.MapId = plan.MapId;
        world.NodeId = plan.DestinationNodeId;
        world.EdgeId = null;
        world.EdgeProgress = 0;
    }

    private static IReadOnlyList<TravelCandidate> CreateStartCandidates(
        WorldState world,
        WorldMapDefinition map)
    {
        if (world.NodeId is not null)
        {
            return [new TravelCandidate(world.NodeId, [], 0, world.NodeId)];
        }

        if (world.EdgeId is null) return [];
        var edge = map.Edges.FirstOrDefault(item => item.Id == world.EdgeId);
        if (edge is null) return [];
        var progress = Math.Clamp(world.EdgeProgress, 0, 1);
        var candidates = new List<TravelCandidate>(2);
        AddPartialCandidate(candidates, edge, edge.FromNodeId, progress, 0);
        AddPartialCandidate(candidates, edge, edge.ToNodeId, progress, 1);
        return candidates;
    }

    private static void AddPartialCandidate(
        ICollection<TravelCandidate> candidates,
        WorldEdgeDefinition edge,
        string endpointNodeId,
        double startProgress,
        double endProgress)
    {
        var distanceRatio = Math.Abs(endProgress - startProgress);
        if (distanceRatio <= PositionEpsilon)
        {
            candidates.Add(new TravelCandidate(endpointNodeId, [], 0, endpointNodeId));
            return;
        }

        var duration = Math.Max(1, (int)Math.Ceiling(edge.DurationSeconds * distanceRatio));
        var segment = new TravelSegment
        {
            EdgeId = edge.Id,
            StartProgress = startProgress,
            EndProgress = endProgress,
            DurationSeconds = duration
        };
        candidates.Add(new TravelCandidate(endpointNodeId, [segment], duration, $"{edge.Id}:{endProgress:0}"));
    }

    private static bool TryFindNodePath(
        WorldMapDefinition map,
        string startNodeId,
        string destinationNodeId,
        out IReadOnlyList<WorldEdgeDefinition> path)
    {
        path = [];
        if (startNodeId == destinationNodeId) return true;

        var distances = map.Nodes.ToDictionary(node => node.Id, _ => int.MaxValue, StringComparer.Ordinal);
        var previous = new Dictionary<string, (string NodeId, WorldEdgeDefinition Edge)>(StringComparer.Ordinal);
        var queue = new PriorityQueue<string, (int Cost, string NodeId)>();
        distances[startNodeId] = 0;
        queue.Enqueue(startNodeId, (0, startNodeId));

        while (queue.TryDequeue(out var nodeId, out var priority))
        {
            if (priority.Cost != distances[nodeId]) continue;
            if (nodeId == destinationNodeId) break;
            foreach (var edge in map.Edges
                         .Where(item => item.FromNodeId == nodeId || item.ToNodeId == nodeId)
                         .OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                var nextNodeId = edge.FromNodeId == nodeId ? edge.ToNodeId : edge.FromNodeId;
                var nextCost = checked(priority.Cost + edge.DurationSeconds);
                if (nextCost >= distances[nextNodeId]) continue;
                distances[nextNodeId] = nextCost;
                previous[nextNodeId] = (nodeId, edge);
                queue.Enqueue(nextNodeId, (nextCost, nextNodeId));
            }
        }

        if (distances.GetValueOrDefault(destinationNodeId, int.MaxValue) == int.MaxValue) return false;
        var reversed = new List<WorldEdgeDefinition>();
        var cursor = destinationNodeId;
        while (cursor != startNodeId)
        {
            if (!previous.TryGetValue(cursor, out var step)) return false;
            reversed.Add(step.Edge);
            cursor = step.NodeId;
        }

        reversed.Reverse();
        path = reversed;
        return true;
    }

    private static void SetSegmentEnd(
        WorldState world,
        string mapId,
        TravelSegment segment)
    {
        var edge = GetEdge(mapId, segment.EdgeId);
        world.MapId = mapId;
        world.NodeId = segment.EndProgress <= PositionEpsilon ? edge.FromNodeId : edge.ToNodeId;
        world.EdgeId = null;
        world.EdgeProgress = 0;
    }

    private sealed record TravelCandidate(
        string NodeId,
        IReadOnlyList<TravelSegment> InitialSegments,
        int DurationSeconds,
        string Signature);
}

