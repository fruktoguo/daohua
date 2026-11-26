using System.Text.Json;
using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Npgsql;
using Protocol;
using Server.Clean;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 秘境副本服务端映射、契约与持久化测试。
/// </summary>
public sealed class DungeonServerContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StaticCatalogMapper_ProjectsFourDungeonsWithTenWavesAndLootTable()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var projected = StaticGameCatalogMapper.Dungeons(catalog);
        Assert.Equal(4, projected.Count);

        var json = JsonSerializer.Serialize(projected, ProtocolJson.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(4, doc.RootElement.GetArrayLength());

        var first = doc.RootElement[0];
        Assert.Equal("dungeon.qingyun-secret-gorge", first.GetProperty("dungeonId").GetString());
        Assert.Equal("追忆·青云古涧", first.GetProperty("name").GetString());
        Assert.Equal("node.cloud-peak", first.GetProperty("nodeId").GetString());
        Assert.True(first.GetProperty("minMonsterLevel").GetInt32() >= 1);
        Assert.Equal(127, first.GetProperty("levelAnchors").GetArrayLength());
        Assert.Equal(10, first.GetProperty("totalWaves").GetInt32());
        Assert.Equal(10, first.GetProperty("waves").GetArrayLength());
        Assert.Equal("boss", first.GetProperty("waves")[9].GetProperty("kind").GetString());
        Assert.True(first.GetProperty("clearLootTable").GetArrayLength() >= 2);
    }

    [Fact]
    public void ProtocolSnapshotMapper_ProjectsTiliFieldsAndDungeonIds_AndSectionCodecRoundTripsV47()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.Parse("10000000-0000-4000-8000-00000000d301");
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(Guid.NewGuid()), "快照道友", Now, catalog.Fingerprint);
        aggregate.World.NodeId = "node.cloud-peak";
        var nowUnix = Now.ToUnixTimeMilliseconds() / 1000.0;
        aggregate.Progression.TiliValue = 150;
        aggregate.Progression.TiliUpdatedAtUnixSeconds = nowUnix - 750; // 恢复 2 点，余 30 秒

        var exported = aggregate.ExportState();
        var runtimeSnap = new PlayerRuntimeSnapshot(
            playerId,
            exported.DisplayName,
            catalog.Fingerprint,
            exported.StateVersion,
            Now,
            exported,
            exported.Actions.Count,
            []);

        var dto = ProtocolSnapshotMapper.ToSnapshot(runtimeSnap, catalog.Fingerprint, catalog);
        var progSection = Assert.Single(dto.Sections, s => s.Section == "progression");
        Assert.Equal(SectionCodec.CurrentSchemaVersion, progSection.SchemaVersion);
        Assert.True(SectionCodec.CurrentSchemaVersion >= 47);

        using (var progDoc = JsonDocument.Parse(JsonSerializer.Serialize(progSection.Data, ProtocolJson.Options)))
        {
            var root = progDoc.RootElement;
            Assert.Equal(150, root.GetProperty("tiliValue").GetInt32());
            Assert.Equal(152, root.GetProperty("tiliCurrent").GetInt32());
            Assert.Equal(DungeonEnergyRules.MaxEnergy, root.GetProperty("tiliMax").GetInt32());
            Assert.InRange(root.GetProperty("tiliNextRecoverySeconds").GetDouble(), 329, 331);
        }

        // World 段透传当前节点 dungeonIds
        var worldSection = Assert.Single(dto.Sections, s => s.Section == "world");
        using (var worldDoc = JsonDocument.Parse(JsonSerializer.Serialize(worldSection.Data, ProtocolJson.Options)))
        {
            var dungeonIds = worldDoc.RootElement.GetProperty("dungeonIds").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Contains("dungeon.qingyun-secret-gorge", dungeonIds);
        }

        // SectionCodec 压缩/解压往返校验 v47 持久化无损
        var encoded = SectionCodec.Encode(playerId, "progression", 1, exported.Progression, Now, compress: true);
        var stored = new StoredSection(encoded.PlayerId, encoded.SectionKind, encoded.StateVersion, encoded.SchemaVersion, encoded.Codec, encoded.Payload.ToArray(), encoded.RawBytes, encoded.Checksum, encoded.UpdatedAt);
        var decoded = SectionCodec.Decode<PlayerProgressionState>(stored);
        Assert.Equal(150, decoded.TiliValue);
        Assert.Equal(nowUnix - 750, decoded.TiliUpdatedAtUnixSeconds, precision: 6);
    }

    [Fact]
    public void LegacyZeroTiliAnchor_ProjectsNormalizedAnchorInSnapshotAndParticipantCapture()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.Parse("10000000-0000-4000-8000-00000000d302");
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(Guid.NewGuid()), "旧档道友", Now, catalog.Fingerprint);
        var baseExported = aggregate.ExportState();
        var legacyState = baseExported with
        {
            Progression = baseExported.Progression with { TiliValue = 0, TiliUpdatedAtUnixSeconds = 0 }
        };
        var expectedAnchorUnix = Now.ToUnixTimeMilliseconds() / 1000.0;

        // 1. 协议快照投影应自动透传规范化后的有效锚点，避免前端收到 0 锚点死锁倒计时
        var runtimeSnap = new PlayerRuntimeSnapshot(
            playerId, legacyState.DisplayName, catalog.Fingerprint, legacyState.StateVersion, Now, legacyState, 0, []);
        var dto = ProtocolSnapshotMapper.ToSnapshot(runtimeSnap, catalog.Fingerprint, catalog);
        var progSection = Assert.Single(dto.Sections, s => s.Section == "progression");
        using (var progDoc = JsonDocument.Parse(JsonSerializer.Serialize(progSection.Data, ProtocolJson.Options)))
        {
            var root = progDoc.RootElement;
            Assert.Equal(DungeonEnergyRules.InitialEnergy, root.GetProperty("tiliValue").GetInt32());
            Assert.Equal(expectedAnchorUnix, root.GetProperty("tiliUpdatedAtUnixSeconds").GetDouble(), precision: 6);
            Assert.Equal(DungeonEnergyRules.InitialEnergy, root.GetProperty("tiliCurrent").GetInt32());
        }

        // 2. 离线组队快照采集应同样按 LastSimulatedAt 补锚，保证离线成员体力恢复与上线一致
        var participant = ActionParticipantSnapshot.FromState(legacyState, catalog);
        Assert.Equal(DungeonEnergyRules.InitialEnergy, participant.InitialTiliValue);
        Assert.Equal(expectedAnchorUnix, participant.InitialTiliUpdatedAtUnixSeconds, precision: 6);
    }

    [Fact]
    public void SchemaVersionAndMigrationScript_AreAlignedAtV67()
    {
        Assert.True(PostgresSchema.Version >= 67);
        Assert.Contains("dungeon_id", PostgresSchema.Sql, StringComparison.Ordinal);
        Assert.Contains("dungeon_repeat_count", PostgresSchema.Sql, StringComparison.Ordinal);
        Assert.Contains("assist_mode", PostgresSchema.Sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (difficulty BETWEEN 0 AND 10)", PostgresSchema.Sql, StringComparison.Ordinal);
    }
}

