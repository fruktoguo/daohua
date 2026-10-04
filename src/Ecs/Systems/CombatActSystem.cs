using System.Text.Json;
using DaoHua.Ecs.Combat;
using DaoHua.Ecs.Components;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using YuoCore.Ecs;
using static DaoHua.Ecs.DeterministicCombatEngine;

namespace DaoHua.Ecs.Systems;

/// <summary>
/// 玩家与怪物出手 System 共用的规则本体：技能起手、施放、普攻落账与命中反应。
/// 两侧只选择各自的友敌集合和随机侧位；无世界路径也调用同一规则，不维护第二套结算。
/// </summary>
public abstract class CombatActSystem : YuoSystem<CombatEncounterComponent>
{
    /// <summary>技能随机侧位与普攻隔离，偏移保持既有确定性流。</summary>
    private const ulong SkillSideOffset = 0x1000;

    /// <summary>开战即决策各单位的首次出手动作：可放技能直接进入吟唱，否则按普攻周期蓄势。
    /// 已恢复吟唱中的单位（PendingSkillIndex ≥ 0）跳过决策，保留跨战延续的施放。</summary>
    internal static void DecideInitialActions(CombatEncounterState encounter)
    {
        // 复活起身重蓄势也走本入口：已阵亡单位不再排动作，尸体不得携带吟唱标记或预扣灵力。
        // 敌方起手目标同样在玩家方友军威胁轮盘上抽取（决策通道 19）：单盟友提前返回零掷点。
        for (var i = 0; i < encounter.Enemies.Count; i++)
            if (encounter.Enemies[i].IsAlive && encounter.Enemies[i].PendingSkillIndex < 0)
                DecideNextAction(encounter, encounter.Enemies[i], encounter.Enemies, encounter.PlayerAllies,
                    SelectTarget(encounter.PlayerAllies, encounter.Seed, (ulong)(1 + i), encounter.Enemies[i].AttackCount, 19));
        if (encounter.Player.PendingSkillIndex < 0)
            DecideNextAction(encounter, encounter.Player, encounter.PlayerAllies, encounter.Enemies,
                SelectEnemyTarget(encounter, 0, 3));
    }

    /// <summary>
    /// 结算出手边界上已决定的动作：吟唱中的技能在此刻结算（目标在结算时按威胁重新抽取），
    /// 否则结算普通攻击。技能走 side+SkillSideOffset 的独立随机流，与普攻流完全隔离。
    /// </summary>
    internal static void ResolvePending(CombatEncounterState encounter, CombatantState unit,
        IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents,
        CombatantState? target, ulong side, ref List<CombatEvent>? events)
    {
        var pending = unit.PendingSkillIndex;
        unit.PendingSkillIndex = -1;
        if (pending >= 0 && pending < unit.Skills.Count)
        {
            CastSkill(encounter, unit, allies, opponents, target, unit.Skills[pending], side + SkillSideOffset, ref events);
            unit.SkillCastCount += 1;
            return;
        }
        if (target is not null)
            ResolveAttack(encounter, unit, target, side, encounter.ElapsedSeconds, ref events);
    }

