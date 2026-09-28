using DaoHua.Ecs.Components;
using Domain.Common;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using YuoCore.Ecs;

namespace DaoHua.Ecs;

/// <summary>请求期间的行动执行组件；只保存边界、资源流水和直接分段视图。</summary>
public sealed class EcsActionExecutionComponent : YuoComponent
{
    public EcsActionPipeline? Pipeline { get; set; }
}

/// <summary>
/// 供实际 ECS 阶段分别调用的行动工作区。它不调度 World，不持有聚合，
/// 不调用整段 SimulationEngine；每个系统只消费当前单轮或资源自然边界。
/// </summary>
public sealed class EcsActionPipeline
{
    private readonly YuoEntity _entity;
    private readonly PlayerActionQueueComponent _queue;
    private readonly GameDataCatalog _catalog;
    private readonly EcsPlayerActionContext _player;
    private readonly AssetWallet _externalWallet;
    private readonly AssetWallet _workingWallet;
    private readonly ActionMutationBatch _request = new();
    private readonly SimulationEngine.StateVersionCommitSession _versions = new();
    private readonly DateTimeOffset _from;
    private readonly DateTimeOffset _computeTarget;
    private ActionMutationBatch _core = new();
    private DateTimeOffset _cursor;
    private DateTimeOffset _windowStart;
    private DateTimeOffset _windowEnd;
    private DateTimeOffset _frameTarget;
    private double _windowStepSeconds;
    private double _remainingSeconds;
    private double _windowElapsedSeconds;
    private int _transitions;
    private bool _windowOpen;
    private bool _waiting;
    private bool _changed;
    private bool _committed;
    private EcsActionState? _preparedAction;
    private ActionDefinition? _definition;
    private double _duration;
    private double _multiplier;
    private bool _roundDue;
    private CombatEncounterComponent? _battle;
    private double _battleFrom;
    private double _battleTarget;
    private bool _combatStepDue;
    private readonly List<CombatEvent> _battleEvents = [];
    private readonly List<double> _battleDeaths = [];
    private readonly List<double> _battleRespawns = [];

    private EcsActionPipeline(YuoEntity entity, GameDataCatalog catalog, DateTimeOffset target)
    {
        _entity = entity;
        _queue = entity.GetComponent<PlayerActionQueueComponent>()!;
        _catalog = catalog;
        _player = new(entity, catalog);
        _from = _cursor = _player.LastSimulatedAt;
        if (target < _from) throw new DomainException(DomainErrorCode.InvalidArgument, "行动目标时间不能倒退。");
        Target = target;
        _computeTarget = target > _from.AddHours(48) ? _from.AddHours(48) : target;
        _externalWallet = new(_player.PlayerId);
        _externalWallet.Restore(entity.GetComponent<PlayerInventoryComponent>()!.Items.Select(
            pair => new AssetBalance(_player.PlayerId, pair.Key, pair.Value, 0)));
        _workingWallet = new(_player.PlayerId);
        _workingWallet.Restore(_externalWallet.Balances);
    }

    public DateTimeOffset Target { get; }
    public DateTimeOffset Current => _cursor;
    /// <summary>不变量违例时的现场证据：窗口/等待/剩余预算/队首行动/战斗边界，供快速失败诊断。</summary>
    internal string DiagnosticState
        => $"window={_windowOpen},waiting={_waiting},remaining={_remainingSeconds:0.######}," +
           $"windowEnd={_windowEnd:o},step={_windowStepSeconds:0.######},action={_preparedAction?.Id.ToString() ?? "-"}," +
           $"queued={_player.Actions.Count},kind={_definition?.ExecutionKind.ToString() ?? "-"}," +
           $"pk={_definition?.Kind.ToString() ?? "-"},battle={_battle is not null}," +
           $"battleOutcome={_battle?.State.Outcome.ToString() ?? "-"},battleElapsed={_battle?.State.ElapsedSeconds:0.###}," +
           $"battleTarget={_battleTarget:0.###},due={_combatStepDue},roundDue={_roundDue}," +
           $"frame={_frameTarget:o},phase={_preparedAction?.Expedition?.Phase.ToString() ?? "-"}," +
           $"actStatus={_preparedAction?.Status.ToString() ?? "-"}";
    public bool IsComplete => _cursor >= Target && !_windowOpen;
    public bool NeedsPreparation => !IsComplete && (!_windowOpen || (!_waiting && _preparedAction is null));
    public bool HasPreparedAction => _preparedAction is not null;
    public ActionKind? PreparedKind => _definition?.Kind;
    public ActionExecutionKind? PreparedExecutionKind => _definition?.ExecutionKind;
    public int BulkStepCount { get; private set; }

