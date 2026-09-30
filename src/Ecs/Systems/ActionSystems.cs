using Domain.GameData;
using Domain.Common;
using Domain.Player;
using DaoHua.Ecs.Components;
using YuoCore.Ecs;

namespace DaoHua.Ecs.Systems;

internal static class EcsSystemContext
{
    public static EcsClockComponent Clock(YuoEntity entity)
        => entity.World.WorldMain.GetComponent<EcsClockComponent>()
           ?? throw new InvalidOperationException("ECS world clock is not initialized.");

    public static bool ShouldUpdate(YuoEntity entity)
    {
        var clock = Clock(entity);
        return !clock.SkipWorldUpdate && (clock.ActivePlayerId is not { } active
            || entity.GetComponent<PlayerIdentityComponent>()?.PlayerId == active);
    }

    /// <summary>取本实体当前请求期工作组件；未挂请求时返回 null，System 直接跳过。</summary>
    public static EcsActionExecutionComponent? Execution(YuoEntity entity)
        => entity.GetComponent<EcsActionExecutionComponent>();
}

/// <summary>Clock 阶段开启资源自然窗口；准备阶段不推进实际时间。</summary>
[SystemOrder(100)]
public sealed class ActionClockSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (!EcsSystemContext.ShouldUpdate(queue.Entity)) return;
        using var measurement = RuntimeInstrumentation.MeasureCurrent("ecs.system.run.ActionClockSystem");
        if (EcsSystemContext.Execution(queue.Entity) is { } execution)
            EcsActionSession.PrepareClock(execution, EcsSystemContext.Clock(queue.Entity).Current);
    }
}

/// <summary>Queue 阶段处理地点、材料、阻塞停泊与循环轮转；只准备一轮。</summary>
[SystemOrder(200)]
public sealed class ActionQueueSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (!EcsSystemContext.ShouldUpdate(queue.Entity)) return;
        using var measurement = RuntimeInstrumentation.MeasureCurrent("ecs.system.run.ActionQueueSystem");
        if (EcsSystemContext.Execution(queue.Entity) is { } execution)
            EcsActionSession.PrepareQueue(execution);
    }
}

/// <summary>采集阶段直接推进当前轮的整秒进度和持续收益。</summary>
[SystemOrder(300)]
public sealed class GatheringActionSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (!EcsSystemContext.ShouldUpdate(queue.Entity)) return;
        using var measurement = RuntimeInstrumentation.MeasureCurrent("ecs.system.run.GatheringActionSystem");
        if (EcsSystemContext.Execution(queue.Entity) is { } execution
            && EcsActionSession.PreparedKind(execution) == ActionKind.Exploration)
            EcsActionSession.AdvancePreparedAction(execution);
    }
}

/// <summary>制造阶段直接推进通过材料准入的当前轮；到期物品在 Yield 阶段结算。</summary>
[SystemOrder(310)]
public sealed class CraftingActionSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (!EcsSystemContext.ShouldUpdate(queue.Entity)) return;
        using var measurement = RuntimeInstrumentation.MeasureCurrent("ecs.system.run.CraftingActionSystem");
        if (EcsSystemContext.Execution(queue.Entity) is { } execution
            && EcsActionSession.PreparedKind(execution) == ActionKind.Alchemy)
            EcsActionSession.AdvancePreparedAction(execution);
    }
}

/// <summary>其余普通计时行动共用单轮原语，战斗仍由独立战斗实体系统推进。</summary>
[SystemOrder(320)]
public sealed class ActionBoundarySystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (!EcsSystemContext.ShouldUpdate(queue.Entity)) return;
        using var measurement = RuntimeInstrumentation.MeasureCurrent("ecs.system.run.ActionBoundarySystem");
        if (EcsSystemContext.Execution(queue.Entity) is { } execution
            && EcsActionSession.HasPreparedAction(execution)
            && EcsActionSession.PreparedKind(execution) is not (ActionKind.Exploration or ActionKind.Alchemy))
            EcsActionSession.AdvancePreparedAction(execution);
    }
}

/// <summary>移动系统直接修改路径分段、地图坐标与到达后的队列。</summary>
[SystemOrder(330)]
public sealed class TravelActionSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (!EcsSystemContext.ShouldUpdate(queue.Entity)) return;
        using var measurement = RuntimeInstrumentation.MeasureCurrent("ecs.system.run.TravelActionSystem");
        if (!EcsSystemContext.Clock(queue.Entity).PreparationOnly
            && EcsSystemContext.Execution(queue.Entity) is { } execution)
            EcsActionSession.AdvanceTravel(execution);
    }
}

/// <summary>Yield 阶段只提交本轮实际到期的奖励、材料扣除、账本与队列完成状态。</summary>
[SystemOrder(400)]
public sealed class TimedActionYieldSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (!EcsSystemContext.ShouldUpdate(queue.Entity)) return;
        using var measurement = RuntimeInstrumentation.MeasureCurrent("ecs.system.run.TimedActionYieldSystem");
        if (EcsSystemContext.Execution(queue.Entity) is { } execution)
            EcsActionSession.CompleteYield(execution);
    }
}

/// <summary>战斗准备边界：仅创建搜寻/Encounter 实体，不推进战斗单位；单位推进由 CombatEventAdvanceSystem 完成。</summary>
[SystemOrder(550)]
public sealed class CombatActionBoundarySystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        var clock = EcsSystemContext.Clock(queue.Entity);
        if (!EcsSystemContext.ShouldUpdate(queue.Entity)) return;
        using var measurement = RuntimeInstrumentation.MeasureCurrent("ecs.system.run.CombatActionBoundarySystem");
        if (clock.PreparationOnly) return;
        if (EcsSystemContext.Execution(queue.Entity) is { } execution)
            EcsActionSession.AdvanceCombatPreparation(execution);
    }
}

/// <summary>战斗资源片段结束后提交死亡、掉落、技能与远征阶段。</summary>
[SystemOrder(700)]
public sealed class CombatActionYieldSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (!EcsSystemContext.ShouldUpdate(queue.Entity)) return;
        using var measurement = RuntimeInstrumentation.MeasureCurrent("ecs.system.run.CombatActionYieldSystem");
        if (!EcsSystemContext.Clock(queue.Entity).PreparationOnly
            && EcsSystemContext.Execution(queue.Entity) is { } execution)
            EcsActionSession.CompleteCombatYield(execution);
    }
}

/// <summary>Fiber 内提交本帧实体时钟，并清理领域边界一次性标记。</summary>
[SystemOrder(800)]
public sealed class PlayerClockCommitSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        var clock = EcsSystemContext.Clock(queue.Entity);
        if (clock.ActivePlayerId is { } active
            && queue.Entity.GetComponent<PlayerIdentityComponent>()?.PlayerId != active)
            return;
        using var measurement = RuntimeInstrumentation.MeasureCurrent("ecs.system.run.PlayerClockCommitSystem");
        if (clock.SkipWorldUpdate)
        {
            clock.SkipWorldUpdateEntities = Math.Max(0, clock.SkipWorldUpdateEntities - 1);
            if (clock.SkipWorldUpdateEntities == 0) clock.SkipWorldUpdate = false;
            return;
        }
        if (EcsSystemContext.Execution(queue.Entity) is { } execution)
            EcsActionSession.CompleteClock(execution, clock.PreparationOnly);
    }
}
