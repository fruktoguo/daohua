using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using System.Text.Json;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 旅行行动测试：世界图路径规划、中途边处理、优先级挂起、取消折返与大步离线推进。
/// </summary>
public sealed class TravelActionTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "catalog-a");

    /// <summary>
    /// 无向图多跳路径规划：逆向行走时边顺序、方向与进度端点（1.0→0.0）必须正确。
    /// </summary>
    [Fact]
    public void WorldGraphPlansUndirectedMultiHopPathWithCorrectSegments()
    {
        var graph = new WorldGraph();
        graph.AddNode(WorldNode.Create("a", "甲", 0, 0));
        graph.AddNode(WorldNode.Create("b", "乙", 10, 0));
        graph.AddNode(WorldNode.Create("c", "丙", 20, 0));
        graph.AddUndirectedEdge(WorldEdge.Create("e_ab", "a", "b", 10, 10));
        graph.AddUndirectedEdge(WorldEdge.Create("e_bc", "b", "c", 10, 10));

        // 从 c 逆向走到 a
        var plan = graph.Plan("c", "a");
        Assert.Equal(2, plan.EdgeIds.Count);
        Assert.Equal("e_bc", plan.EdgeIds[0]);
        Assert.Equal("e_ab", plan.EdgeIds[1]);
        Assert.Equal(20, plan.TotalTravelSeconds);
        Assert.Equal(2, plan.Segments.Count);

        // 第一个路段：c 到 b (在边 e_bc 上反向，progress 1.0 -> 0.0)
        Assert.Equal("e_bc", plan.Segments[0].EdgeId);
        Assert.Equal("c", plan.Segments[0].FromNodeId);
        Assert.Equal("b", plan.Segments[0].ToNodeId);
        Assert.Equal(1.0, plan.Segments[0].StartProgress);
        Assert.Equal(0.0, plan.Segments[0].EndProgress);

        // 第二个路段：b 到 a (在边 e_ab 上反向，progress 1.0 -> 0.0)
        Assert.Equal("e_ab", plan.Segments[1].EdgeId);
        Assert.Equal("b", plan.Segments[1].FromNodeId);
        Assert.Equal("a", plan.Segments[1].ToNodeId);
        Assert.Equal(1.0, plan.Segments[1].StartProgress);
        Assert.Equal(0.0, plan.Segments[1].EndProgress);
    }

    /// <summary>
    /// 边中位置规划：按当前 EdgeProgress 在继续前进与原路折返间选择总耗时更短的方向。
    /// </summary>
    [Fact]
    public void PlanFromPositionHandlesMidEdgeForwardAndReverseCorrectly()
    {
        var graph = new WorldGraph();
        graph.AddNode(WorldNode.Create("n1", "一", 0, 0));
        graph.AddNode(WorldNode.Create("n2", "二", 10, 0));
        graph.AddNode(WorldNode.Create("n3", "三", 20, 0));
        graph.AddUndirectedEdge(WorldEdge.Create("e12", "n1", "n2", 10, 10));
        graph.AddUndirectedEdge(WorldEdge.Create("e23", "n2", "n3", 10, 10));

        // 站在 e12 上，progress = 0.8 (靠近 n2)，目标是 n3
        // 向前到 n2 耗时 2s，n2->n3 耗时 10s，共 12s
        // 折返到 n1 耗时 8s，n1->n2->n3 耗时 20s，共 28s
        // 应该选择向前到 n2
        var planForward = graph.PlanFromPosition("e12", 0.8, "n1", "n3");
        Assert.Equal(2, planForward.Segments.Count);
        Assert.Equal("e12", planForward.Segments[0].EdgeId);
        Assert.Equal(0.8, planForward.Segments[0].StartProgress);
        Assert.Equal(1.0, planForward.Segments[0].EndProgress);
        Assert.Equal(2.0, planForward.Segments[0].DurationSeconds, precision: 4);
        Assert.Equal("e23", planForward.Segments[1].EdgeId);
        Assert.Equal(12.0, planForward.TotalTravelSeconds, precision: 4);

        // 站在 e12 上，progress = 0.8，目标是 n1
        // 折返到 n1 耗时 8s
        // 前进到 n2 再折返到 n1 耗时 2s + 10s = 12s
        // 应该选择折返到 n1
        var planReverse = graph.PlanFromPosition("e12", 0.8, "n2", "n1");
        Assert.Single(planReverse.Segments);
        Assert.Equal("e12", planReverse.Segments[0].EdgeId);
        Assert.Equal(0.8, planReverse.Segments[0].StartProgress);
        Assert.Equal(0.0, planReverse.Segments[0].EndProgress);
        Assert.Equal(8.0, planReverse.Segments[0].DurationSeconds, precision: 4);
        Assert.Equal(8.0, planReverse.TotalTravelSeconds, precision: 4);
    }

    /// <summary>
    /// 移动指令插队队首时，原运行中行动被挂起为 Pending 且进度秒严格保留（不丢已做功）。
    /// </summary>
    [Fact]
    public void PriorityTravelSuspendsCurrentRunningActionAndPreservesProgress()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("李逍遥");

        // 1. 玩家先开始修炼 (10秒一轮)
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);

        // 2. 模拟推进 4 秒
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(4), catalog);
        Assert.Single(player.Actions);
        Assert.Equal(ActionQueueStatus.Running, player.Actions[0].Status);
        Assert.Equal(4.0, player.Actions[0].ProgressSeconds);

        // 3. 在已有行动时点击移动到青云山门
        var plan = catalog.WorldGraph.PlanFromPosition(
            player.World.EdgeId,
            player.World.EdgeProgress,
            player.World.NodeId,
            "node.cloud-gate");

        var travelState = new TravelActionState(
            "node.cloud-gate",
            plan.TotalTravelSeconds,
            plan.EdgeIds,
            plan.Segments);

        player.EnqueuePriority(
            new ActionIntent(new ActionId("world.travel"), 1, TargetNodeId: "node.cloud-gate"),
            Baseline.AddSeconds(4),
            catalog.Actions,
            travelState);

        // 验证队列：移动插在首位，原修炼被挂起且进度严格保持 4 秒
        Assert.Equal(2, player.Actions.Count);
        var firstAction = player.Actions[0];
        var secondAction = player.Actions[1];

        Assert.Equal("world.travel", firstAction.Intent.ActionId.Value);
        Assert.Equal(ActionQueueStatus.Running, firstAction.Status);
        Assert.NotNull(firstAction.Travel);
        Assert.Equal("node.cloud-gate", firstAction.Travel.TargetNodeId);

        Assert.Equal("cultivation.draw-qi", secondAction.Intent.ActionId.Value);
        Assert.Equal(ActionQueueStatus.Pending, secondAction.Status);
        Assert.Equal(4.0, secondAction.ProgressSeconds);
    }

    /// <summary>
    /// 移动推进全程：中途位于边内（0&lt;progress&lt;1），到达触发 world.arrived 并出队，
    /// 被挂起的行动自动恢复 Running 且保留此前进度继续推进。
    /// </summary>
    [Fact]
    public void TravelSimulationAdvancesPositionFinishesAndResumesSuspendedAction()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("张三丰");

        // 初始位置在云溪村
        Assert.Equal("node.yunxi-village", player.World.NodeId);

        // 队列先放一个修炼行动
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3), catalog);
        Assert.Equal(3.0, player.Actions[0].ProgressSeconds);

        // 规划从云溪村到青云山门
        var plan = catalog.WorldGraph.PlanFromPosition(
            player.World.EdgeId,
            player.World.EdgeProgress,
            player.World.NodeId,
            "node.cloud-gate");

        var travelDuration = plan.TotalTravelSeconds;
        Assert.True(travelDuration > 0);

        var travelState = new TravelActionState(
            "node.cloud-gate",
            plan.TotalTravelSeconds,
            plan.EdgeIds,
            plan.Segments);

        player.EnqueuePriority(
            new ActionIntent(new ActionId("world.travel"), 1, TargetNodeId: "node.cloud-gate"),
            Baseline.AddSeconds(3),
            catalog.Actions,
            travelState);

        // 推进 travelDuration * 0.35 秒（在第一段路中间）
        var midTime = travelDuration * 0.35;
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3 + midTime), catalog);

        // 此时移动仍在进行中
        Assert.Equal(2, player.Actions.Count);
        Assert.Equal("world.travel", player.Actions[0].Intent.ActionId.Value);
        Assert.NotNull(player.World.EdgeId);
        Assert.True(player.World.EdgeProgress > 0 && player.World.EdgeProgress < 1.0);
        Assert.Equal(midTime, player.Actions[0].ProgressSeconds, precision: 4);

        // 继续推进完成移动，并额外推进 2 秒以恢复修炼
        var finishTime = travelDuration + 2.0;
        var simResult = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(3 + finishTime), catalog);

        // 移动完成：
        // 1. 玩家抵达青云山门
        Assert.Equal("node.cloud-gate", player.World.NodeId);
        Assert.Null(player.World.EdgeId);
        Assert.Equal(0, player.World.EdgeProgress);

        // 2. 触发 world.arrived 事件
        Assert.Contains(simResult.Events, e => e.Code == "world.arrived");

        // 3. world.travel 行动已移出队列，队列只剩修炼
        Assert.Single(player.Actions);
        var resumedAction = player.Actions[0];
        Assert.Equal("cultivation.draw-qi", resumedAction.Intent.ActionId.Value);
        Assert.Equal(ActionQueueStatus.Running, resumedAction.Status);

        // 4. 原先 3 秒进度 + 额外 2 秒 = 5 秒进度
        Assert.Equal(5.0, resumedAction.ProgressSeconds);
    }

    /// <summary>
    /// 取消移动行动后玩家停留在边中位置（不回弹到节点），
    /// 且能以该边中坐标为起点重新规划任意目的地。
    /// </summary>
    [Fact]
    public void CancelTravelLeavesPlayerAtCurrentEdgePositionAndAllowsRePlanning()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("令狐冲");

        var plan = catalog.WorldGraph.PlanFromPosition(
            player.World.EdgeId,
            player.World.EdgeProgress,
            player.World.NodeId,
            "node.cloud-gate");

        var travelState = new TravelActionState(
            "node.cloud-gate",
            plan.TotalTravelSeconds,
            plan.EdgeIds,
            plan.Segments);

        player.EnqueuePriority(
            new ActionIntent(new ActionId("world.travel"), 1, TargetNodeId: "node.cloud-gate"),
            Baseline,
            catalog.Actions,
            travelState);

        // 移动到路段中间 (0.35 处)
        var midDuration = plan.TotalTravelSeconds * 0.35;
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(midDuration), catalog);

        var edgeBeforeCancel = player.World.EdgeId;
        var progressBeforeCancel = player.World.EdgeProgress;
        Assert.NotNull(edgeBeforeCancel);
        Assert.True(progressBeforeCancel > 0);

        // 玩家取消移动行动
        var travelActionId = player.Actions[0].Id;
        player.RemoveAction(travelActionId);
        Assert.Empty(player.Actions);

        // 玩家仍停留在路段中间
        Assert.Equal(edgeBeforeCancel, player.World.EdgeId);
        Assert.Equal(progressBeforeCancel, player.World.EdgeProgress);

        // 此时以半路位置重新规划回云溪村
        var returnPlan = catalog.WorldGraph.PlanFromPosition(
            player.World.EdgeId,
            player.World.EdgeProgress,
            player.World.NodeId,
            "node.yunxi-village");

        Assert.NotNull(returnPlan);
        Assert.NotEmpty(returnPlan.Segments);
        Assert.Equal("node.yunxi-village", returnPlan.ToNodeId);
    }

    /// <summary>
    /// 边中反复改向（乒乓折返）场景：每次改向重规划的路段端点与耗时必须精确，
    /// 推进后位置单调正确，最终到达出队。
    /// </summary>
    [Fact]
    public void PingPongTurnaroundMidEdgeSimulatesAndCalculatesPreciseSegments()
    {
        var graph = new WorldGraph();
        graph.AddNode(WorldNode.Create("n1", "一", 0, 0));
        graph.AddNode(WorldNode.Create("n2", "二", 10, 0));
        graph.AddNode(WorldNode.Create("n3", "三", 20, 0));
        graph.AddUndirectedEdge(WorldEdge.Create("e12", "n1", "n2", 10, 10));
        graph.AddUndirectedEdge(WorldEdge.Create("e23", "n2", "n3", 10, 10));

        var player = CreatePlayer("令狐冲");
        player.World.NodeId = "n1";

        var actionCatalog = new ActionCatalog(
        [
            new ActionDefinition(new ActionId("world.travel"), "赶路", ActionKind.Travel, 1,
                new Dictionary<ItemId, double>(), [], null, true, ActionExecutionKind.Travel)
        ]);
        var gameData = new GameDataCatalog("v1", "fp1", actionCatalog, worldGraph: graph);

        // 1. 从 n1 前往 n2
        var plan1 = graph.PlanFromPosition(null, 0, "n1", "n2");
        var travel1 = new TravelActionState("n2", plan1.TotalTravelSeconds, plan1.EdgeIds, plan1.Segments);
        player.EnqueuePriority(new ActionIntent(new ActionId("world.travel"), 1, "n2"), Baseline, actionCatalog, travel1);

        // 推进 4 秒：位于 e12 进度 0.4
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(4), gameData);
        Assert.Equal("e12", player.World.EdgeId);
        Assert.Equal(0.4, player.World.EdgeProgress, 4);

        // 2. 乒乓改向：决定折返回 n1
        var planReverse = graph.PlanFromPosition(player.World.EdgeId, player.World.EdgeProgress, player.World.NodeId, "n1");
        Assert.Single(planReverse.Segments);
        Assert.Equal(0.4, planReverse.Segments[0].StartProgress, 4);
        Assert.Equal(0.0, planReverse.Segments[0].EndProgress, 4);
        Assert.Equal(4.0, planReverse.TotalTravelSeconds, 4);

        var travelReverse = new TravelActionState("n1", planReverse.TotalTravelSeconds, planReverse.EdgeIds, planReverse.Segments);
        player.EnqueuePriority(new ActionIntent(new ActionId("world.travel"), 1, "n1"), Baseline.AddSeconds(4), actionCatalog, travelReverse);

        // 推进 2 秒向 n1 行进：位于 e12 进度 0.2
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(6), gameData);
        Assert.Equal("e12", player.World.EdgeId);
        Assert.Equal(0.2, player.World.EdgeProgress, 4);

        // 3. 乒乓再改向：决定直接去 n3
        var planForwardTo3 = graph.PlanFromPosition(player.World.EdgeId, player.World.EdgeProgress, player.World.NodeId, "n3");
        Assert.Equal(2, planForwardTo3.Segments.Count);
        Assert.Equal("e12", planForwardTo3.Segments[0].EdgeId);
        Assert.Equal(0.2, planForwardTo3.Segments[0].StartProgress, 4);
        Assert.Equal(1.0, planForwardTo3.Segments[0].EndProgress, 4);
        Assert.Equal(8.0, planForwardTo3.Segments[0].DurationSeconds, 4);

        Assert.Equal("e23", planForwardTo3.Segments[1].EdgeId);
        Assert.Equal(10.0, planForwardTo3.Segments[1].DurationSeconds, 4);
        Assert.Equal(18.0, planForwardTo3.TotalTravelSeconds, 4);

        var travelTo3 = new TravelActionState("n3", planForwardTo3.TotalTravelSeconds, planForwardTo3.EdgeIds, planForwardTo3.Segments);
        player.EnqueuePriority(new ActionIntent(new ActionId("world.travel"), 1, "n3"), Baseline.AddSeconds(6), actionCatalog, travelTo3);

        // 推进 18 秒完成全部移动抵达 n3
        var finalSim = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(24), gameData);
        Assert.Equal("n3", player.World.NodeId);
        Assert.Null(player.World.EdgeId);
        Assert.Equal(0, player.World.EdgeProgress);
        Assert.Contains(finalSim.Events, e => e.Code == "world.arrived");
        Assert.Empty(player.Actions);
    }

    /// <summary>
    /// 大步离线快进在同一次 AdvanceTo 内先完成移动，再把剩余时间结算给积压修炼轮次。
    /// </summary>
    [Fact]
    public void LargeOfflineJumpCompletesTravelAndAdvancesFullBacklog()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("石破天");

        // 队列先放一个修炼行动 (10s/次，无限循环 -1)
        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);

        // 插入移动行动 (从云溪村到青云山门)
        var plan = catalog.WorldGraph.PlanFromPosition(player.World.EdgeId, player.World.EdgeProgress, player.World.NodeId, "node.cloud-gate");
        var travelState = new TravelActionState("node.cloud-gate", plan.TotalTravelSeconds, plan.EdgeIds, plan.Segments);
        player.EnqueuePriority(new ActionIntent(new ActionId("world.travel"), 1, "node.cloud-gate"), Baseline, catalog.Actions, travelState);

        var travelDuration = plan.TotalTravelSeconds;
        Assert.True(travelDuration > 0);

        // 离线跳跃：一次性推进 travelDuration + 1000 秒 (修炼 100 轮)
        var totalOfflineJumpSeconds = travelDuration + 1000;
        var simResult = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(totalOfflineJumpSeconds), catalog);

        // 移动已完成抵达终点
        Assert.Equal("node.cloud-gate", player.World.NodeId);
        Assert.Null(player.World.EdgeId);
        Assert.Equal(0, player.World.EdgeProgress);
        Assert.Contains(simResult.Events, e => e.Code == "world.arrived");

        // 队列中只剩修炼，且完成了 100 轮
        Assert.Single(player.Actions);
        var runningAction = player.Actions[0];
        Assert.Equal("cultivation.draw-qi", runningAction.Intent.ActionId.Value);
        Assert.Equal(ActionQueueStatus.Running, runningAction.Status);
        Assert.Equal(100, runningAction.CompletedRepeats);
        Assert.Equal(0.0, runningAction.ProgressSeconds, 4);

        // 总计完成行动数：1 次移动 + 100 次修炼 = 101
        Assert.Equal(101, player.Progression.CompletedActions);
    }
}
