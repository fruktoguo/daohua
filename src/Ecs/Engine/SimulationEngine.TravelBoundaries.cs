using System.Collections.ObjectModel;
using Domain.Common;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Market;
using Domain.Player;
using Domain.Simulation;
using Domain.World;

namespace DaoHua.Ecs;

public static partial class SimulationEngine
{
    /// <summary>只规划或校验当前移动路径，实际耗时交由移动系统推进。</summary>
    public static bool PrepareTravelAction(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog gameData, DateTimeOffset at, ActionMutationBatch mutations)
        => PrepareTravelActionCore(player, action, definition, gameData, at, ref mutations.EventTail) is not null;

    /// <summary>按原资源窗口预算推进移动，跨路段坐标与到达结算使用原浮点顺序。</summary>
    public static double AdvanceTravelBoundary(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog gameData, DateTimeOffset from, DateTimeOffset target,
        double remainingSeconds, ActionMutationBatch mutations)
    {
        AdvanceTravelAction(player, action, definition, gameData, from, target, ref remainingSeconds,
            ref mutations.EventTail, ref mutations.ActionCount, ref mutations.RepeatCount);
        return remainingSeconds;
    }

    private static TravelActionState? PrepareTravelActionCore(IPlayerResourceContext player, IPlayerActionState action,
        ActionDefinition definition, GameDataCatalog? gameData, DateTimeOffset at, ref SimulationEventTail? events)
    {
        var worldGraph = gameData?.WorldGraph;
        var travel = action.Travel;

        // 如果 travel 状态为空，基于当前玩家位置和意图目标节点进行自愈规划
        if (travel is null)
        {
            var targetNodeId = action.Intent.TargetNodeId ?? player.World.NodeId;
            if (worldGraph is not null && worldGraph.ContainsNode(targetNodeId))
            {
                try
                {
                    var plan = worldGraph.PlanFromPosition(
                        player.World.EdgeId,
                        player.World.EdgeProgress,
                        player.World.NodeId,
                        targetNodeId);
                    travel = new TravelActionState(
                        targetNodeId,
                        plan.TotalTravelSeconds,
                        plan.EdgeIds,
                        plan.Segments);
                    action.Travel = travel;
                }
                catch (DomainException)
                {
                    player.RemoveAction(action.Id, incrementVersion: false,
                        endedAtUnixSeconds: at.ToUnixTimeMilliseconds() / 1000d);
                    events ??= new SimulationEventTail();
                    events.Add(new SimulationEvent(
                        at,
                        "action.invalid-dropped",
                        $"移动行动 {definition.Name} 无法到达目标地点，已自动移出队列。",
                        definition.Id));
                    return null;
                }
            }
            else
            {
                player.RemoveAction(action.Id, incrementVersion: false,
                    endedAtUnixSeconds: at.ToUnixTimeMilliseconds() / 1000d);
                events ??= new SimulationEventTail();
                events.Add(new SimulationEvent(
                    at,
                    "action.invalid-dropped",
                    $"移动行动 {definition.Name} 目标节点无效，已自动移出队列。",
                    definition.Id));
                return null;
            }
        }

        return travel;
    }
}
