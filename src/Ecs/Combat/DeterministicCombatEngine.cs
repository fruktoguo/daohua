using System.Text.Json;
using DaoHua.Ecs.Combat;
using DaoHua.Ecs.Components;
using Domain.Common;
using Domain.Combat;
using Domain.GameData;

namespace DaoHua.Ecs;

/// <summary>
/// 无外部随机副作用的确定性战斗推进引擎。
/// 相同的初始属性与时间推进必得完全一致的伤害和事件序列，供客户端预测回放与服务端权威核验。
/// </summary>
public static class DeterministicCombatEngine
{
    /// <summary>单次推进最多产出的战斗事件上限，防止攻速异常引发死循环</summary>
    public const int MaxEventsPerAdvance = Systems.CombatEventAdvanceSystem.MaxEventsPerAdvance;

    /// <summary>无世界调用复用 ECS 遭遇创建与动作决策。</summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantStats player, CombatantStats enemy,
        double playerRespawnSeconds = 0)
        => Systems.CombatEventAdvanceSystem.Start(encounterId, seed, player, enemy, playerRespawnSeconds);

    /// <summary>无世界调用复用 ECS 遭遇创建与动作决策。</summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantStats player,
        IReadOnlyList<CombatantTemplate> enemies, double playerRespawnSeconds = 0)
        => Systems.CombatEventAdvanceSystem.Start(encounterId, seed, player, enemies, playerRespawnSeconds);

    /// <summary>无世界调用复用 ECS 遭遇创建与动作决策。</summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantTemplate player,
        IReadOnlyList<CombatantTemplate> enemies, double playerRespawnSeconds = 0)
        => Systems.CombatEventAdvanceSystem.Start(encounterId, seed, player, enemies, playerRespawnSeconds);

    /// <summary>无世界调用复用 ECS 遭遇创建与动作决策（包含多友方盟友）。</summary>
    public static CombatEncounterState StartAllied(string encounterId, long seed,
        IReadOnlyList<CombatantTemplate> allies, int selfIndex,
        IReadOnlyList<CombatantTemplate> enemies, double playerRespawnSeconds = 0)
        => Systems.CombatEventAdvanceSystem.StartAllied(encounterId, seed, allies, selfIndex, enemies, playerRespawnSeconds);

    /// <summary>无世界调用复用 ECS 自然边界查询。</summary>
    public static double NextBoundarySeconds(CombatEncounterState encounter, double targetSeconds)
        => Systems.CombatEventAdvanceSystem.NextBoundarySeconds(encounter, targetSeconds);

    /// <summary>无世界调用复用 ECS 单边界规则。</summary>
    public static CombatAdvanceResult AdvanceOneBoundary(CombatEncounterState encounter, double targetSeconds,
        ICombatPhaseDispatcher? dispatcher = null, CombatEncounterComponent? host = null)
        => Systems.CombatEventAdvanceSystem.AdvanceOneBoundary(encounter, targetSeconds, dispatcher, host);

    /// <summary>无世界调用复用 ECS 批量自然边界规则。</summary>
    public static CombatAdvanceResult AdvanceTo(CombatEncounterState encounter, double targetSeconds,
        ICombatPhaseDispatcher? dispatcher = null, CombatEncounterComponent? host = null)
        => Systems.CombatEventAdvanceSystem.AdvanceTo(encounter, targetSeconds, dispatcher, host);

    /// <summary>按存活敌人有效威胁权重抽取玩家攻击目标；全零威胁退化为等概率。</summary>
    /// <param name="encounter">遭遇战状态。</param>
    /// <param name="side">攻击方随机侧位（盟友按成员序固定分配，成员 0 沿用单人位 0）。</param>
    /// <param name="attackerCount">攻击方出手计数（确定性抽样的计数维度）。</param>
    /// <param name="channel">随机通道号（与其他判定隔离的抽样流）。</param>
    /// <returns>被选中的存活敌人；无存活敌人返回 null。</returns>
    public static CombatantState? SelectEnemyTarget(CombatEncounterState encounter, ulong side, double attackerCount, ulong channel)
        => SelectTarget(encounter.Enemies, encounter.Seed, side, attackerCount, channel);

    /// <summary>不可重复命中的多目标抽取：动态缩减概率池（Sequential Elimination）。</summary>
    /// <param name="encounter">遭遇战状态。</param>
    /// <param name="maxTargets">最多抽取目标数。</param>
    /// <param name="attackerCount">攻击方出手计数（确定性抽样的计数维度）。</param>
    /// <param name="channel">随机通道号；多目标逐位递增以隔离抽样流。</param>
    /// <returns>按抽取顺序排列的不重复存活敌人。</returns>
    public static IReadOnlyList<CombatantState> SelectEnemyTargets(CombatEncounterState encounter, ulong side, int maxTargets, double attackerCount, ulong channel)
        => SelectTargets(encounter.Enemies, maxTargets, encounter.Seed, side, attackerCount, channel);

    /// <summary>按有效威胁权重从任意存活目标列表抽取单个目标；全零威胁退化为等概率。</summary>
    /// <remarks>
    /// 免分配版：不物化存活列表，直接在 opponents 上按出现顺序扫描。
    /// 轮盘推进顺序、全零威胁均匀索引的候选序与原 Where().ToArray() 快照位级一致。
    /// </remarks>
    internal static CombatantState? SelectTarget(IReadOnlyList<CombatantState> opponents, long seed,
        ulong side, double count, ulong channel)
    {
        var aliveCount = 0;
        var total = 0d;
        CombatantState? first = null;
        for (var i = 0; i < opponents.Count; i++)
        {
            var unit = opponents[i];
            if (!unit.IsAlive) continue;
            first ??= unit;
            aliveCount++;
            total += unit.EffectiveThreat;
        }
        if (aliveCount == 0) return null;
        if (aliveCount == 1) return first;
        var roll = Roll(seed, side, count, channel);
        if (total <= 1e-12)
            return PickAliveAt(opponents, (int)Math.Min(aliveCount - 1, (long)(roll * aliveCount)));
        var ticket = roll * total;
        CombatantState? last = null;
        for (var i = 0; i < opponents.Count; i++)
        {
            var unit = opponents[i];
            if (!unit.IsAlive) continue;
            last = unit;
            if (ticket < unit.EffectiveThreat) return unit;
            ticket -= unit.EffectiveThreat;
        }
        // 兜底返回最后一个存活单位（原 alive[^1]）：浮点累减使 ticket 始终非负时可达。
        return last;
    }

    /// <summary>不可重复命中的多目标抽取：动态缩减概率池（Sequential Elimination）。</summary>
    /// <remarks>
    /// 免分配版：候选池不物化，每轮在 opponents 上跳过已命中单位（引用比较，与 List.Remove 等价）。
    /// 每轮 Roll 的 channel + result.Count 递增、轮盘顺序、全零威胁均匀索引与原版逐位一致。
    /// </remarks>
    internal static IReadOnlyList<CombatantState> SelectTargets(IReadOnlyList<CombatantState> opponents, int maxTargets,
        long seed, ulong side, double count, ulong channel)
    {
        var result = new List<CombatantState>(Math.Min(maxTargets, opponents.Count));
        while (result.Count < maxTargets)
        {
            var roll = Roll(seed, side, count, channel + (ulong)result.Count);
            var total = 0d;
            var aliveRemaining = 0;
            for (var i = 0; i < opponents.Count; i++)
            {
                var unit = opponents[i];
                if (!unit.IsAlive || IsAlreadyPicked(result, unit)) continue;
                aliveRemaining++;
                total += unit.EffectiveThreat;
            }
            if (aliveRemaining == 0) break;
            CombatantState? chosen;
            if (total <= 1e-12)
            {
                chosen = PickAliveAt(opponents, result,
                    (int)Math.Min(aliveRemaining - 1, (long)(roll * aliveRemaining)));
            }
            else
            {
                var ticket = roll * total;
                chosen = null;
                for (var i = 0; i < opponents.Count; i++)
                {
                    var unit = opponents[i];
                    if (!unit.IsAlive || IsAlreadyPicked(result, unit)) continue;
                    chosen = unit;
                    if (ticket < unit.EffectiveThreat) break;
                    ticket -= unit.EffectiveThreat;
                }
            }
            // aliveRemaining ≥ 1 保证 chosen 非空：均匀索引 wanted < aliveRemaining，轮盘至少记录首个候选。
            result.Add(chosen!);
        }
        return result;
    }

    /// <summary>血量比例最低的存活友方；与原 Where+OrderBy+Take(1) 一致：按出现顺序严格较小者胜出，
    /// 平局保留先见者，NaN 参与比较走 double.CompareTo 语义（与原排序键比较器一致）。</summary>
    internal static CombatantState? LowestHitpointsRatioAlly(IReadOnlyList<CombatantState> allies)
    {
        CombatantState? best = null;
        var bestRatio = double.PositiveInfinity;
        for (var i = 0; i < allies.Count; i++)
        {
            var unit = allies[i];
            if (!unit.IsAlive) continue;
            var ratio = unit.Hitpoints / unit.Stats.MaxHitpoints;
            if (best is null || ratio.CompareTo(bestRatio) < 0) { best = unit; bestRatio = ratio; }
        }
        return best;
    }

    /// <summary>是否存在存活单位：与原 Any(IsAlive) 同序短路；CombatEncounterState.Restore 也复用。</summary>
    internal static bool AnyAlive(IReadOnlyList<CombatantState> units)
    {
        for (var i = 0; i < units.Count; i++)
            if (units[i].IsAlive) return true;
        return false;
    }

    /// <summary>已命中集合查询：Sequential Elimination 候选数 ≤ 8，引用比较线性扫描无分配。</summary>
    private static bool IsAlreadyPicked(List<CombatantState> picked, CombatantState unit)
    {
        for (var i = 0; i < picked.Count; i++)
            if (ReferenceEquals(picked[i], unit)) return true;
        return false;
    }

    /// <summary>存活单位中第 wanted 个（出现顺序）；与物化数组下标语义一致。</summary>
    private static CombatantState? PickAliveAt(IReadOnlyList<CombatantState> opponents, int wanted)
        => PickAliveAt(opponents, null, wanted);

    /// <summary>在未命中单位中取第 wanted 个存活项；wanted 恒小于存活数，遍历必命中。</summary>
    private static CombatantState? PickAliveAt(IReadOnlyList<CombatantState> opponents,
        List<CombatantState>? picked, int wanted)
    {
        var seen = 0;
        for (var i = 0; i < opponents.Count; i++)
        {
            var unit = opponents[i];
            if (!unit.IsAlive || (picked is not null && IsAlreadyPicked(picked, unit))) continue;
            if (seen++ == wanted) return unit;
        }
        return null;
    }

    /// <summary>遭遇创建及恢复入场时共用出手 System 的动作决策。</summary>
    internal static void DecideInitialActions(CombatEncounterState encounter)
        => Systems.CombatActSystem.DecideInitialActions(encounter);


    /// <summary>纯命中与伤害解算：只填写本次命中结果，不变更单位资源、动作游标或事件队列。</summary>
    internal static void ResolveHit(CombatHitContext context)
    {
        var encounter = context.Encounter;
        var target = context.Target;
        var side = context.Side;
        var abilityId = context.AbilityId;
        var attack = context.AttackStats;
        var defense = target.Stats;
        // 战内 Buff 折叠：攻方攻击力/命中/暴击与守方双抗按层数实时生效，判定与结算同读一份面板。
        var attackEffective = context.Attacker.EffectiveStats;
        var defenseEffective = target.EffectiveStats;
        var contestRoll = Roll(encounter.Seed, side, context.Count, 0);
        var broken = attackEffective.BreakPower > defenseEffective.ParryResolve
            && contestRoll < attackEffective.BreakPower / (attackEffective.BreakPower + defenseEffective.ParryResolve + 100);
        var parried = defenseEffective.ParryResolve > attackEffective.BreakPower
            && contestRoll < defenseEffective.ParryResolve / (defenseEffective.ParryResolve + attackEffective.BreakPower + 100);
        var accuracy = attackEffective.Accuracy * (broken ? 2 : 1);
        var evasion = defenseEffective.Evasion * (parried ? 2 : 1);
        // 战斗经验对抗修正（总纲 §7.7.6）：作用于两侧等效输入，不占随机通道，不改暴击/破招判定与伤害链。
        (accuracy, evasion) = ApplyCombatExperienceContest(accuracy, evasion, attack.CombatExperience, defense.CombatExperience);
        var hit = Roll(encounter.Seed, side, context.Count, 1) >= evasion / (evasion + accuracy + 100);
        var criticalPoints = attackEffective.Critical * (broken ? 2 : 1);
        var critical = hit && Roll(encounter.Seed, side, context.Count, 2)
            < criticalPoints / (criticalPoints + defenseEffective.CriticalResistance + 100);
        var resistance = attack.DamageKind == CombatDamageKind.Physical ? defenseEffective.Defense : defenseEffective.MagicResistance;
        resistance *= parried ? 2 : 1;
        // 原始威力口径（总纲 §7.7.1）：普攻 = AttackPower ×(1+普攻增幅点数/100)；
        // 技能命中的 AttackPower 已在出手构造时并入技能增幅与倍率，此处不再二次乘算。
        // 技能命中沿用出手时构造的 AttackPower（已含倍率），仅叠加战内 Buff 的攻击力加成。
        var rawDamage = abilityId is null
            ? attackEffective.AttackPower * PercentValue.Multiplier(attack.BasicAttackBonusPoints, PercentCurveMode.Linear)
            : attack.AttackPower + context.Attacker.BuffAttackPower;
        context.RawDamage = rawDamage;
        // 第一层：大类抗性平方穿甲。等价于 RawDamage² / (RawDamage + RES × K + 1)，先除后乘避免平方中间值溢出。
        var damage = hit ? rawDamage * (rawDamage / (rawDamage + resistance * defenseEffective.ResistanceScale + 1)) : 0;
        // 第二层：攻方分类增伤（Linear 点数逐项乘算）：大类增伤 × 五行增伤 × 总增伤。
        damage *= PercentValue.Multiplier(
            attack.DamageKind == CombatDamageKind.Physical ? attack.PhysicalDamageBonusPoints : attack.SpellDamageBonusPoints,
            PercentCurveMode.Linear);
        if (attack.Element != CombatElement.None)
            damage *= PercentValue.Multiplier(attack.Element switch
            {
                CombatElement.Metal => attack.MetalDamageBonusPoints,
                CombatElement.Wood => attack.WoodDamageBonusPoints,
                CombatElement.Water => attack.WaterDamageBonusPoints,
                CombatElement.Fire => attack.FireDamageBonusPoints,
                CombatElement.Earth => attack.EarthDamageBonusPoints,
                _ => 0
            }, PercentCurveMode.Linear);
        // 总增伤 = 面板点数 + 战内 Buff 增伤点数（同 Linear 乘区合并，如破阵子战阵增益）。
        damage *= PercentValue.Multiplier(
            attack.TotalDamageBonusPoints + context.Attacker.BuffDamageBonusPoints, PercentCurveMode.Linear);
        // 第三层：守方分类减伤（NonLinear 点数逐项收敛）：大类减伤 × 对应五行减伤。
        damage *= PercentValue.Multiplier(
            attack.DamageKind == CombatDamageKind.Physical ? defense.PhysicalDamageReductionPoints : defense.SpellDamageReductionPoints,
            PercentCurveMode.NonLinear);
        if (attack.Element != CombatElement.None)
            damage *= PercentValue.Multiplier(attack.Element switch
            {
                CombatElement.Metal => defense.MetalDamageReductionPoints,
                CombatElement.Wood => defense.WoodDamageReductionPoints,
                CombatElement.Water => defense.WaterDamageReductionPoints,
                CombatElement.Fire => defense.FireDamageReductionPoints,
                CombatElement.Earth => defense.EarthDamageReductionPoints,
                _ => 0
            }, PercentCurveMode.NonLinear);
        // 暴击倍率 = 1 + 暴击伤害点数/100（Linear：基础 50 点即 150%）。
        if (critical) damage *= PercentValue.Multiplier(attack.CriticalDamagePoints, PercentCurveMode.Linear);
        // 末位收口：总减伤（免伤点数）NonLinear——面板、临时与战内 Buff 免伤同乘区合并。
        damage *= PercentValue.Multiplier(
            defense.DamageAbsorptionPoints + target.BonusAbsorptionPoints + target.BuffAbsorptionPoints,
            PercentCurveMode.NonLinear);
        // 第六层：固定伤害增减修正（总纲 §7.7.1：命中时末位固定点数直加直减，不吃抗性与百分比，最低截断为 0）。
        if (hit)
        {
            var fixedBonus = attack.FixedDamageBonus
                + (attack.DamageKind == CombatDamageKind.Physical ? attack.FixedPhysicalDamageBonus : attack.FixedSpellDamageBonus);
            var fixedReduction = defense.FixedDamageReduction
                + (attack.DamageKind == CombatDamageKind.Physical ? defense.FixedPhysicalDamageReduction : defense.FixedSpellDamageReduction);
            damage = Math.Max(0, damage + fixedBonus - fixedReduction);
        }
        context.Hit = hit;
        context.Critical = critical;
        context.Broken = broken;
        context.Parried = parried;
        context.Damage = damage;
        context.Damage = Math.Max(0, context.Damage);
    }

    /// <summary>
    /// 雷击符类打击伤害（总纲 §20.2）：无命中/破招/格挡/暴击掷点、不消耗随机通道；
    /// 固定走抗性平方穿透 → 攻方 Linear 增伤 → 守方 NonLinear 减伤 → 免伤收口。
    /// 调用方在构造打击属性时已把技能增幅并入 AttackPower（与技能命中同口径）。
    /// </summary>
    internal static double ResolveStrikeDamage(CombatantStats attack, CombatantState target)
    {
        var defense = target.EffectiveStats;
        var resistance = attack.DamageKind == CombatDamageKind.Physical ? defense.Defense : defense.MagicResistance;
        var rawDamage = attack.AttackPower;
        // 第一层：大类抗性平方穿甲（与 ResolveHit 同式）。
        var damage = rawDamage * (rawDamage / (rawDamage + resistance * defense.ResistanceScale + 1));
        // 第二层：攻方分类增伤（Linear）：大类 × 五行 × 总增伤 × 技能增幅。
        damage *= PercentValue.Multiplier(
            attack.DamageKind == CombatDamageKind.Physical ? attack.PhysicalDamageBonusPoints : attack.SpellDamageBonusPoints,
            PercentCurveMode.Linear);
        if (attack.Element != CombatElement.None)
            damage *= PercentValue.Multiplier(attack.Element switch
            {
                CombatElement.Metal => attack.MetalDamageBonusPoints,
                CombatElement.Wood => attack.WoodDamageBonusPoints,
                CombatElement.Water => attack.WaterDamageBonusPoints,
                CombatElement.Fire => attack.FireDamageBonusPoints,
                CombatElement.Earth => attack.EarthDamageBonusPoints,
                _ => 0
            }, PercentCurveMode.Linear);
        damage *= PercentValue.Multiplier(attack.TotalDamageBonusPoints, PercentCurveMode.Linear);
        // 第三层：守方分类减伤（NonLinear）：大类 × 对应五行。
        damage *= PercentValue.Multiplier(
            attack.DamageKind == CombatDamageKind.Physical ? defense.PhysicalDamageReductionPoints : defense.SpellDamageReductionPoints,
            PercentCurveMode.NonLinear);
        if (attack.Element != CombatElement.None)
            damage *= PercentValue.Multiplier(attack.Element switch
            {
                CombatElement.Metal => defense.MetalDamageReductionPoints,
                CombatElement.Wood => defense.WoodDamageReductionPoints,
                CombatElement.Water => defense.WaterDamageReductionPoints,
                CombatElement.Fire => defense.FireDamageReductionPoints,
                CombatElement.Earth => defense.EarthDamageReductionPoints,
                _ => 0
            }, PercentCurveMode.NonLinear);
        // 末位收口：总减伤（免伤点数）NonLinear。
        damage *= PercentValue.Multiplier(
            defense.DamageAbsorptionPoints + target.BonusAbsorptionPoints + target.BuffAbsorptionPoints,
            PercentCurveMode.NonLinear);
        // 第六层：固定伤害增减修正（总纲 §7.7.1）。
        var fixedBonus = attack.FixedDamageBonus
            + (attack.DamageKind == CombatDamageKind.Physical ? attack.FixedPhysicalDamageBonus : attack.FixedSpellDamageBonus);
        var fixedReduction = defense.FixedDamageReduction
            + (attack.DamageKind == CombatDamageKind.Physical ? defense.FixedPhysicalDamageReduction : defense.FixedSpellDamageReduction);
        return Math.Max(0, damage + fixedBonus - fixedReduction);
    }

    /// <summary>
    /// 战斗经验对抗修正（总纲 §7.7.6）：经验比值 r=clamp(aCE/max(bCE,ε), 0.1, 10)，
    /// 占优方等效按 /8 斜率增益封顶 ×2，劣势方等效按 /16 斜率衰减托底 ×0.5；
    /// 双方战斗经验均 ≤0 时中立不修正。只作用于命中/闪避等效输入，随机序列不变。
    /// </summary>
    public static (double Accuracy, double Evasion) ApplyCombatExperienceContest(
        double accuracy, double evasion, double attackerCombatExperience, double defenderCombatExperience)
    {
        if (attackerCombatExperience <= 0 && defenderCombatExperience <= 0) return (accuracy, evasion);
        var r = Math.Clamp(attackerCombatExperience / Math.Max(defenderCombatExperience, double.Epsilon), 0.1, 10);
        if (r >= 1)
            return (accuracy * Math.Min(2, 1 + (r - 1) / 8), evasion * Math.Max(0.5, 1 - (r - 1) / 16));
        var s = 1 / r;
        return (accuracy * Math.Max(0.5, 1 - (s - 1) / 16), evasion * Math.Min(2, 1 + (s - 1) / 8));
    }

    internal static string? AbilityText(JsonElement value, string key)
        => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    internal static double AbilityNumber(JsonElement value, string key)
        => value.TryGetProperty(key, out var property) && property.TryGetDouble(out var number) ? number : 0;

    internal static CombatElement AbilityElement(JsonElement ability)
        => Enum.TryParse<CombatElement>(AbilityText(ability, "element") ?? "none", true, out var element) ? element : CombatElement.None;

    internal static double Roll(long seed, ulong side, double attackCount, ulong channel)
        => DeterministicRoll.Next(seed, side, attackCount, channel);

}
