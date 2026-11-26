using DaoHua.Ecs;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.World;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 大地图境界移动速度规则与旅行路径折算确定性门禁（总纲 §3.1 / §8.3.1b）：
/// 防止境界分段判定漂移（如大境界入口误叠加中境界）、路径缓存污染、分段耗时尾差及自动赶路漏接移速。
/// </summary>
public sealed class WorldTravelRulesTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 0, 0, 0, 1.00)]     // 非法低值钳制至 Lv.1
    [InlineData(1, 0, 0, 0, 1.00)]     // 凡俗·武 起步
    [InlineData(6, 0, 0, 0, 1.00)]     // 凡俗·武 末层
    [InlineData(7, 1, 0, 5, 1.05)]     // 凡俗·玄 +5%
    [InlineData(12, 1, 0, 5, 1.05)]    // 凡俗·玄 末层
    [InlineData(13, 2, 0, 10, 1.10)]   // 凡俗·天 +5%
    [InlineData(18, 2, 0, 10, 1.10)]   // 凡俗·天 叩天门
    [InlineData(19, 2, 1, 40, 1.40)]   // 练气·初 大境界 +30%，不重复计中境界
    [InlineData(21, 2, 1, 40, 1.40)]   // 练气·初 末层
    [InlineData(22, 3, 1, 45, 1.45)]   // 练气·中 +5%
    [InlineData(25, 4, 1, 50, 1.50)]   // 练气·后 +5%
    [InlineData(28, 5, 1, 55, 1.55)]   // 练气·极 +5%
    [InlineData(30, 5, 1, 55, 1.55)]   // 半步筑基（练气·极 末层）
    [InlineData(31, 5, 2, 85, 1.85)]   // 筑基·初 大境界 +30%，不重复计中境界
    [InlineData(34, 6, 2, 90, 1.90)]   // 筑基·中 +5%
    [InlineData(43, 8, 3, 130, 2.30)]  // 金丹·初
    [InlineData(55, 11, 4, 175, 2.75)] // 元婴·初
    [InlineData(67, 14, 5, 220, 3.20)] // 化神·初
    [InlineData(115, 26, 9, 400, 5.00)] // 渡劫·初
    [InlineData(126, 29, 9, 415, 5.15)] // 半步飞升（渡劫·极）
    [InlineData(127, 29, 10, 445, 5.45)] // 飞升满级：29 中 + 10 大
    [InlineData(200, 29, 10, 445, 5.45)] // 超上限钳制至 Lv.127
    public void RealmSpeedMultiplier_MatchesMidAndMajorBreakLadder(
        int realmLevel, int expectedMid, int expectedMajor, int expectedPoints, double expectedMultiplier)
    {
        var (mid, major) = WorldTravelRules.CountRealmBreaks(realmLevel);
        Assert.Equal(expectedMid, mid);
        Assert.Equal(expectedMajor, major);
        Assert.Equal(expectedPoints, WorldTravelRules.SpeedBonusPoints(realmLevel));
        Assert.Equal(expectedMultiplier, WorldTravelRules.SpeedMultiplier(realmLevel), precision: 12);
    }

    [Fact]
    public void PlanFromPosition_ScalesDurationsWithoutMutatingCacheOrTopology()
    {
        var graph = new WorldGraph();
        graph.AddNode(WorldNode.Create("a", "起点", 0, 0));
        graph.AddNode(WorldNode.Create("b", "中继", 10, 0));
        graph.AddNode(WorldNode.Create("c", "终点", 25, 0));
        graph.AddUndirectedEdge(WorldEdge.Create("e1", "a", "b", 10, 14));
        graph.AddUndirectedEdge(WorldEdge.Create("e2", "b", "c", 15, 21));

        var basePlan = graph.PlanFromPosition(null, 0, "a", "c");
        var fastPlan = graph.PlanFromPosition(null, 0, "a", "c", WorldTravelRules.SpeedMultiplier(19)); // 1.40×
        var baseAgain = graph.PlanFromPosition(null, 0, "a", "c");

        // 缓存与拓扑不受缩放影响
        Assert.Equal(35d, basePlan.TotalTravelSeconds, precision: 10);
        Assert.Equal(35d, baseAgain.TotalTravelSeconds, precision: 10);
        Assert.Equal(basePlan.EdgeIds, fastPlan.EdgeIds);
        Assert.Equal(basePlan.TotalDistance, fastPlan.TotalDistance, precision: 10);

        // 缩放后总耗时严格等于各段耗时精确之和（14/1.4 + 21/1.4 = 10 + 15 = 25s）
        Assert.Equal(2, fastPlan.Segments.Count);
        Assert.Equal(10d, fastPlan.Segments[0].DurationSeconds, precision: 10);
        Assert.Equal(15d, fastPlan.Segments[1].DurationSeconds, precision: 10);
        Assert.Equal(fastPlan.Segments.Sum(s => s.DurationSeconds), fastPlan.TotalTravelSeconds, precision: 12);

        // 边中途重规划同样应用移速倍率（e1 进度 0.5 续行：剩余 7s + e2 21s = 28s / 1.4 = 20s）
        var midEdgePlan = graph.PlanFromPosition("e1", 0.5, "a", "c", 1.4);
        Assert.Equal(20d, midEdgePlan.TotalTravelSeconds, precision: 10);

        // 非法移速倍率快速失败
        Assert.Throws<DomainException>(() => graph.PlanFromPosition(null, 0, "a", "c", 0));
        Assert.Throws<DomainException>(() => graph.PlanFromPosition(null, 0, "a", "c", -1));
        Assert.Throws<DomainException>(() => graph.PlanFromPosition(null, 0, "a", "c", double.NaN));
    }

    [Fact]
    public void SimulationTravel_AutoTravelAndSelfHealApplyRealmSpeedMultiplier()
    {
        var graph = new WorldGraph();
        graph.AddNode(WorldNode.Create("n1", "起点", 0, 0));
        graph.AddNode(WorldNode.Create("n2", "目标", 20, 0));
        graph.AddUndirectedEdge(WorldEdge.Create("e12", "n1", "n2", 20, 37));

        var travelDef = new ActionDefinition(
            new ActionId("world.travel"), "赶路", ActionKind.Travel, 1,
            new Dictionary<ItemId, double>(),
            [new ActionYield(ActionYieldKind.RootExp, ActionYieldPeriod.PerSecond, 1, Aspect: CoreAspectKind.Agility)],
            ExecutionKind: ActionExecutionKind.Travel);
        var gatherDef = new ActionDefinition(
            new ActionId("explore.gather"), "采集", ActionKind.Exploration, 5,
            new Dictionary<ItemId, double>(),
            [new ActionYield(ActionYieldKind.Experience, ActionYieldPeriod.PerRepeat, 10)],
            AllowedNodeIds: new HashSet<string>(StringComparer.Ordinal) { "n2" });
        var actions = new ActionCatalog([travelDef, gatherDef]);
        var gameData = new GameDataCatalog("travel-speed", "travel-speed", actions, worldGraph: graph);

        // Lv.31 筑基一层：移速 1.85×，37s 路径折算为精确 20s
        var basePlayer = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "御风修士", Baseline, "travel-speed");
        var baseState = basePlayer.ExportState();
        var player = PlayerAggregate.Restore(baseState with
        {
            Progression = baseState.Progression with { RealmLevel = 31 },
            World = new PlayerWorldStateDocument("n1", 0, 0, 0),
            Settings = baseState.Settings with { AutoTravelEnabled = true }
        });

        // 1. 在 n1 入队锚定 n2 的采集行动，触发自动赶路插队
        player.Enqueue(new ActionIntent(gatherDef.Id, 1, AnchorNodeId: "n2"), Baseline, actions);
        var mid = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), gameData);
        Assert.Equal("e12", player.World.EdgeId);
        Assert.Equal(0.5, player.World.EdgeProgress, precision: 6);
        Assert.DoesNotContain(mid.Events, e => e.Code == "world.arrived");

        // 再推进 10 秒（累计 20 秒）准时抵达，且身法经验按实际行进 20 秒入账
        var arrived = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(20), gameData);
        Assert.Equal("n2", player.World.NodeId);
        Assert.Null(player.World.EdgeId);
        Assert.Contains(arrived.Events, e => e.Code == "world.arrived");
        Assert.Equal(20d, player.Loadout.CoreAttributes.AgilityExp, precision: 6);

        // 2. Travel 字段缺失时走 PrepareTravelActionCore 自愈规划，同样应用 1.85× 移速（返程 20 秒抵达）
        player.EnqueuePriority(new ActionIntent(travelDef.Id, 1, "n1"), Baseline.AddSeconds(20), actions, travel: null);
        var back = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(40), gameData);
        Assert.Equal("n1", player.World.NodeId);
        Assert.Contains(back.Events, e => e.Code == "world.arrived");
    }
}