/// <summary>
/// 秘境副本真实 PostgreSQL 集成测试：验证队伍秘境档案字段、助战模式开关与自然终局后全员就绪复位持久化。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class DungeonSocialPostgresIntegrationTests
{
    private static async Task<Guid> InsertPlayerAsync(NpgsqlDataSource source, string prefix)
    {
        var accountId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var accountName = $"{prefix}_acc_{suffix}";
        var displayName = $"{prefix}_{suffix}";
        await using var conn = await source.OpenConnectionAsync();
        await using (var accCmd = new NpgsqlCommand("""
            INSERT INTO identity.accounts(account_id, login_normalized, password_hash, password_salt, password_iterations, created_at, updated_at)
            VALUES ($1, lower($2), '\x00'::bytea, '\x00'::bytea, 100000, now(), now())
            """, conn))
        {
            accCmd.Parameters.Add(new NpgsqlParameter { Value = accountId });
            accCmd.Parameters.Add(new NpgsqlParameter { Value = accountName });
            await accCmd.ExecuteNonQueryAsync();
        }
        await using (var playerCmd = new NpgsqlCommand("""
            INSERT INTO identity.players(player_id, account_id, display_name, display_name_normalized, invite_code, created_at, last_seen_at)
            VALUES ($1, $2, $3, lower($3), left(replace($1::text, '-', ''), 16), now(), now())
            """, conn))
        {
            playerCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
            playerCmd.Parameters.Add(new NpgsqlParameter { Value = accountId });
            playerCmd.Parameters.Add(new NpgsqlParameter { Value = displayName });
            await playerCmd.ExecuteNonQueryAsync();
        }
        return playerId;
    }

    [Fact]
    public async Task PartyDungeonProfile_AssistMode_AndResetReady_PersistInPostgres()
    {
        var cs = PostgresTestSchema.RequireConnectionString("秘境队伍集成测试");
        var source = PostgresTestSchema.GetSharedDataSource(cs);
        await PostgresTestSchema.EnsureAsync(source);

        var repo = new PostgresSocialRepository(source);
        var leaderId = await InsertPlayerAsync(source, "dg_leader");
        var memberId = await InsertPlayerAsync(source, "dg_member");
        var now = DateTimeOffset.UtcNow;

        // 1. 创建秘境队伍并校验持久化字段
        var profile = new PartyProfileRecord(
            Name: "青云速刷队",
            ActivityKind: PartyActivityKinds.Dungeon,
            NodeId: "node.cloud-peak",
            MinValuation: 5000,
            IsPublic: true,
            PvpEnabled: false,
            Difficulty: 4,
            DungeonId: "dungeon.qingyun-secret-gorge",
            DungeonRepeatCount: 5);
        var partyId = await repo.CreatePartyAsync(leaderId, profile, now);
        var loaded = await repo.GetPartyAsync(partyId);
        Assert.NotNull(loaded);
        Assert.Equal(PartyActivityKinds.Dungeon, loaded!.Profile.ActivityKind);
        Assert.Equal("dungeon.qingyun-secret-gorge", loaded.Profile.DungeonId);
        Assert.Equal(4, loaded.Profile.Difficulty);
        Assert.Equal(5, loaded.Profile.DungeonRepeatCount);
        Assert.False(loaded.Profile.PvpEnabled);

        // 2. 成员入队、切助战模式、全员准备后模拟自然终局复位全员 ready = false
        await repo.TryJoinPartyAsync(partyId, memberId, now.AddSeconds(1));
        Assert.True(await repo.TrySetPartyMemberAssistAsync(memberId, true));
        await repo.TrySetPartyMemberReadyAsync(leaderId, true);
        await repo.TrySetPartyMemberReadyAsync(memberId, true);

        var beforeReset = await repo.GetPartyAsync(partyId);
        Assert.NotNull(beforeReset);
        var memberRow = Assert.Single(beforeReset!.Members, m => m.PlayerId == memberId);
        Assert.True(memberRow.AssistMode);
        Assert.All(beforeReset.Members, m => Assert.True(m.Ready));

        await repo.ResetPartyMembersReadyAsync(partyId);
        var afterReset = await repo.GetPartyAsync(partyId);
        Assert.NotNull(afterReset);
        Assert.All(afterReset!.Members, m => Assert.False(m.Ready));
        // 复位就绪不应误清助战标记
        Assert.True(Assert.Single(afterReset.Members, m => m.PlayerId == memberId).AssistMode);
    }
}
