using DaoHua.Ecs.Components;
using Domain.Common;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using YuoCore.Ecs;

namespace DaoHua.Ecs;

/// <summary>
/// 请求期行动执行工作组件：挂载在玩家实体上的单请求生命周期组件，
/// 保存窗口游标、资源钱包、变更批次与战斗实体句柄；行动 System 直接读写其字段。
/// 构造/装配经 <see cref="EcsActionSession"/>，请求末 <see cref="Commit"/> 后自动摘除工作态。
/// </summary>
public sealed class EcsActionExecutionComponent : YuoComponent
{
    // —— 基础设施引用（Attach 时装配，请求期内不变） ——
    internal YuoEntity Entity = null!;
    internal PlayerActionQueueComponent Queue = null!;
    internal GameDataCatalog Catalog = null!;
    internal EcsPlayerActionContext Player = null!;
    internal AssetWallet ExternalWallet = null!;
    internal AssetWallet WorkingWallet = null!;
    internal ActionMutationBatch Request = null!;
    internal SimulationEngine.StateVersionCommitSession Versions = null!;
    internal ActionMutationBatch Core = null!;

    // —— 推进游标与窗口 ——
    internal DateTimeOffset From;
    internal DateTimeOffset Cursor;
    internal DateTimeOffset WindowStart;
    internal DateTimeOffset WindowEnd;
    internal DateTimeOffset FrameTarget;
    internal DateTimeOffset ComputeTarget;
    internal double WindowStepSeconds;
    internal double RemainingSeconds;
    internal double WindowElapsedSeconds;
    internal int Transitions;
    internal bool WindowOpen;
    internal bool Waiting;
    internal bool Changed;
    internal bool Committed;

    // —— 本轮准备态 ——
    internal QueuedAction? PreparedAction;
    internal ActionDefinition? Definition;
    internal double Duration;
    internal double Multiplier;
    internal bool RoundDue;

    // —— 战斗段句柄与事件收集 ——
    internal CombatEncounterComponent? Battle;
    internal double BattleFrom;
    internal double BattleTarget;
    internal bool CombatStepDue;
    internal readonly List<CombatEvent> BattleEvents = [];
    internal readonly List<double> BattleDeaths = [];
    internal readonly List<double> BattleRespawns = [];

    internal int BulkStepCount { get; set; }
    public DateTimeOffset Target { get; internal set; }

    public static EcsActionExecutionComponent Attach(
        YuoEntity entity, GameDataCatalog catalog, DateTimeOffset target)
    {
        var component = entity.GetOrAddComponent<EcsActionExecutionComponent>();
        if (component.Entity is not null && !component.Committed)
            throw new InvalidOperationException("行动请求尚未结束。");
        var queue = entity.GetComponent<PlayerActionQueueComponent>()!;
        var player = new EcsPlayerActionContext(entity, catalog);
        var from = player.LastSimulatedAt;
        if (target < from) throw new DomainException(DomainErrorCode.InvalidArgument, "行动目标时间不能倒退。");
        var external = new AssetWallet(player.PlayerId);
        external.Restore(entity.GetComponent<PlayerInventoryComponent>()!.Items.Select(
            pair => new AssetBalance(player.PlayerId, pair.Key, pair.Value, 0)));
        var working = new AssetWallet(player.PlayerId);
        working.Restore(external.Balances);
        component.Entity = entity;
        component.Queue = queue;
        component.Catalog = catalog;
        component.Player = player;
        component.ExternalWallet = external;
        component.WorkingWallet = working;
        component.Request = new();
        component.Versions = new();
        component.Core = new();
        component.From = component.Cursor = from;
        component.ComputeTarget = target > from.AddHours(48) ? from.AddHours(48) : target;
        component.Target = target;
        component.WindowStart = component.WindowEnd = component.FrameTarget = from;
        component.WindowStepSeconds = component.RemainingSeconds = component.WindowElapsedSeconds = 0;
        component.Transitions = 0;
        component.WindowOpen = component.Waiting = component.Changed = component.Committed = false;
        component.PreparedAction = null;
        component.Definition = null;
        component.Duration = component.Multiplier = 0;
        component.RoundDue = false;
        component.Battle = null;
        component.BattleFrom = component.BattleTarget = 0;
        component.CombatStepDue = false;
        component.BattleEvents.Clear();
        component.BattleDeaths.Clear();
        component.BattleRespawns.Clear();
        component.BulkStepCount = 0;
        return component;
    }

