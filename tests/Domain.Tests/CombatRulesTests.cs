namespace Domain.Tests;

/// <summary>
/// 战斗规则测试：伤害公式、元素克制、随机切片与状态恢复回归。
/// </summary>
public sealed class CombatRulesTests
{
    /// <summary>
    /// 伤害公式回归：物理吃防御护甲，法术受法抗与元素抗性削减，抗性缩放参与结算；
    /// 期望值为公式的独立手算结果，防止公式改动静默漂移。
    /// </summary>
    [Theory]
    [InlineData(CombatDamageKind.Physical, CombatElement.None, 100d * 100 / 141)]
    [InlineData(CombatDamageKind.Spell, CombatElement.Fire, 100d * 100 / 181 * 0.5)]
    public void DamageUsesMatchingCategoryResistanceScaleAndElement(
        CombatDamageKind kind, CombatElement element, double expected)
    {
        var attacker = CombatantStats.Create(1000, 100, 0, 1, damageKind: kind) with
        { Element = element };
        var defender = CombatantStats.Create(1000, 0, 20, 1) with
        { MagicResistance = 40, ResistanceScale = 2, FireDamageReductionPoints = 100 };
        var state = DeterministicCombatEngine.Start("formula", 42, attacker, defender);
        var hit = DeterministicCombatEngine.AdvanceTo(state, 1.01).Events[0];
        Assert.Equal("player", hit.AttackerId);
        Assert.True(hit.Hit);
        Assert.Equal(expected, hit.Damage, 10);
    }

    /// <summary>
    /// 含暴击/破防/格挡等随机判定的战斗：切片推进 + 快照恢复后的事件序列必须与整段推进逐帧一致；
    /// 不同种子必须产生不同事件流（确定性随机门禁）。
    /// </summary>
    [Fact]
    public void RandomCombatSurvivesSlicingAndRestoreWithoutChangingEvents()
    {
        var player = CombatantStats.Create(1000000, 80, 20, 0.7, 30, 90) with
        { Critical = 150, BreakPower = 90, ParryResolve = 5 };
        var enemy = player with { BreakPower = 10, ParryResolve = 180 };
        var whole = DeterministicCombatEngine.Start("replay", 123, player, enemy);
        var split = DeterministicCombatEngine.Start("replay", 123, player, enemy);
        var expected = DeterministicCombatEngine.AdvanceTo(whole, 120).Events;
        var first = DeterministicCombatEngine.AdvanceTo(split, 13.2).Events;
        var restored = CombatEncounterState.Restore("replay", split.Seed, new CombatantTemplate(player),
            CombatEncounterState.Snapshot(split.Player),
            [new CombatantTemplate(enemy)],
            [CombatEncounterState.Snapshot(split.Enemies[0])],
            split.ElapsedSeconds, split.Outcome, split.Sequence);
        var second = DeterministicCombatEngine.AdvanceTo(restored, 120).Events;
        Assert.Equal(expected, first.Concat(second));
        Assert.Contains(expected, attack => !attack.Hit);
        Assert.Contains(expected, attack => attack.Critical);
        Assert.Contains(expected, attack => attack.Break);
        Assert.Contains(expected, attack => attack.Parried);
        Assert.Equal(whole.Player.Hitpoints, restored.Player.Hitpoints);
        var differentSeed = DeterministicCombatEngine.Start("replay", 124, player, enemy);
        Assert.NotEqual(expected[0], DeterministicCombatEngine.AdvanceTo(differentSeed, 120).Events[0]);
    }

