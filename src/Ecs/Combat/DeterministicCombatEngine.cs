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
    public const int MaxEventsPerAdvance = 100_000;

    /// <summary>开启一场玩家对单敌的遭遇战：纯创建后立即决策各单位首次出手。</summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantStats player, CombatantStats enemy,
        double playerRespawnSeconds = 0)
        => Start(encounterId, seed, new CombatantTemplate(player), [new CombatantTemplate(enemy)], playerRespawnSeconds);

    /// <summary>开启一场多敌方单位的战斗遭遇。</summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantStats player, IReadOnlyList<CombatantTemplate> enemies,
        double playerRespawnSeconds = 0)
        => Start(encounterId, seed, new CombatantTemplate(player), enemies, playerRespawnSeconds);

    /// <summary>开启一场玩家方携带技能模板的遭遇（斗法剧本双方镜像同构）。</summary>
    public static CombatEncounterState Start(string encounterId, long seed, CombatantTemplate player, IReadOnlyList<CombatantTemplate> enemies,
        double playerRespawnSeconds = 0)
    {
        var encounter = CombatEncounterState.Create(encounterId, seed, player, enemies, playerRespawnSeconds);
        // 开战即决策各单位首次出手：可放技能直接进入吟唱，否则按普攻周期蓄势。
        DecideInitialActions(encounter);
        return encounter;
    }

    /// <summary>
    /// 查询下一确定性战斗边界。返回值不会早于当前战斗时钟，也不会晚于 targetSeconds。
    /// ECS 宿主可用它把一帧拆成离散边界；现行 OOP 调用仍应使用 <see cref="AdvanceTo"/>。
    /// </summary>
    public static double NextBoundarySeconds(CombatEncounterState encounter, double targetSeconds)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        ValidateTarget(encounter, targetSeconds);
        if (encounter.Outcome != CombatOutcome.Running || encounter.ElapsedSeconds >= targetSeconds)
            return encounter.ElapsedSeconds;

        var next = encounter.IsPlayerDowned ? encounter.PlayerRespawnAtSeconds : encounter.Player.NextAttackAtSeconds;
        if (!encounter.IsPlayerDowned)
        {
            for (var i = 0; i < encounter.Enemies.Count; i++)
            {
                var enemy = encounter.Enemies[i];
                if (enemy.IsAlive && enemy.NextAttackAtSeconds < next) next = enemy.NextAttackAtSeconds;
            }
        }

        var effectAt = NextBuffBoundary(encounter);
        if (effectAt < next) next = effectAt;
        if (!double.IsFinite(next))
            throw new DomainException(DomainErrorCode.InvariantViolation, "战斗攻击时间无效。");
        return Math.Min(next, targetSeconds);
    }

    /// <summary>
    /// 只推进一个确定性战斗边界：Buff 到期/回复、复活或一个时刻内双方按既有顺序的出手。
    /// 不复制伤害公式，也不改变随机流；返回结果的事件列表只包含本边界产生的事件。
    /// 当目标时间早于下一边界时只推进战斗时钟，不产生事件。
    /// </summary>
    public static CombatAdvanceResult AdvanceOneBoundary(CombatEncounterState encounter, double targetSeconds,
        ICombatPhaseDispatcher? dispatcher = null, CombatEncounterComponent? host = null)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        ValidateTarget(encounter, targetSeconds);
        var from = encounter.ElapsedSeconds;
        if (encounter.Outcome != CombatOutcome.Running || from >= targetSeconds)
            return EmptyResult(encounter, from, targetSeconds);

        var context = new CombatPhaseContext { Encounter = encounter };
        AdvanceBoundary(encounter, targetSeconds, dispatcher ?? InlineCombatPhaseDispatcher.Instance, host, context);
        return new CombatAdvanceResult(encounter.EncounterId, from, encounter.ElapsedSeconds, encounter.Outcome,
            (IReadOnlyList<CombatEvent>?)context.Events ?? [], encounter.Sequence, context.PlayerDeaths, context.PlayerRespawns);
    }

    /// <summary>
    /// 将战斗状态按事件驱动推进至目标时刻：只跨过双方出手时刻，不产生等间隔时间片。
    /// </summary>
    /// <param name="encounter">遭遇战状态（就地推进）。</param>
    /// <param name="targetSeconds">目标战斗时钟秒数（不得早于当前已推进时间）。</param>
    /// <returns>本段推进的战斗帧：事件明细、终局裁决与序列号。</returns>
    public static CombatAdvanceResult AdvanceTo(CombatEncounterState encounter, double targetSeconds,
        ICombatPhaseDispatcher? dispatcher = null, CombatEncounterComponent? host = null)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        ValidateTarget(encounter, targetSeconds);
        var from = encounter.ElapsedSeconds;
        dispatcher ??= InlineCombatPhaseDispatcher.Instance;
        var context = new CombatPhaseContext { Encounter = encounter };
        var eventCount = 0;
        while (encounter.Outcome == CombatOutcome.Running && encounter.ElapsedSeconds < targetSeconds - 1e-12)
        {
            if (++eventCount > MaxEventsPerAdvance)
                throw new DomainException(DomainErrorCode.InvariantViolation, "战斗推进超过最大事件数。");
            if (!AdvanceBoundary(encounter, targetSeconds, dispatcher, host, context)) break;
        }
        return new CombatAdvanceResult(encounter.EncounterId, from, targetSeconds, encounter.Outcome,
            (IReadOnlyList<CombatEvent>?)context.Events ?? [], encounter.Sequence, context.PlayerDeaths, context.PlayerRespawns);
    }

    private static void ValidateTarget(CombatEncounterState encounter, double targetSeconds)
    {
        if (!double.IsFinite(targetSeconds) || targetSeconds < encounter.ElapsedSeconds)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗目标时间无效。");
    }

    private static CombatAdvanceResult EmptyResult(CombatEncounterState encounter, double from, double to)
        => new(encounter.EncounterId, from, to, encounter.Outcome, [], encounter.Sequence);

    private static bool AdvanceBoundary(CombatEncounterState encounter, double targetSeconds,
        ICombatPhaseDispatcher dispatcher, CombatEncounterComponent? host, CombatPhaseContext context)
    {
        using var measurement = RuntimeInstrumentation.MeasureCurrent("domain.combat.event");
        var naturalBoundary = GetNaturalBoundarySeconds(encounter, out var effectAt);
        if (naturalBoundary > targetSeconds)
        {
            encounter.ElapsedSeconds = targetSeconds;
            return false;
        }

        // 与原 AdvanceTo 完全一致：即使浮点差值小于容差，也必须写入边界原值，
        // 否则 5 秒回复网格在 9.999999999999991 等表示下会重复命中同一边界。
        encounter.ElapsedSeconds = Math.Max(encounter.ElapsedSeconds, naturalBoundary);

        if (effectAt <= encounter.ElapsedSeconds + 1e-12)
        {
            context.EffectAt = effectAt;
            dispatcher.Dispatch<ICombatBuffTickPhase>(host, context);
        }

        if (encounter.IsPlayerDowned)
        {
            if (encounter.PlayerRespawnAtSeconds <= encounter.ElapsedSeconds + 1e-12)
                dispatcher.Dispatch<ICombatPlayerRespawnPhase>(host, context);
        }
        else
        {
            if (encounter.Player.IsAlive && encounter.Player.NextAttackAtSeconds <= encounter.ElapsedSeconds + 1e-12)
                dispatcher.Dispatch<ICombatPlayerActPhase>(host, context);
            for (var i = 0; i < encounter.Enemies.Count; i++)
            {
                var enemy = encounter.Enemies[i];
                if (enemy.IsAlive && enemy.NextAttackAtSeconds <= encounter.ElapsedSeconds + 1e-12)
                {
                    context.EnemyIndex = i;
                    context.Enemy = enemy;
                    dispatcher.Dispatch<ICombatEnemyActPhase>(host, context);
                }
            }

            if (!encounter.Player.IsAlive && encounter.PlayerRespawnSeconds > 0)
                dispatcher.Dispatch<ICombatPlayerDownedPhase>(host, context);
        }

        encounter.Sequence += 1;
        dispatcher.Dispatch<ICombatOutcomePhase>(host, context);
        return true;
    }

    private static double GetNaturalBoundarySeconds(CombatEncounterState encounter, out double effectAt)
    {
        var next = encounter.IsPlayerDowned ? encounter.PlayerRespawnAtSeconds : encounter.Player.NextAttackAtSeconds;
        if (!encounter.IsPlayerDowned)
        {
            for (var i = 0; i < encounter.Enemies.Count; i++)
            {
                var enemy = encounter.Enemies[i];
                if (enemy.IsAlive && enemy.NextAttackAtSeconds < next) next = enemy.NextAttackAtSeconds;
            }
        }

        effectAt = NextBuffBoundary(encounter);
        if (effectAt < next) next = effectAt;
        if (!double.IsFinite(next))
            throw new DomainException(DomainErrorCode.InvariantViolation, "战斗攻击时间无效。");
        return next;
    }

    /// <summary>按存活敌人有效威胁权重抽取玩家攻击目标；全零威胁退化为等概率。</summary>
    /// <param name="encounter">遭遇战状态。</param>
    /// <param name="attackerCount">攻击方出手计数（确定性抽样的计数维度）。</param>
    /// <param name="channel">随机通道号（与其他判定隔离的抽样流）。</param>
    /// <returns>被选中的存活敌人；无存活敌人返回 null。</returns>
    public static CombatantState? SelectEnemyTarget(CombatEncounterState encounter, double attackerCount, ulong channel)
        => SelectTarget(encounter.Enemies, encounter.Seed, 0, attackerCount, channel);

    /// <summary>不可重复命中的多目标抽取：动态缩减概率池（Sequential Elimination）。</summary>
    /// <param name="encounter">遭遇战状态。</param>
    /// <param name="maxTargets">最多抽取目标数。</param>
    /// <param name="attackerCount">攻击方出手计数（确定性抽样的计数维度）。</param>
    /// <param name="channel">随机通道号；多目标逐位递增以隔离抽样流。</param>
    /// <returns>按抽取顺序排列的不重复存活敌人。</returns>
    public static IReadOnlyList<CombatantState> SelectEnemyTargets(CombatEncounterState encounter, int maxTargets, double attackerCount, ulong channel)
        => SelectTargets(encounter.Enemies, maxTargets, encounter.Seed, 0, attackerCount, channel);

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

    /// <summary>最近的战内效果边界：全体单位 Buff 的最早到期时刻；存在回复类 Buff 时叠加 5 秒结算网格。</summary>
    private static double NextBuffBoundary(CombatEncounterState encounter)
    {
        var next = double.PositiveInfinity;
        var regenPending = false;
        // 扫描顺序与原 Prepend(player) 一致：先玩家后敌人；min 求值无平局歧义。
        Scan(encounter.Player);
        for (var i = 0; i < encounter.Enemies.Count; i++) Scan(encounter.Enemies[i]);
        if (regenPending)
            next = Math.Min(next, (Math.Floor(encounter.ElapsedSeconds / Systems.CombatBuffTickSystem.TickSeconds) + 1) * Systems.CombatBuffTickSystem.TickSeconds);
        return next;

        void Scan(CombatantState unit)
        {
            if (unit.Stats.HpRegen != 0 || unit.Stats.MpRegen != 0) regenPending = true;
            var buffs = unit.Buffs;
            for (var i = 0; i < buffs.Count; i++)
            {
                if (buffs[i].ExpiresAtSeconds < next) next = buffs[i].ExpiresAtSeconds;
                if (buffs[i].HpRegen != 0 || buffs[i].MpRegen != 0) regenPending = true;
            }
        }
    }

    /// <summary>纯命中与伤害解算：只填写本次命中结果，不变更单位资源、动作游标或事件队列。</summary>
    internal static void ResolveHit(CombatHitContext context)
    {
        var encounter = context.Encounter;
        var target = context.Target;
        var side = context.Side;
        var abilityId = context.AbilityId;
        var attack = context.AttackStats;
        var defense = target.Stats;
        var contestRoll = Roll(encounter.Seed, side, context.Count, 0);
        var broken = attack.BreakPower > defense.ParryResolve
            && contestRoll < attack.BreakPower / (attack.BreakPower + defense.ParryResolve + 100);
        var parried = defense.ParryResolve > attack.BreakPower
            && contestRoll < defense.ParryResolve / (defense.ParryResolve + attack.BreakPower + 100);
        var accuracy = attack.Accuracy * (broken ? 2 : 1);
        var evasion = defense.Evasion * (parried ? 2 : 1);
        // 战斗经验对抗修正（总纲 §7.7.6）：作用于两侧等效输入，不占随机通道，不改暴击/破招判定与伤害链。
        (accuracy, evasion) = ApplyCombatExperienceContest(accuracy, evasion, attack.CombatExperience, defense.CombatExperience);
        var hit = Roll(encounter.Seed, side, context.Count, 1) >= evasion / (evasion + accuracy + 100);
        var criticalPoints = attack.Critical * (broken ? 2 : 1);
        var critical = hit && Roll(encounter.Seed, side, context.Count, 2)
            < criticalPoints / (criticalPoints + defense.CriticalResistance + 100);
        var resistance = attack.DamageKind == CombatDamageKind.Physical ? defense.Defense : defense.MagicResistance;
        resistance *= parried ? 2 : 1;
        var rawDamage = attack.AttackPower * attack.BasicAttackRatio;
        // 第一层：大类抗性平方穿甲。等价于 RawDamage² / (RawDamage + RES × K + 1)，先除后乘避免平方中间值溢出。
        var damage = hit ? rawDamage * (rawDamage / (rawDamage + resistance * defense.ResistanceScale + 1)) : 0;
        // 第二层：攻方分类增伤（Linear 点数逐项乘算）：大类增伤 × 五行增伤 × 总增伤 × 普攻/技能增幅。
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
        // 普攻/技能增幅互斥取一：abilityId 为空即普攻（「普通攻击」tag 机制待配表落地）。
        damage *= PercentValue.Multiplier(
            abilityId is null ? attack.BasicAttackBonusPoints : attack.SkillBonusPoints,
            PercentCurveMode.Linear);
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
        if (critical) damage *= attack.CriticalMultiplier;
        // 末位收口：总减伤（免伤点数）NonLinear——面板、临时与战内 Buff 免伤同乘区合并。
        damage *= PercentValue.Multiplier(
            defense.DamageAbsorptionPoints + target.BonusAbsorptionPoints + target.BuffAbsorptionPoints,
            PercentCurveMode.NonLinear);
        context.Hit = hit;
        context.Critical = critical;
        context.Broken = broken;
        context.Parried = parried;
        context.Damage = damage;
        context.Damage = Math.Max(0, context.Damage);
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
