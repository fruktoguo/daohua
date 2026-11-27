using Domain.Combat;

namespace DaoHua.Ecs;

/// <summary>
/// 对称战斗（<see cref="CombatEncounterState.IsSymmetric"/>）的同刻同时生效波次缓冲区：
/// 当同一时间戳 <c>t</c> 存在 ≥2 名到点出手的存活单位时，
/// 阶段 1 基于波初冻结快照完成全部命中/伤害/反伤/阵盘反击/特质/治疗/Buff 求值，
/// 阶段 2 在波末按「统一落伤（遵循欲速则不达留一）→ 仍存活者结算治疗与吸血 → 灵力/威胁/Buff 与派生层数 → 刷新存活人数特质 → 回填尾帧资源」
/// 原子提交，确保同刻互杀判平且无任何先后手或满血吸血顺序偏向。
/// </summary>
internal sealed class SimultaneousCombatWave
{
    private readonly record struct PendingDamageEntry(
        CombatantState Attacker,
        CombatantState Target,
        double Damage,
        bool AccumulateXuyi,
        bool BypassShield,
        object? RecorderActionHandle);

    private readonly record struct PendingHealEntry(
        CombatantState Source,
        CombatantState Target,
        DuelRecordEffectKind RecordKind,
        double Amount,
        string? DefId,
        int CombatEventIndex,
        object? RecorderActionHandle);

    private readonly Dictionary<CombatantState, double> _localVirtualHp = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CombatantState, double> _localVirtualShield = new(ReferenceEqualityComparer.Instance);
    private readonly List<PendingDamageEntry> _damages = [];
    private readonly List<PendingHealEntry> _heals = [];
    private readonly Dictionary<CombatantState, double> _manaDeltas = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CombatantState, double> _shieldGains = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<CombatantState> _shieldConsumed = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CombatantState, double> _threatDeltas = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CombatantState, double> _threatBonusDeltas = new(ReferenceEqualityComparer.Instance);
    private readonly List<Action> _buffOps = [];
    private readonly Dictionary<CombatantState, double> _pendingRanhunStacks = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<CombatantState> _xuyiConsumed = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<CombatantState> _downedRecorded = new(ReferenceEqualityComparer.Instance);
    private readonly int _eventsStartIndex;

    public SimultaneousCombatWave(int eventsStartIndex)
    {
        _eventsStartIndex = eventsStartIndex;
    }

    /// <summary>切换至本波次下一名出手单位前重置本地虚拟气血与护盾，隔离不同出手者之间的同刻互杀影响。</summary>
    public void BeginActor()
    {
        _localVirtualHp.Clear();
        _localVirtualShield.Clear();
    }

    /// <summary>读取目标在当前出手者视角下的剩余气血（反映同一出手者多段连击的递减，不受同波其他出手者干扰）。</summary>
    public double GetLocalVirtualHp(CombatantState target)
        => _localVirtualHp.TryGetValue(target, out var hp) ? hp : target.Hitpoints;

    /// <summary>读取目标在当前出手者视角下的剩余护盾。</summary>
    public double GetLocalVirtualShield(CombatantState target)
        => _localVirtualShield.TryGetValue(target, out var shield) ? shield : target.Shield;

    /// <summary>判断目标在当前出手者本次动作内是否仍存活。</summary>
    public bool IsLocallyAlive(CombatantState target) => GetLocalVirtualHp(target) > 1e-12;

    /// <summary>登记一次同刻护盾获取（波末仅对仍存活目标落账）。</summary>
    public void AddShieldGain(CombatantState target, double amount)
    {
        if (!double.IsFinite(amount) || amount <= 1e-9) return;
        _shieldGains[target] = _shieldGains.GetValueOrDefault(target) + amount;
    }

    /// <summary>登记本波次单位主动引爆并消耗全部护盾（如「迸裂」）。</summary>
    public void ConsumeAllShield(CombatantState unit) => _shieldConsumed.Add(unit);

