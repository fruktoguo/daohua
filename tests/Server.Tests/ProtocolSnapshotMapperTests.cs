using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.World;
using Protocol;
using Server.Clean;
using Xunit;

namespace Server.Tests;

public sealed class ProtocolSnapshotMapperTests
{
    [Fact]
    public void SnapshotMapper_EmitsProtocolDtoWithoutDomainRuntimeObjects()
    {
        var playerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(accountId), "映射测试", DateTimeOffset.UtcNow, "fp");
        var state = aggregate.ExportState();
        var runtime = new PlayerRuntimeSnapshot(playerId, accountId.ToString("D"), aggregate.DisplayName, 1, aggregate.LastSimulatedAt, state, [], []);
        var dto = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp");
        Assert.Equal(playerId.ToString("D"), dto.PlayerId);
        Assert.Equal("映射测试", dto.DisplayName);
        Assert.Equal(7, dto.Sections.Count);
        Assert.All(dto.Sections, section => Assert.False(string.IsNullOrWhiteSpace(section.PayloadBase64)));
    }

    [Fact]
    public void ToDelta_CarriesOnlyChangedAssetsAgainstBaseline()
    {
        var baseline = new List<AssetBalanceDto> { new("spirit", null, 100, 1) };
        var current = new List<AssetBalanceDto> { new("spirit", null, 80, 2), new("item.herb", null, 5, 1) };

        var changed = ProtocolSnapshotMapper.DiffAssets(baseline, current);
        Assert.Equal(2, changed.Count);
        Assert.Contains(changed, asset => asset.AssetId == "spirit" && asset.Quantity == 80);
        Assert.Contains(changed, asset => asset.AssetId == "item.herb" && asset.Quantity == 5);

        // 余额与基线一致时不产生变更；无基线时回退为全量。
        Assert.Empty(ProtocolSnapshotMapper.DiffAssets(current, current));
        Assert.Equal(current.Count, ProtocolSnapshotMapper.DiffAssets(null, current).Count);
    }

    [Fact]
    public void DiffAssets_EmitsZeroBalanceForRemovedAsset()
    {
        var baseline = new List<AssetBalanceDto> { new("spirit", null, 100, 7) };

        var changed = ProtocolSnapshotMapper.DiffAssets(baseline, []);

        var removed = Assert.Single(changed);
        Assert.Equal("spirit", removed.AssetId);
        Assert.Equal(0, removed.Quantity);
        Assert.Equal(7, removed.Version);
    }

    [Fact]
    public void DiffSections_CarriesOnlySectionsWithModifiedChecksum()
    {
        var section1 = new PlayerSectionDto("progression", 1, 1, "json", "payload1", 10, 10, "checksum_prog_1", DateTimeOffset.UtcNow);
        var section2 = new PlayerSectionDto("world", 1, 1, "json", "payload2", 10, 10, "checksum_world_1", DateTimeOffset.UtcNow);
        var sections = new List<PlayerSectionDto> { section1, section2 };

        // Baseline has same checksum for world, different for progression
        var baseline = new Dictionary<string, string>
        {
            ["progression"] = "checksum_prog_old",
            ["world"] = "checksum_world_1"
        };

        var diff = ProtocolSnapshotMapper.DiffSections(baseline, sections);
        Assert.Single(diff);
        Assert.Equal("progression", diff[0].Section);

        // Baseline identical -> empty diff
        var identicalBaseline = new Dictionary<string, string>
        {
            ["progression"] = "checksum_prog_1",
            ["world"] = "checksum_world_1"
        };
        Assert.Empty(ProtocolSnapshotMapper.DiffSections(identicalBaseline, sections));

        // Baseline null or empty -> all sections returned
        Assert.Equal(2, ProtocolSnapshotMapper.DiffSections(null, sections).Count);
        Assert.Equal(2, ProtocolSnapshotMapper.DiffSections(new Dictionary<string, string>(), sections).Count);
    }

    [Fact]
    public void ToActionQueueSection_WithCollectMorningDew_MapsNameAndType()
    {
        var gameData = GameDataCatalog.LoadEmbedded();
        var actionId = new ActionId("explore.collect-morning-dew");
        var actions = new List<QueuedActionState>
        {
            new(Guid.NewGuid(), new ActionIntent(actionId), DateTimeOffset.UtcNow, ActionQueueStatus.Pending, 0, 1, 0)
        };

        var result = ProtocolSnapshotMapper.ToActionQueueSection(actions, gameData);
        Assert.Single(result);

        var json = JsonSerializer.Serialize(result);
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement[0];

        Assert.Equal("收集 晨露", item.GetProperty("actionName").GetString());
        Assert.Equal("exploration", item.GetProperty("actionType").GetString());
    }

    [Fact]
    public void ToActionQueueSection_GracefulFallback_WhenGameDataNullOrUnknownAction()
    {
        var actions = new List<QueuedActionState>
        {
            new(Guid.NewGuid(), new ActionIntent(new ActionId("unknown.action")), DateTimeOffset.UtcNow, ActionQueueStatus.Pending, 0, 1, 0),
            new(Guid.NewGuid(), new ActionIntent(new ActionId("unknown.travel")), DateTimeOffset.UtcNow, ActionQueueStatus.Running, 0, 1, 0,
                Travel: new TravelActionState("node.target", 10.0, [], []))
        };

        // 1. gameData 为 null 时的回退
        var resultNullData = ProtocolSnapshotMapper.ToActionQueueSection(actions, null);
        var jsonNull = JsonSerializer.Serialize(resultNullData);
        using var docNull = JsonDocument.Parse(jsonNull);

        var item0Null = docNull.RootElement[0];
        Assert.Equal(JsonValueKind.Null, item0Null.GetProperty("actionName").ValueKind);
        Assert.Equal("cultivation", item0Null.GetProperty("actionType").GetString());

        var item1Null = docNull.RootElement[1];
        Assert.Equal("赶路", item1Null.GetProperty("actionName").GetString());
        Assert.Equal("travel", item1Null.GetProperty("actionType").GetString());

        // 2. gameData 有效但行动 ID 未知时的回退
        var gameData = GameDataCatalog.LoadEmbedded();
        var resultUnknown = ProtocolSnapshotMapper.ToActionQueueSection(actions, gameData);
        var jsonUnknown = JsonSerializer.Serialize(resultUnknown);
        using var docUnknown = JsonDocument.Parse(jsonUnknown);

        var item0Unknown = docUnknown.RootElement[0];
        Assert.Equal(JsonValueKind.Null, item0Unknown.GetProperty("actionName").ValueKind);
        Assert.Equal("cultivation", item0Unknown.GetProperty("actionType").GetString());

        var item1Unknown = docUnknown.RootElement[1];
        Assert.Equal("赶路", item1Unknown.GetProperty("actionName").GetString());
        Assert.Equal("travel", item1Unknown.GetProperty("actionType").GetString());
    }

    [Fact]
    public void ToSnapshot_ActionQueueSection_IncludesActionNameAndType()
    {
        var gameData = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(accountId), "快照测试", DateTimeOffset.UtcNow, "fp");
        var state = aggregate.ExportState() with
        {
            Actions =
            [
                new QueuedActionState(Guid.NewGuid(), new ActionIntent(new ActionId("explore.collect-morning-dew")), DateTimeOffset.UtcNow, ActionQueueStatus.Pending, 0, 1, 0)
            ]
        };
        var runtime = new PlayerRuntimeSnapshot(playerId, accountId.ToString("D"), aggregate.DisplayName, 1, aggregate.LastSimulatedAt, state, [], []);
        var snapshot = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp", gameData);

        var actionSection = Assert.Single(snapshot.Sections, s => s.Section == PlayerSectionKinds.ActionQueue);
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(actionSection.PayloadBase64));
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement[0];

        Assert.Equal("收集 晨露", item.GetProperty("actionName").GetString());
        Assert.Equal("exploration", item.GetProperty("actionType").GetString());
    }
}
