using DaoHua.Ecs;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using Xunit;

namespace Ecs.Tests;

/// <summary>行动规则迁移门禁：真实 ECS 旅行跨段、到达结算与挂起行动恢复共用一个请求。</summary>
public sealed class EcsActionExecutionRulesTests
{
    [Fact]
    public async Task TravelArrivalArchivesAtItsBoundaryAndResumesTheSuspendedInfiniteAction()
    {
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "旅行规则", from, catalog.Fingerprint);
        var suspended = player.Enqueue(new(new ActionId("cultivation.draw-qi"), -1), from, catalog.Actions);
        ((IPlayerActionState)suspended).ProgressSeconds = 3;
        var plan = catalog.WorldGraph.PlanFromPosition(player.World.EdgeId, player.World.EdgeProgress,
            player.World.NodeId, "node.cloud-gate");
        var travel = player.EnqueuePriority(new(new ActionId("world.travel"), 1, "node.cloud-gate"),
            from, catalog.Actions, new TravelActionState("node.cloud-gate", plan.TotalTravelSeconds,
                plan.EdgeIds, plan.Segments));

        using var runtime = new EcsGameRuntime(catalog, from);
        var id = runtime.CreatePlayer(player.ExportState());
        var midSeconds = plan.TotalTravelSeconds * 0.35;
        await runtime.RunPlayerOnceAsync(id, from.AddSeconds(midSeconds));
        var mid = runtime.ExportSnapshot(id).State;
        Assert.Equal(travel.Id, mid.Actions[0].Id);
        Assert.NotNull(mid.World.EdgeId);
        Assert.InRange(mid.World.EdgeProgress, double.Epsilon, 1 - double.Epsilon);
        Assert.Equal(midSeconds, mid.Actions[0].ProgressSeconds, 5);

        var arrival = from.AddSeconds(plan.TotalTravelSeconds);
        await runtime.RunPlayerOnceAsync(id, arrival.AddSeconds(2));
        var actual = runtime.ExportSnapshot(id).State;
        Assert.Equal("node.cloud-gate", actual.World.NodeId);
        Assert.Null(actual.World.EdgeId);
        Assert.Equal(0, actual.World.EdgeProgress);
        var resumed = Assert.Single(actual.Actions);
        Assert.Equal(suspended.Id, resumed.Id);
        Assert.Equal(-1, resumed.RemainingRepeats);
        Assert.Equal(5, resumed.ProgressSeconds);
        Assert.Equal(ActionQueueStatus.Running, resumed.Status);
        var report = Assert.Single(actual.Duel.Reports);
        Assert.Equal(travel.Id.ToString("N"), report.ReportId);
        Assert.Equal(arrival.ToUnixTimeMilliseconds() / 1000d, report.EndedAtUnixSeconds);
        Assert.Equal(arrival, Assert.Single(runtime.DrainSimulationEvents(id), entry => entry.Code == "world.arrived").At);
        Assert.Equal(1, actual.Progression.CompletedActions);
        Assert.Equal(arrival.AddSeconds(2), actual.LastSimulatedAt);
    }
}
