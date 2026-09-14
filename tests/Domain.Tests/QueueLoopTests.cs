using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 队列无限循环（Queue Loop）与受阻轮换测试套件。
/// 验证：单任务重复、多任务次序交替、禁用循环出队、移动单次豁免、受阻轮换与全受阻安全停泊。
/// </summary>
public sealed class QueueLoopTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "catalog-a");

    [Fact]
    public void QueueLoop_RepeatsSingleActionIndefinitely_WhenLoopEnabled()
    {
        // 单任务（吐纳 2 次，单次 10 秒），在 Settings.LoopEnabled = true 时，
        // 每轮完成后重置次数并继续下一轮，绝不出队。
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("甲");
        Assert.True(player.Settings.LoopEnabled);

        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 2), Baseline, catalog.Actions);

        // 推进 25 秒：前 20 秒完成 2 轮（第 1 轮 10s，第 2 轮 10s），触发循环重置为 2 次，随后第 3 轮推进 5 秒
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(25), catalog);

        Assert.True(result.Changed);
        Assert.Single(player.Actions);
        var action = player.Actions[0];
        Assert.Equal(2, action.CompletedRepeats);
        Assert.Equal(2, action.RemainingRepeats); // 重置后初始 2 次，正在执行第 1 次
        Assert.Equal(5, action.ProgressSeconds);
        Assert.Equal(ActionQueueStatus.Running, action.Status);
        Assert.Equal(25d, player.Progression.Qi);
        Assert.Equal(2, player.Progression.CompletedActions);
    }

    [Fact]
    public void QueueLoop_CyclesMultipleActionsInOrder_WhenLoopEnabled()
    {
        // 多个不同任务（采集 1 次 2 秒 + 吐纳 1 次 3 秒）在 Settings.LoopEnabled = true 时，依次交替循环执行
        var herb = new ItemId("item.herb");
        var actions = new ActionCatalog([
            new ActionDefinition(new ActionId("test.gather"), "采药", ActionKind.Exploration, 2, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double> { [herb] = 1 }),
            new ActionDefinition(new ActionId("test.draw-qi"), "吐纳", ActionKind.Cultivation, 3, new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(), 1)
        ]);
        var catalog = new GameDataCatalog("test", "test", actions, new Dictionary<ItemId, ItemDefinition>());

        var player = CreatePlayer("乙");
        Assert.True(player.Settings.LoopEnabled);

        player.Enqueue(new ActionIntent(new ActionId("test.gather"), 1), Baseline, actions);
        player.Enqueue(new ActionIntent(new ActionId("test.draw-qi"), 1), Baseline, actions);

        Assert.Equal(2, player.Actions.Count);
        Assert.Equal(new ActionId("test.gather"), player.Actions[0].Intent.ActionId);
        Assert.Equal(new ActionId("test.draw-qi"), player.Actions[1].Intent.ActionId);

        // 推进 2 秒：采药完成，轮换到队尾，吐纳成为队首并激活为 Running
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(2), catalog);
        Assert.Equal(2, player.Actions.Count);
        Assert.Equal(new ActionId("test.draw-qi"), player.Actions[0].Intent.ActionId);
        Assert.Equal(ActionQueueStatus.Running, player.Actions[0].Status);
        Assert.Equal(new ActionId("test.gather"), player.Actions[1].Intent.ActionId);
        Assert.Equal(ActionQueueStatus.Pending, player.Actions[1].Status);

        // 再推进 3 秒（累计 5 秒）：吐纳完成，轮换到队尾，采药重回队首
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(5), catalog);
        Assert.Equal(2, player.Actions.Count);
        Assert.Equal(new ActionId("test.gather"), player.Actions[0].Intent.ActionId);
        Assert.Equal(ActionQueueStatus.Running, player.Actions[0].Status);
        Assert.Equal(new ActionId("test.draw-qi"), player.Actions[1].Intent.ActionId);
        Assert.Equal(ActionQueueStatus.Pending, player.Actions[1].Status);

        // 再推进 2 秒（累计 7 秒）：采药再次完成并轮转，总完成数 = 3
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(7), catalog);
        Assert.Equal(new ActionId("test.draw-qi"), player.Actions[0].Intent.ActionId);
        Assert.Equal(3, player.Progression.CompletedActions);
    }

    [Fact]
    public void QueueLoop_RemovesActionDirectly_WhenLoopDisabled()
    {
        // 当 player.SetLoopEnabled(false) 时，任务次数耗尽后直接从队列移除，队列最终变空
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("丙");
        player.SetLoopEnabled(false);
        Assert.False(player.Settings.LoopEnabled);

        player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), 2), Baseline, catalog.Actions);

        // 推进 20 秒完成 2 轮
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(20), catalog);

        Assert.Equal(2, result.CompletedActions);
        Assert.Empty(player.Actions);
        Assert.Equal(2, player.Progression.CompletedActions);

        // 再次推进墙钟，队列不会复活
        SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(40), catalog);
        Assert.Empty(player.Actions);
    }

    [Fact]
    public void QueueLoop_DoesNotLoopTravelAction_EvenWhenLoopEnabled()
    {
        // 移动行动（world.travel）属于单次位移，即使 LoopEnabled = true，到达目的地后也绝不出队重入
        var graph = new WorldGraph();
        graph.AddNode(WorldNode.Create("n1", "起点", 0, 0));
        graph.AddNode(WorldNode.Create("n2", "终点", 10, 0));
        graph.AddUndirectedEdge(WorldEdge.Create("e12", "n1", "n2", 10, 10));

        var actionCatalog = new ActionCatalog([
            new ActionDefinition(new ActionId("world.travel"), "移动", ActionKind.Exploration, 0,
                new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(),
                ExecutionKind: ActionExecutionKind.Travel)
        ]);
        var gameData = new GameDataCatalog("travel", "travel", actionCatalog, worldGraph: graph);

        var player = CreatePlayer("行者");
        player.TravelTo("n1");
        Assert.True(player.Settings.LoopEnabled);

        var plan = graph.Plan("n1", "n2");
        var travelState = new TravelActionState("n2", plan.TotalTravelSeconds, plan.EdgeIds, plan.Segments);
        player.EnqueuePriority(new ActionIntent(new ActionId("world.travel"), 1, "n2"), Baseline, actionCatalog, travelState);

        // 推进 10 秒抵达目的地
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), gameData);

        Assert.Equal("n2", player.World.NodeId);
        Assert.Null(player.World.EdgeId);
        Assert.Equal(0, player.World.EdgeProgress);
        Assert.Contains(result.Events, e => e.Code == "world.arrived");
        Assert.Empty(player.Actions); // 关键门禁：到达后直接出队，绝不出队重入
    }

    [Fact]
    public void QueueLoop_RotatesBlockedActionToTail_WhenMultipleActionsExist()
    {
        // 当队列开启循环且有多个行动时，若首项受阻（如战斗失败变为 Blocked），能够轮换到队尾并继续推进下一个可用行动；
        // 若全受阻则安全停泊退出。
        var combatActionId = new ActionId("combat.boss");
        var qiActionId = new ActionId("cultivation.draw-qi");
        var actions = new ActionCatalog([
            new ActionDefinition(combatActionId, "强力妖兽", ActionKind.Combat, 0,
                new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(),
                ExecutionKind: ActionExecutionKind.CombatEncounter,
                CombatEncounterId: "encounter.boss"),
            new ActionDefinition(qiActionId, "吐纳", ActionKind.Cultivation, 2,
                new Dictionary<ItemId, double>(), new Dictionary<ItemId, double>(), 1)
        ]);
        var monster = new CombatMonsterDefinition("enemy.boss", "妖皇", 100, CombatantStats.Create(10_000, 10_000, 0, 1), []);
        var encounter = new CombatEncounterDefinition(
            "encounter.boss", "妖皇巢穴", 1, 1, 1, 1, 1, 1,
            [new CombatSpawnDefinition("enemy.boss", 1, 1)], [], [], 0);
        var catalog = new GameDataCatalog("boss-test", "boss-test", actions,
            combatEncounters: new Dictionary<string, CombatEncounterDefinition> { ["encounter.boss"] = encounter },
            combatMonsters: new Dictionary<string, CombatMonsterDefinition> { ["enemy.boss"] = monster });

        var player = CreatePlayer("丁");
        Assert.True(player.Settings.LoopEnabled);

        player.Enqueue(new ActionIntent(combatActionId, 1), Baseline, actions);
        player.Enqueue(new ActionIntent(qiActionId, 1), Baseline, actions);

        // 阶段一：首项战斗失败变 Blocked，轮转到队尾，推进第 2 项吐纳（耗时 2 秒）
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(4), catalog);

        Assert.Contains(result.Events, e => e.Code == "combat.lost");
        Assert.Equal(2, player.Actions.Count);
        // 吐纳行动完成一轮后又轮换回队尾，或者战斗项留在队尾保持 Blocked
        var blockedAction = player.Actions.Single(a => a.Intent.ActionId == combatActionId);
        Assert.Equal(ActionQueueStatus.Blocked, blockedAction.Status);
        Assert.True(player.Progression.CompletedActions >= 1);

        // 阶段二：全受阻安全停泊退出验证
        // 将吐纳行动也替换为一个必定失败的战斗行动
        player.RemoveAction(player.Actions.First(a => a.Intent.ActionId == qiActionId).Id);
        player.Enqueue(new ActionIntent(combatActionId, 1), Baseline.AddSeconds(4), actions);

        // 此时两项均为打不过的妖皇，推进 10 秒应安全停泊退出，绝不因无限轮换抛出 InvariantViolation
        var halted = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(14), catalog);

        Assert.Equal(2, player.Actions.Count);
        Assert.All(player.Actions, a => Assert.Equal(ActionQueueStatus.Blocked, a.Status));
    }
}
