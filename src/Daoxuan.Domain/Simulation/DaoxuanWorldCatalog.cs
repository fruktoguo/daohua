using Daoxuan.Domain.GameData;
using Daoxuan.Domain.Models;

namespace Daoxuan.Domain.Simulation;

public static class DaoxuanWorldCatalog
{
    private const double PositionEpsilon = 0.000_001;

    public static IReadOnlyCollection<DaoxuanWorldMapDefinition> All => DaoxuanGameData.Current.Maps.Values.ToArray();

    public static DaoxuanWorldMapDefinition GetMap(string mapId)
    {
        return DaoxuanGameData.Current.Maps.TryGetValue(mapId, out var map)
            ? map
            : throw new KeyNotFoundException($"未知地图: {mapId}");
    }

    public static DaoxuanWorldNodeDefinition GetNode(string mapId, string nodeId)
    {
        return GetMap(mapId).Nodes.FirstOrDefault(node => node.Id == nodeId)
            ?? throw new KeyNotFoundException($"未知地图节点: {nodeId}");
    }

    public static DaoxuanWorldEdgeDefinition GetEdge(string mapId, string edgeId)
    {
        return GetMap(mapId).Edges.FirstOrDefault(edge => edge.Id == edgeId)
            ?? throw new KeyNotFoundException($"未知地图连线: {edgeId}");
    }

    public static bool Normalize(DaoxuanWorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var changed = false;
        if (!DaoxuanGameData.Current.Maps.TryGetValue(world.MapId, out var map))
        {
            world.MapId = DaoxuanWorldMapIds.QingyunMountains;
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

        world.NodeId = DaoxuanWorldNodeIds.HerbVale;
        world.EdgeId = null;
        world.EdgeProgress = 0;
        return true;
    }

    public static bool CanPerformAction(DaoxuanWorldState world, DaoxuanActionDefinition action)
    {
        if (action.AvailableNodeIds.Count == 0) return true;
        return world.NodeId is not null && action.AvailableNodeIds.Contains(world.NodeId);
    }

    public static double GetCultivationMultiplier(DaoxuanWorldState world)
    {
        if (world.NodeId is null) return 1;
        return GetNode(world.MapId, world.NodeId).CultivationMultiplier;
    }

    public static (double X, double Y) GetPosition(DaoxuanWorldState world)
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

    public static bool TryPlanTravel(
        DaoxuanWorldState world,
        string destinationNodeId,
        out DaoxuanTravelPlan plan)
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
                segments.Add(new DaoxuanTravelSegment
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
        plan = new DaoxuanTravelPlan
        {
            MapId = world.MapId,
            DestinationNodeId = destinationNodeId,
            TotalDurationSeconds = best.DurationSeconds,
            Segments = best.InitialSegments.Select(segment => segment.Clone()).ToList()
        };
        return true;
    }

    public static void ApplyTravelProgress(DaoxuanWorldState world, DaoxuanTravelPlan plan, int elapsedSeconds)
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
        DaoxuanWorldState world,
        DaoxuanWorldMapDefinition map)
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
        DaoxuanWorldEdgeDefinition edge,
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
        var segment = new DaoxuanTravelSegment
        {
            EdgeId = edge.Id,
            StartProgress = startProgress,
            EndProgress = endProgress,
            DurationSeconds = duration
        };
        candidates.Add(new TravelCandidate(endpointNodeId, [segment], duration, $"{edge.Id}:{endProgress:0}"));
    }

    private static bool TryFindNodePath(
        DaoxuanWorldMapDefinition map,
        string startNodeId,
        string destinationNodeId,
        out IReadOnlyList<DaoxuanWorldEdgeDefinition> path)
    {
        path = [];
        if (startNodeId == destinationNodeId) return true;

        var distances = map.Nodes.ToDictionary(node => node.Id, _ => int.MaxValue, StringComparer.Ordinal);
        var previous = new Dictionary<string, (string NodeId, DaoxuanWorldEdgeDefinition Edge)>(StringComparer.Ordinal);
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
        var reversed = new List<DaoxuanWorldEdgeDefinition>();
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
        DaoxuanWorldState world,
        string mapId,
        DaoxuanTravelSegment segment)
    {
        var edge = GetEdge(mapId, segment.EdgeId);
        world.MapId = mapId;
        world.NodeId = segment.EndProgress <= PositionEpsilon ? edge.FromNodeId : edge.ToNodeId;
        world.EdgeId = null;
        world.EdgeProgress = 0;
    }

    private sealed record TravelCandidate(
        string NodeId,
        IReadOnlyList<DaoxuanTravelSegment> InitialSegments,
        int DurationSeconds,
        string Signature);
}

