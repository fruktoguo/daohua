using System.Text.Json;

namespace Domain.Tests;

public sealed class CombatSkillTests
{
    [Fact]
    public void CastRestoreChargesManaOnceAndCooldownPreventsRecast()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        player.Loadout.AbilitySlots.Add(new("ability.heavy-slash", 1, []));
        var attributes = PlayerAttributeResolver.Resolve(player, catalog);
        var encounter = CombatEncounterState.Start("skill", 71, attributes.ToCombatant(), CombatantStats.Create(100000, 0, 20, 100));
        var mana = player.Combat.Manapoints;
        var casting = CombatSkillEngine.Advance(player, catalog, encounter, attributes, PlayerCombatSkillState.Empty, 0);
        Assert.Equal(mana - 100, player.Combat.Manapoints);
        Assert.Equal(1, CombatSkillEngine.NextBoundarySeconds(casting.State));
        var restored = JsonSerializer.Deserialize<PlayerCombatSkillState>(JsonSerializer.Serialize(casting.State))!;
        Assert.True(restored.IsValid);
        DeterministicCombatEngine.AdvanceTo(encounter, 1);
        var completed = CombatSkillEngine.Advance(player, catalog, encounter, attributes, restored, 1);
        Assert.True(Assert.Single(completed.Events).Damage > 0);
        Assert.Equal(mana - 100, player.Combat.Manapoints);
        Assert.Equal(18, Assert.Single(completed.State.Cooldowns).RemainingSeconds);
        Assert.Null(CombatSkillEngine.Advance(player, catalog, encounter, attributes, completed.State, 0).State.CastingAbilityId);
    }

    [Fact]
    public void HealingRespectsTriggerAndCapsHealthAndNoEffectAbilitiesAreRejected()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        player.Loadout.AbilitySlots.Add(new("ability.qingping-melody", 1, []));
        var attributes = PlayerAttributeResolver.Resolve(player, catalog);
        var stats = attributes.ToCombatant();
        var enemy = CombatantStats.Create(100000, 0, 0, 100);
        var full = CombatEncounterState.Start("healing", 8, stats, enemy);
        var inactive = CombatSkillEngine.Advance(player, catalog, full, attributes, PlayerCombatSkillState.Empty, 0);
        Assert.Null(inactive.State.CastingAbilityId);
        var hurt = CombatEncounterState.Restore("healing", 8, stats, enemy, 0,
            stats.MaxHitpoints - 1, enemy.MaxHitpoints, 0, 0, 0, 0, CombatOutcome.Running);
        var casting = CombatSkillEngine.Advance(player, catalog, hurt, attributes, inactive.State, 0);
        DeterministicCombatEngine.AdvanceTo(hurt, 0.5);
        var completed = CombatSkillEngine.Advance(player, catalog, hurt, attributes, casting.State, 0.5);
        Assert.Equal(1, Assert.Single(completed.Events).Healing);
        Assert.Equal(stats.MaxHitpoints, hurt.Player.Hitpoints);
        Assert.Throws<DomainException>(() => CombatSkillEngine.ValidateAbility(catalog.Abilities["ability.taunt"]));
        Assert.Throws<DomainException>(() => CombatSkillEngine.ValidateAbility(catalog.Abilities["ability.pozhen-melody"]));
    }

    private static PlayerAggregate CreatePlayer(GameDataCatalog catalog)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "技能", DateTimeOffset.UnixEpoch, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(player, catalog);
        player.Combat.MaxManapoints = 1000;
        player.Combat.Manapoints = player.Combat.MaxManapoints;
        return player;
    }
}
