using DaoHua.Ecs.Components;
using Domain.Player;
using Domain.Simulation;
using YuoCore.Ecs;

namespace DaoHua.Ecs;

/// <summary>队列事件同步派发，不进入 Update 扫描；普通命令与批量推进共用。</summary>
public interface IPlayerActionQueueMutation : ISystemTag;

/// <summary>复用队列操作工作区，只在同步派发期间借用调用方状态。</summary>
public sealed class PlayerActionQueueMutationComponent : YuoComponent
{
    /// <summary>队列规则管线的工作区；Begin/End 生命周期内绑定玩家视图与队列列表，其余时刻为空。</summary>
    public PlayerActionQueueMutationContext Work { get; } = new();
    /// <summary>实体级队列派发器；分片创建玩家时装配，命令门面与行动上下文共用。</summary>
    public IPlayerActionQueueMutationSink Sink { get; internal set; } = null!;
}

/// <summary>实体级队列入口；负载消费完成后立即解除状态引用。</summary>
public sealed class EcsPlayerActionQueueMutationSink(YuoEntity entity, int maxBattleReports)
    : IPlayerActionQueueMutationSink
{
    /// <summary>同步派发一条队列操作：绑定工作区 → RunSystem 按阶段消费 → 返回结果并解除绑定。</summary>
    public PlayerActionQueueMutationResult Dispatch(IPlayerResourceContext player, List<QueuedAction> actions,
        in PlayerActionQueueMutation mutation)
    {
        var work = entity.GetComponent<PlayerActionQueueMutationComponent>()!.Work;
        work.Begin(player, actions, mutation, maxBattleReports);
        try
        {
            YuoWorld.RunSystem<IPlayerActionQueueMutation>(entity);
            return work.Result;
        }
        finally { work.End(); }
    }
}

/// <summary>先校验并固定归档目标，任何无效输入都不得进入后续副反应。</summary>
[SystemOrder(100)]
public sealed class PlayerActionQueuePrepareSystem : YuoSystem<PlayerActionQueueMutationComponent, PlayerActionQueueComponent>, IPlayerActionQueueMutation
{
    /// <summary>管线阶段一：校验意图并固定归档名单；未命中实例时置 Changed=false 短路后续阶段。</summary>
    public override void Run(PlayerActionQueueMutationComponent mutation, PlayerActionQueueComponent queue)
        => PlayerActionQueueRules.Prepare(mutation.Work);
}

/// <summary>取消死亡远征时先恢复最低气血，保持原取消结算顺序。</summary>
[SystemOrder(110)]
public sealed class PlayerActionQueueRecoverySystem : YuoSystem<PlayerActionQueueMutationComponent, PlayerResourcesComponent>, IPlayerActionQueueMutation
{
    /// <summary>管线阶段二：死亡远征被移除时把气血恢复到 1 点并标脏 Combat 分段。</summary>
    public override void Run(PlayerActionQueueMutationComponent mutation, PlayerResourcesComponent resources)
    {
        var work = mutation.Work;
        if (!work.RecoverHitpoints) return;
        resources.Hitpoints = 1;
        work.Player.MarkDirty(PlayerSectionKind.Combat);
    }
}

/// <summary>实例离队前归档账本；优先插队替换的旧实例也在此消费。</summary>
[SystemOrder(120)]
public sealed class PlayerActionQueueReportArchiveSystem : YuoSystem<PlayerActionQueueMutationComponent, PlayerDuelComponent>, IPlayerActionQueueMutation
{
    /// <summary>管线阶段三：把 Prepare 固定的归档名单逐个归档出队战报（含插队替换的旧实例）。</summary>
    public override void Run(PlayerActionQueueMutationComponent mutation, PlayerDuelComponent duel)
    {
        var work = mutation.Work;
        foreach (var instanceId in work.ArchiveIds)
            ActionReportBook.ArchiveActionReport(work.Player, instanceId, work.EndedAtUnixSeconds, work.MaxBattleReports);
    }
}

/// <summary>执行单份队列结构规则，恢复 Pending 队首并提交调用方的版本与脏流水。</summary>
[SystemOrder(130)]
public sealed class PlayerActionQueueApplySystem : YuoSystem<PlayerActionQueueMutationComponent, PlayerActionQueueComponent>, IPlayerActionQueueMutation
{
    /// <summary>管线阶段四：执行队列结构变更、激活队首并提交版本与脏标记。</summary>
    public override void Run(PlayerActionQueueMutationComponent mutation, PlayerActionQueueComponent queue)
    {
        PlayerActionQueueRules.ApplyStructure(mutation.Work);
        PlayerActionQueueRules.ActivateHead(mutation.Work);
        PlayerActionQueueRules.Commit(mutation.Work);
    }
}

/// <summary>命令切换队首时立即释放旧遭遇；推进会话在战斗段 Yield 保留其既有清理点。</summary>
[SystemOrder(140)]
public sealed class PlayerActionQueueCombatReleaseSystem : YuoSystem<PlayerActionQueueMutationComponent, PlayerCombatComponent>, IPlayerActionQueueMutation
{
    /// <summary>管线阶段五：队首变化且非推进会话时释放旧战斗实体。</summary>
    public override void Run(PlayerActionQueueMutationComponent mutation, PlayerCombatComponent combat)
    {
        var work = mutation.Work;
        // 推进会话（EcsPlayerActionContext）的遭遇释放由战斗段 Yield 清理点负责，这里只处理命令路径。
        if (!work.Changed || !work.HeadChanged || combat.EncounterEntityId == 0
            || work.Player is EcsPlayerActionContext) return;
        mutation.Entity.World.WorldMain.GetComponent<EcsClockComponent>()!.ReleaseCombatBattle!(mutation.Entity);
    }
}
