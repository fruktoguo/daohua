using System.Collections.ObjectModel;
using Domain.Simulation;

namespace Domain.Player;

/// <summary>队列变化的业务原因；消息只携带已确定的操作，不持有运行时实体。</summary>
public enum PlayerActionQueueMutationKind : byte
{
    /// <summary>按顺序追加到队尾。</summary>
    Enqueue,
    /// <summary>高优插队到队首：同 ActionId 旧实例去重、原队首挂起、新行动立即 Running。</summary>
    EnqueuePriority,
    /// <summary>显式移除指定行动实例。</summary>
    Remove,
    /// <summary>队列内上下移动一位。</summary>
    Move,
    /// <summary>指定实例轮转到队尾。</summary>
    Rotate,
    /// <summary>单轮结算完成后的循环/离队裁决。</summary>
    FinishRound,
    /// <summary>一键清空整个队列。</summary>
    Clear
}

/// <summary>轮完成后的队列裁决；无限重复属于 Continue，不进入离队分支。</summary>
public enum PlayerActionRoundDisposition : byte
{
    /// <summary>留队继续推进（含无限重复与未触发离队条件）。</summary>
    Continue,
    /// <summary>剩余次数归零后按循环开关重置进度并轮转到队尾。</summary>
    Recycled,
    /// <summary>出队并归档行动账本。</summary>
    Removed
}

/// <summary>同步队列操作负载，不进入存档或协议。</summary>
/// <param name="Kind">队列变化类型。</param>
/// <param name="InstanceId">目标行动实例 Id；入队时可指定，缺省由规则层生成新 Id。</param>
/// <param name="Intent">入队行动意图（Enqueue/EnqueuePriority 必填并校验）。</param>
/// <param name="At">操作发生的墙钟时刻，用于入队时间与归档时刻推导。</param>
/// <param name="Catalog">行动配表目录，用于校验意图合法性。</param>
/// <param name="Travel">插队移动行动附带的路径状态。</param>
/// <param name="Participation">插队行动附带的行动参与绑定。</param>
/// <param name="IncrementVersion">本次变更是否提交聚合版本号；模拟内部推进传 false。</param>
/// <param name="EndedAtUnixSeconds">归档结束时刻（Unix 秒）；负数表示按变更类型推导。</param>
/// <param name="Direction">Move 方向，仅允许 -1/1。</param>
/// <param name="Loopable">FinishRound 是否允许走循环裁决；false 时次数归零即离队。</param>
public readonly record struct PlayerActionQueueMutation(
    PlayerActionQueueMutationKind Kind,
    Guid InstanceId = default,
    ActionIntent? Intent = null,
    DateTimeOffset At = default,
    ActionCatalog? Catalog = null,
    TravelActionState? Travel = null,
    ActionParticipationBinding? Participation = null,
    bool IncrementVersion = true,
    double EndedAtUnixSeconds = -1,
    int Direction = 0,
    bool Loopable = true);

/// <summary>队列变更管线的同步结果；不携带借用的运行时引用，可安全跨阶段返回。</summary>
/// <param name="Changed">本次变更是否实际改动了队列。</param>
/// <param name="Action">新建或命中的行动实例；未命中或无涉及时为 null。</param>
/// <param name="RoundDisposition">FinishRound 的离队/循环裁决；非 FinishRound 恒为 Continue。</param>
public readonly record struct PlayerActionQueueMutationResult(
    bool Changed, QueuedAction? Action, PlayerActionRoundDisposition RoundDisposition);

/// <summary>命令门面与行动上下文借用的同步队列入口；实现不得保存调用方状态引用。</summary>
public interface IPlayerActionQueueMutationSink
{
    /// <summary>按统一变更管线同步消费一条队列操作并返回结果。</summary>
    /// <param name="player">玩家状态视图（资源恢复、脏标与版本提交的直接对象）。</param>
    /// <param name="actions">被变更的行动队列可变列表。</param>
    /// <param name="mutation">队列操作负载。</param>
    PlayerActionQueueMutationResult Dispatch(IPlayerResourceContext player, List<QueuedAction> actions,
        in PlayerActionQueueMutation mutation);
}