    /// <summary>
    /// 优化 ECS 的离线批处理资格。只有不包含战斗实体的计时行动才允许合并自然资源窗口；
    /// 战斗、旅行和跨类型行动仍由完整 ECS System 顺序推进，避免改变随机数和边界语义。
    /// </summary>
    public bool CanBulkAdvance
    {
        get
        {
            if (_battle is not null || _preparedAction?.Expedition is { Phase: CombatExpeditionPhase.Combat })
                return false;
            if (_player.Actions.Count == 0) return true;
            var definition = _catalog.Actions.Get(_player.Actions[0].Intent.ActionId);
            return definition.ExecutionKind == ActionExecutionKind.Timed
                && definition.Kind is ActionKind.Exploration or ActionKind.Alchemy;
        }
    }

    /// <summary>
    /// 在保持系统固定顺序的前提下合并无战斗自然窗口。
    /// 返回 false 表示队列已经切换到需要完整 ECS 战斗/旅行系统的行动，调用方应继续普通循环。
    /// </summary>
    public bool AdvanceBulkTimedUntilUnsupported(CancellationToken cancellationToken)
    {
        while (!IsComplete)
        {
            if (!CanBulkAdvance) return false;
            if (++BulkStepCount > SimulationEngine.MaxTransitionsPerAdvance)
                throw new DomainException(DomainErrorCode.InvariantViolation, "ECS 自然边界推进超过最大转换数。");
            cancellationToken.ThrowIfCancellationRequested();
            if (NeedsPreparation)
            {
                // 对应 ActionClock -> ActionQueue 的准备 tick；准备阶段不推进实际时间。
                PrepareClock(_cursor);
                PrepareQueue();
                if (!CanBulkAdvance) return false;
            }

            if (IsComplete) break;
            var next = NextBoundaryAt;
            if (next < _cursor)
                throw new DomainException(DomainErrorCode.InvariantViolation, "ECS 批量自然边界时钟发生倒退。");

            // 对应 Gathering/Crafting -> Yield -> ClockCommit 的固定执行顺序。
            _frameTarget = next;
            AdvancePreparedAction();
            CompleteYield();
            CompleteClock(preparationOnly: false);
        }

        return true;
    }

    /// <summary>请求开始时只挂工作区；资源与队列变更由准备 tick 的系统执行。</summary>
    public static void BeginRequest(YuoEntity entity, GameDataCatalog catalog, DateTimeOffset target)
    {
        var component = entity.GetOrAddComponent<EcsActionExecutionComponent>();
        if (component.Pipeline is not null) throw new InvalidOperationException("行动请求尚未结束。");
        component.Pipeline = new(entity, catalog, target);
    }

    public static EcsActionPipeline? Find(YuoEntity entity)
        => entity.GetComponent<EcsActionExecutionComponent>()?.Pipeline;

