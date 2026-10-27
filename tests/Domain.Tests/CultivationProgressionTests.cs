using System;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Xunit;

namespace Domain.Tests;

public sealed class CultivationProgressionTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LianjingHuaqiDefinitionExistsInCatalog()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        Assert.True(catalog.Actions.Contains(new ActionId("cultivation.lianjing-huaqi")));

        var action = catalog.Actions.Get(new ActionId("cultivation.lianjing-huaqi"));
        Assert.Equal(ActionKind.Cultivation, action.Kind);
        Assert.Equal(18, action.Level);
        Assert.Equal("炼精化气", action.Name);
        Assert.Equal(10, action.DurationSeconds);

        // 验证 18 级基准产出由 percentage 自动派生为 18 级对应数值
        Assert.Contains(action.Yields, y => y.Kind == ActionYieldKind.Experience && y.Amount >= 100);
        Assert.Contains(action.Yields, y => y.Kind == ActionYieldKind.Mana && y.Amount >= 100);
        Assert.Contains(action.Yields, y => y.Kind == ActionYieldKind.RootExp && y.Amount >= 50);
    }

    [Fact]
    public void ResolveHighestAvailableCultivationReturnsCorrectTier()
    {
        var catalog = GameDataCatalog.LoadEmbedded();

        var lv1 = PlayerAggregate.ResolveHighestAvailableCultivation(catalog.Actions, 1);
        Assert.NotNull(lv1);
        Assert.Equal("cultivation.draw-qi", lv1.Id.Value);

        var lv17 = PlayerAggregate.ResolveHighestAvailableCultivation(catalog.Actions, 17);
        Assert.NotNull(lv17);
        Assert.Equal("cultivation.draw-qi", lv17.Id.Value);

        var lv18 = PlayerAggregate.ResolveHighestAvailableCultivation(catalog.Actions, 18);
        Assert.NotNull(lv18);
        Assert.Equal("cultivation.lianjing-huaqi", lv18.Id.Value);

        var lv30 = PlayerAggregate.ResolveHighestAvailableCultivation(catalog.Actions, 30);
        Assert.NotNull(lv30);
        Assert.Equal("cultivation.lianjing-huaqi", lv30.Id.Value);
    }

    [Fact]
    public void EnqueueCultivationAutoUpgradesForHigherRealmPlayer()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "练气修士", Baseline, catalog.Fingerprint);
        player.Progression.RealmLevel = 18;

        // 18 级玩家请求入队 1 级的引气入体 -> 自动升档为炼精化气
        var queued = player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);
        Assert.Equal("cultivation.lianjing-huaqi", queued.Intent.ActionId.Value);
        Assert.Equal(-1, queued.RemainingRepeats);
    }

    [Fact]
    public void EnqueueCultivationBlocksUnderleveledPlayer()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "凡胎修士", Baseline, catalog.Fingerprint);
        player.Progression.RealmLevel = 1;

        // 1 级玩家越级请求 18 级的炼精化气 -> 拒绝入队
        var ex = Assert.Throws<DomainException>(() =>
            player.Enqueue(new ActionIntent(new ActionId("cultivation.lianjing-huaqi"), -1), Baseline, catalog.Actions));
        Assert.Equal(DomainErrorCode.Conflict, ex.Code);
    }

    [Fact]
    public void BreakthroughAutoUpgradesQueuedCultivationAction()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "破境修士", Baseline, catalog.Fingerprint);
        player.Progression.RealmLevel = 17;

        // 17 级挂机引气入体
        var queued = player.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), Baseline, catalog.Actions);
        Assert.Equal("cultivation.draw-qi", queued.Intent.ActionId.Value);

        // 满经验突破到 18 级（叩天门）
        var expCap = catalog.RealmProgression!.ExpCapFor(17);
        player.Progression.Experience = expCap;
        player.Breakthrough(catalog.RealmProgression, catalog.Actions);

        Assert.Equal(18, player.Progression.RealmLevel);
        // 队列中的行动平滑自动切换为炼精化气
        Assert.Equal("cultivation.lianjing-huaqi", player.Actions[0].Intent.ActionId.Value);
        Assert.Equal(-1, player.Actions[0].RemainingRepeats);
    }

    [Fact]
    public void SpiritStoneScale_Tier1VersusTier2()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var drawQi = catalog.Actions.Get(new ActionId("cultivation.draw-qi"));
        var lianjing = catalog.Actions.Get(new ActionId("cultivation.lianjing-huaqi"));

        Assert.Equal(1, PlayerAggregate.ResolveCultivationSpiritStoneScale(drawQi));
        Assert.Equal(2, PlayerAggregate.ResolveCultivationSpiritStoneScale(lianjing));
    }

    [Fact]
    public void CultivationSpiritStoneDoublingExecution()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "注灵修士", Baseline, catalog.Fingerprint);
        player.Progression.RealmLevel = 18;
        var stone = new ItemId(EnhancementRules.SpiritStoneItemId);
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Apply(new AssetDelta(player.PlayerId, new AssetKey(stone), 1000, "test"));

        // 18 级角色挂炼精化气，配置 20 灵石档位 (二级翻倍的中档)
        var intent = new ActionIntent(
            new ActionId("cultivation.lianjing-huaqi"),
            1,
            Parameters: new Dictionary<string, double> { ["spiritStone"] = 20 });
        player.Enqueue(intent, Baseline, catalog.Actions);

        // 推进 10.2 秒完成一轮
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10.2), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);

        // 验证扣减了 20 灵石（剩余 980）
        Assert.Equal(980, wallet.Get(new AssetKey(stone)));

        // 验证产出：18 级基准 133 * 2.0 = 266 修为
        Assert.Equal(266, player.Progression.Experience);
    }
}
