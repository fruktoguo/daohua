using Domain.Combat;

namespace DaoHua.Ecs;

/// <summary>各类战斗伤害共用的最终扣血裁决，统一留一、实际损失、蓄意与存活人数变化。</summary>
internal static class CombatDamageSettlement
{
    /// <summary>按来源特质限制致死伤害；不改变其他伤害乘区。</summary>
    internal static double Limit(CombatantState attacker, double hitpoints, double damage)
        => hitpoints > 1 && damage >= hitpoints
            && CombatTraitRules.ContainsTrait(attacker.Stats.Traits, CombatTraitRules.HasteTraitId)
            ? hitpoints - 1 : damage;

    /// <summary>扣除生命并返回裁决伤害及实际扣血；对称同刻波次期间暂存至波次缓冲区统一落账。</summary>
    internal static (double Damage, double Actual) Apply(CombatEncounterState? encounter,
        CombatantState attacker, CombatantState target, double damage, bool accumulateXuyi = true)
    {
        if (encounter?.ActiveWave is SimultaneousCombatWave wave)
        {
            var deferred = wave.RecordDamage(encounter, attacker, target, damage, accumulateXuyi);
            return (deferred.Damage, deferred.Actual);
        }
        if (!target.IsAlive) return (0, 0);
        damage = Limit(attacker, target.Hitpoints, Math.Max(0, damage));
        var before = target.Hitpoints;
        target.Hitpoints = Math.Max(0, before - damage);
        var actual = before - target.Hitpoints;
        target.SyncDerivedStacks();
        if (accumulateXuyi && actual > 0 && target.IsAlive
            && CombatTraitRules.ContainsTrait(target.Stats.Traits, CombatTraitRules.XuyiTraitId))
        {
            var existing = target.Buffs.FirstOrDefault(buff => buff.BuffId == CombatTraitRules.XuyiBuffId);
            target.SetBuff(new CombatBuffState(CombatTraitRules.XuyiBuffId, CombatTraitRules.BattleLifetimeSeconds,
                (existing?.Stacks ?? 0) + actual * CombatTraitRules.LevelScale(target, CombatTraitRules.XuyiTraitId),
                "蓄意", ResetOnWave: true));
        }
        if (!target.IsAlive) encounter?.NotifyAliveCountsChanged();
        return (damage, actual);
    }

    /// <summary>读取当前上下文下目标是否存活（同刻波次内读当前出手者视角虚拟存活，常规读真实存活）。</summary>
    internal static bool IsTargetAlive(CombatEncounterState? encounter, CombatantState target)
        => encounter?.ActiveWave is SimultaneousCombatWave wave ? wave.IsLocallyAlive(target) : target.IsAlive;

    /// <summary>即时与衍生伤害使用实时增伤 Buff，避免只读静态面板遗漏欲速则不达等修饰。</summary>
    internal static CombatantStats StrikeStats(CombatantState unit, double power, CombatDamageKind kind, CombatElement element)
        => unit.EffectiveStats with
        {
            AttackPower = power, DamageKind = kind, Element = element,
            TotalDamageBonusPoints = unit.Stats.TotalDamageBonusPoints + unit.BuffTotal(BuffEffectChannel.DamageBonusPoints)
        };
}
