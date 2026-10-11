using System.Text.Json;
using DaoHua.Ecs;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Xunit;

namespace Ecs.Tests;

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
        // 6 = 两轮采集产出（seed 玩家引导已终态，无首轮赠送）。
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

    [Fact]
    public async Task FiniteGatheringThenCombatKeepsTheSameBoundaryAndResults()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.Parse("10000000-0000-4000-8000-000000000013");
        var accountId = Guid.Parse("20000000-0000-4000-8000-000000000013");
        var player = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(accountId),
            "采集接战斗", SeedTime, catalog.Fingerprint);
        player.SetPresence(PresenceState.Offline, SeedTime);
        player.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), 1), SeedTime, catalog.Actions);
        player.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1), SeedTime, catalog.Actions);
        var seed = player.ExportState();
        var expected = PlayerAggregate.Restore(seed);
        var wallet = CreateWallet(expected, Array.Empty<AssetBalance>());
        var target = SeedTime.AddSeconds(37);
        var result = SimulationEngine.AdvanceTo(expected, target, catalog, wallet);
        foreach (var delta in result.AssetDeltas) wallet.Apply(delta);

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        runtime.CreatePlayer(seed);
        await runtime.RunPlayerOnceAsync(playerId, target);
        var actual = runtime.ExportSnapshot(playerId);

        AssertStateEqual(expected.ExportState(), actual.State, "采集接战斗");
        AssertAssetsEqual(wallet, actual.Assets, "采集接战斗");
        static string EventJson(IEnumerable<SimulationEvent> events)
            => JsonSerializer.Serialize(events.Select(entry => new
            {
                entry.At, entry.Code, entry.Message, entry.ActionId,
                Items = entry.ItemDeltas?.Select(pair => new { Item = pair.Key.Value, Quantity = pair.Value }).ToArray()
            }));
        Assert.Equal(EventJson(result.Events), EventJson(runtime.DrainSimulationEvents(playerId)));
    }

    /// <summary>
    /// 离线锚点封顶（§14.2）：同一份断线存档一次推进 60h 与按 20h/40h/60h 三段推进，
    /// 导出状态、钱包增量与完成轮次逐项一致，收益都只算到锚点 + 48h；
    /// offline.capped 全程恰好一次，且出现在跨越边界的那段。
    /// </summary>
    [Fact]
    public async Task OfflineAnchorCapIsIdenticalAcrossOneShotAndSegmentedAdvances()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.Parse("10000000-0000-4000-8000-000000000014");
        var accountId = Guid.Parse("20000000-0000-4000-8000-000000000014");
        var seed = CreateSeedState(catalog, playerId, accountId, "离线封顶",
            new ActionId("cultivation.draw-qi"), -1);
        var target = SeedTime.AddHours(60);

        // 收益基准：同一初态只推进到锚点 + 48h（离线收益边界）。
        var reference = PlayerAggregate.Restore(seed);
        var referenceWallet = CreateWallet(reference, []);
        SimulationEngine.AdvanceTo(reference, SeedTime.AddHours(48), catalog, referenceWallet);

        // 一次性推进 T0 → T0+60h。
        using var oneShot = new EcsGameRuntime(catalog, SeedTime);
        oneShot.CreatePlayer(seed);
        await oneShot.RunPlayerOnceAsync(playerId, target);
        var oneShotSnapshot = oneShot.ExportSnapshot(playerId);
        var oneShotEvents = oneShot.DrainSimulationEvents(playerId);

        // 相同初态按 T0→+20h →+40h →+60h 三段推进。
        using var segmented = new EcsGameRuntime(catalog, SeedTime);
        segmented.CreatePlayer(seed);
        var segmentedEvents = new List<SimulationEvent>();
        var cappedSegments = new List<int>();
        for (var index = 0; index < 3; index++)
        {
            await segmented.RunPlayerOnceAsync(playerId, SeedTime.AddHours((index + 1) * 20));
            var drained = segmented.DrainSimulationEvents(playerId);
            if (drained.Any(entry => entry.Code == "offline.capped")) cappedSegments.Add(index);
            segmentedEvents.AddRange(drained);
        }
        var segmentedSnapshot = segmented.ExportSnapshot(playerId);

        // 一次性与三段推进的导出状态、钱包增量、完成轮次逐项一致。
        // StateVersion 按请求次数推进（三段多两次请求级版本递增），不属收益口径，比对时归零。
        var oneShotState = oneShotSnapshot.State with { StateVersion = 0 };
        var segmentedState = segmentedSnapshot.State with { StateVersion = 0 };
        Assert.Equal(JsonSerializer.Serialize(oneShotState), JsonSerializer.Serialize(segmentedState));
        Assert.Equal(JsonSerializer.Serialize(oneShotSnapshot.Assets), JsonSerializer.Serialize(segmentedSnapshot.Assets));
        Assert.Equal(oneShotSnapshot.State.Actions[0].CompletedRepeats, segmentedSnapshot.State.Actions[0].CompletedRepeats);

        // 两种推进方式的收益都等于只推进到锚点 + 48h。
        Assert.Equal(reference.Progression.Experience, oneShotSnapshot.State.Progression.Experience, 9);
        Assert.Equal(reference.Progression.Experience, segmentedSnapshot.State.Progression.Experience, 9);
        Assert.Equal(reference.Progression.CompletedActions, oneShotSnapshot.State.Progression.CompletedActions);
        Assert.Equal(reference.Progression.CompletedActions, segmentedSnapshot.State.Progression.CompletedActions);
        AssertAssetsEqual(referenceWallet, oneShotSnapshot.Assets, "一次性 60h 封顶");
        AssertAssetsEqual(referenceWallet, segmentedSnapshot.Assets, "三段 60h 封顶");

        // offline.capped 全程恰好一次：一次性请求一个，三段里只在跨边界的 +60h 段出现。
        Assert.Equal(1, oneShotEvents.Count(entry => entry.Code == "offline.capped"));
        Assert.Equal(1, segmentedEvents.Count(entry => entry.Code == "offline.capped"));
        Assert.Equal([2], cappedSegments);
    }

    /// <summary>请求起点已越过锚点边界：不再产生收益、不再发 offline.capped，时钟照常推进到目标。</summary>
    [Fact]
    public async Task AdvanceStartingBeyondAnchorCapYieldsNoIncomeAndNoCappedEvent()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.Parse("10000000-0000-4000-8000-000000000015");
        var accountId = Guid.Parse("20000000-0000-4000-8000-000000000015");
        var seed = CreateSeedState(catalog, playerId, accountId, "越界请求",
            new ActionId("cultivation.draw-qi"), -1);

        using var runtime = new EcsGameRuntime(catalog, SeedTime);
        runtime.CreatePlayer(seed);
        // 首次请求跨越锚点 +48h 边界：该次的 capped 事件先消费掉。
        await runtime.RunPlayerOnceAsync(playerId, SeedTime.AddHours(50));
        Assert.Equal(1, runtime.DrainSimulationEvents(playerId).Count(entry => entry.Code == "offline.capped"));
        var before = runtime.ExportSnapshot(playerId);

        await runtime.RunPlayerOnceAsync(playerId, SeedTime.AddHours(60));
        var after = runtime.ExportSnapshot(playerId);
        var events = runtime.DrainSimulationEvents(playerId);

        Assert.DoesNotContain(events, entry => entry.Code == "offline.capped");
        Assert.Equal(SeedTime.AddHours(60), after.State.LastSimulatedAt);
        Assert.Equal(before.State.Progression.Experience, after.State.Progression.Experience);
        Assert.Equal(before.State.Progression.CompletedActions, after.State.Progression.CompletedActions);
        Assert.Equal(before.State.Actions[0].CompletedRepeats, after.State.Actions[0].CompletedRepeats);
        Assert.Equal(JsonSerializer.Serialize(before.Assets), JsonSerializer.Serialize(after.Assets));
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
        player.SetPresence(PresenceState.Offline, SeedTime);
        // parity 门禁与引导语义无关：终态化避免教程里程碑推进对绑定行动
        // 的强制出队截断改变无限重复边界。
        player.SkipTutorial();
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