    /// <summary>准备 tick 完成后只读下一个真实边界，读取本身不改资源或队列。</summary>
    public DateTimeOffset NextBoundaryAt
    {
        get
        {
            if (IsComplete) return Target;
            if (NeedsPreparation) throw new InvalidOperationException("必须先运行行动准备阶段。");
            if (_waiting) return _windowEnd;
            if (_definition!.ExecutionKind == ActionExecutionKind.CombatEncounter)
            {
                if (_battle is not null)
                {
                    // 战斗段与 OOP AdvanceTo 同粒度：tick 落在本窗口战斗预算的终点，
                    // 单次系统调用内消化全部出手边界；逐边界 tick 会把外层转换预算放大数倍，
                    // 长时离线补算的连续战斗会合法耗尽最大转换数。
                    return CombatBoundaryAt(_battleTarget);
                }
                var expedition = _preparedAction!.Expedition!;
                var phaseRemaining = expedition.Phase is CombatExpeditionPhase.Duel or CombatExpeditionPhase.DuelPending
                    ? _remainingSeconds : Math.Max(0, expedition.PhaseDurationSeconds - expedition.PhaseElapsedSeconds);
                return _windowStart.AddSeconds(_windowElapsedSeconds - _remainingSeconds + Math.Min(_remainingSeconds, phaseRemaining));
            }
            var needed = _definition.ExecutionKind == ActionExecutionKind.Travel
                ? Math.Max(0, _preparedAction!.Travel!.TotalDurationSeconds - _preparedAction.Travel.ProgressSeconds)
                : Math.Max(0, _duration - _preparedAction!.ProgressSeconds);
            var step = Math.Min(_remainingSeconds, needed);
            return _windowStart.AddSeconds(_windowElapsedSeconds - _remainingSeconds + step);
        }
    }

    /// <summary>Clock 阶段只开启资源自然窗口并记住本 tick 允许到达的时刻。</summary>
    public void PrepareClock(DateTimeOffset frameTarget)
    {
        using var stageMeasurement = RuntimeInstrumentation.MeasureCurrent("ecs.stage.clock");
        if (IsComplete) return;
        if (!_windowOpen)
        {
            if (_cursor >= _computeTarget)
            {
                var capped = SimulationEngine.ApplyOfflineCap(_player, Target, _request);
                _versions.MarkRequestChanged(capped);
                _changed |= capped;
                _cursor = Target;
                return;
            }
            _windowStart = _cursor;
            FlushCombatState();
            var plan = SimulationEngine.PrepareResourceBoundary(_player, _catalog, _workingWallet, _computeTarget, _request);
            _windowStepSeconds = plan.StepSeconds;
            _windowEnd = _windowStart.AddSeconds(plan.StepSeconds);
            _windowElapsedSeconds = (_windowEnd - _windowStart).TotalSeconds;
            _remainingSeconds = _windowElapsedSeconds;
            _core = new();
            _transitions = 0;
            _waiting = false;
            _windowOpen = true;
            _versions.MarkRequestChanged(plan.Changed);
            _changed |= plan.Changed;
        }
        _frameTarget = frameTarget < _windowEnd ? frameTarget : _windowEnd;
    }