/// <summary>
/// 队列规则的临时工作区。实体路径由请求组件复用，独立聚合由命令门面复用；
/// 归档目标在结构变更前确定，任何阶段不重新寻找已经离队的实例。
/// </summary>
public sealed class PlayerActionQueueMutationContext
{
    /// <summary>本次变更绑定的玩家状态视图；仅 Begin/End 生命周期内有效。</summary>
    public IPlayerResourceContext Player { get; private set; } = null!;
    /// <summary>被变更的行动队列可变列表；实现不得在 End 之后继续持有。</summary>
    public List<QueuedAction> Actions { get; private set; } = null!;
    /// <summary>本次队列操作负载。</summary>
    public PlayerActionQueueMutation Mutation { get; private set; }
    /// <summary>行动账本归档保留上限（入队校验与归档裁剪共用）。</summary>
    public int MaxBattleReports { get; private set; }
    /// <summary>待归档行动实例 ID；在结构变更前确定，任何阶段不重新寻找已离队的实例。</summary>
    public List<Guid> ArchiveIds { get; } = [];
    /// <summary>目标实例在队列中的索引；未命中为 -1。</summary>
    public int Index { get; internal set; }
    /// <summary>Move 操作的交换目标索引。</summary>
    public int TargetIndex { get; internal set; }
    /// <summary>本次变更是否实际改动队列；未改动时后续阶段直接跳过。</summary>
    public bool Changed { get; internal set; }
    /// <summary>死亡中的远征队首被移除时需按同语义恢复最低气血（1 点）。</summary>
    public bool RecoverHitpoints { get; internal set; }
    /// <summary>结构变更后是否需要把队首从 Pending 激活为 Running。</summary>
    public bool ActivateHead { get; internal set; }
    /// <summary>是否提交聚合版本号；模拟内部推进传 false，由轮结算统一提交。</summary>
    public bool IncrementVersion { get; internal set; }
    /// <summary>Begin 时记录的原队首 ID，用于 HeadChanged 判定；空队为 null。</summary>
    public Guid? PreviousHeadId { get; private set; }
    /// <summary>新建（入队）或命中（其余操作）的行动实例；无涉及时为 null。</summary>
    public QueuedAction? Action { get; internal set; }
    /// <summary>FinishRound 的循环/离队裁决；非 FinishRound 恒为 Continue。</summary>
    public PlayerActionRoundDisposition RoundDisposition { get; internal set; }
    /// <summary>队首是否相对 Begin 时刻发生变化（含清空队列）。</summary>
    public bool HeadChanged => PreviousHeadId != (Actions.Count == 0 ? null : Actions[0].Id);
    /// <summary>归档结束时刻：显式指定优先，插队取操作时刻，其余取最后推进时刻。</summary>
    public double EndedAtUnixSeconds => Mutation.EndedAtUnixSeconds >= 0
        ? Mutation.EndedAtUnixSeconds
        : Mutation.Kind == PlayerActionQueueMutationKind.EnqueuePriority
            ? Mutation.At.ToUnixTimeMilliseconds() / 1000d
            : Player.LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d;

    /// <summary>绑定工作区；重复绑定视为管线重入，立即失败以暴露所有权破坏。</summary>
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

    /// <summary>导出本次变更的同步结果；不携带借用的运行时引用。</summary>
    public PlayerActionQueueMutationResult Result => new(Changed, Action, RoundDisposition);

