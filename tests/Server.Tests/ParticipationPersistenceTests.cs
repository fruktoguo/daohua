using System.Text.Json;
using Domain.Persistence;
using Domain.Player;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>组队唯一权威检查点、成员收件箱及个人原子入账的真实 PostgreSQL 边界。</summary>
[Collection("PostgresIntegration")]
public sealed class ParticipationPersistenceTests
{
    /// <summary>同次推进交付三份结算，任一投递失败全部回滚；重启与解散均保留检查点和待消费证据。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task AdvanceAndAllMemberInboxesCommitTogetherAndSurviveDisband()
    {
        await using var fixture = await Fixture.CreateAsync(3);
        var now = fixture.StartedAt.AddSeconds(1);
        var deliveries = fixture.Players.Select(player => new ParticipationInboxItem(fixture.SessionId, player, 1, "{\"xp\":3}", now)).ToArray();
        var broken = deliveries.ToArray();
        broken[2] = broken[2] with { PlayerId = Guid.NewGuid() };
        await Assert.ThrowsAsync<PostgresException>(async () => await fixture.Store.CommitAdvanceAsync(
            fixture.SessionId, 0, "{\"wave\":1}", now, broken));
        Assert.Equal(0, (await fixture.Store.LoadAsync(fixture.SessionId))!.Version);
        Assert.Empty(await fixture.Store.LoadPendingAsync(fixture.Players[0]));

        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 0, "{\"wave\":1}", now, deliveries));
        Assert.False(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 0, "{\"wave\":99}", now, deliveries));
        var recovered = new PostgresParticipationSessionStore(fixture.Source);
        var checkpoint = Assert.Single(await recovered.ListActiveAsync(), session => session.SessionId == fixture.SessionId);
        Assert.Equal(1, checkpoint.Version);
        Assert.Equal(now, checkpoint.LastSimulatedAt);
        Assert.Equal(1, JsonDocument.Parse(checkpoint.StateJson).RootElement.GetProperty("wave").GetInt32());
        Assert.Equal(checkpoint, Assert.Single(await recovered.ListActiveForMemberAsync(fixture.Players[2])));
        foreach (var player in fixture.Players) Assert.Single(await recovered.LoadPendingAsync(player));

        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 1, "{\"ended\":true}", now, [], now, "party-disbanded"));
        await new PostgresSocialRepository(fixture.Source).DisbandPartyAsync(fixture.PartyId);
        Assert.NotNull(await recovered.LoadAsync(fixture.SessionId));
        foreach (var player in fixture.Players) Assert.Single(await recovered.LoadPendingAsync(player));
        Assert.Empty(await recovered.ListActiveForMemberAsync(fixture.Players[0]));
    }

    /// <summary>既有 inbox 主键冲突不能留下推进了一半的检查点。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task DuplicateInboxSequenceRollsBackCheckpointAdvance()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var now = fixture.StartedAt.AddSeconds(1);
        var delivery = new ParticipationInboxItem(fixture.SessionId, fixture.Players[0], 1, "{}", now);
        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 0, "{\"wave\":1}", now, [delivery]));
        var error = await Assert.ThrowsAsync<PostgresException>(async () => await fixture.Store.CommitAdvanceAsync(
            fixture.SessionId, 1, "{\"wave\":2}", now.AddSeconds(1), [delivery]));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal(1, (await fixture.Store.LoadAsync(fixture.SessionId))!.Version);
        Assert.Single(await fixture.Store.LoadPendingAsync(fixture.Players[0]));
    }

    /// <summary>会话活跃或仍有未消费个人份额时，冷挂机任务不得入队。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ColdJobAdmissionSkipsActiveSessionAndPendingSettlement()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var jobs = new PostgresComputeJobRepository(fixture.Source);
        var target = fixture.StartedAt.AddSeconds(30);
        Assert.Equal(ComputeEnqueueResult.SkippedParticipation,
            await jobs.EnqueueAsync(fixture.Players[0], target, new byte[] { 1 }));

        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 0, "{\"ended\":true}", target,
            [new(fixture.SessionId, fixture.Players[0], 1, "{}", target)], target, "member-offline-timeout"));
        Assert.Equal(ComputeEnqueueResult.SkippedParticipation,
            await jobs.EnqueueAsync(fixture.Players[0], target.AddSeconds(1), new byte[] { 2 }));
    }

    /// <summary>成员失败结算不留下余额、分段或消费回执；精确重放不重复发放，不同幂等键不能二次消费。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task MemberConsumptionRollsBackWithAssetsAndSectionsAndReplaysExactly()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var player = fixture.Players[0];
        var now = fixture.StartedAt.AddSeconds(1);
        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 0, "{\"wave\":1}", now,
            [new(fixture.SessionId, player, 1, "{\"xp\":3}", now)]));
        var transactionId = Guid.NewGuid();
        var section = SectionCodec.Encode(player, PlayerSectionKinds.Progression, 5, new { ageSeconds = 3d }, now);
        var request = new EconomyTransactionRequest(transactionId, $"participation:{transactionId:D}", "participation.settlement", player,
            [new(player, "item.test-participation", "", 7)], now, Sections: [section],
            ParticipationReceipt: new(fixture.SessionId, player, 1));
        var invalidReceipt = new CommandReceipt(Guid.NewGuid(), Guid.NewGuid(), "participation.test", new string('a', 64), 5, now, now.AddMinutes(30));
        await Assert.ThrowsAsync<PostgresException>(async () => await fixture.Economy.CommitAsync(request with { Receipts = [invalidReceipt] }));
        Assert.Single(await fixture.Store.LoadPendingAsync(player));
        Assert.Null(await fixture.ScalarAsync("SELECT quantity FROM economy.player_asset_balances WHERE player_id = $1 AND asset_id = 'item.test-participation'", player));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM platform.section_flush_queue WHERE player_id = $1", player));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM economy.transactions WHERE transaction_id = $1", transactionId));

        Assert.False((await fixture.Economy.CommitAsync(request)).AlreadyCommitted);
        Assert.True((await fixture.Economy.CommitAsync(request)).AlreadyCommitted);
        Assert.Empty(await fixture.Store.LoadPendingAsync(player));
        Assert.Equal(7d, await fixture.ScalarAsync("SELECT quantity FROM economy.player_asset_balances WHERE player_id = $1 AND asset_id = 'item.test-participation'", player));
        Assert.Equal(5L, await fixture.ScalarAsync("SELECT state_version FROM platform.section_flush_queue WHERE player_id = $1", player));
        Assert.Equal(transactionId, await fixture.ScalarAsync("SELECT consumed_transaction_id FROM social.party_session_inbox WHERE session_id = $1 AND player_id = $2", fixture.SessionId, player));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Economy.CommitAsync(request with
        {
            TransactionId = Guid.NewGuid(), IdempotencyKey = $"participation:duplicate:{Guid.NewGuid():D}"
        }));
        await Assert.ThrowsAsync<EconomyIdempotencyConflictException>(async () => await fixture.Economy.CommitAsync(request with
        {
            ParticipationReceipt = new(fixture.SessionId, player, 2)
        }));
    }

    /// <summary>零掉落的血蓝、经验结算仍保存成员状态并确认收件箱，不需要伪造资产变化。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ZeroAssetSettlementCommitsMemberSectionAndInboxReceipt()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var player = fixture.Players[0];
        var now = fixture.StartedAt.AddSeconds(1);
        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 0, "{\"wave\":1}", now,
            [new(fixture.SessionId, player, 1, "{\"hp\":1}", now)]));
        var transactionId = Guid.NewGuid();
        var request = new EconomyTransactionRequest(transactionId, $"participation:zero:{transactionId:D}", "participation.settlement", player, [], now,
            Sections: [SectionCodec.Encode(player, PlayerSectionKinds.Progression, 2, new { ageSeconds = 1d }, now)],
            ParticipationReceipt: new(fixture.SessionId, player, 1));
        Assert.True((await fixture.Economy.CommitAsync(request)).Applied);
        Assert.True((await fixture.Economy.CommitAsync(request)).AlreadyCommitted);
        Assert.Empty(await fixture.Store.LoadPendingAsync(player));
        Assert.Equal(2L, await fixture.ScalarAsync("SELECT state_version FROM platform.section_flush_queue WHERE player_id = $1", player));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM economy.transaction_lines WHERE transaction_id = $1", transactionId));
    }

    /// <summary>回线补算截止于最早离线成员的逐出边界，当前在线成员不会误触发超时散场。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task OfflineExpiryUsesFrozenRosterAndExcludesOnlineMembers()
    {
        await using var fixture = await Fixture.CreateAsync(3);
        var inactiveAt = fixture.StartedAt.AddHours(-49);
        for (var index = 0; index < fixture.Players.Length; index++)
            await fixture.ScalarAsync("""
                UPDATE game.player_projection SET offline_since=$2, presence='offline', updated_at=$2 WHERE player_id=$1
                """, fixture.Players[index], inactiveAt.AddHours(index));
        Assert.Equal(inactiveAt.AddHours(49), await fixture.Store.GetOfflineExpiryAsync(
            fixture.SessionId, TimeSpan.FromHours(48), [fixture.Players[0]]));
        Assert.Null(await fixture.Store.GetOfflineExpiryAsync(fixture.SessionId, TimeSpan.FromHours(48), fixture.Players));
    }

    /// <summary>开战前提真实落库；取消准备和同人数名册替换均由恢复查询识别，不依赖社交仲裁已运行。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PersistentOrganizationQueryDetectsUnreadyAndFrozenRosterDrift()
    {
        await using var fixture = await Fixture.CreateAsync(3);
        var social = new PostgresSocialRepository(fixture.Source);
        Assert.Null(await fixture.Store.GetInvalidationReasonAsync(fixture.SessionId));
        Assert.True(await social.TryUpdatePartyProfileAsync(fixture.PartyId,
            PartyProfileRecord.Default with { Name = "名称变更不散场", NodeId = "node.test" }, fixture.StartedAt));
        Assert.Null(await fixture.Store.GetInvalidationReasonAsync(fixture.SessionId));

        Assert.True(await social.TrySetPartyMemberReadyAsync(fixture.Players[1], false));
        Assert.Equal("member-unready", await fixture.Store.GetInvalidationReasonAsync(fixture.SessionId));
        Assert.True(await social.TrySetPartyMemberReadyAsync(fixture.Players[1], true));
        Assert.Null(await fixture.Store.GetInvalidationReasonAsync(fixture.SessionId));

        // 同人数、全就绪仍必须逐名匹配冻结名册，不能只比人数。
        var replacement = Guid.NewGuid();
        await fixture.ScalarAsync("INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())", replacement, replacement.ToString());
        await fixture.ScalarAsync("INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,created_at,last_seen_at,status) VALUES ($1,$1,$2,$2,now(),now(),1)", replacement, replacement.ToString());
        await fixture.ScalarAsync("UPDATE social.party_members SET player_id=$2 WHERE player_id=$1", fixture.Players[1], replacement);
        Assert.Equal("member-roster-changed", await fixture.Store.GetInvalidationReasonAsync(fixture.SessionId));
    }

    /// <summary>组织恢复读取 durable 世界分段而非滞后投影；迁地中的 edgeId 使驻留失效。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PersistentOrganizationQueryRequiresFrozenTargetSlotsAndMemberResidence()
    {
        await using var fixture = await Fixture.CreateAsync(3);
        await fixture.ScalarAsync("UPDATE game.player_projection SET current_node_id='node.stale' WHERE player_id=ANY($1)", fixture.Players);
        Assert.Null(await fixture.Store.GetInvalidationReasonAsync(fixture.SessionId));
        await fixture.ScalarAsync("UPDATE social.parties SET open_slots=4 WHERE party_id=$1", fixture.PartyId);
        Assert.Equal("party-slots-changed", await fixture.Store.GetInvalidationReasonAsync(fixture.SessionId));
        await fixture.ScalarAsync("UPDATE social.parties SET open_slots=3, node_id='node.other' WHERE party_id=$1", fixture.PartyId);
        Assert.Equal("target-node-changed", await fixture.Store.GetInvalidationReasonAsync(fixture.SessionId));
        await fixture.ScalarAsync("UPDATE social.parties SET node_id='' WHERE party_id=$1", fixture.PartyId);
        Assert.Null(await fixture.Store.GetInvalidationReasonAsync(fixture.SessionId));
        var world = SectionCodec.Encode(fixture.Players[1], PlayerSectionKinds.World, 2,
            new PlayerWorldStateDocument("node.test", 0, 0, 0, EdgeId: "edge.travel"), fixture.StartedAt);
        await new PostgresSectionRepository(fixture.Source, NullLogger<PostgresSectionRepository>.Instance)
            .EnqueueSectionsAsync([world]);
        Assert.Equal("member-node-changed", await fixture.Store.GetInvalidationReasonAsync(fixture.SessionId));
        await new PostgresSocialRepository(fixture.Source).DisbandPartyAsync(fixture.PartyId);
        Assert.Equal("party-disbanded", await fixture.Store.GetInvalidationReasonAsync(fixture.SessionId));
    }

    /// <summary>下一检查点与低频终局清理只回收已消费载荷，未消费份额及经济审计事实始终保留。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ConsumedPayloadsAreReclaimedWithoutDeletingPendingSettlementsOrEconomyHistory()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        var first = fixture.StartedAt.AddSeconds(1);
        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 0, "{\"wave\":1}", first,
            fixture.Players.Select(player => new ParticipationInboxItem(fixture.SessionId, player, 1, "{}", first)).ToArray()));
        await ConsumeAsync(1);
        var second = first.AddSeconds(1);
        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 1, "{\"wave\":2}", second,
            fixture.Players.Select(player => new ParticipationInboxItem(fixture.SessionId, player, 2, "{}", second)).ToArray()));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM social.party_session_inbox WHERE session_id=$1 AND player_id=$2 AND sequence=1", fixture.SessionId, fixture.Players[0]));
        Assert.Equal(2, (await fixture.Store.LoadPendingAsync(fixture.Players[1])).Count);

        var endedAt = second.AddSeconds(1);
        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 2, "{\"ended\":true}", endedAt,
            fixture.Players.Select(player => new ParticipationInboxItem(fixture.SessionId, player, 3, "{}", endedAt)).ToArray(), endedAt, "member-unready"));
        await ConsumeAsync(2);
        await ConsumeAsync(3);
        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = "Host=unused;Database=unused"
        }).Build());
        var retention = new PostgresRetentionRepository(fixture.Source, options, NullLogger<PostgresRetentionRepository>.Instance);
        Assert.True((await retention.PruneAsync(endedAt.AddMinutes(1))).ParticipationInboxes >= 2);
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM social.party_session_inbox WHERE session_id=$1 AND consumed_at IS NOT NULL", fixture.SessionId));
        Assert.Equal(3, (await fixture.Store.LoadPendingAsync(fixture.Players[1])).Count);
        Assert.Equal(3L, await fixture.ScalarAsync("SELECT count(*) FROM economy.transactions WHERE actor_id=$1 AND transaction_type='participation.retention-test'", fixture.Players[0]));

        async Task ConsumeAsync(long sequence)
        {
            var id = Guid.NewGuid();
            await fixture.Economy.CommitAsync(new(id, $"participation:retention:{id:D}", "participation.retention-test", fixture.Players[0], [],
                ParticipationReceipt: new(fixture.SessionId, fixture.Players[0], sequence)));
        }
    }

    /// <summary>会话提交推进身份 MVCC 版本，旧 Compute Serializable 快照不能越过会话归属。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SessionAdvanceInvalidatesComputeSnapshotBeforeIdentityFence()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        await using var connection = await fixture.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await using (var read = new NpgsqlCommand("SELECT status FROM identity.players WHERE player_id=$1", connection, transaction))
        {
            read.Parameters.AddWithValue(fixture.Players[0]);
            await read.ExecuteScalarAsync();
        }
        var target = fixture.StartedAt.AddSeconds(1);
        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 0, "{\"wave\":1}", target, []));
        await using var fence = new NpgsqlCommand("UPDATE identity.players SET status=status WHERE player_id=$1", connection, transaction);
        fence.Parameters.AddWithValue(fixture.Players[0]);
        var error = await Assert.ThrowsAsync<PostgresException>(async () => await fence.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.SerializationFailure, error.SqlState);
    }

    private sealed class Fixture(NpgsqlDataSource source, Guid[] players, Guid partyId, string sessionId, DateTimeOffset startedAt) : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; } = source;
        public Guid[] Players { get; } = players;
        public Guid PartyId { get; } = partyId;
        public string SessionId { get; } = sessionId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public PostgresParticipationSessionStore Store { get; } = new(source);
        public PostgresEconomyRepository Economy { get; } = new(source, NullLogger<PostgresEconomyRepository>.Instance);

        public static async Task<Fixture> CreateAsync(int memberCount)
        {
            var source = NpgsqlDataSource.Create(PostgresTestSchema.RequireConnectionString("组队会话持久化集成测试"));
            await PostgresTestSchema.EnsureAsync(source);
            var players = Enumerable.Range(0, memberCount).Select(_ => Guid.NewGuid()).ToArray();
            foreach (var player in players)
            {
                await SqlAsync(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())", player, player.ToString());
                await SqlAsync(source, "INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,created_at,last_seen_at,status) VALUES ($1,$1,$2,$2,now(),now(),1)", player, player.ToString());
            }
            // 毫秒边界避免数据库微秒精度与 DateTimeOffset 的 100ns 精度差异干扰检查点断言。
            var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var social = new PostgresSocialRepository(source);
            var partyId = await social.CreatePartyAsync(players[0], PartyProfileRecord.Default, startedAt);
            Assert.True(await social.TryUpdatePartyOpenSlotsAsync(partyId, Math.Max(2, memberCount), startedAt));
            foreach (var player in players.Skip(1))
                Assert.Equal(PartyJoinResult.Joined, await social.TryJoinPartyAsync(partyId, player, startedAt));
            foreach (var player in players)
            {
                Assert.True(await social.TrySetPartyMemberReadyAsync(player, true));
                await SqlAsync(source, """
                    INSERT INTO game.player_projection(player_id, current_node_id, last_active_at, updated_at)
                    VALUES ($1, 'node.test', $2, $2)
                    """, player, startedAt);
                var world = SectionCodec.Encode(player, PlayerSectionKinds.World, 1,
                    new PlayerWorldStateDocument("node.test", 0, 0, 0), startedAt);
                await SqlAsync(source, """
                    INSERT INTO game.player_state_sections
                        (player_id,section_kind,section_version,schema_version,codec,payload,raw_bytes,stored_bytes,checksum,updated_at)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
                    """, player, world.SectionKind, world.StateVersion, world.SchemaVersion, world.Codec,
                    world.Payload.ToArray(), world.RawBytes, world.StoredBytes, world.Checksum, startedAt);
            }
            var sessionId = $"action:{Guid.NewGuid():N}";
            var fixture = new Fixture(source, players, partyId, sessionId, startedAt);
            var specJson = JsonSerializer.Serialize(new { encounterId = "encounter.test", nodeId = "node.test", members = players.Select(player => new { playerId = player.ToString("D") }) });
            await fixture.Store.CreateOrInitializeAsync(new(sessionId, partyId, specJson, "{\"wave\":0}", 0, startedAt));
            return fixture;
        }

        public async Task<object?> ScalarAsync(string sql, params object[] values)
        {
            await using var command = Source.CreateCommand(sql);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            return await command.ExecuteScalarAsync();
        }

        private static async Task SqlAsync(NpgsqlDataSource source, string sql, params object[] values)
        {
            await using var command = source.CreateCommand(sql);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            // 只清理本用例会话，避免冻结名册留在共享测试库中干扰后续恢复扫描。
            await SqlAsync(Source, "DELETE FROM social.party_session_inbox WHERE session_id = $1", SessionId);
            await SqlAsync(Source, "DELETE FROM social.party_sessions WHERE session_id = $1", SessionId);
            await SqlAsync(Source, "DELETE FROM social.parties WHERE party_id = $1", PartyId);
            await Source.DisposeAsync();
        }
    }
}
