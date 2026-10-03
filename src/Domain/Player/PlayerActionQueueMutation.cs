using System.Collections.ObjectModel;
using Domain.Simulation;

namespace Domain.Player;

/// <summary>队列变化的业务原因；消息只携带已确定的操作，不持有运行时实体。</summary>
public enum PlayerActionQueueMutationKind : byte
{
    Enqueue,
    EnqueuePriority,
    Remove,
    Move,
    Rotate,
    FinishRound
}

/// <summary>轮完成后的队列裁决；无限重复属于 Continue，不进入离队分支。</summary>
public enum PlayerActionRoundDisposition : byte
{
    Continue,
    Recycled,
    Removed
}

/// <summary>同步队列操作负载，不进入存档或协议。</summary>
public readonly record struct PlayerActionQueueMutation(
    PlayerActionQueueMutationKind Kind,
    Guid InstanceId = default,
    ActionIntent? Intent = null,
    DateTimeOffset At = default,
    ActionCatalog? Catalog = null,
    TravelActionState? Travel = null,
    bool IncrementVersion = true,
    double EndedAtUnixSeconds = -1,
    int Direction = 0,
    bool Loopable = true);

public readonly record struct PlayerActionQueueMutationResult(
    bool Changed, QueuedAction? Action, PlayerActionRoundDisposition RoundDisposition);

/// <summary>命令门面与行动上下文借用的同步队列入口；实现不得保存调用方状态引用。</summary>
public interface IPlayerActionQueueMutationSink
{
    PlayerActionQueueMutationResult Dispatch(IPlayerResourceContext player, List<QueuedAction> actions,
        in PlayerActionQueueMutation mutation);
}

/// <summary>
/// 队列规则的临时工作区。实体路径由请求组件复用，独立聚合由命令门面复用；
/// 归档目标在结构变更前确定，任何阶段不重新寻找已经离队的实例。
/// </summary>
public sealed class PlayerActionQueueMutationContext
{
    public IPlayerResourceContext Player { get; private set; } = null!;
    public List<QueuedAction> Actions { get; private set; } = null!;
    public PlayerActionQueueMutation Mutation { get; private set; }
    public int MaxBattleReports { get; private set; }
    public List<Guid> ArchiveIds { get; } = [];
    public int Index { get; internal set; }
    public int TargetIndex { get; internal set; }
    public bool Changed { get; internal set; }
    public bool RecoverHitpoints { get; internal set; }
    public bool ActivateHead { get; internal set; }
    public bool IncrementVersion { get; internal set; }
    public Guid? PreviousHeadId { get; private set; }
    public QueuedAction? Action { get; internal set; }
    public PlayerActionRoundDisposition RoundDisposition { get; internal set; }
    public bool HeadChanged => PreviousHeadId != (Actions.Count == 0 ? null : Actions[0].Id);
    public double EndedAtUnixSeconds => Mutation.EndedAtUnixSeconds >= 0
        ? Mutation.EndedAtUnixSeconds
        : Mutation.Kind == PlayerActionQueueMutationKind.EnqueuePriority
            ? Mutation.At.ToUnixTimeMilliseconds() / 1000d
            : Player.LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d;

    public void Begin(IPlayerResourceContext player, List<QueuedAction> actions,
        in PlayerActionQueueMutation mutation, int maxBattleReports)
    {
        if (Player is not null) throw new InvalidOperationException("队列变更不允许重入。");
        Player = player;
        Actions = actions;
        Mutation = mutation;
        MaxBattleReports = maxBattleReports;
        PreviousHeadId = actions.Count == 0 ? null : actions[0].Id;
        Index = TargetIndex = -1;
        Changed = RecoverHitpoints = ActivateHead = false;
        IncrementVersion = mutation.IncrementVersion;
        Action = null;
        RoundDisposition = PlayerActionRoundDisposition.Continue;
        ArchiveIds.Clear();
    }

    public PlayerActionQueueMutationResult Result => new(Changed, Action, RoundDisposition);

    public void End()
    {
        Player = null!;
        Actions = null!;
        Mutation = default;
        Action = null;
        ArchiveIds.Clear();
    }
}

