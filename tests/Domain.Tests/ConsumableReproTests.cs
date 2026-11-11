namespace Domain.Tests;

/// <summary>
/// 技艺消耗品端到端复现：真实内嵌配表下验证持续消耗品在炼丹行动的激活、加速与扣时，
/// 以及秘法行动（enhancement 大类）下强化成功率消耗品的生效守卫。
/// </summary>
public sealed class ConsumableReproTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name) =>
        PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "catalog-a");

    private static AssetWallet CreateWallet(PlayerAggregate player, params (AssetKey Asset, double Quantity)[] balances)
    {
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Restore(balances.Select(balance => new AssetBalance(player.PlayerId, balance.Asset, balance.Quantity, 0)));
        return wallet;
    }

    /// <summary>定神香在炼丹行动中应自动充能、扣件并按 30% 加速折减单轮耗时。</summary>
    [Fact]
    public void DingshenIncenseActivatesOnAlchemyAction()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("丹修");
        var dingshen = new ItemId("item.dingshen-incense");
        var grass = new ItemId("item.ningxue-grass");
        var wallet = CreateWallet(player,
            (new AssetKey(dingshen), 3),
            (new AssetKey(grass), 30));
        player.ConfigureAutoUse("alchemy", 0, dingshen.Value, 1, true, stackableSlotCount: 3);
        player.Enqueue(new ActionIntent(new ActionId("alchemy.refine-qi-pill"), -1), Baseline, catalog.Actions);

        // 单轮基准 12s；定神香 +30% 后约 9.23s。推进 10s 应完成至少一轮。
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);

        Assert.True(result.CompletedRepeats > 0, $"未完成任何轮次，事件: {string.Join(", ", result.Events.Select(e => e.Code))}");
        Assert.True(wallet.Get(new AssetKey(dingshen)) < 3, "定神香未被消耗");
        Assert.Contains(result.Events, e => e.Code == "auto-use.activated");
        var slot = player.Settings.AutoUseSlots.Single(s => s.ItemId == dingshen.Value);
        Assert.True(slot.RemainingDurationSeconds > 0, $"RemainingDurationSeconds={slot.RemainingDurationSeconds}");
    }

    /// <summary>淬灵粉在装备强化行动上正常充能：消耗 1 件并充入 300 秒持续时长。</summary>
    [Fact]
    public void CuilingPowderActivatesOnEnhancementAction()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("器修");
        var cuiling = new ItemId("item.cuiling-powder");
        var sword = new ItemId("equipment.qingfeng-sword");
        var stone = new ItemId(EnhancementRules.SpiritStoneItemId);
        var wallet = CreateWallet(player,
            (new AssetKey(cuiling), 3),
            (new AssetKey(sword), 1),
            (new AssetKey(stone), 500));
        player.ConfigureAutoUse("enhancement", 0, cuiling.Value, 1, true);
        player.Enqueue(new ActionIntent(new ActionId(EnhancementRules.ActionId), -1, TargetItemId: sword.Value),
            Baseline, catalog.Actions);

        // +1 目标基础耗时 6s，推进 7s 应完成至少一轮强化。
        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(7), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);

        Assert.True(result.CompletedRepeats > 0,
            $"未完成强化轮次，事件: {string.Join(", ", result.Events.Select(e => e.Code))}");
        Assert.True(wallet.Get(new AssetKey(cuiling)) < 3, "淬灵粉未被消耗");
        Assert.Contains(result.Events, e => e.Code == "auto-use.activated");
        var slot = player.Settings.AutoUseSlots.Single(s => s.ItemId == cuiling.Value);
        Assert.True(slot.RemainingDurationSeconds > 0,
            $"RemainingDurationSeconds={slot.RemainingDurationSeconds}");
    }

    /// <summary>
    /// 淬灵粉在秘法化灵行动上不得激活：强化成功率加成仅被装备强化结算消费，
    /// 秘法行动同属 enhancement 大类但无成功率判定，对其充能只会白扣物品与持续时长。
    /// </summary>
    [Fact]
    public void CuilingPowderDoesNotActivateOnSecretArtLiquefy()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer("秘修");
        var cuiling = new ItemId("item.cuiling-powder");
        var hide = new ItemId("item.beast-hide");
        var wallet = CreateWallet(player,
            (new AssetKey(cuiling), 3),
            (new AssetKey(hide), 30));
        player.ConfigureAutoUse("enhancement", 0, cuiling.Value, 1, true);
        player.Enqueue(new ActionIntent(new ActionId("secret_art.liquefy"), -1, TargetItemId: hide.Value),
            Baseline, catalog.Actions);

        var result = SimulationEngine.AdvanceTo(player, Baseline.AddSeconds(10), catalog, wallet);
        result.AssetDeltas.ToList().ForEach(wallet.Apply);

        // 化灵本身正常结算，但淬灵粉不充能不扣件。
        Assert.True(result.CompletedRepeats > 0,
            $"未完成化灵轮次，事件: {string.Join(", ", result.Events.Select(e => e.Code))}");
        Assert.Equal(3, wallet.Get(new AssetKey(cuiling)));
        Assert.DoesNotContain(result.Events, e => e.Code == "auto-use.activated");
        var slot = player.Settings.AutoUseSlots.Single(s => s.ItemId == cuiling.Value);
        Assert.Equal(0, slot.RemainingDurationSeconds);
    }
}
