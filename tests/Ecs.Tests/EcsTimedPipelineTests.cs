using System.Text.Json;
using DaoHua.Ecs;
using DaoHua.Ecs.Components;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Xunit;

namespace Ecs.Tests;

/// <summary>验证真实阶段调度中重复零时间准备不会重复消费、入账或推进资源窗口。</summary>
public sealed class EcsTimedPipelineTests
{
    [Fact]
    public async Task RepeatedPreparationTicksKeepResourcesQueueAndReportsEqualToOop()
    {
        var from = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var target = from.AddSeconds(11);
        var incense = new ItemId("item.test-incense");
        var flame = new ItemId("item.test-flame");
        var herb = new ItemId("item.test-herb");
        var pill = new ItemId("item.test-pill");
        var actions = new ActionCatalog([
            new(new ActionId("test.gather"), "采集", ActionKind.Exploration, 2,
                new Dictionary<ItemId, double>(), [new(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 3, herb)]),
            new(new ActionId("test.craft"), "制造", ActionKind.Alchemy, 2,
                new Dictionary<ItemId, double> { [herb] = 3 }, [new(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, pill)], Technique: "alchemy")
        ]);
        var catalog = new GameDataCatalog("test", "test", actions, new Dictionary<ItemId, ItemDefinition>
        {
            [incense] = new(incense, "寻灵香", "", Consumable: new(new HashSet<string> { "exploration" }, 300, true, GatheringMultiplier: 1.5)),
            [flame] = new(flame, "地火引", "", Consumable: new(new HashSet<string> { "alchemy" }, 300, true, AlchemyMultiplier: 1.25))
        });
        var player = PlayerAggregate.Create(new(Guid.Parse("10000000-0000-4000-8000-000000000090")),
            new(Guid.Parse("20000000-0000-4000-8000-000000000090")), "阶段回归", from, catalog.Fingerprint);
        player.ConfigureAutoUse("exploration", 0, incense.Value, 1, true);
        player.ConfigureAutoUse("alchemy", 0, flame.Value, 1, true);
        player.Buffs.Active.Add(new("test.expiry", from.AddSeconds(3).ToUnixTimeSeconds()));
        player.Enqueue(new(new ActionId("test.gather")), from, actions);
        player.Enqueue(new(new ActionId("test.craft")), from, actions);
        var seed = player.ExportState();
        var balances = new[] { new AssetBalance(player.PlayerId, new(incense), 1, 0), new AssetBalance(player.PlayerId, new(flame), 1, 0) };
        var expectedWallet = new AssetWallet(player.PlayerId);
        expectedWallet.Restore(balances);
        var expectedResult = SimulationEngine.AdvanceTo(player, target, catalog, expectedWallet);
        foreach (var delta in expectedResult.AssetDeltas) expectedWallet.Apply(delta);

        using var runtime = new EcsGameRuntime(catalog, from);
        var id = runtime.CreatePlayer(seed, balances);
        var entity = runtime.World.GetEntity(runtime.ExportSnapshot(id).EntityId)!;
        var clock = runtime.World.WorldMain.GetComponent<EcsClockComponent>()!;
        var execution = EcsActionExecutionComponent.Attach(entity, catalog, target);
        var tickCount = 0;
        async Task Tick(DateTimeOffset at, bool preparation)
        {
            if (++tickCount > 100) throw new InvalidOperationException("阶段测试未收敛。");
            await runtime.Fiber.PostAsync((_, _) =>
            {
                clock.Current = clock.Target = at;
                clock.RequestTarget = target;
                clock.DeltaSeconds = preparation ? 0 : (at - EcsActionSession.Current(execution)).TotalSeconds;
                clock.ActivePlayerId = id;
                clock.PreparationOnly = preparation;
                return ValueTask.CompletedTask;
            });
            await runtime.Fiber.TickAsync();
        }
        while (!EcsActionSession.IsComplete(execution))
        {
            do { await Tick(EcsActionSession.Current(execution), true); } while (EcsActionSession.NeedsPreparation(execution));
            var prepared = JsonSerializer.Serialize(runtime.ExportSnapshot(id).State);
            await Tick(EcsActionSession.Current(execution), true);
            Assert.Equal(prepared, JsonSerializer.Serialize(runtime.ExportSnapshot(id).State));
            if (!EcsActionSession.IsComplete(execution)) await Tick(EcsActionSession.NextBoundaryAt(execution), false);
        }
        var actualResult = EcsActionSession.Commit(execution);
        var actual = runtime.ExportSnapshot(id);
        Assert.Equal(JsonSerializer.Serialize(player.ExportState()), JsonSerializer.Serialize(actual.State));
        Assert.Equal(JsonSerializer.Serialize(expectedResult.Events.Select(entry => new
        {
            entry.At, entry.Code, entry.Message, entry.ActionId,
            Items = entry.ItemDeltas?.Select(pair => new { Item = pair.Key.Value, Quantity = pair.Value }).ToArray()
        })), JsonSerializer.Serialize(actualResult.Events.Select(entry => new
        {
            entry.At, entry.Code, entry.Message, entry.ActionId,
            Items = entry.ItemDeltas?.Select(pair => new { Item = pair.Key.Value, Quantity = pair.Value }).ToArray()
        })));
        Assert.Equal(JsonSerializer.Serialize(expectedResult.AssetDeltas), JsonSerializer.Serialize(actualResult.AssetDeltas));
        Assert.Equal(expectedWallet.Balances.OrderBy(x => x.Asset.ToString()).Select(x => (x.Asset, x.Quantity)),
            actual.Assets.OrderBy(x => x.Key.ToString()).Select(x => (x.Key, x.Value)));
    }
}
