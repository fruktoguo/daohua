using DaoHua.Ecs.Components;
using Domain.Combat;
using YuoCore.Ecs;

namespace DaoHua.Ecs.Combat;

/// <summary>
/// 边界收尾观察派发器：把全部相位转发给内层派发器（缺省内联实现），并在每个自然边界
/// 收尾相位（<see cref="ICombatOutcomePhase"/>）结算完成后追加一次宿主回调——事件驱动
/// 消耗品判定等「边界落地后同刻复查」逻辑的统一挂载点；战斗推进次序与随机流不变。
/// 传入 tracked 遭遇时内嵌 <see cref="CombatFactTracker"/>，回调携带边界事实增量并
/// 把增量折叠进各单位的 PendingWake 供技能槽判定缓存消费（TASK-20261112-15）。
/// </summary>
public sealed class CombatBoundaryHookDispatcher(Action<CombatPhaseContext, CombatFactDelta> afterBoundary,
    CombatEncounterState? tracked = null, ICombatPhaseDispatcher? inner = null) : ICombatPhaseDispatcher
{
    private readonly ICombatPhaseDispatcher _inner = inner ?? InlineCombatPhaseDispatcher.Instance;
    private readonly CombatFactTracker? _tracker = tracked is null ? null : new CombatFactTracker(tracked);

    /// <summary>宿主注入外部事实（共享钱包脏标记/战外面板刷新等追踪器不可 diff 的输入），并入下一边界增量。</summary>
    public void MarkExternal() => _tracker?.MarkExternal();

    /// <summary>转发相位派发；边界收尾相位完成后先产出事实增量、折叠待唤醒掩码，再执行宿主回调。</summary>
    public void Dispatch<TTag>(CombatEncounterComponent? host, CombatPhaseContext context) where TTag : ISystemTag
    {
        _inner.Dispatch<TTag>(host, context);
        if (typeof(TTag) != typeof(ICombatOutcomePhase)) return;
        var delta = _tracker?.Advance(context.Encounter) ?? CombatFactDelta.Full;
        FoldPendingWake(context.Encounter, in delta);
        afterBoundary(context, delta);
    }

    /// <summary>把边界增量按遭遇绝对侧折叠进每槽待判定位（技能判定缓存的唤醒输入；仅数组已建槽位累积）。</summary>
    private static void FoldPendingWake(CombatEncounterState encounter, in CombatFactDelta delta)
    {
        FoldSide(encounter.PlayerAllies, delta.Allies, delta.AllyUnion, delta.EnemyUnion, delta.Facts);
        FoldSide(encounter.Enemies, delta.Enemies, delta.AllyUnion, delta.EnemyUnion, delta.Facts);
    }

    private static void FoldSide(IReadOnlyList<CombatantState> units, UnitChannel[]? selfDelta,
        UnitChannel allyUnion, UnitChannel enemyUnion, BattleFact facts)
    {
        if (selfDelta is null) return;
        for (var i = 0; i < selfDelta.Length && i < units.Count; i++)
        {
            if (units[i].SkillWakePending is not { } pending) continue;
            var folded = new SlotWakeMask { Self = selfDelta[i], AllySide = allyUnion,
                EnemySide = enemyUnion, Facts = facts };
            for (var s = 0; s < pending.Length; s++)
                pending[s] = pending[s].Union(folded);
        }
    }
}
