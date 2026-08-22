using System.Text.Json;
using DaoHua.Domain.Models;
using DaoHua.Domain.Simulation;
using Xunit;

namespace DaoHua.Domain.Tests;

/// <summary>
/// 验证 DaoHuaSimulation（单人离线与在线确定性模拟器）的核心状态机流转、行动队列、自动消耗品使用、增益结算与快进确定性。
/// </summary>
public sealed class DaoHuaSimulationTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-05T00:00:00Z");

    /// <summary>
    /// 验证背包在扣减物品数量至 0 时，自动从 Items 字典中移除对应 Key，保持数据紧凑。
    /// </summary>
    [Fact]
    public void Inventory_removes_item_entry_when_quantity_reaches_zero()
    {
        var inventory = new DaoHuaInventoryState();
        inventory.Add(DaoHuaItemIds.GatheringIncense, 1);

        inventory.Add(DaoHuaItemIds.GatheringIncense, -1);

        Assert.Equal(0, inventory.Get(DaoHuaItemIds.GatheringIncense));
        Assert.DoesNotContain(DaoHuaItemIds.GatheringIncense, inventory.Items.Keys);
    }

    /// <summary>
    /// 验证状态归一化（NormalizeState）会清理背包中遗留的数量为 0 的物品条目，同时保留自动使用槽位引用。
    /// </summary>
    [Fact]
    public void Normalize_state_removes_legacy_zero_inventory_entries_without_clearing_item_slots()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "归零修士", Baseline);
        state.Inventory.Items[DaoHuaItemIds.QiGatheringPill] = 0;
        state.DirtySections = DaoHuaDirtySections.None;

        var changed = DaoHuaSimulation.NormalizeState(state);

        Assert.True(changed);
        Assert.DoesNotContain(DaoHuaItemIds.QiGatheringPill, state.Inventory.Items.Keys);
        Assert.Equal(
            DaoHuaItemIds.QiGatheringPill,
            state.Queue.AutoUseSlots[DaoHuaActionType.Cultivation][0].ItemId);
        Assert.True(state.DirtySections.HasFlag(DaoHuaDirtySections.Inventory));
    }

    /// <summary>
    /// 验证定时行动在微步推进时保留毫秒级进度，并在到达确切截止时间边界时精准触发完成。
    /// </summary>
    [Fact]
    public void Timed_action_preserves_millisecond_progress_and_completes_at_exact_deadline()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), now: Baseline);
        state.Queue.Entries.Clear();
        state.Queue.LoopEnabled = false;
        DaoHuaSimulation.Enqueue(state, DaoHuaActionIds.GatherHerbs);

        DaoHuaSimulation.AdvanceTo(state, Baseline);
        var deadline = DaoHuaSimulation.GetNextTransitionAt(state);
        Assert.Equal(Baseline.AddSeconds(8), deadline);

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddMilliseconds(3_456));
        Assert.Equal(3_456, state.Queue.Entries[0].ProgressMilliseconds);
        Assert.Equal(3, state.Queue.Entries[0].ProgressSeconds);

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddMilliseconds(7_999));
        Assert.Single(state.Queue.Entries);
        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(8));
        Assert.Empty(state.Queue.Entries);
        Assert.Equal(1, state.Stats.CompletedActions);
    }

    /// <summary>
    /// 验证初始默认队列依次完成采药、炼丹和引气修炼循环，正确产出真气并循环轮转。
    /// </summary>
    [Fact]
    public void Initial_queue_completes_gather_alchemy_cultivation_loop()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "测试修士", Baseline);

        var result = DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(30));

        Assert.Equal(3, result.CompletedActions);
        Assert.Equal(3, state.Stats.CompletedActions);
        Assert.Equal(3, state.Stats.HerbsCollected);
        Assert.Equal(1, state.Stats.PillsCrafted);
        Assert.Equal(1, state.Stats.CultivationCycles);
        Assert.Equal(13, state.Stats.Qi);
        Assert.Equal(state.Stats.Qi, state.Inventory.Get(DaoHuaItemIds.Qi));
        Assert.Equal(0, state.Inventory.Get(DaoHuaItemIds.SpiritHerb));
        Assert.Equal(0, state.Inventory.Get(DaoHuaItemIds.QiGatheringPill));
        Assert.Single(state.Buffs);
        Assert.Equal(3, state.Queue.Entries.Count);
        Assert.Equal(DaoHuaActionIds.GatherHerbs, state.Queue.Entries[0].ActionId);
    }

    /// <summary>
    /// 验证结构化事件日志（StructuredEventLog）保持单调递增的序号 Sequence，并保留完整发生时间与模板。
    /// </summary>
    [Fact]
    public void Log_events_keep_stable_sequence_and_full_display_time()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "日志修士", Baseline);

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(8));

        Assert.Equal(2, state.StructuredEventLog.Count);
        Assert.Equal([1L, 2L], state.StructuredEventLog.Select(entry => entry.Sequence));
        Assert.Equal(Baseline, state.StructuredEventLog[0].OccurredAt);
        Assert.Contains("{{action:explore.gather-herbs|采集草药}}", state.StructuredEventLog[1].Template, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证离线挂机推进时记录历史结构化日志，但不向内存中的 WebSocket 待发队列（PendingEvents）堆积冗余消息。
    /// </summary>
    [Fact]
    public void Offline_progress_keeps_history_without_queuing_websocket_events()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "离线日志修士", Baseline);
        state.PendingEvents.Clear();
        state.PresenceMode = DaoHuaPresenceMode.IdleOffline;

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(30));

        Assert.Empty(state.PendingEvents);
        Assert.NotEmpty(state.StructuredEventLog);
        Assert.Contains(
            state.StructuredEventLog,
            entry => entry.Template.Contains("行动完成", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证循环模式下单个行动执行完毕后，立即重置执行次数并移至队列末尾。
    /// </summary>
    [Fact]
    public void Completed_loop_action_moves_to_queue_tail_immediately()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "轮转修士", Baseline);
        var completedEntryId = state.Queue.Entries[0].EntryId;

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(8));

        Assert.Equal(
            [DaoHuaActionIds.RefineQiPill, DaoHuaActionIds.DrawQi, DaoHuaActionIds.GatherHerbs],
            state.Queue.Entries.Select(entry => entry.ActionId));
        Assert.Equal(completedEntryId, state.Queue.Entries[^1].EntryId);
        Assert.Equal(1, state.Queue.Entries[^1].RemainingRepeats);
        Assert.Equal(0, state.Queue.Entries[^1].ProgressSeconds);
    }

    /// <summary>
    /// 验证配置了多次重复的行动在全部批次执行完毕前留在队首，耗尽后才轮转至队尾。
    /// </summary>
    [Fact]
    public void Repeated_loop_action_finishes_its_batch_before_moving_to_tail()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "批次修士", Baseline);
        state.Queue.Entries =
        [
            new DaoHuaQueuedAction
            {
                ActionId = DaoHuaActionIds.GatherHerbs,
                TotalRepeats = 2,
                RemainingRepeats = 2,
                DurationSeconds = DaoHuaActionCatalog.Get(DaoHuaActionIds.GatherHerbs).DurationSeconds
            },
            new DaoHuaQueuedAction
            {
                ActionId = DaoHuaActionIds.DrawQi,
                TotalRepeats = 1,
                RemainingRepeats = 1,
                DurationSeconds = DaoHuaActionCatalog.Get(DaoHuaActionIds.DrawQi).DurationSeconds
            }
        ];

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(8));
        Assert.Equal(DaoHuaActionIds.GatherHerbs, state.Queue.Entries[0].ActionId);
        Assert.Equal(1, state.Queue.Entries[0].RemainingRepeats);

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(16));
        Assert.Equal(DaoHuaActionIds.DrawQi, state.Queue.Entries[0].ActionId);
        Assert.Equal(DaoHuaActionIds.GatherHerbs, state.Queue.Entries[1].ActionId);
        Assert.Equal(2, state.Queue.Entries[1].RemainingRepeats);
    }

    /// <summary>
    /// 验证无限循环行动（repeats = -1）持续在队首执行且不随完成减少次数。
    /// </summary>
    [Fact]
    public void Infinite_action_is_accepted_and_stays_at_queue_head()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "无限修士", Baseline);
        state.Queue.Entries.Clear();

        var command = DaoHuaSimulation.Enqueue(state, DaoHuaActionIds.GatherHerbs, -1);
        var result = DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(16));

        Assert.True(command.Success);
        Assert.Equal(2, result.CompletedActions);
        Assert.Single(state.Queue.Entries);
        Assert.Equal(-1, state.Queue.Entries[0].TotalRepeats);
        Assert.Equal(-1, state.Queue.Entries[0].RemainingRepeats);
        Assert.Equal(0, state.Queue.Entries[0].ProgressSeconds);
    }

    /// <summary>
    /// 验证发起历练战斗（StartCombat）会立即中断当前正在运行的队列并抢占队首执行。
    /// </summary>
    [Fact]
    public void Starting_combat_interrupts_current_loop_and_runs_immediately()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "历练修士", Baseline);
        var previousHead = state.Queue.Entries[0];
        previousHead.Status = DaoHuaActionStatus.Running;
        previousHead.ProgressSeconds = 3;

        var command = DaoHuaSimulation.StartCombat(state, DaoHuaActionIds.HuntQiBeast, 3);

        Assert.True(command.Success);
        Assert.Equal(DaoHuaActionIds.HuntQiBeast, state.Queue.Entries[0].ActionId);
        Assert.Equal(3, state.Queue.Entries[0].TotalRepeats);
        Assert.Equal(3, state.Queue.Entries[0].RemainingRepeats);
        Assert.Equal(DaoHuaActionStatus.Queued, previousHead.Status);
        Assert.Equal(3, previousHead.ProgressSeconds);

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(1));

        Assert.Equal(DaoHuaActionStatus.Running, state.Queue.Entries[0].Status);
        Assert.Equal(1, state.Queue.Entries[0].ProgressSeconds);
    }

    /// <summary>
    /// 验证历练远征包含固定的初始搜寻（20s）与次级搜寻（2.5s），期间生命/灵力享受自然回复。
    /// </summary>
    [Fact]
    public void Combat_expedition_keeps_fixed_initial_search_and_resources_between_encounters()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "连续历练修士", Baseline);
        state.Queue.Entries.Clear();
        state.Combat.StaminaLevel = 1_000;
        state.Combat.IntelligenceLevel = 1_000;
        state.Combat.AttackLevel = 1_000;
        state.Combat.DefenseLevel = 1_000;
        state.Combat.SwordsmanshipLevel = 1_000;
        state.Combat.TalismanLevel = 1_000;
        state.Combat.SearchSpeedRatio = 1;
        state.Combat.LearnedAbilityIds.Add(DaoHuaCombatAbilityIds.HeavySlash);
        state.Combat.AbilitySlots[0].AbilityId = DaoHuaCombatAbilityIds.HeavySlash;
        state.CombatResources = new DaoHuaCombatResourceState
        {
            Initialized = true,
            Hitpoints = 50,
            Manapoints = 40
        };

        var command = DaoHuaSimulation.StartCombat(state, DaoHuaActionIds.HuntQiBeast, 2);
        Assert.True(command.Success);
        state.CombatResources.Hitpoints = 50;
        state.CombatResources.Manapoints = 40;

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(19));
        var action = state.Queue.Entries[0];
        var expedition = Assert.IsType<DaoHuaCombatExpeditionState>(action.CombatExpedition);
        Assert.Equal(DaoHuaCombatExpeditionPhase.InitialSearch, expedition.Phase);
        Assert.Equal(20_000, expedition.PhaseDurationMilliseconds);
        Assert.Equal(0, expedition.BattleSequence);

        var expectedInitialPlayer = DaoHuaCombatSimulator.CreatePlayerState(state.Combat, state.Buffs, state.LastSimulatedAt);
        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(20));
        Assert.Equal(DaoHuaCombatExpeditionPhase.Combat, expedition.Phase);
        Assert.Equal(1, expedition.BattleSequence);
        // 初始搜寻阶段（20秒）自然回复生效
        Assert.True(expedition.InitialHitpoints >= 50);
        Assert.True(expedition.InitialManapoints >= 40);
        Assert.Equal(0, action.DurationSeconds);

        for (var seconds = 21; seconds <= 130 && expedition.Phase == DaoHuaCombatExpeditionPhase.Combat; seconds++)
        {
            DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(seconds));
        }

        Assert.Equal(DaoHuaCombatExpeditionPhase.Search, expedition.Phase);
        Assert.Equal(2_500, expedition.PhaseDurationMilliseconds);
        Assert.Equal(1, action.RemainingRepeats);
        var retainedHitpoints = state.CombatResources.Hitpoints;
        var retainedManapoints = state.CombatResources.Manapoints;

        var remainingSearchMilliseconds = expedition.PhaseDurationMilliseconds - expedition.PhaseElapsedMilliseconds;
        DaoHuaSimulation.AdvanceTo(state, state.LastSimulatedAt.AddMilliseconds(remainingSearchMilliseconds + 1_000));

        Assert.Equal(DaoHuaCombatExpeditionPhase.Combat, expedition.Phase);
        Assert.Equal(2, expedition.BattleSequence);
        // 第二次遭遇开始时，生命和灵力享受了搜寻阶段的自然回复
        Assert.True(expedition.InitialHitpoints >= retainedHitpoints);
        Assert.True(expedition.InitialManapoints >= retainedManapoints);
    }

    /// <summary>
    /// 验证重新开启战斗会继承现有生命/灵力资源，并重置全新的初始搜寻时间线。
    /// </summary>
    [Fact]
    public void Restarting_combat_preserves_resources_and_starts_a_fresh_battle_timeline()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "重开历练修士", Baseline);
        state.Queue.Entries.Clear();
        state.CombatResources = new DaoHuaCombatResourceState
        {
            Initialized = true,
            Hitpoints = 10,
            Manapoints = 20
        };

        Assert.True(DaoHuaSimulation.StartCombat(state, DaoHuaActionIds.HuntQiBeast, 1).Success);

        Assert.Equal(10, state.CombatResources.Hitpoints);
        Assert.Equal(20, state.CombatResources.Manapoints);
        var expedition = Assert.IsType<DaoHuaCombatExpeditionState>(state.Queue.Entries[0].CombatExpedition);
        Assert.Equal(DaoHuaCombatExpeditionPhase.InitialSearch, expedition.Phase);
        Assert.Equal(0, expedition.BattleSequence);
        Assert.Equal(default, expedition.BattleStartedAt);
        Assert.Empty(expedition.AppliedConsumedItems);
    }

    /// <summary>
    /// 验证在战败死亡（0点生命）状态下重新开启战斗时，自动复活至 1 点生命值并启动新战斗。
    /// </summary>
    [Fact]
    public void Restarting_during_combat_with_zero_hp_replaces_action_and_revives_player_with_one_hp()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "死亡重开修士", Baseline);
        state.Queue.Entries.Clear();
        Assert.True(DaoHuaSimulation.StartCombat(state, DaoHuaActionIds.HuntQiBeast, 2).Success);
        var oldAction = state.Queue.Entries[0];
        oldAction.Status = DaoHuaActionStatus.Running;
        var oldExpedition = Assert.IsType<DaoHuaCombatExpeditionState>(oldAction.CombatExpedition);
        oldExpedition.Phase = DaoHuaCombatExpeditionPhase.Search;
        oldExpedition.PhaseElapsedMilliseconds = 60_000;
        oldExpedition.PhaseDurationMilliseconds = 180_000;
        state.CombatResources.Hitpoints = 0;
        state.CombatResources.Manapoints = 15;

        Assert.True(DaoHuaSimulation.StartCombat(state, DaoHuaActionIds.HuntQiBeast, 1).Success);

        Assert.DoesNotContain(state.Queue.Entries, entry => entry.EntryId == oldAction.EntryId);
        var restarted = state.Queue.Entries[0];
        var restartedExpedition = Assert.IsType<DaoHuaCombatExpeditionState>(restarted.CombatExpedition);
        Assert.Equal(DaoHuaCombatExpeditionPhase.InitialSearch, restartedExpedition.Phase);
        Assert.Equal(0, restartedExpedition.PhaseElapsedMilliseconds);
        // 离开上一场战斗死亡状态复活：生命值回复到 1 点，灵力不回复（保持 15 点），保证不死
        Assert.Equal(1, state.CombatResources.Hitpoints);
        Assert.Equal(15, state.CombatResources.Manapoints);
    }

    /// <summary>
    /// 验证战斗状态机能够准确对齐下一次战斗事件（如施法/攻击）的时间截止点，无多余延迟。
    /// </summary>
    [Fact]
    public void Combat_action_consumes_the_exact_next_event_deadline_without_extra_delay()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "准时出手修士", Baseline);
        state.Queue.Entries.Clear();
        Assert.True(DaoHuaSimulation.StartCombat(state, DaoHuaActionIds.HuntQiBeast, 1).Success);

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(20));
        var action = state.Queue.Entries[0];
        var expedition = Assert.IsType<DaoHuaCombatExpeditionState>(action.CombatExpedition);
        Assert.Equal(DaoHuaCombatExpeditionPhase.Combat, expedition.Phase);

        var loadout = Assert.IsType<DaoHuaCombatLoadout>(expedition.BattleLoadout);
        var initialPlayer = DaoHuaCombatSimulator.CreatePlayerState(
            loadout,
            expedition.BattleBuffs ?? [],
            expedition.BattleStartedAt,
            expedition.InitialHitpoints,
            expedition.InitialManapoints);
        var preview = DaoHuaCombatSimulator.Run(
            state.PlayerId,
            loadout,
            expedition.BattleBuffs ?? [],
            expedition.BattleInventory ?? new Dictionary<string, long>(StringComparer.Ordinal),
            DaoHuaCombatEncounterIds.QiBeast,
            expedition.BattleStartedAt,
            TimeSpan.Zero,
            expedition.CompletedEncountersBeforeBattle,
            initialPlayer,
            stopAfterFirstOutcome: true);
        var nextEventMilliseconds = Assert.IsType<long>(preview.NextEventMilliseconds);
        var exactDeadline = expedition.BattleStartedAt.AddMilliseconds(nextEventMilliseconds);

        Assert.Equal(exactDeadline, DaoHuaSimulation.GetNextTransitionAt(state));

        DaoHuaSimulation.AdvanceTo(state, exactDeadline.AddMilliseconds(-1));
        Assert.Equal(exactDeadline, DaoHuaSimulation.GetNextTransitionAt(state));

        DaoHuaSimulation.AdvanceTo(state, exactDeadline);

        Assert.Equal(exactDeadline, state.LastSimulatedAt);
        var followingDeadline = DaoHuaSimulation.GetNextTransitionAt(state);
        Assert.True(followingDeadline is null || followingDeadline > exactDeadline);
    }

    /// <summary>
    /// 验证玩家战斗败退后原地等待 180 秒完全重生，且重生后继续挑战当前波次直至获胜。
    /// </summary>
    [Fact]
    public void Player_death_keeps_the_same_wave_until_full_respawn_and_victory()
    {
        var state = DaoHuaSimulation.CreateDefaultState(
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            "败退修士",
            Baseline);
        state.Queue.Entries.Clear();
        state.Combat.AttackLevel = 1;
        state.Combat.SwordsmanshipLevel = 1;

        var command = DaoHuaSimulation.StartCombat(state, DaoHuaActionIds.HuntQiBeast, 2);
        Assert.True(command.Success);

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(20));
        var action = state.Queue.Entries[0];
        var expedition = Assert.IsType<DaoHuaCombatExpeditionState>(action.CombatExpedition);
        var encounterId = Assert.IsType<string>(expedition.EncounterId);
        var battleSequence = expedition.BattleSequence;
        expedition.InitialHitpoints = 0;
        expedition.EmittedEventCount = 0;
        state.CombatResources.Hitpoints = 0;

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddMilliseconds(20_001));

        Assert.Equal(DaoHuaCombatExpeditionPhase.Combat, expedition.Phase);
        Assert.Equal(encounterId, expedition.EncounterId);
        Assert.Equal(battleSequence, expedition.BattleSequence);
        Assert.Equal(2, action.RemainingRepeats);
        Assert.Equal(1, state.Stats.CombatLosses);
        Assert.Equal(0, state.CombatResources.Hitpoints);
        var respawnAt = expedition.BattleStartedAt.AddSeconds(180);

        DaoHuaSimulation.AdvanceTo(state, respawnAt.AddMilliseconds(-1));
        Assert.Equal(DaoHuaCombatExpeditionPhase.Combat, expedition.Phase);
        Assert.Equal(2, action.RemainingRepeats);
        Assert.Equal(0, state.CombatResources.Hitpoints);

        state.Combat.AttackLevel = 1_000;
        state.Combat.DefenseLevel = 1_000;
        state.Combat.StaminaLevel = 1_000;
        state.Combat.SwordsmanshipLevel = 1_000;
        state.Combat.LearnedAbilityIds.Add(DaoHuaCombatAbilityIds.HeavySlash);
        state.Combat.AbilitySlots[0].AbilityId = DaoHuaCombatAbilityIds.HeavySlash;
        expedition.BattleLoadout = state.Combat.Clone();

        DaoHuaSimulation.AdvanceTo(state, respawnAt.AddSeconds(1));

        Assert.Equal(DaoHuaCombatExpeditionPhase.Combat, expedition.Phase);
        Assert.Equal(2, action.RemainingRepeats);
        Assert.Contains(state.StructuredEventLog, entry => entry.Template.Contains("战斗复起", StringComparison.Ordinal));
        var restored = DaoHuaCombatSimulator.CreatePlayerState(state.Combat, state.Buffs, state.LastSimulatedAt);
        Assert.InRange(state.CombatResources.Hitpoints, 1, restored.MaxHitpoints);
        Assert.InRange(state.CombatResources.Manapoints, 1, restored.MaxManapoints);

        for (var seconds = 2; seconds <= 60 && expedition.Phase == DaoHuaCombatExpeditionPhase.Combat; seconds++)
        {
            DaoHuaSimulation.AdvanceTo(state, respawnAt.AddSeconds(seconds));
        }

        Assert.Equal(1, state.Stats.CombatLosses);
        Assert.Equal(DaoHuaCombatExpeditionPhase.Search, expedition.Phase);
        Assert.Equal(1, action.RemainingRepeats);
        Assert.True(state.CombatResources.Hitpoints > 0);
    }

    /// <summary>
    /// 验证快照 JSON 反序列化后能够归一化补齐固定数量的功法槽与自动消耗品槽位数组。
    /// </summary>
    [Fact]
    public void Snapshot_deserializes_and_normalizes_to_slot_lists()
    {
        var snapshotJson = $$"""
            {
              "playerId": "00000000-0000-0000-0000-00000000beef",
              "name": "测试快照修士",
              "lastSimulatedAt": "2026-08-04T00:00:00+00:00",
              "queue": {
                "entries": [],
                "loopEnabled": true,
                "autoUseSlots": {
                  "Cultivation": [
                    { "itemId": "{{DaoHuaItemIds.QiGatheringPill}}", "quantity": 2, "enabled": true, "triggers": [] }
                  ]
                }
              },
              "combat": {
                "abilitySlots": [
                  { "abilityId": "{{DaoHuaCombatAbilityIds.HeavySlash}}", "level": 7, "triggers": [] }
                ],
                "consumableSlots": [
                  { "itemId": "{{DaoHuaItemIds.HealingElixir}}", "quantity": 1, "enabled": true, "triggers": [] },
                  { "itemId": "{{DaoHuaItemIds.SpiritTea}}", "quantity": 1, "enabled": true, "triggers": [] }
                ]
              }
            }
            """;

        var state = JsonSerializer.Deserialize<DaoHuaPlayerState>(
            snapshotJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(state);

        var changed = DaoHuaSimulation.NormalizeState(state);

        Assert.True(changed);
        foreach (var actionType in Enum.GetValues<DaoHuaActionType>())
        {
            Assert.Equal(
                DaoHuaCombatLimits.AutoUseSlotCount,
                state.Queue.AutoUseSlots[actionType].Count);
        }

        var cultivationSlots = state.Queue.AutoUseSlots[DaoHuaActionType.Cultivation];
        Assert.Equal(DaoHuaItemIds.QiGatheringPill, cultivationSlots[0].ItemId);
        Assert.Equal(2, cultivationSlots[0].Quantity);
        Assert.True(cultivationSlots[0].Enabled);
        Assert.Null(cultivationSlots[1].ItemId);
        Assert.Null(cultivationSlots[2].ItemId);
        Assert.Null(state.Queue.AutoUseSlots[DaoHuaActionType.Exploration][0].ItemId);
        Assert.Equal(DaoHuaCombatLimits.AbilitySlotCount, state.Combat.AbilitySlots.Count);
        Assert.Equal(DaoHuaCombatAbilityIds.HeavySlash, state.Combat.AbilitySlots[0].AbilityId);
        Assert.Equal(3, state.Combat.ConsumableSlots.Count);
        Assert.Equal(DaoHuaItemIds.HealingElixir, state.Combat.ConsumableSlots[0].ItemId);
        Assert.Equal(DaoHuaItemIds.SpiritTea, state.Combat.ConsumableSlots[1].ItemId);
        Assert.Null(state.Combat.ConsumableSlots[2].ItemId);

        // 归一化后写出的快照一律使用数组形态，可以再次无损读入。
        var reserialized = JsonSerializer.Serialize(
            state,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var reloaded = JsonSerializer.Deserialize<DaoHuaPlayerState>(
            reserialized,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(reloaded);
        Assert.Equal(
            DaoHuaItemIds.QiGatheringPill,
            reloaded.Queue.AutoUseSlots[DaoHuaActionType.Cultivation][0].ItemId);
        Assert.Equal(3, reloaded.Combat.ConsumableSlots.Count);
    }

    /// <summary>
    /// 验证多槽位自动消耗品独立判定与扣减，各已启用槽位的物品在行动开始时按配置数量正确扣减并产出对应收益。
    /// </summary>
    [Fact]
    public void Auto_use_consumes_each_enabled_slot_independently()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "多槽修士", Baseline);
        state.Queue.Entries =
        [
            new DaoHuaQueuedAction
            {
                ActionId = DaoHuaActionIds.GatherHerbs,
                DurationSeconds = DaoHuaActionCatalog.Get(DaoHuaActionIds.GatherHerbs).DurationSeconds
            }
        ];
        state.Inventory.Add(DaoHuaItemIds.GatheringIncense, 3);
        Assert.True(DaoHuaSimulation.SetAutoUse(
            state,
            DaoHuaActionType.Exploration,
            DaoHuaItemIds.GatheringIncense,
            1,
            true,
            slotIndex: 0).Success);
        Assert.True(DaoHuaSimulation.SetAutoUse(
            state,
            DaoHuaActionType.Exploration,
            DaoHuaItemIds.GatheringIncense,
            2,
            true,
            slotIndex: 1).Success);

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(8));

        Assert.Equal(0, state.Inventory.Get(DaoHuaItemIds.GatheringIncense));
        Assert.Equal(5, state.Inventory.Get(DaoHuaItemIds.SpiritHerb));
        Assert.Equal(
            2,
            state.StructuredEventLog.Count(entry => entry.Template.Contains("自动使用", StringComparison.Ordinal)));
    }

    /// <summary>
    /// 验证自动消耗品配置校验：槽位索引越界与单个槽位触发器数量超限（>3个）时被安全拦截。
    /// </summary>
    [Fact]
    public void Auto_use_rejects_out_of_range_slot_index_and_invalid_triggers()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "越界修士", Baseline);

        var outOfRange = DaoHuaSimulation.SetAutoUse(
            state,
            DaoHuaActionType.Exploration,
            DaoHuaItemIds.GatheringIncense,
            1,
            true,
            slotIndex: DaoHuaCombatLimits.AutoUseSlotCount);
        Assert.False(outOfRange.Success);
        Assert.Equal("INVALID_SLOT_INDEX", outOfRange.Code);

        var invalidTriggers = DaoHuaSimulation.SetAutoUse(
            state,
            DaoHuaActionType.Exploration,
            DaoHuaItemIds.GatheringIncense,
            1,
            true,
            slotIndex: 0,
            triggers: Enumerable.Range(0, DaoHuaCombatLimits.TriggerCountPerSlot + 1)
                .Select(_ => new DaoHuaCombatTrigger
                {
                    Dependency = DaoHuaCombatTriggerDependency.Self,
                    Condition = DaoHuaCombatTriggerCondition.MissingHitpoints,
                    Comparator = DaoHuaCombatTriggerComparator.GreaterThanOrEqual,
                    Value = 1
                })
                .ToList());
        Assert.False(invalidTriggers.Success);
        Assert.Equal("INVALID_TRIGGERS", invalidTriggers.Code);

        var withTrigger = DaoHuaSimulation.SetAutoUse(
            state,
            DaoHuaActionType.Exploration,
            DaoHuaItemIds.GatheringIncense,
            1,
            true,
            slotIndex: 2,
            triggers:
            [
                new DaoHuaCombatTrigger
                {
                    Dependency = DaoHuaCombatTriggerDependency.Self,
                    Condition = DaoHuaCombatTriggerCondition.MissingHitpoints,
                    Comparator = DaoHuaCombatTriggerComparator.GreaterThanOrEqual,
                    Value = 1
                }
            ]);
        Assert.True(withTrigger.Success);
        Assert.Single(state.Queue.AutoUseSlots[DaoHuaActionType.Exploration][2].Triggers!);
    }

    /// <summary>
    /// 验证一小时大步长快进推进与 3600 次逐秒推进在修为、完成数、采集品及丹药产出上完全等价（确定性补算）。
    /// </summary>
    [Fact]
    public void One_hour_fast_forward_matches_second_by_second_progression()
    {
        var fast = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "快进修士", Baseline);
        var stepped = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "逐秒修士", Baseline);

        DaoHuaSimulation.AdvanceTo(fast, Baseline.AddHours(1));
        for (var second = 1; second <= 3600; second++)
        {
            DaoHuaSimulation.AdvanceTo(stepped, Baseline.AddSeconds(second));
        }

        Assert.Equal(stepped.Stats.Qi, fast.Stats.Qi);
        Assert.Equal(stepped.Stats.CompletedActions, fast.Stats.CompletedActions);
        Assert.Equal(stepped.Stats.HerbsCollected, fast.Stats.HerbsCollected);
        Assert.Equal(stepped.Stats.PillsCrafted, fast.Stats.PillsCrafted);
        Assert.Equal(stepped.Inventory.Get(DaoHuaItemIds.SpiritHerb), fast.Inventory.Get(DaoHuaItemIds.SpiritHerb));
        Assert.Equal(stepped.Inventory.Get(DaoHuaItemIds.QiGatheringPill), fast.Inventory.Get(DaoHuaItemIds.QiGatheringPill));
    }

    /// <summary>
    /// 验证完全离线模式（FullyOffline）下不推进行动队列，仅更新最后模拟时间戳。
    /// </summary>
    [Fact]
    public void Fully_offline_player_does_not_advance_action_queue()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "离线修士", Baseline);
        state.PresenceMode = DaoHuaPresenceMode.FullyOffline;

        var result = DaoHuaSimulation.AdvanceTo(state, Baseline.AddDays(7));

        Assert.Equal(0, result.CompletedActions);
        Assert.Equal(0, state.Stats.CompletedActions);
        Assert.Equal(Baseline.AddDays(7), state.LastSimulatedAt);
    }

    /// <summary>
    /// 验证非循环模式下，当队首行动因缺少材料无法执行时立即终止并移除，后续可用行动顺利衔接。
    /// </summary>
    [Fact]
    public void Missing_action_ends_and_next_action_continues_when_loop_is_disabled()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "炼丹修士", Baseline);
        state.Queue.LoopEnabled = false;
        state.Queue.Entries =
        [
            new DaoHuaQueuedAction
            {
                ActionId = DaoHuaActionIds.RefineQiPill,
                DurationSeconds = DaoHuaActionCatalog.Get(DaoHuaActionIds.RefineQiPill).DurationSeconds
            },
            new DaoHuaQueuedAction
            {
                ActionId = DaoHuaActionIds.GatherHerbs,
                DurationSeconds = DaoHuaActionCatalog.Get(DaoHuaActionIds.GatherHerbs).DurationSeconds
            }
        ];

        var result = DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(1));

        Assert.Equal(0, result.CompletedActions);
        Assert.Single(state.Queue.Entries);
        Assert.Equal(DaoHuaActionIds.GatherHerbs, state.Queue.Entries[0].ActionId);
        Assert.Equal(1, state.Queue.Entries[0].ProgressSeconds);
        Assert.Equal(1, state.Stats.BlockedActions);
        Assert.Equal(0, state.Inventory.Get(DaoHuaItemIds.SpiritHerb));
        Assert.Equal(0, state.Inventory.Get(DaoHuaItemIds.QiGatheringPill));
    }

    /// <summary>
    /// 验证循环模式下，材料不足的行动被标记为 Blocked 并自动轮转至队尾，不中断队列中其他可用行动的执行。
    /// </summary>
    [Fact]
    public void Missing_action_moves_to_tail_and_next_action_runs_when_loop_is_enabled()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "跳过修士", Baseline);
        state.Queue.Entries =
        [
            new DaoHuaQueuedAction
            {
                ActionId = DaoHuaActionIds.RefineQiPill,
                DurationSeconds = DaoHuaActionCatalog.Get(DaoHuaActionIds.RefineQiPill).DurationSeconds
            },
            new DaoHuaQueuedAction
            {
                ActionId = DaoHuaActionIds.GatherHerbs,
                DurationSeconds = DaoHuaActionCatalog.Get(DaoHuaActionIds.GatherHerbs).DurationSeconds
            }
        ];

        var result = DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(1));

        Assert.Equal(0, result.CompletedActions);
        Assert.Equal(DaoHuaActionIds.GatherHerbs, state.Queue.Entries[0].ActionId);
        Assert.Equal(1, state.Queue.Entries[0].ProgressSeconds);
        Assert.Equal(DaoHuaActionIds.RefineQiPill, state.Queue.Entries[1].ActionId);
        Assert.Equal(DaoHuaActionStatus.Blocked, state.Queue.Entries[1].Status);
        Assert.Contains("skipped:alchemy.refine-qi-pill", result.Events);
    }

    /// <summary>
    /// 验证循环防空转机制：当队列中所有行动均不可用且被跳过一遍后，模拟器自动停止空转，避免死循环消耗 CPU。
    /// </summary>
    [Fact]
    public void Loop_stops_after_each_unavailable_action_was_skipped_once()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "空转保护修士", Baseline);
        state.Queue.Entries =
        [
            new DaoHuaQueuedAction
            {
                ActionId = DaoHuaActionIds.RefineQiPill,
                DurationSeconds = DaoHuaActionCatalog.Get(DaoHuaActionIds.RefineQiPill).DurationSeconds
            }
        ];

        var result = DaoHuaSimulation.AdvanceTo(state, Baseline.AddHours(1));

        Assert.Equal(0, result.CompletedActions);
        Assert.Equal(1, result.TransitionCount);
        Assert.Single(state.Queue.Entries);
        Assert.Equal(DaoHuaActionStatus.Blocked, state.Queue.Entries[0].Status);
        Assert.Equal(Baseline.AddHours(1), state.LastSimulatedAt);
    }

    /// <summary>
    /// 验证局部进度推进（行动未完成）只标记 QueueProgress 脏位，而不触发整条 Queue 脏标记。
    /// </summary>
    [Fact]
    public void Partial_advance_marks_only_queue_progress_dirty()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "进度修士", Baseline);
        state.DirtySections = DaoHuaDirtySections.None;

        var result = DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(1));

        Assert.True(result.DirtySections.HasFlag(DaoHuaDirtySections.QueueProgress));
        Assert.False(result.DirtySections.HasFlag(DaoHuaDirtySections.Queue));
        Assert.Equal(1, state.Queue.Entries[0].ProgressSeconds);
    }

    /// <summary>
    /// 验证重复下发相同运行时设置（如相同的循环开关、自动消耗品槽位）具有幂等性，不修改版本号且不产生脏标记。
    /// </summary>
    [Fact]
    public void Repeating_identical_realtime_settings_does_not_dirty_state()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "幂等修士", Baseline);
        state.DirtySections = DaoHuaDirtySections.None;
        var version = state.Version;

        DaoHuaSimulation.SetLoop(state, true);
        DaoHuaSimulation.SetAutoUse(
            state,
            DaoHuaActionType.Cultivation,
            DaoHuaItemIds.QiGatheringPill,
            1,
            true);

        Assert.Equal(version, state.Version);
        Assert.Equal(DaoHuaDirtySections.None, state.DirtySections);
    }

    /// <summary>
    /// 验证在探索（采药）和炼丹行动中，自动使用槽位配置引灵香/异火道具可按规则正确获得产出加成倍率。
    /// </summary>
    [Fact]
    public void Exploration_and_alchemy_auto_use_slots_apply_output_buffs()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "产出增益修士", Baseline);
        state.Queue.Entries =
        [
            new DaoHuaQueuedAction
            {
                ActionId = DaoHuaActionIds.GatherHerbs,
                DurationSeconds = DaoHuaActionCatalog.Get(DaoHuaActionIds.GatherHerbs).DurationSeconds
            }
        ];
        state.Inventory.Add(DaoHuaItemIds.GatheringIncense, 1);
        DaoHuaSimulation.SetAutoUse(state, DaoHuaActionType.Exploration, DaoHuaItemIds.GatheringIncense, 1, true);

        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(8));

        Assert.Equal(5, state.Inventory.Get(DaoHuaItemIds.SpiritHerb));
        Assert.Equal(0, state.Inventory.Get(DaoHuaItemIds.GatheringIncense));

        state.Queue.Entries =
        [
            new DaoHuaQueuedAction
            {
                ActionId = DaoHuaActionIds.RefineQiPill,
                DurationSeconds = DaoHuaActionCatalog.Get(DaoHuaActionIds.RefineQiPill).DurationSeconds
            }
        ];
        state.Inventory.Add(DaoHuaItemIds.SpiritHerb, 3);
        state.Inventory.Add(DaoHuaItemIds.AlchemyFlame, 1);
        DaoHuaSimulation.SetAutoUse(state, DaoHuaActionType.Alchemy, DaoHuaItemIds.AlchemyFlame, 1, true);
        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(20));

        Assert.Equal(2, state.Inventory.Get(DaoHuaItemIds.QiGatheringPill));
        Assert.Equal(0, state.Inventory.Get(DaoHuaItemIds.AlchemyFlame));
    }

    /// <summary>
    /// 验证玩家处于空闲或非战斗行动期间，依据角色属性每 10 秒自然回复生命值与法力值。
    /// </summary>
    [Fact]
    public void Continuous_regeneration_applies_in_idle_and_non_combat_actions()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "自动回复修士", Baseline);
        state.Queue.Entries.Clear();
        state.CombatResources = new DaoHuaCombatResourceState
        {
            Initialized = true,
            Hitpoints = 50,
            Manapoints = 20
        };

        var attributes = DaoHuaCombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, Baseline, state.Stats.Spirit);
        Assert.True(attributes.HpRegenPerTenSeconds > 0);
        Assert.True(attributes.MpRegenPerTenSeconds > 0);

        // 空闲 10 秒：正常享受自然自动回复
        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(10));
        var expectedHpGain = (long)Math.Floor(attributes.MaxHitpoints * attributes.HpRegenPerTenSeconds * (10_000.0 / 10_000.0));
        var expectedMpGain = (long)Math.Floor(attributes.MaxManapoints * attributes.MpRegenPerTenSeconds * (10_000.0 / 10_000.0));

        Assert.Equal(Math.Min(attributes.MaxHitpoints, 50 + expectedHpGain), state.CombatResources.Hitpoints);
        Assert.Equal(Math.Min(attributes.MaxManapoints, 20 + expectedMpGain), state.CombatResources.Manapoints);

        // 加入非战斗行动（采药），推进 8 秒：持续运转回复
        state.Queue.Entries.Add(new DaoHuaQueuedAction
        {
            ActionId = DaoHuaActionIds.GatherHerbs,
            DurationSeconds = 8
        });

        var prevHp = state.CombatResources.Hitpoints;
        var prevMp = state.CombatResources.Manapoints;
        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(18));
        Assert.True(state.CombatResources.Hitpoints >= prevHp);
        Assert.True(state.CombatResources.Manapoints >= prevMp);
    }

    /// <summary>
    /// 验证玩家死亡（生命值为 0）状态下，灵力依然正常自动回复，而生命值保持为 0 直至完全重生。
    /// </summary>
    [Fact]
    public void Death_state_regenerates_mana_while_health_remains_zero_until_full_respawn()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "死亡回蓝修士", Baseline);
        state.Queue.Entries.Clear();
        state.CombatResources = new DaoHuaCombatResourceState
        {
            Initialized = true,
            Hitpoints = 0,
            Manapoints = 20
        };

        // 处于死亡状态（生命为 0）：空闲推进 10 秒
        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(10));

        // 生命值依然为 0（未复活不回血），而灵力正常自动回复
        Assert.Equal(0, state.CombatResources.Hitpoints);
        Assert.True(state.CombatResources.Manapoints > 20);
    }

    /// <summary>
    /// 验证脱离战斗（通过移除队列或战斗结束）时，存活玩家保持现有血量/法力，而死亡玩家脱战复活至 1 点生命保底。
    /// </summary>
    [Fact]
    public void Leaving_combat_by_remove_or_finish_revives_dead_player_to_one_hp_and_preserves_living()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "脱战复活修士", Baseline);
        state.Queue.Entries.Clear();
        state.CombatResources = new DaoHuaCombatResourceState
        {
            Initialized = true,
            Hitpoints = 50,
            Manapoints = 30
        };

        // 存活状态下加入历练并移除：保持原有生命与灵力
        Assert.True(DaoHuaSimulation.StartCombat(state, DaoHuaActionIds.HuntQiBeast, 1).Success);
        Assert.Equal(50, state.CombatResources.Hitpoints);
        Assert.Equal(30, state.CombatResources.Manapoints);

        var actionId = state.Queue.Entries[0].EntryId;
        Assert.True(DaoHuaSimulation.Remove(state, actionId).Success);
        Assert.Equal(50, state.CombatResources.Hitpoints);
        Assert.Equal(30, state.CombatResources.Manapoints);

        // 死亡状态下（生命值 0）进入战斗后被移除：脱战复活至 1 点生命，灵力不回复
        state.CombatResources.Hitpoints = 0;
        state.CombatResources.Manapoints = 25;
        Assert.True(DaoHuaSimulation.StartCombat(state, DaoHuaActionIds.HuntQiBeast, 1).Success);
        // StartCombat 脱战复活至 1 点血
        Assert.Equal(1, state.CombatResources.Hitpoints);
        Assert.Equal(25, state.CombatResources.Manapoints);

        // 模拟战斗被打死
        state.CombatResources.Hitpoints = 0;
        state.CombatResources.Manapoints = 10;
        var combatEntryId = state.Queue.Entries[0].EntryId;
        Assert.True(DaoHuaSimulation.Remove(state, combatEntryId).Success);

        // 移除后脱离战斗复活：生命值为 1 点，灵力保持 10 点，保证不死
        Assert.Equal(1, state.CombatResources.Hitpoints);
        Assert.Equal(10, state.CombatResources.Manapoints);
    }

    /// <summary>
    /// 验证玩家身上的持续 Buff 状态在进入战斗、脱离战斗以及切换其他非战斗行动时均持久保留，不被意外清除。
    /// </summary>
    [Fact]
    public void Buffs_persist_and_do_not_reset_on_combat_exit_or_switch()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "永久增益修士", Baseline);
        state.Buffs.Add(new DaoHuaBuffState
        {
            BuffId = "test_buff",
            Name = "聚元诀",
            Stacks = 2,
            DamageRatio = 0.2,
            ExpiresAt = Baseline.AddMinutes(30)
        });

        // 开始战斗
        Assert.True(DaoHuaSimulation.StartCombat(state, DaoHuaActionIds.HuntQiBeast, 1).Success);
        Assert.Single(state.Buffs);
        Assert.Equal("test_buff", state.Buffs[0].BuffId);
        Assert.Equal(2, state.Buffs[0].Stacks);

        // 取消战斗
        var combatId = state.Queue.Entries[0].EntryId;
        Assert.True(DaoHuaSimulation.Remove(state, combatId).Success);
        Assert.Single(state.Buffs);
        Assert.Equal("test_buff", state.Buffs[0].BuffId);

        // 切换到赶路或采药
        state.Queue.Entries.Add(new DaoHuaQueuedAction
        {
            ActionId = DaoHuaActionIds.GatherHerbs,
            DurationSeconds = 8
        });
        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(8));
        Assert.Single(state.Buffs);
        Assert.Equal("test_buff", state.Buffs[0].BuffId);
    }

    /// <summary>
    /// 验证当队列中所有行动都因条件不满足而空转时，角色依然持续获得自然自动回复。
    /// </summary>
    [Fact]
    public void Continuous_regeneration_applies_while_all_loop_actions_are_blocked()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "空转回复修士", Baseline);
        state.Queue.Entries.Clear();
        state.Queue.LoopEnabled = true;
        state.CombatResources = new DaoHuaCombatResourceState
        {
            Initialized = true,
            Hitpoints = 50,
            Manapoints = 20
        };

        // 队列中只有一个条件不满足的行动（缺少灵草），循环模式下会被跳过并空转。
        state.Queue.Entries.Add(new DaoHuaQueuedAction
        {
            ActionId = DaoHuaActionIds.RefineQiPill,
            DurationSeconds = DaoHuaActionCatalog.Get(DaoHuaActionIds.RefineQiPill).DurationSeconds
        });

        var attributes = DaoHuaCombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, Baseline, state.Stats.Spirit);
        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(10));

        // 空转的 10 秒同样享受自动回复
        var expectedHpGain = (long)Math.Floor(attributes.MaxHitpoints * attributes.HpRegenPerTenSeconds * 1.0);
        var expectedMpGain = (long)Math.Floor(attributes.MaxManapoints * attributes.MpRegenPerTenSeconds * 1.0);
        Assert.Equal(Math.Min(attributes.MaxHitpoints, 50 + expectedHpGain), state.CombatResources.Hitpoints);
        Assert.Equal(Math.Min(attributes.MaxManapoints, 20 + expectedMpGain), state.CombatResources.Manapoints);
    }

    /// <summary>
    /// 验证历练在多轮遭遇战之间正确保留战斗损耗（残血/残蓝），绝不在新一轮遭遇战开始时违规重置回满。
    /// </summary>
    [Fact]
    public void Combat_encounters_preserve_damaged_resources_across_battles()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "连战修士", Baseline);
        state.Queue.Entries.Clear();
        state.CombatResources = new DaoHuaCombatResourceState
        {
            Initialized = true,
            Hitpoints = 80,
            Manapoints = 30
        };

        // 启动历练，重复 2 次
        Assert.True(DaoHuaSimulation.StartCombat(state, DaoHuaActionIds.HuntQiBeast, 2).Success);
        Assert.Equal(80, state.CombatResources.Hitpoints);
        Assert.Equal(30, state.CombatResources.Manapoints);

        // 推进 60 秒（覆盖若干遭遇战与搜寻）
        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(60));

        // 检查生命与灵力：没有被完全回满至 MaxHitpoints / MaxManapoints
        var attributes = DaoHuaCombatSimulator.CreatePlayerAttributes(state.Combat, state.Buffs, Baseline, state.Stats.Spirit);
        // 如果受伤或释放技能消耗灵力，绝不自动满状态
        Assert.True(state.CombatResources.Hitpoints <= attributes.MaxHitpoints);
        Assert.True(state.CombatResources.Manapoints <= attributes.MaxManapoints);
    }

    /// <summary>
    /// 验证战斗结束或切换波次时，未到期的技能与消耗品冷却时间不会被强制清零重置。
    /// </summary>
    [Fact]
    public void Combat_end_does_not_clear_skill_and_consumable_cooldowns()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "历练修士", Baseline);
        state.Queue.Entries.Clear();
        state.Stats.Spirit = 100;
        state.CombatResources = new DaoHuaCombatResourceState
        {
            Initialized = true,
            Hitpoints = 500,
            Manapoints = 200
        };
        state.Inventory.Add(DaoHuaItemIds.FireTalisman, 5);
        state.Combat.ConsumableSlots[0] = new DaoHuaConsumableSlot
        {
            ItemId = DaoHuaItemIds.FireTalisman,
            Quantity = 1,
            Enabled = true,
            Triggers =
            [
                new DaoHuaCombatTrigger
                {
                    Dependency = DaoHuaCombatTriggerDependency.Self,
                    Condition = DaoHuaCombatTriggerCondition.CurrentHitpoints,
                    Comparator = DaoHuaCombatTriggerComparator.GreaterThanOrEqual,
                    Value = 1
                }
            ]
        };

        // 启动历练
        Assert.True(DaoHuaSimulation.StartCombat(state, DaoHuaActionIds.HuntQiBeast, 10).Success);

        // 推进到初次搜寻结束并进入战斗且发生使用 (初次搜寻20秒 + 战斗5秒)
        DaoHuaSimulation.AdvanceTo(state, Baseline.AddSeconds(25));

        // 验证消耗品使用后产生冷却
        var hasCooldown = state.CombatResources.ConsumableSlotCooldowns.Count > 0
            && state.CombatResources.ConsumableSlotCooldowns[0] > state.LastSimulatedAt;
        Assert.True(hasCooldown);

        // 推进到下一场搜寻（10秒后）
        var previousSimulatedAt = state.LastSimulatedAt;
        DaoHuaSimulation.AdvanceTo(state, previousSimulatedAt.AddSeconds(10));

        // 冷却时间随物理时间自然缩短，但未到期的冷却不会被重置清除
        var remaining = (state.CombatResources.ConsumableSlotCooldowns[0] - state.LastSimulatedAt).TotalMilliseconds;
        Assert.True(remaining > 0);
    }

    /// <summary>
    /// 验证脱离战斗后，尚未结束的技能与消耗品冷却时间完好保存在 CombatResources 中。
    /// </summary>
    [Fact]
    public void Leaving_combat_preserves_cooldowns_in_combat_resources()
    {
        var state = DaoHuaSimulation.CreateDefaultState(Guid.NewGuid(), "脱战修士", Baseline);
        state.Queue.Entries.Clear();
        state.CombatResources.AbilityCooldowns[DaoHuaCombatAbilityIds.HeavySlash] = Baseline.AddSeconds(20);
        state.CombatResources.ConsumableSlotCooldowns = [Baseline.AddSeconds(15)];

        // 启动历练然后移除队列（脱离战斗）
        Assert.True(DaoHuaSimulation.StartCombat(state, DaoHuaActionIds.HuntQiBeast, 1).Success);
        var entryId = state.Queue.Entries[0].EntryId;
        Assert.True(DaoHuaSimulation.Remove(state, entryId).Success);

        // 确认脱战后，技能和物品冷却依然完好保存在 CombatResources
        Assert.True(state.CombatResources.AbilityCooldowns.ContainsKey(DaoHuaCombatAbilityIds.HeavySlash));
        Assert.Equal(Baseline.AddSeconds(20), state.CombatResources.AbilityCooldowns[DaoHuaCombatAbilityIds.HeavySlash]);
        Assert.Equal(Baseline.AddSeconds(15), state.CombatResources.ConsumableSlotCooldowns[0]);
    }
}
