namespace Domain.Combat;

/// <summary>
/// 当前战斗的条件特质监听表：只绑定已激活特质，事件派发不重新检查单位资格。
/// 绑定引用仅在当前遭遇内存活，入战及恢复时由定格特质重建，不进入快照。
/// </summary>
internal sealed class CombatTraitListeners(CombatEncounterState encounter)
{
    private static readonly CombatAliveCountTraitChecker[] Checkers = [new OutnumberedChecker(), new DuelKingChecker()];
    private static readonly Dictionary<string, CombatAliveCountTraitChecker> ByTraitId
        = Checkers.ToDictionary(checker => checker.TraitId, StringComparer.Ordinal);
    private static readonly Dictionary<string, CombatAliveCountTraitChecker> ByBuffId
        = Checkers.ToDictionary(checker => checker.BuffId, StringComparer.Ordinal);

    private List<Listener>? _aliveCountListeners;

    /// <summary>当前实际挂接的存活人数检查器数量；未装备者不占监听槽位。</summary>
    internal int AliveCountListenerCount => _aliveCountListeners?.Sum(listener => listener.Checkers.Length) ?? 0;

    /// <summary>真实入场或快照应用后重建绑定，并清理载荷中已失去装配资格的条件 Buff。</summary>
    internal void Rebuild()
    {
        _aliveCountListeners?.Clear();
        for (var i = 0; i < encounter.PlayerAllies.Count; i++) BindInitial(encounter.PlayerAllies[i], i);
        for (var i = 0; i < encounter.Enemies.Count; i++) BindInitial(encounter.Enemies[i], encounter.PlayerAllies.Count + i);
        AliveCountsChanged();
    }

    /// <summary>存活人数变化后立即通知已挂检查器；无人监听时不统计人数、不访问单位特质或 Buff。</summary>
    internal void AliveCountsChanged()
    {
        if (_aliveCountListeners is not { Count: > 0 }) return;
        var (allies, enemies) = CountAlive();
        foreach (var listener in _aliveCountListeners) Refresh(listener, allies, enemies);
    }

    private void BindInitial(CombatantState unit, int order)
    {
        var checkers = Resolve(unit.TraitState.Ids);
        // 恢复时只对真实携带的 Buff 做来源资格对账，不能将过期绑定或旧加成带入新战斗。
        unit.RemoveBuffsWhere(buff => ByBuffId.TryGetValue(buff.BuffId, out var checker) && !checkers.Contains(checker));
        if (checkers.Length > 0) (_aliveCountListeners ??= []).Add(new Listener(unit, order, checkers));
    }

    private static CombatAliveCountTraitChecker[] Resolve(IReadOnlyList<string>? traits)
    {
        if (traits is not { Count: > 0 }) return [];
        List<CombatAliveCountTraitChecker>? active = null;
        for (var i = 0; i < traits.Count; i++)
        {
            if (!ByTraitId.TryGetValue(traits[i], out var checker)) continue;
            active ??= [];
            if (!active.Contains(checker)) active.Add(checker);
        }
        if (active is null) return [];
        // 使用规则顺序而非装配 ID 顺序，保持 Buff 列表与浮点累加顺序确定。
        active.Sort(static (left, right) => left.Order.CompareTo(right.Order));
        return active.ToArray();
    }

    private (double Allies, double Enemies) CountAlive()
        => (encounter.PlayerAllies.Count(static unit => unit.IsAlive), encounter.Enemies.Count(static unit => unit.IsAlive));

    private void Refresh(Listener listener, double allies, double enemies)
    {
        var isAlly = listener.Order < encounter.PlayerAllies.Count;
        foreach (var checker in listener.Checkers)
            checker.Refresh(listener.Unit, isAlly ? allies : enemies, isAlly ? enemies : allies);
    }

    private readonly record struct Listener(CombatantState Unit, int Order, CombatAliveCountTraitChecker[] Checkers);

    private sealed class OutnumberedChecker() : CombatAliveCountTraitChecker(
        CombatTraitRules.OutnumberedTraitId, "buff.woyaodashige", "我要打十个", 10, 100)
    {
        protected override double ResolveStacks(double own, double opponents) => Math.Max(0, opponents - 1);
        protected override BuffEffect[] Effects(double value)
            => [new(BuffEffectChannel.DamageBonusPoints, value), new(BuffEffectChannel.DamageAbsorptionPoints, value)];
    }

    private sealed class DuelKingChecker() : CombatAliveCountTraitChecker(
        CombatTraitRules.DuelKingTraitId, "buff.dantiaowang", "单挑王", 20, 200)
    {
        protected override double ResolveStacks(double own, double opponents) => own == 1 && opponents == 1 ? 1 : 0;
        protected override BuffEffect[] Effects(double value)
            => [new(BuffEffectChannel.DamageBonusPoints, value), new(BuffEffectChannel.DamageAbsorptionPoints, value),
                new(BuffEffectChannel.CriticalDamagePoints, value), new(BuffEffectChannel.ActionSpeedPoints, value)];
    }
}

/// <summary>无状态的存活人数检查器；装配资格由监听表保证，规则只判断当前人数与单位存活状态。</summary>
internal abstract class CombatAliveCountTraitChecker(string traitId, string buffId, string name, double points, int order)
{
    internal string TraitId { get; } = traitId;
    internal string BuffId { get; } = buffId;
    internal int Order { get; } = order;

    internal void Refresh(CombatantState unit, double own, double opponents)
    {
        var stacks = unit.IsAlive ? ResolveStacks(own, opponents) : 0;
        var existing = unit.Buffs.FirstOrDefault(buff => buff.BuffId == BuffId);
        if (stacks == 0)
        {
            if (existing is not null) Remove(unit);
            return;
        }
        var value = points * CombatTraitRules.LevelScale(unit, TraitId);
        if (existing?.Stacks == stacks && existing.EffectValue(BuffEffectChannel.DamageBonusPoints) == value) return;
        unit.SetBuff(new CombatBuffState(BuffId, CombatTraitRules.BattleLifetimeSeconds, stacks, name,
            ResetOnWave: true, Effects: Effects(value)));
    }

    internal void Remove(CombatantState unit) => unit.RemoveBuffsWhere(buff => buff.BuffId == BuffId);
    protected abstract double ResolveStacks(double own, double opponents);
    protected abstract BuffEffect[] Effects(double value);
}
