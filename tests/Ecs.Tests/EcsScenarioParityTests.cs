using System.Text.Json;
using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Economy;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using Xunit;

namespace Ecs.Tests;

/// <summary>跨自然业务边界的 ECS 门禁：从队列进入搜索，再进入真实战斗单位系统。</summary>
public sealed class EcsScenarioParityTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(300)]
    public async Task QueuedCombatCrossesSearchAndCombatBoundariesWithOopState(double durationSeconds)
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.Parse("10000000-0000-4000-8000-000000000021");
        var player = PlayerAggregate.Create(new PlayerId(playerId),
            new AccountId(Guid.Parse("20000000-0000-4000-8000-000000000021")),
            "Ecs长战斗", SeedTime, catalog.Fingerprint);
        player.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1), SeedTime, catalog.Actions);
        var seed = player.ExportState();
        var expected = PlayerAggregate.Restore(seed);
        var wallet = new AssetWallet(expected.PlayerId);
        var expectedResult = SimulationEngine.AdvanceTo(expected, SeedTime.AddSeconds(durationSeconds), catalog, wallet);
        foreach (var delta in expectedResult.AssetDeltas) wallet.Apply(delta);

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        runtime.CreatePlayer(seed);
        await runtime.RunPlayerOnceAsync(playerId, SeedTime.AddSeconds(durationSeconds));
        var actual = runtime.ExportSnapshot(playerId).State;

        Assert.Equal(expected.Actions.Count, actual.Actions.Count);
        Assert.Equal(expected.Actions[0].Intent, actual.Actions[0].Intent);
        Assert.True(expected.Actions[0].Expedition?.Phase == actual.Actions[0].Expedition?.Phase,
            $"expected={expected.Actions[0].Expedition?.Phase}, actual={actual.Actions[0].Expedition?.Phase}, last={actual.LastSimulatedAt:O}, encounters={runtime.World.Entities.Values.Count(entity => entity.GetComponent<DaoHua.Ecs.Components.CombatEncounterComponent>() is not null)}, action={JsonSerializer.Serialize(actual.Actions[0])}");
        Assert.True(actual.Actions[0].Expedition?.Phase is CombatExpeditionPhase.Combat or CombatExpeditionPhase.InitialSearch or CombatExpeditionPhase.Search,
            $"战斗行动未产生有效远征阶段：{actual.Actions[0].Expedition?.Phase}");
        Assert.Equal(JsonSerializer.Serialize(expected.ExportState()), JsonSerializer.Serialize(actual));
        Assert.Equal(EventJson(expectedResult.Events), EventJson(runtime.DrainSimulationEvents(playerId)));
        Assert.Equal(wallet.Balances.OrderBy(balance => balance.Asset.ToString()).Select(balance => (balance.Asset, balance.Quantity)),
            runtime.ExportSnapshot(playerId).Assets.OrderBy(pair => pair.Key.ToString()).Select(pair => (pair.Key, pair.Value)));
        if (durationSeconds >= 120) Assert.True(actual.Progression.CompletedActions >= 2);
        if (durationSeconds >= 300) Assert.True(actual.Progression.TotalDefeats >= 1);

        var encounters = runtime.World.Entities.Values
            .Select(entity => entity.GetComponent<DaoHua.Ecs.Components.CombatEncounterComponent>())
            .Where(component => component is not null)
            .ToArray();
        Assert.NotEmpty(encounters);
        Assert.All(encounters, encounter => Assert.NotEmpty(encounter!.EnemyEntityIds));
    }

    [Fact]
    public async Task TravelCompletionResumesQueuedActionWithIdenticalResults()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(new(Guid.Parse("10000000-0000-4000-8000-000000000031")),
            new(Guid.Parse("20000000-0000-4000-8000-000000000031")), "移动回归", SeedTime, catalog.Fingerprint);
        player.Enqueue(new(new ActionId("cultivation.draw-qi"), -1), SeedTime, catalog.Actions);
        var path = catalog.WorldGraph.PlanFromPosition(null, 0, player.World.NodeId, "node.cloud-gate");
        player.EnqueuePriority(new(new ActionId("world.travel"), 1, TargetNodeId: "node.cloud-gate"), SeedTime,
            catalog.Actions, new TravelActionState("node.cloud-gate", path.TotalTravelSeconds, path.EdgeIds, path.Segments));
        var seed = player.ExportState();
        var target = SeedTime.AddSeconds(path.TotalTravelSeconds + 13);
        var expected = SimulationEngine.AdvanceTo(player, target, catalog);
        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        runtime.CreatePlayer(seed);
        await runtime.RunPlayerOnceAsync(player.PlayerId.Value, target);
        var actual = runtime.ExportSnapshot(player.PlayerId.Value).State;
        Assert.Equal(JsonSerializer.Serialize(player.ExportState()), JsonSerializer.Serialize(actual));
        Assert.Equal(EventJson(expected.Events), EventJson(runtime.DrainSimulationEvents(player.PlayerId.Value)));
        Assert.Equal("node.cloud-gate", actual.World.NodeId);
        Assert.Equal("cultivation.draw-qi", Assert.Single(actual.Actions).Intent.ActionId.Value);
    }

    [Fact]
    public async Task MixedPlayerBoundariesAndCombatReloadKeepExactWindowResults()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var players = new[] { "explore.gather-ningxue-grass", "combat.hunt-qi-beast" }.Select((actionId, index) =>
        {
            var player = PlayerAggregate.Create(new(Guid.Parse($"10000000-0000-4000-8000-{40 + index:D12}")),
                new(Guid.Parse($"20000000-0000-4000-8000-{40 + index:D12}")), $"混合边界{index}", SeedTime, catalog.Fingerprint);
            player.Enqueue(new(new ActionId(actionId), index == 0 ? 6 : -1), SeedTime, catalog.Actions);
            return player;
        }).ToArray();
        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        foreach (var player in players) runtime.CreatePlayer(player.ExportState());
        var wallets = players.ToDictionary(player => player.PlayerId, player => new AssetWallet(player.PlayerId));
        // 不同请求边界检验剩余秒数；第二段前真实卸载并从快照重建战斗单位。
        foreach (var elapsed in new[] { 37.25, 120d })
        {
            var target = SeedTime.AddSeconds(elapsed);
            var expected = players.ToDictionary(player => player.PlayerId,
                player => SimulationEngine.AdvanceTo(player, target, catalog, wallets[player.PlayerId]));
            await runtime.RunOnceAsync(target);
            foreach (var player in players)
            {
                foreach (var delta in expected[player.PlayerId].AssetDeltas) wallets[player.PlayerId].Apply(delta);
                var actual = runtime.ExportSnapshot(player.PlayerId.Value);
                Assert.Equal(JsonSerializer.Serialize(player.ExportState()), JsonSerializer.Serialize(actual.State));
                Assert.Equal(EventJson(expected[player.PlayerId].Events),
                    EventJson(runtime.DrainSimulationEvents(player.PlayerId.Value)));
                Assert.Equal(wallets[player.PlayerId].Balances.OrderBy(balance => balance.Asset.ToString()).Select(balance => (balance.Asset, balance.Quantity)),
                    actual.Assets.OrderBy(pair => pair.Key.ToString()).Select(pair => (pair.Key, pair.Value)));
                Assert.True(runtime.RemovePlayer(player.PlayerId.Value));
                runtime.CreatePlayer(actual.State, actual.Assets.Select(pair => new AssetBalance(player.PlayerId, pair.Key, pair.Value, 0)).ToArray());
            }
        }
    }

    [Fact]
    public async Task BatchAdvanceKeepsPlayersIsolatedAndUnloadRemovesCombatEntities()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var players = Enumerable.Range(0, 10)
            .Select(index =>
            {
                var id = Guid.Parse($"10000000-0000-4000-8000-{(300 + index):D12}");
                var aggregate = PlayerAggregate.Create(new PlayerId(id),
                    new AccountId(Guid.Parse($"20000000-0000-4000-8000-{(300 + index):D12}")),
                    $"批量{index}", SeedTime, catalog.Fingerprint);
                aggregate.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1), SeedTime, catalog.Actions);
                return aggregate.ExportState();
            }).ToArray();

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        foreach (var state in players) runtime.CreatePlayer(state);
        await runtime.RunOnceAsync(SeedTime.AddSeconds(30));
        Assert.Equal(players.Length, runtime.PlayerCount);
        Assert.All(players, state =>
        {
            var actual = runtime.ExportSnapshot(state.PlayerId.Value).State;
            Assert.Equal(state.PlayerId, actual.PlayerId);
            Assert.Equal(-1, actual.Actions.Single().RemainingRepeats);
        });

        var before = runtime.World.Entities.Count;
        Assert.True(runtime.RemovePlayer(players[0].PlayerId.Value));
        Assert.Equal(players.Length - 1, runtime.PlayerCount);
        Assert.True(runtime.World.Entities.Count < before);
        Assert.False(runtime.RemovePlayer(players[0].PlayerId.Value));
    }
    private static string EventJson(IEnumerable<SimulationEvent> events)
        => JsonSerializer.Serialize(events.Select(entry => new
        {
            entry.At, entry.Code, entry.Message, entry.ActionId,
            Items = entry.ItemDeltas?.Select(pair => new { Item = pair.Key.Value, Quantity = pair.Value }).ToArray()
        }));

}