/// <summary>
/// 独立于 ECS 的队列状态原语。参数归一化、结构变更和重复裁决各只有一份实现；
/// 实体 System 在固定阶段调用这些原语，并承担归档、资源恢复和运行时实体清理。
/// </summary>
public static class PlayerActionQueueRules
{
    public static void Prepare(PlayerActionQueueMutationContext work)
    {
        var mutation = work.Mutation;
        var actions = work.Actions;
        switch (mutation.Kind)
        {
            case PlayerActionQueueMutationKind.Enqueue:
            case PlayerActionQueueMutationKind.EnqueuePriority:
            {
                var priority = mutation.Kind == PlayerActionQueueMutationKind.EnqueuePriority;
                var intent = ValidateIntent(mutation.Intent, mutation.Catalog, priority);
                if (priority)
                    for (var i = actions.Count - 1; i >= 0; i--)
                        if (actions[i].Intent.ActionId == intent.ActionId) work.ArchiveIds.Add(actions[i].Id);
                if (actions.Count - work.ArchiveIds.Count >= PlayerAggregate.MaxActionQueueLength)
                    throw new DomainException(DomainErrorCode.Conflict, $"行动队列最多只能包含 {PlayerAggregate.MaxActionQueueLength} 项。");
                var instanceId = mutation.InstanceId == Guid.Empty ? Guid.NewGuid() : mutation.InstanceId;
                var normalized = new ActionIntent(intent.ActionId, priority ? 1 : intent.RepeatCount,
                    intent.TargetNodeId,
                    intent.Parameters is null ? null : new ReadOnlyDictionary<string, double>(
                        intent.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)),
                    intent.PvpEnabled,
                    priority ? intent.AnchorNodeId : intent.AnchorNodeId ??
                        (work.Player.World.EdgeId is null ? work.Player.World.NodeId : null),
                    intent.TargetItemId,
                    intent.AuxItemId);
                work.Action = new(instanceId, normalized, mutation.At) { Travel = mutation.Travel };
                work.ActivateHead = priority;
                work.Changed = true;
                return;
            }
            case PlayerActionQueueMutationKind.Move:
                if (mutation.Direction is not (-1 or 1))
                    throw new DomainException(DomainErrorCode.InvalidArgument, "行动移动方向必须为 -1 或 1。");
                break;
        }

