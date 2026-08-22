// ==============================================================================
// 文件名称: ActionAdvanceContractsTests.cs
// 主要职责: 验证动作推进纯领域契约（ActionAdvanceContracts）的不可变性、序列化往返与增量计算契约。
// 核心逻辑 / 定义内容:
//   1. 验证 ActionAdvanceResult 与 ActionDelta 的 JSON 序列化/反序列化往返数据完整性。
//   2. 验证 ActionDelta 中物品增量累加与归零自动清理语义。
//   3. 验证 ActionPlayerStatDelta 与 ActionCombatResourceDelta 的变更标记（HasChanges）。
//   4. 验证 Unchanged 与 Block 便捷工厂方法的初始状态与稳定错误码。
// ==============================================================================

using System.Collections.Frozen;
using System.Text.Json;
using Domain.Common;
using Domain.Constants;
using Domain.Models;
using Domain.Simulation;

namespace Domain.Tests;

/// <summary>
/// 验证 ActionAdvanceContracts 纯领域契约数据结构的正确性与序列化契约。
/// </summary>
public sealed class ActionAdvanceContractsTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.Parse("2026-08-05T00:00:00Z");

    /// <summary>
    /// 验证 ActionAdvanceResult 可以完整进行 JSON 序列化并准确反序列化，所有关键字段不丢失。
    /// </summary>
    [Fact]
    public void ActionAdvanceResult_roundtrips_through_json_serialization()
    {
        var result = new ActionAdvanceResult
        {
            IdempotencyKey = "test-player:entry-1:rev-1:step-1",
            ConsumedUntil = Baseline.AddSeconds(10),
            Completed = true,
            RemainingRepeatsDelta = -1,
            Blocked = false,
            BlockReasonCode = null,
            NextPhase = CombatExpeditionPhase.Search,
            NextProgressMilliseconds = 1500,
            NextTransitionHint = Baseline.AddSeconds(15),
            Delta = new ActionDelta
            {
                Stats = new ActionPlayerStatDelta
                {
                    QiDelta = 100.5,
                    HerbsCollectedDelta = 2,
                    CompletedActionsDelta = 1
                },
                CombatResources = new ActionCombatResourceDelta
                {
                    HitpointsDelta = -25.0,
                    ManapointsDelta = 10.0,
                    HitpointRemainder = 0.5
                },
                World = new ActionWorldDelta
                {
                    TargetNodeId = WorldNodeIds.HerbVale,
                    EdgeProgress = 0.75
                }
            },
            Events =
            [
                new ActionEventDelta
                {
                    IdempotencyKey = "event-1",
                    OccurredAt = Baseline.AddSeconds(10),
                    Template = "采集完成",
                    ActionType = ActionType.Exploration,
                    Category = "gather"
                }
            ]
        };
        result.Delta.AddItemDelta(ItemIds.SpiritHerb, 3);
        result.Delta.AddItemDelta(ItemIds.QiGatheringPill, -1);

        var json = JsonSerializer.Serialize(result);
        var deserialized = JsonSerializer.Deserialize<ActionAdvanceResult>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(result.IdempotencyKey, deserialized.IdempotencyKey);
        Assert.Equal(result.ConsumedUntil, deserialized.ConsumedUntil);
        Assert.True(deserialized.Completed);
        Assert.Equal(-1, deserialized.RemainingRepeatsDelta);
        Assert.False(deserialized.Blocked);
        Assert.Equal(CombatExpeditionPhase.Search, deserialized.NextPhase);
        Assert.Equal(1500, deserialized.NextProgressMilliseconds);
        Assert.Equal(result.NextTransitionHint, deserialized.NextTransitionHint);

        // 验证 Delta 增量
        Assert.Equal(3, deserialized.Delta.ItemDeltas[ItemIds.SpiritHerb]);
        Assert.Equal(-1, deserialized.Delta.ItemDeltas[ItemIds.QiGatheringPill]);
        Assert.Equal(100.5, deserialized.Delta.Stats.QiDelta);
        Assert.Equal(2, deserialized.Delta.Stats.HerbsCollectedDelta);
        Assert.Equal(1, deserialized.Delta.Stats.CompletedActionsDelta);
        Assert.Equal(-25.0, deserialized.Delta.CombatResources.HitpointsDelta);
        Assert.Equal(10.0, deserialized.Delta.CombatResources.ManapointsDelta);
        Assert.Equal(0.5, deserialized.Delta.CombatResources.HitpointRemainder);
        Assert.Equal(WorldNodeIds.HerbVale, deserialized.Delta.World?.TargetNodeId);
        Assert.Equal(0.75, deserialized.Delta.World?.EdgeProgress);

        // 验证结构化事件
        var singleEvent = Assert.Single(deserialized.Events);
        Assert.Equal("event-1", singleEvent.IdempotencyKey);
        Assert.Equal("采集完成", singleEvent.Template);
        Assert.Equal(ActionType.Exploration, singleEvent.ActionType);
        Assert.Equal("gather", singleEvent.Category);
    }

    /// <summary>
    /// 验证 ActionDelta 的 AddItemDelta 方法在数量归零时能自动清理键值，保持增量紧凑。
    /// </summary>
    [Fact]
    public void ActionDelta_AddItemDelta_cleans_up_zero_entries()
    {
        var delta = new ActionDelta();
        delta.AddItemDelta(ItemIds.Spirit, 100);
        Assert.Equal(100, delta.ItemDeltas[ItemIds.Spirit]);

        delta.AddItemDelta(ItemIds.Spirit, -40);
        Assert.Equal(60, delta.ItemDeltas[ItemIds.Spirit]);

        delta.AddItemDelta(ItemIds.Spirit, -60);
        Assert.DoesNotContain(ItemIds.Spirit, delta.ItemDeltas.Keys);
    }

    /// <summary>
    /// 验证 ActionPlayerStatDelta 和 ActionCombatResourceDelta 能准确报告 HasChanges 属性。
    /// </summary>
    [Fact]
    public void Deltas_correctly_report_HasChanges()
    {
        var statDelta = new ActionPlayerStatDelta();
        Assert.False(statDelta.HasChanges);

        statDelta.QiDelta = 10;
        Assert.True(statDelta.HasChanges);

        var resourceDelta = new ActionCombatResourceDelta();
        Assert.False(resourceDelta.HasChanges);

        resourceDelta.HitpointsDelta = -5;
        Assert.True(resourceDelta.HasChanges);
    }

    /// <summary>
    /// 验证 Unchanged 与 Block 便捷工厂方法生成正确的初始状态与错误码。
    /// </summary>
    [Fact]
    public void Factory_methods_create_expected_results()
    {
        var unchanged = ActionAdvanceResult.Unchanged(Baseline);
        Assert.Equal(Baseline, unchanged.ConsumedUntil);
        Assert.False(unchanged.Completed);
        Assert.False(unchanged.Blocked);
        Assert.Null(unchanged.BlockReasonCode);

        var blocked = ActionAdvanceResult.Block(
            Baseline.AddSeconds(5),
            ActionBlockReasons.MissingInputs,
            "idemp-block-1");
        Assert.Equal(Baseline.AddSeconds(5), blocked.ConsumedUntil);
        Assert.False(blocked.Completed);
        Assert.True(blocked.Blocked);
        Assert.Equal(ActionBlockReasons.MissingInputs, blocked.BlockReasonCode);
        Assert.Equal("idemp-block-1", blocked.IdempotencyKey);
    }

    /// <summary>
    /// 验证 ActionExecutionContext 纯数据快照初始化。
    /// </summary>
    [Fact]
    public void ActionExecutionContext_initializes_properly()
    {
        var playerId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var definition = new ActionDefinition
        {
            Id = ActionIds.DrawQi,
            Name = "吐纳灵气",
            Description = "打坐吸纳天地灵气",
            Type = ActionType.Cultivation,
            DurationSeconds = 10
        };

        var context = new ActionExecutionContext
        {
            PlayerId = playerId,
            EntryId = entryId,
            ExecutionRevision = 3,
            Action = definition,
            Cursor = Baseline,
            Target = Baseline.AddSeconds(10),
            ProgressMilliseconds = 2000,
            CurrentMapId = WorldMapIds.QingyunMountains,
            CurrentNodeId = WorldNodeIds.HerbVale,
            AvailableInventory = new Dictionary<string, double> { [ItemIds.Spirit] = 50 }.ToFrozenDictionary(StringComparer.Ordinal)
        };

        Assert.Equal(playerId, context.PlayerId);
        Assert.Equal(entryId, context.EntryId);
        Assert.Equal(3, context.ExecutionRevision);
        Assert.Equal(ActionIds.DrawQi, context.Action.Id);
        Assert.Equal(Baseline, context.Cursor);
        Assert.Equal(Baseline.AddSeconds(10), context.Target);
        Assert.Equal(2000, context.ProgressMilliseconds);
        Assert.Equal(WorldMapIds.QingyunMountains, context.CurrentMapId);
        Assert.Equal(WorldNodeIds.HerbVale, context.CurrentNodeId);
        Assert.Equal(50, context.AvailableInventory[ItemIds.Spirit]);
    }

    /// <summary>
    /// 验证 ExecutorAction 纯函数在输入不足时受阻、输入充足且到达时间时生成正确增量。
    /// </summary>
    [Fact]
    public void ExecutorAction_advances_or_blocks_deterministically_based_on_context()
    {
        var playerId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var pillDefinition = new ActionDefinition
        {
            Id = ActionIds.RefineQiPill,
            Name = "炼制聚气丹",
            Description = "炼丹",
            Type = ActionType.Alchemy,
            DurationSeconds = 10,
            Inputs = new Dictionary<string, long> { [ItemIds.SpiritHerb] = 2 },
            Outputs = new Dictionary<string, long> { [ItemIds.QiGatheringPill] = 1 }
        };

        // 1. 输入不足：受阻
        var blockedContext = new ActionExecutionContext
        {
            PlayerId = playerId,
            EntryId = entryId,
            ExecutionRevision = 1,
            Action = pillDefinition,
            Cursor = Baseline,
            Target = Baseline.AddSeconds(10),
            ProgressMilliseconds = 0,
            AvailableInventory = new Dictionary<string, double> { [ItemIds.SpiritHerb] = 1.0 }.ToFrozenDictionary(StringComparer.Ordinal)
        };

        var blockedResult = ExecutorAction.Advance(blockedContext);
        Assert.True(blockedResult.Blocked);
        Assert.Equal(ActionBlockReasons.MissingInputs, blockedResult.BlockReasonCode);
        Assert.False(blockedResult.Completed);

        // 2. 输入充足且到达完成时间：成功完成
        var successContext = new ActionExecutionContext
        {
            PlayerId = playerId,
            EntryId = entryId,
            ExecutionRevision = 1,
            Action = pillDefinition,
            Cursor = Baseline,
            Target = Baseline.AddSeconds(10),
            ProgressMilliseconds = 0,
            AvailableInventory = new Dictionary<string, double> { [ItemIds.SpiritHerb] = 5.0 }.ToFrozenDictionary(StringComparer.Ordinal)
        };

        var successResult = ExecutorAction.Advance(successContext);
        Assert.False(successResult.Blocked);
        Assert.True(successResult.Completed);
        Assert.Equal(Baseline.AddSeconds(10), successResult.ConsumedUntil);
        Assert.Equal(-2.0, successResult.Delta.ItemDeltas[ItemIds.SpiritHerb]);
        Assert.Equal(1.0, successResult.Delta.ItemDeltas[ItemIds.QiGatheringPill]);
        Assert.Equal(1.0, successResult.Delta.Stats.PillsCraftedDelta);
        Assert.Equal(1.0, successResult.Delta.Stats.CompletedActionsDelta);
    }
}
