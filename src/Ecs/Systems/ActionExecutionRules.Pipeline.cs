using DaoHua.Ecs.Components;
using DaoHua.Ecs;
using DaoHua.Ecs.Combat;
using Domain.Common;
using Domain.Combat;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using static DaoHua.Ecs.EcsActionSession;

namespace DaoHua.Ecs.Systems;

/// <summary>行动 System 的阶段规则；普通 tick 与离线批量路径直接复用同一工作组件和执行顺序。</summary>
internal static partial class ActionExecutionRules
{
    internal const int MaxTransitionsPerAdvance = 100_000;

    /// <summary>
    /// 在保持系统固定顺序的前提下合并无战斗自然窗口。
    /// 返回 false 表示队列已经切换到需要完整 ECS 战斗/旅行系统的行动，调用方应继续普通循环。
    /// </summary>
    public static bool AdvanceBulkTimedUntilUnsupported(EcsActionExecutionComponent e, CancellationToken cancellationToken)
    {
        while (!IsComplete(e))
        {
            if (!CanBulkAdvance(e)) return false;
            if (++e.BulkStepCount > MaxTransitionsPerAdvance)
                throw new DomainException(DomainErrorCode.InvariantViolation, "ECS 自然边界推进超过最大转换数。");
            cancellationToken.ThrowIfCancellationRequested();
            if (NeedsPreparation(e))
            {
                // 对应 ActionClock -> ActionQueue 的准备 tick；准备阶段不推进实际时间。
                PrepareClock(e, e.Cursor);
                PrepareQueue(e);
                if (!CanBulkAdvance(e)) return false;
            }

            if (IsComplete(e)) break;
            var next = NextBoundaryAt(e);
            if (next < e.Cursor)
                throw new DomainException(DomainErrorCode.InvariantViolation, "ECS 批量自然边界时钟发生倒退。");

            // 对应 Gathering/Crafting -> Yield -> ClockCommit 的固定执行顺序。
            e.FrameTarget = next;
            AdvancePreparedAction(e);
            CompleteYield(e);
            CompleteClock(e, preparationOnly: false);
        }

        return true;
    }

    /// <summary>Clock 阶段只开启资源自然窗口并记住本 tick 允许到达的时刻。</summary>
    public static void PrepareClock(EcsActionExecutionComponent e, DateTimeOffset frameTarget)
    {
        using var stageMeasurement = RuntimeInstrumentation.MeasureCurrent("ecs.stage.clock");
        if (IsComplete(e)) return;
        if (!e.WindowOpen)
        {
            if (e.Cursor >= e.ComputeTarget)
            {
                // offline.capped 只在跨越封顶边界的请求发一次：起点已越界的请求静默推进时钟。
                var capped = ApplyOfflineCap(e.Player, e.Target, e.Request, e.From <= e.IncomeCapBoundary);
                e.Versions.MarkRequestChanged(capped);
                e.Changed |= capped;
                e.Cursor = e.Target;
                return;
            }
            e.WindowStart = e.Cursor;
            FlushCombatState(e);
            var plan = PrepareResourceBoundary(e.Player, e.Catalog, e.WorkingWallet, e.ComputeTarget, e.Request);
            e.WindowStepSeconds = plan.StepSeconds;
            e.WindowEnd = e.WindowStart.AddSeconds(plan.StepSeconds);
            e.WindowElapsedSeconds = (e.WindowEnd - e.WindowStart).TotalSeconds;
            e.RemainingSeconds = e.WindowElapsedSeconds;
            e.Core = new();
            e.Transitions = 0;
            e.Waiting = false;
            e.WindowOpen = true;
            e.Versions.MarkRequestChanged(plan.Changed);
            e.Changed |= plan.Changed;
        }
        e.FrameTarget = frameTarget < e.WindowEnd ? frameTarget : e.WindowEnd;
    }