    /// <summary>登记一次同刻伤害（返回按当前出手者虚拟护盾与气血裁决的伤害与实伤，用于吸血/燃魂/反伤计算）。</summary>
    public (double Damage, double Actual, bool LocalDowned) RecordDamage(
        CombatEncounterState encounter,
        CombatantState attacker,
        CombatantState target,
        double damage,
        bool accumulateXuyi,
        bool bypassShield = false)
    {
        var virtualHp = GetLocalVirtualHp(target);
        if (virtualHp <= 1e-12) return (0, 0, false);
        var remaining = Math.Max(0, damage);
        var shieldAbsorbed = 0d;
        if (!bypassShield)
        {
            var virtualShield = GetLocalVirtualShield(target);
            if (virtualShield > 0 && remaining > 0)
            {
                shieldAbsorbed = Math.Min(virtualShield, remaining);
                _localVirtualShield[target] = Math.Max(0, virtualShield - shieldAbsorbed);
                remaining -= shieldAbsorbed;
            }
        }
        var limitedHp = remaining > 0 ? CombatDamageSettlement.Limit(attacker, virtualHp, remaining) : 0;
        var nextVirtualHp = Math.Max(0, virtualHp - limitedHp);
        var actual = virtualHp - nextVirtualHp;
        _localVirtualHp[target] = nextVirtualHp;
        var totalDealt = shieldAbsorbed + limitedHp;
        var handle = (encounter.ActionRecorder as DuelActionRecorder)?.CaptureActiveAction();
        _damages.Add(new PendingDamageEntry(attacker, target, totalDealt, accumulateXuyi, bypassShield, handle));
        var localDowned = virtualHp > 1e-12 && nextVirtualHp <= 1e-12;
        if (localDowned) _downedRecorded.Add(target);
        return (totalDealt, actual, localDowned);
    }

    /// <summary>登记一次同刻治疗/吸血（波末扣伤之后，仅对仍存活目标按缺血上限实际落账）。</summary>
    public void RecordHeal(
        CombatEncounterState encounter,
        CombatantState source,
        CombatantState target,
        DuelRecordEffectKind recordKind,
        double amount,
        string? defId = null,
        int combatEventIndex = -1)
    {
        if (!double.IsFinite(amount) || amount <= 1e-9) return;
        var handle = (encounter.ActionRecorder as DuelActionRecorder)?.CaptureActiveAction();
        _heals.Add(new PendingHealEntry(source, target, recordKind, amount, defId, combatEventIndex, handle));
    }

    /// <summary>登记灵力净变动（吸蓝、天雷阵耗蓝等）。</summary>
    public void AddManaDelta(CombatantState unit, double delta)
    {
        if (delta == 0) return;
        _manaDeltas[unit] = _manaDeltas.GetValueOrDefault(unit) + delta;
    }

    /// <summary>读取单位在波初快照上的可用灵力（叠加同一单位在本波已消耗的灵力，避免同一单位连触发透支）。</summary>
    public double GetEffectiveMana(CombatantState unit)
        => Math.Clamp(unit.Manapoints + _manaDeltas.GetValueOrDefault(unit), 0, unit.Stats.MaxManapoints);

    /// <summary>登记基础威胁变动。</summary>
    public void AddThreatDelta(CombatantState unit, double delta)
    {
        if (delta == 0) return;
        _threatDeltas[unit] = _threatDeltas.GetValueOrDefault(unit) + delta;
    }

    /// <summary>登记威胁增幅点数变动。</summary>
    public void AddThreatBonusDelta(CombatantState unit, double delta)
    {
        if (delta == 0) return;
        _threatBonusDeltas[unit] = _threatBonusDeltas.GetValueOrDefault(unit) + delta;
    }

    /// <summary>延迟一项 Buff 变更操作至波末统一应用（防止同刻先结算侧的麻痹/移形/燃魂改变同刻后结算侧判定）。</summary>
    public void DeferBuffOp(Action op) => _buffOps.Add(op);

    /// <summary>累加同刻波次内目标的燃烧预览层数，返回叠加后的累计总层数供战报记录与技能 Buff 口径保持一致。</summary>
    public double AddPreviewRanhunStacks(CombatantState target, double addedStacks)
    {
        var baseStacks = _pendingRanhunStacks.TryGetValue(target, out var pending)
            ? pending
            : target.Buffs.FirstOrDefault(buff => buff.BuffId == CombatTraitRules.RanhunBuffId)?.Stacks ?? 0;
        var next = baseStacks + addedStacks;
        _pendingRanhunStacks[target] = next;
        return next;
    }

