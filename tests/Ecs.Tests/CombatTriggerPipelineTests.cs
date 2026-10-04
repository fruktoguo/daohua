using DaoHua.Ecs;
using Domain.Combat;
using Xunit;

namespace Ecs.Tests;

/// <summary>轻量触发器注册表门禁：只验证发现、顺序和内联结算边界。</summary>
public sealed class CombatTriggerPipelineTests
{
    [Fact]
    public void RegistryDiscoversInlineHandlersInDeterministicOrder()
    {
        InlineCombatTriggerRegistry.EnsureInitialized();

        Assert.Equal(4, InlineCombatTriggerRegistry.OnHitHandlerCount);
        Assert.Equal(
            [
                typeof(InlineOnHitLeechHandler),
                typeof(InlineManaLeechHandler),
                typeof(InlineThornsHandler),
                typeof(InlineThreatOnTakenHandler),
            ],
            InlineCombatTriggerRegistry.OnHitHandlerTypes);
        Assert.DoesNotContain(
            typeof(YuoCore.Ecs.ISystemTag),
            typeof(InlineOnHitLeechHandler).GetInterfaces());
    }

    [Fact]
    public void InlinePipelinePreservesOnHitEffectsAndClearsPendingTriggers()
    {
        var player = CombatantStats.Create(1000, 100, 0, 1) with
        {
            Effects = new CombatEffectProfile(HpDrainPoints: 50, MpDrainPoints: 100),
            MaxManapoints = 200,
        };
        var enemy = CombatantStats.Create(100000, 200, 0, 2) with
        {
            Effects = new CombatEffectProfile(DamageReflectPoints: 50),
            MaxManapoints = 200,
        };
        var state = DeterministicCombatEngine.Start(
            "inline-trigger-registry",
            7,
            new CombatantTemplate(player),
            [new CombatantTemplate(enemy)]);

        var result = DeterministicCombatEngine.AdvanceTo(state, 1.01);

        Assert.Equal(2, result.Events.Count);
        Assert.Equal("enemy:0", result.Events[1].AttackerId);
        Assert.Equal("player", result.Events[1].TargetId);
        Assert.Empty(state.PendingTriggers);
        Assert.Equal(200, state.Player.Manapoints, 10);
        Assert.Equal(200 - result.Events[0].Damage, state.Enemies[0].Manapoints, 10);
        Assert.True(state.Player.Hitpoints < 1000);
    }

    [Fact]
    public void ThreatOnTakenAccumulatesTargetThreatByRawDamageOnHit()
    {
        // 力微饭否语义：受击方（玩家）被命中时按攻击方面板原始伤害 1:1 累积自身威胁。
        var player = CombatantStats.Create(100000, 50, 0, 1) with
        {
            Effects = new CombatEffectProfile(ThreatOnTakenPoints: 100),
        };
        var enemy = CombatantStats.Create(100000, 80, 0, 1);
        var state = DeterministicCombatEngine.Start(
            "threat-on-taken",
            11,
            new CombatantTemplate(player),
            [new CombatantTemplate(enemy)]);

        var baseline = DeterministicCombatEngine.Start(
            "threat-on-taken-baseline",
            11,
            new CombatantTemplate(player with { Effects = CombatEffectProfile.Empty }),
            [new CombatantTemplate(enemy)]);
        var result = DeterministicCombatEngine.AdvanceTo(state, 1.01);
        DeterministicCombatEngine.AdvanceTo(baseline, 1.01);

        // 敌方命中（事件序列第二位为 enemy:0 → player 的攻击）后，玩家威胁按原始伤害等额上涨。
        var enemyHit = result.Events.First(e => e.AttackerId == "enemy:0");
        Assert.True(enemyHit.Hit);
        // 力微饭否贡献 = 同种子对照战斗的玩家威胁差（隔离玩家自身普攻的实伤威胁）。
        var expected = enemy.AttackPower * enemy.BasicAttackRatio;
        Assert.Equal(expected, state.Player.Threat - baseline.Player.Threat, 6);
        // 无效果参数的对照组：受击不涨威胁，威胁全部来自玩家自身普攻实伤。
        Assert.True(baseline.Player.Threat > 0);
    }

