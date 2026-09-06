using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Server.Clean;
using System.Text.Json;
using Xunit;
using CleanSectionCodec = Server.Clean.SectionCodec;

namespace Server.Tests;

public sealed class SectionCodecTests
{
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

    [Fact]
    public void TamperedPayload_IsRejected()
    {
        var encoded = CleanSectionCodec.Encode(Guid.NewGuid(), PlayerSectionKinds.Combat, 1, new { hitpoints = 10d }, DateTimeOffset.UtcNow);
        var payload = encoded.Payload.ToArray();
        payload[0] ^= 0x01;
        var stored = new StoredSection(encoded.PlayerId, encoded.SectionKind, encoded.StateVersion, encoded.SchemaVersion, encoded.Codec, payload, encoded.RawBytes, encoded.Checksum, encoded.UpdatedAt);
        Assert.Throws<InvalidDataException>(() => CleanSectionCodec.Decode<JsonElement>(stored));
    }

    [Fact]
    public void AggregateEncoding_EmitsOnlyDomainDirtySections()
    {
        var aggregate = PlayerAggregate.Create(new PlayerId(Guid.NewGuid()), new AccountId(Guid.NewGuid()), "测试修士", DateTimeOffset.UtcNow, "fp");
        var sections = CleanSectionCodec.EncodeAggregate(aggregate, DateTimeOffset.UtcNow, 1);
        Assert.Equal(7, sections.Count);
        Assert.Contains(sections, section => section.SectionKind == PlayerSectionKinds.ActionQueue);
        Assert.All(sections, section => Assert.Equal("fp", section.GameDataFingerprint));
    }

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
