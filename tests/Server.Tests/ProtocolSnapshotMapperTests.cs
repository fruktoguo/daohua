using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Domain.World;
using Protocol;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 快照协议映射门禁：DTO 不泄露领域运行时对象、资产/分段增量 diff 语义（含删除归零）、
/// 权威模拟时刻随帧下发，以及行动队列显示字段在配表缺失时的安全回退。
/// </summary>
public sealed class ProtocolSnapshotMapperTests
{
    /// <summary>快照 DTO 全为协议载荷：8 个分段以 Base64 下发，不含领域运行时对象。</summary>
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
        Assert.Equal(8, dto.Sections.Count);
        Assert.All(dto.Sections, section => Assert.False(string.IsNullOrWhiteSpace(section.PayloadBase64)));
    }
    /// <summary>资产 diff 只携带相对基线变化的条目；基线一致为空、无基线回退全量。</summary>

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
    /// <summary>资产从账本消失时 diff 必须显式下发 0 余额条目，否则前端会残留旧余额。</summary>

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
    /// <summary>分段 diff 按 checksum 判定变化；基线缺失或为空时回退为全量下发。</summary>

    [Fact]
    public void DiffSections_CarriesOnlySectionsWithModifiedChecksum()
    {
        var section1 = new PlayerSectionDto("progression", 1, 1, "json", "payload1", 10, 10, "checksum_prog_1", DateTimeOffset.UtcNow);
        var section2 = new PlayerSectionDto("world", 1, 1, "json", "payload2", 10, 10, "checksum_world_1", DateTimeOffset.UtcNow);
        var sections = new List<PlayerSectionDto> { section1, section2 };

        var baseline = new Dictionary<string, string>
        {
            ["progression"] = "checksum_prog_old",
            ["world"] = "checksum_world_1"
        };

        var diff = ProtocolSnapshotMapper.DiffSections(baseline, sections);
        Assert.Single(diff);
        Assert.Equal("progression", diff[0].Section);

        var identicalBaseline = new Dictionary<string, string>
        {
            ["progression"] = "checksum_prog_1",
            ["world"] = "checksum_world_1"
        };
        Assert.Empty(ProtocolSnapshotMapper.DiffSections(identicalBaseline, sections));

        Assert.Equal(2, ProtocolSnapshotMapper.DiffSections(null, sections).Count);
        Assert.Equal(2, ProtocolSnapshotMapper.DiffSections(new Dictionary<string, string>(), sections).Count);
    }
    /// <summary>配表缺失或行动 Id 未知时队列显示字段安全回退：actionName 置空、actionType 兜底，不得抛错。</summary>

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
    /// <summary>行动队列分段载荷必须携带 actionName/actionType，供前端不经配表直接展示。</summary>

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

public sealed class ProtocolAttributesPointsTests
{
    /// <summary>
    /// 属性 DTO 门禁：百分比属性一律按原始点数下发（100 点 = 100%），
    /// 暴击/免暴/免伤等不得在协议边界预折为比率；曲线解算只发生在结算与前端展示层。
    /// </summary>
    [Fact]
    public void ToAttributesDto_EmitsRawPointsWithoutRatioConversion()
    {
        var attributes = PlayerAttributeResolver.Resolve(
            PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "点数", DateTimeOffset.UnixEpoch, "fp"),
            GameDataCatalog.LoadEmbedded());
        var dto = ProtocolSnapshotMapper.ToAttributesDto(attributes);
        // 线上序列化走 ProtocolJson.Options（Web 默认 camelCase），测试必须同口径才能看到真实协议键名。
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, ProtocolJson.Options));
        var root = doc.RootElement;
        // 点数原样透传：领域值是什么，线上就是什么。
        Assert.Equal(attributes.Critical, root.GetProperty("critical").GetDouble());
        Assert.Equal(attributes.CriticalAvoidance, root.GetProperty("criticalResistance").GetDouble());
        Assert.Equal(attributes.DamageAbsorptionPoints, root.GetProperty("damageAbsorptionPoints").GetDouble());
        Assert.Equal(attributes.AttackSpeedPoints, root.GetProperty("attackSpeedPoints").GetDouble());
        Assert.Equal(attributes.CriticalMultiplier, root.GetProperty("criticalMultiplier").GetDouble());
        // 增减伤 16 项全字段在场（点数）。
        foreach (var key in new[]
        {
            "physicalDamageBonusPoints", "spellDamageBonusPoints",
            "metalDamageBonusPoints", "woodDamageBonusPoints", "waterDamageBonusPoints",
            "fireDamageBonusPoints", "earthDamageBonusPoints",
            "totalDamageBonusPoints", "basicAttackBonusPoints", "skillBonusPoints",
            "physicalDamageReductionPoints", "spellDamageReductionPoints",
            "metalDamageReductionPoints", "woodDamageReductionPoints", "waterDamageReductionPoints",
            "fireDamageReductionPoints", "earthDamageReductionPoints",
        })
            Assert.True(root.TryGetProperty(key, out _), $"缺少协议字段 {key}");
    }
}