    /// <summary>Queue 阶段完成一次准入；重复准备 tick 复用已准备的同一轮，不重复扣用品。</summary>
    public static void PrepareQueue(EcsActionExecutionComponent e)
    {
        using var stageMeasurement = RuntimeInstrumentation.MeasureCurrent("ecs.stage.queue");
        if (IsComplete(e) || !e.WindowOpen || e.PreparedAction is not null || e.Waiting) return;
        var blockedActions = new HashSet<Guid>();
        // 暂停不能绕过已到期参与会话；具体到期判断在队首循环内先于暂停执行。
        while (e.RemainingSeconds > 1e-9 && e.Player.Actions.Count > 0)
        {
            var action = e.Queue.Actions[0];
            if (action.Participation is { } participation && double.IsFinite(participation.EndsAtUnixSeconds)
                && e.Cursor.ToUnixTimeMilliseconds() / 1000d + 1e-6 >= participation.EndsAtUnixSeconds)
            {
                FlushCombatState(e);
                var sessionCombat = e.Entity.GetComponent<PlayerCombatComponent>();
                if (sessionCombat is { EncounterEntityId: not 0 })
                    e.Entity.World.WorldMain.GetComponent<EcsClockComponent>()!.ReleaseCombatBattle!(e.Entity);
                e.Battle = null;
                e.Player.RemoveAction(action.Id, incrementVersion: false, endedAtUnixSeconds: participation.EndsAtUnixSeconds);
                e.Core.AddEvent(new SimulationEvent(e.Cursor, "action.participation.ended", "行动参与会话已结束。", action.Intent.ActionId));
                CountTransition(e);
                continue;
            }
            // 成员参与行动仅是会话引用：等待 inbox 投影，禁止初始化或重演队伍战斗。
            if (action.Participation is not null)
            {
                e.Waiting = true;
                return;
            }
            if (e.Player.Settings.QueuePaused)
            {
                e.Waiting = true;
                return;
            }
            var definition = e.Catalog.Actions.Get(action.Intent.ActionId);
            // 阻塞恢复复核与 OOP 管线同一原语：濒死复苏归一气血 + 准入重跑，
            // 恢复转 Running 由后续分派接管；仍受阻才出队/轮转/停泊。
            if (action.Status == ActionQueueStatus.Blocked)
            {
                if (blockedActions.Contains(action.Id))
                {
                    e.Waiting = true;
                    return;
                }
                blockedActions.Add(action.Id);
                if (!AdvanceBlockedAction(e.Player, action, definition, e.Catalog,
                        e.WorkingWallet, e.Cursor, e.Core))
                {
                    e.Waiting = true;
                    return;
                }
                CountTransition(e);
                continue;
            }
            CountTransition(e);
            // 讨伐迁地判定须在准入前取值：TryStartAction 准入重锚后远征已被重建或清空。
            var staleExpedition = definition.ExecutionKind == ActionExecutionKind.CombatEncounter
                && action.Expedition is { } expedition
                && !CombatActionRules.ExpeditionServedHere(e.Player, definition, expedition, e.Catalog);
            if (!TryStartAction(e.Player, action, definition, e.Catalog, e.WorkingWallet, e.Cursor, e.Core)) continue;
            if (definition.ExecutionKind == ActionExecutionKind.CombatEncounter)
            {
                e.PreparedAction = action;
                e.Definition = definition;
                var playerCombat = e.Entity.GetComponent<PlayerCombatComponent>()!;
                // 模拟内挂起的旧地点战斗实体随迁地直接释放，由新地点搜寻重新入场。
                if (staleExpedition && playerCombat.EncounterEntityId != 0)
                {
                    e.Entity.World.WorldMain.GetComponent<EcsClockComponent>()!.ReleaseCombatBattle!(e.Entity);
                    e.Battle = null;
                }
                CombatActionRules.InitializeCombatAction(e.Player, action, definition, e.Catalog, e.Cursor, e.Core);
                if (action.Expedition!.Phase == CombatExpeditionPhase.Combat)
                {
                    e.Battle = playerCombat.EncounterEntityId == 0 ? null
                        : e.Entity.World.GetEntity(playerCombat.EncounterEntityId)?.GetComponent<CombatEncounterComponent>();
                    if (e.Battle is null)
                    {
                        var encounter = e.Catalog.CombatEncounters[action.Expedition.EncounterId];
                        var restored = CombatActionRules.RestoreCombatBattle(e.Player, action, encounter, e.Catalog);
                        e.Battle = AttachCombat(e, restored, e.Cursor.AddSeconds(-restored.ElapsedSeconds));
                    }
                    if (action.Participation?.Spec is { } sessionSpec)
                    {
                        // 参与会话面板开打即冻结：不随属性输入修订刷新，战外 Buff 视图取成员定格。
                        var selfMemberBuffs = sessionSpec.Members[e.Battle.State.SelfIndex].ExternalBuffs;
                        e.Battle.State.Player.ExternalBuffStacks = buffId
                            => CombatActionRules.SumFrozenBuffStacks(selfMemberBuffs, buffId);
                    }
                    else
                    {
                        var revision = PlayerAttributeResolver.InputRevision(e.Player.Progression, e.Player.World, e.Player.Buffs, e.Player.Loadout);
                        if (e.Catalog.BaseAttributes is null || revision != e.Battle.AttributeRevision)
                        {
                            e.Battle.State.Player.RefreshStats(e.Player.ResolveAttributes(e.Catalog).ToCombatant(e.Player.Progression.CombatExperience));
                            CombatTriggerSurface.Sync(e.Entity, e.Battle.State.Player);
                            e.Battle.AttributeRevision = revision;
                        }
                    }
                    e.BattleFrom = e.Battle.State.ElapsedSeconds;
                    e.BattleTarget = e.BattleFrom + e.RemainingSeconds;
                    e.BattleEvents.Clear();
                    e.BattleDeaths.Clear();
                    e.BattleRespawns.Clear();
                    e.BattleAllyDeaths.Clear();
                    e.BattleAllyRespawns.Clear();
                    e.CombatStepDue = false;
                }
                return;
            }
            if (definition.ExecutionKind == ActionExecutionKind.Travel)
            {
                if (!PrepareTravelAction(e.Player, action, definition, e.Catalog, e.Cursor, e.Core)) continue;
                e.PreparedAction = action;
                e.Definition = definition;
                return;
            }
            e.Multiplier = PrepareTimedAction(e.Player, action, definition, e.Catalog, e.WorkingWallet, e.Cursor, e.Core);
            var duration = TimedActionDuration(e.Player, action, definition, e.Catalog, e.WorkingWallet, e.Core.Deltas);
            if (duration <= 0)
            {
                e.Player.RemoveAction(action.Id, incrementVersion: false, endedAtUnixSeconds: e.Cursor.ToUnixTimeMilliseconds() / 1000d);
                e.Core.AddEvent(new SimulationEvent(e.Cursor, "action.invalid-dropped",
                    $"行动 {definition.Name} 的时长配置无效（{duration:0.######} 秒），已自动移出队列。", definition.Id));
                continue;
            }
            e.PreparedAction = action;
            e.Definition = definition;
            e.Duration = duration;
            return;
        }
        e.Waiting = true;
    }

