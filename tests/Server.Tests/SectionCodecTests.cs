using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Server.Clean;
using System.Text.Json;
using Xunit;
using CleanSectionCodec = Domain.Persistence.SectionCodec;

namespace Server.Tests;

/// <summary>
/// 分段编解码门禁：大载荷 Brotli 压缩、checksum 防篡改、聚合分段指纹打标、
/// Settings 槽位字段序列化兼容，以及写项/刷写队列项的边界校验。
/// </summary>
public sealed class SectionCodecTests
{
    /// <summary>大载荷自动走 Brotli 压缩，经 checksum 校验往返还原。</summary>
    [Fact]
    public void LargePayload_UsesBrotliAndRoundTripsWithChecksum()
    {
        var playerId = Guid.NewGuid();
        var value = new { text = new string('道', 2000), value = 42d };
        var encoded = CleanSectionCodec.Encode(playerId, PlayerSectionKinds.Progression, 3, value, DateTimeOffset.UtcNow);
        Assert.Equal(playerId, encoded.PlayerId);
        Assert.True(encoded.RawBytes > encoded.StoredBytes);
        var stored = new StoredSection(encoded.PlayerId, encoded.SectionKind, encoded.StateVersion, encoded.SchemaVersion, encoded.Codec, encoded.Payload.ToArray(), encoded.RawBytes, encoded.Checksum, encoded.UpdatedAt);
        var restored = CleanSectionCodec.Decode<Dictionary<string, JsonElement>>(stored);
        Assert.Equal(42d, restored["value"].GetDouble());
    }
    /// <summary>载荷任一字节被篡改时解码必须抛 InvalidDataException（checksum 防静默损坏）。</summary>

    [Fact]
    public void TamperedPayload_IsRejected()
    {
        var encoded = CleanSectionCodec.Encode(Guid.NewGuid(), PlayerSectionKinds.Combat, 1, new { hitpoints = 10d }, DateTimeOffset.UtcNow);
        var payload = encoded.Payload.ToArray();
        payload[0] ^= 0x01;
        var stored = new StoredSection(encoded.PlayerId, encoded.SectionKind, encoded.StateVersion, encoded.SchemaVersion, encoded.Codec, payload, encoded.RawBytes, encoded.Checksum, encoded.UpdatedAt);
        Assert.Throws<InvalidDataException>(() => CleanSectionCodec.Decode<JsonElement>(stored));
    }
    /// <summary>聚合编码产出全部 8 个领域分段，且统一打上当前配表指纹。</summary>

    [Fact]
    public void AggregateEncoding_EmitsOnlyDomainDirtySections()
    {
        var aggregate = PlayerAggregate.Create(new PlayerId(Guid.NewGuid()), new AccountId(Guid.NewGuid()), "测试修士", DateTimeOffset.UtcNow, "fp");
        var sections = CleanSectionCodec.EncodeAggregate(aggregate, DateTimeOffset.UtcNow, 1);
        Assert.Equal(8, sections.Count);
        Assert.Contains(sections, section => section.SectionKind == PlayerSectionKinds.ActionQueue);
        Assert.All(sections, section => Assert.Equal("fp", section.GameDataFingerprint));
    }
    /// <summary>autoUseSlots 始终序列化 remainingDurationSeconds；缺该字段的旧载荷反序列化默认 0。</summary>