    /// <summary>
    /// 出手决策：按槽位顺序挑选首个可用技能进入吟唱——触发条件（统一求值器）、冷却就绪与
    /// 灵力消耗在起手时判定并预扣；吟唱占用整条动作时间线，时长 = castMilliseconds × 行动速度乘区，
    /// 冷却自吟唱完成时刻起算。无可用技能时下一次出手回落为普通攻击，普攻间隔吃战内攻速 Buff。
    /// </summary>
    internal static void DecideNextAction(CombatEncounterState encounter, CombatantState unit,
        IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents, CombatantState? target)
    {
        var skills = unit.Skills;
        if (skills.Count > 0)
        {
            var cooldowns = unit.SkillCooldownUntil ??= new double[skills.Count];
            for (var i = 0; i < skills.Count; i++)
            {
                var slot = skills[i];
                if (cooldowns[i] > encounter.ElapsedSeconds + 1e-12) continue;
                // 触发条件为唯一可用性门槛：空表恒可用；求值失败（依赖缺失/词表外输入）跳过本槽。
                if (slot.Triggers is { Count: > 0 } triggers
                    && !SkillTriggerEvaluator.MatchesAll(triggers, unit, target, allies, opponents))
                    continue;
                var manaCost = AbilityNumber(slot.Ability, "manaCost");
                if (unit.Manapoints < manaCost) continue;
                unit.Manapoints -= manaCost;
                // 吟唱时长 = 配表 × 成长乘区 ÷ 行动速度除数（NonLinear 点数）。
                var castSeconds = Math.Max(1e-9,
                    AbilityNumber(slot.Ability, "castMilliseconds") / 1000d * unit.Stats.CastTimeFactor
                    * PercentValue.Multiplier(unit.Stats.ActionSpeedPoints, PercentCurveMode.NonLinear));
                unit.PendingSkillIndex = i;
                unit.NextAttackAtSeconds = encounter.ElapsedSeconds + castSeconds;
                // 冷却自吟唱完成起算：配表 × 成长乘区 ÷ 冷却缩减除数（NonLinear 点数）。
                cooldowns[i] = unit.NextAttackAtSeconds
                    + AbilityNumber(slot.Ability, "cooldownMilliseconds") / 1000d * unit.Stats.CooldownFactor
                    * PercentValue.Multiplier(unit.Stats.CooldownReductionPoints, PercentCurveMode.NonLinear);
                return;
            }
        }
        unit.PendingSkillIndex = -1;
        // 普攻读条双重除数：攻击速度（面板+战内 Buff 合并）× 行动速度（NonLinear 点数）。
        unit.NextAttackAtSeconds = encounter.ElapsedSeconds
            + unit.Stats.AttackIntervalSeconds
            * PercentValue.Multiplier(unit.Stats.AttackSpeedPoints + unit.BuffAttackSpeedPoints, PercentCurveMode.NonLinear)
            * PercentValue.Multiplier(unit.Stats.ActionSpeedPoints, PercentCurveMode.NonLinear);
    }

