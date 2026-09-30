using DaoHua.Ecs;
using DaoHua.Ecs.Components;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Xunit;

namespace Ecs.Tests;

/// <summary>地图节点实体化门禁：MapNodeEntity 长期存在、玩家驻留人口随位置迁移、遭遇池经实体组件解析。</summary>
public sealed class EcsMapNodeTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WorldStartupCreatesOneEntityPerCatalogNode()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        using var runtime = new EcsGameRuntime(catalog, SeedTime);

        Assert.Equal(catalog.WorldGraph.Nodes.Count, runtime.MapNodeCount);

        foreach (var node in catalog.WorldGraph.Nodes)
        {
            var entityId = runtime.GetMapNodeEntityId(node.Id);
            Assert.True(entityId.HasValue, $"节点 {node.Id} 必须存在长期实体。");
            var entity = runtime.World.GetEntity(entityId.Value)!;

            var map = entity.GetComponent<MapNodeComponent>()!;
            Assert.Equal(node.Id, map.NodeId);
            Assert.Equal(node.Name, map.Name);
            Assert.Equal(node.X, map.X);
            Assert.Equal(node.Y, map.Y);

            var profile = entity.GetComponent<EncounterProfileComponent>()!;
            Assert.Equal(node.EncounterIds, profile.EncounterIds);

            Assert.NotNull(entity.GetComponent<NodeBuildingComponent>());
            var population = entity.GetComponent<NodePopulationComponent>()!;
            Assert.Empty(population.PresentPlayerEntityIds);
        }
    }

    [Fact]
    public void CreatePlayerOccupiesSpawnNodeAndRemovePlayerLeaves()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        var player = CreatePlayer(catalog, "10000000-0000-4000-8000-000000000201");
        runtime.CreatePlayer(player.State, player.Balances);

        var playerEntity = runtime.World.GetEntity(runtime.ExportSnapshot(player.State.PlayerId.Value).EntityId)!;
        var world = playerEntity.GetComponent<PlayerWorldComponent>()!;
        var spawnEntityId = runtime.GetMapNodeEntityId(world.World.NodeId);
        Assert.True(spawnEntityId.HasValue);

        Assert.Equal(spawnEntityId.Value, world.CurrentNodeEntityId);
        var population = runtime.World.GetEntity(spawnEntityId.Value)!.GetComponent<NodePopulationComponent>()!;
        Assert.Contains(playerEntity.ID, population.PresentPlayerEntityIds);

        Assert.True(runtime.RemovePlayer(player.State.PlayerId.Value));
        Assert.DoesNotContain(playerEntity.ID, population.PresentPlayerEntityIds);
    }

    [Fact]
    public void TravelToMigratesNodeMembership()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        var player = CreatePlayer(catalog, "10000000-0000-4000-8000-000000000202");
        runtime.CreatePlayer(player.State, player.Balances);

        var playerEntity = runtime.World.GetEntity(runtime.ExportSnapshot(player.State.PlayerId.Value).EntityId)!;
        var worldComponent = playerEntity.GetComponent<PlayerWorldComponent>()!;
        var fromEntityId = worldComponent.CurrentNodeEntityId;
        var toNode = catalog.WorldGraph.Nodes.First(node => node.Id != worldComponent.World.NodeId);

        runtime.MutatePlayer(player.State.PlayerId.Value, aggregate => aggregate.TravelTo(toNode.Id));

        var fromPopulation = runtime.World.GetEntity(fromEntityId)!.GetComponent<NodePopulationComponent>()!;
        Assert.DoesNotContain(playerEntity.ID, fromPopulation.PresentPlayerEntityIds);

        var toEntityId = runtime.GetMapNodeEntityId(toNode.Id)!.Value;
        Assert.Equal(toEntityId, worldComponent.CurrentNodeEntityId);
        var toPopulation = runtime.World.GetEntity(toEntityId)!.GetComponent<NodePopulationComponent>()!;
        Assert.Contains(playerEntity.ID, toPopulation.PresentPlayerEntityIds);
    }

    [Fact]
    public void EncounterPoolResolvesThroughNodeEntityComponent()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        var player = CreatePlayer(catalog, "10000000-0000-4000-8000-000000000203");
        runtime.CreatePlayer(player.State, player.Balances);

        var playerEntity = runtime.World.GetEntity(runtime.ExportSnapshot(player.State.PlayerId.Value).EntityId)!;
        var worldComponent = playerEntity.GetComponent<PlayerWorldComponent>()!;
        Assert.NotEqual(0, worldComponent.CurrentNodeEntityId);

        var context = new EcsPlayerActionContext(playerEntity, catalog);
        var expected = runtime.World.GetEntity(worldComponent.CurrentNodeEntityId)!
            .GetComponent<EncounterProfileComponent>()!.EncounterIds;
        Assert.Equal(expected, context.NodeEncounterIds);
    }

    private static (PlayerAggregateState State, IReadOnlyCollection<AssetBalance> Balances) CreatePlayer(
        GameDataCatalog catalog, string playerId)
    {
        var aggregate = PlayerAggregate.Create(new PlayerId(Guid.Parse(playerId)),
            new AccountId(Guid.Parse("20000000-0000-4000-8000-000000000301")),
            "节点测试", SeedTime, catalog.Fingerprint);
        return (aggregate.ExportState(), Array.Empty<AssetBalance>());
    }
}
