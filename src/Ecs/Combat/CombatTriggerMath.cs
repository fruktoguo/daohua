using Domain.Combat;

namespace DaoHua.Ecs;

/// <summary>
/// 战斗触发器效果数学：内联分发与 RunSystem 实体分发两条路径共用同一实现，
/// 保证无实体宿主（纯领域测试、斗法推演）与实体宿主的结算逐位一致。
/// 参数点数由调用方实时读取 Stats.Effects 传入——实体路径的标记组件只是
/// 「该效果开启」的能力开关，不携带参数；挂摘由 CombatTriggerSurface.Sync 按 Effects 重评估。
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
        // 气血汲取按攻击方归属计入「其他治疗」输出。
        context.Encounter.StatsBook?.RecordHealing(context.Attacker, autoRegen: false, healing);
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
        // 反伤按受击方输出、攻击方实收入账；反弹伤害不再走减免链，减免记 0。
        context.Encounter.StatsBook?.RecordHit(context.Target, context.Attacker,
            context.AttackStats.DamageKind, reflected, 0);
        context.Emit(new CombatEvent(context.AtSeconds, context.Target.Id, context.Attacker.Id,
            reflected, true, context.AttackStats.DamageKind, context.Attacker.Hitpoints));
    }

    /// <summary>
    /// 受击涨威胁（力微饭否类嘲讽）：受击方按减免链前原始威力 <see cref="CombatHitContext.RawDamage"/>
    /// 折算点数累积自身威胁；未命中不算「受到伤害」，零原始伤害不产生威胁。
    /// </summary>
    public static void ThreatOnTaken(CombatHitContext context, double points)
    {
        if (!context.Hit || points <= 0) return;
        var gained = context.RawDamage * points / 100;
        if (gained <= 0) return;
        context.Target.Threat += gained;
    }

    /// <summary>
    /// 阵盘受击反击反应：敌方单位受到有效伤害时，己方在场存活且持有天雷阵/血煞阵的单位
    /// 各自独立判定 20% 概率触发天雷或血煞反击。
    /// </summary>
    public static void FormationReactiveStrike(CombatHitContext context)
    {
        if (!context.Hit || context.Damage <= 0 || !context.Target.IsAlive) return;
        var encounter = context.Encounter;
        if (encounter.PlayerAllies.Contains(context.Target)) return;

        var allies = encounter.PlayerAllies;
        for (var i = 0; i < allies.Count; i++)
        {
            var ally = allies[i];
            if (!ally.IsAlive || !context.Target.IsAlive) continue;

            // 1. 天雷阵盘：20% 几率触发木系雷击，每秒最多 1 次，耗蓝 1%
            if (ally.BuffStacks("formation.tianlei") > 0)
            {
                var canTrigger = ally.LastTianleiTriggerAtSeconds < 0
                    || (context.AtSeconds - ally.LastTianleiTriggerAtSeconds) >= 1.0 - 1e-9;
                var manaCost = ally.Stats.MaxManapoints * 0.01;
                if (canTrigger && ally.Manapoints >= manaCost && context.NextRoll() < 0.20)
                {
                    ally.Manapoints = Math.Max(0, ally.Manapoints - manaCost);
                    ally.LastTianleiTriggerAtSeconds = context.AtSeconds;

                    var rawDamage = ally.EffectiveStats.SpellAttack * 0.5 + 10;
                    var strikeStats = ally.EffectiveStats with
                    {
                        AttackPower = rawDamage,
                        DamageKind = CombatDamageKind.Spell,
                        Element = CombatElement.Wood
                    };
                    var damage = Math.Max(0, DeterministicCombatEngine.ResolveStrikeDamage(strikeStats, context.Target));
                    if (damage > 0)
                    {
                        var targetHpBefore = context.Target.Hitpoints;
                        context.Target.Hitpoints = Math.Max(0, context.Target.Hitpoints - damage);
                        var actualDamage = targetHpBefore - context.Target.Hitpoints;
                        ally.Threat += actualDamage;
                        encounter.StatsBook?.RecordHit(ally, context.Target, CombatDamageKind.Spell, actualDamage, 0);
                        context.Emit(new CombatEvent(context.AtSeconds, ally.Id, context.Target.Id,
                            actualDamage, true, CombatDamageKind.Spell, context.Target.Hitpoints)
                        {
                            AbilityId = "item.tianlei-formation-disk"
                        });
                    }
                }
            }

            if (!context.Target.IsAlive) break;

            // 2. 血煞阵盘：20% 几率触发物理无五行血煞，每秒最多 1 次，10% 最大生命伤害并 100% 吸血
            if (ally.BuffStacks("formation.xuesha") > 0)
            {
                var canTrigger = ally.LastXueshaTriggerAtSeconds < 0
                    || (context.AtSeconds - ally.LastXueshaTriggerAtSeconds) >= 1.0 - 1e-9;
                if (canTrigger && context.NextRoll() < 0.20)
                {
                    ally.LastXueshaTriggerAtSeconds = context.AtSeconds;

                    var rawDamage = ally.Stats.MaxHitpoints * 0.10;
                    var strikeStats = ally.EffectiveStats with
                    {
                        AttackPower = rawDamage,
                        DamageKind = CombatDamageKind.Physical,
                        Element = CombatElement.None
                    };
                    var damage = Math.Max(0, DeterministicCombatEngine.ResolveStrikeDamage(strikeStats, context.Target));
                    if (damage > 0)
                    {
                        var targetHpBefore = context.Target.Hitpoints;
                        context.Target.Hitpoints = Math.Max(0, context.Target.Hitpoints - damage);
                        var actualDamage = targetHpBefore - context.Target.Hitpoints;

                        var heal = Math.Min(ally.Stats.MaxHitpoints - ally.Hitpoints, actualDamage);
                        if (heal > 0)
                        {
                            ally.Hitpoints += heal;
                            encounter.StatsBook?.RecordHealing(ally, autoRegen: false, heal);
                        }

                        ally.Threat += actualDamage;
                        encounter.StatsBook?.RecordHit(ally, context.Target, CombatDamageKind.Physical, actualDamage, 0);
                        context.Emit(new CombatEvent(context.AtSeconds, ally.Id, context.Target.Id,
                            actualDamage, true, CombatDamageKind.Physical, context.Target.Hitpoints)
                        {
                            AbilityId = "item.xuesha-formation-disk",
                            Healing = heal
                        });
                    }
                }
            }
        }
    }
}
