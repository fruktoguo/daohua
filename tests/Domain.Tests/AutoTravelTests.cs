using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.World;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 自动移动（AutoTravelEnabled）测试套件。
/// 验证：入队锚点记录、开轮地点不符时插队赶路至锚点续跑、无锚点回落 Blocked、关闭开关保留原跳过语义。
/// </summary>
public sealed class AutoTravelTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "catalog-a");

    /// <summary>n1—n2 单边世界图 + 仅允许 n1 的采集行动 + world.travel 定义的固定测试目录。</summary>
    private static GameDataCatalog CreateWorld(out ActionCatalog actions)
    {
        var graph = new WorldGraph();
        graph.AddNode(WorldNode.Create("n1", "采集点", 0, 0));
        graph.AddNode(WorldNode.Create("n2", "岔路口", 10, 0));
        graph.AddUndirectedEdge(WorldEdge.Create("e12", "n1", "n2", 10, 10));
        actions = new ActionCatalog([
            new ActionDefinition(new ActionId("test.gather"), "采药", ActionKind.Exploration, 2,
                new Dictionary<ItemId, double>(), [], AllowedNodeIds: new HashSet<string> { "n1" }),
            new ActionDefinition(new ActionId("world.travel"), "赶路", ActionKind.Exploration, 0,
                new Dictionary<ItemId, double>(), [], ExecutionKind: ActionExecutionKind.Travel)
        ]);
        return new GameDataCatalog("auto-travel", "auto-travel", actions, worldGraph: graph);
    }

    /// <summary>
    /// 入队时记录驻留节点为锚点；旅途中（边上行进）入队不登记锚点。
    /// </summary>
    [Fact]
    public void AutoTravel_RecordsAnchorNode_OnEnqueue()
    {
        var gameData = CreateWorld(out var actions);
        var player = CreatePlayer("锚点");

        player.TravelTo("n1");
        player.Enqueue(new ActionIntent(new ActionId("test.gather"), 1), Baseline, actions);
        Assert.Equal("n1", player.Actions[0].Intent.AnchorNodeId);

        player.World.EdgeId = "e12";
        player.World.EdgeProgress = 0.5;
        player.Enqueue(new ActionIntent(new ActionId("test.gather"), 1), Baseline, actions);
        Assert.Null(player.Actions[1].Intent.AnchorNodeId);
    }

    /// <summary>
    /// 核心语义：锚点 n1 的采集行动在玩家移至 n2 后开轮，自动插队 world.travel 回 n1 续跑完成。
    /// </summary>
    [Fact]
    public void AutoTravel_InsertsTravelToAnchor_WhenLocationDrifts()
    {
        var gameData = CreateWorld(out var actions);
        var player = CreatePlayer("采集者");
        player.TravelTo("n1");
        player.Enqueue(new ActionIntent(new ActionId("test.gather"), 1), Baseline, actions);
        player.TravelTo("n2");
        player.SetAutoTravelEnabled(true);

        // n2→n1 需 10 秒；再给 5 秒让采集完成一轮。
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(15), gameData);

        Assert.Contains(result.Events, e => e.Code == "action.auto-travel");
        Assert.Equal("n1", player.World.NodeId);
        Assert.Null(player.World.EdgeId);
        // 旅行单次出队后采集已在原地续跑：循环开启下完成至少一轮且队列中只剩采集行动。
        Assert.True(player.Progression.CompletedActions >= 1);
        Assert.All(player.Actions, action => Assert.Equal(new ActionId("test.gather"), action.Intent.ActionId));
    }

    /// <summary>
    /// 锚点缺失（旅途中入队）时即使开启自动移动也没有可回目的地，保持 Blocked 语义。
    /// </summary>
    [Fact]
    public void AutoTravel_FallsBackToBlocked_WhenAnchorMissing()
    {
        var gameData = CreateWorld(out var actions);
        var player = CreatePlayer("游方");
        player.World.EdgeId = "e12";
        player.World.EdgeProgress = 0.5;
        player.Enqueue(new ActionIntent(new ActionId("test.gather"), 1), Baseline, actions);
        Assert.Null(player.Actions[0].Intent.AnchorNodeId);

        player.TravelTo("n2");
        player.SetAutoTravelEnabled(true);
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(5), gameData);

        Assert.DoesNotContain(result.Events, e => e.Code == "action.auto-travel");
        Assert.Equal(ActionQueueStatus.Blocked, player.Actions[0].Status);
        Assert.Equal("n2", player.World.NodeId);
    }

    /// <summary>
    /// 未开启自动移动时，地点不符保持原有 Blocked 语义，不产生 travel 插队。
    /// </summary>
    [Fact]
    public void AutoTravel_FallsBackToBlocked_WhenDisabled()
    {
        var gameData = CreateWorld(out var actions);
        var player = CreatePlayer("守旧");
        player.TravelTo("n1");
        player.Enqueue(new ActionIntent(new ActionId("test.gather"), 1), Baseline, actions);
        player.TravelTo("n2");

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(5), gameData);

        Assert.DoesNotContain(result.Events, e => e.Code == "action.auto-travel");
        Assert.Equal(ActionQueueStatus.Blocked, player.Actions[0].Status);
        Assert.Equal("n2", player.World.NodeId);
    }
}
