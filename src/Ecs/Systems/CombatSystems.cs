using DaoHua.Ecs.Components;
using Domain.Combat;
using Domain.Common;
using YuoCore.Ecs;

namespace DaoHua.Ecs.Systems;

internal static class CombatSystemGate
{
    public static bool IsActive(CombatEncounterComponent encounter)
    {
        var clock = EcsSystemContext.Clock(encounter.Entity);
        if (clock.PreparationOnly || clock.SkipWorldUpdate) return false;
        var identity = encounter.Entity.World.GetEntity(encounter.PlayerEntityId)?.GetComponent<PlayerIdentityComponent>();
        var active = clock.ActivePlayerId;
        return identity is null || active is null || identity.PlayerId == active;
    }
}

/// <summary>
/// 每次 tick 推进本帧允许的全部战斗自然边界（整段 AdvanceTo，与 OOP 窗口粒度一致）。
/// Encounter 持有时钟和随机序列，玩家与怪物 CombatantComponent.State 与其中的单位引用相同，
/// 领域原语就地修改真实 ECS 单位。同刻技能、Buff、伤害和死亡是不可拆分的随机原子事件，
/// 统一由此系统按边界顺序结算；逐边界拆 tick 不改变结果，只会放大外层转换预算。
/// </summary>
[SystemOrder(600)]
public sealed class CombatEventAdvanceSystem : YuoSystem<CombatEncounterComponent>, IUpdate
{
    public override void Run(CombatEncounterComponent encounter)
    {
        if (!CombatSystemGate.IsActive(encounter)) return;
        using var measurement = RuntimeInstrumentation.MeasureCurrent("ecs.system.run.CombatEventAdvanceSystem");
        if (encounter.State.Outcome != CombatOutcome.Running) return;
        var world = encounter.Entity.World;
        var player = world.GetEntity(encounter.PlayerEntityId);
        if (player is null) return;
        EcsActionPipeline.Find(player)?.AdvanceCombatEncounter(encounter);

    }
}

/// <summary>根据同一战斗单位引用维护怪物 Entity 的目标索引。</summary>
[SystemOrder(610)]
public sealed class CombatTargetSystem : YuoSystem<CombatEncounterComponent>, IUpdate
{
    public override void Run(CombatEncounterComponent encounter)
    {
        if (!CombatSystemGate.IsActive(encounter)) return;
        using var measurement = RuntimeInstrumentation.MeasureCurrent("ecs.system.run.CombatTargetSystem");
        var target = encounter.State.PrimaryEnemy;
        encounter.PrimaryTargetEntityId = 0;
        if (target is null) return;
        for (var i = 0; i < encounter.State.Enemies.Count && i < encounter.EnemyEntityIds.Count; i++)
            if (ReferenceEquals(target, encounter.State.Enemies[i]))
            {
                encounter.PrimaryTargetEntityId = encounter.EnemyEntityIds[i];
                return;
            }
    }
}

/// <summary>按领域事件产生的原序追加战斗日志，不重算战斗随机数。</summary>
[SystemOrder(650)]
public sealed class CombatEventSystem : YuoSystem<CombatEncounterComponent>, IUpdate
{
    public override void Run(CombatEncounterComponent encounter)
    {
        if (!CombatSystemGate.IsActive(encounter)) return;
        using var measurement = RuntimeInstrumentation.MeasureCurrent("ecs.system.run.CombatEventSystem");
        if (encounter.LastEvents.Count == 0) return;
        encounter.Entity.GetOrAddComponent<CombatLogComponent>().Events.AddRange(encounter.LastEvents);
    }
}