    [Fact]
    public void SettingsSectionAlwaysSerializesRemainingDurationAndLegacySlotsDefaultToZero()
    {
        var aggregate = PlayerAggregate.Create(new PlayerId(Guid.NewGuid()), new AccountId(Guid.NewGuid()), "槽位序列化", DateTimeOffset.UtcNow, "fp");
        aggregate.ConfigureAutoUse("cultivation", 0, "item.qi-gathering-pill", 1, true);
        var settingsWrite = CleanSectionCodec.EncodeAggregate(aggregate, DateTimeOffset.UtcNow, 1)
            .Single(write => write.SectionKind == PlayerSectionKinds.Settings);
        var stored = new StoredSection(
            settingsWrite.PlayerId,
            settingsWrite.SectionKind,
            settingsWrite.StateVersion,
            settingsWrite.SchemaVersion,
            settingsWrite.Codec,
            settingsWrite.Payload.ToArray(),
            settingsWrite.RawBytes,
            settingsWrite.Checksum,
            settingsWrite.UpdatedAt);

        var payload = CleanSectionCodec.Decode<JsonElement>(stored);
        var slot = payload.GetProperty("autoUseSlots")[0];
        Assert.True(slot.TryGetProperty("remainingDurationSeconds", out var remaining));
        Assert.Equal(0d, remaining.GetDouble());

        var legacy = JsonSerializer.Deserialize<AutoUseSlotState>(
            "{\"actionType\":\"cultivation\",\"slotIndex\":0,\"itemId\":\"item.qi-gathering-pill\",\"quantity\":1,\"enabled\":true}",
            ServerJson.Options);
        Assert.NotNull(legacy);
        Assert.Equal(0d, legacy.RemainingDurationSeconds);
    }
    /// <summary>进行中参与绑定的 +∞ 散场时刻以 null 落盘并还原为 +∞；NaN/-∞ 仍被 strict 口径拒绝。</summary>
    [Fact]
    public void ParticipationBinding_InfiniteEnd_RoundTripsAsNull()
    {
        var member = new ActionParticipantSnapshot(
            Guid.NewGuid().ToString("D"), "甲", 1, 0, 0, 100, 60,
            new CombatantStats(100, 10, 5, 2), []);
        var spec = new ActionParticipationSpec(
            Guid.NewGuid().ToString("N"), "encounter.test", "node.test", 1, 0, 0, [member])
        { ActionId = new ActionId("combat.hunt-qi-beast") };
        var playerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var openEnd = CleanSectionCodec.Encode(
            playerId, PlayerSectionKinds.ActionQueue, 1,
            new ActionParticipationBinding(spec), now, compress: false);
        Assert.Equal(CleanSectionCodec.RawJson, openEnd.Codec);
        Assert.Contains("\"endsAtUnixSeconds\":\"Infinity\"", System.Text.Encoding.UTF8.GetString(openEnd.Payload.Span));
        var storedOpen = new StoredSection(playerId, openEnd.SectionKind, openEnd.StateVersion,
            openEnd.SchemaVersion, openEnd.Codec, openEnd.Payload.ToArray(), openEnd.RawBytes, openEnd.Checksum, now);
        var restored = CleanSectionCodec.Decode<ActionParticipationBinding>(storedOpen);
        Assert.True(double.IsPositiveInfinity(restored.EndsAtUnixSeconds));

        var ended = CleanSectionCodec.Encode(
            playerId, PlayerSectionKinds.ActionQueue, 1,
            new ActionParticipationBinding(spec, 1234.5), now, compress: false);
        var storedEnded = new StoredSection(playerId, ended.SectionKind, ended.StateVersion,
            ended.SchemaVersion, ended.Codec, ended.Payload.ToArray(), ended.RawBytes, ended.Checksum, now);
        Assert.Equal(1234.5, CleanSectionCodec.Decode<ActionParticipationBinding>(storedEnded).EndsAtUnixSeconds);
    }
    /// <summary>SectionWrite 校验拒绝未知 codec 与超过 4MB 的载荷。</summary>

    [Fact]
    public void SectionWrite_RejectsUnsupportedCodecAndOversizedPayload()
    {
        var playerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new SectionWrite(
            playerId,
            PlayerSectionKinds.Progression,
            1,
            CleanSectionCodec.CurrentSchemaVersion,
            "gzip",
            new byte[] { 1 },
            1,
            new string('a', 64),
            now).Validate());
        Assert.Throws<ArgumentException>(() => new SectionWrite(
            playerId,
            PlayerSectionKinds.Progression,
            1,
            CleanSectionCodec.CurrentSchemaVersion,
            CleanSectionCodec.RawJson,
            new byte[4 * 1024 * 1024 + 1],
            4 * 1024 * 1024 + 1,
            new string('a', 64),
            now).Validate());
    }
    /// <summary>刷写队列项在应用（requireLease）时必须携带非空租约令牌。</summary>

    [Fact]
    public void FlushQueueItem_RequiresLeaseWhenApplying()
    {
        var encoded = CleanSectionCodec.Encode(Guid.NewGuid(), PlayerSectionKinds.Settings, 1, new { locale = "zh-CN" }, DateTimeOffset.UtcNow);
        var item = new FlushQueueItem(
            encoded.PlayerId,
            encoded.SectionKind,
            encoded.StateVersion,
            encoded.SchemaVersion,
            encoded.Codec,
            encoded.Payload.ToArray(),
            encoded.RawBytes,
            encoded.Checksum,
            encoded.UpdatedAt,
            0,
            encoded.UpdatedAt,
            Guid.Empty);
        Assert.Throws<ArgumentException>(() => item.Validate(requireLease: true));
        (item with { LeaseToken = Guid.NewGuid() }).Validate(requireLease: true);
    }
}
