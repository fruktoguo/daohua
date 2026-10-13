using System.Text.Json;
using Domain.Common;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Market;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using SimulationEventTail = DaoHua.Ecs.SimulationEngine.SimulationEventTail;

namespace DaoHua.Ecs.Systems;

/// <summary>旅行行动的 System 所属规则：路径准备、跨段坐标推进与到达结算。</summary>
internal static partial class ActionExecutionRules
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

    internal static TravelActionState? PrepareTravelActionCore(IPlayerResourceContext player, IPlayerActionState action,
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

    /// <summary>
    /// 纯确定性推进移动行动。逐边段平滑累积行进进度与坐标插值，并在抵达终点时完成结算出队与触发到达事件。
    /// </summary>
    internal static void AdvanceTravelAction(
        IPlayerResourceContext player,
        IPlayerActionState action,
        ActionDefinition definition,
        GameDataCatalog? gameData,
        DateTimeOffset from,
        DateTimeOffset target,
        ref double remainingSeconds,
        ref SimulationEventTail? events,
        ref double completedActions,
        ref double completedRepeats)
    {
        var worldGraph = gameData?.WorldGraph;
        var travel = PrepareTravelActionCore(player, action, definition, gameData,
            from.AddSeconds((target - from).TotalSeconds - remainingSeconds), ref events);
        if (travel is null) return;

        if (travel.TotalDurationSeconds <= 0 || travel.Segments.Count == 0)
        {
            FinishTravel(player, action, definition, travel, from, target, ref remainingSeconds,
                ref events, ref completedActions, ref completedRepeats, gameData, worldGraph);
            return;
        }

        var travelRemaining = Math.Max(0, travel.TotalDurationSeconds - travel.ProgressSeconds);
        var step = Math.Min(remainingSeconds, travelRemaining);
        if (step <= 0)
        {
            FinishTravel(player, action, definition, travel, from, target, ref remainingSeconds,
                ref events, ref completedActions, ref completedRepeats, gameData, worldGraph);
            return;
        }

        remainingSeconds -= step;

        var segIndex = travel.CurrentSegmentIndex;
        var segElapsed = travel.SegmentElapsedSeconds;
        var progressSec = travel.ProgressSeconds;
        var toAdvance = step;

        while (toAdvance > 0 && segIndex < travel.Segments.Count)
        {
            var seg = travel.Segments[segIndex];
            var segRemaining = Math.Max(0, seg.DurationSeconds - segElapsed);
            var segStep = Math.Min(toAdvance, segRemaining);
            segElapsed += segStep;
            progressSec += segStep;
            toAdvance -= segStep;

            if (segElapsed + 1e-9 >= seg.DurationSeconds)
            {
                segIndex++;
                segElapsed = 0;
            }
        }

        action.ProgressSeconds = progressSec;
        action.Travel = travel with
        {
            CurrentSegmentIndex = segIndex,
            SegmentElapsedSeconds = segElapsed,
            ProgressSeconds = progressSec
        };

        if (segIndex < travel.Segments.Count)
        {
            var currentSeg = travel.Segments[segIndex];
            var segRatio = currentSeg.DurationSeconds > 0
                ? Math.Clamp(segElapsed / currentSeg.DurationSeconds, 0, 1)
                : 1d;
            var edgeProg = currentSeg.StartProgress + (currentSeg.EndProgress - currentSeg.StartProgress) * segRatio;
            player.World.EdgeId = currentSeg.EdgeId;
            player.World.EdgeProgress = Math.Clamp(edgeProg, 0, 1);

            if (worldGraph is not null && worldGraph.TryGetEdge(currentSeg.EdgeId, out var edgeObj))
            {
                if (worldGraph.TryGetNode(edgeObj.FromNodeId, out var fromNode)
                    && worldGraph.TryGetNode(edgeObj.ToNodeId, out var toNode))
                {
                    player.World.X = fromNode.X + (toNode.X - fromNode.X) * player.World.EdgeProgress;
                    player.World.Y = fromNode.Y + (toNode.Y - fromNode.Y) * player.World.EdgeProgress;
                }
            }

            player.MarkDirty(PlayerSectionKind.World);
            player.MarkDirty(PlayerSectionKind.ActionQueue);
        }
        else
        {
            FinishTravel(player, action, definition, travel, from, target, ref remainingSeconds,
                ref events, ref completedActions, ref completedRepeats, gameData, worldGraph);
        }
    }

    internal static void FinishTravel(
        IPlayerResourceContext player,
        IPlayerActionState action,
        ActionDefinition definition,
        TravelActionState travel,
        DateTimeOffset from,
        DateTimeOffset target,
        ref double remainingSeconds,
        ref SimulationEventTail? events,
        ref double completedActions,
        ref double completedRepeats,
        GameDataCatalog? gameData,
        WorldGraph? worldGraph)
    {
        player.World.NodeId = travel.TargetNodeId;
        player.World.EdgeId = null;
        player.World.EdgeProgress = 0;

        var nodeName = travel.TargetNodeId;
        if (worldGraph is not null && worldGraph.TryGetNode(travel.TargetNodeId, out var targetNode))
        {
            player.World.X = targetNode.X;
            player.World.Y = targetNode.Y;
            nodeName = targetNode.Name;
        }

        player.MarkDirty(PlayerSectionKind.World);
        var arrivalUnix = from.AddSeconds((target - from).TotalSeconds - remainingSeconds).ToUnixTimeMilliseconds() / 1000d;
        // 抵达终点计一次完成轮次，随后实例出队归档账本。
        player.RecordStats(action.Id, arrivalUnix, new PlayerStatsDelta { Cycles = 1 });
        // 赶路六维经验按实际行进总秒数一次性取整入账（总纲 §20.1.4）：定额不吃收益倍率。
        foreach (var yield in definition.Yields)
            if (yield is { Kind: ActionYieldKind.RootExp, Period: ActionYieldPeriod.PerSecond, Aspect: { } aspect })
                GrantRootExperience(player, aspect, Math.Round(yield.Amount * travel.TotalDurationSeconds),
                    gameData, action.Id, arrivalUnix);
        player.RemoveAction(action.Id, incrementVersion: false, endedAtUnixSeconds: arrivalUnix);
        if (player.Actions.Count > 0 && player.Actions[0].Status == ActionQueueStatus.Pending)
        {
            player.Actions[0].Status = ActionQueueStatus.Running;
        }
        player.MarkDirty(PlayerSectionKind.ActionQueue);

        completedActions += 1;
        completedRepeats += 1;
        player.Progression.CompletedActions += 1;
        player.MarkDirty(PlayerSectionKind.Progression);

        var arrivalTime = from.AddSeconds((target - from).TotalSeconds - remainingSeconds);
        events ??= new SimulationEventTail();
        events.Add(new SimulationEvent(
            arrivalTime,
            "world.arrived",
            $"已抵达地点：{nodeName}。",
            definition.Id));
        TutorialRules.OnTravelArrived(player, arrivalTime, ref events);
    }
}