    /// <summary>同一单位在同刻波次内至多消费一次「蓄意」爆发，防止多段连击重复触发。</summary>
    public bool TryConsumeXuyi(CombatantState attacker) => _xuyiConsumed.Add(attacker);

    /// <summary>波末原子提交：先落全部伤害 → 再对存活者落治疗/吸血 → 落灵力/威胁/Buff → 刷新派生层数与事件尾帧。</summary>
    public void Commit(CombatEncounterState encounter, ref List<CombatEvent>? events)
    {
        var recorder = encounter.ActionRecorder as DuelActionRecorder;
        var xuyiGains = new Dictionary<CombatantState, double>(ReferenceEqualityComparer.Instance);
        var wasAliveBefore = new Dictionary<CombatantState, bool>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < encounter.SideA.Count; i++) wasAliveBefore[encounter.SideA[i]] = encounter.SideA[i].IsAlive;
        for (var i = 0; i < encounter.SideB.Count; i++) wasAliveBefore[encounter.SideB[i]] = encounter.SideB[i].IsAlive;

        // 1. 统一应用本波主动消耗护盾与全部同刻伤害（护盾先吸收，溢出遵守欲速则不达留一与蓄意积累）。
        foreach (var consumedUnit in _shieldConsumed)
            consumedUnit.Shield = 0;

        for (var i = 0; i < _damages.Count; i++)
        {
            var entry = _damages[i];
            if (!entry.Target.IsAlive) continue;
            var remaining = Math.Max(0, entry.Damage);
            if (!entry.BypassShield && entry.Target.Shield > 0 && remaining > 0)
            {
                var absorbed = Math.Min(entry.Target.Shield, remaining);
                entry.Target.Shield = Math.Max(0, entry.Target.Shield - absorbed);
                remaining -= absorbed;
            }
            var applied = remaining > 0 ? CombatDamageSettlement.Limit(entry.Attacker, entry.Target.Hitpoints, remaining) : 0;
            var before = entry.Target.Hitpoints;
            entry.Target.Hitpoints = Math.Max(0, before - applied);
            var actual = before - entry.Target.Hitpoints;
            if (!entry.Target.IsAlive) entry.Target.Shield = 0;
            if (entry.AccumulateXuyi && actual > 0
                && CombatTraitRules.ContainsTrait(entry.Target.Stats.Traits, CombatTraitRules.XuyiTraitId))
            {
                xuyiGains[entry.Target] = xuyiGains.GetValueOrDefault(entry.Target)
                    + actual * CombatTraitRules.LevelScale(entry.Target, CombatTraitRules.XuyiTraitId);
            }
            if (before > 1e-12 && !entry.Target.IsAlive && _downedRecorded.Add(entry.Target))
                recorder?.AppendDeferredDowned(entry.RecorderActionHandle, entry.Attacker, entry.Target);
        }

        // 2. 仍存活单位结算护盾获取、蓄意层数积累与同刻治疗/吸血（倒地单位不再被同刻吸血拉起，双方完全对称）。
        foreach (var (unit, shieldGain) in _shieldGains)
        {
            if (unit.IsAlive && shieldGain > 0)
                unit.Shield += shieldGain;
        }
        foreach (var (unit, gain) in xuyiGains)
        {
            if (!unit.IsAlive || gain <= 0) continue;
            var existing = unit.Buffs.FirstOrDefault(buff => buff.BuffId == CombatTraitRules.XuyiBuffId);
            unit.SetBuff(new CombatBuffState(CombatTraitRules.XuyiBuffId, CombatTraitRules.BattleLifetimeSeconds,
                (existing?.Stacks ?? 0) + gain, "蓄意", ResetOnWave: true));
        }

