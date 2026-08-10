using Xiuxian.Domain.Models;
using Xiuxian.Domain.Simulation;
using Xunit;

namespace Xiuxian.Domain.Tests;

public sealed class XiuxianCombatTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-05T00:00:00Z");

    [Fact]
    public void Combat_formulas_match_reference_shapes()
    {
        var hitChance = XiuxianCombatFormula.CalculateHitChance(15, 15);
        Assert.InRange(hitChance, 0.49, 0.51);
        Assert.Equal(100, XiuxianCombatFormula.CalculateMitigatedDamage(100, 0, 0));
        Assert.Equal(100, XiuxianCombatFormula.CalculateMitigatedDamage(101, 1, 0));
        Assert.Equal(3_000, XiuxianCombatFormula.CalculateAttackIntervalMilliseconds(3_000, 0, 0));
        Assert.True(XiuxianCombatFormula.CalculateAttackIntervalMilliseconds(3_000, 100, 0) < 3_000);
        Assert.Equal(1, XiuxianCombatFormula.CalculateRegeneration(110, 0.01));
        Assert.Equal(0, XiuxianCombatFormula.CalculateEnrageStacks(179_999, 180_000));
        Assert.Equal(1, XiuxianCombatFormula.CalculateEnrageStacks(180_000, 180_000));
        Assert.Equal(10, XiuxianCombatFormula.CalculateEnrageStacks(9_000_000, 180_000));
        Assert.Equal(0.7, XiuxianCombatFormula.CalculateControlChance(0.7, 0), precision: 10);
        Assert.Equal(0.35, XiuxianCombatFormula.CalculateControlChance(0.7, 100), precision: 10);
        Assert.Equal(0.7, XiuxianCombatFormula.CalculateControlChance(0.7, double.PositiveInfinity), precision: 10);
        Assert.Equal(25, XiuxianCombatFormula.CalculatePercentage(25, 100));
        Assert.Equal(100, XiuxianCombatFormula.CalculatePercentage(150, 100));
        Assert.Equal(0, XiuxianCombatFormula.CalculatePercentage(25, 0));

        var levelOne = XiuxianCombatCatalog.GetAbilityAtLevel(
            XiuxianCombatAbilityIds.StunningMountain,
            1);
        var levelTen = XiuxianCombatCatalog.GetAbilityAtLevel(
            XiuxianCombatAbilityIds.StunningMountain,
            10);
        Assert.True(levelTen.DamageRatio > levelOne.DamageRatio);
        Assert.True(levelTen.FlatDamage >= levelOne.FlatDamage);
    }

    [Fact]
    public void Cast_resolves_when_simulation_reaches_the_exact_millisecond_boundary()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Combat.AttackLevel = 1_000;
        state.Combat.IntelligenceLevel = 1_000;
        state.Combat.FiveElementsLevel = 1_000;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.FiveElementsFlare;

        var beforeBoundary = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromMilliseconds(699));
        var atBoundary = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromMilliseconds(700));

        Assert.Equal("cast", beforeBoundary.PlayerAction?.Kind);
        Assert.NotEqual("cast", atBoundary.PlayerAction?.Kind);
        Assert.True(atBoundary.DamageDealt > beforeBoundary.DamageDealt);
    }

    [Fact]
    public void Combat_simulation_is_deterministic_for_same_start_boundary()
    {
        var first = XiuxianSimulation.CreateDefaultState(Guid.Parse("11111111-1111-1111-1111-111111111111"), now: Baseline);
        var second = first.Clone();
        var encounter = XiuxianCombatCatalog.GetEncounter(XiuxianCombatEncounterIds.QiBeast);

        var firstResult = XiuxianCombatSimulator.Run(
            first.PlayerId,
            first.Combat,
            first.Buffs,
            first.Inventory.Items,
            encounter.Id,
            Baseline,
            TimeSpan.FromSeconds(60));
        var secondResult = XiuxianCombatSimulator.Run(
            second.PlayerId,
            second.Combat,
            second.Buffs,
            second.Inventory.Items,
            encounter.Id,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.Equal(firstResult.EncounterId, secondResult.EncounterId);
        Assert.Equal(firstResult.SimulatedMilliseconds, secondResult.SimulatedMilliseconds);
        Assert.Equal(firstResult.Encounters, secondResult.Encounters);
        Assert.Equal(firstResult.Wins, secondResult.Wins);
        Assert.Equal(firstResult.Losses, secondResult.Losses);
        Assert.Equal(firstResult.DamageDealt, secondResult.DamageDealt);
        Assert.Equal(firstResult.DamageTaken, secondResult.DamageTaken);
        Assert.Equal(firstResult.Experience, secondResult.Experience);
        Assert.Equal(firstResult.Spirit, secondResult.Spirit);
        Assert.Equal(firstResult.DroppedQuantity, secondResult.DroppedQuantity);
        Assert.Equal(firstResult.ConsumedItems, secondResult.ConsumedItems);
        Assert.Equal(firstResult.Events, secondResult.Events);
        Assert.Equal(firstResult.MaxEnrageStacks, secondResult.MaxEnrageStacks);
        Assert.Equal(firstResult.HitpointsRegenerated, secondResult.HitpointsRegenerated);
        Assert.Equal(firstResult.ManapointsRegenerated, secondResult.ManapointsRegenerated);
        Assert.Equal(firstResult.HitpointsRecoveredFromConsumables, secondResult.HitpointsRecoveredFromConsumables);
        Assert.Equal(firstResult.ManapointsRecoveredFromConsumables, secondResult.ManapointsRecoveredFromConsumables);
        Assert.Equal(firstResult.ProcessedEvents, secondResult.ProcessedEvents);
        Assert.Equal(firstResult.PlayerAction, secondResult.PlayerAction);
        Assert.Equal(firstResult.EnemyAction, secondResult.EnemyAction);
        Assert.NotNull(firstResult.PlayerAction);
        Assert.NotNull(firstResult.EnemyAction);
        Assert.InRange(
            firstResult.PlayerAction!.ElapsedMilliseconds,
            0,
            firstResult.PlayerAction.DurationMilliseconds);
        Assert.InRange(
            firstResult.EnemyAction!.ElapsedMilliseconds,
            0,
            firstResult.EnemyAction.DurationMilliseconds);
        Assert.InRange(firstResult.ProcessedEvents, 1, 1_000);
        Assert.True(firstResult.HitpointsRegenerated > 0);
        Assert.Contains(firstResult.Events, entry => entry.Contains("施放", StringComparison.Ordinal));
    }

    [Fact]
    public void Combat_consumes_food_drink_and_talisman_by_independent_slot_rules()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Inventory.Add(XiuxianItemIds.HealingElixir, 2);
        state.Inventory.Add(XiuxianItemIds.SpiritTea, 1);
        state.Inventory.Add(XiuxianItemIds.FireTalisman, 1);
        state.Combat.ConsumableSlots[0] = new XiuxianConsumableSlot
        {
            ItemId = XiuxianItemIds.HealingElixir,
            Quantity = 1,
            Enabled = true
        };
        state.Combat.ConsumableSlots[1] = new XiuxianConsumableSlot
        {
            ItemId = XiuxianItemIds.SpiritTea,
            Quantity = 1,
            Enabled = true
        };
        state.Combat.ConsumableSlots[2] = new XiuxianConsumableSlot
        {
            ItemId = XiuxianItemIds.FireTalisman,
            Quantity = 1,
            Enabled = true
        };

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.Equal(1, result.ConsumedItems[XiuxianItemIds.FireTalisman]);
        Assert.Equal(1, result.ConsumedItems[XiuxianItemIds.SpiritTea]);
        Assert.True(result.ConsumedItems.GetValueOrDefault(XiuxianItemIds.HealingElixir) <= 2);
        Assert.Contains(result.BuffApplications, buff => buff.BuffId == XiuxianBuffIds.SpiritTea);
        Assert.Contains(result.BuffApplications, buff => buff.BuffId == XiuxianBuffIds.FireTalisman);
    }

    [Fact]
    public void Combat_reports_consumable_slot_cooldowns_and_new_runs_reset_before_immediate_use()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Inventory.Add(XiuxianItemIds.FireTalisman, 2);
        state.Combat.ConsumableSlots[0] = new XiuxianConsumableSlot
        {
            ItemId = XiuxianItemIds.FireTalisman,
            Quantity = 1,
            Enabled = true
        };

        var active = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(1));
        var restarted = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline.AddMinutes(1),
            TimeSpan.Zero);

        Assert.Equal(299_000, active.PlayerConsumableCooldowns![0]);
        Assert.Equal(300_000, restarted.PlayerConsumableCooldowns![0]);
        Assert.All(restarted.PlayerConsumableCooldowns.Skip(1), remaining => Assert.Equal(0, remaining));
    }

    [Fact]
    public void Combat_recovery_consumable_is_applied_on_five_second_event_boundaries()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.Parse("44444444-4444-4444-4444-444444444444"), now: Baseline);
        state.Combat.DefenseLevel = 30;
        state.Combat.StaminaLevel = 5;
        state.Inventory.Add(XiuxianItemIds.RejuvenatingDew, 1);
        state.Combat.ConsumableSlots[0] = new XiuxianConsumableSlot
        {
            ItemId = XiuxianItemIds.RejuvenatingDew,
            Quantity = 1,
            Enabled = true
        };

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(180));

        Assert.Equal(1, result.ConsumedItems[XiuxianItemIds.RejuvenatingDew]);
        Assert.True(result.HitpointsRecoveredFromConsumables > 0);
        Assert.Equal(0, result.ManapointsRecoveredFromConsumables);
    }

    [Fact]
    public void Combat_ability_trigger_uses_custom_and_semantics_and_heal_effect()
    {
        var state = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            now: Baseline);
        state.Combat.DefenseLevel = 1;
        state.Combat.StaminaLevel = 1;
        state.Combat.AbilitySlots[0] = new XiuxianCombatAbilitySlot
        {
            AbilityId = XiuxianCombatAbilityIds.RejuvenationArt,
            Level = 1,
            Triggers =
            [
                new XiuxianCombatTrigger
                {
                    Dependency = XiuxianCombatTriggerDependency.Self,
                    Condition = XiuxianCombatTriggerCondition.MissingHitpointsPercentage,
                    Comparator = XiuxianCombatTriggerComparator.GreaterThanOrEqual,
                    Value = 1
                },
                new XiuxianCombatTrigger
                {
                    Dependency = XiuxianCombatTriggerDependency.Self,
                    Condition = XiuxianCombatTriggerCondition.CurrentManapoints,
                    Comparator = XiuxianCombatTriggerComparator.GreaterThanOrEqual,
                    Value = 60
                }
            ]
        };

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.True(result.HitpointsRecoveredFromAbilities > 0);
        Assert.Contains(result.Events, entry => entry.Contains("功法恢复", StringComparison.Ordinal));
    }

    [Fact]
    public void Combat_uses_regular_and_rare_drop_tables_as_item_maps()
    {
        var state = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            now: Baseline);
        state.Combat.AttackLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.FiveElementsLevel = 100;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.DamageType = XiuxianDamageType.Fire;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.FiveElementsFlare;

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromMinutes(10));

        Assert.True(result.Wins > 0);
        Assert.NotNull(result.DroppedItems);
        Assert.True(result.DroppedItems!.GetValueOrDefault(XiuxianItemIds.QiBeastCore) >= result.Wins);
        Assert.Contains(result.Events, entry => entry.Contains("掉落", StringComparison.Ordinal));
    }

    [Fact]
    public void Combat_drop_quantity_multiplier_scales_guaranteed_drops()
    {
        var state = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            now: Baseline);
        state.Combat.AttackLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.FiveElementsLevel = 100;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.DamageType = XiuxianDamageType.Fire;
        state.Combat.CombatDropQuantity = 1;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.FiveElementsFlare;

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromMinutes(2));

        Assert.True(result.Wins > 0);
        Assert.True(result.DroppedItems!.GetValueOrDefault(XiuxianItemIds.QiBeastCore) >= result.Wins * 2L);
    }

    [Fact]
    public void Combat_damage_over_time_is_processed_on_event_boundaries()
    {
        var state = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            now: Baseline);
        // 保持目标能活到首个 3 秒持续伤害边界，避免测试只覆盖直接伤害结算。
        state.Combat.StaminaLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.AttackLevel = 1;
        state.Combat.FiveElementsLevel = 1;
        state.Combat.IntelligenceLevel = 10;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.DamageType = XiuxianDamageType.Fire;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.BlazingStorm;

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(30));

        Assert.Contains(result.Events, entry => entry.Contains("持续伤害", StringComparison.Ordinal));
        Assert.True(result.DamageDealt > 0);
    }

    [Fact]
    public void Combat_control_effects_use_catalog_probabilities_and_expire_on_boundaries()
    {
        var sawStun = false;
        for (var seed = 1; seed <= 16 && !sawStun; seed++)
        {
            var state = XiuxianSimulation.CreateDefaultState(
                new Guid(seed, 0, 0, new byte[8]),
                now: Baseline);
            state.Combat.StaminaLevel = 100;
            state.Combat.DefenseLevel = 100;
            state.Combat.IntelligenceLevel = 10;
            state.Combat.Style = XiuxianCombatStyle.Body;
            state.Combat.DamageType = XiuxianDamageType.Physical;
            state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.StunningMountain;

            var result = XiuxianCombatSimulator.Run(
                state.PlayerId,
                state.Combat,
                state.Buffs,
                state.Inventory.Items,
                XiuxianCombatEncounterIds.QiBeast,
                Baseline,
                TimeSpan.FromSeconds(20));
            sawStun = result.Events.Any(entry => entry.Contains("定身", StringComparison.Ordinal));
        }

        Assert.True(sawStun);
    }

    [Fact]
    public void Combat_action_updates_stats_and_inventory_at_completion_boundary()
    {
        var state = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            now: Baseline);
        state.Queue.Entries.Clear();
        state.Queue.LoopEnabled = false;
        state.Combat.StaminaLevel = 100;
        state.Combat.IntelligenceLevel = 100;
        state.Combat.AttackLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.FiveElementsLevel = 100;
        XiuxianSimulation.Enqueue(state, XiuxianActionIds.HuntQiBeast);

        var result = XiuxianSimulation.AdvanceTo(state, Baseline.AddSeconds(60));

        Assert.Equal(1, result.CompletedActions);
        Assert.Equal(1, state.Stats.CompletedActions);
        Assert.True(state.Stats.CombatWins >= 0);
        Assert.True(state.Stats.CombatExperience >= 0);
        Assert.True(state.Inventory.Get(XiuxianItemIds.Spirit) >= 0);
        Assert.Empty(state.Queue.Entries);
    }

    [Fact]
    public void Stronger_loadout_can_clear_multiple_low_tier_encounters()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.Parse("22222222-2222-2222-2222-222222222222"), now: Baseline);
        state.Combat.AttackLevel = 40;
        state.Combat.DefenseLevel = 40;
        state.Combat.FiveElementsLevel = 40;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.DamageType = XiuxianDamageType.Fire;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.FiveElementsFlare;

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.True(result.Wins > 0);
        Assert.True(result.Experience >= result.Wins * 15);
    }

    [Fact]
    public void Invalid_combat_loadout_does_not_partially_mutate_state()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        var originalStyle = state.Combat.Style;
        var originalAbility = state.Combat.AbilitySlots[0].AbilityId;

        var result = XiuxianSimulation.SetCombatLoadout(
            state,
            XiuxianCombatStyle.Body,
            XiuxianDamageType.Fire,
            [new XiuxianCombatAbilitySlot
            {
                AbilityId = XiuxianCombatAbilityIds.FiveElementsFlare,
                Level = 1
            }],
            [
                new XiuxianConsumableSlot
                {
                    ItemId = XiuxianItemIds.SpiritTea,
                    Quantity = 1,
                    Enabled = true
                },
                new XiuxianConsumableSlot
                {
                    ItemId = XiuxianItemIds.SpiritHerb,
                    Quantity = 1,
                    Enabled = true
                }
            ]);

        Assert.False(result.Success);
        Assert.Equal(originalStyle, state.Combat.Style);
        Assert.Equal(originalAbility, state.Combat.AbilitySlots[0].AbilityId);
    }

    [Fact]
    public void Invalid_combat_enum_is_rejected_before_any_mutation()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        var original = state.Combat.Clone();

        var result = XiuxianSimulation.SetCombatLoadout(
            state,
            (XiuxianCombatStyle)999,
            XiuxianDamageType.Fire,
            null,
            null);

        Assert.False(result.Success);
        Assert.Equal("INVALID_COMBAT_STYLE", result.Code);
        Assert.Equal(original.Style, state.Combat.Style);
        Assert.Equal(original.DamageType, state.Combat.DamageType);
    }

    [Fact]
    public void Combat_loadout_rejects_abilities_the_player_has_not_learned()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);

        var result = XiuxianSimulation.SetCombatLoadout(
            state,
            null,
            null,
            [new XiuxianCombatAbilitySlot
            {
                AbilityId = XiuxianCombatAbilityIds.BlazingStorm,
                Level = 1
            }],
            null);

        Assert.False(result.Success);
        Assert.Equal("ABILITY_NOT_LEARNED", result.Code);
        Assert.Equal(XiuxianCombatAbilityIds.FiveElementsFlare, state.Combat.AbilitySlots[0].AbilityId);
    }

    [Fact]
    public void Manual_item_use_reuses_catalog_buff_and_inventory_rules()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Inventory.Add(XiuxianItemIds.QiGatheringPill, 2);

        var result = XiuxianSimulation.UseItem(
            state,
            XiuxianItemIds.QiGatheringPill,
            XiuxianActionType.Cultivation,
            2);

        Assert.True(result.Success);
        Assert.Equal(0, state.Inventory.Get(XiuxianItemIds.QiGatheringPill));
        Assert.Equal(2, Assert.Single(state.Buffs).Stacks);
        Assert.Equal(1.5, state.Buffs[0].QiGainMultiplier);
    }

    [Fact]
    public void Combat_fast_forward_matches_incremental_progression()
    {
        for (var horizon = 1; horizon <= 220; horizon++)
        {
            var probeFast = XiuxianSimulation.CreateDefaultState(Guid.Parse("33333333-3333-3333-3333-333333333333"), now: Baseline);
            var probeStepped = probeFast.Clone();
            probeFast.Queue.Entries.Clear();
            probeStepped.Queue.Entries.Clear();
            XiuxianSimulation.Enqueue(probeFast, XiuxianActionIds.HuntQiBeast, -1);
            XiuxianSimulation.Enqueue(probeStepped, XiuxianActionIds.HuntQiBeast, -1);
            XiuxianSimulation.AdvanceTo(probeFast, Baseline.AddSeconds(horizon));
            for (var second = 1; second <= horizon; second++)
            {
                XiuxianSimulation.AdvanceTo(probeStepped, Baseline.AddSeconds(second));
            }
            var fastExpedition = probeFast.Queue.Entries[0].CombatExpedition;
            var steppedExpedition = probeStepped.Queue.Entries[0].CombatExpedition;
            if (probeFast.Stats.CompletedActions != probeStepped.Stats.CompletedActions
                || fastExpedition?.Phase != steppedExpedition?.Phase
                || fastExpedition?.PhaseElapsedMilliseconds != steppedExpedition?.PhaseElapsedMilliseconds
                || fastExpedition?.InitialHitpoints != steppedExpedition?.InitialHitpoints
                || fastExpedition?.InitialManapoints != steppedExpedition?.InitialManapoints
                || probeFast.CombatResources.Hitpoints != probeStepped.CombatResources.Hitpoints
                || probeFast.CombatResources.Manapoints != probeStepped.CombatResources.Manapoints)
            {
                Console.WriteLine($"FIRST DIVERGENCE horizon={horizon} fast={probeFast.Stats.CompletedActions}/{fastExpedition?.Phase}/{fastExpedition?.PhaseElapsedMilliseconds}/{(fastExpedition?.BattleStartedAt - Baseline)?.TotalSeconds:F3}/{fastExpedition?.InitialHitpoints}/{fastExpedition?.InitialManapoints}/{probeFast.CombatResources.Hitpoints}/{probeFast.CombatResources.Manapoints} stepped={probeStepped.Stats.CompletedActions}/{steppedExpedition?.Phase}/{steppedExpedition?.PhaseElapsedMilliseconds}/{(steppedExpedition?.BattleStartedAt - Baseline)?.TotalSeconds:F3}/{steppedExpedition?.InitialHitpoints}/{steppedExpedition?.InitialManapoints}/{probeStepped.CombatResources.Hitpoints}/{probeStepped.CombatResources.Manapoints}");
                break;
            }
        }

        var fast = XiuxianSimulation.CreateDefaultState(Guid.Parse("33333333-3333-3333-3333-333333333333"), now: Baseline);
        var stepped = fast.Clone();
        fast.Queue.Entries.Clear();
        stepped.Queue.Entries.Clear();
        XiuxianSimulation.Enqueue(fast, XiuxianActionIds.HuntQiBeast, -1);
        XiuxianSimulation.Enqueue(stepped, XiuxianActionIds.HuntQiBeast, -1);

        XiuxianSimulation.AdvanceTo(fast, Baseline.AddSeconds(600));
        for (var second = 1; second <= 600; second++)
        {
            XiuxianSimulation.AdvanceTo(stepped, Baseline.AddSeconds(second));
        }

        Console.WriteLine($"FAST wins={fast.Stats.CombatWins} losses={fast.Stats.CombatLosses} completed={fast.Stats.CompletedActions} phase={fast.Queue.Entries[0].CombatExpedition?.Phase} phaseElapsed={fast.Queue.Entries[0].CombatExpedition?.PhaseElapsedMilliseconds} battle={fast.Queue.Entries[0].CombatExpedition?.BattleSequence} hp={fast.CombatResources.Hitpoints} mp={fast.CombatResources.Manapoints} progress={fast.Queue.Entries[0].ProgressSeconds}");
        Console.WriteLine($"STEP wins={stepped.Stats.CombatWins} losses={stepped.Stats.CombatLosses} completed={stepped.Stats.CompletedActions} phase={stepped.Queue.Entries[0].CombatExpedition?.Phase} phaseElapsed={stepped.Queue.Entries[0].CombatExpedition?.PhaseElapsedMilliseconds} battle={stepped.Queue.Entries[0].CombatExpedition?.BattleSequence} hp={stepped.CombatResources.Hitpoints} mp={stepped.CombatResources.Manapoints} progress={stepped.Queue.Entries[0].ProgressSeconds}");
        Console.WriteLine("FAST EVENTS " + string.Join(" | ", fast.StructuredEventLog.Where(entry => entry.Template.Contains("战斗开始", StringComparison.Ordinal) || entry.Template.Contains("行动完成", StringComparison.Ordinal) || entry.Template.Contains("败退", StringComparison.Ordinal) || entry.Template.Contains("击败", StringComparison.Ordinal)).Select(entry => $"{(entry.OccurredAt - Baseline).TotalSeconds:F3}:{entry.Template}")));
        Console.WriteLine("STEP EVENTS " + string.Join(" | ", stepped.StructuredEventLog.Where(entry => entry.Template.Contains("战斗开始", StringComparison.Ordinal) || entry.Template.Contains("行动完成", StringComparison.Ordinal) || entry.Template.Contains("败退", StringComparison.Ordinal) || entry.Template.Contains("击败", StringComparison.Ordinal)).Select(entry => $"{(entry.OccurredAt - Baseline).TotalSeconds:F3}:{entry.Template}")));

        Assert.Equal(stepped.Stats.CombatWins, fast.Stats.CombatWins);
        Assert.Equal(stepped.Stats.CombatLosses, fast.Stats.CombatLosses);
        Assert.Equal(stepped.Stats.CombatExperience, fast.Stats.CombatExperience);
        Assert.Equal(stepped.Stats.CombatDamageDealt, fast.Stats.CombatDamageDealt);
        Assert.Equal(stepped.Inventory.Get(XiuxianItemIds.QiBeastCore), fast.Inventory.Get(XiuxianItemIds.QiBeastCore));
        Assert.Equal(stepped.Inventory.Get(XiuxianItemIds.Spirit), fast.Inventory.Get(XiuxianItemIds.Spirit));
        Assert.Equal(stepped.Inventory.Get(XiuxianItemIds.SpiritHerb), fast.Inventory.Get(XiuxianItemIds.SpiritHerb));
        Assert.Equal(stepped.Inventory.Get(XiuxianItemIds.CloudSugar), fast.Inventory.Get(XiuxianItemIds.CloudSugar));
        Assert.Equal(stepped.Queue.Entries[0].ProgressSeconds, fast.Queue.Entries[0].ProgressSeconds);
    }

    [Fact]
    public void Equipment_catalog_uses_reference_enhancement_curve()
    {
        Assert.Equal(0, XiuxianCombatCatalog.GetEnhancementTotalBonusMultiplier(0));
        Assert.Equal(14.5, XiuxianCombatCatalog.GetEnhancementTotalBonusMultiplier(10));
        Assert.Equal(50, XiuxianCombatCatalog.GetEnhancementTotalBonusMultiplier(20));

        var stats = XiuxianCombatCatalog.AggregateEquipmentStats(
        [new XiuxianEquippedItem
        {
            ItemId = XiuxianItemIds.CursedSpiritBow,
            EnhancementLevel = 10
        }]);

        Assert.Equal(1.05 + 14.5 * 0.021, stats.AccuracyRatio, precision: 10);
        Assert.Equal(1.30 + 14.5 * 0.026, stats.DamageRatio, precision: 10);
        Assert.Equal(XiuxianCombatStyle.Talisman, stats.Style);
        Assert.Equal(XiuxianDamageType.Physical, stats.DamageType);
    }

    [Fact]
    public void Critical_and_absorption_formulas_keep_zero_values_effectless()
    {
        Assert.Equal(0, XiuxianCombatFormula.CalculateCriticalChance(0, 0));
        Assert.Equal(100, XiuxianCombatFormula.ApplyCriticalDamage(100, 0.5, false));
        Assert.Equal(100, XiuxianCombatFormula.ApplyDamageAbsorption(100, 0));
    }

    [Fact]
    public void Critical_avoidance_and_damage_absorption_apply_numeric_values()
    {
        Assert.Equal(0.3, XiuxianCombatFormula.CalculateCriticalChance(0.5, 0.2), precision: 10);
        Assert.Equal(150, XiuxianCombatFormula.ApplyCriticalDamage(100, 0.5, true));
        Assert.Equal(75, XiuxianCombatFormula.ApplyDamageAbsorption(100, 0.25));
    }

    [Fact]
    public void Invalid_equipment_layout_is_rejected_atomically()
    {
        var state = XiuxianSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        var original = state.Combat.Clone();

        var result = XiuxianSimulation.SetCombatLoadout(
            state,
            null,
            null,
            null,
            null,
            equipment:
            [
                new XiuxianEquippedItem { ItemId = XiuxianItemIds.CursedSpiritBow },
                new XiuxianEquippedItem { ItemId = XiuxianItemIds.FuriousSpiritSpear }
            ]);

        Assert.False(result.Success);
        Assert.Equal("INVALID_EQUIPMENT", result.Code);
        Assert.Empty(state.Combat.Equipment);
        Assert.Equal(original.Style, state.Combat.Style);
    }

    [Fact]
    public void Equipment_affixes_are_applied_as_combat_status_events()
    {
        var state = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            now: Baseline);
        state.Combat.AttackLevel = 100;
        state.Combat.TalismanLevel = 100;
        state.Combat.DefenseLevel = 100;
        state.Combat.StaminaLevel = 100;
        state.Combat.Equipment =
        [new XiuxianEquippedItem
        {
            ItemId = XiuxianItemIds.CursedSpiritBow,
            EnhancementLevel = 5
        }];

        var result = XiuxianCombatSimulator.Run(
            state.PlayerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.QiBeast,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.Contains(result.Events, entry => entry.Contains("蚀心", StringComparison.Ordinal));
        Assert.True(result.DamageDealt > 0);
    }

    [Fact]
    public void Equipment_combat_fast_forward_matches_stepped_progression()
    {
        var fast = XiuxianSimulation.CreateDefaultState(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), now: Baseline);
        var stepped = fast.Clone();
        foreach (var state in new[] { fast, stepped })
        {
            state.Queue.Entries.Clear();
            state.Queue.LoopEnabled = true;
            state.Combat.AttackLevel = 60;
            state.Combat.TalismanLevel = 60;
            state.Combat.DefenseLevel = 60;
            state.Combat.Equipment =
            [new XiuxianEquippedItem
            {
                ItemId = XiuxianItemIds.SunderingSpiritCrossbow,
                EnhancementLevel = 10
            }];
            XiuxianSimulation.Enqueue(state, XiuxianActionIds.HuntQiBeast, -1);
        }

        XiuxianSimulation.AdvanceTo(fast, Baseline.AddMinutes(3));
        for (var second = 1; second <= 180; second++)
        {
            XiuxianSimulation.AdvanceTo(stepped, Baseline.AddSeconds(second));
        }

        Assert.Equal(stepped.Stats.CombatWins, fast.Stats.CombatWins);
        Assert.Equal(stepped.Stats.CombatLosses, fast.Stats.CombatLosses);
        Assert.Equal(stepped.Stats.CombatDamageDealt, fast.Stats.CombatDamageDealt);
        Assert.Equal(stepped.Inventory.Get(XiuxianItemIds.QiBeastCore), fast.Inventory.Get(XiuxianItemIds.QiBeastCore));
        Assert.Equal(stepped.Queue.Entries[0].ProgressSeconds, fast.Queue.Entries[0].ProgressSeconds);
    }

    [Fact]
    public void Multi_enemy_zone_uses_weighted_strength_limited_spawn_rules()
    {
        var playerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var state = XiuxianSimulation.CreateDefaultState(playerId, now: Baseline);

        var first = XiuxianCombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.Zero);
        var second = XiuxianCombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.Zero);

        var encounter = XiuxianCombatCatalog.GetEncounter(XiuxianCombatEncounterIds.TwilightValley);
        var strengthByEnemy = encounter.RandomSpawns.ToDictionary(spawn => spawn.Enemy.Id, spawn => spawn.Strength);
        Assert.NotNull(first.FinalEnemies);
        Assert.NotNull(second.FinalEnemies);
        var enemies = first.FinalEnemies!;
        Assert.InRange(enemies.Count, 2, encounter.MaxSpawnCount);
        Assert.True(enemies.Sum(enemy => strengthByEnemy[enemy.EnemyId]) <= encounter.MaxTotalStrength);
        Assert.Equal(
            enemies.Select(enemy => enemy.EnemyId),
            second.FinalEnemies!.Select(enemy => enemy.EnemyId));
        Assert.Equal(first.PlayerAction, second.PlayerAction);
        Assert.Equal(first.EnemyAction, second.EnemyAction);
        Assert.NotNull(first.PlayerAction);
        Assert.NotNull(first.EnemyAction);
    }

    [Fact]
    public void Multi_enemy_zone_spawns_boss_on_every_tenth_encounter()
    {
        var playerId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var state = XiuxianSimulation.CreateDefaultState(playerId, now: Baseline);

        var result = XiuxianCombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.Zero,
            completedEncountersBeforeRun: 9);

        Assert.NotNull(result.FinalEnemies);
        var enemy = Assert.Single(result.FinalEnemies!);
        Assert.Equal("enemy.twilight-valley-master", enemy.EnemyId);
    }

    [Fact]
    public void All_enemies_ability_resolves_against_the_whole_alive_group()
    {
        var playerId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var state = XiuxianSimulation.CreateDefaultState(playerId, now: Baseline);
        state.Combat.StaminaLevel = 1_000_000;
        state.Combat.IntelligenceLevel = 1_000_000;
        state.Combat.AttackLevel = 1_000_000;
        state.Combat.FiveElementsLevel = 1_000_000;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.BlazingStorm;
        state.Combat.AbilitySlots[0].Level = 1;

        var result = XiuxianCombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.FromMilliseconds(2_100));

        Assert.NotNull(result.FinalEnemies);
        Assert.True(result.FinalEnemies!.Count >= 2);
        Assert.All(result.FinalEnemies, enemy => Assert.True(enemy.IsDefeated));
        Assert.Equal(result.FinalEnemies.Count, result.EnemiesDefeated);
        Assert.Equal(1, result.Wins);
    }

    [Fact]
    public void Cleared_multi_enemy_zone_advances_to_respawn_without_same_timestamp_spin()
    {
        var playerId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var state = XiuxianSimulation.CreateDefaultState(playerId, now: Baseline);
        state.Combat.StaminaLevel = 1_000;
        state.Combat.IntelligenceLevel = 1_000;
        state.Combat.AttackLevel = 1_000;
        state.Combat.DefenseLevel = 1_000;
        state.Combat.FiveElementsLevel = 1_000;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.BlazingStorm;
        state.Combat.AbilitySlots[0].Level = 100;

        var result = XiuxianCombatSimulator.Run(
            playerId,
            state.Combat,
            state.Buffs,
            state.Inventory.Items,
            XiuxianCombatEncounterIds.TwilightValley,
            Baseline,
            TimeSpan.FromSeconds(60));

        Assert.True(result.Wins > 1);
        Assert.InRange(result.ProcessedEvents, 1, 1_000);
    }

    [Fact]
    public void Control_expiration_during_group_respawn_does_not_wake_player_action()
    {
        Span<byte> playerBytes = stackalloc byte[16];
        playerBytes.Clear();
        BitConverter.TryWriteBytes(playerBytes, 8L);
        playerBytes[15] = 0x7A;
        var state = XiuxianSimulation.CreateDefaultState(new Guid(playerBytes), now: Baseline);
        state.Queue.Entries.Clear();
        state.Queue.LoopEnabled = true;
        state.Combat.StaminaLevel = 1_000;
        state.Combat.IntelligenceLevel = 1_000;
        state.Combat.AttackLevel = 1_000;
        state.Combat.SwordsmanshipLevel = 1_000;
        state.Combat.DefenseLevel = 1_000;
        state.Combat.TalismanLevel = 1_000;
        state.Combat.FiveElementsLevel = 1_000;
        state.Combat.Style = XiuxianCombatStyle.FiveElements;
        state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.BlazingStorm;
        state.Combat.AbilitySlots[0].Level = 100;
        state.Combat.Equipment =
        [
            new XiuxianEquippedItem
            {
                ItemId = XiuxianItemIds.SunderingSpiritCrossbow,
                EnhancementLevel = 7
            }
        ];
        state.World.NodeId = XiuxianWorldNodeIds.TwilightPass;
        XiuxianSimulation.Enqueue(state, XiuxianActionIds.SweepTwilightValley, -1);

        var result = XiuxianSimulation.AdvanceTo(state, Baseline.AddHours(1));

        // 新的重新搜索节奏会提高遭遇次数，但控制过期不能造成无界唤醒或事件爆炸。
        Assert.InRange(result.CompletedActions, 200, 400);
        Assert.InRange(result.CombatEvents, 1, 10_000);
        Assert.True(state.Stats.CombatWins > 0);
    }

    [Fact]
    public void Multi_enemy_combat_fast_forward_matches_incremental_progression()
    {
        var fast = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            now: Baseline);
        var stepped = fast.Clone();
        foreach (var state in new[] { fast, stepped })
        {
            state.Queue.Entries.Clear();
            state.Queue.LoopEnabled = true;
            state.Combat.StaminaLevel = 1_000;
            state.Combat.IntelligenceLevel = 1_000;
            state.Combat.AttackLevel = 1_000;
            state.Combat.DefenseLevel = 1_000;
            state.Combat.FiveElementsLevel = 1_000;
            state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.BlazingStorm;
            state.Combat.AbilitySlots[0].Level = 100;
            state.World.NodeId = XiuxianWorldNodeIds.TwilightPass;
            XiuxianSimulation.Enqueue(state, XiuxianActionIds.SweepTwilightValley, -1);
        }

        XiuxianSimulation.AdvanceTo(fast, Baseline.AddMinutes(4));
        for (var second = 1; second <= 240; second++)
        {
            XiuxianSimulation.AdvanceTo(stepped, Baseline.AddSeconds(second));
        }

        Assert.Equal(
            stepped.CombatEncounterCompletions.GetValueOrDefault(XiuxianCombatEncounterIds.TwilightValley),
            fast.CombatEncounterCompletions.GetValueOrDefault(XiuxianCombatEncounterIds.TwilightValley));
        Assert.Equal(stepped.Stats.CombatWins, fast.Stats.CombatWins);
        Assert.Equal(stepped.Stats.CombatLosses, fast.Stats.CombatLosses);
        Assert.Equal(stepped.Stats.CombatEnemiesDefeated, fast.Stats.CombatEnemiesDefeated);
        Assert.Equal(stepped.Stats.CombatExperience, fast.Stats.CombatExperience);
        Assert.Equal(stepped.Stats.CombatDamageDealt, fast.Stats.CombatDamageDealt);
        Assert.Equal(stepped.Inventory.Get(XiuxianItemIds.Spirit), fast.Inventory.Get(XiuxianItemIds.Spirit));
        Assert.Equal(stepped.Queue.Entries[0].ProgressSeconds, fast.Queue.Entries[0].ProgressSeconds);
    }

    [Fact]
    public void Combat_fast_forward_matches_fine_grained_online_progression()
    {
        var fast = XiuxianSimulation.CreateDefaultState(
            Guid.Parse("abababab-abab-abab-abab-abababababab"),
            now: Baseline);
        var stepped = fast.Clone();
        foreach (var state in new[] { fast, stepped })
        {
            state.Queue.Entries.Clear();
            state.Queue.LoopEnabled = true;
            state.Combat.StaminaLevel = 100;
            state.Combat.IntelligenceLevel = 100;
            state.Combat.AttackLevel = 100;
            state.Combat.DefenseLevel = 100;
            state.Combat.FiveElementsLevel = 100;
            state.Combat.Style = XiuxianCombatStyle.FiveElements;
            state.Combat.AbilitySlots[0].AbilityId = XiuxianCombatAbilityIds.FiveElementsFlare;
            XiuxianSimulation.Enqueue(state, XiuxianActionIds.HuntQiBeast, -1);
        }

        XiuxianSimulation.AdvanceTo(fast, Baseline.AddSeconds(30));
        for (var elapsedMilliseconds = 100; elapsedMilliseconds <= 30_000; elapsedMilliseconds += 100)
        {
            XiuxianSimulation.AdvanceTo(stepped, Baseline.AddMilliseconds(elapsedMilliseconds));
        }

        Assert.Equal(fast.Stats.CombatWins, stepped.Stats.CombatWins);
        Assert.Equal(fast.Stats.CombatLosses, stepped.Stats.CombatLosses);
        Assert.Equal(fast.Stats.CombatDamageDealt, stepped.Stats.CombatDamageDealt);
        Assert.Equal(fast.Stats.CombatDamageTaken, stepped.Stats.CombatDamageTaken);
        Assert.Equal(fast.CombatResources.Hitpoints, stepped.CombatResources.Hitpoints);
        Assert.Equal(fast.CombatResources.Manapoints, stepped.CombatResources.Manapoints);
        Assert.Equal(
            fast.Queue.Entries[0].CombatExpedition?.PhaseElapsedMilliseconds,
            stepped.Queue.Entries[0].CombatExpedition?.PhaseElapsedMilliseconds);
    }
}