    /// <summary>解除绑定并清空借用引用；管线以 finally 保证调用，防止工作区跨请求残留。</summary>
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
    /// <summary>
    /// 管线阶段一：参数归一化与裁决。校验意图、确定归档名单与循环/离队裁决，
    /// 只写工作区不改队列结构；未命中实例或无可做变更时置 Changed=false 短路后续阶段。
    /// </summary>
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
                var intent = ValidateIntent(mutation.Intent, mutation.Catalog, priority, mutation.Participation);
                var personalActionCount = 0;
                var hasParticipation = false;
                for (var i = 0; i < actions.Count; i++)
                {
                    if (actions[i].Participation is not null) hasParticipation = true;
                    else personalActionCount++;
                }
                if (mutation.Participation is { } participation)
                {
                    if (!priority || !participation.IsValid)
                        throw new DomainException(DomainErrorCode.InvalidArgument, "行动参与绑定必须合法且优先入队。");
                    if (hasParticipation)
                        throw new DomainException(DomainErrorCode.Conflict, "行动队列只能包含一个参与引用。");
                }
                else
                {
                    if (priority && hasParticipation)
                        throw new DomainException(DomainErrorCode.Conflict, "行动参与期间不能优先插队。");
                    if (priority)
                        for (var i = actions.Count - 1; i >= 0; i--)
                            if (actions[i].Intent.ActionId == intent.ActionId) work.ArchiveIds.Add(actions[i].Id);
                    // 参与引用占独立槽位；个人优先插队只沿用同类去重，不删除其他行动腾槽。
                    if (personalActionCount - work.ArchiveIds.Count >= PlayerAggregate.MaxActionQueueLength)
                        throw new DomainException(DomainErrorCode.Conflict, $"个人行动队列最多只能包含 {PlayerAggregate.MaxActionQueueLength} 项。");
                }
                var instanceId = mutation.InstanceId == Guid.Empty ? Guid.NewGuid() : mutation.InstanceId;
                // 插队行动默认只跑一轮（RepeatCount=1）；行动参与例外——无限驻留队首
                // 直至散场时刻收尾（共享参与剧本），否则波次清空即走离队裁决导致 WaveNumber 恒 1。
                var normalized = new ActionIntent(intent.ActionId,
                    priority && mutation.Participation is null ? 1 : intent.RepeatCount,
                    intent.TargetNodeId,
                    intent.Parameters is null ? null : new ReadOnlyDictionary<string, double>(
                        intent.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)),
                    intent.PvpEnabled,
                    priority ? intent.AnchorNodeId : intent.AnchorNodeId ??
                        (work.Player.World.EdgeId is null ? work.Player.World.NodeId : null),
                    intent.TargetItemId,
                    intent.AuxItemId);
                work.Action = new(instanceId, normalized, mutation.At)
                    { Travel = mutation.Travel, Participation = mutation.Participation };
                work.ActivateHead = priority;
                work.Changed = true;
                return;
            }
            case PlayerActionQueueMutationKind.Move:
                if (mutation.Direction is not (-1 or 1))
                    throw new DomainException(DomainErrorCode.InvalidArgument, "行动移动方向必须为 -1 或 1。");
                break;
            case PlayerActionQueueMutationKind.Clear:
                work.Changed = actions.Count > 0;
                if (!work.Changed) return;
                // 远征状态只可能挂在队首实例：死亡中的队首远征按单项移除同语义恢复最低气血。
                work.RecoverHitpoints = actions[0].Expedition is not null && work.Player.Resources.Hitpoints <= 0;
                for (var i = 0; i < actions.Count; i++) work.ArchiveIds.Add(actions[i].Id);
                return;
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

    private static ActionIntent ValidateIntent(ActionIntent? intent, ActionCatalog? catalog, bool priority,
        ActionParticipationBinding? participation)
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
        // 已收尾会话只补投持久化引用并归档，不执行被新配表删除的行动。
        if (priority && participation is { IsValid: true } && double.IsFinite(participation.EndsAtUnixSeconds))
            return intent;
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

    /// <summary>管线阶段二：按 Prepare 裁决执行队列结构变更（增删/换位/轮转/清空）。</summary>
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
                if (work.Mutation.Participation is null)
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
            case PlayerActionQueueMutationKind.Clear:
                actions.Clear();
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

    /// <summary>管线阶段三：按裁决激活队首（仅 Changed 且裁决要求时把 Pending 队首置为 Running）。</summary>
    public static void ActivateHead(PlayerActionQueueMutationContext work)
    {
        if (work.Changed && work.ActivateHead && work.Actions.Count > 0
            && work.Actions[0].Status == ActionQueueStatus.Pending)
            work.Actions[0].Status = ActionQueueStatus.Running;
    }

    /// <summary>
    /// 管线阶段四：提交版本号与脏标记。模拟内移除与单项循环由轮结算统一标脏，
    /// 仅插队路径立即标脏以保证在线请求即时可见。
    /// </summary>
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
