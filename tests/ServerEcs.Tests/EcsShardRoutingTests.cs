using System.Text.Json;
using DaoHua.Ecs;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Server.Clean;
using Xunit;

namespace ServerEcs.Tests;

/// <summary>固定分片路由门禁：分片只改变 ECS 所有权，不得改变三类行动结果。</summary>
public sealed class EcsShardRoutingTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FourShardsKeepGatherCraftCombatSnapshotsIdentical()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var cases = CreateCases(catalog);
        Dictionary<Guid, EcsPlayerSnapshot> expected;
        using (var single = new EcsGameRuntime(catalog, SeedTime))
        {
            foreach (var item in cases) single.CreatePlayer(item.State, item.Balances);
            foreach (var item in cases)
                await single.RunPlayerOnceAsync(item.State.PlayerId.Value, SeedTime.AddSeconds(37));
            expected = cases.ToDictionary(item => item.State.PlayerId.Value,
                item => single.ExportSnapshot(item.State.PlayerId.Value));
        }

        using var sharded = new EcsGameRuntime(catalog, SeedTime, shardCount: 4);
        foreach (var item in cases) sharded.CreatePlayer(item.State, item.Balances);
        foreach (var item in cases)
            await sharded.RunPlayerOnceAsync(item.State.PlayerId.Value, SeedTime.AddSeconds(37));

        Assert.Equal(4, sharded.ShardCount);
        Assert.Equal(cases.Length, sharded.PlayerCount);
        Assert.True(cases.Select(item => sharded.GetWorld(item.State.PlayerId.Value)).Distinct().Count() > 1,
            "测试角色必须实际分布到多个 World。");
        foreach (var item in cases)
        {
            var actual = sharded.ExportSnapshot(item.State.PlayerId.Value);
            Assert.Equal(JsonSerializer.Serialize(expected[item.State.PlayerId.Value].State),
                JsonSerializer.Serialize(actual.State));
            Assert.Equal(expected[item.State.PlayerId.Value].Assets, actual.Assets);
        }
    }

    [Fact]
    public async Task CancelledShardAdvanceReleasesOwnerAndDisposeIsIdempotent()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var item = CreateCases(catalog)[0];
        using var runtime = new EcsGameRuntime(catalog, SeedTime, shardCount: 4);
        runtime.CreatePlayer(item.State, item.Balances);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.RunPlayerOnceAsync(item.State.PlayerId.Value, SeedTime.AddSeconds(37), cancelled.Token).AsTask());

        await runtime.RunPlayerOnceAsync(item.State.PlayerId.Value, SeedTime.AddSeconds(37)).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Dispose();
        runtime.Dispose();
    }

    [Fact]
    public async Task SystemMeasurementsExcludeInactivePlayersInSameWorld()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var cases = CreateCases(catalog);
        using var telemetry = new BenchmarkTelemetry();

        using (var single = new EcsGameRuntime(catalog, SeedTime))
        {
            single.CreatePlayer(cases[0].State, cases[0].Balances);
            await single.RunPlayerOnceAsync(cases[0].State.PlayerId.Value, SeedTime.AddSeconds(37));
        }
        var first = telemetry.Snapshot().Measurements;
        var stage = "ecs.system.run.ActionClockSystem|gathering";
        var singleCount = first[stage].Count;
        Assert.True(singleCount > 0);

        using (var shared = new EcsGameRuntime(catalog, SeedTime))
        {
            shared.CreatePlayer(cases[0].State, cases[0].Balances);
            shared.CreatePlayer(cases[1].State, cases[1].Balances);
            await shared.RunPlayerOnceAsync(cases[0].State.PlayerId.Value, SeedTime.AddSeconds(37));
        }
        var measurements = telemetry.Snapshot().Measurements;
        Assert.Equal(singleCount, measurements[stage].Count - singleCount);
        Assert.True(measurements["ecs.system_tick|gathering"].Count > 0);
        Assert.True(measurements["ecs.mailbox.post|gathering"].Count > 0);
        Assert.True(measurements["ecs.mailbox.callback|gathering"].Count > 0);
        Assert.True(measurements["ecs.owner_hold|all"].Count > 0);
    }

    private static Case[] CreateCases(GameDataCatalog catalog)
    {
        var definitions = new[]
        {
            ("10000000-0000-4000-8000-000000000111", "explore.gather-ningxue-grass", Array.Empty<AssetBalance>()),
            ("10000000-0000-4000-8000-000000000112", "alchemy.refine-qi-pill", new[]
            {
                new AssetBalance(new PlayerId(Guid.Parse("10000000-0000-4000-8000-000000000112")),
                    new AssetKey(new ItemId("item.ningxue-grass")), 9, 0)
            }),
            ("10000000-0000-4000-8000-000000000113", "combat.hunt-qi-beast", Array.Empty<AssetBalance>())
        };
        return definitions.Select((definition, index) =>
        {
            var playerId = Guid.Parse(definition.Item1);
            var player = PlayerAggregate.Create(new PlayerId(playerId),
                new AccountId(Guid.Parse($"20000000-0000-4000-8000-{111 + index:D12}")),
                $"分片{index}", SeedTime, catalog.Fingerprint);
            player.Enqueue(new ActionIntent(new ActionId(definition.Item2), -1), SeedTime, catalog.Actions);
            return new Case(player.ExportState(), definition.Item3);
        }).ToArray();
    }

    private sealed record Case(PlayerAggregateState State, IReadOnlyCollection<AssetBalance> Balances);
}
