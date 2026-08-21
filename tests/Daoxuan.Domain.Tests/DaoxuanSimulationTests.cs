using System.Text.Json;
using Daoxuan.Domain.Models;
using Daoxuan.Domain.Simulation;
using Xunit;

namespace Daoxuan.Domain.Tests;

public sealed class DaoxuanSimulationTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-05T00:00:00Z");

    [Fact]
    public void Inventory_removes_item_entry_when_quantity_reaches_zero()
    {
        var inventory = new DaoxuanInventoryState();
        inventory.Add(DaoxuanItemIds.GatheringIncense, 1);

        inventory.Add(DaoxuanItemIds.GatheringIncense, -1);

        Assert.Equal(0, inventory.Get(DaoxuanItemIds.GatheringIncense));
        Assert.DoesNotContain(DaoxuanItemIds.GatheringIncense, inventory.Items.Keys);
    }

    [Fact]
    public void Normalize_state_removes_legacy_zero_inventory_entries_without_clearing_item_slots()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "归零修士", Baseline);
        state.Inventory.Items[DaoxuanItemIds.QiGatheringPill] = 0;
        state.DirtySections = DaoxuanDirtySections.None;

        var changed = DaoxuanSimulation.NormalizeState(state);

        Assert.True(changed);
        Assert.DoesNotContain(DaoxuanItemIds.QiGatheringPill, state.Inventory.Items.Keys);
        Assert.Equal(
            DaoxuanItemIds.QiGatheringPill,
            state.Queue.AutoUseSlots[DaoxuanActionType.Cultivation][0].ItemId);
        Assert.True(state.DirtySections.HasFlag(DaoxuanDirtySections.Inventory));
    }

    [Fact]
    public void Timed_action_preserves_millisecond_progress_and_completes_at_exact_deadline()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Queue.Entries.Clear();
        state.Queue.LoopEnabled = false;
        DaoxuanSimulation.Enqueue(state, DaoxuanActionIds.GatherHerbs);

        DaoxuanSimulation.AdvanceTo(state, Baseline);
        var deadline = DaoxuanSimulation.GetNextTransitionAt(state);
        Assert.Equal(Baseline.AddSeconds(8), deadline);

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddMilliseconds(3_456));
        Assert.Equal(3_456, state.Queue.Entries[0].ProgressMilliseconds);
        Assert.Equal(3, state.Queue.Entries[0].ProgressSeconds);

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddMilliseconds(7_999));
        Assert.Single(state.Queue.Entries);
        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(8));
        Assert.Empty(state.Queue.Entries);
        Assert.Equal(1, state.Stats.CompletedActions);
    }

    [Fact]
    public void Initial_queue_completes_gather_alchemy_cultivation_loop()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "测试修士", Baseline);

        var result = DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(30));

        Assert.Equal(3, result.CompletedActions);
        Assert.Equal(3, state.Stats.CompletedActions);
        Assert.Equal(3, state.Stats.HerbsCollected);
        Assert.Equal(1, state.Stats.PillsCrafted);
        Assert.Equal(1, state.Stats.CultivationCycles);
        Assert.Equal(13, state.Stats.Qi);
        Assert.Equal(state.Stats.Qi, state.Inventory.Get(DaoxuanItemIds.Qi));
        Assert.Equal(0, state.Inventory.Get(DaoxuanItemIds.SpiritHerb));
        Assert.Equal(0, state.Inventory.Get(DaoxuanItemIds.QiGatheringPill));
        Assert.Single(state.Buffs);
        Assert.Equal(3, state.Queue.Entries.Count);
        Assert.Equal(DaoxuanActionIds.GatherHerbs, state.Queue.Entries[0].ActionId);
    }

    [Fact]
    public void Log_events_keep_stable_sequence_and_full_display_time()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "日志修士", Baseline);

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(8));

        Assert.Equal(2, state.StructuredEventLog.Count);
        Assert.Equal([1L, 2L], state.StructuredEventLog.Select(entry => entry.Sequence));
        Assert.Equal(Baseline, state.StructuredEventLog[0].OccurredAt);
        Assert.StartsWith("[08-05 00:00:00]", state.EventLog[0], StringComparison.Ordinal);
        Assert.Contains("{{action:explore.gather-herbs|采集草药}}", state.StructuredEventLog[1].Template, StringComparison.Ordinal);
    }

    [Fact]
    public void Offline_progress_keeps_history_without_queuing_websocket_events()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "离线日志修士", Baseline);
        state.PendingEvents.Clear();
        state.PresenceMode = DaoxuanPresenceMode.IdleOffline;

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(30));

        Assert.Empty(state.PendingEvents);
        Assert.NotEmpty(state.StructuredEventLog);
        Assert.Contains(
            state.StructuredEventLog,
            entry => entry.Template.Contains("行动完成", StringComparison.Ordinal));
    }

    [Fact]
    public void Completed_loop_action_moves_to_queue_tail_immediately()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "轮转修士", Baseline);
        var completedEntryId = state.Queue.Entries[0].EntryId;

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(8));

        Assert.Equal(
            [DaoxuanActionIds.RefineQiPill, DaoxuanActionIds.DrawQi, DaoxuanActionIds.GatherHerbs],
            state.Queue.Entries.Select(entry => entry.ActionId));
        Assert.Equal(completedEntryId, state.Queue.Entries[^1].EntryId);
        Assert.Equal(1, state.Queue.Entries[^1].RemainingRepeats);
        Assert.Equal(0, state.Queue.Entries[^1].ProgressSeconds);
    }

    [Fact]
    public void Repeated_loop_action_finishes_its_batch_before_moving_to_tail()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "批次修士", Baseline);
        state.Queue.Entries =
        [
            new DaoxuanQueuedAction
            {
                ActionId = DaoxuanActionIds.GatherHerbs,
                TotalRepeats = 2,
                RemainingRepeats = 2,
                DurationSeconds = DaoxuanActionCatalog.Get(DaoxuanActionIds.GatherHerbs).DurationSeconds
            },
            new DaoxuanQueuedAction
            {
                ActionId = DaoxuanActionIds.DrawQi,
                TotalRepeats = 1,
                RemainingRepeats = 1,
                DurationSeconds = DaoxuanActionCatalog.Get(DaoxuanActionIds.DrawQi).DurationSeconds
            }
        ];

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(8));
        Assert.Equal(DaoxuanActionIds.GatherHerbs, state.Queue.Entries[0].ActionId);
        Assert.Equal(1, state.Queue.Entries[0].RemainingRepeats);

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(16));
        Assert.Equal(DaoxuanActionIds.DrawQi, state.Queue.Entries[0].ActionId);
        Assert.Equal(DaoxuanActionIds.GatherHerbs, state.Queue.Entries[1].ActionId);
        Assert.Equal(2, state.Queue.Entries[1].RemainingRepeats);
    }

    [Fact]
    public void Infinite_action_is_accepted_and_stays_at_queue_head()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "无限修士", Baseline);
        state.Queue.Entries.Clear();

        var command = DaoxuanSimulation.Enqueue(state, DaoxuanActionIds.GatherHerbs, -1);
        var result = DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(16));

        Assert.True(command.Success);
        Assert.Equal(2, result.CompletedActions);
        Assert.Single(state.Queue.Entries);
        Assert.Equal(-1, state.Queue.Entries[0].TotalRepeats);
        Assert.Equal(-1, state.Queue.Entries[0].RemainingRepeats);
        Assert.Equal(0, state.Queue.Entries[0].ProgressSeconds);
    }

    [Fact]
    public void Starting_combat_interrupts_current_loop_and_runs_immediately()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "历练修士", Baseline);
        var previousHead = state.Queue.Entries[0];
        previousHead.Status = DaoxuanActionStatus.Running;
        previousHead.ProgressSeconds = 3;

        var command = DaoxuanSimulation.StartCombat(state, DaoxuanActionIds.HuntQiBeast, 3);

        Assert.True(command.Success);
        Assert.Equal(DaoxuanActionIds.HuntQiBeast, state.Queue.Entries[0].ActionId);
        Assert.Equal(3, state.Queue.Entries[0].TotalRepeats);
        Assert.Equal(3, state.Queue.Entries[0].RemainingRepeats);
        Assert.Equal(DaoxuanActionStatus.Queued, previousHead.Status);
        Assert.Equal(3, previousHead.ProgressSeconds);

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(1));

        Assert.Equal(DaoxuanActionStatus.Running, state.Queue.Entries[0].Status);
        Assert.Equal(1, state.Queue.Entries[0].ProgressSeconds);
    }

    [Fact]
    public void Combat_expedition_keeps_fixed_initial_search_and_resources_between_encounters()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "连续历练修士", Baseline);
        state.Queue.Entries.Clear();
        state.Combat.StaminaLevel = 1_000;
        state.Combat.IntelligenceLevel = 1_000;
        state.Combat.AttackLevel = 1_000;
        state.Combat.DefenseLevel = 1_000;
        state.Combat.SwordsmanshipLevel = 1_000;
        state.Combat.TalismanLevel = 1_000;
        state.Combat.SearchSpeedRatio = 1;
        state.Combat.LearnedAbilityIds.Add(DaoxuanCombatAbilityIds.HeavySlash);
        state.Combat.AbilitySlots[0].AbilityId = DaoxuanCombatAbilityIds.HeavySlash;
        state.CombatResources = new DaoxuanCombatResourceState
        {
            Initialized = true,
            Hitpoints = 50,
            Manapoints = 40
        };

        var command = DaoxuanSimulation.StartCombat(state, DaoxuanActionIds.HuntQiBeast, 2);
        Assert.True(command.Success);
        state.CombatResources.Hitpoints = 50;
        state.CombatResources.Manapoints = 40;

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(19));
        var action = state.Queue.Entries[0];
        var expedition = Assert.IsType<DaoxuanCombatExpeditionState>(action.CombatExpedition);
        Assert.Equal(DaoxuanCombatExpeditionPhase.InitialSearch, expedition.Phase);
        Assert.Equal(20_000, expedition.PhaseDurationMilliseconds);
        Assert.Equal(0, expedition.BattleSequence);

        var expectedInitialPlayer = DaoxuanCombatSimulator.CreatePlayerState(state.Combat, state.Buffs, state.LastSimulatedAt);
        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(20));
        Assert.Equal(DaoxuanCombatExpeditionPhase.Combat, expedition.Phase);
        Assert.Equal(1, expedition.BattleSequence);
        // 初始搜寻阶段（20秒）自然回复生效
        Assert.True(expedition.InitialHitpoints >= 50);
        Assert.True(expedition.InitialManapoints >= 40);
        Assert.Equal(0, action.DurationSeconds);

        for (var seconds = 21; seconds <= 130 && expedition.Phase == DaoxuanCombatExpeditionPhase.Combat; seconds++)
        {
            DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(seconds));
        }

        Assert.Equal(DaoxuanCombatExpeditionPhase.Search, expedition.Phase);
        Assert.Equal(2_500, expedition.PhaseDurationMilliseconds);
        Assert.Equal(1, action.RemainingRepeats);
        var retainedHitpoints = state.CombatResources.Hitpoints;
        var retainedManapoints = state.CombatResources.Manapoints;

        var remainingSearchMilliseconds = expedition.PhaseDurationMilliseconds - expedition.PhaseElapsedMilliseconds;
        DaoxuanSimulation.AdvanceTo(state, state.LastSimulatedAt.AddMilliseconds(remainingSearchMilliseconds + 1_000));

        Assert.Equal(DaoxuanCombatExpeditionPhase.Combat, expedition.Phase);
        Assert.Equal(2, expedition.BattleSequence);
        // 第二次遭遇开始时，生命和灵力享受了搜寻阶段的自然回复
        Assert.True(expedition.InitialHitpoints >= retainedHitpoints);
        Assert.True(expedition.InitialManapoints >= retainedManapoints);
    }

    [Fact]
    public void Restarting_combat_preserves_resources_and_starts_a_fresh_battle_timeline()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "重开历练修士", Baseline);
        state.Queue.Entries.Clear();
        state.CombatResources = new DaoxuanCombatResourceState
        {
            Initialized = true,
            Hitpoints = 10,
            Manapoints = 20
        };

        Assert.True(DaoxuanSimulation.StartCombat(state, DaoxuanActionIds.HuntQiBeast, 1).Success);

        Assert.Equal(10, state.CombatResources.Hitpoints);
        Assert.Equal(20, state.CombatResources.Manapoints);
        var expedition = Assert.IsType<DaoxuanCombatExpeditionState>(state.Queue.Entries[0].CombatExpedition);
        Assert.Equal(DaoxuanCombatExpeditionPhase.InitialSearch, expedition.Phase);
        Assert.Equal(0, expedition.BattleSequence);
        Assert.Equal(default, expedition.BattleStartedAt);
        Assert.Empty(expedition.AppliedConsumedItems);
    }

    [Fact]
    public void Restarting_during_death_replaces_action_and_revives_player_with_one_hp()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "死亡重开修士", Baseline);
        state.Queue.Entries.Clear();
        Assert.True(DaoxuanSimulation.StartCombat(state, DaoxuanActionIds.HuntQiBeast, 2).Success);
        var oldAction = state.Queue.Entries[0];
        oldAction.Status = DaoxuanActionStatus.Running;
        var oldExpedition = Assert.IsType<DaoxuanCombatExpeditionState>(oldAction.CombatExpedition);
        oldExpedition.Phase = DaoxuanCombatExpeditionPhase.Death;
        oldExpedition.PhaseElapsedMilliseconds = 60_000;
        oldExpedition.PhaseDurationMilliseconds = 180_000;
        state.CombatResources.Hitpoints = 0;
        state.CombatResources.Manapoints = 15;

        Assert.True(DaoxuanSimulation.StartCombat(state, DaoxuanActionIds.HuntQiBeast, 1).Success);

        Assert.DoesNotContain(state.Queue.Entries, entry => entry.EntryId == oldAction.EntryId);
        var restarted = state.Queue.Entries[0];
        var restartedExpedition = Assert.IsType<DaoxuanCombatExpeditionState>(restarted.CombatExpedition);
        Assert.Equal(DaoxuanCombatExpeditionPhase.InitialSearch, restartedExpedition.Phase);
        Assert.Equal(0, restartedExpedition.PhaseElapsedMilliseconds);
        // 离开上一场战斗死亡状态复活：生命值回复到 1 点，灵力不回复（保持 15 点），保证不死
        Assert.Equal(1, state.CombatResources.Hitpoints);
        Assert.Equal(15, state.CombatResources.Manapoints);
    }

    [Fact]
    public void Combat_action_consumes_the_exact_next_event_deadline_without_extra_delay()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "准时出手修士", Baseline);
        state.Queue.Entries.Clear();
        Assert.True(DaoxuanSimulation.StartCombat(state, DaoxuanActionIds.HuntQiBeast, 1).Success);

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(20));
        var action = state.Queue.Entries[0];
        var expedition = Assert.IsType<DaoxuanCombatExpeditionState>(action.CombatExpedition);
        Assert.Equal(DaoxuanCombatExpeditionPhase.Combat, expedition.Phase);

        var loadout = Assert.IsType<DaoxuanCombatLoadout>(expedition.BattleLoadout);
        var initialPlayer = DaoxuanCombatSimulator.CreatePlayerState(
            loadout,
            expedition.BattleBuffs ?? [],
            expedition.BattleStartedAt,
            expedition.InitialHitpoints,
            expedition.InitialManapoints);
        var preview = DaoxuanCombatSimulator.Run(
            state.PlayerId,
            loadout,
            expedition.BattleBuffs ?? [],
            expedition.BattleInventory ?? new Dictionary<string, long>(StringComparer.Ordinal),
            DaoxuanCombatEncounterIds.QiBeast,
            expedition.BattleStartedAt,
            TimeSpan.Zero,
            expedition.CompletedEncountersBeforeBattle,
            initialPlayer,
            stopAfterFirstOutcome: true);
        var nextEventMilliseconds = Assert.IsType<long>(preview.NextEventMilliseconds);
        var exactDeadline = expedition.BattleStartedAt.AddMilliseconds(nextEventMilliseconds);

        Assert.Equal(exactDeadline, DaoxuanSimulation.GetNextTransitionAt(state));

        DaoxuanSimulation.AdvanceTo(state, exactDeadline.AddMilliseconds(-1));
        Assert.Equal(exactDeadline, DaoxuanSimulation.GetNextTransitionAt(state));

        DaoxuanSimulation.AdvanceTo(state, exactDeadline);

        Assert.Equal(exactDeadline, state.LastSimulatedAt);
        var followingDeadline = DaoxuanSimulation.GetNextTransitionAt(state);
        Assert.True(followingDeadline is null || followingDeadline > exactDeadline);
    }

    [Fact]
    public void Player_death_keeps_the_same_wave_until_full_respawn_and_victory()
    {
        var state = DaoxuanSimulation.CreateDefaultState(
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            "败退修士",
            Baseline);
        state.Queue.Entries.Clear();
        state.Combat.AttackLevel = 1;
        state.Combat.SwordsmanshipLevel = 1;

        var command = DaoxuanSimulation.StartCombat(state, DaoxuanActionIds.HuntQiBeast, 2);
        Assert.True(command.Success);

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(20));
        var action = state.Queue.Entries[0];
        var expedition = Assert.IsType<DaoxuanCombatExpeditionState>(action.CombatExpedition);
        var encounterId = Assert.IsType<string>(expedition.EncounterId);
        var battleSequence = expedition.BattleSequence;
        expedition.InitialHitpoints = 0;
        expedition.EmittedEventCount = 0;
        state.CombatResources.Hitpoints = 0;

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddMilliseconds(20_001));

        Assert.Equal(DaoxuanCombatExpeditionPhase.Combat, expedition.Phase);
        Assert.Equal(encounterId, expedition.EncounterId);
        Assert.Equal(battleSequence, expedition.BattleSequence);
        Assert.Equal(2, action.RemainingRepeats);
        Assert.Equal(1, state.Stats.CombatLosses);
        Assert.Equal(0, state.CombatResources.Hitpoints);
        var respawnAt = expedition.BattleStartedAt.AddSeconds(180);

        DaoxuanSimulation.AdvanceTo(state, respawnAt.AddMilliseconds(-1));
        Assert.Equal(DaoxuanCombatExpeditionPhase.Combat, expedition.Phase);
        Assert.Equal(2, action.RemainingRepeats);
        Assert.Equal(0, state.CombatResources.Hitpoints);

        state.Combat.AttackLevel = 1_000;
        state.Combat.DefenseLevel = 1_000;
        state.Combat.StaminaLevel = 1_000;
        state.Combat.SwordsmanshipLevel = 1_000;
        state.Combat.LearnedAbilityIds.Add(DaoxuanCombatAbilityIds.HeavySlash);
        state.Combat.AbilitySlots[0].AbilityId = DaoxuanCombatAbilityIds.HeavySlash;
        expedition.BattleLoadout = state.Combat.Clone();

        DaoxuanSimulation.AdvanceTo(state, respawnAt.AddSeconds(1));

        Assert.Equal(DaoxuanCombatExpeditionPhase.Combat, expedition.Phase);
        Assert.Equal(2, action.RemainingRepeats);
        Assert.Contains(state.StructuredEventLog, entry => entry.Template.Contains("战斗复起", StringComparison.Ordinal));
        var restored = DaoxuanCombatSimulator.CreatePlayerState(state.Combat, state.Buffs, state.LastSimulatedAt);
        Assert.InRange(state.CombatResources.Hitpoints, 1, restored.MaxHitpoints);
        Assert.InRange(state.CombatResources.Manapoints, 1, restored.MaxManapoints);

        for (var seconds = 2; seconds <= 60 && expedition.Phase == DaoxuanCombatExpeditionPhase.Combat; seconds++)
        {
            DaoxuanSimulation.AdvanceTo(state, respawnAt.AddSeconds(seconds));
        }

        Assert.Equal(1, state.Stats.CombatLosses);
        Assert.Equal(DaoxuanCombatExpeditionPhase.Search, expedition.Phase);
        Assert.Equal(1, action.RemainingRepeats);
        Assert.True(state.CombatResources.Hitpoints > 0);
    }

    [Fact]
    public void Legacy_combat_recovery_phase_migrates_without_restoring_resources()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "旧档修士", Baseline);
        state.Queue.Entries.Clear();
        state.CombatResources = new DaoxuanCombatResourceState
        {
            Initialized = true,
            Hitpoints = 3,
            Manapoints = 4
        };
        Assert.True(DaoxuanSimulation.StartCombat(state, DaoxuanActionIds.HuntQiBeast, 2).Success);
        state.CombatResources.Hitpoints = 3;
        state.CombatResources.Manapoints = 4;
        var action = state.Queue.Entries[0];
        action.Status = DaoxuanActionStatus.Running;
        var expedition = Assert.IsType<DaoxuanCombatExpeditionState>(action.CombatExpedition);
        expedition.Phase = DaoxuanCombatExpeditionPhase.Recovery;
        expedition.PhaseElapsedMilliseconds = 2_000;
        expedition.PhaseDurationMilliseconds = 150_000;

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(1));

        Assert.Equal(DaoxuanCombatExpeditionPhase.Search, expedition.Phase);
        Assert.Equal(5_000, expedition.PhaseDurationMilliseconds);
        // 迁移到 Search 阶段且经过 1 秒自然回复，数值不会瞬间回满，但保持自然增长
        Assert.True(state.CombatResources.Hitpoints >= 3);
        Assert.True(state.CombatResources.Manapoints >= 4);
        Assert.DoesNotContain(state.StructuredEventLog, entry => entry.Template.Contains("调息完成", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_loop_snapshot_restores_completed_prefix_to_queue_tail()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "旧档修士", Baseline);
        var currentId = Guid.NewGuid();
        state.Queue.Entries =
        [
            new DaoxuanQueuedAction
            {
                EntryId = currentId,
                ActionId = DaoxuanActionIds.RefineQiPill,
                RemainingRepeats = 2,
                ProgressSeconds = 4,
                DurationSeconds = 12,
                Status = DaoxuanActionStatus.Running
            },
            new DaoxuanQueuedAction
            {
                ActionId = DaoxuanActionIds.DrawQi,
                RemainingRepeats = 1,
                DurationSeconds = 10
            }
        ];
        state.Queue.LoopTemplate =
        [
            new DaoxuanQueuedAction
            {
                ActionId = DaoxuanActionIds.GatherHerbs,
                RemainingRepeats = 2,
                DurationSeconds = 8
            },
            new DaoxuanQueuedAction
            {
                ActionId = DaoxuanActionIds.RefineQiPill,
                RemainingRepeats = 3,
                DurationSeconds = 12
            },
            new DaoxuanQueuedAction
            {
                ActionId = DaoxuanActionIds.DrawQi,
                RemainingRepeats = 1,
                DurationSeconds = 10
            }
        ];

        var changed = DaoxuanSimulation.NormalizeState(state);

        Assert.True(changed);
        Assert.Empty(state.Queue.LoopTemplate);
        Assert.Equal(
            [DaoxuanActionIds.RefineQiPill, DaoxuanActionIds.DrawQi, DaoxuanActionIds.GatherHerbs],
            state.Queue.Entries.Select(entry => entry.ActionId));
        Assert.Equal(currentId, state.Queue.Entries[0].EntryId);
        Assert.Equal(3, state.Queue.Entries[0].TotalRepeats);
        Assert.Equal(2, state.Queue.Entries[0].RemainingRepeats);
        Assert.Equal(4, state.Queue.Entries[0].ProgressSeconds);
        Assert.Equal(2, state.Queue.Entries[^1].TotalRepeats);
        Assert.Equal(2, state.Queue.Entries[^1].RemainingRepeats);
    }

    [Fact]
    public void Normalize_state_migrates_old_combat_slots_without_losing_configuration()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "兼容修士", Baseline);
        state.Combat.AbilitySlots =
        [
            new DaoxuanCombatAbilitySlot { AbilityId = DaoxuanCombatAbilityIds.HeavySlash, Level = 7 },
            new DaoxuanCombatAbilitySlot(),
            new DaoxuanCombatAbilitySlot(),
            new DaoxuanCombatAbilitySlot()
        ];
        state.Combat.FoodSlots =
        [
            new DaoxuanConsumableSlot
            {
                ItemId = DaoxuanItemIds.HealingElixir,
                Quantity = 2,
                Enabled = true
            },
            new DaoxuanConsumableSlot()
        ];
        state.Combat.DrinkSlots =
        [
            new DaoxuanConsumableSlot
            {
                ItemId = DaoxuanItemIds.SpiritTea,
                Quantity = 1,
                Enabled = true
            }
        ];

        var changed = DaoxuanSimulation.NormalizeState(state);

        Assert.True(changed);
        Assert.Equal(DaoxuanCombatLimits.AbilitySlotCount, state.Combat.AbilitySlots.Count);
        Assert.Equal(DaoxuanCombatAbilityIds.HeavySlash, state.Combat.AbilitySlots[0].AbilityId);
        Assert.Equal(7, state.Combat.AbilitySlots[0].Level);
        Assert.Equal(3, state.Combat.ConsumableSlots.Count);
        Assert.Equal(DaoxuanItemIds.HealingElixir, state.Combat.ConsumableSlots[0].ItemId);
        Assert.Equal(2, state.Combat.ConsumableSlots[0].Quantity);
        Assert.Equal(DaoxuanItemIds.SpiritTea, state.Combat.ConsumableSlots[1].ItemId);
        Assert.Null(state.Combat.ConsumableSlots[2].ItemId);
        Assert.Empty(state.Combat.FoodSlots);
        Assert.Empty(state.Combat.DrinkSlots);
    }

    [Fact]
    public void Legacy_snapshot_deserializes_and_normalizes_to_slot_lists()
    {
        // 旧版快照形态：autoUseSlots 每类行动是单个槽位对象，战斗分 food/drink 两组，功法槽 5 格。
        var legacyJson = $$"""
            {
              "playerId": "00000000-0000-0000-0000-00000000beef",
              "name": "旧快照修士",
              "lastSimulatedAt": "2026-08-04T00:00:00+00:00",
              "queue": {
                "entries": [],
                "loopTemplate": [],
                "loopEnabled": true,
                "autoUseSlots": {
                  "Cultivation": { "itemId": "{{DaoxuanItemIds.QiGatheringPill}}", "quantity": 2, "enabled": true },
                  "Exploration": { "itemId": null, "quantity": 1, "enabled": false },
                  "Alchemy": { "itemId": null, "quantity": 1, "enabled": false },
                  "Combat": { "itemId": null, "quantity": 1, "enabled": false }
                }
              },
              "combat": {
                "abilitySlots": [
                  { "abilityId": "{{DaoxuanCombatAbilityIds.HeavySlash}}", "level": 7, "triggers": [] },
                  { "abilityId": null, "level": 1, "triggers": [] },
                  { "abilityId": null, "level": 1, "triggers": [] },
                  { "abilityId": null, "level": 1, "triggers": [] },
                  { "abilityId": "{{DaoxuanCombatAbilityIds.BlazingBlast}}", "level": 3, "triggers": [] }
                ],
                "foodSlots": [
                  { "itemId": "{{DaoxuanItemIds.HealingElixir}}", "quantity": 1, "enabled": true, "triggers": [] },
                  { "itemId": null, "quantity": 1, "enabled": false, "triggers": [] },
                  { "itemId": null, "quantity": 1, "enabled": false, "triggers": [] }
                ],
                "drinkSlots": [
                  { "itemId": "{{DaoxuanItemIds.SpiritTea}}", "quantity": 1, "enabled": true, "triggers": [] },
                  { "itemId": null, "quantity": 1, "enabled": false, "triggers": [] },
                  { "itemId": null, "quantity": 1, "enabled": false, "triggers": [] }
                ]
              }
            }
            """;

        var state = JsonSerializer.Deserialize<DaoxuanPlayerState>(
            legacyJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(state);

        var changed = DaoxuanSimulation.NormalizeState(state);

        Assert.True(changed);
        foreach (var actionType in Enum.GetValues<DaoxuanActionType>())
        {
            Assert.Equal(
                DaoxuanCombatLimits.AutoUseSlotCount,
                state.Queue.AutoUseSlots[actionType].Count);
        }

        var cultivationSlots = state.Queue.AutoUseSlots[DaoxuanActionType.Cultivation];
        Assert.Equal(DaoxuanItemIds.QiGatheringPill, cultivationSlots[0].ItemId);
        Assert.Equal(2, cultivationSlots[0].Quantity);
        Assert.True(cultivationSlots[0].Enabled);
        Assert.Null(cultivationSlots[1].ItemId);
        Assert.Null(cultivationSlots[2].ItemId);
        Assert.Null(state.Queue.AutoUseSlots[DaoxuanActionType.Exploration][0].ItemId);
        Assert.Equal(DaoxuanCombatLimits.AbilitySlotCount, state.Combat.AbilitySlots.Count);
        Assert.Equal(DaoxuanCombatAbilityIds.HeavySlash, state.Combat.AbilitySlots[0].AbilityId);
        Assert.Equal(3, state.Combat.ConsumableSlots.Count);
        Assert.Equal(DaoxuanItemIds.HealingElixir, state.Combat.ConsumableSlots[0].ItemId);
        Assert.Equal(DaoxuanItemIds.SpiritTea, state.Combat.ConsumableSlots[1].ItemId);
        Assert.Null(state.Combat.ConsumableSlots[2].ItemId);
        Assert.Empty(state.Combat.FoodSlots);
        Assert.Empty(state.Combat.DrinkSlots);

        // 归一化后写出的快照一律使用数组形态，可以再次无损读入。
        var reserialized = JsonSerializer.Serialize(
            state,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var reloaded = JsonSerializer.Deserialize<DaoxuanPlayerState>(
            reserialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(reloaded);
        Assert.Equal(
            DaoxuanItemIds.QiGatheringPill,
            reloaded.Queue.AutoUseSlots[DaoxuanActionType.Cultivation][0].ItemId);
        Assert.Equal(3, reloaded.Combat.ConsumableSlots.Count);
    }

    [Fact]
    public void Auto_use_consumes_each_enabled_slot_independently()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "多槽修士", Baseline);
        state.Queue.Entries =
        [
            new DaoxuanQueuedAction
            {
                ActionId = DaoxuanActionIds.GatherHerbs,
                DurationSeconds = DaoxuanActionCatalog.Get(DaoxuanActionIds.GatherHerbs).DurationSeconds
            }
        ];
        state.Inventory.Add(DaoxuanItemIds.GatheringIncense, 3);
        Assert.True(DaoxuanSimulation.SetAutoUse(
            state,
            DaoxuanActionType.Exploration,
            DaoxuanItemIds.GatheringIncense,
            1,
            true,
            slotIndex: 0).Success);
        Assert.True(DaoxuanSimulation.SetAutoUse(
            state,
            DaoxuanActionType.Exploration,
            DaoxuanItemIds.GatheringIncense,
            2,
            true,
            slotIndex: 1).Success);

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(8));

        Assert.Equal(0, state.Inventory.Get(DaoxuanItemIds.GatheringIncense));
        Assert.Equal(5, state.Inventory.Get(DaoxuanItemIds.SpiritHerb));
        Assert.Equal(
            2,
            state.StructuredEventLog.Count(entry => entry.Template.Contains("自动使用", StringComparison.Ordinal)));
    }

    [Fact]
    public void Auto_use_rejects_out_of_range_slot_index_and_invalid_triggers()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "越界修士", Baseline);

        var outOfRange = DaoxuanSimulation.SetAutoUse(
            state,
            DaoxuanActionType.Exploration,
            DaoxuanItemIds.GatheringIncense,
            1,
            true,
            slotIndex: DaoxuanCombatLimits.AutoUseSlotCount);
        Assert.False(outOfRange.Success);
        Assert.Equal("INVALID_SLOT_INDEX", outOfRange.Code);

        var invalidTriggers = DaoxuanSimulation.SetAutoUse(
            state,
            DaoxuanActionType.Exploration,
            DaoxuanItemIds.GatheringIncense,
            1,
            true,
            slotIndex: 0,
            triggers: Enumerable.Range(0, DaoxuanCombatLimits.TriggerCountPerSlot + 1)
                .Select(_ => new DaoxuanCombatTrigger
                {
                    Dependency = DaoxuanCombatTriggerDependency.Self,
                    Condition = DaoxuanCombatTriggerCondition.MissingHitpoints,
                    Comparator = DaoxuanCombatTriggerComparator.GreaterThanOrEqual,
                    Value = 1
                })
                .ToList());
        Assert.False(invalidTriggers.Success);
        Assert.Equal("INVALID_TRIGGERS", invalidTriggers.Code);

        var withTrigger = DaoxuanSimulation.SetAutoUse(
            state,
            DaoxuanActionType.Exploration,
            DaoxuanItemIds.GatheringIncense,
            1,
            true,
            slotIndex: 2,
            triggers:
            [
                new DaoxuanCombatTrigger
                {
                    Dependency = DaoxuanCombatTriggerDependency.Self,
                    Condition = DaoxuanCombatTriggerCondition.MissingHitpoints,
                    Comparator = DaoxuanCombatTriggerComparator.GreaterThanOrEqual,
                    Value = 1
                }
            ]);
        Assert.True(withTrigger.Success);
        Assert.Single(state.Queue.AutoUseSlots[DaoxuanActionType.Exploration][2].Triggers!);
    }

    [Fact]
    public void One_hour_fast_forward_matches_second_by_second_progression()
    {
        var fast = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "快进修士", Baseline);
        var stepped = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "逐秒修士", Baseline);

        DaoxuanSimulation.AdvanceTo(fast, Baseline.AddHours(1));
        for (var second = 1; second <= 3600; second++)
        {
            DaoxuanSimulation.AdvanceTo(stepped, Baseline.AddSeconds(second));
        }

        Assert.Equal(stepped.Stats.Qi, fast.Stats.Qi);
        Assert.Equal(stepped.Stats.CompletedActions, fast.Stats.CompletedActions);
        Assert.Equal(stepped.Stats.HerbsCollected, fast.Stats.HerbsCollected);
        Assert.Equal(stepped.Stats.PillsCrafted, fast.Stats.PillsCrafted);
        Assert.Equal(stepped.Inventory.Get(DaoxuanItemIds.SpiritHerb), fast.Inventory.Get(DaoxuanItemIds.SpiritHerb));
        Assert.Equal(stepped.Inventory.Get(DaoxuanItemIds.QiGatheringPill), fast.Inventory.Get(DaoxuanItemIds.QiGatheringPill));
    }

    [Fact]
    public void Fully_offline_player_does_not_advance_action_queue()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "离线修士", Baseline);
        state.PresenceMode = DaoxuanPresenceMode.FullyOffline;

        var result = DaoxuanSimulation.AdvanceTo(state, Baseline.AddDays(7));

        Assert.Equal(0, result.CompletedActions);
        Assert.Equal(0, state.Stats.CompletedActions);
        Assert.Equal(Baseline.AddDays(7), state.LastSimulatedAt);
    }

    [Fact]
    public void Missing_action_ends_and_next_action_continues_when_loop_is_disabled()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "炼丹修士", Baseline);
        state.Queue.LoopEnabled = false;
        state.Queue.Entries =
        [
            new DaoxuanQueuedAction
            {
                ActionId = DaoxuanActionIds.RefineQiPill,
                DurationSeconds = DaoxuanActionCatalog.Get(DaoxuanActionIds.RefineQiPill).DurationSeconds
            },
            new DaoxuanQueuedAction
            {
                ActionId = DaoxuanActionIds.GatherHerbs,
                DurationSeconds = DaoxuanActionCatalog.Get(DaoxuanActionIds.GatherHerbs).DurationSeconds
            }
        ];

        var result = DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(1));

        Assert.Equal(0, result.CompletedActions);
        Assert.Single(state.Queue.Entries);
        Assert.Equal(DaoxuanActionIds.GatherHerbs, state.Queue.Entries[0].ActionId);
        Assert.Equal(1, state.Queue.Entries[0].ProgressSeconds);
        Assert.Equal(1, state.Stats.BlockedActions);
        Assert.Equal(0, state.Inventory.Get(DaoxuanItemIds.SpiritHerb));
        Assert.Equal(0, state.Inventory.Get(DaoxuanItemIds.QiGatheringPill));
    }

    [Fact]
    public void Missing_action_moves_to_tail_and_next_action_runs_when_loop_is_enabled()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "跳过修士", Baseline);
        state.Queue.Entries =
        [
            new DaoxuanQueuedAction
            {
                ActionId = DaoxuanActionIds.RefineQiPill,
                DurationSeconds = DaoxuanActionCatalog.Get(DaoxuanActionIds.RefineQiPill).DurationSeconds
            },
            new DaoxuanQueuedAction
            {
                ActionId = DaoxuanActionIds.GatherHerbs,
                DurationSeconds = DaoxuanActionCatalog.Get(DaoxuanActionIds.GatherHerbs).DurationSeconds
            }
        ];

        var result = DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(1));

        Assert.Equal(0, result.CompletedActions);
        Assert.Equal(DaoxuanActionIds.GatherHerbs, state.Queue.Entries[0].ActionId);
        Assert.Equal(1, state.Queue.Entries[0].ProgressSeconds);
        Assert.Equal(DaoxuanActionIds.RefineQiPill, state.Queue.Entries[1].ActionId);
        Assert.Equal(DaoxuanActionStatus.Blocked, state.Queue.Entries[1].Status);
        Assert.Contains("skipped:alchemy.refine-qi-pill", result.Events);
    }

    [Fact]
    public void Loop_stops_after_each_unavailable_action_was_skipped_once()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "空转保护修士", Baseline);
        state.Queue.Entries =
        [
            new DaoxuanQueuedAction
            {
                ActionId = DaoxuanActionIds.RefineQiPill,
                DurationSeconds = DaoxuanActionCatalog.Get(DaoxuanActionIds.RefineQiPill).DurationSeconds
            }
        ];

        var result = DaoxuanSimulation.AdvanceTo(state, Baseline.AddHours(1));

        Assert.Equal(0, result.CompletedActions);
        Assert.Equal(1, result.TransitionCount);
        Assert.Single(state.Queue.Entries);
        Assert.Equal(DaoxuanActionStatus.Blocked, state.Queue.Entries[0].Status);
        Assert.Equal(Baseline.AddHours(1), state.LastSimulatedAt);
    }

    [Fact]
    public void Partial_advance_marks_only_queue_progress_dirty()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "进度修士", Baseline);
        state.DirtySections = DaoxuanDirtySections.None;

        var result = DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(1));

        Assert.True(result.DirtySections.HasFlag(DaoxuanDirtySections.QueueProgress));
        Assert.False(result.DirtySections.HasFlag(DaoxuanDirtySections.Queue));
        Assert.Equal(1, state.Queue.Entries[0].ProgressSeconds);
    }

    [Fact]
    public void Repeating_identical_realtime_settings_does_not_dirty_state()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "幂等修士", Baseline);
        state.DirtySections = DaoxuanDirtySections.None;
        var version = state.Version;

        DaoxuanSimulation.SetLoop(state, true);
        DaoxuanSimulation.SetAutoUse(
            state,
            DaoxuanActionType.Cultivation,
            DaoxuanItemIds.QiGatheringPill,
            1,
            true);

        Assert.Equal(version, state.Version);
        Assert.Equal(DaoxuanDirtySections.None, state.DirtySections);
    }

    [Fact]
    public void Exploration_and_alchemy_auto_use_slots_apply_output_buffs()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "产出增益修士", Baseline);
        state.Queue.Entries =
        [
            new DaoxuanQueuedAction
            {
                ActionId = DaoxuanActionIds.GatherHerbs,
                DurationSeconds = DaoxuanActionCatalog.Get(DaoxuanActionIds.GatherHerbs).DurationSeconds
            }
        ];
        state.Inventory.Add(DaoxuanItemIds.GatheringIncense, 1);
        DaoxuanSimulation.SetAutoUse(state, DaoxuanActionType.Exploration, DaoxuanItemIds.GatheringIncense, 1, true);

        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(8));

        Assert.Equal(5, state.Inventory.Get(DaoxuanItemIds.SpiritHerb));
        Assert.Equal(0, state.Inventory.Get(DaoxuanItemIds.GatheringIncense));

        state.Queue.Entries =
        [
            new DaoxuanQueuedAction
            {
                ActionId = DaoxuanActionIds.RefineQiPill,
                DurationSeconds = DaoxuanActionCatalog.Get(DaoxuanActionIds.RefineQiPill).DurationSeconds
            }
        ];
        state.Inventory.Add(DaoxuanItemIds.SpiritHerb, 3);
        state.Inventory.Add(DaoxuanItemIds.AlchemyFlame, 1);
        DaoxuanSimulation.SetAutoUse(state, DaoxuanActionType.Alchemy, DaoxuanItemIds.AlchemyFlame, 1, true);
        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(20));

        Assert.Equal(2, state.Inventory.Get(DaoxuanItemIds.QiGatheringPill));
        Assert.Equal(0, state.Inventory.Get(DaoxuanItemIds.AlchemyFlame));
    }

    [Fact]
    public void Continuous_regeneration_applies_in_idle_and_non_combat_actions()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "自动回复修士", Baseline);
        state.Queue.Entries.Clear();
        state.CombatResources = new DaoxuanCombatResourceState
        {
            Initialized = true,
            Hitpoints = 50,
            Manapoints = 20
        };

        var attributes = DaoxuanCombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, Baseline, state.Stats.Spirit);
        Assert.True(attributes.HpRegenPerTenSeconds > 0);
        Assert.True(attributes.MpRegenPerTenSeconds > 0);

        // 空闲 10 秒：正常享受自然自动回复
        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(10));
        var expectedHpGain = (long)Math.Floor(attributes.MaxHitpoints * attributes.HpRegenPerTenSeconds * (10_000.0 / 10_000.0));
        var expectedMpGain = (long)Math.Floor(attributes.MaxManapoints * attributes.MpRegenPerTenSeconds * (10_000.0 / 10_000.0));

        Assert.Equal(Math.Min(attributes.MaxHitpoints, 50 + expectedHpGain), state.CombatResources.Hitpoints);
        Assert.Equal(Math.Min(attributes.MaxManapoints, 20 + expectedMpGain), state.CombatResources.Manapoints);

        // 加入非战斗行动（采药），推进 8 秒：持续运转回复
        state.Queue.Entries.Add(new DaoxuanQueuedAction
        {
            ActionId = DaoxuanActionIds.GatherHerbs,
            DurationSeconds = 8
        });

        var prevHp = state.CombatResources.Hitpoints;
        var prevMp = state.CombatResources.Manapoints;
        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(18));
        Assert.True(state.CombatResources.Hitpoints >= prevHp);
        Assert.True(state.CombatResources.Manapoints >= prevMp);
    }

    [Fact]
    public void Death_state_regenerates_mana_while_health_remains_zero_until_full_respawn()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "死亡回蓝修士", Baseline);
        state.Queue.Entries.Clear();
        state.CombatResources = new DaoxuanCombatResourceState
        {
            Initialized = true,
            Hitpoints = 0,
            Manapoints = 20
        };

        // 处于死亡状态（生命为 0）：空闲推进 10 秒
        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(10));

        // 生命值依然为 0（未复活不回血），而灵力正常自动回复
        Assert.Equal(0, state.CombatResources.Hitpoints);
        Assert.True(state.CombatResources.Manapoints > 20);
    }

    [Fact]
    public void Leaving_combat_by_remove_or_finish_revives_dead_player_to_one_hp_and_preserves_living()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "脱战复活修士", Baseline);
        state.Queue.Entries.Clear();
        state.CombatResources = new DaoxuanCombatResourceState
        {
            Initialized = true,
            Hitpoints = 50,
            Manapoints = 30
        };

        // 存活状态下加入历练并移除：保持原有生命与灵力
        Assert.True(DaoxuanSimulation.StartCombat(state, DaoxuanActionIds.HuntQiBeast, 1).Success);
        Assert.Equal(50, state.CombatResources.Hitpoints);
        Assert.Equal(30, state.CombatResources.Manapoints);

        var actionId = state.Queue.Entries[0].EntryId;
        Assert.True(DaoxuanSimulation.Remove(state, actionId).Success);
        Assert.Equal(50, state.CombatResources.Hitpoints);
        Assert.Equal(30, state.CombatResources.Manapoints);

        // 死亡状态下（生命值 0）进入战斗后被移除：脱战复活至 1 点生命，灵力不回复
        state.CombatResources.Hitpoints = 0;
        state.CombatResources.Manapoints = 25;
        Assert.True(DaoxuanSimulation.StartCombat(state, DaoxuanActionIds.HuntQiBeast, 1).Success);
        // StartCombat 脱战复活至 1 点血
        Assert.Equal(1, state.CombatResources.Hitpoints);
        Assert.Equal(25, state.CombatResources.Manapoints);

        // 模拟战斗被打死
        state.CombatResources.Hitpoints = 0;
        state.CombatResources.Manapoints = 10;
        var combatEntryId = state.Queue.Entries[0].EntryId;
        Assert.True(DaoxuanSimulation.Remove(state, combatEntryId).Success);

        // 移除后脱离战斗复活：生命值为 1 点，灵力保持 10 点，保证不死
        Assert.Equal(1, state.CombatResources.Hitpoints);
        Assert.Equal(10, state.CombatResources.Manapoints);
    }

    [Fact]
    public void Buffs_persist_and_do_not_reset_on_combat_exit_or_switch()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "永久增益修士", Baseline);
        state.Buffs.Add(new DaoxuanBuffState
        {
            BuffId = "test_buff",
            Name = "聚元诀",
            Stacks = 2,
            DamageRatio = 0.2,
            ExpiresAt = Baseline.AddMinutes(30)
        });

        // 开始战斗
        Assert.True(DaoxuanSimulation.StartCombat(state, DaoxuanActionIds.HuntQiBeast, 1).Success);
        Assert.Single(state.Buffs);
        Assert.Equal("test_buff", state.Buffs[0].BuffId);
        Assert.Equal(2, state.Buffs[0].Stacks);

        // 取消战斗
        var combatId = state.Queue.Entries[0].EntryId;
        Assert.True(DaoxuanSimulation.Remove(state, combatId).Success);
        Assert.Single(state.Buffs);
        Assert.Equal("test_buff", state.Buffs[0].BuffId);

        // 切换到赶路或采药
        state.Queue.Entries.Add(new DaoxuanQueuedAction
        {
            ActionId = DaoxuanActionIds.GatherHerbs,
            DurationSeconds = 8
        });
        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(8));
        Assert.Single(state.Buffs);
        Assert.Equal("test_buff", state.Buffs[0].BuffId);
    }

    [Fact]
    public void Continuous_regeneration_applies_while_all_loop_actions_are_blocked()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "空转回复修士", Baseline);
        state.Queue.Entries.Clear();
        state.Queue.LoopEnabled = true;
        state.CombatResources = new DaoxuanCombatResourceState
        {
            Initialized = true,
            Hitpoints = 50,
            Manapoints = 20
        };

        // 队列中只有一个条件不满足的行动（缺少灵草），循环模式下会被跳过并空转。
        state.Queue.Entries.Add(new DaoxuanQueuedAction
        {
            ActionId = DaoxuanActionIds.RefineQiPill,
            DurationSeconds = DaoxuanActionCatalog.Get(DaoxuanActionIds.RefineQiPill).DurationSeconds
        });

        var attributes = DaoxuanCombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, Baseline, state.Stats.Spirit);
        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(10));

        // 空转的 10 秒同样享受自动回复
        var expectedHpGain = (long)Math.Floor(attributes.MaxHitpoints * attributes.HpRegenPerTenSeconds * 1.0);
        var expectedMpGain = (long)Math.Floor(attributes.MaxManapoints * attributes.MpRegenPerTenSeconds * 1.0);
        Assert.Equal(Math.Min(attributes.MaxHitpoints, 50 + expectedHpGain), state.CombatResources.Hitpoints);
        Assert.Equal(Math.Min(attributes.MaxManapoints, 20 + expectedMpGain), state.CombatResources.Manapoints);
    }

    [Fact]
    public void Combat_encounters_preserve_damaged_resources_across_battles()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "连战修士", Baseline);
        state.Queue.Entries.Clear();
        state.CombatResources = new DaoxuanCombatResourceState
        {
            Initialized = true,
            Hitpoints = 80,
            Manapoints = 30
        };

        // 启动历练，重复 2 次
        Assert.True(DaoxuanSimulation.StartCombat(state, DaoxuanActionIds.HuntQiBeast, 2).Success);
        Assert.Equal(80, state.CombatResources.Hitpoints);
        Assert.Equal(30, state.CombatResources.Manapoints);

        // 推进 60 秒（覆盖若干遭遇战与搜寻）
        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(60));

        // 检查生命与灵力：没有被完全回满至 MaxHitpoints / MaxManapoints
        var attributes = DaoxuanCombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, Baseline, state.Stats.Spirit);
        // 如果受伤或释放技能消耗灵力，绝不自动满状态
        Assert.True(state.CombatResources.Hitpoints <= attributes.MaxHitpoints);
        Assert.True(state.CombatResources.Manapoints <= attributes.MaxManapoints);
    }

    [Fact]
    public void Combat_end_does_not_clear_skill_and_consumable_cooldowns()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "历练修士", Baseline);
        state.Queue.Entries.Clear();
        state.Stats.Spirit = 100;
        state.CombatResources = new DaoxuanCombatResourceState
        {
            Initialized = true,
            Hitpoints = 500,
            Manapoints = 200
        };
        state.Inventory.Add(DaoxuanItemIds.FireTalisman, 5);
        state.Combat.ConsumableSlots[0] = new DaoxuanConsumableSlot
        {
            ItemId = DaoxuanItemIds.FireTalisman,
            Quantity = 1,
            Enabled = true,
            Triggers =
            [
                new DaoxuanCombatTrigger
                {
                    Dependency = DaoxuanCombatTriggerDependency.Self,
                    Condition = DaoxuanCombatTriggerCondition.CurrentHitpoints,
                    Comparator = DaoxuanCombatTriggerComparator.GreaterThanOrEqual,
                    Value = 1
                }
            ]
        };

        // 启动历练
        Assert.True(DaoxuanSimulation.StartCombat(state, DaoxuanActionIds.HuntQiBeast, 10).Success);

        // 推进到初次搜寻结束并进入战斗且发生使用 (初次搜寻20秒 + 战斗5秒)
        DaoxuanSimulation.AdvanceTo(state, Baseline.AddSeconds(25));

        // 验证消耗品使用后产生冷却
        var hasCooldown = state.CombatResources.ConsumableSlotCooldowns.Count > 0
            && state.CombatResources.ConsumableSlotCooldowns[0] > state.LastSimulatedAt;
        Assert.True(hasCooldown);

        // 推进到下一场搜寻（10秒后）
        var previousSimulatedAt = state.LastSimulatedAt;
        DaoxuanSimulation.AdvanceTo(state, previousSimulatedAt.AddSeconds(10));

        // 冷却时间随物理时间自然缩短，但未到期的冷却不会被重置清除
        var remaining = (state.CombatResources.ConsumableSlotCooldowns[0] - state.LastSimulatedAt).TotalMilliseconds;
        Assert.True(remaining > 0);
    }

    [Fact]
    public void Leaving_combat_preserves_cooldowns_in_combat_resources()
    {
        var state = DaoxuanSimulation.CreateDefaultState(Guid.NewGuid(), "脱战修士", Baseline);
        state.Queue.Entries.Clear();
        state.CombatResources.AbilityCooldowns[DaoxuanCombatAbilityIds.HeavySlash] = Baseline.AddSeconds(20);
        state.CombatResources.ConsumableSlotCooldowns = [Baseline.AddSeconds(15)];

        // 启动历练然后移除队列（脱离战斗）
        Assert.True(DaoxuanSimulation.StartCombat(state, DaoxuanActionIds.HuntQiBeast, 1).Success);
        var entryId = state.Queue.Entries[0].EntryId;
        Assert.True(DaoxuanSimulation.Remove(state, entryId).Success);

        // 确认脱战后，技能和物品冷却依然完好保存在 CombatResources
        Assert.True(state.CombatResources.AbilityCooldowns.ContainsKey(DaoxuanCombatAbilityIds.HeavySlash));
        Assert.Equal(Baseline.AddSeconds(20), state.CombatResources.AbilityCooldowns[DaoxuanCombatAbilityIds.HeavySlash]);
        Assert.Equal(Baseline.AddSeconds(15), state.CombatResources.ConsumableSlotCooldowns[0]);
    }
}
