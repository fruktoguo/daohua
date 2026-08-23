// ==============================================================================
// 文件名称: ActionTransitionSystemTests.cs
// 主要职责: 验证 ECS 薄组件纯数据特性与唯一 ActionTransitionSystem 的原子过渡、生命周期流转与预算边界。
// 核心逻辑 / 定义内容:
//   1. 验证 ActionQueueComponent 与各薄组件纯数据容器特性（无自发出队/结算）。
//   2. 验证 ActionTransitionSystem.ApplyAdvanceResult 在单个 Fiber 临界区内原子提交所有 Delta。
//   3. 验证队列生命周期：有限次数 vs 无限次数 vs 队列循环开启/关闭时的克隆与出队机制。
//   4. 验证受阻（Blocked）状态流转与错误码记录。
//   5. 验证多步离散事件推进（AdvanceEntityTo）与预算上限（Cap）可续算不变量。
//   6. 验证 YuoCore 原生 IActionTransitionDue 标签调度机制。
// ==============================================================================

namespace Domain.Tests;

using System.Collections.Frozen;
using Domain.Common;
using Domain.Components;
using Domain.Constants;
using Domain.Definitions;
using Domain.Models;
using Domain.Simulation;
using Domain.Systems;
using YuoCore.Ecs;

/// <summary>
/// 验证薄 ECS 运行时组件与唯一 ActionTransitionSystem 的领域不变量与系统行为。
/// </summary>
public sealed class ActionTransitionSystemTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-05T00:00:00Z");

    private static (YuoWorld World, YuoEntity Entity) CreateTestPlayerEntity()
    {
        var world = new YuoWorld();
        world.LoadAssembly(typeof(ActionTransitionSystem).Assembly);
        world.Refresh();
        var entity = new YuoEntity(world);

        var queue = entity.AddComponent<ActionQueueComponent>();
        var inventory = entity.AddComponent<InventoryComponent>();
        var cultivation = entity.AddComponent<CultivationComponent>();
        var combatResources = entity.AddComponent<CombatResourcesComponent>();
        var stats = entity.AddComponent<PlayerStatsComponent>();
        var dirty = entity.AddComponent<SyncDirtyComponent>();

        cultivation.LastSimulatedAt = Baseline;
        cultivation.PresenceMode = PresenceMode.Online;

        return (world, entity);
    }

    /// <summary>
    /// 验证 ActionQueueComponent 与薄组件仅作为纯数据容器，具备准确的只读派生属性，绝无自发出队或副作用。
    /// </summary>
    [Fact]
    public void Components_behave_as_pure_data_containers_without_self_mutation()
    {
        var (_, entity) = CreateTestPlayerEntity();
        var queue = entity.GetComponent<ActionQueueComponent>();
        var inventory = entity.GetComponent<InventoryComponent>();

        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.Count);
        Assert.Null(queue.Current);

        var entry1 = new QueueEntryState { ActionId = ActionIds.GatherHerbs, RemainingRepeats = 2 };
        var entry2 = new QueueEntryState { ActionId = ActionIds.DrawQi, RemainingRepeats = 1 };
        queue.Entries.Add(entry1);
        queue.Entries.Add(entry2);

        Assert.False(queue.IsEmpty);
        Assert.Equal(2, queue.Count);
        Assert.Same(entry1, queue.Current);

        // 验证 InventoryComponent 只读查询与快照
        inventory.Items[ItemIds.SpiritHerb] = 5.0;
        Assert.Equal(5.0, inventory.Get(ItemIds.SpiritHerb));
        Assert.Equal(0.0, inventory.Get(ItemIds.QiGatheringPill));
        Assert.True(inventory.Has(ItemIds.SpiritHerb, 3.0));
        Assert.False(inventory.Has(ItemIds.SpiritHerb, 10.0));

        var snapshot = inventory.ToFrozenSnapshot();
        Assert.Equal(5.0, snapshot[ItemIds.SpiritHerb]);
    }

    /// <summary>
    /// 验证 ActionTransitionSystem.ApplyAdvanceResult 能够跨全部薄组件原子应用增量，更新脏标记与模拟时间。
    /// </summary>
    [Fact]
    public void ApplyAdvanceResult_atomically_commits_deltas_across_all_components()
    {
        var (_, entity) = CreateTestPlayerEntity();
        var queue = entity.GetComponent<ActionQueueComponent>();
        var inventory = entity.GetComponent<InventoryComponent>();
        var cultivation = entity.GetComponent<CultivationComponent>();
        var combatResources = entity.GetComponent<CombatResourcesComponent>();
        var stats = entity.GetComponent<PlayerStatsComponent>();
        var dirty = entity.GetComponent<SyncDirtyComponent>();

        var entry = new QueueEntryState
        {
            ActionId = ActionIds.GatherHerbs,
            RemainingRepeats = 5,
            TotalRepeats = 5,
            Status = ActionStatus.Running
        };
        queue.Entries.Add(entry);
        inventory.Items[ItemIds.Spirit] = 20.0;

        var resultTime = Baseline.AddSeconds(10);
        var advanceResult = new ActionAdvanceResult
        {
            IdempotencyKey = "test-step-1",
            ConsumedUntil = resultTime,
            Completed = true,
            RemainingRepeatsDelta = -1,
            NextProgressMilliseconds = 0,
            Delta = new ActionDelta
            {
                Stats = new ActionPlayerStatDelta
                {
                    QiDelta = 25.0,
                    SpiritDelta = -5.0,
                    HerbsCollectedDelta = 1,
                    CompletedActionsDelta = 1
                },
                CombatResources = new ActionCombatResourceDelta
                {
                    HitpointsDelta = -15.0,
                    ManapointsDelta = 10.0,
                    HitpointRemainder = 0.25,
                    AbilityCooldownUpdates = new Dictionary<string, DateTimeOffset>
                    {
                        ["skill-slash"] = resultTime.AddSeconds(5)
                    }
                }
            },
            Events =
            [
                new ActionEventDelta
                {
                    IdempotencyKey = "evt-1",
                    OccurredAt = resultTime,
                    Template = "采集完成",
                    ActionType = ActionType.Exploration,
                    Category = "gather"
                }
            ]
        };
        advanceResult.Delta.AddItemDelta(ItemIds.SpiritHerb, 2.0);
        advanceResult.Delta.AddItemDelta(ItemIds.Spirit, -5.0);

        // 调用唯一的过渡系统提交
        ActionTransitionSystem.ApplyAdvanceResult(entity, advanceResult);

        // 1. 验证背包
        Assert.Equal(2.0, inventory.Get(ItemIds.SpiritHerb));
        Assert.Equal(15.0, inventory.Get(ItemIds.Spirit));

        // 2. 验证修为与统计
        Assert.Equal(25.0, cultivation.Qi);
        Assert.Equal(-5.0, stats.Spirit);
        Assert.Equal(1, stats.HerbsCollected);
        Assert.Equal(1, stats.CompletedActions);

        // 3. 验证战斗资源
        Assert.Equal(-15.0, combatResources.Hitpoints);
        Assert.Equal(10.0, combatResources.Manapoints);
        Assert.Equal(0.25, combatResources.HitpointRemainder);
        Assert.Equal(resultTime.AddSeconds(5), combatResources.AbilityCooldowns["skill-slash"]);

        // 4. 验证队列生命周期
        Assert.Equal(4, entry.RemainingRepeats);
        Assert.Equal(0, entry.ProgressMilliseconds);
        Assert.Equal(ActionStatus.Running, entry.Status);
        Assert.Equal(1, entry.ExecutionRevision);

        // 5. 验证事件与脏标记
        var singleEvent = Assert.Single(dirty.PendingEvents);
        Assert.Equal(1, singleEvent.Sequence);
        Assert.Equal("采集完成", singleEvent.Template);
        Assert.True(dirty.IsDirty(DirtySections.Inventory));
        Assert.True(dirty.IsDirty(DirtySections.Stats));
        Assert.True(dirty.IsDirty(DirtySections.Combat));
        Assert.True(dirty.IsDirty(DirtySections.Events));
        Assert.True(dirty.IsDirty(DirtySections.Queue));
        Assert.Equal(resultTime, cultivation.LastSimulatedAt);
        Assert.Equal(1, dirty.Version);
    }

    /// <summary>
    /// 验证队列完成时的生命周期流转：有限次数在 LoopEnabled=true 下克隆新条目至队尾，LoopEnabled=false 时彻底移出队列。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActionTransitionSystem_manages_queue_completion_and_loop_cloning(bool loopEnabled)
    {
        var (_, entity) = CreateTestPlayerEntity();
        var queue = entity.GetComponent<ActionQueueComponent>();
        queue.LoopEnabled = loopEnabled;

        var originalEntryId = Guid.NewGuid();
        var entry = new QueueEntryState
        {
            EntryId = originalEntryId,
            ActionId = ActionIds.RefineQiPill,
            TotalRepeats = 1,
            RemainingRepeats = 1,
            Status = ActionStatus.Running
        };
        queue.Entries.Add(entry);

        var finishTime = Baseline.AddSeconds(12);
        var completeResult = new ActionAdvanceResult
        {
            ConsumedUntil = finishTime,
            Completed = true,
            RemainingRepeatsDelta = -1
        };

        ActionTransitionSystem.ApplyAdvanceResult(entity, completeResult);

        if (loopEnabled)
        {
            // 循环开启：原条目移出，克隆项追加至末尾
            var loopedEntry = Assert.Single(queue.Entries);
            Assert.NotEqual(originalEntryId, loopedEntry.EntryId);
            Assert.Equal(ActionIds.RefineQiPill, loopedEntry.ActionId);
            Assert.Equal(1, loopedEntry.RemainingRepeats);
            Assert.Equal(0, loopedEntry.ProgressMilliseconds);
            Assert.Equal(ActionStatus.Running, loopedEntry.Status);
        }
        else
        {
            // 循环关闭：彻底出队
            Assert.True(queue.IsEmpty);
            Assert.Null(queue.Current);
        }
    }

    /// <summary>
    /// 验证无限循环任务（RemainingRepeats < 0）完成时不减少次数且不旋转出队。
    /// </summary>
    [Fact]
    public void ActionTransitionSystem_handles_infinite_action_completion()
    {
        var (_, entity) = CreateTestPlayerEntity();
        var queue = entity.GetComponent<ActionQueueComponent>();

        var entry = new QueueEntryState
        {
            ActionId = ActionIds.DrawQi,
            RemainingRepeats = -1,
            Status = ActionStatus.Running,
            ProgressMilliseconds = 5000
        };
        queue.Entries.Add(entry);

        var result = new ActionAdvanceResult
        {
            ConsumedUntil = Baseline.AddSeconds(10),
            Completed = true,
            RemainingRepeatsDelta = -1,
            NextProgressMilliseconds = 0
        };

        ActionTransitionSystem.ApplyAdvanceResult(entity, result);

        Assert.Equal(1, queue.Count);
        Assert.Equal(-1, queue.Current!.RemainingRepeats);
        Assert.True(queue.Current.IsInfinite);
        Assert.Equal(0, queue.Current.ProgressMilliseconds);
        Assert.Equal(ActionStatus.Running, queue.Current.Status);
    }

    /// <summary>
    /// 验证受阻（Blocked）状态流转与原因码记录，不发生非法扣减。
    /// </summary>
    [Fact]
    public void ActionTransitionSystem_records_blocked_status_and_reason_code()
    {
        var (_, entity) = CreateTestPlayerEntity();
        var queue = entity.GetComponent<ActionQueueComponent>();
        var dirty = entity.GetComponent<SyncDirtyComponent>();

        var entry = new QueueEntryState
        {
            ActionId = ActionIds.RefineQiPill,
            RemainingRepeats = 3,
            Status = ActionStatus.Queued
        };
        queue.Entries.Add(entry);

        var blockResult = ActionAdvanceResult.Block(
            Baseline.AddSeconds(2),
            ActionBlockReasons.MissingInputs,
            "block-key-1");

        ActionTransitionSystem.ApplyAdvanceResult(entity, blockResult);

        Assert.Equal(ActionStatus.Blocked, entry.Status);
        Assert.Equal(ActionBlockReasons.MissingInputs, entry.BlockReasonCode);
        Assert.True(dirty.IsDirty(DirtySections.Queue));
    }

    /// <summary>
    /// 验证 AdvanceEntityTo 的离散时间推进与步数预算上限（Cap）可续算不变量。
    /// </summary>
    [Fact]
    public void AdvanceEntityTo_handles_discrete_progression_and_budget_capping()
    {
        var (_, entity) = CreateTestPlayerEntity();
        var queue = entity.GetComponent<ActionQueueComponent>();
        var inventory = entity.GetComponent<InventoryComponent>();
        var cultivation = entity.GetComponent<CultivationComponent>();

        // 添加一个吐纳任务（耗时 10 秒，产出灵气）
        queue.Entries.Add(new QueueEntryState
        {
            ActionId = ActionIds.DrawQi,
            RemainingRepeats = 5,
            TotalRepeats = 5
        });

        // 1. 推进 5 秒：不足以完成单次动作（10s），只累加进度
        var midResult = ActionTransitionSystem.AdvanceEntityTo(entity, Baseline.AddSeconds(5));
        Assert.True(midResult.ReachedTarget);
        Assert.False(midResult.Capped);
        Assert.Equal(0, midResult.CompletedActions);
        Assert.Equal(5000, queue.Current!.ProgressMilliseconds);
        Assert.Equal(0.0, cultivation.Qi);
        Assert.Equal(Baseline.AddSeconds(5), cultivation.LastSimulatedAt);

        // 2. 再推进 5 秒（总共 10 秒）：恰好到达完成边界，结算产出
        var completeResult = ActionTransitionSystem.AdvanceEntityTo(entity, Baseline.AddSeconds(10));
        Assert.True(completeResult.ReachedTarget);
        Assert.False(completeResult.Capped);
        Assert.Equal(1, completeResult.CompletedActions);
        Assert.Equal(0, queue.Current.ProgressMilliseconds);
        Assert.True(cultivation.Qi > 0);
        Assert.Equal(Baseline.AddSeconds(10), cultivation.LastSimulatedAt);

        // 3. 验证步数预算（maxTransitions）：限制步数为 1
        var cappedResult = ActionTransitionSystem.AdvanceEntityTo(
            entity,
            Baseline.AddSeconds(100),
            maxTransitions: 1);

        Assert.True(cappedResult.Capped);
        Assert.False(cappedResult.ReachedTarget);
        Assert.Equal(1, cappedResult.TransitionCount);
        // 验证模拟游标停留在已结算的时间点（20s），没有直接跳到 100s
        Assert.Equal(Baseline.AddSeconds(20), cultivation.LastSimulatedAt);
    }

    /// <summary>
    /// 验证通过 YuoCore 原生 RunSystem 标签机制调度 ActionTransitionSystem，需显式注入目标时间并真实推进游标与进度。
    /// </summary>
    [Fact]
    public void YuoCore_tag_dispatch_triggers_ActionTransitionSystem_with_explicit_target()
    {
        var (world, entity) = CreateTestPlayerEntity();
        var queue = entity.GetComponent<ActionQueueComponent>();
        var cultivation = entity.GetComponent<CultivationComponent>();

        queue.Entries.Add(new QueueEntryState
        {
            ActionId = ActionIds.GatherHerbs,
            RemainingRepeats = 2
        });

        var targetTime = Baseline.AddSeconds(5);
        entity.AddComponent<ActionTransitionTargetComponent>().TargetTime = targetTime;

        // 通过 YuoWorld.RunSystem 触发 IActionTransitionDue
        YuoWorld.RunSystem<IActionTransitionDue>(entity);

        // 验证系统已被成功触发：组件被一次性消费、时间游标真实推进、单步进度推进 5000ms
        Assert.Null(entity.GetComponent<ActionTransitionTargetComponent>());
        Assert.Equal(targetTime, cultivation.LastSimulatedAt);
        Assert.Equal(5000, queue.Current!.ProgressMilliseconds);
        Assert.Equal(ActionStatus.Running, queue.Current.Status);
        Assert.True(queue.Current.ExecutionRevision > 0);
    }

    /// <summary>
    /// 验证当缺失 ActionTransitionTargetComponent 目标时间戳时直接执行 ActionTransitionSystem 会快速失败。
    /// </summary>
    [Fact]
    public void ActionTransitionSystem_throws_when_target_component_missing()
    {
        var (_, entity) = CreateTestPlayerEntity();
        var queue = entity.GetComponent<ActionQueueComponent>();

        queue.Entries.Add(new QueueEntryState
        {
            ActionId = ActionIds.GatherHerbs,
            RemainingRepeats = 2
        });

        var system = new ActionTransitionSystem();
        Assert.Throws<InvalidOperationException>(() => system.Run(queue));
    }

    /// <summary>
    /// 验证 ApplyAdvanceResult 能够原子提交 Buff 的新增、叠加、驱散以及大地图移动增量（WorldDelta）。
    /// </summary>
    [Fact]
    public void ApplyAdvanceResult_atomically_updates_buffs_and_world_delta()
    {
        var (_, entity) = CreateTestPlayerEntity();
        var queue = entity.GetComponent<ActionQueueComponent>();
        var cultivation = entity.GetComponent<CultivationComponent>();
        var stats = entity.GetComponent<PlayerStatsComponent>();
        var dirty = entity.GetComponent<SyncDirtyComponent>();

        var entry = new QueueEntryState
        {
            ActionId = ActionIds.GatherHerbs,
            RemainingRepeats = 1,
            Travel = new TravelPlan
            {
                MapId = WorldMapIds.QingyunMountains,
                DestinationNodeId = WorldNodeIds.YunxiVillage
            }
        };
        queue.Entries.Add(entry);

        cultivation.Buffs.Add(new BuffState
        {
            BuffId = "buff-old",
            Name = "旧增益",
            Stacks = 1,
            ExpiresAt = Baseline.AddSeconds(10)
        });

        var result = new ActionAdvanceResult
        {
            ConsumedUntil = Baseline.AddSeconds(10),
            Completed = false,
            Delta = new ActionDelta
            {
                Stats = new ActionPlayerStatDelta
                {
                    CultivationCyclesDelta = 3
                },
                AddedBuffs =
                [
                    new BuffState { BuffId = "buff-new", Name = "新增益", Stacks = 2, ExpiresAt = Baseline.AddMinutes(5) }
                ],
                RemovedBuffIds = ["buff-old"],
                World = new ActionWorldDelta
                {
                    TargetMapId = WorldMapIds.QingyunMountains,
                    TargetNodeId = WorldNodeIds.TeaHouse
                }
            }
        };

        ActionTransitionSystem.ApplyAdvanceResult(entity, result);

        // 验证 Buff 列表更新
        Assert.DoesNotContain(cultivation.Buffs, b => b.BuffId == "buff-old");
        var newBuff = Assert.Single(cultivation.Buffs, b => b.BuffId == "buff-new");
        Assert.Equal(2, newBuff.Stacks);

        // 验证 WorldDelta 作用于 Travel 与脏标记
        Assert.Equal(WorldNodeIds.TeaHouse, entry.Travel?.DestinationNodeId);
        Assert.True(dirty.IsDirty(DirtySections.World));
        Assert.True(dirty.IsDirty(DirtySections.Buffs));

        // 验证 CultivationCycles 统计
        Assert.Equal(3, stats.CultivationCycles);
        Assert.Equal(3, cultivation.CultivationCycles);
    }

    /// <summary>
    /// 验证 AdvanceEntityTo 能够按序连续推进多个不同任务并在 LoopEnabled 时形成闭环。
    /// </summary>
    [Fact]
    public void AdvanceEntityTo_advances_multiple_entries_sequentially_with_loop()
    {
        var (_, entity) = CreateTestPlayerEntity();
        var queue = entity.GetComponent<ActionQueueComponent>();
        var inventory = entity.GetComponent<InventoryComponent>();
        var cultivation = entity.GetComponent<CultivationComponent>();

        queue.LoopEnabled = true;
        // 动作1：采集灵草（10s，产出灵草）
        queue.Entries.Add(new QueueEntryState
        {
            ActionId = ActionIds.GatherHerbs,
            TotalRepeats = 1,
            RemainingRepeats = 1
        });
        // 动作2：吐纳灵气（10s，产出灵气）
        queue.Entries.Add(new QueueEntryState
        {
            ActionId = ActionIds.DrawQi,
            TotalRepeats = 1,
            RemainingRepeats = 1
        });

        // 推进 20 秒：依次完成两个动作
        var result = ActionTransitionSystem.AdvanceEntityTo(entity, Baseline.AddSeconds(20));

        Assert.True(result.ReachedTarget);
        Assert.False(result.Capped);
        Assert.Equal(2, result.CompletedActions);
        Assert.Equal(2, queue.Count);
        Assert.True(inventory.Get(ItemIds.SpiritHerb) > 0);
        Assert.True(cultivation.Qi > 0);
        Assert.Equal(Baseline.AddSeconds(20), cultivation.LastSimulatedAt);
    }

    /// <summary>
    /// 验证当队列中首个任务受阻且存在多个任务时，AdvanceEntityTo 能够轮换到后续可用任务执行。
    /// </summary>
    [Fact]
    public void AdvanceEntityTo_rotates_blocked_entries_when_multiple_items_exist()
    {
        var (_, entity) = CreateTestPlayerEntity();
        var queue = entity.GetComponent<ActionQueueComponent>();
        var cultivation = entity.GetComponent<CultivationComponent>();

        queue.LoopEnabled = true;
        // 动作1：炼丹（缺少药材，受阻）
        queue.Entries.Add(new QueueEntryState
        {
            ActionId = ActionIds.RefineQiPill,
            TotalRepeats = 1,
            RemainingRepeats = 1
        });
        // 动作2：吐纳（无消耗，10s）
        queue.Entries.Add(new QueueEntryState
        {
            ActionId = ActionIds.DrawQi,
            TotalRepeats = 1,
            RemainingRepeats = 1
        });

        // 推进 10 秒：动作1受阻轮换到末尾，动作2成功执行并完成
        var result = ActionTransitionSystem.AdvanceEntityTo(entity, Baseline.AddSeconds(10));

        Assert.True(result.ReachedTarget);
        Assert.Equal(1, result.CompletedActions);
        Assert.True(cultivation.Qi > 0);
    }
}