    [Fact]
    public void UnseenTraitGrantsTenSecondZeroThreatBuffAtCreate()
    {
        // 看不到我语义：开战授予 buff.yinshen（ThreatPoints -100 → EffectiveThreat 恒 0）。
        var player = CombatantStats.Create(1000, 50, 0, 1) with
        {
            Traits = [CombatTraitRules.UnseenTraitId],
        };
        var enemy = CombatantStats.Create(100000, 80, 0, 1);
        var state = CombatEncounterState.Create(
            "unseen-trait",
            13,
            new CombatantTemplate(player),
            [new CombatantTemplate(enemy)]);

        var buff = Assert.Single(state.Player.Buffs);
        Assert.Equal(CombatTraitRules.UnseenBuffId, buff.BuffId);
        Assert.Equal(CombatTraitRules.UnseenBuffDurationSeconds, buff.ExpiresAtSeconds);
        // 威胁基数照涨但有效威胁乘区归零。
        state.Player.Threat = 500;
        Assert.Equal(0, state.Player.EffectiveThreat);
        // 无特质单位不授予。
        Assert.Empty(state.Enemies[0].Buffs);
    }

    [Fact]
    public void UnseenBuffExpiresAtTenSecondBoundary()
    {
        var player = CombatantStats.Create(100000, 50, 0, 1) with
        {
            Traits = [CombatTraitRules.UnseenTraitId],
        };
        var enemy = CombatantStats.Create(100000, 80, 0, 1);
        var state = DeterministicCombatEngine.Start(
            "unseen-expire",
            17,
            new CombatantTemplate(player),
            [new CombatantTemplate(enemy)]);

        DeterministicCombatEngine.AdvanceTo(state, 9.0);
        Assert.Single(state.Player.Buffs);
        Assert.True(state.Player.Threat > 0);
        Assert.Equal(0, state.Player.EffectiveThreat);

        // 到期时刻本身是相位调度边界：t=10 效果相位精确剔除。
        DeterministicCombatEngine.AdvanceTo(state, 10.5);
        Assert.Empty(state.Player.Buffs);
        Assert.True(state.Player.EffectiveThreat > 0);
    }

    [Fact]
    public void UnseenBuffDoesNotDuplicateOnRestore()
    {
        var player = CombatantStats.Create(100000, 50, 0, 1) with
        {
            Traits = [CombatTraitRules.UnseenTraitId],
        };
        CombatantTemplate[] enemies = [new(CombatantStats.Create(100000, 80, 0, 1))];
        var source = DeterministicCombatEngine.Start(
            "unseen-restore",
            19,
            new CombatantTemplate(player),
            enemies);
        DeterministicCombatEngine.AdvanceTo(source, 5.0);

        var restored = CombatEncounterState.Restore(
            source.EncounterId,
            source.Seed,
            new CombatantTemplate(player),
            CombatEncounterState.Snapshot(source.Player),
            enemies,
            source.Enemies.Select(CombatEncounterState.Snapshot).ToArray(),
            source.ElapsedSeconds,
            source.Outcome,
            source.Sequence,
            source.PlayerRespawnSeconds,
            source.PlayerRespawnAtSeconds);

        // 恢复只还原快照内 Buffs，不重新走开战授予：数量不翻倍。
        Assert.Single(restored.Player.Buffs);
        var expected = DeterministicCombatEngine.AdvanceTo(source, 11.0);
        var actual = DeterministicCombatEngine.AdvanceTo(restored, 11.0);
        Assert.Equal(expected.Events, actual.Events);
        Assert.Empty(restored.Player.Buffs);
    }
}
