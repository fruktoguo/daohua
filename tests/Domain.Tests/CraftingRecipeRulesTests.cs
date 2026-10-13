using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;

namespace Domain.Tests;

/// <summary>
/// 制造配方图鉴解锁等级规则与怪物击败记录测试。
/// 验证总纲 §23 万象图鉴与解锁机制。
/// </summary>
public sealed class CraftingRecipeRulesTests
{
    private static readonly DateTimeOffset Baseline = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 18)]
    [InlineData(3, 30)]
    [InlineData(4, 42)]
    public void ConvertTierToLevelCalculatesCorrectly(int tier, int expectedLevel)
    {
        Assert.Equal(expectedLevel, CraftingRecipeRules.ConvertTierToLevel(tier));
    }

    [Fact]
    public void EmbeddedRecipesResolveCorrectUnlockLevels()
    {
        var catalog = GameDataCatalog.LoadEmbedded();

        // T1 配方引气丹产出 item.qi-gathering-pill (Tier 1)，所需境界等级应为 1
        if (catalog.Actions.TryGet(new ActionId("alchemy.refine-qi-pill"), out var t1Alchemy))
        {
            var level = CraftingRecipeRules.ResolveRequiredRealmLevel(t1Alchemy, catalog);
            Assert.Equal(1, level);
        }

        // T2 配方通脉丹产出 item.tongmai-pill (Tier 2)，所需境界等级应为 18
        if (catalog.Actions.TryGet(new ActionId("alchemy.refine-tongmai-pill"), out var t2Alchemy))
        {
            var level = CraftingRecipeRules.ResolveRequiredRealmLevel(t2Alchemy, catalog);
            Assert.Equal(18, level);
        }

        // T1 锻造青锋剑产出 equipment.qingfeng-sword (Tier 1)，所需境界等级应为 1
        if (catalog.Actions.TryGet(new ActionId("forge.qingfeng-sword"), out var t1Forge))
        {
            var level = CraftingRecipeRules.ResolveRequiredRealmLevel(t1Forge, catalog);
            Assert.Equal(1, level);
        }

        // 雷击符与聚灵阵盘当前设定为 1 级解锁
        if (catalog.Actions.TryGet(new ActionId("talisman.draw-leiji"), out var talisman))
        {
            var level = CraftingRecipeRules.ResolveRequiredRealmLevel(talisman, catalog);
            Assert.Equal(1, level);
        }
        if (catalog.Actions.TryGet(new ActionId("formation.deploy-juling"), out var formation))
        {
            var level = CraftingRecipeRules.ResolveRequiredRealmLevel(formation, catalog);
            Assert.Equal(1, level);
        }
    }

    [Fact]
    public void DefeatedMonsterIdsAreRecordedAndPersisted()
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "测试道友", Baseline, "catalog-a");
        Assert.Empty(player.Progression.DefeatedMonsterIds);

        player.Progression.DefeatedMonsterIds.Add("enemy.wolf");
        player.Progression.DefeatedMonsterIds.Add("enemy.bear");

        var snapshot = player.ExportState();
        Assert.NotNull(snapshot.Progression.DefeatedMonsterIds);
        Assert.Equal(2, snapshot.Progression.DefeatedMonsterIds.Count);
        Assert.Contains("enemy.wolf", snapshot.Progression.DefeatedMonsterIds);
        Assert.Contains("enemy.bear", snapshot.Progression.DefeatedMonsterIds);

        // 文档往返重建
        var restored = PlayerAggregate.Restore(snapshot);
        Assert.Equal(2, restored.Progression.DefeatedMonsterIds.Count);
        Assert.Contains("enemy.wolf", restored.Progression.DefeatedMonsterIds);
        Assert.Contains("enemy.bear", restored.Progression.DefeatedMonsterIds);
    }
}
