using Domain.Economy;
using System.Text.Json;

namespace Domain.Tests;

/// <summary>
/// 资源模拟测试：配置回复跨恢复一致性与资源耗尽后的已结算产出。
/// </summary>
public sealed class ResourceSimulationTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 验证 <c>ConfiguredRegenCarriesAcrossRestoreAndMatchesOfflineAdvancement</c>（Configured Regen Carries Across Restore And Matches Offline Advancement）所描述的领域行为。
    /// </summary>
    [Fact]
    public void ConfiguredRegenCarriesAcrossRestoreAndMatchesOfflineAdvancement()
    {
        var attributes = new BaseAttributeCatalog([new(1, 1000, 100, 10, 0, 0, 0, 100, 0, 0, 0, 0, 0, 7, 3)]);
        using var equipment = JsonDocument.Parse("{\"flatStats\":{\"maxHitpoints\":500}}");
        var item = new ItemId("equipment.hp-only");
        var catalog = new GameDataCatalog("test", "test", new ActionCatalog([]), baseAttributes: attributes,
            rawEquipments: new Dictionary<string, JsonElement> { [item.Value] = equipment.RootElement });
        var offline = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "回复", Baseline, "test");
        var state = offline.ExportState();
        offline = PlayerAggregate.Restore(state with
        {
            Loadout = state.Loadout with { CoreAttributes = new(0, 0, 0, 0, 0, 0) },
            EquippedItemIds = [item]
        });
        offline.Combat.Hitpoints = 1;
        offline.Combat.Manapoints = 0;
        var online = PlayerAggregate.Restore(offline.ExportState());
        SimulationEngine.AdvanceTo(online, Baseline.AddSeconds(4.5), catalog);
        Assert.Equal(1, online.Combat.Hitpoints);
        Assert.Equal(4.5, online.Combat.RegenCarrySeconds);
        online = PlayerAggregate.Restore(online.ExportState());
        SimulationEngine.AdvanceTo(online, Baseline.AddSeconds(5), catalog);
        Assert.Equal(1500, online.Combat.MaxHitpoints);
        Assert.Equal(8, online.Combat.Hitpoints);
        Assert.Equal(3, online.Combat.Manapoints);
        SimulationEngine.AdvanceTo(online, Baseline.AddSeconds(12), catalog);
        SimulationEngine.AdvanceTo(offline, Baseline.AddSeconds(12), catalog);
        Assert.Equal(15, offline.Combat.Hitpoints);
        Assert.Equal(6, offline.Combat.Manapoints);
        Assert.Equal(offline.Combat.Hitpoints, online.Combat.Hitpoints);
        Assert.Equal(offline.Combat.Manapoints, online.Combat.Manapoints);
        Assert.Equal(2, online.Combat.RegenCarrySeconds);
    }

    /// <summary>
    /// 验证 <c>ResourceExhaustionKeepsSettledOutputAndAllowsFollowingAction</c>（Resource Exhaustion Keeps Settled Output And Allows Following Action）所描述的领域行为。
    /// </summary>
    [Fact]
    public void ResourceExhaustionKeepsSettledOutputAndAllowsFollowingAction()
    {
        var material = new ItemId("item.material");
        var output = new ItemId("item.product");
        var craft = new ActionId("test.craft");
        var gather = new ActionId("test.gather");
        var actions = new ActionCatalog([
            new(craft, "加工", ActionKind.Exploration, 2, new Dictionary<ItemId, double> { [material] = 1 }, [new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, output)]),
            new(gather, "采集", ActionKind.Exploration, 2, new Dictionary<ItemId, double>(), [new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, material)])
        ]);
        var catalog = new GameDataCatalog("test", "test", actions);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "材料", Baseline, "test");
        player.SetLoopEnabled(false);
        player.Enqueue(new(craft, 2), Baseline, actions);
        player.Enqueue(new(gather, 1), Baseline, actions);
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Credit(new AssetKey(material), 1, "测试初始材料");
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet);
        foreach (var delta in result.AssetDeltas) wallet.Apply(delta);
        Assert.Equal(1, wallet.Get(new AssetKey(output)));
        Assert.Equal(1, wallet.Get(new AssetKey(material)));
        Assert.Equal(2, result.CompletedActions);
        Assert.Empty(player.Actions);
        Assert.Contains(result.Events, e => e.Code == "action.blocked");
    }
}
