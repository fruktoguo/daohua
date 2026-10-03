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

        Assert.Equal(3, InlineCombatTriggerRegistry.OnHitHandlerCount);
        Assert.Equal(
            [
                typeof(InlineOnHitLeechHandler),
                typeof(InlineManaLeechHandler),
                typeof(InlineThornsHandler),
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
}
