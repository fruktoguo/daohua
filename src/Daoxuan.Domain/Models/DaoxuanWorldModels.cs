namespace Daoxuan.Domain.Models;

public sealed class DaoxuanWorldState
{
    public string MapId { get; set; } = DaoxuanWorldMapIds.QingyunMountains;
    public string? NodeId { get; set; } = DaoxuanWorldNodeIds.HerbVale;
    public string? EdgeId { get; set; }
    public double EdgeProgress { get; set; }

    public DaoxuanWorldState Clone()
    {
        return new DaoxuanWorldState
        {
            MapId = MapId,
            NodeId = NodeId,
            EdgeId = EdgeId,
            EdgeProgress = EdgeProgress
        };
    }
}

public sealed class DaoxuanTravelPlan
{
    public required string MapId { get; set; }
    public required string DestinationNodeId { get; set; }
    public int TotalDurationSeconds { get; set; }
    public List<DaoxuanTravelSegment> Segments { get; set; } = [];

    public DaoxuanTravelPlan Clone()
    {
        return new DaoxuanTravelPlan
        {
            MapId = MapId,
            DestinationNodeId = DestinationNodeId,
            TotalDurationSeconds = TotalDurationSeconds,
            Segments = Segments.Select(segment => segment.Clone()).ToList()
        };
    }
}

public sealed class DaoxuanTravelSegment
{
    public required string EdgeId { get; set; }
    public double StartProgress { get; set; }
    public double EndProgress { get; set; }
    public int DurationSeconds { get; set; }

    public DaoxuanTravelSegment Clone()
    {
        return new DaoxuanTravelSegment
        {
            EdgeId = EdgeId,
            StartProgress = StartProgress,
            EndProgress = EndProgress,
            DurationSeconds = DurationSeconds
        };
    }
}

public sealed class DaoxuanWorldMapDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public int BackgroundSeed { get; init; }
    public required IReadOnlyList<DaoxuanWorldNodeDefinition> Nodes { get; init; }
    public required IReadOnlyList<DaoxuanWorldEdgeDefinition> Edges { get; init; }
}

public sealed class DaoxuanWorldNodeDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double CultivationMultiplier { get; init; } = 1;
    public IReadOnlyList<string> AvailableActionIds { get; init; } = [];
    public IReadOnlyList<string> EncounterIds { get; init; } = [];
    public IReadOnlyList<DaoxuanNearbyEntityDefinition> Npcs { get; init; } = [];
    public IReadOnlyList<DaoxuanNearbyEntityDefinition> Buildings { get; init; } = [];
}

public sealed class DaoxuanWorldEdgeDefinition
{
    public required string Id { get; init; }
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public int DurationSeconds { get; init; }
}

public sealed class DaoxuanNearbyEntityDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Description { get; init; }
    public string Effect { get; init; } = string.Empty;
}