        for (var i = 0; i < _heals.Count; i++)
        {
            var heal = _heals[i];
            var actualHealed = 0d;
            if (heal.Target.IsAlive)
            {
                actualHealed = Math.Min(heal.Target.Stats.MaxHitpoints - heal.Target.Hitpoints, Math.Max(0, heal.Amount));
                if (actualHealed > 1e-9)
                {
                    heal.Target.Hitpoints += actualHealed;
                    encounter.StatsBook?.RecordHealing(heal.Source, autoRegen: false, actualHealed);
                    recorder?.AppendDeferredHeal(heal.RecorderActionHandle, heal.Source, heal.Target,
                        heal.RecordKind, actualHealed, heal.DefId);
                }
            }
            if (events is not null && heal.CombatEventIndex >= 0 && heal.CombatEventIndex < events.Count)
            {
                events[heal.CombatEventIndex] = events[heal.CombatEventIndex] with
                {
                    Healing = actualHealed,
                    TargetHitpointsAfter = heal.Target.Hitpoints
                };
            }
            else if (events is not null && Math.Abs(actualHealed - heal.Amount) > 1e-9)
            {
                for (var eIdx = _eventsStartIndex; eIdx < events.Count; eIdx++)
                {
                    var ev = events[eIdx];
                    if (ev.AttackerId == heal.Source.Id
                        && (ev.TargetId == heal.Target.Id || (heal.DefId is not null && ev.AbilityId == heal.DefId))
                        && Math.Abs(ev.Healing - heal.Amount) <= 1e-9)
                    {
                        events[eIdx] = ev with
                        {
                            Healing = actualHealed,
                            TargetHitpointsAfter = ev.TargetId == heal.Target.Id
                                ? heal.Target.Hitpoints
                                : ev.TargetHitpointsAfter
                        };
                        break;
                    }
                }
            }
        }

        // 3. 统一应用灵力、威胁、延迟 Buff 与派生层数。
        foreach (var (unit, delta) in _manaDeltas)
            unit.Manapoints = Math.Clamp(unit.Manapoints + delta, 0, unit.Stats.MaxManapoints);
        foreach (var (unit, delta) in _threatDeltas)
            unit.Threat = Math.Max(0, unit.Threat + delta);
        foreach (var (unit, delta) in _threatBonusDeltas)
            unit.ThreatBonusPoints += delta;
        for (var i = 0; i < _buffOps.Count; i++)
            _buffOps[i]();

        var aliveChanged = false;
        for (var i = 0; i < encounter.SideA.Count; i++)
        {
            var u = encounter.SideA[i];
            u.SyncDerivedStacks();
            if (wasAliveBefore.TryGetValue(u, out var beforeAlive) && beforeAlive != u.IsAlive) aliveChanged = true;
        }
        for (var i = 0; i < encounter.SideB.Count; i++)
        {
            var u = encounter.SideB[i];
            u.SyncDerivedStacks();
            if (wasAliveBefore.TryGetValue(u, out var beforeAlive) && beforeAlive != u.IsAlive) aliveChanged = true;
        }
        if (aliveChanged) encounter.NotifyAliveCountsChanged();

        // 4. 回填本波次事件的气血与灵力终态快照，保证回放资源条精确收敛至落账真值。
        if (events is not null)
        {
            for (var i = _eventsStartIndex; i < events.Count; i++)
            {
                var ev = events[i];
                var attacker = encounter.FindUnit(ev.AttackerId);
                var target = encounter.FindUnit(ev.TargetId);
                events[i] = ev with
                {
                    TargetHitpointsAfter = target?.Hitpoints ?? ev.TargetHitpointsAfter,
                    AttackerManapointsAfter = ev.AttackerManapointsAfter.HasValue
                        ? (attacker?.Manapoints ?? ev.AttackerManapointsAfter)
                        : null,
                    TargetManapointsAfter = ev.TargetManapointsAfter.HasValue
                        ? (target?.Manapoints ?? ev.TargetManapointsAfter)
                        : null,
                    AttackerShieldAfter = ev.AttackerShieldAfter.HasValue
                        ? (attacker?.Shield ?? ev.AttackerShieldAfter)
                        : null,
                    TargetShieldAfter = ev.TargetShieldAfter.HasValue
                        ? (target?.Shield ?? ev.TargetShieldAfter)
                        : null
                };
            }
        }
    }
}
