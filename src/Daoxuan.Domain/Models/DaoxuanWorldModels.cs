namespace Daoxuan.Domain.Models;

/// <summary>
/// 玩家当前在修仙大世界中的位置与移动状态。
/// </summary>
public sealed class DaoxuanWorldState
{
    /// <summary>当前所处的世界地图 ID。</summary>
    public string MapId { get; set; } = DaoxuanWorldMapIds.QingyunMountains;
    /// <summary>当前停驻的地图节点 ID（若在移动中则为起步节点或目标节点）。</summary>
    public string? NodeId { get; set; } = DaoxuanWorldNodeIds.HerbVale;
    /// <summary>当前正在途经的路径边 ID（若停留在节点处则为 null）。</summary>
    public string? EdgeId { get; set; }
    /// <summary>当前边上的移动进度比例（0.0 ~ 1.0）。</summary>
    public double EdgeProgress { get; set; }

    /// <summary>克隆世界状态对象。</summary>
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

/// <summary>
/// 玩家大地图寻路与长途旅行执行方案。
/// </summary>
public sealed class DaoxuanTravelPlan
{
    /// <summary>目标地图 ID。</summary>
    public required string MapId { get; set; }
    /// <summary>目的地节点 ID。</summary>
    public required string DestinationNodeId { get; set; }
    /// <summary>整个旅行路线总耗时（秒）。</summary>
    public int TotalDurationSeconds { get; set; }
    /// <summary>旅行途经的各边路段列表。</summary>
    public List<DaoxuanTravelSegment> Segments { get; set; } = [];

    /// <summary>克隆旅行计划对象。</summary>
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

/// <summary>
/// 旅行路径中的单个边路段执行进度。
/// </summary>
public sealed class DaoxuanTravelSegment
{
    /// <summary>对应的世界地图边 ID。</summary>
    public required string EdgeId { get; set; }
    /// <summary>该路段起始进度比例（通常为 0.0）。</summary>
    public double StartProgress { get; set; }
    /// <summary>该路段目标结束进度比例（通常为 1.0）。</summary>
    public double EndProgress { get; set; }
    /// <summary>穿越该路段所需耗时（秒）。</summary>
    public int DurationSeconds { get; set; }

    /// <summary>克隆路段对象。</summary>
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

/// <summary>
/// 世界大地图静态拓扑定义，包含全部节点地点与相连边路径。
/// </summary>
public sealed class DaoxuanWorldMapDefinition
{
    /// <summary>地图唯一标识 ID。</summary>
    public required string Id { get; init; }
    /// <summary>地图名称。</summary>
    public required string Name { get; init; }
    /// <summary>地图风貌描述。</summary>
    public required string Description { get; init; }
    /// <summary>背景渲染随机种子。</summary>
    public int BackgroundSeed { get; init; }
    /// <summary>地图包含的节点地点列表。</summary>
    public required IReadOnlyList<DaoxuanWorldNodeDefinition> Nodes { get; init; }
    /// <summary>地图包含的连通路径边列表。</summary>
    public required IReadOnlyList<DaoxuanWorldEdgeDefinition> Edges { get; init; }
}

/// <summary>
/// 世界地图节点/地点静态配置定义。
/// </summary>
public sealed class DaoxuanWorldNodeDefinition
{
    /// <summary>节点唯一标识 ID。</summary>
    public required string Id { get; init; }
    /// <summary>地点显示名称。</summary>
    public required string Name { get; init; }
    /// <summary>地点详细描述。</summary>
    public required string Description { get; init; }
    /// <summary>地图平面 X 轴坐标。</summary>
    public double X { get; init; }
    /// <summary>地图平面 Y 轴坐标。</summary>
    public double Y { get; init; }
    /// <summary>该地点的灵气浓度/修炼收益倍率。</summary>
    public double CultivationMultiplier { get; init; } = 1;
    /// <summary>该地点开放可执行的动作 ID 列表。</summary>
    public IReadOnlyList<string> AvailableActionIds { get; init; } = [];
    /// <summary>该地点可触发的战斗遭遇副本 ID 列表。</summary>
    public IReadOnlyList<string> EncounterIds { get; init; } = [];
    /// <summary>驻留在此处的 NPC 列表。</summary>
    public IReadOnlyList<DaoxuanNearbyEntityDefinition> Npcs { get; init; } = [];
    /// <summary>驻留在此处的建筑/设施列表。</summary>
    public IReadOnlyList<DaoxuanNearbyEntityDefinition> Buildings { get; init; } = [];
}

/// <summary>
/// 连接两个世界节点的路径边静态配置定义。
/// </summary>
public sealed class DaoxuanWorldEdgeDefinition
{
    /// <summary>边唯一标识 ID。</summary>
    public required string Id { get; init; }
    /// <summary>起始节点 ID。</summary>
    public required string FromNodeId { get; init; }
    /// <summary>目标节点 ID。</summary>
    public required string ToNodeId { get; init; }
    /// <summary>正常御剑/步行穿越该路径的基础耗时（秒）。</summary>
    public int DurationSeconds { get; init; }
}

/// <summary>
/// 地点附近可见实体（NPC、建筑、石碑、灵泉等）静态原型定义。
/// </summary>
public sealed class DaoxuanNearbyEntityDefinition
{
    /// <summary>实体唯一 ID。</summary>
    public required string Id { get; init; }
    /// <summary>实体名称。</summary>
    public required string Name { get; init; }
    /// <summary>实体分类（npc、building 等）。</summary>
    public required string Kind { get; init; }
    /// <summary>实体外观与背景介绍。</summary>
    public required string Description { get; init; }
    /// <summary>实体附带的交互效果或特性说明。</summary>
    public string Effect { get; init; } = string.Empty;
}

