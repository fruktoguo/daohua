// ==============================================================================
// 文件名称: StateSerializerEnvelopeTests.cs
// 主要职责: 验证 SnapshotEnvelope 序列化、Schema 版本校验、游戏数据指纹校验与旧快照拒绝行为。
// ==============================================================================

using System.Text;
using System.Text.Json;
using Domain.GameData;
using Domain.Models;
using Server.Infrastructure;
using Xunit;

namespace Server.Tests;

public sealed class StateSerializerEnvelopeTests
{
    [Fact]
    public void SerializeAndDeserialize_Roundtrip_PreservesEnvelopeAndState()
    {
        var state = new PlayerState
        {
            PlayerId = Guid.NewGuid(),
            Name = "太初道人",
            Account = "taichu",
            SequenceId = 12345,
            Version = 42,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-10),
            LastSimulatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            PresenceMode = PresenceMode.IdleOffline
        };
        state.Inventory.Items["herb_spirit_grass"] = 99;

        var bytes = StateSerializer.Serialize(state);
        var envelope = StateSerializer.DeserializeEnvelope(bytes);
        var restored = StateSerializer.Deserialize(bytes);

        Assert.Equal(SnapshotEnvelope.CurrentSchemaVersion, envelope.SnapshotSchemaVersion);
        Assert.Equal(GameDataCatalog.Current.Fingerprint, envelope.GameDataFingerprint);
        Assert.Equal(42L, envelope.PlayerStateVersion);
        Assert.Equal(12345L, envelope.SequenceId);
        Assert.Equal(state.LastSimulatedAt, envelope.LastSimulatedAt);

