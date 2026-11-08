using Domain.Combat;
using Domain.Common;

namespace DaoHua.Ecs;

/// <summary>特质命中与周期反应的共享规则，实体和内联路径只负责薄派发。</summary>
internal static class CombatTraitReactions
{
    /// <summary>直接命中落账后先施加燃魂，再判定蓄意释放；衍生伤害不重新派发命中。</summary>
    internal static void OnHit(CombatHitContext context)
    {
        if (!context.Hit || !context.Attacker.IsAlive) return;
        ApplyRanhun(context.Attacker, context.Target, context.AttackStats.Element, context.ActualDamage, context.AtSeconds);
        var attacker = context.Attacker;
        if (!CombatTraitRules.ContainsTrait(attacker.Stats.Traits, CombatTraitRules.XuyiTraitId)) return;
        var buff = attacker.Buffs.FirstOrDefault(current => current.BuffId == CombatTraitRules.XuyiBuffId);
        if (buff is null || buff.Stacks <= attacker.Stats.MaxHitpoints) return;
        attacker.RemoveBuffsWhere(current => current.BuffId == CombatTraitRules.XuyiBuffId);
        var opponents = context.Encounter.PlayerAllies.Contains(attacker)
            ? context.Encounter.Enemies : context.Encounter.PlayerAllies;
        // 主目标先结算，随后按阵容序溅射；消费先于落账，且爆发不重新积蓄意。
        if (context.Target.IsAlive) Burst(context.Target, true);
        foreach (var target in opponents)
            if (target.IsAlive && !ReferenceEquals(target, context.Target)) Burst(target, false);

        void Burst(CombatantState target, bool primary)
        {
            var damage = primary ? buff.Stacks : DeterministicCombatEngine.ResolveStrikeDamage(
                CombatDamageSettlement.StrikeStats(attacker, buff.Stacks, CombatDamageKind.Physical, CombatElement.None), target);
            var settled = CombatDamageSettlement.Apply(context.Encounter, attacker, target, damage, accumulateXuyi: false);
            attacker.Threat += settled.Damage;
            context.Encounter.StatsBook?.RecordHit(attacker, target, primary ? CombatDamageKind.True : CombatDamageKind.Physical, settled.Damage, primary ? 0 : Math.Max(0, buff.Stacks - settled.Damage));
            context.Emit(new CombatEvent(context.AtSeconds, attacker.Id, target.Id, settled.Damage, true,
                primary ? CombatDamageKind.True : CombatDamageKind.Physical, target.Hitpoints) { AbilityId = CombatTraitRules.XuyiTraitId });
        }
    }

    /// <summary>按实际扣血叠层，最后施加者取得同一燃魂状态的伤害归属。</summary>
    internal static void ApplyRanhun(CombatantState attacker, CombatantState target, CombatElement element, double actual, double at)
    {
        if (actual <= 0 || !target.IsAlive || element != CombatElement.Fire
            || !CombatTraitRules.ContainsTrait(attacker.Stats.Traits, CombatTraitRules.RanhunTraitId)) return;
        var stacks = Math.Max(1, Math.Floor(actual * 0.02 * CombatTraitRules.LevelScale(attacker, CombatTraitRules.RanhunTraitId)));
        var existing = target.Buffs.FirstOrDefault(buff => buff.BuffId == CombatTraitRules.RanhunBuffId);
        target.SetBuff(new CombatBuffState(CombatTraitRules.RanhunBuffId, at + 5, stacks + (existing?.Stacks ?? 0),
            "燃魂", ResetOnWave: true, Effects: [new(BuffEffectChannel.FireSpellDamagePerSecond, 1)], SourceUnitId: attacker.Id));
    }

    /// <summary>整秒自然边界结算火属性术法持续伤害，不回流命中触发器。</summary>
    internal static void Tick(CombatEncounterState encounter, CombatantState target, double at, ref List<CombatEvent>? events)
    {
        if (!target.IsAlive) return;
        foreach (var buff in target.Buffs)
        {
            var power = buff.EffectValue(BuffEffectChannel.FireSpellDamagePerSecond) * buff.Stacks;
            if (power <= 0 || buff.ExpiresAtSeconds + 1e-9 < at) continue;
            var source = encounter.PlayerAllies.Concat(encounter.Enemies).FirstOrDefault(unit => unit.Id == buff.SourceUnitId)
                ?? throw new DomainException(DomainErrorCode.InvariantViolation, "持续伤害缺少战斗来源。");
            var attack = CombatDamageSettlement.StrikeStats(source, power, CombatDamageKind.Spell, CombatElement.Fire);
            var damage = DeterministicCombatEngine.ResolveStrikeDamage(attack, target);
            var settled = CombatDamageSettlement.Apply(encounter, source, target, damage);
            source.Threat += settled.Damage;
            encounter.StatsBook?.RecordHit(source, target, CombatDamageKind.Spell, settled.Damage, Math.Max(0, power - settled.Damage));
            (events ??= []).Add(new CombatEvent(at, source.Id, target.Id, settled.Damage, true, CombatDamageKind.Spell, target.Hitpoints)
                { AbilityId = CombatTraitRules.RanhunTraitId, AttackerManapointsAfter = source.Manapoints, TargetManapointsAfter = target.Manapoints });
            // 积蓄意可能修改目标 Buff 列表；燃魂当前仅一条，收尾后返回避免枚举失效。
            return;
        }
    }
}

/// <summary>内联特质命中反应，位于汲取与反伤之前。</summary>
[CombatTriggerOrder(50)]
public sealed class InlineTraitReactionHandler : CombatTriggerHandler, IInlineOnHitTrigger
{
    /// <summary>只消费攻击方的命中条目。</summary>
    public void OnHit(PendingCombatTrigger trigger)
    {
        if (ReferenceEquals(trigger.Unit, trigger.Context.Attacker)) CombatTraitReactions.OnHit(trigger.Context);
    }
}