    /// <summary>
    /// 固定伤害增减测试：攻方拥有固定增伤时，即便守方拥有极大护甲，依然能够打出对应的固定伤害；
    /// 守方拥有固定减伤时，在最后的伤害中扣减，超额减免截断为 0。
    /// </summary>
    [Fact]
    public void FixedDamageBonusAndReduction_ApplyAtEndOfFormulaAndClampToZero()
    {
        // 攻方攻击力 0，固定增伤 10 点，必中
        var attacker = CombatantStats.Create(1000, 0, 0, 1, accuracy: 1000) with
        {
            FixedDamageBonus = 10
        };
        // 守方护甲 999999，固定减伤 4 点，闪避 0
        var defender = CombatantStats.Create(1000, 0, 999999, 1, evasion: 0) with
        {
            FixedDamageReduction = 4
        };
        var state = DeterministicCombatEngine.Start("fixed-dmg", 1, attacker, defender);
        var hit = DeterministicCombatEngine.AdvanceTo(state, 1.01).Events[0];
        Assert.True(hit.Hit);
        // 0 基础伤害 + 10 固定增伤 - 4 固定减伤 = 6
        Assert.Equal(6.0, hit.Damage, 5);

        // 守方固定减伤 15 点时（超过 10 点），最终伤害必须截断为 0
        var defenderHighReduction = defender with { FixedDamageReduction = 15 };
        var stateZero = DeterministicCombatEngine.Start("fixed-dmg-zero", 1, attacker, defenderHighReduction);
        var hitZero = DeterministicCombatEngine.AdvanceTo(stateZero, 1.01).Events[0];
        Assert.True(hitZero.Hit);
        Assert.Equal(0.0, hitZero.Damage);
    }

    /// <summary>
    /// 固定物理与法术伤害测试：物理固定增免只在物理伤害时生效，法术固定增免只在术法伤害时生效。
    /// </summary>
    [Theory]
    [InlineData(CombatDamageKind.Physical, 10.0, 0.0)]
    [InlineData(CombatDamageKind.Spell, 0.0, 15.0)]
    public void FixedPhysicalAndSpellDamage_OnlyAffectsMatchingKind(
        CombatDamageKind kind, double expectedPhysDamage, double expectedSpellDamage)
    {
        var attacker = CombatantStats.Create(1000, 0, 0, 1, accuracy: 1000, damageKind: kind) with
        {
            FixedPhysicalDamageBonus = 10,
            FixedSpellDamageBonus = 15
        };
        var defender = CombatantStats.Create(1000, 0, 999999, 1, evasion: 0);
        var state = DeterministicCombatEngine.Start("fixed-type", 1, attacker, defender);
        var hit = DeterministicCombatEngine.AdvanceTo(state, 1.01).Events[0];
        Assert.True(hit.Hit);
        var expectedTotal = expectedPhysDamage + expectedSpellDamage;
        Assert.Equal(expectedTotal, hit.Damage, 5);
    }

    /// <summary>
    /// 验证雷击符类直接打击伤害（ResolveStrikeDamage）也正确应用末位固定增减伤。
    /// </summary>
    [Fact]
    public void ResolveStrikeDamage_AppliesFixedDamageBonusAndReduction()
    {
        var attacker = CombatantStats.Create(1000, 0, 0, 1, damageKind: CombatDamageKind.Physical) with
        {
            FixedDamageBonus = 20,
            FixedPhysicalDamageBonus = 5
        };
        var defender = CombatantStats.Create(1000, 0, 999999, 1);
        var target = new CombatantState("dummy", defender);
        var damage = DeterministicCombatEngine.ResolveStrikeDamage(attacker, target);
        // 基础伤害穿甲后为 0，固定增伤 20 + 物理固定增伤 5 = 25
        Assert.Equal(25.0, damage, 5);
    }

    /// <summary>
    /// 验证攻击未命中（Miss）时，固定增伤绝不触发，伤害恒为 0。
    /// </summary>
    [Fact]
    public void FixedDamageBonus_DoesNotTriggerOnMiss()
    {
        // 命中为 0，面对 999999 闪避，必然 Miss
        var attacker = CombatantStats.Create(1000, 100, 0, 1, accuracy: 0) with
        {
            FixedDamageBonus = 50
        };
        var defender = CombatantStats.Create(1000, 0, 0, 1, evasion: 999999);
        var state = DeterministicCombatEngine.Start("miss-test", 1, attacker, defender);
        var hit = DeterministicCombatEngine.AdvanceTo(state, 1.01).Events[0];
        Assert.False(hit.Hit);
        Assert.Equal(0.0, hit.Damage);
    }
}