    /// <summary>采集或制造阶段推进单个已裁剪片段；准备 tick 的零时长不会产生连续收益。</summary>
    public static void AdvancePreparedAction(EcsActionExecutionComponent e)
    {
        if (e.PreparedAction is not { } action || e.Definition is not { ExecutionKind: ActionExecutionKind.Timed } definition || e.RoundDue) return;
        if (e.FrameTarget < NextBoundaryAt(e)) return;
        var needed = Math.Max(0, e.Duration - action.ProgressSeconds);
        // FrameTarget >= NextBoundaryAt 已保证 min(remaining, needed) 落入本帧；
        // 不能再按 tick 量化差值推导可用量：亚 tick（~1e-7s）残差会让边界落回同一时刻形成不动点。
        var step = Math.Min(e.RemainingSeconds, needed);
        e.RemainingSeconds -= step;
        e.Cursor = e.WindowStart.AddSeconds(e.WindowElapsedSeconds - e.RemainingSeconds);
        AdvanceTimedAction(e.Player, action, definition, e.Multiplier, e.Catalog, step, e.Cursor);
        e.RoundDue = action.ProgressSeconds + 1e-9 >= e.Duration;
    }

    /// <summary>Yield 阶段只在本轮到期时执行一次物品、材料、统计与重复次数结算。</summary>
    public static void CompleteYield(EcsActionExecutionComponent e)
    {
        if (!e.RoundDue || e.PreparedAction is not { } action || e.Definition is not { } definition) return;
        CompleteTimedAction(e.Player, action, definition, e.Multiplier, e.Catalog, e.WorkingWallet, e.Cursor, e.Core);
        e.RoundDue = false;
        e.PreparedAction = null;
        e.Definition = null;
    }