        Assert.NotNull(restored);
        Assert.Equal(state.PlayerId, restored.PlayerId);
        Assert.Equal(state.Name, restored.Name);
        Assert.Equal(state.Account, restored.Account);
        Assert.Equal(42L, restored.Version);
        Assert.Equal(12345L, restored.SequenceId);
        Assert.Equal(99L, restored.Inventory.Items["herb_spirit_grass"]);
    }

    [Fact]
    public void Deserialize_RejectsLegacyRawPlayerStateJson_ThrowsInvalidDataException()
    {
        var rawState = new PlayerState
        {
            PlayerId = Guid.NewGuid(),
            Name = "旧版玩家",
            Account = "legacy_user",
            SequenceId = 1,
            Version = 10
        };

        // 旧版直接序列化 PlayerState（无 SnapshotEnvelope 外壳）
        var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(rawState, StateSerializer.Options);

        var ex = Assert.Throws<InvalidDataException>(() => StateSerializer.DeserializeEnvelope(legacyBytes));
        Assert.Contains("Schema 版本不兼容", ex.Message);
    }

    [Fact]
    public void Deserialize_RejectsIncompatibleSchemaVersion_ThrowsInvalidDataException()
    {
        var state = new PlayerState
        {
            PlayerId = Guid.NewGuid(),
            Name = "未来玩家",
            Version = 1
        };

        var futureEnvelope = new SnapshotEnvelope(
            SnapshotSchemaVersion: 99, // 未来未知版本
            GameDataFingerprint: GameDataCatalog.Current.Fingerprint,
            PlayerStateVersion: 1,
            SequenceId: 1,
            LastSimulatedAt: DateTimeOffset.UtcNow,
            Payload: state);

        var bytes = StateSerializer.SerializeEnvelope(futureEnvelope);

        var ex = Assert.Throws<InvalidDataException>(() => StateSerializer.DeserializeEnvelope(bytes));
        Assert.Contains("快照 Schema 版本不兼容: 快照版本 99", ex.Message);
    }

    [Fact]
    public void Deserialize_RejectsMismatchedGameDataFingerprint_ThrowsInvalidDataException()
    {
        var state = new PlayerState
        {
            PlayerId = Guid.NewGuid(),
            Name = "指纹不一致玩家",
            Version = 1
        };

        var bytesWithMismatchedFingerprint = StateSerializer.Serialize(state, "invalid_fingerprint_123");

        var ex = Assert.Throws<InvalidDataException>(() => StateSerializer.DeserializeEnvelope(bytesWithMismatchedFingerprint));
        Assert.Contains("游戏数据指纹不一致", ex.Message);
        Assert.Contains("invalid_fingerprint_123", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a json payload")]
    [InlineData("{\"someOtherField\": 123}")]
    public void Deserialize_RejectsInvalidPayload_ThrowsInvalidDataException(string invalidJson)
    {
        var bytes = Encoding.UTF8.GetBytes(invalidJson);
        Assert.Throws<InvalidDataException>(() => StateSerializer.DeserializeEnvelope(bytes));
    }

    [Fact]
    public void Deserialize_RejectsNullPayloadInsideEnvelope_ThrowsInvalidDataException()
    {
        var json = $$"""
        {
            "snapshotSchemaVersion": {{SnapshotEnvelope.CurrentSchemaVersion}},
            "gameDataFingerprint": "{{GameDataCatalog.Current.Fingerprint}}",
            "playerStateVersion": 1,
            "sequenceId": 1,
            "lastSimulatedAt": "2026-08-22T20:00:00+08:00",
            "payload": null
        }
        """;

        var bytes = Encoding.UTF8.GetBytes(json);
        var ex = Assert.Throws<InvalidDataException>(() => StateSerializer.DeserializeEnvelope(bytes));
        Assert.Contains("载荷 (Payload) 为空", ex.Message);
    }

    [Fact]
    public void Deserialize_RejectsTamperedVersion_ThrowsInvalidDataException()
    {
        var state = new PlayerState
        {
            PlayerId = Guid.NewGuid(),
            Name = "版本篡改玩家",
            SequenceId = 1,
            Version = 1,
            LastSimulatedAt = DateTimeOffset.UtcNow
        };

        var tamperedEnvelope = new SnapshotEnvelope(
            SnapshotSchemaVersion: SnapshotEnvelope.CurrentSchemaVersion,
            GameDataFingerprint: GameDataCatalog.Current.Fingerprint,
            PlayerStateVersion: 999, // 篡改顶层版本号
            SequenceId: 1,
            LastSimulatedAt: state.LastSimulatedAt,
            Payload: state);

        var bytes = StateSerializer.SerializeEnvelope(tamperedEnvelope);
        var ex = Assert.Throws<InvalidDataException>(() => StateSerializer.DeserializeEnvelope(bytes));
        Assert.Contains("快照信封版本号 (999) 与载荷内部版本号 (1) 不一致", ex.Message);
    }

    [Fact]
    public void Deserialize_RejectsTamperedSequenceId_ThrowsInvalidDataException()
    {
        var state = new PlayerState
        {
            PlayerId = Guid.NewGuid(),
            Name = "序号篡改玩家",
            SequenceId = 1,
            Version = 1,
            LastSimulatedAt = DateTimeOffset.UtcNow
        };

        var tamperedEnvelope = new SnapshotEnvelope(
            SnapshotSchemaVersion: SnapshotEnvelope.CurrentSchemaVersion,
            GameDataFingerprint: GameDataCatalog.Current.Fingerprint,
            PlayerStateVersion: 1,
            SequenceId: 888, // 篡改顶层序号
            LastSimulatedAt: state.LastSimulatedAt,
            Payload: state);

        var bytes = StateSerializer.SerializeEnvelope(tamperedEnvelope);
        var ex = Assert.Throws<InvalidDataException>(() => StateSerializer.DeserializeEnvelope(bytes));
        Assert.Contains("快照信封序号 (888) 与载荷内部序号 (1) 不一致", ex.Message);
    }

    [Fact]
    public void Deserialize_RejectsEmptyPlayerIdOrName_ThrowsInvalidDataException()
    {
        var stateWithEmptyId = new PlayerState
        {
            PlayerId = Guid.Empty,
            Name = "无ID玩家",
            SequenceId = 1,
            Version = 1,
            LastSimulatedAt = DateTimeOffset.UtcNow
        };

        var bytes = StateSerializer.Serialize(stateWithEmptyId);
        var exId = Assert.Throws<InvalidDataException>(() => StateSerializer.DeserializeEnvelope(bytes));
        Assert.Contains("PlayerId 为空", exId.Message);

        var stateWithEmptyName = new PlayerState
        {
            PlayerId = Guid.NewGuid(),
            Name = "   ",
            SequenceId = 1,
            Version = 1,
            LastSimulatedAt = DateTimeOffset.UtcNow
        };

        var bytesName = StateSerializer.Serialize(stateWithEmptyName);
        var exName = Assert.Throws<InvalidDataException>(() => StateSerializer.DeserializeEnvelope(bytesName));
        Assert.Contains("玩家名称为空", exName.Message);
    }
}
