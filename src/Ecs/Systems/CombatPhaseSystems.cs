using DaoHua.Ecs.Combat;
using DaoHua.Ecs.Components;
using YuoCore.Ecs;

namespace DaoHua.Ecs.Systems;

/// <summary>
/// 战斗相位 System 集合：每类一个 tag 订阅，Run 体把 component.PhaseContext
/// 转交给 DeterministicCombatEngine 的同名相位实现。
/// 实体派发与 Inline 派发共享同一相位方法——两条路径的确定性由单源实现保证。
/// </summary>
internal static class CombatPhaseSystems
{
    /// <summary>无世界路径的相位派发：按 tag 类型同步调用同一相位实现。</summary>
    public static void DispatchInline<TTag>(CombatPhaseContext context) where TTag : ISystemTag
    {
        if (typeof(TTag) == typeof(ICombatBuffTickPhase)) { DeterministicCombatEngine.ApplyBuffTickPhase(context); return; }
        if (typeof(TTag) == typeof(ICombatPlayerRespawnPhase)) { DeterministicCombatEngine.ApplyPlayerRespawnPhase(context); return; }
        if (typeof(TTag) == typeof(ICombatPlayerActPhase)) { DeterministicCombatEngine.ApplyPlayerActPhase(context); return; }
        if (typeof(TTag) == typeof(ICombatEnemyActPhase)) { DeterministicCombatEngine.ApplyEnemyActPhase(context); return; }
        if (typeof(TTag) == typeof(ICombatPlayerDownedPhase)) { DeterministicCombatEngine.ApplyPlayerDownedPhase(context); return; }
        if (typeof(TTag) == typeof(ICombatOutcomePhase)) { DeterministicCombatEngine.ApplyOutcomePhase(context); return; }
        throw new InvalidOperationException($"未登记的战斗相位 tag：{typeof(TTag).Name}。");
    }
}

[SystemOrder(100)]
public sealed class CombatBuffTickSystem : YuoSystem<CombatEncounterComponent>, ICombatBuffTickPhase
{
    public override void Run(CombatEncounterComponent encounter)
        => DeterministicCombatEngine.ApplyBuffTickPhase(encounter.PhaseContext!);
}

[SystemOrder(110)]
public sealed class CombatPlayerRespawnSystem : YuoSystem<CombatEncounterComponent>, ICombatPlayerRespawnPhase
{
    public override void Run(CombatEncounterComponent encounter)
        => DeterministicCombatEngine.ApplyPlayerRespawnPhase(encounter.PhaseContext!);
}

[SystemOrder(120)]
public sealed class CombatPlayerActSystem : YuoSystem<CombatEncounterComponent>, ICombatPlayerActPhase
{
    public override void Run(CombatEncounterComponent encounter)
        => DeterministicCombatEngine.ApplyPlayerActPhase(encounter.PhaseContext!);
}

[SystemOrder(130)]
public sealed class CombatEnemyActSystem : YuoSystem<CombatEncounterComponent>, ICombatEnemyActPhase
{
    public override void Run(CombatEncounterComponent encounter)
        => DeterministicCombatEngine.ApplyEnemyActPhase(encounter.PhaseContext!);
}

[SystemOrder(140)]
public sealed class CombatPlayerDownedSystem : YuoSystem<CombatEncounterComponent>, ICombatPlayerDownedPhase
{
    public override void Run(CombatEncounterComponent encounter)
        => DeterministicCombatEngine.ApplyPlayerDownedPhase(encounter.PhaseContext!);
}

[SystemOrder(150)]
public sealed class CombatOutcomeSystem : YuoSystem<CombatEncounterComponent>, ICombatOutcomePhase
{
    public override void Run(CombatEncounterComponent encounter)
        => DeterministicCombatEngine.ApplyOutcomePhase(encounter.PhaseContext!);
}
