using System.Text.Json;
using DaoHua.Ecs;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Xunit;

namespace ServerEcs.Tests;

/// <summary>
/// 采集与制造行动的完整 ECS parity 门禁。两边从同一份已入队快照开始，
/// 避免随机行动实例 ID 把测试变成命令生成器测试；状态和资产都必须与公共 OOP 快进入口一致。
/// </summary>
public sealed class EcsActionParityTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InfiniteGatheringMatchesOopAcrossMultipleRepeats()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.Parse("10000000-0000-4000-8000-000000000011");
        var accountId = Guid.Parse("20000000-0000-4000-8000-000000000011");
        var actionId = new ActionId("explore.gather-ningxue-grass");
        var seed = CreateSeedState(catalog, playerId, accountId, "Ecs采集", actionId, -1);
        var expected = PlayerAggregate.Restore(seed);
        var balances = Array.Empty<AssetBalance>();
        var expectedWallet = CreateWallet(expected, balances);
        var expectedResult = SimulationEngine.AdvanceTo(expected, SeedTime.AddSeconds(17), catalog, expectedWallet);
        foreach (var delta in expectedResult.AssetDeltas) expectedWallet.Apply(delta);

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        runtime.CreatePlayer(seed, balances);
        await runtime.RunPlayerOnceAsync(playerId, SeedTime.AddSeconds(17));
        var actual = runtime.ExportSnapshot(playerId);

        AssertStateEqual(expected.ExportState(), actual.State, "采集");
        AssertAssetsEqual(expectedWallet, actual.Assets, "采集");
        var action = Assert.Single(actual.State.Actions);
        Assert.Equal(actionId, action.Intent.ActionId);
        Assert.Equal(-1, action.RemainingRepeats);
        Assert.Equal(1, action.ProgressSeconds, 12);
        Assert.Equal(6, actual.Assets[new AssetKey(new ItemId("item.ningxue-grass"))], 12);
    }

    [Fact]
    public async Task InfiniteCraftingMatchesOopUntilMaterialsBlock()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.Parse("10000000-0000-4000-8000-000000000012");
        var accountId = Guid.Parse("20000000-0000-4000-8000-000000000012");
        var actionId = new ActionId("alchemy.refine-qi-pill");
        var seed = CreateSeedState(catalog, playerId, accountId, "Ecs制造", actionId, -1);
        var expected = PlayerAggregate.Restore(seed);
        var material = new AssetKey(new ItemId("item.ningxue-grass"));
        var output = new AssetKey(new ItemId("item.qi-gathering-pill"));
        var balances = new[] { new AssetBalance(expected.PlayerId, material, 9, 0) };
        var expectedWallet = CreateWallet(expected, balances);
        // 3 × 12 秒恰好消耗初始材料，再给出 1 秒让下一轮进入材料不足阻塞路径。
        var expectedResult = SimulationEngine.AdvanceTo(expected, SeedTime.AddSeconds(37), catalog, expectedWallet);
        foreach (var delta in expectedResult.AssetDeltas) expectedWallet.Apply(delta);

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        runtime.CreatePlayer(seed, balances);
        await runtime.RunPlayerOnceAsync(playerId, SeedTime.AddSeconds(37));
        var actual = runtime.ExportSnapshot(playerId);

        AssertStateEqual(expected.ExportState(), actual.State, "制造");
        AssertAssetsEqual(expectedWallet, actual.Assets, "制造");
        var action = Assert.Single(actual.State.Actions);
        Assert.Equal(actionId, action.Intent.ActionId);
        Assert.Equal(-1, action.RemainingRepeats);
        Assert.Equal(ActionQueueStatus.Blocked, action.Status);
        Assert.Equal(0, actual.Assets.GetValueOrDefault(material));
        Assert.Equal(3, actual.Assets.GetValueOrDefault(output));
    }

    private static PlayerAggregateState CreateSeedState(
        GameDataCatalog catalog,
        Guid playerId,
        Guid accountId,
        string displayName,
        ActionId actionId,
        double repeatCount)
    {
        var player = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(accountId),
            displayName, SeedTime, catalog.Fingerprint);
        player.Enqueue(new ActionIntent(actionId, repeatCount), SeedTime, catalog.Actions);
        return player.ExportState();
    }

    private static AssetWallet CreateWallet(PlayerAggregate player, IReadOnlyCollection<AssetBalance> balances)
    {
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Restore(balances);
        return wallet;
    }

    private static void AssertStateEqual(PlayerAggregateState expected, PlayerAggregateState actual, string scenario)
    {
        var expectedJson = JsonSerializer.Serialize(expected);
        var actualJson = JsonSerializer.Serialize(actual);
        Assert.True(string.Equals(expectedJson, actualJson, StringComparison.Ordinal),
            $"ECS {scenario} 状态与 OOP 不一致。expected={expectedJson} actual={actualJson}");
    }

    private static void AssertAssetsEqual(
        AssetWallet expected,
        IReadOnlyDictionary<AssetKey, double> actual,
        string scenario)
    {
        var expectedAssets = expected.Balances.ToDictionary(balance => balance.Asset, balance => balance.Quantity);
        Assert.Equal(expectedAssets.Count, actual.Count);
        foreach (var (asset, quantity) in expectedAssets)
            Assert.True(actual.TryGetValue(asset, out var actualQuantity) &&
                        Math.Abs(actualQuantity - quantity) <= 1e-12,
                $"ECS {scenario} 资产 {asset} 不一致。expected={quantity} actual={actual.GetValueOrDefault(asset)}");
    }
}
