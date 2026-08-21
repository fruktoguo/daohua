using Daoxuan.Domain.Models;
using Daoxuan.Domain.Simulation;
using Xunit;

namespace Daoxuan.Domain.Tests;

public sealed class DaoxuanWorldTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-07T00:00:00Z");

    [Fact]
    public void Travel_planner_combines_shortest_multi_edge_path_into_one_action()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Queue.Entries.Clear();

        var result = DaoxuanSimulation.TravelTo(state, DaoxuanWorldNodeIds.TwilightPass);

        Assert.True(result.Success);
        var travel = Assert.Single(state.Queue.Entries);
        Assert.Equal(DaoxuanActionIds.WorldTravel, travel.ActionId);
        Assert.Equal(37, travel.DurationSeconds);
        Assert.Equal(
            ["edge.herb-spring", "edge.spring-twilight"],
            travel.TravelPlan!.Segments.Select(segment => segment.EdgeId).ToArray());
    }

    [Fact]
    public void Removing_travel_keeps_player_on_edge_and_replans_from_that_position()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Queue.Entries.Clear();
        Assert.True(DaoxuanSimulation.TravelTo(state, DaoxuanWorldNodeIds.TwilightPass).Success);

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(10));

        var activeTravel = Assert.Single(state.Queue.Entries);
        Assert.Null(state.World.NodeId);
        Assert.Equal("edge.herb-spring", state.World.EdgeId);
        Assert.Equal(10d / 18d, state.World.EdgeProgress, precision: 6);
        Assert.True(DaoxuanSimulation.Remove(state, activeTravel.EntryId).Success);
        Assert.Empty(state.Queue.Entries);
        Assert.Null(state.World.NodeId);
        Assert.Equal(10d / 18d, state.World.EdgeProgress, precision: 6);

        Assert.True(DaoxuanSimulation.TravelTo(state, DaoxuanWorldNodeIds.HerbVale).Success);
        var returnTravel = Assert.Single(state.Queue.Entries);
        Assert.Equal(10, returnTravel.DurationSeconds);
        Assert.Equal(0, returnTravel.TravelPlan!.Segments[0].EndProgress);
    }

    [Fact]
    public void Offline_fast_forward_completes_travel_and_arrives_at_destination()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Queue.Entries.Clear();
        state.PresenceMode = DaoxuanPresenceMode.IdleOffline;
        Assert.True(DaoxuanSimulation.TravelTo(state, DaoxuanWorldNodeIds.CloudMarket).Success);

        var result = DaoxuanSimulation.AdvanceTo(state, Baseline.AddHours(1));

        Assert.Equal(DaoxuanWorldNodeIds.CloudMarket, state.World.NodeId);
        Assert.Null(state.World.EdgeId);
        Assert.Empty(state.Queue.Entries);
        Assert.Equal(1, result.CompletedActions);
        Assert.Contains(result.Events, entry => entry == $"arrived:{DaoxuanWorldNodeIds.CloudMarket}");
    }

    [Fact]
    public void Location_restricts_actions_and_scales_cultivation_output()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Queue.Entries.Clear();
        state.World.NodeId = DaoxuanWorldNodeIds.CloudGate;

        var blocked = DaoxuanSimulation.Enqueue(state, DaoxuanActionIds.GatherHerbs);

        Assert.False(blocked.Success);
        Assert.Equal("ACTION_UNAVAILABLE_HERE", blocked.Code);

        state.World.NodeId = DaoxuanWorldNodeIds.SpiritSpring;
        Assert.True(DaoxuanSimulation.Enqueue(state, DaoxuanActionIds.DrawQi).Success);
        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(10));

        Assert.Equal(13, state.Inventory.Get(DaoxuanItemIds.Qi));
        Assert.Equal(13, state.Stats.Qi);
    }

    [Fact]
    public void Replanning_travel_replaces_old_route_but_preserves_other_actions()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        var originalCount = state.Queue.Entries.Count;
        Assert.True(DaoxuanSimulation.TravelTo(state, DaoxuanWorldNodeIds.CloudMarket).Success);
        Assert.True(DaoxuanSimulation.TravelTo(state, DaoxuanWorldNodeIds.SunsetRidge).Success);

        Assert.Equal(originalCount + 1, state.Queue.Entries.Count);
        Assert.Equal(DaoxuanActionIds.WorldTravel, state.Queue.Entries[0].ActionId);
        Assert.Equal(DaoxuanWorldNodeIds.SunsetRidge, state.Queue.Entries[0].TravelPlan!.DestinationNodeId);
        Assert.Single(state.Queue.Entries, entry => entry.TravelPlan is not null);
    }
}