    /// <summary>移动只在当前资源窗口或到达边界结算，保留跨路段运算顺序。</summary>
    public static void AdvanceTravel(EcsActionExecutionComponent e)
    {
        if (e.PreparedAction is not { } action || e.Definition is not { ExecutionKind: ActionExecutionKind.Travel } definition
            || e.FrameTarget < NextBoundaryAt(e)) return;
        e.RemainingSeconds = AdvanceTravelBoundary(e.Player, action, definition, e.Catalog,
            e.WindowStart, e.WindowEnd, e.RemainingSeconds, e.Core);
        e.Cursor = e.WindowStart.AddSeconds(e.WindowElapsedSeconds - e.RemainingSeconds);
        e.PreparedAction = null;
        e.Definition = null;
    }

    /// <summary>Completion 阶段只在真实资源窗口末提交 Core 时钟、版本、钱包与回复。</summary>
    public static void CompleteClock(EcsActionExecutionComponent e, bool preparationOnly)
    {
        if (IsComplete(e) || !e.WindowOpen || preparationOnly) return;
        // DateTimeOffset 以 tick（1e-7s）量化：窗口末残留的亚 tick 预算无法再用下一帧消费，
        // 到达窗口末时直接归零，否则 boundary==cursor 会构成死循环不动点。
        if (e.FrameTarget >= e.WindowEnd && (e.Waiting || e.RemainingSeconds <= 1e-6))
        {
            e.RemainingSeconds = 0;
            e.Cursor = e.WindowEnd;
        }
        if (e.RemainingSeconds > 1e-9) return;
        e.Cursor = e.WindowEnd;
        e.Player.Progression.AgeSeconds += e.WindowElapsedSeconds;
        e.Player.LastSimulatedAt = e.WindowEnd;
        var headActivated = false;
        if (e.Player.Actions.Count > 0 && e.Player.Actions[0].Status == ActionQueueStatus.Pending)
        {
            e.Player.Actions[0].Status = ActionQueueStatus.Running;
            headActivated = true;
            if (e.Transitions == 0) e.Player.MarkDirty(PlayerSectionKind.ActionQueue);
        }
        if (e.Transitions > 0)
        {
            e.Player.MarkDirty(PlayerSectionKind.Progression);
            e.Player.MarkDirty(PlayerSectionKind.ActionQueue);
        }
        // 队首 Pending→Running 是可见状态翻转：暂停队列等零转移窗口同样产生 Core 版本与推送，
        // 不计入会让队列显示长期停在「排队中」直到下一次真实转移。
        e.Versions.RecordCoreStep(e.Transitions > 0 || headActivated, endsNaturalBoundary: true);
        var changed = CompleteResourceBoundary(e.Player, e.Catalog, e.WorkingWallet,
            e.WindowStart, e.WindowStepSeconds, e.Core, e.Request);
        e.Versions.MarkRequestChanged(changed);
        e.Changed |= e.Transitions > 0 || headActivated || changed;
        e.WindowOpen = false;
        e.Waiting = false;
        e.PreparedAction = null;
        e.Definition = null;
    }

    internal static void CountTransition(EcsActionExecutionComponent e)
    {
        if (++e.Transitions > MaxTransitionsPerAdvance)
            throw new DomainException(DomainErrorCode.InvariantViolation, "单次模拟超过最大状态转换数。");
    }
}