        work.Index = actions.FindIndex(action => action.Id == mutation.InstanceId);
        if (work.Index < 0) return;
        work.Action = actions[work.Index];
        switch (mutation.Kind)
        {
            case PlayerActionQueueMutationKind.Remove:
                PrepareRemoval(work);
                break;
            case PlayerActionQueueMutationKind.Move:
                work.TargetIndex = work.Index + mutation.Direction;
                work.Changed = work.TargetIndex >= 0 && work.TargetIndex < actions.Count;
                work.ActivateHead = work.Changed;
                break;
            case PlayerActionQueueMutationKind.Rotate:
                work.Changed = actions.Count > 1;
                work.ActivateHead = work.Changed;
                break;
            case PlayerActionQueueMutationKind.FinishRound:
                // 负数重复始终保留，只有正数减到 0 才进入循环或离队裁决。
                if (work.Action.RemainingRepeats != 0) return;
                if (work.Player.Settings.LoopEnabled && mutation.Loopable)
                {
                    work.RoundDisposition = PlayerActionRoundDisposition.Recycled;
                    work.Changed = true;
                    work.ActivateHead = actions.Count > 1;
                    // 延续既有轮转版本时机：单项循环不额外提交版本。
                    work.IncrementVersion = actions.Count > 1;
                }
                else
                {
                    work.RoundDisposition = PlayerActionRoundDisposition.Removed;
                    PrepareRemoval(work);
                }
                break;
            default:
                throw new InvalidOperationException("未知队列变更类型。");
        }
    }

    private static ActionIntent ValidateIntent(ActionIntent? intent, ActionCatalog? catalog, bool priority)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (intent is null || !intent.ActionId.IsValid || (!priority && (!double.IsFinite(intent.RepeatCount)
            || (intent.RepeatCount != -1 && (intent.RepeatCount < 1 || intent.RepeatCount > 1_000_000
                || intent.RepeatCount != Math.Floor(intent.RepeatCount))))))
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动意图无效。");
        if (intent.TargetNodeId is not null && (intent.TargetNodeId.Length == 0 || intent.TargetNodeId.Length > 128))
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动目标节点无效。");
        if (intent.TargetItemId is not null && (intent.TargetItemId.Length == 0 || intent.TargetItemId.Length > 128))
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动目标物品无效。");
        if (intent.AuxItemId is not null && (intent.AuxItemId.Length == 0 || intent.AuxItemId.Length > 128))
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动辅助物品无效。");
        if (intent.Parameters is not null && intent.Parameters.Any(pair =>
            string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 64 || !double.IsFinite(pair.Value)))
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动参数无效。");
        if (!catalog.Contains(intent.ActionId))
            throw new DomainException(DomainErrorCode.NotFound, $"行动 {intent.ActionId} 不存在。");
        var definition = catalog.Get(intent.ActionId);
        // 强化行动轮次时长由目标等级派生（5+t 恒正），配表 durationSeconds 为 0 不构成死循环。
        if (!priority && intent.RepeatCount == -1 && definition.DurationSeconds <= 0
            && definition.ExecutionKind != ActionExecutionKind.CombatEncounter
            && definition.Kind != ActionKind.Enhancement)
            throw new DomainException(DomainErrorCode.InvalidArgument, "无限重复行动必须具有正时长，避免模拟无法推进。");
        return intent;
    }

    private static void PrepareRemoval(PlayerActionQueueMutationContext work)
    {
        work.Changed = true;
        work.RecoverHitpoints = work.Action!.Expedition is not null && work.Player.Resources.Hitpoints <= 0;
        work.ArchiveIds.Add(work.Action.Id);
        work.ActivateHead = work.Index == 0;
    }

    public static void ApplyStructure(PlayerActionQueueMutationContext work)
    {
        if (!work.Changed) return;
        var actions = work.Actions;
        switch (work.Mutation.Kind)
        {
            case PlayerActionQueueMutationKind.Enqueue:
                actions.Add(work.Action!);
                break;
            case PlayerActionQueueMutationKind.EnqueuePriority:
                for (var i = actions.Count - 1; i >= 0; i--)
                    if (actions[i].Intent.ActionId == work.Action!.Intent.ActionId) actions.RemoveAt(i);
                if (actions.Count > 0 && actions[0].Status == ActionQueueStatus.Running)
                    actions[0].Status = ActionQueueStatus.Pending;
                work.Action!.Status = ActionQueueStatus.Running;
                actions.Insert(0, work.Action);
                break;
            case PlayerActionQueueMutationKind.Remove:
                actions.RemoveAt(work.Index);
                break;
            case PlayerActionQueueMutationKind.Move:
                (actions[work.Index], actions[work.TargetIndex]) = (actions[work.TargetIndex], actions[work.Index]);
                for (var i = 1; i < actions.Count; i++)
                    if (actions[i].Status == ActionQueueStatus.Running) actions[i].Status = ActionQueueStatus.Pending;
                break;
            case PlayerActionQueueMutationKind.Rotate:
                Rotate(work);
                break;
            case PlayerActionQueueMutationKind.FinishRound:
                if (work.RoundDisposition == PlayerActionRoundDisposition.Removed)
                    actions.RemoveAt(work.Index);
                else
                {
                    work.Action!.RemainingRepeats = work.Action.Intent.RepeatCount;
                    work.Action.ProgressSeconds = 0;
                    if (actions.Count > 1) Rotate(work);
                }
                break;
        }
    }

    private static void Rotate(PlayerActionQueueMutationContext work)
    {
        var action = work.Action!;
        work.Actions.RemoveAt(work.Index);
        if (action.Status == ActionQueueStatus.Running) action.Status = ActionQueueStatus.Pending;
        work.Actions.Add(action);
    }

    public static void ActivateHead(PlayerActionQueueMutationContext work)
    {
        if (work.Changed && work.ActivateHead && work.Actions.Count > 0
            && work.Actions[0].Status == ActionQueueStatus.Pending)
            work.Actions[0].Status = ActionQueueStatus.Running;
    }

    public static void Commit(PlayerActionQueueMutationContext work)
    {
        if (!work.Changed) return;
        if (work.IncrementVersion) work.Player.StateVersion = checked(work.Player.StateVersion + 1);
        // 模拟内移除与单项循环由轮结算统一标脏，保持已有脏流水和版本时机。
        if (work.IncrementVersion || work.Mutation.Kind == PlayerActionQueueMutationKind.EnqueuePriority)
            work.Player.MarkDirty(PlayerSectionKind.ActionQueue);
    }

    /// <summary>普通计时与战斗轮共同维护的次数递减原语，不负责收益和离队。</summary>
    public static void RecordCompletedRound(IPlayerActionState action)
    {
        action.CompletedRepeats += 1;
        if (action.RemainingRepeats > 0) action.RemainingRepeats -= 1;
    }

    /// <summary>独立聚合按与实体 tag 系统相同的阶段消费，队列规则不复制。</summary>
    public static PlayerActionQueueMutationResult DispatchInline(PlayerActionQueueMutationContext work,
        IPlayerResourceContext player, List<QueuedAction> actions, in PlayerActionQueueMutation mutation,
        int maxBattleReports)
    {
        work.Begin(player, actions, mutation, maxBattleReports);
        try
        {
            Prepare(work);
            if (work.RecoverHitpoints)
            {
                player.Resources.Hitpoints = 1;
                player.MarkDirty(PlayerSectionKind.Combat);
            }
            foreach (var instanceId in work.ArchiveIds)
                ActionReportBook.ArchiveActionReport(player, instanceId, work.EndedAtUnixSeconds, work.MaxBattleReports);
            ApplyStructure(work);
            ActivateHead(work);
            Commit(work);
            return work.Result;
        }
        finally { work.End(); }
    }
}
