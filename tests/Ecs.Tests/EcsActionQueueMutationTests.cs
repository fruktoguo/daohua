using System.Text.Json;
using DaoHua.Ecs;
using DaoHua.Ecs.Components;
using Domain.Combat;
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
    public async Task FullPersonalQueueAllowsParticipationAndRestoresEveryPersonalAction(bool commandView)
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        for (var i = 0; i < PlayerAggregate.MaxActionQueueLength; i++)
            player.Enqueue(new(new ActionId("cultivation.draw-qi"), -1), From, catalog.Actions);
        var originalActions = player.ExportState().Actions;
        var spec = CreateParticipationSpec(player, catalog);
        var expected = PlayerAggregate.Restore(player.ExportState());
        Assert.NotNull(expected.StartActionParticipation(spec, From, catalog.Actions));

        using var runtime = new EcsGameRuntime(catalog, From);
        var id = runtime.CreatePlayer(player.ExportState());
        if (commandView)
            runtime.MutatePlayer(id, aggregate => Assert.NotNull(aggregate.StartActionParticipation(spec, From, catalog.Actions)));
        else
        {
            var entity = runtime.World.GetEntity(runtime.ExportSnapshot(id).EntityId)!;
            await runtime.Fiber.PostAsync((_, _) =>
            {
                new EcsPlayerActionContext(entity).EnqueuePriority(new(spec.ActionId, -1, spec.NodeId), From,
                    catalog.Actions, participation: new ActionParticipationBinding(spec, double.PositiveInfinity));
                return ValueTask.CompletedTask;
            });
            await runtime.Fiber.TickAsync();
        }
        var participating = runtime.ExportSnapshot(id).State;
        Assert.Equal(PlayerAggregate.MaxActionQueueLength + 1, participating.Actions.Count);
        Assert.Equal(ActionQueueStatus.Running, participating.Actions[0].Status);
        Assert.Single(participating.Actions, action => action.Participation is not null);
        Assert.Equal(JsonSerializer.Serialize(originalActions), JsonSerializer.Serialize(participating.Actions.Skip(1)));
        Assert.Equal(expected.Actions.Select(action => action.Intent), participating.Actions.Select(action => action.Intent));
        var before = JsonSerializer.Serialize(participating);
        Assert.Equal(DomainErrorCode.Conflict,
            Assert.Throws<DomainException>(() => runtime.EnqueueAction(id, new("cultivation.draw-qi"))).Code);
        Assert.Equal(DomainErrorCode.Conflict, Assert.Throws<DomainException>(() => runtime.MutatePlayer(id,
            aggregate => aggregate.Enqueue(new(new ActionId("cultivation.draw-qi")), From, catalog.Actions))).Code);
        Assert.Equal(before, JsonSerializer.Serialize(runtime.ExportSnapshot(id).State));
        AssertWorkReleased(runtime, id);

        // 持久化只留引用，257 项文档恢复后仍完整保留所有个人行动。
        var json = JsonSerializer.Serialize(participating, Domain.Persistence.PersistenceJson.Options);
        var document = JsonSerializer.Deserialize<PlayerAggregateState>(json, Domain.Persistence.PersistenceJson.Options)!;
        var restored = PlayerAggregate.Restore(document);
        Assert.Equal(json, JsonSerializer.Serialize(restored.ExportState(), Domain.Persistence.PersistenceJson.Options));
        Assert.Null(restored.Actions[0].Participation!.Spec);
        runtime.RestorePlayer(id, restored.ExportState(), []);
        runtime.MutatePlayer(id, aggregate => Assert.True(aggregate.EndActionParticipation(spec.SessionId, From.ToUnixTimeSeconds())));
        await runtime.RunOnceAsync(From.AddSeconds(1));

        var ended = runtime.ExportSnapshot(id).State;
        Assert.Equal(originalActions.Select(action => action.Id), ended.Actions.Select(action => action.Id));
        Assert.All(ended.Actions, action => Assert.Null(action.Participation));
        Assert.Equal(ActionQueueStatus.Running, ended.Actions[0].Status);
        Assert.All(ended.Actions.Skip(1), action => Assert.Equal(ActionQueueStatus.Pending, action.Status));
        var afterEnding = JsonSerializer.Serialize(ended);
        Assert.Equal(DomainErrorCode.Conflict, Assert.Throws<DomainException>(() => runtime.MutatePlayer(id,
            aggregate => aggregate.EnqueuePriority(new(new ActionId("world.travel"), 1, "node.cloud-gate"),
                From.AddSeconds(1), catalog.Actions, new("node.cloud-gate", 100, [], [])))).Code);
        Assert.Equal(afterEnding, JsonSerializer.Serialize(runtime.ExportSnapshot(id).State));
        Assert.True(runtime.RemoveAction(id, originalActions[0].Id));
        runtime.EnqueueAction(id, new("cultivation.draw-qi"));
        Assert.Equal(PlayerAggregate.MaxActionQueueLength, runtime.ExportSnapshot(id).State.Actions.Count);
        runtime.MutatePlayer(id, aggregate => Assert.True(aggregate.ClearActions()));
        Assert.Empty(runtime.ExportSnapshot(id).State.Actions);
        AssertWorkReleased(runtime, id);
    }

    [Fact]
    public void ParticipationLeavesAllPersonalCapacityAvailable()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        for (var i = 0; i < PlayerAggregate.MaxActionQueueLength - 1; i++)
            player.Enqueue(new(new ActionId("cultivation.draw-qi")), From, catalog.Actions);
        var spec = CreateParticipationSpec(player, catalog);
        Assert.NotNull(player.StartActionParticipation(spec, From, catalog.Actions));
        using var runtime = new EcsGameRuntime(catalog, From);
        var id = runtime.CreatePlayer(player.ExportState());
        runtime.EnqueueAction(id, new("cultivation.draw-qi"));
        Assert.Equal(PlayerAggregate.MaxActionQueueLength + 1, runtime.ExportSnapshot(id).State.Actions.Count);
        var before = JsonSerializer.Serialize(runtime.ExportSnapshot(id).State);
        Assert.Equal(DomainErrorCode.Conflict,
            Assert.Throws<DomainException>(() => runtime.EnqueueAction(id, new("cultivation.draw-qi"))).Code);
        Assert.Equal(before, JsonSerializer.Serialize(runtime.ExportSnapshot(id).State));
        AssertWorkReleased(runtime, id);
    }

    [Fact]
    public void PriorityTravelAtPersonalCapacityDoesNotRemoveUnrelatedActions()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        for (var i = 0; i < PlayerAggregate.MaxActionQueueLength; i++)
            player.Enqueue(new(new ActionId("cultivation.draw-qi")), From, catalog.Actions);
        using var runtime = new EcsGameRuntime(catalog, From);
        var id = runtime.CreatePlayer(player.ExportState());
        var before = JsonSerializer.Serialize(runtime.ExportSnapshot(id).State);
        Assert.Equal(DomainErrorCode.Conflict, Assert.Throws<DomainException>(() => runtime.MutatePlayer(id,
            aggregate => aggregate.EnqueuePriority(new(new ActionId("world.travel"), 1, "node.cloud-gate"),
                From, catalog.Actions, new("node.cloud-gate", 100, [], [])))).Code);
        Assert.Equal(before, JsonSerializer.Serialize(runtime.ExportSnapshot(id).State));
        AssertWorkReleased(runtime, id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void RestoreRejectsInvalidParticipationCapacityOrQueueStatus(int invalidCase)
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        for (var i = 0; i < PlayerAggregate.MaxActionQueueLength; i++)
            player.Enqueue(new(new ActionId("cultivation.draw-qi")), From, catalog.Actions);
        var spec = CreateParticipationSpec(player, catalog);
        Assert.NotNull(player.StartActionParticipation(spec, From, catalog.Actions));
        var state = player.ExportState();
        var actions = state.Actions.ToArray();
        switch (invalidCase)
        {
            case 0: // 257 个个人行动，不能冒充参与额外槽位。
                actions[0] = actions[0] with { Participation = null };
                break;
            case 1: // 即使总数未越界，也只允许一个参与引用。
                actions = actions.Take(2).ToArray();
                actions[1] = actions[1] with { Participation = actions[0].Participation };
                break;
            case 2:
                actions[0] = actions[0] with { Participation = null, Status = ActionQueueStatus.Pending };
                actions[1] = actions[1] with { Participation = state.Actions[0].Participation, Status = ActionQueueStatus.Running };
                break;
            case 3:
                actions[0] = actions[0] with { Participation = actions[0].Participation! with { EndsAtUnixSeconds = -1 } };
                break;
            case 4:
                actions[0] = actions[0] with { Status = ActionQueueStatus.Pending };
                break;
            case 5:
                actions[0] = actions[0] with { Status = ActionQueueStatus.Blocked };
                break;
            case 6:
                actions[1] = actions[1] with { Status = ActionQueueStatus.Running };
                break;
            case 7:
                actions = [.. actions, actions[1] with { Id = Guid.NewGuid() }];
                break;
        }
        Assert.Equal(DomainErrorCode.InvalidArgument,
            Assert.Throws<DomainException>(() => PlayerAggregate.Restore(state with { Actions = actions })).Code);
    }

    [Fact]
    public void PriorityEnqueueCannotDuplicateOrDisplaceParticipationReference()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var spec = CreateParticipationSpec(player, catalog);
        Assert.NotNull(player.StartActionParticipation(spec, From, catalog.Actions));
        var before = JsonSerializer.Serialize(player.ExportState());
        Assert.Equal(DomainErrorCode.Conflict, Assert.Throws<DomainException>(() => player.EnqueuePriority(
            new(spec.ActionId, -1, spec.NodeId), From, catalog.Actions,
            participation: new ActionParticipationBinding(spec, double.PositiveInfinity))).Code);
        Assert.Equal(DomainErrorCode.Conflict, Assert.Throws<DomainException>(() => player.EnqueuePriority(
            new(spec.ActionId), From, catalog.Actions)).Code);
        Assert.Equal(before, JsonSerializer.Serialize(player.ExportState()));
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

    /// <summary>
    /// 行动参与绑定经 ECS 快照导出必须原样保留：ExportSnapshotCore 重建 QueuedActionState
    /// 曾漏传 Participation，导致协议层拿不到参与规格（盟友阵列恒空、装载对账误判绑定缺失）。
    /// 以 OOP ExportState 序列化逐位一致收口——同字段两处导出永远对齐。
    /// </summary>
    [Fact]
    public void ActionParticipationBindingSurvivesEcsSnapshotExport()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var memberA = CreatePlayer(catalog);
        var memberB = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "队友乙", From, catalog.Fingerprint);
        PlayerAttributeResolver.Apply(memberA, catalog);
        PlayerAttributeResolver.Apply(memberB, catalog);
        var stats = CombatantStats.Create(50_000, 2_000, 500, 1.0);
        ActionParticipantSnapshot MakeSnapshot(PlayerAggregate member) => new(
            member.PlayerId.Value.ToString("D"), member.DisplayName, member.Progression.RealmLevel,
            0, 0, stats.MaxHitpoints, stats.MaxManapoints, stats, [], null, PlayerCombatSkillState.Empty);
        var spec = new ActionParticipationSpec("session.export", "encounter.qi-beast", "node.test", 97,
            0, From.ToUnixTimeMilliseconds() / 1000d, [MakeSnapshot(memberA), MakeSnapshot(memberB)])
        {
            ActionId = new ActionId("combat.hunt-qi-beast")
        };

        var expected = PlayerAggregate.Restore(memberA.ExportState());
        Assert.NotNull(expected.StartActionParticipation(spec, From, catalog.Actions));

        using var runtime = new EcsGameRuntime(catalog, From);
        var id = runtime.CreatePlayer(memberA.ExportState());
        runtime.MutatePlayer(id, aggregate => aggregate.StartActionParticipation(spec, From, catalog.Actions));
        var actual = runtime.ExportSnapshot(id).State;

        var binding = actual.Actions[0].Participation;
        Assert.NotNull(binding);
        Assert.Equal("session.export", binding.SessionId);
        Assert.True(double.IsPositiveInfinity(binding.EndsAtUnixSeconds));
        Assert.Equal(2, binding.Spec!.Members.Count);
        Assert.Equal(expected.Actions[0].Participation!.Spec!.Members.Select(member => member.PlayerId),
            binding.Spec.Members.Select(member => member.PlayerId));
        var json = JsonSerializer.Serialize(binding, Domain.Persistence.PersistenceJson.Options);
        Assert.DoesNotContain("members", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stats", json, StringComparison.OrdinalIgnoreCase);
        var restoredBinding = JsonSerializer.Deserialize<ActionParticipationBinding>(json,
            Domain.Persistence.PersistenceJson.Options)!;
        Assert.Equal(binding.SessionId, restoredBinding.SessionId);
        Assert.Null(restoredBinding.Spec);
        Assert.True(restoredBinding.IsValid);
        var expandedEnvelope = JsonSerializer.Serialize(new
        {
            spec,
            endsAtUnixSeconds = From.AddMinutes(1).ToUnixTimeSeconds(),
            appliedSequence = 7
        }, Domain.Persistence.PersistenceJson.Options);
        var referenceOnly = JsonSerializer.Deserialize<ActionParticipationBinding>(expandedEnvelope,
            Domain.Persistence.PersistenceJson.Options)!;
        Assert.Equal(spec.SessionId, referenceOnly.SessionId);
        Assert.Equal(7, referenceOnly.AppliedSequence);
        Assert.Null(referenceOnly.Spec);
        AssertWorkReleased(runtime, id);
    }

    private static ActionParticipationSpec CreateParticipationSpec(PlayerAggregate player, GameDataCatalog catalog)
        => new("session.capacity", "encounter.qi-beast", "node.test", 97, 0, From.ToUnixTimeSeconds(),
            [player.CaptureActionParticipantSnapshot(catalog)])
        {
            ActionId = new ActionId("combat.hunt-qi-beast")
        };

    private static PlayerAggregate CreatePlayer(GameDataCatalog catalog)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "队列回归", From, catalog.Fingerprint);
        // 种子按「已断线存档」播种：装载规范化对持久化 Online 一律转离线，
        // 离线种子让 expected=Restore(种子) 镜像与实体逐位一致。
        player.SetPresence(PresenceState.Offline, From);
        return player;
    }

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