    /// <summary>
    /// 技能施放统一结算：伤害/治疗/支援（威胁修正与战内 Buff）走同一条管线，玩家、怪物与斗法镜像完全同构。
    /// 灵力消耗与冷却已在起手决策时预扣，此处只负责完成结算。
    /// 技能类别判定：heal 效果或正治疗量为治疗；伤害为零且目标为友方/自身为支援技；其余为伤害技。
    /// </summary>
    private static void CastSkill(CombatEncounterState encounter, CombatantState caster,
        IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents,
        CombatantState? target, SkillSlot slot, ulong side, ref List<CombatEvent>? events)
    {
        var ability = slot.Ability;
        var spell = AbilityText(ability, "category") == "spell";
        var attackBase = spell ? caster.Stats.SpellAttack : caster.Stats.AttackPower;
        var id = AbilityText(ability, "id") ?? string.Empty;
        var ratioBonus = CombatGrowthCurve.AbilityRatioGrowth(slot.Level);
        var flatBonus = CombatGrowthCurve.AbilityFlatGrowth(slot.Level);
        var targetType = AbilityText(ability, "targetType");
        // 威胁类字段是施放方修正，与技能效果类型无关：任何施放都可携带仇恨增益。
        caster.Threat += AbilityNumber(ability, "threatGain");
        caster.ThreatBonusPoints += AbilityNumber(ability, "threatPoints");
        var healOutput = attackBase * caster.Stats.SkillIntensity * AbilityNumber(ability, "healingRatio") * ratioBonus
            + AbilityNumber(ability, "flatHealing") * flatBonus;
        if (AbilityText(ability, "effectType") == "heal" || healOutput > 0)
        {
            // 治疗目标范围按 targetType 解析；治疗增益（healingBonusRatio）对一切施放单位通用。
            // 免分配展开：self 单目标 / allAllies 顺序遍历存活友方 / 缺省取血量比例最低的存活友方
            // （原 OrderBy+Take(1) 的稳定首元素语义，平局保留列表先见者）。
            var output = healOutput * PercentValue.Multiplier(caster.Stats.Effects.HealingBonusPoints, PercentCurveMode.Linear);
            // 局部函数不能捕获外层 ref 参数：events 以 ref 形参传递，语义与原先一致。
            void ApplyHeal(CombatantState ally, ref List<CombatEvent>? ev)
            {
                var healed = Math.Min(ally.Stats.MaxHitpoints - ally.Hitpoints, Math.Max(0, output));
                ally.Hitpoints += healed;
                ev ??= new List<CombatEvent>();
                ev.Add(new CombatEvent(encounter.ElapsedSeconds, caster.Id, ally.Id, 0, true,
                    spell ? CombatDamageKind.Spell : CombatDamageKind.Physical, ally.Hitpoints)
                {
                    AbilityId = id, Healing = healed,
                    AttackerManapointsAfter = caster.Manapoints,
                    TargetManapointsAfter = ally.Manapoints
                });
            }
            if (targetType == "self")
            {
                ApplyHeal(caster, ref events);
            }
            else if (targetType == "allAllies")
            {
                for (var i = 0; i < allies.Count; i++)
                    if (allies[i].IsAlive) ApplyHeal(allies[i], ref events);
            }
            else if (LowestHitpointsRatioAlly(allies) is { } weakest)
            {
                ApplyHeal(weakest, ref events);
            }
            return;
        }
        var hasDamage = AbilityNumber(ability, "damageRatio") + AbilityNumber(ability, "flatDamage") > 0;
        if (targetType == "self" || !hasDamage && targetType is not ("targetEnemy" or "allEnemies"))
        {
            // 支援技：以自身/友方为目标的零伤害技能；战内 Buff 授予友方并按战斗时钟到期。
            if (ability.TryGetProperty("buff", out var buff) && buff.ValueKind == JsonValueKind.Object)
            {
                var buffId = AbilityText(buff, "buffId") ?? $"ability-buff:{id}";
                var grant = new CombatBuffState(buffId,
                    encounter.ElapsedSeconds + Math.Max(0, AbilityNumber(buff, "durationSeconds")), 1,
                    AbilityNumber(buff, "hpRegen"), AbilityNumber(buff, "mpRegen"),
                    AbilityNumber(buff, "attackSpeedPoints"), AbilityNumber(buff, "damageAbsorptionPoints"),
                    AbilityNumber(buff, "threatPoints"),
                    AbilityNumber(buff, "hpRegenPostRatio"), AbilityNumber(buff, "mpRegenPostRatio"));
                // maxStacks 缺省为 1：同 ID 重复授予刷新时长并叠层至上限。
                var stackCap = Math.Max(1, AbilityNumber(buff, "maxStacks"));
                void GrantBuff(CombatantState ally)
                {
                    var index = ally.Buffs.FindIndex(current => current.BuffId == buffId);
                    if (index < 0) ally.Buffs.Add(grant);
                    else ally.Buffs[index] = ally.Buffs[index] with
                    {
                        ExpiresAtSeconds = grant.ExpiresAtSeconds,
                        Stacks = Math.Min(stackCap, ally.Buffs[index].Stacks + 1)
                    };
                }
                // 目标范围与治疗效果同口径展开：lowestHitpointsAlly 取血量比例最低存活友方
                // （原 OrderBy+Take(1) 稳定首元素语义），allAllies 顺序遍历存活友方，其余作用于自身。
                if (targetType == "lowestHitpointsAlly")
                {
                    if (LowestHitpointsRatioAlly(allies) is { } weakest) GrantBuff(weakest);
                }
                else if (targetType == "allAllies")
                {
                    for (var i = 0; i < allies.Count; i++)
                        if (allies[i].IsAlive) GrantBuff(allies[i]);
                }
                else
                {
                    GrantBuff(caster);
                }
            }
            events ??= new List<CombatEvent>();
            events.Add(new CombatEvent(encounter.ElapsedSeconds, caster.Id, caster.Id, 0, true,
                spell ? CombatDamageKind.Spell : CombatDamageKind.Physical, caster.Hitpoints)
            {
                AbilityId = id,
                AttackerManapointsAfter = caster.Manapoints,
                TargetManapointsAfter = caster.Manapoints
            });
            return;
        }
        var stats = caster.Stats with
        {
            AttackPower = attackBase * caster.Stats.SkillIntensity * AbilityNumber(ability, "damageRatio") * ratioBonus
                + AbilityNumber(ability, "flatDamage") * flatBonus,
            BasicAttackRatio = 1,
            DamageKind = spell ? CombatDamageKind.Spell : CombatDamageKind.Physical,
            Element = AbilityElement(ability)
        };
        // 技能出手以施放方累计完成次数为抽样计数，保证确定性随机流不依赖槽位语义。
        var count = caster.SkillCastCount + 1;
        var maxTargets = ability.TryGetProperty("maxTargets", out var maxTargetsProperty)
            && maxTargetsProperty.TryGetInt32(out var parsed) ? parsed : 0;
        if (targetType == "allEnemies")
        {
            // AoE 对一切施放单位通用：按威胁顺序不可重复抽取至多 maxTargets 个敌方目标。
            foreach (var picked in SelectTargets(opponents, maxTargets > 0 ? maxTargets : int.MaxValue,
                encounter.Seed, side, count, 17))
                ResolveAttack(encounter, caster, picked, side, encounter.ElapsedSeconds, ref events, stats, count, id);
            return;
        }
        var single = target is { IsAlive: true } ? target : SelectTarget(opponents, encounter.Seed, side, count, 16);
        if (single is not null)
            ResolveAttack(encounter, caster, single, side, encounter.ElapsedSeconds, ref events, stats, count, id);
    }

