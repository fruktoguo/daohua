// ==============================================================================
// 文件名称: DaoHuaWorldTests.cs
// 主要职责: 世界大地图拓扑（DaoHuaWorld）与 Dijkstra 最短寻路路径规划单元测试。
// 核心逻辑 / 定义内容:
//   1. 验证多路段跨区域最短移动路径规划并合并为复合 WorldTravel 行动。
//   2. 验证移动途中取消或中断时玩家位置在路段比例进度上的精确定位与二次重规划。
//   3. 验证非相连节点不可达检测与区域专属行动准入限制。
// ==============================================================================

using DaoHua.Domain.Models;
using DaoHua.Domain.Simulation;
using Xunit;

namespace DaoHua.Domain.Tests;

/// <summary>
/// 验证 DaoHuaWorld（世界大地图拓扑、Dijkstra 最短寻路路径规划、路途中断重规划与区域行动限制）。
/// </summary>
public sealed class DaoHuaWorldTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-07T00:00:00Z");

    /// <summary>
    /// 验证大地图寻路规划器将跨多条相连路段的最短路径整合为单个复合 WorldTravel 行动。
    /// </summary>
    [Fact]
    public void Travel_planner_combines_shortest_multi_edge_path_into_one_action()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Queue.Entries.Clear();

        var result = DaoHuaSimulation.TravelTo(state, DaoHuaWorldNodeIds.TwilightPass);

        Assert.True(result.Success);
        var travel = Assert.Single(state.Queue.Entries);
        Assert.Equal(DaoHuaActionIds.WorldTravel, travel.ActionId);
        Assert.Equal(37, travel.DurationSeconds);
        Assert.Equal(
            ["edge.herb-spring", "edge.spring-twilight"],
            travel.TravelPlan!.Segments.Select(segment => segment.EdgeId).ToArray());
    }

    /// <summary>
    /// 验证在路段中途中断移动时，玩家位置精准锚定在当前路段比例进度上，且后续寻路自动以该中间点为起点计算新路径。
    /// </summary>
    [Fact]
    public void Removing_travel_keeps_player_on_edge_and_replans_from_that_position()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Queue.Entries.Clear();
        Assert.True(DaoHuaSimulation.TravelTo(state, DaoHuaWorldNodeIds.TwilightPass).Success);

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(10));

        var activeTravel = Assert.Single(state.Queue.Entries);
        Assert.Null(state.World.NodeId);
        Assert.Equal("edge.herb-spring", state.World.EdgeId);
        Assert.Equal(10d / 18d, state.World.EdgeProgress, precision: 6);
        Assert.True(DaoHuaSimulation.Remove(state, activeTravel.EntryId).Success);
        Assert.Empty(state.Queue.Entries);
        Assert.Null(state.World.NodeId);
        Assert.Equal(10d / 18d, state.World.EdgeProgress, precision: 6);

        Assert.True(DaoHuaSimulation.TravelTo(state, DaoHuaWorldNodeIds.HerbVale).Success);
        var returnTravel = Assert.Single(state.Queue.Entries);
        Assert.Equal(10, returnTravel.DurationSeconds);
        Assert.Equal(0, returnTravel.TravelPlan!.Segments[0].EndProgress);
    }

    /// <summary>
    /// 验证离线挂机快进推进时，大地图移动能跨越时间边界顺利完成并准确降落在目的地节点。
    /// </summary>
    [Fact]
    public void Offline_fast_forward_completes_travel_and_arrives_at_destination()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Queue.Entries.Clear();
        state.PresenceMode = DaoHuaPresenceMode.IdleOffline;
        Assert.True(DaoHuaSimulation.TravelTo(state, DaoHuaWorldNodeIds.CloudMarket).Success);

        var result = DaoHuaSimulation.AdvanceTo(state, Baseline.AddHours(1));

        Assert.Equal(DaoHuaWorldNodeIds.CloudMarket, state.World.NodeId);
        Assert.Null(state.World.EdgeId);
        Assert.Empty(state.Queue.Entries);
        Assert.Equal(1, result.CompletedActions);
        Assert.Contains(result.Events, entry => entry == $"arrived:{DaoHuaWorldNodeIds.CloudMarket}");
    }

    /// <summary>
    /// 验证节点地理位置对行动执行的准入限制（不在产地禁止采集）以及特定节点的修炼增益。
    /// </summary>
    [Fact]
    public void Location_restricts_actions_and_scales_cultivation_output()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Queue.Entries.Clear();
        state.World.NodeId = DaoHuaWorldNodeIds.CloudGate;

        var blocked = DaoHuaSimulation.Enqueue(state, DaoHuaActionIds.GatherHerbs);

        Assert.False(blocked.Success);
        Assert.Equal("ACTION_UNAVAILABLE_HERE", blocked.Code);

        state.World.NodeId = DaoHuaWorldNodeIds.SpiritSpring;
        Assert.True(DaoHuaSimulation.Enqueue(state, DaoHuaActionIds.DrawQi).Success);
        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(10));

        Assert.Equal(13, state.Inventory.Get(DaoHuaItemIds.Qi));
        Assert.Equal(13, state.Stats.Qi);
    }

    /// <summary>
    /// 验证重新下发移动目的地时，旧的移动规划被安全替换，且队列中的其他非移动行动完整保留。
    /// </summary>
    [Fact]
    public void Replanning_travel_replaces_old_route_but_preserves_other_actions()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        var originalCount = state.Queue.Entries.Count;
        Assert.True(DaoHuaSimulation.TravelTo(state, DaoHuaWorldNodeIds.CloudMarket).Success);
        Assert.True(DaoHuaSimulation.TravelTo(state, DaoHuaWorldNodeIds.SunsetRidge).Success);

        Assert.Equal(originalCount + 1, state.Queue.Entries.Count);
        Assert.Equal(DaoHuaActionIds.WorldTravel, state.Queue.Entries[0].ActionId);
        Assert.Equal(DaoHuaWorldNodeIds.SunsetRidge, state.Queue.Entries[0].TravelPlan!.DestinationNodeId);
        Assert.Single(state.Queue.Entries, entry => entry.TravelPlan is not null);
    }
}
