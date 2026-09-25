using Domain.GameData;
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


}

/// <summary>Clock 阶段开启资源自然窗口；准备阶段不推进实际时间。</summary>
[SystemOrder(100)]
public sealed class ActionClockSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (EcsSystemContext.ShouldUpdate(queue.Entity))
            EcsActionPipeline.Find(queue.Entity)?.PrepareClock(EcsSystemContext.Clock(queue.Entity).Current);
    }
}

/// <summary>Queue 阶段处理地点、材料、阻塞停泊与循环轮转；只准备一轮。</summary>
[SystemOrder(200)]
public sealed class ActionQueueSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (EcsSystemContext.ShouldUpdate(queue.Entity))
            EcsActionPipeline.Find(queue.Entity)?.PrepareQueue();
    }
}

/// <summary>采集阶段直接推进当前轮的整秒进度和持续收益。</summary>
[SystemOrder(300)]
public sealed class GatheringActionSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (!EcsSystemContext.ShouldUpdate(queue.Entity)) return;
        if (EcsActionPipeline.Find(queue.Entity) is { PreparedKind: ActionKind.Exploration } pipeline)
            pipeline.AdvancePreparedAction();
    }
}

/// <summary>制造阶段直接推进通过材料准入的当前轮；到期物品在 Yield 阶段结算。</summary>
[SystemOrder(310)]
public sealed class CraftingActionSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (!EcsSystemContext.ShouldUpdate(queue.Entity)) return;
        if (EcsActionPipeline.Find(queue.Entity) is { PreparedKind: ActionKind.Alchemy } pipeline)
            pipeline.AdvancePreparedAction();
    }
}

/// <summary>其余普通计时行动共用单轮原语，战斗仍由独立战斗实体系统推进。</summary>
[SystemOrder(320)]
public sealed class ActionBoundarySystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (!EcsSystemContext.ShouldUpdate(queue.Entity)) return;
        if (EcsActionPipeline.Find(queue.Entity) is { HasPreparedAction: true } pipeline
            && pipeline.PreparedKind is not (ActionKind.Exploration or ActionKind.Alchemy))
            pipeline.AdvancePreparedAction();
    }
}

/// <summary>移动系统直接修改路径分段、地图坐标与到达后的队列。</summary>
[SystemOrder(330)]
public sealed class TravelActionSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (EcsSystemContext.ShouldUpdate(queue.Entity) && !EcsSystemContext.Clock(queue.Entity).PreparationOnly)
            EcsActionPipeline.Find(queue.Entity)?.AdvanceTravel();
    }
}

/// <summary>Yield 阶段只提交本轮实际到期的奖励、材料扣除、账本与队列完成状态。</summary>
[SystemOrder(400)]
public sealed class TimedActionYieldSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (EcsSystemContext.ShouldUpdate(queue.Entity))
            EcsActionPipeline.Find(queue.Entity)?.CompleteYield();
    }
}

/// <summary>战斗准备边界：仅创建搜寻/Encounter 实体，不推进战斗单位；单位推进由 CombatEventAdvanceSystem 完成。</summary>
[SystemOrder(550)]
public sealed class CombatActionBoundarySystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        var clock = EcsSystemContext.Clock(queue.Entity);
        if (!EcsSystemContext.ShouldUpdate(queue.Entity) || clock.PreparationOnly) return;
        EcsActionPipeline.Find(queue.Entity)?.AdvanceCombatPreparation();
    }
}

/// <summary>战斗资源片段结束后提交死亡、掉落、技能与远征阶段。</summary>
[SystemOrder(700)]
public sealed class CombatActionYieldSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        if (EcsSystemContext.ShouldUpdate(queue.Entity) && !EcsSystemContext.Clock(queue.Entity).PreparationOnly)
            EcsActionPipeline.Find(queue.Entity)?.CompleteCombatYield();
    }
}

/// <summary>Fiber 内提交本帧实体时钟，并清理领域边界一次性标记。</summary>
[SystemOrder(800)]
public sealed class PlayerClockCommitSystem : YuoSystem<PlayerActionQueueComponent>, IUpdate
{
    public override void Run(PlayerActionQueueComponent queue)
    {
        var clock = EcsSystemContext.Clock(queue.Entity);
        if (clock.SkipWorldUpdate)
        {
            if (clock.ActivePlayerId is null || queue.Entity.GetComponent<PlayerIdentityComponent>()?.PlayerId == clock.ActivePlayerId)
            {
                clock.SkipWorldUpdateEntities = Math.Max(0, clock.SkipWorldUpdateEntities - 1);
                if (clock.SkipWorldUpdateEntities == 0) clock.SkipWorldUpdate = false;
            }
            return;
        }
        if (clock.ActivePlayerId is { } active
            && queue.Entity.GetComponent<PlayerIdentityComponent>()?.PlayerId != active)
            return;
        if (EcsActionPipeline.Find(queue.Entity) is { } pipeline)
        {
            pipeline.CompleteClock(clock.PreparationOnly);
            return;
        }
    }
}