    /// <summary>按破招化解、闪避、暴击顺序判定后，沿「大类抗性穿甲 → 攻方分类增伤 → 守方分类减伤 → 暴击 → 总免伤收口」结算链结算；
    /// 抽样仅依赖已持久化的种子与出手序号。判定前、伤害落账前、落账后三个点位依次驱动钩子管线；钩子衍生伤害直接落账不再回流。</summary>
    private static void ResolveAttack(CombatEncounterState encounter, CombatantState attacker, CombatantState target,
        ulong side, double at, ref List<CombatEvent>? events,
        CombatantStats? statsOverride = null, double? countOverride = null, string? abilityId = null)
    {
        if (!attacker.IsAlive || !target.IsAlive) return;
        var context = new CombatHitContext
        {
            Encounter = encounter,
            Attacker = attacker,
            Target = target,
            AttackStats = statsOverride ?? attacker.Stats,
            AbilityId = abilityId,
            Side = side,
            Count = countOverride ?? attacker.AttackCount,
            AtSeconds = at
        };
        ResolveHit(context);
        target.Hitpoints = Math.Max(0, target.Hitpoints - context.Damage);
        attacker.AttackCount += 1;
        attacker.Threat += context.Damage;
        events ??= new List<CombatEvent>();
        // 主事件先行占位，Aftermath（吸血/吸蓝/反伤）结算完成后回填双方灵力真值；
        // 衍生事件追加在主事件之后，保持战斗日志时序不变。
        var mainEventIndex = events.Count;
        events.Add(new CombatEvent(at, attacker.Id, target.Id, context.Damage, context.Hit, context.AttackStats.DamageKind, target.Hitpoints)
        { Critical = context.Critical, Break = context.Broken, Parried = context.Parried, AbilityId = abilityId });
        EmitHitTriggers(encounter, context, events, mainEventIndex);
    }

    /// <summary>
    /// 命中落账后的触发器入队与固定点消费：攻击方/受击方各入队一条，按入队顺序立即消费，
    /// 消费点与原 ApplyAftermath 同位——UpdateOutcome 永远在触发器结算之后运行。
    /// 衍生事件与主事件灵力回填由派发方按序完成。
    /// </summary>
    private static void EmitHitTriggers(CombatEncounterState encounter, CombatHitContext context,
        List<CombatEvent> events, int mainEventIndex)
    {
        var pending = encounter.PendingTriggers;
        pending.Add(new PendingCombatTrigger(CombatTriggerKind.OnHit,
            context.Attacker.EntityId, context.Attacker, context, events, mainEventIndex));
        pending.Add(new PendingCombatTrigger(CombatTriggerKind.OnHit,
            context.Target.EntityId, context.Target, context, events, mainEventIndex));
        (encounter.TriggerSink ?? InlineCombatTriggerSink.Instance).Drain(encounter);
    }
}
