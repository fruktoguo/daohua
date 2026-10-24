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

    /// <summary>
    /// 盟友随机侧位：成员 0 沿用单人位 0（单人路径位级不变），其余成员高位偏移，
    /// 只依赖共享剧本成员下标——与成员所在分片/在线状态无关，各端重放逐位一致。
    /// </summary>
    internal static ulong AllySide(int memberIndex) => memberIndex == 0 ? 0UL : AllySideBase + (ulong)memberIndex;

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
        // 盟友按成员序逐个决策：目标抽样沿用计数 0/通道 3 口径，成员 0 与单人逐位一致。
        for (var i = 0; i < encounter.PlayerAllies.Count; i++)
            if (encounter.PlayerAllies[i].IsAlive && encounter.PlayerAllies[i].PendingSkillIndex < 0)
                DecideNextAction(encounter, encounter.PlayerAllies[i], encounter.PlayerAllies, encounter.Enemies,
                    SelectEnemyTarget(encounter, AllySide(i), 0, 3));
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
    /// 灵力消耗在起手时判定并预扣；吟唱占用整条动作时间线，时长 = castMilliseconds ÷(1+行动速度点数/100)，
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
                    && !SkillTriggerEvaluator.MatchesAll(triggers, unit, target, allies, opponents, encounter.ElapsedSeconds))
                    continue;
                var manaCost = AbilityNumber(slot.Ability, "manaCost");
                if (unit.Manapoints < manaCost) continue;
                unit.Manapoints -= manaCost;
                // 吟唱时长 = 配表 ÷ 行动速度除数（NonLinear 点数，境界成长已注入本通道）。
                var castSeconds = Math.Max(1e-9,
                    AbilityNumber(slot.Ability, "castMilliseconds") / 1000d
                    * PercentValue.Multiplier(unit.Stats.ActionSpeedPoints, PercentCurveMode.NonLinear));
                unit.PendingSkillIndex = i;
                unit.NextAttackAtSeconds = encounter.ElapsedSeconds + castSeconds;
                // 冷却自吟唱完成起算：配表 ÷ 冷却缩减除数（NonLinear 点数，境界成长已注入本通道）。
                cooldowns[i] = unit.NextAttackAtSeconds
                    + AbilityNumber(slot.Ability, "cooldownMilliseconds") / 1000d
                    * PercentValue.Multiplier(unit.Stats.CooldownReductionPoints, PercentCurveMode.NonLinear);
                return;
            }
        }
        unit.PendingSkillIndex = -1;
        // 普攻读条双重除数：攻击速度（面板+战内 Buff 合并）× 行动速度（NonLinear 点数）。
        unit.NextAttackAtSeconds = encounter.ElapsedSeconds
            + unit.Stats.AttackIntervalSeconds
            * PercentValue.Multiplier(unit.Stats.AttackSpeedPoints + unit.BuffTotal(BuffEffectChannel.AttackSpeedPoints), PercentCurveMode.NonLinear)
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
        var levelGrowth = CombatGrowthCurve.AbilityLevelGrowth(slot.Level);
        var targetType = AbilityText(ability, "targetType");
        // 威胁类字段是施放方修正，与技能效果类型无关：任何施放都可携带仇恨增益。
        // 等级成长与伤害同式（总纲 §5.2）：全部数值项同吃每级 +2% 线性成长。
        caster.Threat += AbilityNumber(ability, "threatGain") * levelGrowth;
        caster.ThreatBonusPoints += AbilityNumber(ability, "threatPoints") * levelGrowth;
        // 治疗量吃技能增幅点数（Linear，含等阶成长注入），与技能伤害同通道。
        var healOutput = attackBase * PercentValue.Multiplier(caster.Stats.SkillBonusPoints, PercentCurveMode.Linear)
            * AbilityNumber(ability, "healingRatio") * levelGrowth
            + AbilityNumber(ability, "flatHealing") * levelGrowth;
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
                // 治疗按施放方归属计入「其他治疗」输出。
                if (healed > 0) encounter.StatsBook?.RecordHealing(caster, autoRegen: false, healed);
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
            // 配表支持 buffs[] 多 Buff（如碎罡同时授予碎罡与罡锋），单 Buff 的旧 buff 对象仍兼容解析。
            var buffSpecs = AbilityBuffSpecs(ability);
            if (buffSpecs.Count > 0)
            {
                // 目标范围与治疗效果同口径展开：lowestHitpointsAlly 取血量比例最低存活友方
                // （原 OrderBy+Take(1) 稳定首元素语义），allAllies 顺序遍历存活友方，其余作用于自身。
                if (targetType == "lowestHitpointsAlly")
                {
                    if (LowestHitpointsRatioAlly(allies) is { } weakest)
                        foreach (var spec in buffSpecs) GrantBuff(spec, caster, weakest, encounter.ElapsedSeconds, levelGrowth, id);
                }
                else if (targetType == "allAllies")
                {
                    for (var i = 0; i < allies.Count; i++)
                        if (allies[i].IsAlive)
                            foreach (var spec in buffSpecs) GrantBuff(spec, caster, allies[i], encounter.ElapsedSeconds, levelGrowth, id);
                }
                else
                {
                    foreach (var spec in buffSpecs) GrantBuff(spec, caster, caster, encounter.ElapsedSeconds, levelGrowth, id);
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
        // 技能单发威力 = 攻基 ×(1+技能增幅/100) × 技能倍率 + 固定值——技能增幅并入原始威力乘位，
        // 与普攻增幅在普攻威力上的口径一致，先增减后抗穿（总纲 §7.7.1 原始伤害口径）。
        var stats = caster.Stats with
        {
            AttackPower = attackBase * PercentValue.Multiplier(caster.Stats.SkillBonusPoints, PercentCurveMode.Linear)
                * AbilityNumber(ability, "damageRatio") * levelGrowth
                + AbilityNumber(ability, "flatDamage") * levelGrowth,
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
        if (single is null) return;
        ResolveAttack(encounter, caster, single, side, encounter.ElapsedSeconds, ref events, stats, count, id);
        // 多段打击（hitCount>1，连环等）：对同一目标逐段独立结算完整技能伤害——后续段沿用
        // 普攻计数随机流（countOverride=null → 取 attacker.AttackCount 自增序列），各段命中/
        // 暴击独立掷点；目标中途死亡即终止剩余连段，不转火不空掷。
        var hitCount = Math.Max(1, (int)AbilityNumber(ability, "hitCount"));
        for (var hitIndex = 1; hitIndex < hitCount && single.IsAlive; hitIndex++)
            ResolveAttack(encounter, caster, single, side, encounter.ElapsedSeconds, ref events, stats, null, id);
        // 击杀连锁（chainOnKillCount）：目标阵亡后按威胁轮盘抽另一存活敌方再结算一次完整技能
        // 伤害，至多追加 chainOnKillCount 次；连锁未致死即终止。被击杀目标已离存活池，
        // 连锁命中未死者时循环结束，故天然不重复命中、总目标数 ≤ 1 + chainOnKillCount。
        // 连锁帧沿用同一次施放的抽样计数，命中/暴击掷点继承本次出手运气（贯穿语义）。
        var chainMax = (int)AbilityNumber(ability, "chainOnKillCount");
        for (var chainIndex = 0; chainIndex < chainMax && !single.IsAlive; chainIndex++)
        {
            single = SelectTarget(opponents, encounter.Seed, side, count, 20 + (ulong)chainIndex);
            if (single is null) break;
            ResolveAttack(encounter, caster, single, side, encounter.ElapsedSeconds, ref events, stats, count, id);
        }
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
        // 行动级战斗统计：攻方按伤害类别计输出、守方计实收与减免（减免 = 原始威力 − 实收，未命中不落账）。
        encounter.StatsBook?.RecordHit(attacker, target, context.AttackStats.DamageKind,
            context.Damage, context.Hit ? Math.Max(0, context.RawDamage - context.Damage) : 0);
        target.Hitpoints = Math.Max(0, target.Hitpoints - context.Damage);
        // 气血变动后重算「每损失 N 点生命获得 1 层」类 Buff 的层数。
        target.SyncDerivedStacks();
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
    /// 解析技能授予的战内 Buff 规格列表：优先 <c>buffs[]</c> 多 Buff，回落单 <c>buff</c> 对象。
    /// 单 Buff 回落使既有技能配表无需改动即可继续工作。
    /// </summary>
    private static List<JsonElement> AbilityBuffSpecs(JsonElement ability)
    {
        if (ability.TryGetProperty("buffs", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            var specs = new List<JsonElement>(array.GetArrayLength());
            foreach (var entry in array.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object) specs.Add(entry);
            return specs;
        }
        return ability.TryGetProperty("buff", out var single) && single.ValueKind == JsonValueKind.Object
            ? [single]
            : [];
    }

    /// <summary>
    /// 授予单条 Buff 规格：层数由施法者属性（法攻百分比 / 双抗总和按比例）与目标状态
    /// （每损失 N 点气血 1 层）派生。层数承载数值——Buff 只存每层基础量，结算时统一按层数线性放大。
    /// </summary>
    private static void GrantBuff(JsonElement buff, CombatantState caster, CombatantState target,
        double grantedAtSeconds, double levelGrowth, string abilityId)
    {
        var baseBuffId = AbilityText(buff, "buffId") ?? $"ability-buff:{abilityId}";
        var policy = AbilityText(buff, "stackPolicy") ?? "capped";
        var buffId = policy == "perCaster" ? $"{baseBuffId}:{caster.Id}" : baseBuffId;
        var missingPerStack = AbilityNumber(buff, "missingHitpointsPerStack");
        var spellStackPercent = AbilityNumber(buff, "spellAttackStackPercent");
        var resistancePerStack = AbilityNumber(buff, "resistancePerStack");
        var hasStackDerivation = missingPerStack > 0 || spellStackPercent > 0 || resistancePerStack > 0;
        // 层数派生通道把等级成长与技能增幅折入层数，效果量保持每层基础值，防止等级被二次放大；
        // 无层数派生的 Buff 仍按等级成长放大全部数值项。时长/叠层上限不成长。
        var growth = levelGrowth * PercentValue.Multiplier(caster.Stats.SkillBonusPoints, PercentCurveMode.Linear);
        var effectGrowth = hasStackDerivation ? 1.0 : levelGrowth;
        var initialStacks = InitialStacks(buff, caster, target, growth);
        // 数值效果键按注册表解析（GrowsWithLevel 通道自动乘成长，双抗破防比率豁免）；生命周期键保持直读。
        var grant = new CombatBuffState(buffId,
            grantedAtSeconds + Math.Max(0, AbilityNumber(buff, "durationSeconds")), initialStacks,
            AbilityText(buff, "name") ?? baseBuffId,
            ResetOnWave: buff.TryGetProperty("resetOnWave", out var resetFlag) && resetFlag.ValueKind == JsonValueKind.True,
            // 已损气血通道存储「折入成长后的等效每层阈值」，动态重算才能沿用同一成长口径。
            MissingHitpointsPerStack: missingPerStack > 0 ? missingPerStack / growth : 0,
            Effects: BuffEffectParser.Parse(buff, effectGrowth));
        // maxStacks 缺省为 1：同 ID 重复授予刷新时长并叠层至上限（受控入口自动失效聚合缓存）。
        var stackCap = Math.Max(1, AbilityNumber(buff, "maxStacks"));
        var existing = target.Buffs.FirstOrDefault(current => current.BuffId == buffId);
        var nextStacks = existing is null ? initialStacks
            : policy switch
            {
                "maxKeep" => Math.Max(existing.Stacks, initialStacks),
                _ => Math.Min(stackCap, existing.Stacks + initialStacks)
            };
        target.SetBuff(grant with { Stacks = nextStacks });
        // 层数由已损气血派生者，授予瞬间即按当前血线对齐（满血为 0 层）。
        target.SyncDerivedStacks();
    }

    /// <summary>
    /// 派生授予层数：<c>missingHitpointsPerStack</c> &gt; 0 时层数由目标已损气血与
    /// <paramref name="growth"/> 折算（每 N 点已损 1 层，满血 0 层）；否则
    /// <c>spellAttackStackPercent</c> 按「基础 1 层 + 施法者术法攻击百分比向下取整」；
    /// 否则 <c>resistancePerStack</c> 按「基础 1 层 + 施法者双抗总和比例向下取整」；
    /// 均未配置时为基础 1 层。
    /// </summary>
    /// <param name="growth">等级成长 × 技能增幅；已损气血阈值与层数百分比共用同一成长乘区。</param>
    private static double InitialStacks(JsonElement buff, CombatantState caster, CombatantState target,
        double growth)
    {
        var missingPerStack = AbilityNumber(buff, "missingHitpointsPerStack");
        if (missingPerStack > 0)
        {
            var missing = Math.Max(0, target.Stats.MaxHitpoints - target.Hitpoints);
            return Math.Floor(missing * growth / missingPerStack + 1e-9);
        }
        var spellAttackPercent = AbilityNumber(buff, "spellAttackStackPercent");
        if (spellAttackPercent > 0)
        {
            // 基础 1 层 + 百分比额外层数；等级与技能增幅只提升百分比，不改变每层回血量。
            // epsilon 吸收二进制浮点在整数边界上的下偏。
            var extraStacks = Math.Max(0, caster.Stats.SpellAttack) * spellAttackPercent * growth;
            return 1 + Math.Floor(extraStacks + 1e-9);
        }
        var resistancePerStack = AbilityNumber(buff, "resistancePerStack");
        if (resistancePerStack > 0)
        {
            // 层数锚定施法者「自身双抗总和」的原始面板语义，故读 Stats 而非 EffectiveStats：
            // EffectiveStats 会把该技能自身的碎罡破防折叠进来，语义上不再等于「自身双抗」。
            var resistance = caster.Stats.Defense + caster.Stats.MagicResistance;
            var ratio = AbilityNumber(buff, "resistanceStackRatio");
            return 1 + Math.Floor(resistance * ratio * growth / resistancePerStack + 1e-9);
        }
        return 1.0;
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