    /// <summary>Queue 阶段完成一次准入；重复准备 tick 复用已准备的同一轮，不重复扣用品。</summary>
    public void PrepareQueue()
    {
        using var stageMeasurement = RuntimeInstrumentation.MeasureCurrent("ecs.stage.queue");
        if (IsComplete || !_windowOpen || _preparedAction is not null || _waiting) return;
        while (_remainingSeconds > 1e-9 && _player.Actions.Count > 0)
        {
            var action = _queue.Actions[0];
            if (action.Status == ActionQueueStatus.Blocked)
            {
                if (!SimulationEngine.AdvanceBlockedAction(_player, action, _cursor))
                {
                    _waiting = true;
                    return;
                }
                CountTransition();
                continue;
            }
            CountTransition();
            var definition = _catalog.Actions.Get(action.ActionId);
            if (!SimulationEngine.TryStartAction(_player, action, definition, _catalog, _workingWallet, _cursor, _core)) continue;
            if (definition.ExecutionKind == ActionExecutionKind.CombatEncounter)
            {
                _preparedAction = action;
                _definition = definition;
                SimulationEngine.InitializeCombatAction(_player, action, definition, _catalog, _cursor, _core);
                if (action.Expedition!.Phase == CombatExpeditionPhase.Combat)
                {
                    var playerCombat = _entity.GetComponent<PlayerCombatComponent>()!;
                    _battle = playerCombat.EncounterEntityId == 0 ? null
                        : _entity.World.GetEntity(playerCombat.EncounterEntityId)?.GetComponent<CombatEncounterComponent>();
                    if (_battle is null)
                    {
                        var encounter = _catalog.CombatEncounters[action.Expedition.EncounterId];
                        var restored = SimulationEngine.RestoreCombatBattle(_player, action, encounter, _catalog);
                        _battle = AttachCombat(restored, _cursor.AddSeconds(-restored.ElapsedSeconds));
                    }
                    var revision = PlayerAttributeResolver.InputRevision(_player.Progression, _player.World, _player.Buffs, _player.Loadout);
                    if (_catalog.BaseAttributes is null || revision != _battle.AttributeRevision)
                    {
                        _battle.State.Player.RefreshStats(_player.ResolveAttributes(_catalog).ToCombatant());
                        _battle.AttributeRevision = revision;
                    }
                    _battleFrom = _battle.State.ElapsedSeconds;
                    _battleTarget = _battleFrom + _remainingSeconds;
                    _battleEvents.Clear();
                    _battleDeaths.Clear();
                    _battleRespawns.Clear();
                    _combatStepDue = false;
                }
                return;
            }
            if (definition.ExecutionKind == ActionExecutionKind.Travel)
            {
                if (!SimulationEngine.PrepareTravelAction(_player, action, definition, _catalog, _cursor, _core)) continue;
                _preparedAction = action;
                _definition = definition;
                return;
            }
            var duration = SimulationEngine.TimedActionDuration(_player, definition);
            if (duration <= 0)
            {
                _player.RemoveAction(action.Id, incrementVersion: false, endedAtUnixSeconds: _cursor.ToUnixTimeMilliseconds() / 1000d);
                _core.AddEvent(new SimulationEvent(_cursor, "action.invalid-dropped",
                    $"行动 {definition.Name} 的时长配置无效（{duration:0.######} 秒），已自动移出队列。", definition.Id));
                continue;
            }
            _preparedAction = action;
            _definition = definition;
            _duration = duration;
            _multiplier = SimulationEngine.PrepareTimedAction(_player, action, definition, _catalog, _workingWallet, _cursor, _core);
            return;
        }
        _waiting = true;
    }

    /// <summary>采集或制造阶段推进单个已裁剪片段；准备 tick 的零时长不会产生连续收益。</summary>
    public void AdvancePreparedAction()
    {
        if (_preparedAction is not { } action || _definition is not { ExecutionKind: ActionExecutionKind.Timed } definition || _roundDue) return;
        if (_frameTarget < NextBoundaryAt) return;
        var needed = Math.Max(0, _duration - action.ProgressSeconds);
        // _frameTarget >= NextBoundaryAt 已保证 min(remaining, needed) 落入本帧；
        // 不能再按 tick 量化差值推导可用量：亚 tick（~1e-7s）残差会让边界落回同一时刻形成不动点。
        var step = Math.Min(_remainingSeconds, needed);
        _remainingSeconds -= step;
        _cursor = _windowStart.AddSeconds(_windowElapsedSeconds - _remainingSeconds);
        SimulationEngine.AdvanceTimedAction(_player, action, definition, _multiplier, _catalog, step, _cursor);
        _roundDue = action.ProgressSeconds + 1e-9 >= _duration;
    }

    /// <summary>Yield 阶段只在本轮到期时执行一次物品、材料、统计与重复次数结算。</summary>
    public void CompleteYield()
    {
        if (!_roundDue || _preparedAction is not { } action || _definition is not { } definition) return;
        SimulationEngine.CompleteTimedAction(_player, action, definition, _multiplier, _catalog, _cursor, _core);
        _roundDue = false;
        _preparedAction = null;
        _definition = null;
    }

    /// <summary>移动只在当前资源窗口或到达边界结算，保留跨路段运算顺序。</summary>
    public void AdvanceTravel()
    {
        if (_preparedAction is not { } action || _definition is not { ExecutionKind: ActionExecutionKind.Travel } definition
            || _frameTarget < NextBoundaryAt) return;
        _remainingSeconds = SimulationEngine.AdvanceTravelBoundary(_player, action, definition, _catalog,
            _windowStart, _windowEnd, _remainingSeconds, _core);
        _cursor = _windowStart.AddSeconds(_windowElapsedSeconds - _remainingSeconds);
        _preparedAction = null;
        _definition = null;
    }

