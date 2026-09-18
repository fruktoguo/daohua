namespace Domain.Tests;

/// <summary>
/// 战斗钩子管线测试：汲取/反伤衍生结算、衍生伤害不回流、确定性一致性回归。
/// </summary>
public sealed class CombatHookTests
{
    /// <summary>
    /// 气血汲取：命中后攻击方按实际伤害比例回血，产出攻击方自体治疗衍生事件；
    /// 主伤害事件保持在衍生事件之前。
    /// </summary>
    [Fact]
    public void HpDrainHealsAttackerByDealtDamage()
    {
        var player = CombatantStats.Create(1000, 100, 0, 1) with
        { Effects = new CombatEffectProfile(HpDrainRatio: 0.5) };
        var enemy = CombatantStats.Create(100000, 0, 0, 2);
        var state = CombatEncounterState.Restore("drain", 7, player,
            new CombatantSnapshot(500, 0, 1, 0, 0),
            [new CombatantTemplate(enemy)],
            [new CombatantSnapshot(100000, 0, 2, 0, 0)],
            0, CombatOutcome.Running);
        var result = DeterministicCombatEngine.AdvanceTo(state, 1.01);
        // 普攻伤害 100×(100/101)；回血 = 伤害 × 0.5。
        const double damage = 100d * 100 / 101;
        Assert.Equal(2, result.Events.Count);
        var hit = result.Events[0];
        Assert.Equal("enemy:0", hit.TargetId);
        Assert.Equal(damage, hit.Damage, 10);
        var drain = result.Events[1];
        Assert.Equal("player", drain.AttackerId);
        Assert.Equal("player", drain.TargetId);
        Assert.Equal(0, drain.Damage);
        Assert.Equal(damage * 0.5, drain.Healing, 10);
        Assert.Equal(500 + damage * 0.5, state.Player.Hitpoints, 10);
    }

    /// <summary>
    /// 灵力汲取：命中后从目标灵力池按实际伤害偷取；目标减全额，攻击方只装得下的部分。
    /// </summary>
    [Fact]
    public void ManaDrainStealsTargetMana()
    {
        var player = CombatantStats.Create(1000, 100, 0, 1) with
        { MaxManapoints = 100, Effects = new CombatEffectProfile(MpDrainRatio: 1) };
        var enemy = CombatantStats.Create(100000, 0, 0, 2) with { MaxManapoints = 200 };
        var state = CombatEncounterState.Restore("mana", 7, player,
            new CombatantSnapshot(1000, 50, 1, 0, 0),
            [new CombatantTemplate(enemy)],
            [new CombatantSnapshot(100000, 200, 2, 0, 0)],
            0, CombatOutcome.Running);
        DeterministicCombatEngine.AdvanceTo(state, 1.01);
        // 偷取量 = 100×(100/101)；攻击方灵力 50+偷取量 夹取至上限 100，目标扣全额。
        Assert.Equal(100, state.Player.Manapoints, 6);
        Assert.Equal(200 - 100d * 100 / 101, state.Enemies[0].Manapoints, 10);
    }

    /// <summary>
    /// 伤害反弹：受击方把实收伤害按比例弹回攻击方；衍生事件方向反转且计入受击方威胁值。
    /// </summary>
    [Fact]
    public void DamageReflectBouncesDamageBackToAttacker()
    {
        var player = CombatantStats.Create(1000, 100, 0, 1);
        var enemy = CombatantStats.Create(100000, 0, 0, 2) with
        { Effects = new CombatEffectProfile(DamageReflectRatio: 0.5) };
        var state = CombatEncounterState.Start("reflect", 7, player, enemy);
        var result = DeterministicCombatEngine.AdvanceTo(state, 1.01);
        Assert.Equal(2, result.Events.Count);
        var reflected = result.Events[1];
        Assert.Equal("enemy:0", reflected.AttackerId);
        Assert.Equal("player", reflected.TargetId);
        const double reflected_damage = 100d * 100 / 101 * 0.5;
        Assert.Equal(reflected_damage, reflected.Damage, 10);
        Assert.Equal(1000 - reflected_damage, state.Player.Hitpoints, 10);
        Assert.Equal(reflected_damage, state.Enemies[0].Threat, 10);
    }

    /// <summary>
    /// 衍生伤害不回流管线：双方互带反伤时，反弹伤害不再触发受击方的反伤钩子，
    /// 否则将无限递归；一次出手只产出主事件 + 一条衍生事件。
    /// </summary>
    [Fact]
    public void ReflectedDamageDoesNotReenterPipeline()
    {
        var player = CombatantStats.Create(1000, 100, 0, 1) with
        { Effects = new CombatEffectProfile(DamageReflectRatio: 1) };
        var enemy = CombatantStats.Create(100000, 0, 0, 2) with
        { Effects = new CombatEffectProfile(DamageReflectRatio: 1) };
        var state = CombatEncounterState.Start("norecur", 7, player, enemy);
        var result = DeterministicCombatEngine.AdvanceTo(state, 1.01);
        Assert.Equal(2, result.Events.Count);
        Assert.Equal(1000 - 100d * 100 / 101, state.Player.Hitpoints, 10);
        Assert.Equal(100000 - 100d * 100 / 101, state.Enemies[0].Hitpoints, 10);
    }

    /// <summary>
    /// 未命中不触发任何衍生效果：高闪避目标下攻击落空，不产生治疗/反弹事件。
    /// </summary>
    [Fact]
    public void MissedAttackEmitsNoDerivedEvents()
    {
        var player = CombatantStats.Create(1000, 100, 0, 1) with
        { Effects = new CombatEffectProfile(HpDrainRatio: 1) };
        var enemy = CombatantStats.Create(100000, 0, 0, 2, evasion: 1e9) with
        { Effects = new CombatEffectProfile(DamageReflectRatio: 1) };
        var state = CombatEncounterState.Restore("miss", 7, player,
            new CombatantSnapshot(500, 0, 1, 0, 0),
            [new CombatantTemplate(enemy)],
            [new CombatantSnapshot(100000, 0, 2, 0, 0)],
            0, CombatOutcome.Running);
        var result = DeterministicCombatEngine.AdvanceTo(state, 1.01);
        Assert.Single(result.Events);
        Assert.False(result.Events[0].Hit);
        Assert.Equal(500, state.Player.Hitpoints);
    }

    /// <summary>
    /// 钩子参与后的战斗保持确定性：相同初始状态两次推进产出逐帧一致的事件序列。
    /// </summary>
    [Fact]
    public void HookedCombatStaysDeterministic()
    {
        var player = CombatantStats.Create(1000, 100, 0, 1, 30, 90) with
        { Effects = new CombatEffectProfile(HpDrainRatio: 0.3, MpDrainRatio: 0.5) };
        var enemy = CombatantStats.Create(100000, 50, 0, 0.5) with
        { MaxManapoints = 200, Effects = new CombatEffectProfile(DamageReflectRatio: 0.2) };
        var first = CombatEncounterState.Start("det", 42, player, enemy);
        var second = CombatEncounterState.Start("det", 42, player, enemy);
        var firstEvents = DeterministicCombatEngine.AdvanceTo(first, 30).Events;
        var secondEvents = DeterministicCombatEngine.AdvanceTo(second, 30).Events;
        Assert.Equal(firstEvents, secondEvents);
        Assert.Equal(first.Player.Hitpoints, second.Player.Hitpoints);
        Assert.Equal(first.Player.Manapoints, second.Player.Manapoints);
        Assert.Equal(first.Enemies[0].Hitpoints, second.Enemies[0].Hitpoints);
    }
}
