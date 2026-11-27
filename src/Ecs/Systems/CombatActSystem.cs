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
    /// <summary>非本机盟友随机侧位基底：成员下标 ≥1 的盟友用 0x400+下标，与敌侧位（1..8）和技能偏移域隔离。</summary>
    private const ulong AllySideBase = 0x400;
    /// <summary>对称双阵营甲侧随机侧位基底（与 PVE 侧位空间隔离，双侧对称分配）。</summary>
    private const ulong SymmetricSideAOffset = 0x2000;
    /// <summary>对称双阵营乙侧随机侧位基底。</summary>
    private const ulong SymmetricSideBOffset = 0x3000;
    /// <summary>对称双阵营统一的出手目标抽取通道（双侧共用同一通道号）。</summary>
    internal const ulong SymmetricResolveTargetChannel = 18;
    /// <summary>对称双阵营统一的决策目标抽取通道（双侧共用同一通道号）。</summary>
    internal const ulong SymmetricDecideTargetChannel = 19;

    /// <summary>
    /// 盟友随机侧位：成员 0 沿用单人位 0（单人路径位级不变），其余成员高位偏移，
    /// 只依赖共享剧本成员下标——与成员所在分片/在线状态无关，各端重放逐位一致。
    /// </summary>
    internal static ulong AllySide(int memberIndex) => memberIndex == 0 ? 0UL : AllySideBase + (ulong)memberIndex;

    /// <summary>
    /// 对称双阵营随机侧位：由遭遇种子奇偶决定甲/乙两翼对 0x2000/0x3000 标签的映射归属，
    /// 配合外层规范化归序同时消除「发起方偏向」与「UUID 字典序偏向」。
    /// </summary>
    internal static ulong SymmetricUnitSide(long seed, CombatSide side, int sideIndex)
    {
        var flip = (seed & 1L) != 0;
        var useB = (side == CombatSide.SideB) ^ flip;
        return (useB ? SymmetricSideBOffset : SymmetricSideAOffset) + (ulong)sideIndex;
    }

    private readonly record struct ActionDecisionPlan(
        CombatantState Unit,
        int SkillIndex,
        double ManaCost,
        double HpCost,
        double NextAttackAtSeconds,
        double CooldownUntil);

    /// <summary>开战即决策各单位的首次出手动作：可放技能直接进入吟唱，否则按普攻周期蓄势。
    /// 已恢复吟唱中的单位（PendingSkillIndex ≥ 0）跳过决策，保留跨战延续的施放。
    /// 对称模式下双侧单位基于同一冻结入场快照并行求值后统一提交预扣，消除视野先后偏差。</summary>
    internal static void DecideInitialActions(CombatEncounterState encounter)
    {
        CombatTraitReactions.ApplyBattleStartReactions(encounter);
        if (encounter.IsSymmetric)
        {
            var pending = new List<CombatantState>(encounter.SideA.Count + encounter.SideB.Count);
            for (var i = 0; i < encounter.SideA.Count; i++)
                if (encounter.SideA[i].IsAlive && encounter.SideA[i].PendingSkillIndex < 0)
                    pending.Add(encounter.SideA[i]);
            for (var i = 0; i < encounter.SideB.Count; i++)
                if (encounter.SideB[i].IsAlive && encounter.SideB[i].PendingSkillIndex < 0)
                    pending.Add(encounter.SideB[i]);
            if (pending.Count > 0)
                DecideNextActionsSimultaneously(encounter, pending);
            return;
        }
        // 复活起身重蓄势也走本入口：已阵亡单位不再排动作，尸体不得携带吟唱标记或预扣灵力。
        // 敌方起手目标同样在玩家方友军威胁轮盘上抽取（决策通道 19）：单盟友提前返回零掷点。
        for (var i = 0; i < encounter.Enemies.Count; i++)
            if (encounter.Enemies[i].IsAlive && encounter.Enemies[i].PendingSkillIndex < 0)
                DecideNextAction(encounter, encounter.Enemies[i], encounter.Enemies, encounter.PlayerAllies,
                    SelectTarget(encounter.PlayerAllies, encounter.Seed, (ulong)(1 + i), encounter.Enemies[i].AttackCount, 19));
        // 盟友按成员序逐个决策：目标抽样沿用计数 0/通道 3 口径，成员 0 与单人逐位一致。
        for (var i = 0; i < encounter.PlayerAllies.Count; i++)
            if (encounter.PlayerAllies[i].IsAlive && encounter.PlayerAllies[i].PendingSkillIndex < 0)
                DecideNextAction(encounter, encounter.PlayerAllies[i], encounter.PlayerAllies, encounter.Enemies,
                    SelectEnemyTarget(encounter, AllySide(i), 0, 3));
    }

    /// <summary>对称模式单名到点单位的直接出手与下轮决策（零同刻争用，无需开波次缓冲）。</summary>
    internal static void ResolveSymmetricSingle(CombatEncounterState encounter, CombatantState unit,
        ref List<CombatEvent>? events)
    {
        var allies = encounter.AlliesOf(unit);
        var opponents = encounter.OpponentsOf(unit);
        var side = SymmetricUnitSide(encounter.Seed, unit.Side, unit.SideIndex);
        var target = SelectTarget(opponents, encounter.Seed, side, unit.AttackCount, SymmetricResolveTargetChannel);
        ResolvePending(encounter, unit, allies, opponents, target, side, ref events);
        if (unit.IsAlive)
        {
            DecideNextAction(encounter, unit, allies, opponents,
                SelectTarget(opponents, encounter.Seed, side, unit.AttackCount, SymmetricDecideTargetChannel));
        }
    }

    /// <summary>
    /// 对称模式同刻多单位同时出手波次：基于波初快照结算全部到点单位的攻防/吸血/反伤/阵盘/特质/Buff，
    /// 波末统一落账并刷新存活人数特质，最后对本波所有仍存活单位两阶段并行决策下一次出手。
    /// </summary>
    internal static void ResolveSymmetricWave(CombatEncounterState encounter, IReadOnlyList<CombatantState> dueUnits,
        ref List<CombatEvent>? events)
    {
        var startEventIndex = events?.Count ?? 0;
        var wave = new SimultaneousCombatWave(startEventIndex);
        encounter.ActiveWave = wave;
        encounter.DeferAliveCountNotifications = true;
        try
        {
            for (var i = 0; i < dueUnits.Count; i++)
            {
                var unit = dueUnits[i];
                wave.BeginActor();
                var allies = encounter.AlliesOf(unit);
                var opponents = encounter.OpponentsOf(unit);
                var side = SymmetricUnitSide(encounter.Seed, unit.Side, unit.SideIndex);
                var target = SelectTarget(opponents, encounter.Seed, side, unit.AttackCount, SymmetricResolveTargetChannel);
                ResolvePending(encounter, unit, allies, opponents, target, side, ref events);
            }
        }
        finally
        {
            encounter.ActiveWave = null;
        }
        wave.Commit(encounter, ref events);
        encounter.FlushAliveCountNotifications();
        DecideNextActionsSimultaneously(encounter, dueUnits);
    }

    /// <summary>对称两阶段动作决策：先基于同一冻结状态并行求值全部存活单位的下一动作，再统一落账预扣灵力与冷却。</summary>
    internal static void DecideNextActionsSimultaneously(CombatEncounterState encounter, IReadOnlyList<CombatantState> units)
    {
        var plans = new List<ActionDecisionPlan>(units.Count);
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (!unit.IsAlive) continue;
            var allies = encounter.AlliesOf(unit);
            var opponents = encounter.OpponentsOf(unit);
            var side = SymmetricUnitSide(encounter.Seed, unit.Side, unit.SideIndex);
            var target = SelectTarget(opponents, encounter.Seed, side, unit.AttackCount, SymmetricDecideTargetChannel);
            plans.Add(PlanNextAction(encounter, unit, allies, opponents, target));
        }
        for (var i = 0; i < plans.Count; i++)
            CommitActionPlan(plans[i]);
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
            var slot = unit.Skills[pending];
            var abilityId = AbilityText(slot.Ability, "id") ?? string.Empty;
            var hasYixing = unit.BuffStacks(CombatTraitRules.YixingBuffId) > 0;
            var morphOverride = hasYixing ? AbilityText(slot.Ability, "yixingMorphName") : null;
            encounter.ActionRecorder?.BeginAction(encounter.ElapsedSeconds, unit, DuelRecordActionKind.Skill, abilityId, morphOverride);
            try
            {
                CastSkill(encounter, unit, allies, opponents, target, slot, pending, side + SkillSideOffset, ref events);
            }
            finally
            {
                encounter.ActionRecorder?.EndAction();
            }
            unit.SkillCastCount += 1;
            return;
        }
        if (target is not null && !CombatTraitRules.ContainsTrait(unit.Stats.Traits, CombatTraitRules.CangfengTraitId))
        {
            var dealtBasicDamage = false;
            encounter.ActionRecorder?.BeginAction(encounter.ElapsedSeconds, unit, DuelRecordActionKind.BasicAttack);
            try
            {
                ResolveAttack(encounter, unit, target, side, encounter.ElapsedSeconds, ref events, dealtDirectDamage: out dealtBasicDamage);
            }
            finally
            {
                encounter.ActionRecorder?.EndAction();
            }
            NotifyUnitAttacked(encounter, unit, target, encounter.ElapsedSeconds);
            if (dealtBasicDamage)
                ConsumeJianyiAfterDirectDamage(encounter, unit);
        }
    }

    /// <summary>
    /// 出手决策：按槽位顺序挑选首个可用技能进入吟唱——触发条件（统一求值器）、冷却就绪与
    /// 灵力/气血消耗在起手时判定并预扣；吟唱占用整条动作时间线，时长 = castMilliseconds ÷(1+行动速度点数/100)，
    /// 冷却自吟唱完成时刻起算。无可用技能时下一次出手回落为普通攻击（携带「藏锋」则收剑待机）。
    /// </summary>
    internal static void DecideNextAction(CombatEncounterState encounter, CombatantState unit,
        IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents, CombatantState? target)
        => CommitActionPlan(PlanNextAction(encounter, unit, allies, opponents, target));

    /// <summary>只读求值单位的下一次动作方案（不修改灵力/冷却/待施放槽，供单单位与对称两阶段决策共用）。</summary>
    private static ActionDecisionPlan PlanNextAction(CombatEncounterState encounter, CombatantState unit,
        IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents, CombatantState? target)
    {
        var skills = unit.Skills;
        if (skills.Count > 0)
        {
            var cooldowns = unit.SkillCooldownUntil ??= new double[skills.Count];
            // 订阅底座（TASK-20261112-15）：技能与消耗品共用「条件→依赖→唤醒」实现——
            // 每槽条件折算订阅掩码，边界事实增量由钩子折叠进逐槽待判定位；未翻转的槽位
            // 直接复用上次判定（求值纯读无随机副作用，复用结果与重评逐位一致）。
            var selfInEnemies = unit.Side == CombatSide.SideB;
            var masks = unit.SkillWakeMasks ??= BuildSkillWakeMasks(skills, selfInEnemies);
            var pending = unit.SkillWakePending ??= BuildFullPending(skills.Count);
            var verdicts = unit.SkillTriggerVerdicts ??= new bool?[skills.Count];
            var verdictTargets = unit.SkillVerdictTargets ??= new CombatantState?[skills.Count];
            // 判定缓存仅在事实追踪接入时有效；藏锋静默整秒重检与同边界剑意叠层同拍，跳过缓存直取实时层数。
            var tracking = encounter.WakeTracking
                && !CombatTraitRules.ContainsTrait(unit.Stats.Traits, CombatTraitRules.CangfengTraitId);
            for (var i = 0; i < skills.Count; i++)
            {
                var slot = skills[i];
                if (cooldowns[i] > encounter.ElapsedSeconds + 1e-12) continue;
                // 触发条件为唯一可用性门槛：空表恒可用；求值失败（依赖缺失/词表外输入）跳过本槽。
                if (slot.Triggers is { Count: > 0 } triggers)
                {
                    if (verdicts[i] is bool cached && tracking
                        && ReferenceEquals(verdictTargets[i], target)
                        && !masks[i].Intersects(pending[i]))
                    {
                        // 订阅事实未翻转且目标引用不变：判定结果与上次逐位一致，直接复用。
                        if (!cached) continue;
                    }
                    else
                    {
                        RuntimeInstrumentation.Count("skill.trigger_evals");
                        var matched = SkillTriggerEvaluator.MatchesAll(triggers, unit, target, allies,
                            opponents, encounter.ElapsedSeconds);
                        verdicts[i] = matched;
                        verdictTargets[i] = target;
                        pending[i] = SlotWakeMask.None;
                        if (!matched) continue;
                    }
                }
                var manaCost = AbilityNumber(slot.Ability, "manaCost");
                if (unit.Manapoints < manaCost) continue;
                var hpCostRatio = AbilityNumber(slot.Ability, "hpCostMaxHpRatio");
                var hpCost = hpCostRatio > 0 ? unit.Stats.MaxHitpoints * hpCostRatio : 0;
                if (hpCost > 0 && unit.Hitpoints <= hpCost + 1e-9) continue;
                // 吟唱时长 = 配表 ÷ 行动速度除数（NonLinear 点数，境界成长已注入本通道）。
                var castSeconds = Math.Max(1e-9,
                    AbilityNumber(slot.Ability, "castMilliseconds") / 1000d
                    * PercentValue.Multiplier(unit.EffectiveStats.ActionSpeedPoints, PercentCurveMode.NonLinear));
                var nextAttackAt = encounter.ElapsedSeconds + castSeconds;
                // 冷却自吟唱完成起算：配表 ÷ 冷却缩减除数（NonLinear 点数，境界成长已注入本通道）。
                var cdMs = AbilityNumber(slot.Ability, "cooldownMilliseconds");
                var cdMult = PercentValue.Multiplier(unit.Stats.CooldownReductionPoints, PercentCurveMode.NonLinear);
                var cdUntil = nextAttackAt + cdMs / 1000d * cdMult;
                return new ActionDecisionPlan(unit, i, manaCost, hpCost, nextAttackAt, cdUntil);
            }
        }
        // 携带「藏锋」的单位不再进行普通攻击，推进至下一整秒或最近技能冷却就绪时刻重评。
        if (CombatTraitRules.ContainsTrait(unit.Stats.Traits, CombatTraitRules.CangfengTraitId))
        {
            var idleNextAt = Math.Floor(encounter.ElapsedSeconds + 1e-9) + 1.0;
            if (unit.SkillCooldownUntil is { } cds)
            {
                for (var i = 0; i < cds.Length && i < skills.Count; i++)
                    if (cds[i] > encounter.ElapsedSeconds + 1e-9 && cds[i] < idleNextAt)
                        idleNextAt = cds[i];
            }
            return new ActionDecisionPlan(unit, -1, 0, 0, Math.Max(encounter.ElapsedSeconds + 1e-6, idleNextAt), 0);
        }
        // 普攻读条双重除数：攻击速度（面板+战内 Buff 合并）× 行动速度（NonLinear 点数）。
        var basicNextAt = encounter.ElapsedSeconds
            + unit.Stats.AttackIntervalSeconds
            * PercentValue.Multiplier(unit.Stats.AttackSpeedPoints + unit.BuffTotal(BuffEffectChannel.AttackSpeedPoints), PercentCurveMode.NonLinear)
            * PercentValue.Multiplier(unit.EffectiveStats.ActionSpeedPoints, PercentCurveMode.NonLinear);
        return new ActionDecisionPlan(unit, -1, 0, 0, basicNextAt, 0);
    }

    /// <summary>提交单条动作决策方案：落账灵力/气血预扣、待施放槽、下一次出手时刻与技能冷却。</summary>
    private static void CommitActionPlan(ActionDecisionPlan plan)
    {
        var unit = plan.Unit;
        if (plan.SkillIndex >= 0)
        {
            unit.Manapoints -= plan.ManaCost;
            if (plan.HpCost > 0)
            {
                unit.Hitpoints = Math.Max(1e-9, unit.Hitpoints - plan.HpCost);
                unit.SyncDerivedStacks();
            }
            unit.PendingSkillIndex = plan.SkillIndex;
            unit.NextAttackAtSeconds = plan.NextAttackAtSeconds;
            var cooldowns = unit.SkillCooldownUntil ??= new double[unit.Skills.Count];
            cooldowns[plan.SkillIndex] = plan.CooldownUntil;
            return;
        }
        unit.PendingSkillIndex = -1;
        unit.NextAttackAtSeconds = plan.NextAttackAtSeconds;
    }

    /// <summary>按施放方阵营编译逐槽订阅掩码；无条件槽置空掩码（永不唤醒，本就不参与条件门控）。</summary>
    private static SlotWakeMask[] BuildSkillWakeMasks(IReadOnlyList<SkillSlot> skills, bool selfInEnemies)
    {
        var masks = new SlotWakeMask[skills.Count];
        for (var i = 0; i < skills.Count; i++)
            masks[i] = skills[i].Triggers is { Count: > 0 } triggers
                ? TriggerWakePlanner.ResolveMask(triggers, selfInEnemies)
                : SlotWakeMask.None;
        return masks;
    }

    /// <summary>逐槽待判定位初始全量：保证首评必然执行（判定缓存的冷启动语义）。</summary>
    private static SlotWakeMask[] BuildFullPending(int count)
    {
        var pending = new SlotWakeMask[count];
        Array.Fill(pending, SlotWakeMask.Full);
        return pending;
    }

    /// <summary>造成普攻或伤害型技能直伤后记录时刻并散去全部「剑意」。</summary>
    private static void ConsumeJianyiAfterDirectDamage(CombatEncounterState encounter, CombatantState caster)
    {
        caster.LastDirectDamageAtSeconds = encounter.ElapsedSeconds;
        if (caster.BuffStacks(CombatTraitRules.JianyiBuffId) <= 0) return;
        if (encounter.ActiveWave is SimultaneousCombatWave wave)
            wave.DeferBuffOp(() => caster.RemoveBuffsWhere(b => b.BuffId == CombatTraitRules.JianyiBuffId));
        else
            caster.RemoveBuffsWhere(b => b.BuffId == CombatTraitRules.JianyiBuffId);
    }

    /// <summary>
    /// 独立敌对攻击行为通知（普攻/进攻技能/进攻符箓每个被攻击目标调用 1 次，不受多段连击或衍生伤害次数影响）：
    /// 若受击目标正在吟唱配置了 <c>channelingAttackedBuff</c> 的技能（如「天罡断岳」），为其叠 1 层对应增益。
    /// </summary>
    internal static void NotifyUnitAttacked(CombatEncounterState? encounter, CombatantState attacker,
        CombatantState target, double at)
    {
        if (!CombatDamageSettlement.IsTargetAlive(encounter, target)) return;
        var pending = target.PendingSkillIndex;
        if (pending < 0 || pending >= target.Skills.Count) return;
        var slot = target.Skills[pending];
        if (!slot.Ability.TryGetProperty("channelingAttackedBuff", out var buffSpec)
            || buffSpec.ValueKind != JsonValueKind.Object) return;
        var abilityId = AbilityText(slot.Ability, "id") ?? string.Empty;
        var levelGrowth = CombatGrowthCurve.AbilityLevelGrowth(slot.Level);
        GrantBuff(buffSpec, target, target, at, levelGrowth, abilityId, encounter);
    }

    /// <summary>
    /// 技能施放统一结算：伤害/治疗/支援（护盾、威胁修正与战内 Buff）走同一条管线，玩家、怪物与斗法镜像完全同构。
    /// 灵力与气血消耗及冷却已在起手决策时预扣，此处只负责完成结算。
    /// </summary>
    private static void CastSkill(CombatEncounterState encounter, CombatantState caster,
        IReadOnlyList<CombatantState> allies, IReadOnlyList<CombatantState> opponents,
        CombatantState? target, SkillSlot slot, int slotIndex, ulong side, ref List<CombatEvent>? events)
    {
        var ability = slot.Ability;
        var spell = AbilityText(ability, "category") == "spell";
        var attackBase = spell ? caster.Stats.SpellAttack : caster.Stats.AttackPower;
        var id = AbilityText(ability, "id") ?? string.Empty;
        var levelGrowth = CombatGrowthCurve.AbilityLevelGrowth(slot.Level);
        var targetType = AbilityText(ability, "targetType");
        var wave = encounter.ActiveWave as SimultaneousCombatWave;
        // 威胁类字段是施放方修正，与技能效果类型无关：任何施放都可携带仇恨增益。
        // 等级成长与伤害同式（总纲 §5.2）：全部数值项同吃每级 +2% 线性成长。
        var threatGain = AbilityNumber(ability, "threatGain") * levelGrowth;
        var threatPoints = AbilityNumber(ability, "threatPoints") * levelGrowth;
        if (wave is not null)
        {
            wave.AddThreatDelta(caster, threatGain);
            wave.AddThreatBonusDelta(caster, threatPoints);
        }
        else
        {
            caster.Threat += threatGain;
            caster.ThreatBonusPoints += threatPoints;
        }
        var skillBonusMult = PercentValue.Multiplier(caster.Stats.SkillBonusPoints, PercentCurveMode.Linear);
        // 治疗量吃技能增幅点数（Linear，含等阶成长注入），与技能伤害同通道。
        var healOutput = attackBase * skillBonusMult
            * AbilityNumber(ability, "healingRatio") * levelGrowth
            + AbilityNumber(ability, "flatHealing") * levelGrowth;
        if (AbilityText(ability, "effectType") == "heal" || healOutput > 0)
        {
            // 治疗目标范围按 targetType 解析；治疗增益（healingBonusRatio）对一切施放单位通用。
            // 免分配展开：self 单目标 / allAllies 顺序遍历存活友方 / 缺省取血量比例最低的存活友方
            // （原 OrderBy+Take(1) 的稳定首元素语义，平局保留列表先见者）。
            var output = healOutput * PercentValue.Multiplier(caster.EffectiveStats.Effects.HealingBonusPoints, PercentCurveMode.Linear);
            // 局部函数不能捕获外层 ref 参数：events 以 ref 形参传递，语义与原先一致。
            void ApplyHeal(CombatantState ally, ref List<CombatEvent>? ev)
            {
                if (wave is not null)
                {
                    ev ??= new List<CombatEvent>();
                    var evIndex = ev.Count;
                    wave.RecordHeal(encounter, caster, ally, DuelRecordEffectKind.Heal, Math.Max(0, output), combatEventIndex: evIndex);
                    ev.Add(new CombatEvent(encounter.ElapsedSeconds, caster.Id, ally.Id, 0, true,
                        spell ? CombatDamageKind.Spell : CombatDamageKind.Physical, ally.Hitpoints)
                    {
                        AbilityId = id, Healing = Math.Max(0, output),
                        AttackerManapointsAfter = caster.Manapoints,
                        TargetManapointsAfter = ally.Manapoints,
                        AttackerShieldAfter = caster.Shield,
                        TargetShieldAfter = ally.Shield
                    });
                    return;
                }
                var healed = Math.Min(ally.Stats.MaxHitpoints - ally.Hitpoints, Math.Max(0, output));
                ally.Hitpoints += healed;
                // 治疗按施放方归属计入「其他治疗」输出。
                if (healed > 0)
                {
                    encounter.StatsBook?.RecordHealing(caster, autoRegen: false, healed);
                    encounter.ActionRecorder?.RecordHeal(caster, ally, DuelRecordEffectKind.Heal, healed);
                }
                ev ??= new List<CombatEvent>();
                ev.Add(new CombatEvent(encounter.ElapsedSeconds, caster.Id, ally.Id, 0, true,
                    spell ? CombatDamageKind.Spell : CombatDamageKind.Physical, ally.Hitpoints)
                {
                    AbilityId = id, Healing = healed,
                    AttackerManapointsAfter = caster.Manapoints,
                    TargetManapointsAfter = ally.Manapoints,
                    AttackerShieldAfter = caster.Shield,
                    TargetShieldAfter = ally.Shield
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
        var maxHpDamageRatio = AbilityNumber(ability, "maxHpDamageRatio");
        var shieldDamageRatio = AbilityNumber(ability, "shieldDamageRatio");
        var hasDamage = AbilityNumber(ability, "damageRatio") + AbilityNumber(ability, "flatDamage")
            + maxHpDamageRatio + shieldDamageRatio > 0;
        var ranhunBase = AbilityNumber(ability, "ranhunBaseStacks");
        var ranhunRatio = AbilityNumber(ability, "ranhunSpellAttackRatio");
        var onHitBuffSpecs = AbilityBuffSpecs(ability, "onHitBuff", "onHitBuffs");
        var selfBuffSpecs = AbilityBuffSpecs(ability, "selfBuff", "selfBuffs");
        var hasOffensivePayload = hasDamage || ranhunBase > 0 || ranhunRatio > 0 || onHitBuffSpecs.Count > 0;
        if (targetType == "self" || !hasOffensivePayload && targetType is not ("targetEnemy" or "allEnemies"))
        {
            // 支援技：以自身/友方为目标的零伤害技能（含护盾赋予如「罡气护体」及战内 Buff）。
            var shieldMaxHpRatio = AbilityNumber(ability, "shieldMaxHpRatio");
            if (shieldMaxHpRatio > 0)
            {
                var shieldGain = caster.Stats.MaxHitpoints * shieldMaxHpRatio * levelGrowth * skillBonusMult;
                if (shieldGain > 0)
                {
                    if (wave is not null) wave.AddShieldGain(caster, shieldGain);
                    else caster.Shield += shieldGain;
                }
            }
            var buffSpecs = AbilityBuffSpecs(ability);
            if (buffSpecs.Count > 0)
            {
                if (targetType == "lowestHitpointsAlly")
                {
                    if (LowestHitpointsRatioAlly(allies) is { } weakest)
                        foreach (var spec in buffSpecs) GrantBuff(spec, caster, weakest, encounter.ElapsedSeconds, levelGrowth, id, encounter);
                }
                else if (targetType == "allAllies")
                {
                    for (var i = 0; i < allies.Count; i++)
                        if (allies[i].IsAlive)
                            foreach (var spec in buffSpecs) GrantBuff(spec, caster, allies[i], encounter.ElapsedSeconds, levelGrowth, id, encounter);
                }
                else
                {
                    foreach (var spec in buffSpecs) GrantBuff(spec, caster, caster, encounter.ElapsedSeconds, levelGrowth, id, encounter);
                }
            }
            events ??= new List<CombatEvent>();
            events.Add(new CombatEvent(encounter.ElapsedSeconds, caster.Id, caster.Id, 0, true,
                spell ? CombatDamageKind.Spell : CombatDamageKind.Physical, caster.Hitpoints)
            {
                AbilityId = id,
                AttackerManapointsAfter = caster.Manapoints,
                TargetManapointsAfter = caster.Manapoints,
                AttackerShieldAfter = caster.Shield,
                TargetShieldAfter = caster.Shield
            });
            return;
        }
        var consumeAllShield = AbilityFlag(ability, "consumeAllShield");
        var currentShield = caster.Shield;
        if (consumeAllShield && currentShield > 0)
        {
            if (wave is not null) wave.ConsumeAllShield(caster);
            else caster.Shield = 0;
        }
        // 技能单发威力 = 攻基 ×(1+技能增幅/100) × 技能倍率 + 固定值（保持既有乘法结合序防 1-ULP 漂移）；
        // 含生命/护盾威力项（如「迸裂」）时额外叠加对应基数。
        var skillPower = attackBase * skillBonusMult
            * AbilityNumber(ability, "damageRatio") * levelGrowth
            + AbilityNumber(ability, "flatDamage") * levelGrowth;
        if (maxHpDamageRatio > 0 || shieldDamageRatio > 0)
        {
            skillPower += (caster.Stats.MaxHitpoints * maxHpDamageRatio
                + (consumeAllShield ? currentShield : caster.Shield) * shieldDamageRatio) * skillBonusMult * levelGrowth;
        }
        var stats = caster.Stats with
        {
            AttackPower = skillPower,
            DamageKind = spell ? CombatDamageKind.Spell : CombatDamageKind.Physical,
            Element = AbilityElement(ability)
        };
        var consumeBuffId = AbilityText(ability, "consumeBuffId");
        var consumeBuffExtraPerStack = AbilityNumber(ability, "consumeBuffExtraDamagePerStack");
        var consumedBuffStacks = !string.IsNullOrEmpty(consumeBuffId) ? caster.BuffStacks(consumeBuffId) : 0;
        var postFinalDamageMultiplier = consumedBuffStacks > 0 && consumeBuffExtraPerStack > 0
            ? 1.0 + consumeBuffExtraPerStack * consumedBuffStacks
            : 1.0;
        // 技能出手以施放方累计完成次数为抽样计数，保证确定性随机流不依赖槽位语义。
        var count = caster.SkillCastCount + 1;
        var hasYixing = caster.BuffStacks(CombatTraitRules.YixingBuffId) > 0;
        var abilityNameOverride = hasYixing ? AbilityText(ability, "yixingMorphName") : null;
        var effectiveTargetType = hasYixing ? (AbilityText(ability, "yixingMorphTargetType") ?? targetType) : targetType;
        var ignorePrimaryThreat = hasYixing && CombatTraitRules.ContainsTrait(caster.Stats.Traits, CombatTraitRules.JianyuxinTraitId);
        var extraRanhunStacks = ranhunBase > 0 || ranhunRatio > 0
            ? Math.Max(1, Math.Floor((Math.Max(0, attackBase) * skillBonusMult * ranhunRatio + ranhunBase) * levelGrowth + 1e-9))
            : 0;
        var maxTargets = ability.TryGetProperty("maxTargets", out var maxTargetsProperty)
            && maxTargetsProperty.TryGetInt32(out var parsed) ? parsed : 0;
        var zeroDirectDamage = !hasDamage;
        var killedAnyTarget = false;
        var dealtAnyDirectDamage = false;
        List<CombatantState>? attackedTargets = null;
        void Strike(CombatantState foe, double? rollCountOverride, ref List<CombatEvent>? ev)
        {
            attackedTargets ??= new List<CombatantState>(4);
            var alreadyNoted = false;
            for (var k = 0; k < attackedTargets.Count; k++)
                if (ReferenceEquals(attackedTargets[k], foe)) { alreadyNoted = true; break; }
            if (!alreadyNoted) attackedTargets.Add(foe);
            var wasAlive = CombatDamageSettlement.IsTargetAlive(encounter, foe);
            ResolveAttack(encounter, caster, foe, side, encounter.ElapsedSeconds, ref ev,
                out var dealtThisStrike, stats, rollCountOverride, id,
                doubleAccuracy: hasYixing, abilityNameOverride: abilityNameOverride,
                extraRanhunStacks: extraRanhunStacks, zeroDirectDamage: zeroDirectDamage,
                onHitBuffs: onHitBuffSpecs, levelGrowth: levelGrowth,
                postFinalDamageMultiplier: postFinalDamageMultiplier);
            if (dealtThisStrike) dealtAnyDirectDamage = true;
            if (wasAlive && !CombatDamageSettlement.IsTargetAlive(encounter, foe)) killedAnyTarget = true;
        }
        if (effectiveTargetType == "allEnemies")
        {
            // AoE 对一切施放单位通用：按威胁顺序（剑与心+移形时主目标无视仇恨随机）不可重复抽取至多 maxTargets 个敌方目标。
            var pickedList = SelectTargets(opponents, maxTargets > 0 ? maxTargets : int.MaxValue,
                encounter.Seed, side, count, 17, ignorePrimaryThreat);
            if (pickedList.Count == 0) return;
            for (var i = 0; i < pickedList.Count; i++)
                Strike(pickedList[i], count, ref events);
        }
        else
        {
            // 触发剑与心+移形时必须跳过外层按仇恨预选的 target，强制等概率重新抽取主要目标。
            var single = !ignorePrimaryThreat && target is { IsAlive: true }
                ? target
                : SelectTarget(opponents, encounter.Seed, side, count, 16, ignorePrimaryThreat);
            if (single is null) return;
            Strike(single, count, ref events);
            // 多段打击（hitCount>1，连环等）：对同一目标逐段独立结算完整技能伤害。
            var hitCount = Math.Max(1, (int)AbilityNumber(ability, "hitCount"));
            for (var hitIndex = 1; hitIndex < hitCount && CombatDamageSettlement.IsTargetAlive(encounter, single); hitIndex++)
                Strike(single, null, ref events);
            // 击杀连锁（chainOnKillCount）：目标阵亡后按威胁轮盘抽另一存活敌方再结算一次完整技能伤害。
            var chainMax = (int)AbilityNumber(ability, "chainOnKillCount");
            for (var chainIndex = 0; chainIndex < chainMax && !CombatDamageSettlement.IsTargetAlive(encounter, single); chainIndex++)
            {
                single = SelectTarget(opponents, encounter.Seed, side, count, 20 + (ulong)chainIndex);
                if (single is null || !CombatDamageSettlement.IsTargetAlive(encounter, single)) break;
                Strike(single, count, ref events);
            }
        }
        // 全体打击结算完成后：通知各受击目标一次独立攻击行为（天罡断岳蓄层），并移除施法者自身已消耗的天罡与剑意。
        if (attackedTargets is not null)
        {
            for (var i = 0; i < attackedTargets.Count; i++)
                NotifyUnitAttacked(encounter, caster, attackedTargets[i], encounter.ElapsedSeconds);
        }
        if (!string.IsNullOrEmpty(consumeBuffId) && consumedBuffStacks > 0)
        {
            if (wave is not null) wave.DeferBuffOp(() => caster.RemoveBuffsWhere(b => b.BuffId == consumeBuffId));
            else caster.RemoveBuffsWhere(b => b.BuffId == consumeBuffId);
        }
        if (dealtAnyDirectDamage)
            ConsumeJianyiAfterDirectDamage(encounter, caster);
        // 移形按伤害/进攻型技能施放次数固定消耗 1 层（不论命中几次或几个目标）。
        if (hasYixing)
        {
            if (wave is not null) wave.DeferBuffOp(() => caster.ConsumeOneBuffStack(CombatTraitRules.YixingBuffId));
            else caster.ConsumeOneBuffStack(CombatTraitRules.YixingBuffId);
        }
        // 先导移形击杀刷新：起手已持有「移形」且本次进攻击杀至少一名目标时，立即清零当前技能冷却。
        if (hasYixing && killedAnyTarget && AbilityFlag(ability, "resetCooldownOnYixingKill")
            && caster.SkillCooldownUntil is { } cooldowns && (uint)slotIndex < (uint)cooldowns.Length)
            cooldowns[slotIndex] = 0;
        // 进攻技能附加自我增益（如瞬闪仅在起手无移形时获 1 层移形；破军恒获 1 层移形与 5 层势不可挡）。
        if (CombatDamageSettlement.IsTargetAlive(encounter, caster) && selfBuffSpecs.Count > 0)
        {
            for (var i = 0; i < selfBuffSpecs.Count; i++)
            {
                var spec = selfBuffSpecs[i];
                if (AbilityFlag(spec, "skipIfHasYixing") && hasYixing) continue;
                GrantBuff(spec, caster, caster, encounter.ElapsedSeconds, levelGrowth, id, encounter);
            }
        }
    }

    /// <summary>按破招化解、闪避、暴击顺序判定后，沿「大类抗性穿甲 → 攻方分类增伤 → 守方分类减伤 → 暴击 → 总免伤收口」结算链结算；
    /// 抽样仅依赖已持久化的种子与出手序号。判定前、伤害落账前、落账后三个点位依次驱动钩子管线；钩子衍生伤害直接落账不再回流。</summary>
    private static void ResolveAttack(CombatEncounterState encounter, CombatantState attacker, CombatantState target,
        ulong side, double at, ref List<CombatEvent>? events, out bool dealtDirectDamage,
        CombatantStats? statsOverride = null, double? countOverride = null, string? abilityId = null,
        bool doubleAccuracy = false, string? abilityNameOverride = null, double extraRanhunStacks = 0,
        bool zeroDirectDamage = false, IReadOnlyList<JsonElement>? onHitBuffs = null, double levelGrowth = 1,
        double postFinalDamageMultiplier = 1.0)
    {
        dealtDirectDamage = false;
        var wave = encounter.ActiveWave as SimultaneousCombatWave;
        if (!attacker.IsAlive || !CombatDamageSettlement.IsTargetAlive(encounter, target)) return;
        var rawAttackStats = statsOverride ?? attacker.Stats;
        var resolvedElement = CombatTraitRules.ResolveDamageElement(attacker.Stats.Traits, rawAttackStats.Element);
        var attackStats = resolvedElement == rawAttackStats.Element
            ? rawAttackStats
            : rawAttackStats with { Element = resolvedElement };
        var context = new CombatHitContext
        {
            Encounter = encounter,
            Attacker = attacker,
            Target = target,
            AttackStats = attackStats,
            AbilityId = abilityId,
            DoubleAccuracy = doubleAccuracy,
            ZeroDirectDamage = zeroDirectDamage,
            PostFinalDamageMultiplier = postFinalDamageMultiplier,
            Side = side,
            Count = countOverride ?? attacker.AttackCount,
            AtSeconds = at
        };
        var wasTargetAlive = CombatDamageSettlement.IsTargetAlive(encounter, target);
        ResolveHit(context);
        var settled = CombatDamageSettlement.Apply(encounter, attacker, target, context.Damage);
        context.Damage = settled.Damage;
        context.ActualDamage = settled.Actual;
        dealtDirectDamage = context.Hit && context.Damage > 0;
        // 行动级战斗统计：攻方按伤害类别计输出、守方计实收与减免（减免 = 原始威力 − 实收，未命中不落账）。
        encounter.StatsBook?.RecordHit(attacker, target, context.AttackStats.DamageKind,
            context.Damage, context.Hit ? Math.Max(0, context.RawDamage - context.Damage) : 0);
        if (encounter.ActionRecorder is { } recorder)
        {
            var flags = context.Hit ? DuelRecordHitMark.Hit : DuelRecordHitMark.Dodge;
            if (context.Critical) flags |= DuelRecordHitMark.Crit;
            if (context.Broken) flags |= DuelRecordHitMark.Break;
            if (context.Parried) flags |= DuelRecordHitMark.Parry;
            recorder.RecordDamage(attacker, target, DuelRecordEffectKind.Damage,
                context.AttackStats.DamageKind, context.AttackStats.Element, flags,
                context.Damage, abilityId, targetDowned: wasTargetAlive && !CombatDamageSettlement.IsTargetAlive(encounter, target));
        }
        attacker.AttackCount += 1;
        if (wave is not null) wave.AddThreatDelta(attacker, context.Damage);
        else attacker.Threat += context.Damage;
        events ??= new List<CombatEvent>();
        // 主事件先行占位，Aftermath（吸血/吸蓝/反伤）结算完成后回填双方灵力真值；
        // 衍生事件追加在主事件之后，保持战斗日志时序不变。
        var mainEventIndex = events.Count;
        events.Add(new CombatEvent(at, attacker.Id, target.Id, context.Damage, context.Hit, context.AttackStats.DamageKind, target.Hitpoints)
        {
            Critical = context.Critical, Break = context.Broken, Parried = context.Parried,
            AbilityId = abilityId, AbilityNameOverride = abilityNameOverride,
            AttackerShieldAfter = attacker.Shield, TargetShieldAfter = target.Shield
        });
        EmitHitTriggers(encounter, context, events, mainEventIndex);
        // 闪身步：成功闪避攻击时获得 1 层持续 5s 的「移形」（受反比例内置冷却约束）。
        if (!context.Hit && CombatDamageSettlement.IsTargetAlive(encounter, target)
            && CombatTraitRules.ContainsTrait(target.Stats.Traits, CombatTraitRules.ShanshenbuTraitId))
        {
            var shanshenCd = CombatTraitRules.ShanshenbuCooldownSeconds(target.Stats.Traits, target.Stats.TraitLevels);
            if (target.LastShanshenbuTriggerAtSeconds < 0 || at - target.LastShanshenbuTriggerAtSeconds >= shanshenCd - 1e-9)
            {
                target.LastShanshenbuTriggerAtSeconds = at;
                if (wave is not null)
                {
                    var previewExisting = target.Buffs.FirstOrDefault(b => b.BuffId == CombatTraitRules.YixingBuffId);
                    var previewStacks = Math.Min(99, (previewExisting?.Stacks ?? 0) + 1);
                    encounter.ActionRecorder?.RecordBuff(target, target, CombatTraitRules.YixingBuffId, previewStacks,
                        DuelRecordActionKind.Trait, at, CombatTraitRules.ShanshenbuTraitId);
                    wave.DeferBuffOp(() =>
                    {
                        if (!target.IsAlive) return;
                        var cur = target.Buffs.FirstOrDefault(b => b.BuffId == CombatTraitRules.YixingBuffId);
                        var nextStacks = Math.Min(99, (cur?.Stacks ?? 0) + 1);
                        var exp = CombatTraitRules.MergeBuffExpiry(cur?.ExpiresAtSeconds, at + 5.0);
                        target.SetBuff(new CombatBuffState(CombatTraitRules.YixingBuffId, exp, nextStacks, "移形",
                            ResetOnWave: true, Effects: [new(BuffEffectChannel.EvasionPostRatio, 1.0)]));
                    });
                }
                else
                {
                    var cur = target.Buffs.FirstOrDefault(b => b.BuffId == CombatTraitRules.YixingBuffId);
                    var nextStacks = Math.Min(99, (cur?.Stacks ?? 0) + 1);
                    var exp = CombatTraitRules.MergeBuffExpiry(cur?.ExpiresAtSeconds, at + 5.0);
                    target.SetBuff(new CombatBuffState(CombatTraitRules.YixingBuffId, exp, nextStacks, "移形",
                        ResetOnWave: true, Effects: [new(BuffEffectChannel.EvasionPostRatio, 1.0)]));
                    encounter.ActionRecorder?.RecordBuff(target, target, CombatTraitRules.YixingBuffId, nextStacks,
                        DuelRecordActionKind.Trait, at, CombatTraitRules.ShanshenbuTraitId);
                }
            }
        }
        if (context.Hit && extraRanhunStacks > 0)
            CombatTraitReactions.AddRanhunStacks(attacker, target, extraRanhunStacks, at, encounter);
        if (context.Hit && CombatDamageSettlement.IsTargetAlive(encounter, target) && onHitBuffs is { Count: > 0 })
            for (var i = 0; i < onHitBuffs.Count; i++)
                GrantBuff(onHitBuffs[i], attacker, target, at, levelGrowth, abilityId ?? string.Empty, encounter);
    }

    /// <summary>
    /// 解析技能授予的战内 Buff 规格列表：优先指定数组键（如 <c>buffs[]</c>/<c>onHitBuffs[]</c>），回落对应单对象键（<c>buff</c>/<c>onHitBuff</c>）。
    /// </summary>
    private static List<JsonElement> AbilityBuffSpecs(JsonElement ability, string singleKey = "buff", string arrayKey = "buffs")
    {
        if (ability.TryGetProperty(arrayKey, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            var specs = new List<JsonElement>(array.GetArrayLength());
            foreach (var entry in array.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object) specs.Add(entry);
            return specs;
        }
        return ability.TryGetProperty(singleKey, out var single) && single.ValueKind == JsonValueKind.Object
            ? [single]
            : [];
    }

    /// <summary>
    /// 授予单条 Buff 规格：层数由施法者属性（法攻百分比 / 双抗总和按比例）与目标状态
    /// （每损失 N 点气血 1 层）或固定 <c>baseStacks</c> 派生；标记 <c>noLevelGrowth</c> 者数值与派生层数均豁免技能等级放大。
    /// 重复授予时持续时间遵循「保高不降」原则。
    /// </summary>
    internal static void GrantBuff(JsonElement buff, CombatantState caster, CombatantState target,
        double grantedAtSeconds, double levelGrowth, string abilityId, CombatEncounterState? encounter = null,
        bool includeCasterSkillBonus = true)
    {
        var baseBuffId = AbilityText(buff, "buffId") ?? $"ability-buff:{abilityId}";
        var policy = AbilityText(buff, "stackPolicy") ?? "capped";
        var buffId = policy == "perCaster" ? $"{baseBuffId}:{caster.Id}" : baseBuffId;
        var missingPerStack = AbilityNumber(buff, "missingHitpointsPerStack");
        var spellStackPercent = AbilityNumber(buff, "spellAttackStackPercent");
        var resistancePerStack = AbilityNumber(buff, "resistancePerStack");
        var hasStackDerivation = missingPerStack > 0 || spellStackPercent > 0 || resistancePerStack > 0;
        var effectiveLevelGrowth = AbilityFlag(buff, "noLevelGrowth") ? 1.0 : levelGrowth;
        var skillMult = includeCasterSkillBonus
            ? PercentValue.Multiplier(caster.Stats.SkillBonusPoints, PercentCurveMode.Linear)
            : 1.0;
        var growth = effectiveLevelGrowth * skillMult;
        var effectGrowth = hasStackDerivation ? 1.0 : effectiveLevelGrowth;
        var initialStacks = InitialStacks(buff, caster, target, growth);
        var proposedExpiry = grantedAtSeconds + Math.Max(0, AbilityNumber(buff, "durationSeconds"));
        var grant = new CombatBuffState(buffId,
            proposedExpiry, initialStacks,
            AbilityText(buff, "name") ?? baseBuffId,
            ResetOnWave: buff.TryGetProperty("resetOnWave", out var resetFlag) && resetFlag.ValueKind == JsonValueKind.True,
            MissingHitpointsPerStack: missingPerStack > 0 ? missingPerStack / growth : 0,
            Effects: BuffEffectParser.Parse(buff, effectGrowth), SourceUnitId: caster.Id);
        var stackCap = Math.Max(1, AbilityNumber(buff, "maxStacks"));
        if (encounter?.ActiveWave is SimultaneousCombatWave wave)
        {
            var previewExisting = target.Buffs.FirstOrDefault(current => current.BuffId == buffId);
            var previewStacks = previewExisting is null ? initialStacks
                : policy switch
                {
                    "maxKeep" => Math.Max(previewExisting.Stacks, initialStacks),
                    _ => Math.Min(stackCap, previewExisting.Stacks + initialStacks)
                };
            if (previewStacks > 0)
                encounter.ActionRecorder?.RecordBuff(caster, target, baseBuffId, previewStacks);
            wave.DeferBuffOp(() =>
            {
                if (!target.IsAlive) return;
                var existingNow = target.Buffs.FirstOrDefault(current => current.BuffId == buffId);
                var nextNow = existingNow is null ? initialStacks
                    : policy switch
                    {
                        "maxKeep" => Math.Max(existingNow.Stacks, initialStacks),
                        _ => Math.Min(stackCap, existingNow.Stacks + initialStacks)
                    };
                var mergedNow = CombatTraitRules.MergeBuffExpiry(existingNow?.ExpiresAtSeconds, proposedExpiry);
                target.SetBuff(grant with { Stacks = nextNow, ExpiresAtSeconds = mergedNow });
                if (!ReferenceEquals(caster, target) && caster.SkillWakePending is { } casterPendingNow)
                {
                    var sideBuffWake = new SlotWakeMask { AllySide = UnitChannel.Buff, EnemySide = UnitChannel.Buff };
                    for (var i = 0; i < casterPendingNow.Length; i++)
                        casterPendingNow[i] = casterPendingNow[i].Union(sideBuffWake);
                }
                target.SyncDerivedStacks();
            });
            return;
        }
        var existing = target.Buffs.FirstOrDefault(current => current.BuffId == buffId);
        var nextStacks = existing is null ? initialStacks
            : policy switch
            {
                "maxKeep" => Math.Max(existing.Stacks, initialStacks),
                _ => Math.Min(stackCap, existing.Stacks + initialStacks)
            };
        var mergedExpiry = CombatTraitRules.MergeBuffExpiry(existing?.ExpiresAtSeconds, proposedExpiry);
        target.SetBuff(grant with { Stacks = nextStacks, ExpiresAtSeconds = mergedExpiry });
        // 跨单位授 Buff 时同步唤醒施法者同相位后续决策的侧级 Buff 订阅，防止依赖队友状态的技能命中缓存。
        if (!ReferenceEquals(caster, target) && caster.SkillWakePending is { } casterPending)
        {
            var sideBuffWake = new SlotWakeMask { AllySide = UnitChannel.Buff, EnemySide = UnitChannel.Buff };
            for (var i = 0; i < casterPending.Length; i++)
                casterPending[i] = casterPending[i].Union(sideBuffWake);
        }
        // 层数由已损气血派生者，授予瞬间即按当前血线对齐（满血为 0 层）。
        target.SyncDerivedStacks();
        var recordedStacks = target.Buffs.FirstOrDefault(current => current.BuffId == buffId)?.Stacks ?? nextStacks;
        if (recordedStacks > 0)
            encounter?.ActionRecorder?.RecordBuff(caster, target, baseBuffId, recordedStacks);
    }

    /// <summary>
    /// 派生授予层数：<c>missingHitpointsPerStack</c> &gt; 0 时层数由目标已损气血与
    /// <paramref name="growth"/> 折算（每 N 点已损 1 层，满血 0 层）；否则
    /// <c>spellAttackStackPercent</c> 按「(基础层数 + 施法者术法攻击百分比) × 施法者治疗增幅向下取整（保底 1 层）」；
    /// 否则 <c>resistancePerStack</c> 按「基础层数 + 施法者双抗总和比例向下取整」；
    /// 均未配置时取 <c>baseStacks</c>（缺省 1.0）。
    /// </summary>
    /// <param name="growth">等级成长 × 技能增幅；已损气血阈值与层数百分比共用同一成长乘区。</param>
    private static double InitialStacks(JsonElement buff, CombatantState caster, CombatantState target,
        double growth)
    {
        var baseStacks = buff.TryGetProperty("baseStacks", out _) ? Math.Max(1, AbilityNumber(buff, "baseStacks")) : 1.0;
        var missingPerStack = AbilityNumber(buff, "missingHitpointsPerStack");
        if (missingPerStack > 0)
        {
            var missing = Math.Max(0, target.Stats.MaxHitpoints - target.Hitpoints);
            return Math.Floor(missing * growth / missingPerStack + 1e-9);
        }
        var spellAttackPercent = AbilityNumber(buff, "spellAttackStackPercent");
        if (spellAttackPercent > 0)
        {
            // 基础层数（缺省 1，山鬼谣为 5）+ 百分比额外层数；等级与技能增幅只提升百分比，不改变每层回血量；
            // 施法者实时治疗增幅（Linear）作为独立乘区放大总基础层数，严格向下取整并保底 1 层。
            // epsilon 吸收二进制浮点在整数边界上的下偏。
            var extraStacks = Math.Max(0, caster.Stats.SpellAttack) * spellAttackPercent * growth;
            var healingMult = PercentValue.Multiplier(caster.EffectiveStats.Effects.HealingBonusPoints, PercentCurveMode.Linear);
            var totalStacks = (baseStacks + extraStacks) * healingMult;
            return Math.Max(1, Math.Floor(totalStacks + 1e-9));
        }
        var resistancePerStack = AbilityNumber(buff, "resistancePerStack");
        if (resistancePerStack > 0)
        {
            // 层数锚定施法者「自身双抗总和」的原始面板语义，故读 Stats 而非 EffectiveStats：
            // EffectiveStats 会把该技能自身的碎罡破防折叠进来，语义上不再等于「自身双抗」。
            var resistance = caster.Stats.Defense + caster.Stats.MagicResistance;
            var ratio = AbilityNumber(buff, "resistanceStackRatio");
            return baseStacks + Math.Floor(resistance * ratio * growth / resistancePerStack + 1e-9);
        }
        return baseStacks;
    }

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