    /// <summary>搜寻与复活阶段只在自己的边界执行；其它玩家的 tick 不切碎浮点预算。</summary>
    public void AdvanceCombatPreparation()
    {
        if (_preparedAction is not { } action || _definition is not { ExecutionKind: ActionExecutionKind.CombatEncounter } definition
            || _battle is not null || _frameTarget < NextBoundaryAt) return;
        var step = SimulationEngine.AdvanceCombatPreparation(_player, action, definition, _catalog,
            _workingWallet, _windowStart, _windowEnd, _remainingSeconds, _core);
        _remainingSeconds = step.RemainingSeconds;
        _cursor = _windowStart.AddSeconds(_windowElapsedSeconds - _remainingSeconds);
        if (step.Battle is { } battle) AttachCombat(battle, _cursor);
        _preparedAction = null;
        _definition = null;
    }

    /// <summary>
    /// 由真实战斗实体系统推进本窗口战斗段：与 OOP 一致一次性 AdvanceTo 到窗口战斗预算，
    /// 内部逐自然边界消化随机原子事件；事件顺序、随机流与逐边界调用完全等价。
    /// </summary>
    public void AdvanceCombatEncounter(CombatEncounterComponent encounter)
    {
        using var stageMeasurement = RuntimeInstrumentation.MeasureCurrent("ecs.stage.combat_encounter");
        encounter.LastEvents.Clear();
        if (!ReferenceEquals(_battle, encounter) || _preparedAction is null || _combatStepDue) return;
        if (_frameTarget < NextBoundaryAt) return;
        var state = encounter.State;
        if (state.Outcome == CombatOutcome.Running && state.ElapsedSeconds < _battleTarget - 1e-12)
        {
            var result = DeterministicCombatEngine.AdvanceTo(state, _battleTarget);
            encounter.LastEvents.AddRange(result.Events);
            _battleEvents.AddRange(result.Events);
            if (result.PlayerDeathTimestamps is { } deaths) _battleDeaths.AddRange(deaths);
            if (result.PlayerRespawnTimestamps is { } respawns) _battleRespawns.AddRange(respawns);
        }
        _combatStepDue = state.Outcome != CombatOutcome.Running || state.ElapsedSeconds >= _battleTarget - 1e-12;
    }

    /// <summary>窗口或终局收尾才扣持续用品、折算技能、记录死亡与奖励，保留 OOP 的操作顺序。</summary>
    public void CompleteCombatYield()
    {
        using var stageMeasurement = RuntimeInstrumentation.MeasureCurrent("ecs.stage.combat_yield");
        if (!_combatStepDue || _battle is not { } encounter || _preparedAction is not { } action || _definition is not { } definition) return;
        var state = encounter.State;
        var result = new CombatAdvanceResult(state.EncounterId, _battleFrom, _battleTarget,
            state.Outcome, _battleEvents, state.Sequence, _battleDeaths, _battleRespawns);
        _remainingSeconds = SimulationEngine.CompleteCombatAdvance(_player, action, definition,
            _catalog.CombatEncounters[state.EncounterId], _catalog, state, result,
            _windowStart, _windowEnd, _remainingSeconds, _core);
        _cursor = _windowStart.AddSeconds(_windowElapsedSeconds - _remainingSeconds);
        var combat = _entity.GetComponent<PlayerCombatComponent>()!;
        combat.LastOutcome = state.Outcome;
        combat.LastEventCount = _battleEvents.Count;
        if (state.Outcome != CombatOutcome.Running)
        {
            var clock = _entity.World.WorldMain.GetComponent<EcsClockComponent>()!;
            clock.ReleaseCombatBattle!(_entity);
        }
        _battle = null;
        _combatStepDue = false;
        _preparedAction = null;
        _definition = null;
    }

    private DateTimeOffset CombatBoundaryAt(double battleSeconds)
        => _windowStart.AddSeconds(_windowElapsedSeconds - _remainingSeconds + Math.Max(0, battleSeconds - _battleFrom));

