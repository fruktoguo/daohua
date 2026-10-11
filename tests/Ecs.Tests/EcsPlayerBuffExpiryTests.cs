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

/// <summary>持久 Buff 到期门禁：自然窗口与封顶尾段均由玩家组件上的同步 tag 裁决。</summary>
public sealed class EcsPlayerBuffExpiryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NaturalExpiryKeepsNormalAndBulkResultsAndMarksOnlyRealRemoval(bool bulk)
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "增益自然到期", Start, catalog.Fingerprint);
        player.SetPresence(PresenceState.Offline, Start);
        player.Enqueue(new(new ActionId("explore.gather-ningxue-grass"), -1), Start, catalog.Actions);
        var nowUnix = Start.ToUnixTimeSeconds();
        player.Buffs.Active.Add(new("test.expired", nowUnix));
        player.Buffs.Active.Add(new("test.remaining-a", nowUnix + 20));
        player.Buffs.Active.Add(new("test.soon", nowUnix + 5, HpRegen: 3));
        player.Buffs.Active.Add(new("test.remaining-b", nowUnix + 30));
        var seed = player.ExportState();
        var expected = PlayerAggregate.Restore(seed);
        var wallet = new AssetWallet(player.PlayerId);
        var target = Start.AddSeconds(6);
        var expectedResult = SimulationEngine.AdvanceTo(expected, target, catalog, wallet);

        using var runtime = new EcsGameRuntime(catalog, Start);
        var id = runtime.CreatePlayer(seed);
        if (bulk) await runtime.RunPlayerOnceAsync(id, target);
        else await runtime.RunOnceAsync(target);
        var actual = runtime.ExportSnapshot(id).State;
        Assert.Equal(JsonSerializer.Serialize(expected.ExportState()), JsonSerializer.Serialize(actual));
        Assert.Equal(EventJson(expectedResult.Events), EventJson(runtime.DrainSimulationEvents(id)));
        Assert.Equal(new[] { "test.remaining-a", "test.remaining-b" }, actual.Buffs.Select(buff => buff.BuffId));
        Assert.Contains(PlayerSectionKind.Buffs, runtime.GetDirtySections(id));
        var mark = runtime.GetDirtyMarks(id)[PlayerSectionKind.Buffs];
        runtime.AcknowledgeDirtySections(id, [PlayerSectionKind.Buffs]);

        if (bulk) await runtime.RunPlayerOnceAsync(id, target.AddSeconds(1));
        else await runtime.RunOnceAsync(target.AddSeconds(1));
        Assert.DoesNotContain(PlayerSectionKind.Buffs, runtime.GetDirtySections(id));
        Assert.Equal(mark, runtime.GetDirtyMarks(id)[PlayerSectionKind.Buffs]);
        AssertInboxReleased(runtime, id);
    }

    [Fact]
    public async Task RestoreAndOfflineCapExpireCurrentBuffStateWithoutSimulatingTailRewards()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "增益封顶到期", Start, catalog.Fingerprint);
        player.SetPresence(PresenceState.Offline, Start);
        var nowUnix = Start.ToUnixTimeSeconds();
        player.Buffs.Active.Add(new("test.old-state", nowUnix + 70 * 3600));
        using var runtime = new EcsGameRuntime(catalog, Start);
        var id = runtime.CreatePlayer(player.ExportState());
        await runtime.RunPlayerOnceAsync(id, Start.AddSeconds(1));

        player.Buffs.Active.Clear();
        player.Buffs.Active.Add(new("test.capped-tail", nowUnix + 48.5 * 3600, HpRegen: 9));
        player.Buffs.Active.Add(new("test.still-active", nowUnix + 50 * 3600));
        var seed = player.ExportState();
        runtime.RestorePlayer(id, seed, Array.Empty<AssetBalance>());
        var expected = PlayerAggregate.Restore(seed);
        var target = Start.AddHours(49);
        var expectedResult = SimulationEngine.AdvanceTo(expected, target, catalog);
        await runtime.RunPlayerOnceAsync(id, target);
        var actual = runtime.ExportSnapshot(id).State;

        Assert.Equal(JsonSerializer.Serialize(expected.ExportState()), JsonSerializer.Serialize(actual));
        var events = runtime.DrainSimulationEvents(id);
        Assert.Equal(EventJson(expectedResult.Events), EventJson(events));
        Assert.Equal("test.still-active", Assert.Single(actual.Buffs).BuffId);
        Assert.Equal(target, actual.LastSimulatedAt);
        Assert.Equal(0, actual.Progression.CompletedActions);
        Assert.Contains(events, entry => entry.Code == "offline.capped");
        Assert.Contains(PlayerSectionKind.Buffs, runtime.GetDirtySections(id));
        var entity = runtime.World.GetEntity(runtime.ExportSnapshot(id).EntityId)!;
        Assert.Same(entity.GetComponent<PlayerBuffComponent>()!.Buffs,
            entity.GetComponent<PlayerStatsComponent>()!.BuffsInput);
        AssertInboxReleased(runtime, id);
    }

    private static void AssertInboxReleased(EcsGameRuntime runtime, Guid id)
    {
        var inbox = runtime.World.GetEntity(runtime.ExportSnapshot(id).EntityId)!.GetComponent<PlayerBuffExpiryInboxComponent>()!;
        Assert.Equal(0, inbox.AtUnixSeconds);
        Assert.False(inbox.Changed);
    }

    private static string EventJson(IEnumerable<SimulationEvent> events)
        => JsonSerializer.Serialize(events.Select(entry => new { entry.At, entry.Code, entry.Message, entry.ActionId }));
}
