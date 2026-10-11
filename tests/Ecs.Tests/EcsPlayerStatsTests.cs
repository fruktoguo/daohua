using System.Text.Json;
using DaoHua.Ecs;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Xunit;

namespace Ecs.Tests;

/// <summary>统计反应跨入口门禁：批量/普通推进与命令都写实际结算量，并保留 UTC+8 跨日和三账本语义。</summary>
public sealed class EcsPlayerStatsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimedStatsKeepActualCreditsAndDailyRolloverAcrossNormalAndBulkPaths(bool bulk)
    {
        // 首轮在 UTC+8 午夜前完成，其余三轮归属新一天；修为首轮触顶后只增加底蕴。
        var from = new DateTimeOffset(2026, 9, 30, 15, 59, 57, TimeSpan.Zero);
        var herb = new ItemId("item.test-herb");
        var pill = new ItemId("item.test-pill");
        var actionId = new ActionId("test.craft");
        var actions = new ActionCatalog([
            new(actionId, "统计制造", ActionKind.Alchemy, 2,
                new Dictionary<ItemId, double> { [herb] = 1 },
                [new(ActionYieldKind.Experience, ActionYieldPeriod.PerRepeat, 10),
                 new(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, pill)])
        ]);
        var catalog = new GameDataCatalog("test", "test", actions,
            realmProgression: new RealmProgressionCatalog([20, 0], []));
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "统计跨日", from, catalog.Fingerprint);
        player.Progression.Experience = 8;
        player.Progression.Foundation = 6;
        var action = player.Enqueue(new(actionId, -1), from, actions);
        player.SetPresence(PresenceState.Offline, from);
        var seed = player.ExportState();
        var expected = PlayerAggregate.Restore(seed);
        var balances = new[] { new AssetBalance(player.PlayerId, new(herb), 10, 0) };
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Restore(balances);
        var target = from.AddSeconds(8);
        var expectedResult = SimulationEngine.AdvanceTo(expected, target, catalog, wallet);

        using var runtime = new EcsGameRuntime(catalog, from);
        var id = runtime.CreatePlayer(seed, balances);
        if (bulk) await runtime.RunPlayerOnceAsync(id, target);
        else await runtime.RunOnceAsync(target);
        var actual = runtime.ExportSnapshot(id).State;

        Assert.Equal(JsonSerializer.Serialize(expected.ExportState()), JsonSerializer.Serialize(actual));
        Assert.Equal(EventJson(expectedResult.Events), EventJson(runtime.DrainSimulationEvents(id)));
        var report = Assert.Single(actual.Duel.ActiveReports!, entry => entry.ReportId == action.Id.ToString("N"));
        Assert.Equal(12, report.Experience);
        Assert.Equal(28, report.Foundation);
        Assert.Equal(40, report.CombatExperience);
        Assert.Equal(4, report.Cycles);
        Assert.Equal(4, Assert.Single(report.Items).Quantity);
        Assert.Equal(4, Assert.Single(report.ItemsLost!).Quantity);
        Assert.Equal(12, actual.Duel.OfflineReport!.Experience);
        var daily = actual.Duel.DailyReport!;
        Assert.Equal((long)Math.Floor((target.ToUnixTimeSeconds() + 28800d) / 86400), daily.DayIndex);
        Assert.Equal(0, daily.Experience);
        Assert.Equal(30, daily.Foundation);
        Assert.Equal(3, daily.Cycles);
        Assert.Equal(3, Assert.Single(daily.Items).Quantity);
        Assert.Equal(3, Assert.Single(daily.ItemsLost!).Quantity);
        AssertInboxReleased(runtime, id);
    }

    [Fact]
    public void GrowthCommandsDispatchStatsWithoutLosingTouchedSectionsOrCachedReportUpdates()
    {
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var catalog = GameDataCatalog.LoadEmbedded();
        var abilityId = new AbilityId("ability.test");
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "命令统计", from, catalog.Fingerprint);
        var action = player.Enqueue(new(new ActionId("cultivation.draw-qi"), -1), from, catalog.Actions);
        player.Duel.GetOrCreateReport(action.Id, action.Intent.ActionId.Value, "引气", from.ToUnixTimeSeconds());
        player.LearnAbility(abilityId);
        player.SetPresence(PresenceState.Offline, from);
        var seed = player.ExportState();
        var expected = PlayerAggregate.Restore(seed);
        expected.GrantAbilityExperience(abilityId, 100);
        expected.Breakthrough(null);

        using var runtime = new EcsGameRuntime(catalog, from);
        var id = runtime.CreatePlayer(seed);
        var firstTouched = runtime.MutatePlayer(id, aggregate => aggregate.GrantAbilityExperience(abilityId, 100));
        Assert.Contains(PlayerSectionKind.Duel, firstTouched);
        Assert.Contains(PlayerSectionKind.Loadout, firstTouched);
        // 先导出使账本缓存变干净，第二条命令必须重新标记已缓存的三本账。
        runtime.ExportSnapshot(id);
        var marks = runtime.GetDirtyMarks(id);
        var secondTouched = runtime.MutatePlayer(id, aggregate => aggregate.Breakthrough(null));
        Assert.Contains(PlayerSectionKind.Duel, secondTouched);
        Assert.Contains(PlayerSectionKind.Progression, secondTouched);
        Assert.Equal(marks[PlayerSectionKind.Duel] + 1, runtime.GetDirtyMarks(id)[PlayerSectionKind.Duel]);
        var actual = runtime.ExportSnapshot(id).State;
        Assert.Equal(JsonSerializer.Serialize(expected.ExportState()), JsonSerializer.Serialize(actual));
        Assert.All(new[] { actual.Duel.ActiveReports!.Single(), actual.Duel.OfflineReport!, actual.Duel.DailyReport! }, report =>
        {
            Assert.Equal(100, Assert.Single(report.AbilityExp!).Experience);
            Assert.Equal(new[] { "ability", "realm" }, report.LevelUps!.Select(entry => entry.Kind));
        });
        AssertInboxReleased(runtime, id);
    }

    private static void AssertInboxReleased(EcsGameRuntime runtime, Guid id)
    {
        var inbox = runtime.World.GetEntity(runtime.ExportSnapshot(id).EntityId)!.GetComponent<PlayerStatsInboxComponent>()!;
        Assert.Null(inbox.Player);
        Assert.Null(inbox.Delta.ItemGains);
        Assert.Null(inbox.Delta.ItemLosses);
    }

    private static string EventJson(IEnumerable<SimulationEvent> events)
        => JsonSerializer.Serialize(events.Select(entry => new
        {
            entry.At, entry.Code, entry.Message, entry.ActionId,
            Items = entry.ItemDeltas?.Select(pair => new { Item = pair.Key.Value, Quantity = pair.Value }).ToArray()
        }));
}