    private CombatEncounterComponent AttachCombat(CombatEncounterState state, DateTimeOffset startedAt)
    {
        var clock = _entity.World.WorldMain.GetComponent<EcsClockComponent>()!;
        return clock.AttachCombatBattle!(_entity, state, startedAt);
    }

    private void FlushCombatState()
    {
        var combat = _entity.GetComponent<PlayerCombatComponent>()!;
        if (combat.Encounter is not { } battle || _player.Actions.Count == 0
            || _player.Actions[0].Expedition is not { Phase: CombatExpeditionPhase.Combat } expedition) return;
        _player.Actions[0].Expedition = SimulationEngine.CaptureCombatState(battle, expedition) with
        {
            PlayerHitpoints = expedition.PlayerHitpoints,
            PlayerManapoints = expedition.PlayerManapoints
        };
    }

    /// <summary>Completion 阶段只在真实资源窗口末提交 Core 时钟、版本、钱包与回复。</summary>
    public void CompleteClock(bool preparationOnly)
    {
        if (IsComplete || !_windowOpen || preparationOnly) return;
        // DateTimeOffset 以 tick（1e-7s）量化：窗口末残留的亚 tick 预算无法再用下一帧消费，
        // 到达窗口末时直接归零，否则 boundary==cursor 会构成死循环不动点。
        if (_frameTarget >= _windowEnd && (_waiting || _remainingSeconds <= 1e-6))
        {
            _remainingSeconds = 0;
            _cursor = _windowEnd;
        }
        if (_remainingSeconds > 1e-9) return;
        _cursor = _windowEnd;
        _player.Progression.AgeSeconds += _windowElapsedSeconds;
        _player.LastSimulatedAt = _windowEnd;
        if (_player.Actions.Count > 0 && _player.Actions[0].Status == ActionQueueStatus.Pending)
        {
            _player.Actions[0].Status = ActionQueueStatus.Running;
            if (_transitions == 0) _player.MarkDirty(PlayerSectionKind.ActionQueue);
        }
        if (_transitions > 0)
        {
            _player.MarkDirty(PlayerSectionKind.Progression);
            _player.MarkDirty(PlayerSectionKind.ActionQueue);
        }
        _versions.RecordCoreStep(_transitions > 0, endsNaturalBoundary: true);
        var changed = SimulationEngine.CompleteResourceBoundary(_player, _catalog, _workingWallet,
            _windowStart, _windowStepSeconds, _core, _request);
        _versions.MarkRequestChanged(changed);
        _changed |= _transitions > 0 || changed;
        _windowOpen = false;
        _waiting = false;
        _preparedAction = null;
        _definition = null;
    }

    /// <summary>请求末只对外部钱包应用一次合并流水，再提交请求版本并解除组件工作区。</summary>
    public SimulationResult Commit()
    {
        if (_committed || !IsComplete || _windowOpen)
            throw new InvalidOperationException("行动请求尚未完整到达提交边界。");
        FlushCombatState();
        var deltas = _request.ToAssetDeltas(_player.PlayerId, sortByItem: false);
        foreach (var delta in deltas) _externalWallet.Apply(delta);
        var inventory = _entity.GetComponent<PlayerInventoryComponent>()!.Items;
        inventory.Clear();
        foreach (var balance in _externalWallet.Balances) inventory[balance.Asset] = balance.Quantity;
        _versions.Commit(_player);
        _committed = true;
        Detach();
        return new(_player.PlayerId, _from, Target, (Target - _from).TotalSeconds,
            _request.CompletedActions, _request.CompletedRepeats, deltas, _request.Events,
            _player.StateVersion, _changed, _request.DroppedEventCount);
    }

    /// <summary>失败时仅解除请求工作区；实体内容由运行时已有请求检查点恢复。</summary>
    public void Detach() => _entity.GetComponent<EcsActionExecutionComponent>()!.Pipeline = null;

    private void CountTransition()
    {
        if (++_transitions > SimulationEngine.MaxTransitionsPerAdvance)
            throw new DomainException(DomainErrorCode.InvariantViolation, "单次模拟超过最大状态转换数。");
    }
}
