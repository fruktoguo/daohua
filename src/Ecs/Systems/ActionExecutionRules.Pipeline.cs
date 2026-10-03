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
                var capped = ApplyOfflineCap(e.Player, e.Target, e.Request);
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
        while (e.RemainingSeconds > 1e-9 && e.Player.Actions.Count > 0)
        {
            var action = e.Queue.Actions[0];
            if (action.Status == ActionQueueStatus.Blocked)
            {
                if (!AdvanceBlockedAction(e.Player, action, e.Cursor))
                {
                    e.Waiting = true;
                    return;
                }
                CountTransition(e);
                continue;
            }
            CountTransition(e);
            var definition = e.Catalog.Actions.Get(action.Intent.ActionId);
            if (!TryStartAction(e.Player, action, definition, e.Catalog, e.WorkingWallet, e.Cursor, e.Core)) continue;
            if (definition.ExecutionKind == ActionExecutionKind.CombatEncounter)
            {
                e.PreparedAction = action;
                e.Definition = definition;
                CombatActionRules.InitializeCombatAction(e.Player, action, definition, e.Catalog, e.Cursor, e.Core);
                if (action.Expedition!.Phase == CombatExpeditionPhase.Combat)
                {
                    var playerCombat = e.Entity.GetComponent<PlayerCombatComponent>()!;
                    e.Battle = playerCombat.EncounterEntityId == 0 ? null
                        : e.Entity.World.GetEntity(playerCombat.EncounterEntityId)?.GetComponent<CombatEncounterComponent>();
                    if (e.Battle is null)
                    {
                        var encounter = e.Catalog.CombatEncounters[action.Expedition.EncounterId];
                        var restored = CombatActionRules.RestoreCombatBattle(e.Player, action, encounter, e.Catalog);
                        e.Battle = AttachCombat(e, restored, e.Cursor.AddSeconds(-restored.ElapsedSeconds));
                    }
                    var revision = PlayerAttributeResolver.InputRevision(e.Player.Progression, e.Player.World, e.Player.Buffs, e.Player.Loadout);
                    if (e.Catalog.BaseAttributes is null || revision != e.Battle.AttributeRevision)
                    {
                        e.Battle.State.Player.RefreshStats(e.Player.ResolveAttributes(e.Catalog).ToCombatant(e.Player.Progression.CombatExperience));
                        CombatTriggerSurface.Sync(e.Entity, e.Battle.State.Player);
                        e.Battle.AttributeRevision = revision;
                    }
                    e.BattleFrom = e.Battle.State.ElapsedSeconds;
                    e.BattleTarget = e.BattleFrom + e.RemainingSeconds;
                    e.BattleEvents.Clear();
                    e.BattleDeaths.Clear();
                    e.BattleRespawns.Clear();
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
            e.Multiplier = PrepareTimedAction(e.Player, action, definition, e.Catalog, e.WorkingWallet, e.Cursor, e.Core);
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
        if (e.Player.Actions.Count > 0 && e.Player.Actions[0].Status == ActionQueueStatus.Pending)
        {
            e.Player.Actions[0].Status = ActionQueueStatus.Running;
            if (e.Transitions == 0) e.Player.MarkDirty(PlayerSectionKind.ActionQueue);
        }
        if (e.Transitions > 0)
        {
            e.Player.MarkDirty(PlayerSectionKind.Progression);
            e.Player.MarkDirty(PlayerSectionKind.ActionQueue);
        }
        e.Versions.RecordCoreStep(e.Transitions > 0, endsNaturalBoundary: true);
        var changed = CompleteResourceBoundary(e.Player, e.Catalog, e.WorkingWallet,
            e.WindowStart, e.WindowStepSeconds, e.Core, e.Request);
        e.Versions.MarkRequestChanged(changed);
        e.Changed |= e.Transitions > 0 || changed;
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
