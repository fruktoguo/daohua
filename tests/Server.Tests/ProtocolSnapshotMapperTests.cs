using System.Security.Cryptography;
using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
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
    /// <summary>快照 DTO 全为协议载荷：8 个分段以内嵌规范化 JSON 下发，不含领域运行时对象。</summary>
    [Fact]
    public void SnapshotMapper_EmitsProtocolDtoWithoutDomainRuntimeObjects()
    {
        var playerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(accountId), "映射测试", DateTimeOffset.UtcNow, "fp");
        var state = aggregate.ExportState();
        var runtime = new PlayerRuntimeSnapshot(playerId, accountId.ToString("D"), aggregate.DisplayName, 1, aggregate.LastSimulatedAt, state, 0, []);
        var dto = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp", GameDataCatalog.BuiltIn);
        Assert.Equal(playerId.ToString("D"), dto.PlayerId);
        Assert.Equal("映射测试", dto.DisplayName);
        Assert.Equal(8, dto.Sections.Count);
        Assert.All(dto.Sections, section => Assert.True(
            section.Data.ValueKind is JsonValueKind.Object or JsonValueKind.Array,
            $"section {section.Section} data 必须是内嵌 JSON 值。"));
    }

    /// <summary>
    /// checksum 口径门禁：信封 JSON 中 data 成员的线上原始字节即哈希输入——
    /// JsonElement.WriteTo 逐位透传规范化编码字节，客户端可对同区间字节直接复算 SHA-256，
    /// 无需 Base64 往返且不存在序列化口径分叉。
    /// </summary>
    [Fact]
    public void Snapshot_SectionDataWireBytes_ReproduceSectionChecksum()
    {
        var playerId = Guid.NewGuid();
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(Guid.NewGuid()), "校验", DateTimeOffset.UtcNow, "fp");
        var runtime = new PlayerRuntimeSnapshot(playerId, aggregate.AccountId.Value.ToString("D"), aggregate.DisplayName, 1, aggregate.LastSimulatedAt, aggregate.ExportState(), 0, []);
        var dto = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp", GameDataCatalog.BuiltIn);

        var frame = WireEnvelopeCodec.EncodeJson(WireMessageType.Snapshot, Guid.NewGuid(), 1, dto);
        var envelope = WireEnvelopeCodec.Decode(frame);
        using var document = JsonDocument.Parse(envelope.Payload);
        var wireSections = document.RootElement.GetProperty("sections");
        Assert.Equal(dto.Sections.Count, wireSections.GetArrayLength());
        for (var i = 0; i < dto.Sections.Count; i++)
        {
            var wireDataBytes = System.Text.Encoding.UTF8.GetBytes(wireSections[i].GetProperty("data").GetRawText());
            Assert.Equal(dto.Sections[i].Checksum, Convert.ToHexStringLower(SHA256.HashData(wireDataBytes)));
        }
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
        var runtime = new PlayerRuntimeSnapshot(playerId, accountId.ToString("D"), aggregate.DisplayName, 2, simulatedAt, aggregate.ExportState(), 0, []);

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
        var section1 = new PlayerSectionDto("progression", 1, 1, JsonDocument.Parse("{}").RootElement, "checksum_prog_1", DateTimeOffset.UtcNow);
        var section2 = new PlayerSectionDto("world", 1, 1, JsonDocument.Parse("{}").RootElement, "checksum_world_1", DateTimeOffset.UtcNow);
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
        var runtime = new PlayerRuntimeSnapshot(playerId, accountId.ToString("D"), aggregate.DisplayName, 1, aggregate.LastSimulatedAt, state, 0, []);
        var snapshot = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp", gameData);

        var actionSection = Assert.Single(snapshot.Sections, s => s.Section == PlayerSectionKinds.ActionQueue);
        var item = actionSection.Data[0];

        Assert.Equal("收集 晨露", item.GetProperty("actionName").GetString());
        Assert.Equal("exploration", item.GetProperty("actionType").GetString());
    }

    /// <summary>行动队列下发的 durationSeconds 必须反映技艺、工具与建筑加速后的实际单轮耗时，不得退化为配表原耗时。</summary>
    [Fact]
    public void ToSnapshot_ActionQueueSection_ProjectsAcceleratedDurationSeconds()
    {
        var gameData = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(accountId), "加速测试", DateTimeOffset.UtcNow, "fp");
        var actionId = "explore.gather-ningxue-grass";
        var state = aggregate.ExportState() with
        {
            Actions =
            [
                new QueuedActionState(Guid.NewGuid(), new ActionIntent(new ActionId(actionId)), DateTimeOffset.UtcNow, ActionQueueStatus.Running, 0, -1, 0)
            ],
            Loadout = aggregate.ExportState().Loadout with
            {
                TechniqueExperiences = new Dictionary<string, double> { ["gathering"] = 22000 },
                Equipment = [new PlayerEquipmentSlot("equipment.tool-herb-hoe", 0)]
            }
        };
        var runtime = new PlayerRuntimeSnapshot(playerId, accountId.ToString("D"), aggregate.DisplayName, 1, aggregate.LastSimulatedAt, state, 0, []);
        var snapshot = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp", gameData);

        var actionSection = Assert.Single(snapshot.Sections, s => s.Section == PlayerSectionKinds.ActionQueue);
        var item = actionSection.Data[0];

        var expectedDuration = 8.0 / 1.20;
        var actualDuration = item.GetProperty("durationSeconds").GetDouble();
        Assert.Equal(expectedDuration, actualDuration, 5);
        Assert.True(actualDuration < 8.0, "下发的耗时应为加速后的耗时，而非原配表基础耗时。");
    }

    /// <summary>
    /// 世界节点生态生灵互斥投影门禁：普通/精英/首领怪严格互斥，不得跨池重复展示；
    /// 精英/Boss 刷新间隔缺省安全回退（默认 5 和 20 场），绝不下发 0 场出没。
    /// </summary>
    [Fact]
    public void ToWorldSection_MonstersAreMutuallyExclusive_AndIntervalsDoNotZeroOut()
    {
        var gameData = GameDataCatalog.LoadEmbedded();
        var aggregate = PlayerAggregate.Create(new PlayerId(Guid.NewGuid()), new AccountId(Guid.NewGuid()),
            "生态测试", DateTimeOffset.UtcNow, gameData.Fingerprint);

        // 设置玩家驻留在云巅草庐（node.cloud-peak，承载包含普通、精英、首领的绝顶生态）
        aggregate.World.NodeId = "node.cloud-peak";
        aggregate.World.EdgeId = null;

        var runtime = new PlayerRuntimeSnapshot(aggregate.PlayerId.Value, aggregate.AccountId.Value.ToString("D"),
            aggregate.DisplayName, 1, aggregate.LastSimulatedAt, aggregate.ExportState(), 0, []);
        var snapshot = ProtocolSnapshotMapper.ToSnapshot(runtime, gameData.Fingerprint, gameData);

        var worldSection = Assert.Single(snapshot.Sections, s => s.Section == PlayerSectionKinds.World);
        var data = worldSection.Data;

        var eliteInterval = data.GetProperty("eliteInterval").GetDouble();
        var bossInterval = data.GetProperty("bossInterval").GetDouble();
        Assert.True(eliteInterval > 0, "精英出没周期必须大于 0，不可下发 0 场");
        Assert.True(bossInterval > 0, "首领出没周期必须大于 0，不可下发 0 场");
        Assert.Equal(5.0, eliteInterval);
        Assert.Equal(20.0, bossInterval);

        var normalMonsters = data.GetProperty("monsters").EnumerateArray()
            .Select(m => m.GetProperty("monsterId").GetString()!).ToList();
        var eliteEnemies = data.GetProperty("eliteEnemies").EnumerateArray()
            .Select(m => m.GetProperty("monsterId").GetString()!).ToList();
        var bossEnemies = data.GetProperty("bossEnemies").EnumerateArray()
            .Select(m => m.GetProperty("monsterId").GetString()!).ToList();

        // 验证三类生灵严格互斥，青云山门包含火灵（普通）、魔猿（精英）、伥虎（首领）
        Assert.Contains("enemy.fire-spirit", normalMonsters);
        Assert.Contains("enemy.demon-ape", eliteEnemies);
        Assert.Contains("enemy.chang-tiger", bossEnemies);

        // 魔猿不得同时出现在普通怪列表中
        Assert.DoesNotContain("enemy.demon-ape", normalMonsters);
        // 普通怪中不可包含任何精英或首领
        Assert.Empty(normalMonsters.Intersect(eliteEnemies));
        Assert.Empty(normalMonsters.Intersect(bossEnemies));
        Assert.Empty(eliteEnemies.Intersect(bossEnemies));
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
        Assert.Equal(attributes.CriticalDamagePoints, root.GetProperty("criticalDamagePoints").GetDouble());
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

    /// <summary>
    /// 按需属性一次性交付门禁：观察目标命中下标的敌怪才投影 attributes 并触发出站记账回调
    /// （供会话解除交付武装）；未登记观察目标时同一快照不携带属性、不触发回调，
    /// 血蓝分母等实时标量则不受观察态影响常驻下发。
    /// </summary>
    [Fact]
    public void ToSnapshot_InspectedEnemy_ProjectsAttributesOnceWithEmissionMark()
    {
        var gameData = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.NewGuid();
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(Guid.NewGuid()), "观察", DateTimeOffset.UtcNow, "fp");
        var action = new QueuedActionState(
            Guid.NewGuid(),
            new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1),
            DateTimeOffset.UtcNow, ActionQueueStatus.Running, 0, -1, 0,
            new CombatExpeditionState(
                "encounter.qi-beast", CombatExpeditionPhase.Combat, 0, 0, 1,
                15.47, 113, 60, 16, 15, 0, CombatOutcome.Running, 1,
                [new ExpeditionEnemyState("enemy.wood-spirit",
                    new CombatantSnapshot(362, 4.75, 15.99, 7, 0, 1))])
            { PlayerState = new CombatantSnapshot(113, 60, 16, 15, 0) });
        var restored = PlayerAggregate.Restore(aggregate.ExportState() with { Actions = [action] });
        var runtime = new PlayerRuntimeSnapshot(playerId, restored.AccountId.Value.ToString("D"),
            restored.DisplayName, 1, restored.LastSimulatedAt, restored.ExportState(), 0, []);

        var projected = 0;
        var inspected = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp", gameData, new UnitInspectDto("enemy", 0), () => projected++);
        Assert.Equal(1, projected);
        var inspectedEnemies = inspected.Sections.Single(section => section.Section == PlayerSectionKinds.ActionQueue)
            .Data[0].GetProperty("expedition").GetProperty("enemies");
        Assert.True(inspectedEnemies[0].TryGetProperty("attributes", out var inspectedAttributes));
        Assert.Equal(JsonValueKind.Object, inspectedAttributes.ValueKind);

        var uninspected = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp", gameData);
        Assert.Equal(1, projected);
        var plainEnemies = uninspected.Sections.Single(section => section.Section == PlayerSectionKinds.ActionQueue)
            .Data[0].GetProperty("expedition").GetProperty("enemies");
        // 序列化口径为 WhenWritingNull：未观察目标的 attributes 字段整体缺席，不落 null 占位。
        Assert.False(plainEnemies[0].TryGetProperty("attributes", out _));
        Assert.Equal(JsonValueKind.Number, plainEnemies[0].GetProperty("maxHitpoints").ValueKind);
        Assert.Equal(JsonValueKind.Number, plainEnemies[0].GetProperty("maxManapoints").ValueKind);
    }

    /// <summary>
    /// 盟友属性按需交付门禁：观察目标 side=ally 命中 memberIndex 的成员才投影开打冻结面板
    /// attributes 并触发出站记账回调；未命中成员与本机位不携带属性、不触发回调。
    /// </summary>
    [Fact]
    public void ToSnapshot_InspectedAlly_ProjectsFrozenAttributesOnceWithEmissionMark()
    {
        var gameData = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.NewGuid();
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(Guid.NewGuid()), "本机", DateTimeOffset.UtcNow, "fp");
        var selfStats = CombatantStats.Create(113, 10, 5, 2) with { MaxManapoints = 60 };
        var allyStats = CombatantStats.Create(200, 20, 8, 2) with { MaxManapoints = 80 };
        var spec = new ActionParticipationSpec(
            Guid.NewGuid().ToString("D"), "encounter.qi-beast", "node.qi-den", 1, 0, 0,
            [
                new ActionParticipantSnapshot(playerId.ToString("D"), "本机", 1, 0, 0, 113, 60, selfStats, []),
                new ActionParticipantSnapshot(Guid.NewGuid().ToString("D"), "队友", 1, 0, 0, 200, 80, allyStats, []),
            ])
        { ActionId = new ActionId("combat.hunt-qi-beast") };
        var action = new QueuedActionState(
            Guid.NewGuid(),
            new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1, "node.qi-den"),
            DateTimeOffset.UtcNow, ActionQueueStatus.Running, 0, -1, 0,
            new CombatExpeditionState(
                "encounter.qi-beast", CombatExpeditionPhase.Combat, 0, 0, 1,
                15.47, 113, 60, 16, 15, 0, CombatOutcome.Running, 1,
                [new ExpeditionEnemyState("enemy.wood-spirit",
                    new CombatantSnapshot(362, 4.75, 15.99, 7, 0, 1))])
            {
                PlayerState = new CombatantSnapshot(113, 60, 16, 15, 0),
                Allies = [new ExpeditionAllyState(1, new CombatantSnapshot(200, 80, 5, 2, 0))],
            },
            Participation: new ActionParticipationBinding(spec));
        var restored = PlayerAggregate.Restore(aggregate.ExportState() with { Actions = [action] });
        var runtime = new PlayerRuntimeSnapshot(playerId, restored.AccountId.Value.ToString("D"),
            restored.DisplayName, 1, restored.LastSimulatedAt, restored.ExportState(), 0, []);

        var projected = 0;
        var inspected = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp", gameData, new UnitInspectDto("ally", 1), () => projected++);
        Assert.Equal(1, projected);
        var allies = inspected.Sections.Single(section => section.Section == PlayerSectionKinds.ActionQueue)
            .Data[0].GetProperty("expedition").GetProperty("allies");
        // 本机位（memberIndex=0）与未命中成员不携带属性：WhenWritingNull 口径下字段整体缺席。
        Assert.False(allies[0].TryGetProperty("attributes", out _));
        Assert.True(allies[1].TryGetProperty("attributes", out var inspectedAttributes));
        Assert.Equal(JsonValueKind.Object, inspectedAttributes.ValueKind);
        Assert.Equal(200, inspectedAttributes.GetProperty("maxHitpoints").GetDouble());
        Assert.Equal(20, inspectedAttributes.GetProperty("physicalAttack").GetDouble());
    }

    /// <summary>
    /// 有效威胁投影门禁（总纲 §7.8.1）：玩家/盟友/敌人的 threat 字段统一下发
    /// 基础存储值×(1+增幅点数/100) 的有效值——本机面板增幅、冻结面板增幅、
    /// 镜像修正与战内 Buff 点数全部参与放大，不再仅透传基础累积值。
    /// </summary>
    [Fact]
    public void ToActionQueueSection_ProjectsEffectiveThreat()
    {
        var gameData = GameDataCatalog.LoadEmbedded();
        var selfStats = CombatantStats.Create(113, 10, 5, 2) with { MaxManapoints = 60, ThreatBonusPoints = 50 };
        var allyStats = CombatantStats.Create(200, 20, 8, 2) with { MaxManapoints = 80, ThreatBonusPoints = 100 };
        var spec = new ActionParticipationSpec(
            Guid.NewGuid().ToString("D"), "encounter.qi-beast", "node.qi-den", 1, 0, 0,
            [
                new ActionParticipantSnapshot(Guid.NewGuid().ToString("D"), "本机", 1, 0, 0, 113, 60, selfStats, []),
                new ActionParticipantSnapshot(Guid.NewGuid().ToString("D"), "队友", 1, 0, 0, 200, 80, allyStats, []),
            ])
        { ActionId = new ActionId("combat.hunt-qi-beast") };
        var action = new QueuedActionState(
            Guid.NewGuid(),
            new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1, "node.qi-den"),
            DateTimeOffset.UtcNow, ActionQueueStatus.Running, 0, -1, 0,
            new CombatExpeditionState(
                "encounter.qi-beast", CombatExpeditionPhase.Combat, 0, 0, 1,
                15.47, 113, 60, 16, 15, 100, CombatOutcome.Running, 1,
                [new ExpeditionEnemyState("enemy.wood-spirit",
                    new CombatantSnapshot(362, 4.75, 15.99, 7, 100, 1, ThreatBonusPoints: 200))])
            {
                PlayerState = new CombatantSnapshot(113, 60, 16, 15, 100,
                    Buffs: [new CombatBuffState("ability-buff:ability.taunt", 60, 1,
                        ThreatPoints: 200, ThreatPerSecondMaxHpPoints: 5, Name: "嘲讽")]),
                Allies = [new ExpeditionAllyState(1, new CombatantSnapshot(200, 80, 5, 2, 200))],
            },
            Participation: new ActionParticipationBinding(spec));

        var section = ProtocolSnapshotMapper.ToActionQueueSection([action], gameData, null, null, 300);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(section, ProtocolJson.Options));
        var expedition = doc.RootElement[0].GetProperty("expedition");
        // 本机标量：基础 100 ×(1+(面板 300+战内 Buff 200)/100)=600；盟友位走冻结面板与镜像。
        Assert.Equal(600, expedition.GetProperty("playerThreat").GetDouble());
        var allies = expedition.GetProperty("allies");
        // 本机盟友位同源 PlayerState 镜像：基础 100 ×(1+(冻结面板 50+Buff 200)/100)=350。
        Assert.Equal(350, allies[0].GetProperty("threat").GetDouble());
        Assert.Equal(400, allies[1].GetProperty("threat").GetDouble());
        Assert.Equal(300, expedition.GetProperty("enemies")[0].GetProperty("threat").GetDouble());
        // 战内 Buff 投影：本机位标量与盟友位数组同源携带（名称/层数/战斗时钟到期/效果字段）。
        var playerBuff = expedition.GetProperty("playerBuffs")[0];
        Assert.Equal("ability-buff:ability.taunt", playerBuff.GetProperty("buffId").GetString());
        Assert.Equal("嘲讽", playerBuff.GetProperty("name").GetString());
        Assert.Equal(60, playerBuff.GetProperty("expiresAtSeconds").GetDouble());
        Assert.Equal(200, playerBuff.GetProperty("threatPoints").GetDouble());
        Assert.Equal(5, playerBuff.GetProperty("threatPerSecondMaxHpPoints").GetDouble());
        Assert.Equal("嘲讽", allies[0].GetProperty("buffs")[0].GetProperty("name").GetString());
        Assert.False(expedition.GetProperty("enemies")[0].TryGetProperty("buffs", out _));
    }

    /// <summary>
    /// 跨波携带 Buff 门禁（总纲 §7.9.5）：非战斗相位 playerBuffs/盟友 buffs 改投携带态
    /// （expiresAt = 冻结时钟基准 + 剩余秒数）；详情属性面板的威胁增幅折入 Buff 点数，
    /// 与 §7.8.1 有效威胁判定同乘区。
    /// </summary>
    [Fact]
    public void CarriedCombatBuffsProjectDuringSearchAndFoldIntoThreatBonus()
    {
        var gameData = GameDataCatalog.LoadEmbedded();
        var aggregate = PlayerAggregate.Create(new PlayerId(Guid.NewGuid()), new AccountId(Guid.NewGuid()),
            "携带", DateTimeOffset.UtcNow, "fp");
        aggregate.Combat.Buffs = new PlayerCombatBuffState(
            [new CombatBuffCarry("ability-buff:ability.taunt", 40, 2, ThreatPoints: 400, Name: "嘲讽")]);
        var state = aggregate.ExportState();

        // 搜寻相位远征：携带态经 playerCarriedBuffs 入参下发，expiresAt = battleElapsed + 剩余。
        var action = new QueuedActionState(
            Guid.NewGuid(),
            new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1, "node.qi-den"),
            DateTimeOffset.UtcNow, ActionQueueStatus.Running, 0, -1, 0,
            new CombatExpeditionState(
                "encounter.qi-beast", CombatExpeditionPhase.Search, 3, 8, 1, 12.5,
                113, 60, 0, 0, 100, CombatOutcome.Running, 2));
        var section = ProtocolSnapshotMapper.ToActionQueueSection([action], gameData, null, null, 0,
            state.Combat.Buffs);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(section, ProtocolJson.Options));
        var expedition = doc.RootElement[0].GetProperty("expedition");
        var carried = Assert.Single(expedition.GetProperty("playerBuffs").EnumerateArray());
        Assert.Equal("ability-buff:ability.taunt", carried.GetProperty("buffId").GetString());
        Assert.Equal("嘲讽", carried.GetProperty("name").GetString());
        Assert.Equal(2, carried.GetProperty("stacks").GetDouble());
        // 12.5 + 40 = 52.5：前端 serverNow + (expiresAt - battleElapsed) 得剩余 40s。
        Assert.Equal(52.5, carried.GetProperty("expiresAtSeconds").GetDouble(), 6);
        Assert.Equal(400, carried.GetProperty("threatPoints").GetDouble());

        // 详情属性面板：威胁增幅 = 面板 0 + 携带 Buff 合计 400×2。
        var runtime = new PlayerRuntimeSnapshot(state.PlayerId.Value, state.AccountId.Value.ToString("D"),
            state.DisplayName, 1, state.LastSimulatedAt, state, 0, []);
        var snapshot = ProtocolSnapshotMapper.ToSnapshot(runtime, "fp", gameData);
        var combat = snapshot.Sections.Single(section => section.Section == "combat");
        Assert.Equal(800, combat.Data.GetProperty("attributes").GetProperty("threatBonusPoints").GetDouble());
    }

    /// <summary>
    /// 静态遭遇目录投影门禁：必须完整序列化 eliteEnemies 与 battlesPerElite，
    /// 且遭遇全量投影去重后覆盖游戏内全部 12 种大世界妖兽（含 4 只精英与 2 只首领）。
    /// </summary>
    [Fact]
    public void StaticGameCatalogMapper_Encounters_ProjectsEliteEnemiesAndCoversAllMonsters()
    {
        var gameData = GameDataCatalog.LoadEmbedded();
        var encounters = StaticGameCatalogMapper.Encounters(gameData);
        Assert.NotEmpty(encounters);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(encounters, ProtocolJson.Options));
        var root = doc.RootElement;
        var allFoundIds = new HashSet<string>(StringComparer.Ordinal);
        var eliteCount = 0;

        foreach (var enc in root.EnumerateArray())
        {
            Assert.True(enc.TryGetProperty("encounterId", out var encId));
            Assert.True(enc.TryGetProperty("eliteEnemies", out var eliteArray));
            Assert.True(enc.TryGetProperty("battlesPerElite", out _));
            Assert.True(enc.TryGetProperty("bossEnemies", out var bossArray));
            Assert.True(enc.TryGetProperty("randomSpawns", out var spawnsArray));

            foreach (var spawn in spawnsArray.EnumerateArray())
            {
                var enemy = spawn.GetProperty("enemy");
                allFoundIds.Add(enemy.GetProperty("enemyId").GetString()!);
            }
            foreach (var elite in eliteArray.EnumerateArray())
            {
                eliteCount++;
                var id = elite.GetProperty("enemyId").GetString()!;
                allFoundIds.Add(id);
                Assert.Equal("elite", elite.GetProperty("rank").GetString());
            }
            foreach (var boss in bossArray.EnumerateArray())
            {
                var id = boss.GetProperty("enemyId").GetString()!;
                allFoundIds.Add(id);
                Assert.Equal("boss", boss.GetProperty("rank").GetString());
            }
        }

        Assert.True(eliteCount > 0, "遭遇中必须包含精英怪投影。");
        string[] expectedElites = ["enemy.armored-bear", "enemy.demon-ape", "enemy.blade-corpse", "enemy.fallen-devil-priest"];
        string[] expectedBosses = ["enemy.chang-tiger", "enemy.falling-star-beast", "enemy.xuanyuan-remnant"];
        string[] expectedMinions = ["enemy.wood-spirit", "enemy.water-spirit", "enemy.demonized-rabbit", "enemy.fire-spirit", "enemy.rift-wolf", "enemy.star-flame-crow", "enemy.plunder-cultivator"];

        foreach (var id in expectedElites) Assert.Contains(id, allFoundIds);
        foreach (var id in expectedBosses) Assert.Contains(id, allFoundIds);
        foreach (var id in expectedMinions) Assert.Contains(id, allFoundIds);
        Assert.Equal(14, allFoundIds.Count);
    }

    /// <summary>
    /// 怪物技能槽触发条件投影门禁：下发内容必须是领域层已解析的槽位条件
    /// （槽位 triggers → triggerHpRatio 折叠 → 功法 defaultTriggers），
    /// 而不是功法默认条件——前端离线推演按此条件装配怪物技能，
    /// 若下发 defaultTriggers 则甘露等条件技在推演中永不施放（水灵不爆技能）。
    /// </summary>
    [Fact]
    public void StaticGameCatalogMapper_MonsterSkills_ProjectResolvedSlotTriggers()
    {
        var gameData = GameDataCatalog.LoadEmbedded();
        var encounters = StaticGameCatalogMapper.Encounters(gameData);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(encounters, ProtocolJson.Options));
        var waterSpiritSkills = default(JsonElement);
        var found = false;
        foreach (var enc in doc.RootElement.EnumerateArray())
        {
            foreach (var spawn in enc.GetProperty("randomSpawns").EnumerateArray())
            {
                var enemy = spawn.GetProperty("enemy");
                if (enemy.GetProperty("enemyId").GetString() != "enemy.water-spirit") continue;
                waterSpiritSkills = enemy.GetProperty("skills");
                found = true;
            }
        }

        Assert.True(found, "遭遇目录必须包含水灵投影。");
        var skill = Assert.Single(waterSpiritSkills.EnumerateArray());
        Assert.Equal("ability.ganlu", skill.GetProperty("abilityId").GetString());
        Assert.True(skill.TryGetProperty("triggers", out var triggers), "怪物技能槽必须下发解析后的触发条件。");

        // 水灵槽位配 triggerHpRatio=1.0，折叠为 self 气血百分比 ≤ 100（恒真）；下发的必须是它，
        // 而不是甘露配表的 defaultTriggers（友方最低血量缺失 ≥ 1，怪物视角下恒假）。
        var trigger = Assert.Single(triggers.EnumerateArray());
        Assert.Equal("self", trigger.GetProperty("dependency").GetString());
        Assert.Equal("current-hp-percentage", trigger.GetProperty("condition").GetString());
        Assert.Equal("less-than-equal", trigger.GetProperty("comparator").GetString());
        Assert.Equal(100, trigger.GetProperty("value").GetDouble(), 6);

        // 甘露配表默认条件必须与下发内容不同——否则本次修复形同虚设。
        var defaultTriggers = gameData.Abilities["ability.ganlu"].GetProperty("defaultTriggers");
        Assert.Equal("ally-lowest-hp", defaultTriggers[0].GetProperty("dependency").GetString());
    }
}
