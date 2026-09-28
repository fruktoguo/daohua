using System.Text.Json;
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
    public static CombatAdvanceResult AdvanceOneBoundary(CombatEncounterState encounter, double targetSeconds)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        ValidateTarget(encounter, targetSeconds);
        var from = encounter.ElapsedSeconds;
        if (encounter.Outcome != CombatOutcome.Running || from >= targetSeconds)
            return EmptyResult(encounter, from, targetSeconds);

        List<CombatEvent>? events = null;
        List<double>? playerDeaths = null;
        List<double>? playerRespawns = null;
        AdvanceBoundary(encounter, targetSeconds, ref events, ref playerDeaths, ref playerRespawns);
        return new CombatAdvanceResult(encounter.EncounterId, from, encounter.ElapsedSeconds, encounter.Outcome,
            (IReadOnlyList<CombatEvent>?)events ?? [], encounter.Sequence, playerDeaths, playerRespawns);
    }

    /// <summary>
    /// 将战斗状态按事件驱动推进至目标时刻：只跨过双方出手时刻，不产生等间隔时间片。
    /// </summary>
    /// <param name="encounter">遭遇战状态（就地推进）。</param>
    /// <param name="targetSeconds">目标战斗时钟秒数（不得早于当前已推进时间）。</param>
    /// <returns>本段推进的战斗帧：事件明细、终局裁决与序列号。</returns>
    public static CombatAdvanceResult AdvanceTo(CombatEncounterState encounter, double targetSeconds)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        ValidateTarget(encounter, targetSeconds);
        var from = encounter.ElapsedSeconds;
        List<CombatEvent>? events = null;
        List<double>? playerDeaths = null;
        List<double>? playerRespawns = null;
        var eventCount = 0;
        while (encounter.Outcome == CombatOutcome.Running && encounter.ElapsedSeconds < targetSeconds - 1e-12)
        {
            if (++eventCount > MaxEventsPerAdvance)
                throw new DomainException(DomainErrorCode.InvariantViolation, "战斗推进超过最大事件数。");
            if (!AdvanceBoundary(encounter, targetSeconds, ref events, ref playerDeaths, ref playerRespawns)) break;
        }
        return new CombatAdvanceResult(encounter.EncounterId, from, targetSeconds, encounter.Outcome,
            (IReadOnlyList<CombatEvent>?)events ?? [], encounter.Sequence, playerDeaths, playerRespawns);
    }

    private static void ValidateTarget(CombatEncounterState encounter, double targetSeconds)
    {
        if (!double.IsFinite(targetSeconds) || targetSeconds < encounter.ElapsedSeconds)
            throw new DomainException(DomainErrorCode.InvalidArgument, "战斗目标时间无效。");
    }

    private static CombatAdvanceResult EmptyResult(CombatEncounterState encounter, double from, double to)
        => new(encounter.EncounterId, from, to, encounter.Outcome, [], encounter.Sequence);

    private static bool AdvanceBoundary(CombatEncounterState encounter, double targetSeconds,
        ref List<CombatEvent>? events, ref List<double>? playerDeaths, ref List<double>? playerRespawns)
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
            ApplyBuffTick(encounter, effectAt);

        if (encounter.IsPlayerDowned)
        {
            if (encounter.PlayerRespawnAtSeconds <= encounter.ElapsedSeconds + 1e-12)
            {
                encounter.Player.Hitpoints = encounter.Player.Stats.MaxHitpoints;
                encounter.PlayerRespawnAtSeconds = double.PositiveInfinity;
                DecideInitialActions(encounter);
                (playerRespawns ??= []).Add(encounter.ElapsedSeconds);
            }
        }
        else
        {
            if (encounter.Player.IsAlive && encounter.Player.NextAttackAtSeconds <= encounter.ElapsedSeconds + 1e-12)
                PlayerAct(encounter, ref events);
            for (var i = 0; i < encounter.Enemies.Count; i++)
            {
                var enemy = encounter.Enemies[i];
                if (enemy.IsAlive && enemy.NextAttackAtSeconds <= encounter.ElapsedSeconds + 1e-12)
                    EnemyAct(encounter, enemy, i, ref events);
            }

            if (!encounter.Player.IsAlive && encounter.PlayerRespawnSeconds > 0)
            {
                encounter.PlayerRespawnAtSeconds = encounter.ElapsedSeconds + encounter.PlayerRespawnSeconds;
                InterruptCasts(encounter);
                (playerDeaths ??= []).Add(encounter.ElapsedSeconds);
            }
        }

        encounter.Sequence += 1;
        UpdateOutcome(encounter);
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

    /// <summary>团灭瞬间全场吟唱作废：未完成的吟唱视为未释放，退还起手预扣灵力且该槽不进入冷却。</summary>
    private static void InterruptCasts(CombatEncounterState encounter)
    {
        // 处理顺序与原 Prepend(player) 一致：先玩家后敌人。
        InterruptCast(encounter.Player);
        for (var i = 0; i < encounter.Enemies.Count; i++) InterruptCast(encounter.Enemies[i]);
        static void InterruptCast(CombatantState unit)
        {
            var pending = unit.PendingSkillIndex;
            unit.PendingSkillIndex = -1;
            if (pending < 0 || pending >= unit.Skills.Count) return;
            unit.Manapoints = Math.Min(unit.Stats.MaxManapoints,
                unit.Manapoints + AbilityNumber(unit.Skills[pending].Ability, "manaCost"));
            if (unit.SkillCooldownUntil is { } cooldowns && pending < cooldowns.Length)
                cooldowns[pending] = 0;
        }
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
    private static CombatantState? LowestHitpointsRatioAlly(IReadOnlyList<CombatantState> allies)
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

    /// <summary>技能出手的随机流侧位偏移：技能判定使用 side+偏移的独立流，与普攻（side 0/1+i）完全隔离。</summary>
    private const ulong SkillSideOffset = 0x1000;

    /// <summary>战内 Buff 周期回复的结算网格秒数。</summary>
    private const double BuffTickSeconds = 5;

    /// <summary>开战即决策各单位的首次出手动作：可放技能直接进入吟唱，否则按普攻周期蓄势。
    /// 已恢复吟唱中的单位（PendingSkillIndex ≥ 0）跳过决策，保留跨战延续的施放。</summary>
    internal static void DecideInitialActions(CombatEncounterState encounter)
    {
        // 复活起身重蓄势也走本入口：已阵亡单位不再排动作，尸体不得携带吟唱标记或预扣灵力。
        for (var i = 0; i < encounter.Enemies.Count; i++)
            if (encounter.Enemies[i].IsAlive && encounter.Enemies[i].PendingSkillIndex < 0)
                DecideNextAction(encounter, encounter.Enemies[i], encounter.Enemies, encounter.PlayerAllies, encounter.Player);
        if (encounter.Player.PendingSkillIndex < 0)
            DecideNextAction(encounter, encounter.Player, encounter.PlayerAllies, encounter.Enemies,
                SelectEnemyTarget(encounter, 0, 3));
    }

    /// <summary>玩家方出手边界：先结算待完成动作（普攻或吟唱完成的技能），再决策下一动作。</summary>
    private static void PlayerAct(CombatEncounterState encounter, ref List<CombatEvent>? events)
    {
        var player = encounter.Player;
        var allies = encounter.PlayerAllies;
        ResolvePending(encounter, player, allies, encounter.Enemies,
            SelectEnemyTarget(encounter, player.AttackCount, 3), 0, ref events);
        DecideNextAction(encounter, player, allies, encounter.Enemies,
            SelectEnemyTarget(encounter, player.AttackCount, 3));
    }


    /// <summary>敌方出手边界：与玩家同构，先结算待完成动作再决策下一动作。</summary>
    private static void EnemyAct(CombatEncounterState encounter, CombatantState enemy, int index, ref List<CombatEvent>? events)
    {
        ResolvePending(encounter, enemy, encounter.Enemies, encounter.PlayerAllies, encounter.Player, (ulong)(1 + index), ref events);
        DecideNextAction(encounter, enemy, encounter.Enemies, encounter.PlayerAllies, encounter.Player);
    }

    /// <summary>
    /// 结算出手边界上已决定的动作：吟唱中的技能在此刻结算（目标在结算时按威胁重新抽取），
    /// 否则结算普通攻击。技能走 side+SkillSideOffset 的独立随机流，与普攻流完全隔离。
    /// </summary>
    private static void ResolvePending(CombatEncounterState encounter, CombatantState unit,
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
    private static void DecideNextAction(CombatEncounterState encounter, CombatantState unit,
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

    /// <summary>最近的战内效果边界：全体单位 Buff 的最早到期时刻；存在回复类 Buff 时叠加 5 秒结算网格。</summary>
    private static double NextBuffBoundary(CombatEncounterState encounter)
    {
        var next = double.PositiveInfinity;
        var regenPending = false;
        // 扫描顺序与原 Prepend(player) 一致：先玩家后敌人；min 求值无平局歧义。
        Scan(encounter.Player);
        for (var i = 0; i < encounter.Enemies.Count; i++) Scan(encounter.Enemies[i]);
        if (regenPending)
            next = Math.Min(next, (Math.Floor(encounter.ElapsedSeconds / BuffTickSeconds) + 1) * BuffTickSeconds);
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

    /// <summary>战内效果边界结算：先剔除到期 Buff，再按 5 秒网格对存活单位结算周期回复。</summary>
    private static void ApplyBuffTick(CombatEncounterState encounter, double at)
    {
        encounter.Player.Buffs.RemoveAll(buff => buff.ExpiresAtSeconds <= at + 1e-9);
        for (var i = 0; i < encounter.Enemies.Count; i++)
            encounter.Enemies[i].Buffs.RemoveAll(buff => buff.ExpiresAtSeconds <= at + 1e-9);
        if (Math.Abs(at / BuffTickSeconds - Math.Round(at / BuffTickSeconds)) > 1e-9) return;
        // 结算顺序与原 Prepend(player) 一致：先玩家后敌人。
        Tick(encounter.Player);
        for (var i = 0; i < encounter.Enemies.Count; i++) Tick(encounter.Enemies[i]);

        static void Tick(CombatantState unit)
        {
            // 面板回复与战内 Buff 回复同口径：均为每 5 秒结算点一次。
            double hp = unit.Stats.HpRegen, mp = unit.Stats.MpRegen;
            var buffs = unit.Buffs;
            for (var i = 0; i < buffs.Count; i++)
            {
                hp += buffs[i].HpRegen * buffs[i].Stacks;
                mp += buffs[i].MpRegen * buffs[i].Stacks;
            }
            // 阵亡单位气血锁 0 不结算回血，灵力回复照常入账（死亡灵力运转规约战内同构）。
            if (unit.IsAlive && hp != 0) unit.Hitpoints = Math.Clamp(unit.Hitpoints + hp, 0, unit.Stats.MaxHitpoints);
            if (mp != 0) unit.Manapoints = Math.Clamp(unit.Manapoints + mp, 0, unit.Stats.MaxManapoints);
        }
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
                    AbilityNumber(buff, "threatPoints"));
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
        var attack = context.AttackStats;
        var defense = target.Stats;
        var contestRoll = Roll(encounter.Seed, side, context.Count, 0);
        var broken = attack.BreakPower > defense.ParryResolve
            && contestRoll < attack.BreakPower / (attack.BreakPower + defense.ParryResolve + 100);
        var parried = defense.ParryResolve > attack.BreakPower
            && contestRoll < defense.ParryResolve / (defense.ParryResolve + attack.BreakPower + 100);
        var accuracy = attack.Accuracy * (broken ? 2 : 1);
        var evasion = defense.Evasion * (parried ? 2 : 1);
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
        target.Hitpoints = Math.Max(0, target.Hitpoints - context.Damage);
        attacker.AttackCount += 1;
        attacker.Threat += context.Damage;
        events ??= new List<CombatEvent>();
        // 主事件先行占位，Aftermath（吸血/吸蓝/反伤）结算完成后回填双方灵力真值；
        // 衍生事件追加在主事件之后，保持战斗日志时序不变。
        var mainEventIndex = events.Count;
        events.Add(new CombatEvent(at, attacker.Id, target.Id, context.Damage, context.Hit, attack.DamageKind, target.Hitpoints)
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

    internal static string? AbilityText(JsonElement value, string key)
        => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    internal static double AbilityNumber(JsonElement value, string key)
        => value.TryGetProperty(key, out var property) && property.TryGetDouble(out var number) ? number : 0;

    private static CombatElement AbilityElement(JsonElement ability)
        => Enum.TryParse<CombatElement>(AbilityText(ability, "element") ?? "none", true, out var element) ? element : CombatElement.None;

    internal static double Roll(long seed, ulong side, double attackCount, ulong channel)
        => DeterministicRoll.Next(seed, side, attackCount, channel);

    /// <summary>
    /// 根据交战双方存活状态更新战局结果。
    /// </summary>
    private static void UpdateOutcome(CombatEncounterState encounter)
    {
        encounter.Outcome = (encounter.Player.IsAlive, AnyAlive(encounter.Enemies)) switch
        {
            // 原地复活待触发期间团灭不是终局：敌方残存或被持续伤害跳死全灭都保持 Running，起身后统一裁定。
            (false, _) when double.IsFinite(encounter.PlayerRespawnAtSeconds) => CombatOutcome.Running,
            (true, true) => CombatOutcome.Running,
            (true, false) => CombatOutcome.PlayerWon,
            (false, true) => CombatOutcome.EnemyWon,
            _ => CombatOutcome.Draw
        };
    }
}
