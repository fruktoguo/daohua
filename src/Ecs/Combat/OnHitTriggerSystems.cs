using DaoHua.Ecs.Components;
using Domain.Combat;
using YuoCore.Ecs;

namespace DaoHua.Ecs;

/// <summary>气血汲取：命中后攻击方按实伤比例回血（原 HpDrainHook 等价位）。</summary>
[SystemOrder(100)]
public sealed class OnHitLeechSystem : YuoSystem<CombatantComponent, OnHitLeechComponent>, IOnHitTrigger
{
    /// <summary>Tag 派发入口：信箱负载宿主为攻击方时按实伤比例回血。</summary>
    public override void Run(CombatantComponent unit, OnHitLeechComponent leech)
    {
        var trigger = unit.Entity.GetComponent<CombatTriggerInboxComponent>()?.Trigger;
        // 汲取系只响应宿主为攻击方的命中条目；非本单元消费交由其他 system 判定。
        if (trigger is null || !ReferenceEquals(trigger.Context.Attacker, unit.State)) return;
        CombatTriggerMath.HpDrain(trigger.Context, unit.State!.Stats.Effects.HpDrainPoints);
    }
}

/// <summary>灵力汲取：命中后攻击方按实伤从目标灵力池偷取（原 ManaDrainHook 等价位）。</summary>
[SystemOrder(110)]
public sealed class ManaLeechSystem : YuoSystem<CombatantComponent, ManaLeechComponent>, IOnHitTrigger
{
    /// <summary>Tag 派发入口：信箱负载宿主为攻击方时按实伤从目标灵力池偷取。</summary>
    public override void Run(CombatantComponent unit, ManaLeechComponent leech)
    {
        var trigger = unit.Entity.GetComponent<CombatTriggerInboxComponent>()?.Trigger;
        if (trigger is null || !ReferenceEquals(trigger.Context.Attacker, unit.State)) return;
        CombatTriggerMath.ManaDrain(trigger.Context, unit.State!.Stats.Effects.MpDrainPoints);
    }
}

/// <summary>伤害反弹：受击方把实收伤害按比例弹回攻击方（原 DamageReflectHook 等价位）。</summary>
[SystemOrder(200)]
public sealed class ThornsSystem : YuoSystem<CombatantComponent, ThornsComponent>, IOnHitTrigger
{
    /// <summary>Tag 派发入口：信箱负载宿主为受击方时把实收伤害按比例弹回攻击方。</summary>
    public override void Run(CombatantComponent unit, ThornsComponent thorns)
    {
        var trigger = unit.Entity.GetComponent<CombatTriggerInboxComponent>()?.Trigger;
        if (trigger is null || !ReferenceEquals(trigger.Context.Target, unit.State)) return;
        CombatTriggerMath.DamageReflect(trigger.Context, unit.State!.Stats.Effects.DamageReflectPoints);
    }
}

/// <summary>受击涨威胁：受击方按攻击方面板原始伤害累积自身威胁（力微饭否类嘲讽）。</summary>
[SystemOrder(300)]
public sealed class ThreatOnTakenSystem : YuoSystem<CombatantComponent, ThreatOnTakenComponent>, IOnHitTrigger
{
    /// <summary>Tag 派发入口：信箱负载宿主为受击方时按攻击方面板原始伤害累积自身威胁。</summary>
    public override void Run(CombatantComponent unit, ThreatOnTakenComponent marker)
    {
        var trigger = unit.Entity.GetComponent<CombatTriggerInboxComponent>()?.Trigger;
        if (trigger is null || !ReferenceEquals(trigger.Context.Target, unit.State)) return;
        CombatTriggerMath.ThreatOnTaken(trigger.Context, unit.State!.Stats.Effects.ThreatOnTakenPoints);
    }
}
