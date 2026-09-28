using Domain.Combat;
using Domain.Common;
using YuoCore.Ecs;

namespace DaoHua.Ecs;

/// <summary>
/// 命中触发 tag：伤害落账后由战斗边界在固定序列点派发。
/// 攻击方实体上的汲取系与受击方实体上的反伤系各自消费；不进 Update 序列。
/// </summary>
public interface IOnHitTrigger : ISystemTag;

/// <summary>触发器派发期间的负载信箱：实体挂接时常驻，派发前写入、派发后清除。</summary>
public sealed class CombatTriggerInboxComponent : YuoComponent
{
    public PendingCombatTrigger? Trigger { get; set; }
}

/// <summary>命中后按实伤比例回血（气血汲取）标记组件；点数实时读 Stats.Effects。</summary>
public sealed class OnHitLeechComponent : YuoComponent;

/// <summary>命中后按实伤从目标灵力池偷取灵力标记组件。</summary>
public sealed class ManaLeechComponent : YuoComponent;

/// <summary>受击落账后把实收伤害按比例弹回攻击方标记组件。</summary>
public sealed class ThornsComponent : YuoComponent;

/// <summary>
/// 内联触发器派发器：无实体宿主（纯领域推进、斗法剧本）使用的等价路径。
/// 与实体派发共用 CombatTriggerMath，参数直读 Stats.Effects——
/// 与 attach 时组件快照同源，结算结果逐位一致。
/// </summary>
public sealed class InlineCombatTriggerSink : ICombatTriggerSink
{
    public static InlineCombatTriggerSink Instance { get; } = new();

    public void Drain(CombatEncounterState encounter)
    {
        var pending = encounter.PendingTriggers;
        if (pending.Count == 0) return;
        for (var i = 0; i < pending.Count; i++) Dispatch(pending[i]);
        pending.Clear();
    }

    private static void Dispatch(PendingCombatTrigger trigger)
    {
        var context = trigger.Context;
        switch (trigger.Kind)
        {
            case CombatTriggerKind.OnHit:
                // 与实体路径同序：宿主为攻击方跑汲取系（Leech 100 → Mana 110），
                // 宿主为受击方跑反伤系（Thorns 200）；SystemOrder 序在此手工对齐。
                if (ReferenceEquals(trigger.Unit, context.Attacker))
                {
                    CombatTriggerMath.HpDrain(context, context.Attacker.Stats.Effects.HpDrainPoints);
                    CombatTriggerMath.ManaDrain(context, context.Attacker.Stats.Effects.MpDrainPoints);
                }
                else if (ReferenceEquals(trigger.Unit, context.Target))
                {
                    CombatTriggerMath.DamageReflect(context, context.Target.Stats.Effects.DamageReflectPoints);
                }
                break;
        }
        Settle(trigger);
    }

    /// <summary>触发器结算收尾：回填主事件双方灵力真值，衍生事件按序并入主事件之后。</summary>
    internal static void Settle(PendingCombatTrigger trigger)
    {
        var context = trigger.Context;
        trigger.Events[trigger.MainEventIndex] = trigger.Events[trigger.MainEventIndex] with
        {
            AttackerManapointsAfter = context.Attacker.Manapoints,
            TargetManapointsAfter = context.Target.Manapoints
        };
        if (context.DerivedEvents is { } derived)
        {
            trigger.Events.AddRange(derived);
            context.DerivedEvents = null;
        }
    }
}

/// <summary>
/// 战斗单位触发器面装配：实体标识回填、信箱常驻与 reactive buff 标记组件挂摘。
/// 标记组件是「该效果开启」的能力标记——参数永远实时读 <see cref="CombatantStats.Effects"/>，
/// 快照/RefreshStats 后只需重评估挂摘，无参数双写漂移。
/// </summary>
public static class CombatTriggerSurface
{
    /// <summary>首次挂接：回填实体标识并常驻信箱组件，随后按 Effects 同步标记。</summary>
    public static void Attach(YuoEntity entity, CombatantState unit)
    {
        unit.EntityId = entity.ID;
        entity.GetOrAddComponent<CombatTriggerInboxComponent>();
        Sync(entity, unit);
    }

    /// <summary>按单位当前面板 Effects 重评估 reactive buff 标记组件的挂摘。</summary>
    public static void Sync(YuoEntity entity, CombatantState unit)
    {
        SyncMarker<OnHitLeechComponent>(entity, unit.Stats.Effects.HpDrainPoints > 0);
        SyncMarker<ManaLeechComponent>(entity, unit.Stats.Effects.MpDrainPoints > 0);
        SyncMarker<ThornsComponent>(entity, unit.Stats.Effects.DamageReflectPoints > 0);
    }

    private static void SyncMarker<T>(YuoEntity entity, bool enabled) where T : YuoComponent, new()
    {
        if (enabled) entity.GetOrAddComponent<T>();
        else if (entity.HasComponent<T>()) entity.RemoveComponent<T>();
    }
}

/// <summary>
/// 实体触发器派发器：PendingTriggers 逐条按宿住实体 RunSystem&lt;tag&gt; 同步派发，
/// 消费点固定在命中结算之后、下一边界取数前——UpdateOutcome 永远在触发器之后运行。
/// </summary>
public sealed class EcsCombatTriggerSink : ICombatTriggerSink
{
    private readonly YuoWorld _world;

    public EcsCombatTriggerSink(YuoWorld world) => _world = world;

    public void Drain(CombatEncounterState encounter)
    {
        var pending = encounter.PendingTriggers;
        if (pending.Count == 0) return;
        for (var i = 0; i < pending.Count; i++) Dispatch(pending[i]);
        pending.Clear();
    }

    private void Dispatch(PendingCombatTrigger trigger)
    {
        var entity = _world.GetEntity(trigger.UnitEntityId)
            ?? throw new DomainException(DomainErrorCode.InvariantViolation, "战斗单位实体缺失。");
        var inbox = entity.GetComponent<CombatTriggerInboxComponent>()
            ?? throw new DomainException(DomainErrorCode.InvariantViolation, "战斗单位缺少触发器信箱组件。");
        inbox.Trigger = trigger;
        YuoWorld.RunSystem(TagTypeOf(trigger.Kind), entity);
        inbox.Trigger = null;
        InlineCombatTriggerSink.Settle(trigger);
    }

    private static Type TagTypeOf(CombatTriggerKind kind) => kind switch
    {
        CombatTriggerKind.OnHit => typeof(IOnHitTrigger),
        _ => throw new DomainException(DomainErrorCode.InvariantViolation, "未知战斗触发器种类。"),
    };
}
