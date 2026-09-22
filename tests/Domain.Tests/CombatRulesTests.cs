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
        { MagicResistance = 40, ResistanceScale = 2, FireResistance = 200 };
        var state = CombatEncounterState.Start("formula", 42, attacker, defender);
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
        var whole = CombatEncounterState.Start("replay", 123, player, enemy);
        var split = CombatEncounterState.Start("replay", 123, player, enemy);
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
        var differentSeed = CombatEncounterState.Start("replay", 124, player, enemy);
        Assert.NotEqual(expected[0], DeterministicCombatEngine.AdvanceTo(differentSeed, 120).Events[0]);
    }
}