    /// <summary>请求完成后的工作态摘除：实体保留组件壳但字段复位，下一请求 Attach 复用。</summary>
    public void Detach()
    {
        Entity = null!;
        Queue = null!;
        Catalog = null!;
        Player = null!;
        ExternalWallet = null!;
        WorkingWallet = null!;
        Request = null!;
        Versions = null!;
        Core = null!;
        PreparedAction = null;
        Definition = null;
        Battle = null;
    }
}

/// <summary>
/// 行动执行会话：横跨准备/推进/收尾多个 System 阶段的共享服务与不变量。
/// 每个 Update System 的 Run 体消费工作组件字段完成本阶段规则；
/// 本类只承载跨阶段设施（边界求值、批量推进、战斗实体挂接、提交）。
/// </summary>
public static class EcsActionSession
{
    public static DateTimeOffset Current(EcsActionExecutionComponent e) => e.Cursor;
    public static bool IsComplete(EcsActionExecutionComponent e) => e.Cursor >= e.Target && !e.WindowOpen;
    public static bool NeedsPreparation(EcsActionExecutionComponent e)
        => !IsComplete(e) && (!e.WindowOpen || (!e.Waiting && e.PreparedAction is null));
    public static bool HasPreparedAction(EcsActionExecutionComponent e) => e.PreparedAction is not null;
    public static ActionKind? PreparedKind(EcsActionExecutionComponent e) => e.Definition?.Kind;
    public static ActionExecutionKind? PreparedExecutionKind(EcsActionExecutionComponent e) => e.Definition?.ExecutionKind;

    /// <summary>不变量违例时的现场证据：窗口/等待/剩余预算/队首行动/战斗边界，供快速失败诊断。</summary>
    public static string DiagnosticState(EcsActionExecutionComponent e)
        => $"window={e.WindowOpen},waiting={e.Waiting},remaining={e.RemainingSeconds:0.######}," +
           $"windowEnd={e.WindowEnd:o},step={e.WindowStepSeconds:0.######},action={e.PreparedAction?.Id.ToString() ?? "-"}," +
           $"queued={e.Player.Actions.Count},kind={e.Definition?.ExecutionKind.ToString() ?? "-"}," +
           $"pk={e.Definition?.Kind.ToString() ?? "-"},battle={e.Battle is not null}," +
           $"battleOutcome={e.Battle?.State.Outcome.ToString() ?? "-"},battleElapsed={e.Battle?.State.ElapsedSeconds:0.###}," +
           $"battleTarget={e.BattleTarget:0.###},due={e.CombatStepDue},roundDue={e.RoundDue}," +
           $"frame={e.FrameTarget:o},phase={e.PreparedAction?.Expedition?.Phase.ToString() ?? "-"}," +
           $"actStatus={e.PreparedAction?.Status.ToString() ?? "-"}";

    /// <summary>
    /// 优化 ECS 的离线批处理资格。只有不包含战斗实体的计时行动才允许合并自然资源窗口；
    /// 战斗、旅行和跨类型行动仍由完整 ECS System 顺序推进，避免改变随机数和边界语义。
    /// </summary>
    public static bool CanBulkAdvance(EcsActionExecutionComponent e)
    {
        if (e.Battle is not null || e.PreparedAction?.Expedition is { Phase: CombatExpeditionPhase.Combat })
            return false;
        if (e.Player.Actions.Count == 0) return true;
        var definition = e.Catalog.Actions.Get(e.Player.Actions[0].Intent.ActionId);
        return definition.ExecutionKind == ActionExecutionKind.Timed
            && definition.Kind is ActionKind.Exploration or ActionKind.Alchemy;
    }

