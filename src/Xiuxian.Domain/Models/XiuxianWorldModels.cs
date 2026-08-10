namespace Xiuxian.Domain.Models;

public sealed class XiuxianWorldState
{
    public string MapId { get; set; } = XiuxianWorldMapIds.QingyunMountains;
    public string? NodeId { get; set; } = XiuxianWorldNodeIds.HerbVale;
    public string? EdgeId { get; set; }
    public double EdgeProgress { get; set; }

    public XiuxianWorldState Clone()
    {
        return new XiuxianWorldState
        {
            MapId = MapId,
            NodeId = NodeId,
            EdgeId = EdgeId,
            EdgeProgress = EdgeProgress
        };
    }
}

public sealed class XiuxianTravelPlan
{
    public required string MapId { get; set; }
    public required string DestinationNodeId { get; set; }
    public int TotalDurationSeconds { get; set; }
    public List<XiuxianTravelSegment> Segments { get; set; } = [];

    public XiuxianTravelPlan Clone()
    {
        return new XiuxianTravelPlan
        {
            MapId = MapId,
            DestinationNodeId = DestinationNodeId,
            TotalDurationSeconds = TotalDurationSeconds,
            Segments = Segments.Select(segment => segment.Clone()).ToList()
        };
    }
}

public sealed class XiuxianTravelSegment
{
    public required string EdgeId { get; set; }
    public double StartProgress { get; set; }
    public double EndProgress { get; set; }
    public int DurationSeconds { get; set; }

    public XiuxianTravelSegment Clone()
    {
        return new XiuxianTravelSegment
        {
            EdgeId = EdgeId,
            StartProgress = StartProgress,
            EndProgress = EndProgress,
            DurationSeconds = DurationSeconds
        };
    }
}

public sealed class XiuxianWorldMapDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public int BackgroundSeed { get; init; }
    public required IReadOnlyList<XiuxianWorldNodeDefinition> Nodes { get; init; }
    public required IReadOnlyList<XiuxianWorldEdgeDefinition> Edges { get; init; }
}

public sealed class XiuxianWorldNodeDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double CultivationMultiplier { get; init; } = 1;
    public IReadOnlyList<string> AvailableActionIds { get; init; } = [];
    public IReadOnlyList<string> EncounterIds { get; init; } = [];
    public IReadOnlyList<XiuxianNearbyEntityDefinition> Npcs { get; init; } = [];
    public IReadOnlyList<XiuxianNearbyEntityDefinition> Buildings { get; init; } = [];
}

public sealed class XiuxianWorldEdgeDefinition
{
    public required string Id { get; init; }
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public int DurationSeconds { get; init; }
}

public sealed class XiuxianNearbyEntityDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Description { get; init; }
    public string Effect { get; init; } = string.Empty;
}
