using Domain.Combat;

namespace DaoHua.Ecs;

/// <summary>
/// 战斗触发器效果数学：内联分发与 RunSystem 实体分发两条路径共用同一实现，
/// 保证无实体宿主（纯领域测试、斗法推演）与实体宿主的结算逐位一致。
/// 参数点数由调用方提供：实体路径读 reactive buff 组件，内联路径读 Stats.Effects——
/// 组件参数在挂接时从同一 Effects 快照，战斗期间不变。
/// </summary>
public static class CombatTriggerMath
{
    /// <summary>气血汲取：命中后攻击方按实际伤害比例回复气血，产出治疗衍生事件。</summary>
    public static void HpDrain(CombatHitContext context, double points)
    {
        if (!context.Hit || context.Damage <= 0 || !context.Attacker.IsAlive) return;
        if (points <= 0) return;
        var healing = Math.Min(context.Attacker.Stats.MaxHitpoints - context.Attacker.Hitpoints,
            context.Damage * points / 100);
        if (healing <= 0) return;
        context.Attacker.Hitpoints += healing;
        context.Emit(new CombatEvent(context.AtSeconds, context.Attacker.Id, context.Attacker.Id,
            0, true, context.AttackStats.DamageKind, context.Attacker.Hitpoints)
        { AbilityId = context.AbilityId, Healing = healing });
    }

    /// <summary>灵力汲取：命中后按实际伤害从目标灵力池偷取；目标减多少算多少，攻击方只装得下的部分。</summary>
    public static void ManaDrain(CombatHitContext context, double points)
    {
        if (!context.Hit || context.Damage <= 0 || !context.Attacker.IsAlive) return;
        if (points <= 0) return;
        var drained = Math.Min(context.Target.Manapoints, context.Damage * points / 100);
        if (drained <= 0) return;
        context.Target.Manapoints -= drained;
        context.Attacker.Manapoints = Math.Min(context.Attacker.Stats.MaxManapoints,
            context.Attacker.Manapoints + drained);
    }

    /// <summary>伤害反弹：受击方把实收伤害按比例弹回攻击方；衍生伤害直接落账并计入受击方威胁值。</summary>
    public static void DamageReflect(CombatHitContext context, double points)
    {
        if (!context.Hit || context.Damage <= 0 || !context.Attacker.IsAlive) return;
        if (points <= 0) return;
        var reflected = context.Damage * points / 100;
        if (reflected <= 0) return;
        context.Attacker.Hitpoints = Math.Max(0, context.Attacker.Hitpoints - reflected);
        context.Target.Threat += reflected;
        context.Emit(new CombatEvent(context.AtSeconds, context.Target.Id, context.Attacker.Id,
            reflected, true, context.AttackStats.DamageKind, context.Attacker.Hitpoints));
    }
}