    /// <summary>
    /// 在保持系统固定顺序的前提下合并无战斗自然窗口。
    /// 返回 false 表示队列已经切换到需要完整 ECS 战斗/旅行系统的行动，调用方应继续普通循环。
    /// </summary>
    public static bool AdvanceBulkTimedUntilUnsupported(EcsActionExecutionComponent e, CancellationToken cancellationToken)
    {
        while (!IsComplete(e))
        {
            if (!CanBulkAdvance(e)) return false;
            if (++e.BulkStepCount > SimulationEngine.MaxTransitionsPerAdvance)
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

    /// <summary>准备 tick 完成后只读下一个真实边界，读取本身不改资源或队列。</summary>
    public static DateTimeOffset NextBoundaryAt(EcsActionExecutionComponent e)
    {
        if (IsComplete(e)) return e.Target;
        if (NeedsPreparation(e)) throw new InvalidOperationException("必须先运行行动准备阶段。");
        if (e.Waiting) return e.WindowEnd;
        if (e.Definition!.ExecutionKind == ActionExecutionKind.CombatEncounter)
        {
            if (e.Battle is not null)
            {
                // 战斗段与 OOP AdvanceTo 同粒度：tick 落在本窗口战斗预算的终点，
                // 单次系统调用内消化全部出手边界；逐边界 tick 会把外层转换预算放大数倍，
                // 长时离线补算的连续战斗会合法耗尽最大转换数。
                return CombatBoundaryAt(e, e.BattleTarget);
            }
            var expedition = e.PreparedAction!.Expedition!;
            var phaseRemaining = expedition.Phase is CombatExpeditionPhase.Duel or CombatExpeditionPhase.DuelPending
                ? e.RemainingSeconds : Math.Max(0, expedition.PhaseDurationSeconds - expedition.PhaseElapsedSeconds);
            return e.WindowStart.AddSeconds(e.WindowElapsedSeconds - e.RemainingSeconds + Math.Min(e.RemainingSeconds, phaseRemaining));
        }
        var needed = e.Definition.ExecutionKind == ActionExecutionKind.Travel
            ? Math.Max(0, e.PreparedAction!.Travel!.TotalDurationSeconds - e.PreparedAction.Travel.ProgressSeconds)
            : Math.Max(0, e.Duration - e.PreparedAction!.ProgressSeconds);
        var step = Math.Min(e.RemainingSeconds, needed);
        return e.WindowStart.AddSeconds(e.WindowElapsedSeconds - e.RemainingSeconds + step);
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
                var capped = SimulationEngine.ApplyOfflineCap(e.Player, e.Target, e.Request);
                e.Versions.MarkRequestChanged(capped);
                e.Changed |= capped;
                e.Cursor = e.Target;
                return;
            }
            e.WindowStart = e.Cursor;
            FlushCombatState(e);
            var plan = SimulationEngine.PrepareResourceBoundary(e.Player, e.Catalog, e.WorkingWallet, e.ComputeTarget, e.Request);
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
                if (!SimulationEngine.AdvanceBlockedAction(e.Player, action, e.Cursor))
                {
                    e.Waiting = true;
                    return;
                }
                CountTransition(e);
                continue;
            }
            CountTransition(e);
            var definition = e.Catalog.Actions.Get(action.Intent.ActionId);
            if (!SimulationEngine.TryStartAction(e.Player, action, definition, e.Catalog, e.WorkingWallet, e.Cursor, e.Core)) continue;
            if (definition.ExecutionKind == ActionExecutionKind.CombatEncounter)
            {
                e.PreparedAction = action;
                e.Definition = definition;
                SimulationEngine.InitializeCombatAction(e.Player, action, definition, e.Catalog, e.Cursor, e.Core);
                if (action.Expedition!.Phase == CombatExpeditionPhase.Combat)
                {
                    var playerCombat = e.Entity.GetComponent<PlayerCombatComponent>()!;
                    e.Battle = playerCombat.EncounterEntityId == 0 ? null
                        : e.Entity.World.GetEntity(playerCombat.EncounterEntityId)?.GetComponent<CombatEncounterComponent>();
                    if (e.Battle is null)
                    {
                        var encounter = e.Catalog.CombatEncounters[action.Expedition.EncounterId];
                        var restored = SimulationEngine.RestoreCombatBattle(e.Player, action, encounter, e.Catalog);
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
                if (!SimulationEngine.PrepareTravelAction(e.Player, action, definition, e.Catalog, e.Cursor, e.Core)) continue;
                e.PreparedAction = action;
                e.Definition = definition;
                return;
            }
            var duration = SimulationEngine.TimedActionDuration(e.Player, definition);
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
            e.Multiplier = SimulationEngine.PrepareTimedAction(e.Player, action, definition, e.Catalog, e.WorkingWallet, e.Cursor, e.Core);
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
        SimulationEngine.AdvanceTimedAction(e.Player, action, definition, e.Multiplier, e.Catalog, step, e.Cursor);
        e.RoundDue = action.ProgressSeconds + 1e-9 >= e.Duration;
    }

    /// <summary>Yield 阶段只在本轮到期时执行一次物品、材料、统计与重复次数结算。</summary>
    public static void CompleteYield(EcsActionExecutionComponent e)
    {
        if (!e.RoundDue || e.PreparedAction is not { } action || e.Definition is not { } definition) return;
        SimulationEngine.CompleteTimedAction(e.Player, action, definition, e.Multiplier, e.Catalog, e.Cursor, e.Core);
        e.RoundDue = false;
        e.PreparedAction = null;
        e.Definition = null;
    }

    /// <summary>移动只在当前资源窗口或到达边界结算，保留跨路段运算顺序。</summary>
    public static void AdvanceTravel(EcsActionExecutionComponent e)
    {
        if (e.PreparedAction is not { } action || e.Definition is not { ExecutionKind: ActionExecutionKind.Travel } definition
            || e.FrameTarget < NextBoundaryAt(e)) return;
        e.RemainingSeconds = SimulationEngine.AdvanceTravelBoundary(e.Player, action, definition, e.Catalog,
            e.WindowStart, e.WindowEnd, e.RemainingSeconds, e.Core);
        e.Cursor = e.WindowStart.AddSeconds(e.WindowElapsedSeconds - e.RemainingSeconds);
        e.PreparedAction = null;
        e.Definition = null;
    }

    /// <summary>搜寻与复活阶段只在自己的边界执行；其它玩家的 tick 不切碎浮点预算。</summary>
    public static void AdvanceCombatPreparation(EcsActionExecutionComponent e)
    {
        if (e.PreparedAction is not { } action || e.Definition is not { ExecutionKind: ActionExecutionKind.CombatEncounter } definition
            || e.Battle is not null || e.FrameTarget < NextBoundaryAt(e)) return;
        var step = SimulationEngine.AdvanceCombatPreparation(e.Player, action, definition, e.Catalog,
            e.WorkingWallet, e.WindowStart, e.WindowEnd, e.RemainingSeconds, e.Core);
        e.RemainingSeconds = step.RemainingSeconds;
        e.Cursor = e.WindowStart.AddSeconds(e.WindowElapsedSeconds - e.RemainingSeconds);
        if (step.Battle is { } battle) AttachCombat(e, battle, e.Cursor);
        e.PreparedAction = null;
        e.Definition = null;
    }

    /// <summary>
    /// 由真实战斗实体系统推进本窗口战斗段：与 OOP 一致一次性 AdvanceTo 到窗口战斗预算，
    /// 内部逐自然边界消化随机原子事件；事件顺序、随机流与逐边界调用完全等价。
    /// </summary>
    public static void AdvanceCombatEncounter(EcsActionExecutionComponent e, CombatEncounterComponent encounter)
    {
        using var stageMeasurement = RuntimeInstrumentation.MeasureCurrent("ecs.stage.combat_encounter");
        encounter.LastEvents.Clear();
        if (!ReferenceEquals(e.Battle, encounter) || e.PreparedAction is null || e.CombatStepDue) return;
        if (e.FrameTarget < NextBoundaryAt(e)) return;
        var state = encounter.State;
        if (state.Outcome == CombatOutcome.Running && state.ElapsedSeconds < e.BattleTarget - 1e-12)
        {
            var result = DeterministicCombatEngine.AdvanceTo(state, e.BattleTarget,
                DaoHua.Ecs.Combat.EcsCombatPhaseDispatcher.Instance, encounter);
            encounter.LastEvents.AddRange(result.Events);
            e.BattleEvents.AddRange(result.Events);
            if (result.PlayerDeathTimestamps is { } deaths) e.BattleDeaths.AddRange(deaths);
            if (result.PlayerRespawnTimestamps is { } respawns) e.BattleRespawns.AddRange(respawns);
        }
        e.CombatStepDue = state.Outcome != CombatOutcome.Running || state.ElapsedSeconds >= e.BattleTarget - 1e-12;
    }

    /// <summary>窗口或终局收尾才扣持续用品、折算技能、记录死亡与奖励，保留 OOP 的操作顺序。</summary>
    public static void CompleteCombatYield(EcsActionExecutionComponent e)
    {
        using var stageMeasurement = RuntimeInstrumentation.MeasureCurrent("ecs.stage.combat_yield");
        if (!e.CombatStepDue || e.Battle is not { } encounter || e.PreparedAction is not { } action || e.Definition is not { } definition) return;
        var state = encounter.State;
        var result = new CombatAdvanceResult(state.EncounterId, e.BattleFrom, e.BattleTarget,
            state.Outcome, e.BattleEvents, state.Sequence, e.BattleDeaths, e.BattleRespawns);
        e.RemainingSeconds = SimulationEngine.CompleteCombatAdvance(e.Player, action, definition,
            e.Catalog.CombatEncounters[state.EncounterId], e.Catalog, state, result,
            e.WindowStart, e.WindowEnd, e.RemainingSeconds, e.Core);
        e.Cursor = e.WindowStart.AddSeconds(e.WindowElapsedSeconds - e.RemainingSeconds);
        var combat = e.Entity.GetComponent<PlayerCombatComponent>()!;
        combat.LastOutcome = state.Outcome;
        combat.LastEventCount = e.BattleEvents.Count;
        if (state.Outcome != CombatOutcome.Running)
        {
            var clock = e.Entity.World.WorldMain.GetComponent<EcsClockComponent>()!;
            clock.ReleaseCombatBattle!(e.Entity);
        }
        e.Battle = null;
        e.CombatStepDue = false;
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
        var changed = SimulationEngine.CompleteResourceBoundary(e.Player, e.Catalog, e.WorkingWallet,
            e.WindowStart, e.WindowStepSeconds, e.Core, e.Request);
        e.Versions.MarkRequestChanged(changed);
        e.Changed |= e.Transitions > 0 || changed;
        e.WindowOpen = false;
        e.Waiting = false;
        e.PreparedAction = null;
        e.Definition = null;
    }

    /// <summary>请求末只对外部钱包应用一次合并流水，再提交请求版本并解除组件工作区。</summary>
    public static SimulationResult Commit(EcsActionExecutionComponent e)
    {
        if (e.Committed || !IsComplete(e) || e.WindowOpen)
            throw new InvalidOperationException("行动请求尚未完整到达提交边界。");
        FlushCombatState(e);
        var deltas = e.Request.ToAssetDeltas(e.Player.PlayerId, sortByItem: false);
        foreach (var delta in deltas) e.ExternalWallet.Apply(delta);
        var inventory = e.Entity.GetComponent<PlayerInventoryComponent>()!.Items;
        inventory.Clear();
        foreach (var balance in e.ExternalWallet.Balances) inventory[balance.Asset] = balance.Quantity;
        e.Versions.Commit(e.Player);
        var result = new SimulationResult(e.Player.PlayerId, e.From, e.Target, (e.Target - e.From).TotalSeconds,
            e.Request.CompletedActions, e.Request.CompletedRepeats, deltas, e.Request.Events,
            e.Player.StateVersion, e.Changed, e.Request.DroppedEventCount);
        e.Committed = true;
        e.Detach();
        return result;
    }

    private static DateTimeOffset CombatBoundaryAt(EcsActionExecutionComponent e, double battleSeconds)
        => e.WindowStart.AddSeconds(e.WindowElapsedSeconds - e.RemainingSeconds + Math.Max(0, battleSeconds - e.BattleFrom));

    private static CombatEncounterComponent AttachCombat(EcsActionExecutionComponent e, CombatEncounterState state, DateTimeOffset startedAt)
    {
        var clock = e.Entity.World.WorldMain.GetComponent<EcsClockComponent>()!;
        return clock.AttachCombatBattle!(e.Entity, state, startedAt);
    }

    private static void FlushCombatState(EcsActionExecutionComponent e)
    {
        var combat = e.Entity.GetComponent<PlayerCombatComponent>()!;
        if (combat.Encounter is not { } battle || e.Player.Actions.Count == 0
            || e.Player.Actions[0].Expedition is not { Phase: CombatExpeditionPhase.Combat } expedition) return;
        e.Player.Actions[0].Expedition = SimulationEngine.CaptureCombatState(battle, expedition) with
        {
            PlayerHitpoints = expedition.PlayerHitpoints,
            PlayerManapoints = expedition.PlayerManapoints
        };
    }

    private static void CountTransition(EcsActionExecutionComponent e)
    {
        if (++e.Transitions > SimulationEngine.MaxTransitionsPerAdvance)
            throw new DomainException(DomainErrorCode.InvariantViolation, "单次模拟超过最大状态转换数。");
    }
}
