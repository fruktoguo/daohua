using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.World;
using Protocol;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>包含针对 Protocol Snapshot Mapper Tests 的测试用例与断言集合。</summary>
public sealed class ProtocolSnapshotMapperTests
{
    /// <summary>验证 Snapshot Mapper Emits Protocol Dto Without Domain Runtime Objects。</summary>
    [Fact]
    public void SnapshotMapper_EmitsProtocolDtoWithoutDomainRuntimeObjects()
    {
        var playerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(accountId), "映射测试", DateTimeOffset.UtcNow, "fp");
        var state = aggregate.ExportState();
        var runtime = new PlayerRuntimeSnapshot(playerId, accountId.ToString("D"), aggregate.DisplayName, 1, aggregate.LastSimulatedAt, state, [], []);
        var dto = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp", GameDataCatalog.BuiltIn);
        Assert.Equal(playerId.ToString("D"), dto.PlayerId);
        Assert.Equal("映射测试", dto.DisplayName);
        Assert.Equal(7, dto.Sections.Count);
        Assert.All(dto.Sections, section => Assert.False(string.IsNullOrWhiteSpace(section.PayloadBase64)));
    }
    /// <summary>验证 To Delta Carries Only Changed Assets Against Baseline。</summary>

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

    /// <summary>
    /// 增量快照必须随帧下发服务端权威模拟时刻，并能在信封编解码往返后保持毫秒精度。
    /// 浏览器以「服务端时刻 + 本地单调流逝」推算行动倒计时与进度条；该字段缺失会让时钟锚点
    /// 停留在最后一次全量快照，剩余时间被高估且偏移随页面停留时长线性累积，永不回正。
    /// </summary>
    [Fact]
    public void Delta_CarriesAuthoritativeSimulatedAtThroughWireRoundtrip()
    {
        var playerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(accountId), "增量映射测试", DateTimeOffset.UtcNow, "fp");
        var simulatedAt = aggregate.LastSimulatedAt;
        var runtime = new PlayerRuntimeSnapshot(playerId, accountId.ToString("D"), aggregate.DisplayName, 2, simulatedAt, aggregate.ExportState(), [], []);

        var delta = ProtocolSnapshotMapper.ToDelta(runtime, "fp", GameDataCatalog.BuiltIn);
        Assert.Equal(simulatedAt, delta.LastSimulatedAt);

        var frame = WireEnvelopeCodec.EncodeJson(WireMessageType.SnapshotDelta, Guid.NewGuid(), 1, delta);
        var envelope = WireEnvelopeCodec.Decode(frame);
        Assert.True(WireEnvelopeCodec.TryDeserializeJson<PlayerSnapshotDeltaDto>(envelope, out var decoded));
        Assert.Equal(simulatedAt.ToUniversalTime().ToUnixTimeMilliseconds(), decoded!.LastSimulatedAt.ToUnixTimeMilliseconds());
        Assert.Contains("lastSimulatedAt", System.Text.Encoding.UTF8.GetString(envelope.Payload.Span));
    }
    /// <summary>验证 Diff Assets Emits Zero Balance For Removed Asset。</summary>

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
    /// <summary>验证 Diff Sections Carries Only Sections With Modified Checksum。</summary>

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
    /// <summary>验证 To Action Queue Section Graceful Fallback When Game Data Null Or Unknown Action。</summary>

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
    /// <summary>验证 To Snapshot Action Queue Section Includes Action Name And Type。</summary>

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
