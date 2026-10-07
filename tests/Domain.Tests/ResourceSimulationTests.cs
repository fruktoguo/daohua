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
    /// 气血/灵力回复的亚秒进位余量（RegenCarrySeconds）随快照持久化并跨恢复持续累积；
    /// 分段在线推进与整段离线补算的回复结果必须逐值一致（装备加成参与回复基数）。
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
        offline.Resources.Hitpoints = 1;
        offline.Resources.Manapoints = 0;
        var online = PlayerAggregate.Restore(offline.ExportState());
        SimulationEngine.AdvanceTo(online, Baseline.AddSeconds(4.5), catalog);
        Assert.Equal(1, online.Resources.Hitpoints);
        Assert.Equal(4.5, online.Resources.RegenCarrySeconds);
        online = PlayerAggregate.Restore(online.ExportState());
        SimulationEngine.AdvanceTo(online, Baseline.AddSeconds(5), catalog);
        Assert.Equal(1500, online.Resources.MaxHitpoints);
        Assert.Equal(8, online.Resources.Hitpoints, 6);
        Assert.Equal(3, online.Resources.Manapoints, 6);
        SimulationEngine.AdvanceTo(online, Baseline.AddSeconds(12), catalog);
        SimulationEngine.AdvanceTo(offline, Baseline.AddSeconds(12), catalog);
        Assert.Equal(15, offline.Resources.Hitpoints, 6);
        Assert.Equal(6, offline.Resources.Manapoints, 6);
        Assert.Equal(offline.Resources.Hitpoints, online.Resources.Hitpoints);
        Assert.Equal(offline.Resources.Manapoints, online.Resources.Manapoints);
        Assert.Equal(2, online.Resources.RegenCarrySeconds);
    }

    /// <summary>
    /// 材料耗尽时已完成轮次的产出不回滚（action.blocked 记录阻塞），
    /// 队头受阻后轮换执行后续可满足的行动。
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
