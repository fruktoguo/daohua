using System.Text.Json;
using DaoHua.Ecs;
using DaoHua.Ecs.Components;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using Xunit;

namespace Ecs.Tests;

/// <summary>队列事件跨入口门禁：保护参数所有权、账本收尾、版本差集和普通/批量轮转一致性。</summary>
public sealed class EcsActionQueueMutationTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PriorityReplacementCopiesParametersAndArchivesAtTheSameTime(bool commandView)
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var original = player.Enqueue(new(new ActionId("cultivation.draw-qi"), -1), From, catalog.Actions);
        var oldTravel = player.EnqueuePriority(new(new ActionId("world.travel"), 1, "node.cloud-gate"),
            From, catalog.Actions, new("node.cloud-gate", 100, [], []));
        ActionReportBook.EnsureActionReport(player, oldTravel.Id, oldTravel.Intent.ActionId, "旧移动", From);
        ActionReportBook.RecordActionStats(player, oldTravel.Id, From.ToUnixTimeSeconds(), new PlayerStatsDelta { Cycles = 2 });
        var seed = player.ExportState();
        seed = seed with
        {
            Actions = seed.Actions.Select(action => action.Id == original.Id
                ? action with { ProgressSeconds = 3 } : action).ToArray()
        };
        var expected = PlayerAggregate.Restore(seed);
        var parameters = new Dictionary<string, double> { ["test"] = 2 };
        var intent = new ActionIntent(new("world.travel"), 7, "node.cloud-gate", parameters);
        var at = From.AddSeconds(5);
        expected.EnqueuePriority(intent, at, catalog.Actions, new("node.cloud-gate", 100, [], []));

        using var runtime = new EcsGameRuntime(catalog, From);
        var id = runtime.CreatePlayer(seed);
        if (commandView)
        {
            var touched = runtime.MutatePlayer(id, aggregate => aggregate.EnqueuePriority(intent, at,
                catalog.Actions, new("node.cloud-gate", 100, [], [])));
            Assert.Equal(new[] { PlayerSectionKind.ActionQueue, PlayerSectionKind.Duel }, touched.OrderBy(section => section));
        }
        else
        {
            var entity = runtime.World.GetEntity(runtime.ExportSnapshot(id).EntityId)!;
            await runtime.Fiber.PostAsync((_, _) =>
            {
                new EcsPlayerActionContext(entity).EnqueuePriority(intent, at,
                    catalog.Actions, new("node.cloud-gate", 100, [], []));
                return ValueTask.CompletedTask;
            });
            // PostAsync 只确认消息入邮箱，真实操作由下一次 Tick 在 Fiber 所有权内消费。
            await runtime.Fiber.TickAsync();
        }
        parameters["test"] = 99;
        var actual = runtime.ExportSnapshot(id).State;
        Assert.Equal(1, actual.Actions[0].Intent.RepeatCount);
        Assert.Equal(2, actual.Actions[0].Intent.Parameters!["test"]);
        Assert.Equal(ActionQueueStatus.Running, actual.Actions[0].Status);
        Assert.Equal(original.Id, actual.Actions[1].Id);
        Assert.Equal(3, actual.Actions[1].ProgressSeconds);
        Assert.Equal(ActionQueueStatus.Pending, actual.Actions[1].Status);
        Assert.Equal(at.ToUnixTimeSeconds(), Assert.Single(actual.Duel.Reports).EndedAtUnixSeconds);
        Assert.Equal(oldTravel.Id.ToString("N"), actual.Duel.Reports[0].ReportId);
        // 新实例 ID 由各入口生成；其余完整状态应逐位一致。
        actual = actual with { Actions = actual.Actions.Select((action, index) => index == 0
            ? action with { Id = expected.Actions[0].Id } : action).ToArray() };
        Assert.Equal(JsonSerializer.Serialize(expected.ExportState()), JsonSerializer.Serialize(actual));
        AssertWorkReleased(runtime, id);
    }

    [Fact]
    public void CommandQueueMutationPreservesEarlierScalarChangesAndDirtyMarks()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var seed = CreatePlayer(catalog).ExportState();
        var expected = PlayerAggregate.Restore(seed);
        expected.SetLoopEnabled(false);
        expected.Enqueue(new(new ActionId("cultivation.draw-qi"), -1), From, catalog.Actions);
        using var runtime = new EcsGameRuntime(catalog, From);
        var id = runtime.CreatePlayer(seed);
        var touched = runtime.MutatePlayer(id, aggregate =>
        {
            aggregate.SetLoopEnabled(false);
            aggregate.Enqueue(new(new ActionId("cultivation.draw-qi"), -1), From, catalog.Actions);
        });
        Assert.Contains(PlayerSectionKind.Settings, touched);
        Assert.Contains(PlayerSectionKind.ActionQueue, touched);
        var actual = runtime.ExportSnapshot(id).State;
        Assert.Equal(seed.StateVersion + 2, actual.StateVersion);
        Assert.Equal(ActionQueueStatus.Pending, Assert.Single(actual.Actions).Status);
        actual = actual with { Actions = [actual.Actions[0] with { Id = expected.Actions[0].Id }] };
        Assert.Equal(JsonSerializer.Serialize(expected.ExportState()), JsonSerializer.Serialize(actual));

        var before = runtime.GetDirtyMarks(id);
        runtime.MutatePlayer(id, aggregate => aggregate.Enqueue(new(new ActionId("cultivation.draw-qi")), From, catalog.Actions));
        Assert.Equal(before[PlayerSectionKind.ActionQueue] + 1, runtime.GetDirtyMarks(id)[PlayerSectionKind.ActionQueue]);
        AssertWorkReleased(runtime, id);
    }

    [Fact]
    public void DirectEnqueueUsesTheCommandCapacityAndRepeatRules()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        for (var i = 0; i < PlayerAggregate.MaxActionQueueLength; i++)
            player.Enqueue(new(new ActionId("cultivation.draw-qi")), From, catalog.Actions);
        using var runtime = new EcsGameRuntime(catalog, From);
        var id = runtime.CreatePlayer(player.ExportState());
        var before = JsonSerializer.Serialize(runtime.ExportSnapshot(id).State);
        Assert.Throws<DomainException>(() => runtime.EnqueueAction(id, new("cultivation.draw-qi"), 1.5));
        Assert.Throws<DomainException>(() => runtime.EnqueueAction(id, new("cultivation.draw-qi"), 1_000_001));
        Assert.Throws<DomainException>(() => runtime.EnqueueAction(id, new("cultivation.draw-qi")));
        Assert.Equal(before, JsonSerializer.Serialize(runtime.ExportSnapshot(id).State));
        AssertWorkReleased(runtime, id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundCompletionKeepsNormalAndBulkQueuesReportsAndEventsEqual(bool loopEnabled)
    {
        var actions = new ActionCatalog([
            new(new("test.gather"), "采集", ActionKind.Exploration, 1, new Dictionary<ItemId, double>(),
                [new(ActionYieldKind.Experience, ActionYieldPeriod.PerRepeat, 3)]),
            new(new("test.craft"), "制造", ActionKind.Alchemy, 1, new Dictionary<ItemId, double>(),
                [new(ActionYieldKind.Experience, ActionYieldPeriod.PerRepeat, 2)])
        ]);
        var catalog = new GameDataCatalog("test", "test", actions);
        var player = CreatePlayer(catalog);
        player.SetLoopEnabled(loopEnabled);
        player.Enqueue(new(new("test.gather")), From, actions);
        player.Enqueue(new(new("test.craft")), From, actions);
        var seed = player.ExportState();
        var expected = PlayerAggregate.Restore(seed);
        var target = From.AddSeconds(7);
        var result = SimulationEngine.AdvanceTo(expected, target, catalog);

        PlayerAggregateState? normal = null;
        IReadOnlyDictionary<PlayerSectionKind, long>? normalMarks = null;
        foreach (var bulk in new[] { false, true })
        {
            using var runtime = new EcsGameRuntime(catalog, From);
            var id = runtime.CreatePlayer(seed);
            if (bulk) await runtime.RunPlayerOnceAsync(id, target);
            else await runtime.RunOnceAsync(target);
            var actual = runtime.ExportSnapshot(id).State;
            Assert.Equal(JsonSerializer.Serialize(expected.ExportState()), JsonSerializer.Serialize(actual));
            Assert.Equal(EventJson(result.Events), EventJson(runtime.DrainSimulationEvents(id)));
            if (normal is null)
            {
                normal = actual;
                normalMarks = runtime.GetDirtyMarks(id);
            }
            else
            {
                Assert.Equal(JsonSerializer.Serialize(normal), JsonSerializer.Serialize(actual));
                Assert.Equal(normalMarks!.OrderBy(pair => pair.Key), runtime.GetDirtyMarks(id).OrderBy(pair => pair.Key));
            }
            if (loopEnabled)
            {
                Assert.Equal(seed.Actions.Select(action => action.Id).Order(), actual.Actions.Select(action => action.Id).Order());
                Assert.Empty(actual.Duel.Reports);
            }
            else
            {
                Assert.Empty(actual.Actions);
                Assert.Equal(new[] { From.AddSeconds(2).ToUnixTimeSeconds(), From.AddSeconds(1).ToUnixTimeSeconds() },
                    actual.Duel.Reports.Select(report => (long)report.EndedAtUnixSeconds));
            }
            AssertWorkReleased(runtime, id);
        }
    }

    [Fact]
    public void InfiniteRoundCompletionDoesNotArchiveOrAdvanceVersion()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        player.SetLoopEnabled(false);
        var action = player.Enqueue(new(new ActionId("cultivation.draw-qi"), -1), From, catalog.Actions);
        ActionReportBook.EnsureActionReport(player, action.Id, action.Intent.ActionId, "引气", From);
        ActionReportBook.RecordActionStats(player, action.Id, From.ToUnixTimeSeconds(), new PlayerStatsDelta { Cycles = 1 });
        var seed = player.ExportState();
        using var runtime = new EcsGameRuntime(catalog, From);
        var id = runtime.CreatePlayer(seed);
        var beforeMarks = runtime.GetDirtyMarks(id);
        var touched = runtime.MutatePlayer(id, aggregate => Assert.Equal(PlayerActionRoundDisposition.Continue,
            aggregate.FinishActionRound(action.Id, true, From.AddSeconds(10).ToUnixTimeSeconds())));
        Assert.Empty(touched);
        Assert.Equal(beforeMarks.OrderBy(pair => pair.Key), runtime.GetDirtyMarks(id).OrderBy(pair => pair.Key));
        Assert.Equal(JsonSerializer.Serialize(seed), JsonSerializer.Serialize(runtime.ExportSnapshot(id).State));
        AssertWorkReleased(runtime, id);
    }

    private static PlayerAggregate CreatePlayer(GameDataCatalog catalog)
        => PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "队列回归", From, catalog.Fingerprint);

    private static void AssertWorkReleased(EcsGameRuntime runtime, Guid id)
    {
        var work = runtime.World.GetEntity(runtime.ExportSnapshot(id).EntityId)!
            .GetComponent<PlayerActionQueueMutationComponent>()!.Work;
        Assert.Null(work.Player);
        Assert.Null(work.Action);
        Assert.Empty(work.ArchiveIds);
    }

    private static string EventJson(IEnumerable<SimulationEvent> events)
        => JsonSerializer.Serialize(events.Select(entry => new
        {
            entry.At, entry.Code, entry.Message, entry.ActionId,
            Items = entry.ItemDeltas?.Select(pair => new { Item = pair.Key.Value, Quantity = pair.Value }).ToArray()
        }));
}
