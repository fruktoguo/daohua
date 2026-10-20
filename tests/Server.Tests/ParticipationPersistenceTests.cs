using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using System.Text.Json.Nodes;
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
[Trait("Category", "PostgresIntegration")]
public sealed class ParticipationPersistenceTests
{
    /// <summary>同次推进交付三份结算，任一投递失败全部回滚；重启与解散均保留检查点和待消费证据。</summary>
    [Fact]
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

    /// <summary>开场实耗、检查点与首次通知同生同灭；重复或未知提交后的恢复不再扣款或覆盖已存进度。</summary>
    [Fact]
    public async Task OpeningDebitCheckpointAndInitialInboxCommitOrRollBackTogether()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var player = fixture.Players[0];
        const string assetId = "item.test-consumable";
        await fixture.Economy.CommitAsync(new(Guid.NewGuid(), $"test.seed:{Guid.NewGuid():N}", "test.seed", player,
            [new(player, assetId, "", 5)]));
        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 0, "{\"ended\":true}",
            fixture.StartedAt, [], fixture.StartedAt, "test-end"));
        var specJson = JsonSerializer.Serialize(new { encounterId = "encounter.test", nodeId = "node.test",
            members = fixture.Players.Select(p => new { playerId = p.ToString("D") }) });
        var sessionId = $"action:{Guid.NewGuid():N}";
        var brokeId = $"action:{Guid.NewGuid():N}";
        try
        {
            var record = new ActionParticipationSessionRecord(sessionId, fixture.PartyId, specJson,
                "{\"wave\":0}", 1, fixture.StartedAt);
            var balances = await fixture.Store.LoadConsumableBalancesAsync([new(player, assetId, "")]);
            var transactionId = Guid.NewGuid();
            var request = new EconomyTransactionRequest(transactionId, $"party.consumable:{sessionId}",
                "participation.consumable", player, [new(player, assetId, "", -5)]);
            var economy = new ParticipationSessionEconomyCommit(request, balances);
            var initial = new ParticipationInboxItem(sessionId, player, 1, "{\"hp\":5}", fixture.StartedAt);
            await Assert.ThrowsAsync<PostgresException>(async () => await fixture.Store.CreateOrInitializeAsync(
                record, economy, [initial with { PlayerId = Guid.NewGuid() }]));
            Assert.Null(await fixture.Store.LoadAsync(sessionId));
            Assert.Equal(5d, (await fixture.Store.LoadConsumableBalancesAsync([new(player, assetId, "")]))[0].Quantity);
            Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM economy.transactions WHERE transaction_id=$1", transactionId));

            var stored = await fixture.Store.CreateOrInitializeAsync(record, economy, [initial]);
            Assert.Equal(1, stored.Version);
            Assert.Equal(0d, (await fixture.Store.LoadConsumableBalancesAsync([new(player, assetId, "")]))[0].Quantity);
            Assert.Equal(1, Assert.Single(await fixture.Store.LoadPendingAsync(player)).Sequence);
            Assert.Equal(transactionId, await fixture.ScalarAsync(
                "SELECT transaction_id FROM economy.transactions WHERE transaction_id=$1", transactionId));

            // 旧读集已失效，甚至经济幂等键也已换新；重复初始化仍必须只恢复首次权威结果。
            var again = await fixture.Store.CreateOrInitializeAsync(record with { StateJson = "{\"wave\":7}", Version = 7 },
                economy with { Transaction = request with { TransactionId = Guid.NewGuid(), IdempotencyKey = $"test.duplicate:{Guid.NewGuid():N}" } }, [initial]);
            Assert.Equal(stored, again);
            Assert.Single(await fixture.Store.LoadPendingAsync(player));
            Assert.Equal(0d, (await fixture.Store.LoadConsumableBalancesAsync([new(player, assetId, "")]))[0].Quantity);

            Assert.True(await fixture.Store.CommitAdvanceAsync(sessionId, 1, "{\"ended\":true}",
                fixture.StartedAt, [], fixture.StartedAt, "test-end"));
            var brokeRequest = new EconomyTransactionRequest(Guid.NewGuid(), $"party.consumable:{brokeId}",
                "participation.consumable", player, [new(player, assetId, "", -1)]);
            var empty = await fixture.Store.LoadConsumableBalancesAsync([new(player, assetId, "")]);
            await Assert.ThrowsAsync<EconomyInsufficientBalanceException>(async () =>
                await fixture.Store.CreateOrInitializeAsync(
                    new(brokeId, fixture.PartyId, specJson, "{\"wave\":0}", 1, fixture.StartedAt),
                    new(brokeRequest, empty), [initial with { SessionId = brokeId }]));
            Assert.Null(await fixture.Store.LoadAsync(brokeId));
            Assert.Equal(0L, await fixture.ScalarAsync(
                "SELECT count(*) FROM economy.transactions WHERE transaction_id=$1", brokeRequest.TransactionId));
            Assert.Equal(0L, await fixture.ScalarAsync(
                "SELECT count(*) FROM social.party_session_inbox WHERE session_id=$1", brokeId));

            await Assert.ThrowsAsync<ParticipationSessionEconomyConflictException>(async () =>
                await fixture.Store.CreateOrInitializeAsync(
                    new(brokeId, fixture.PartyId, specJson, "{\"wave\":0}", 1, fixture.StartedAt),
                    new(null, balances), [initial with { SessionId = brokeId }]));
            Assert.Null(await fixture.Store.LoadAsync(brokeId));
        }
        finally
        {
            await fixture.ScalarAsync("DELETE FROM social.party_session_inbox WHERE session_id = $1", sessionId);
            await fixture.ScalarAsync("DELETE FROM social.party_sessions WHERE session_id = $1", sessionId);
            await fixture.ScalarAsync("DELETE FROM social.party_sessions WHERE session_id = $1", brokeId);
        }
    }

    /// <summary>推进实耗与通知失败一起回滚，检查点重放或旧经济键复用都不能二次扣除。</summary>
    [Fact]
    public async Task AdvanceDebitIsAtomicAndCheckpointReplayCannotDoubleSpend()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var player = fixture.Players[0];
        var key = new ParticipationAssetKey(player, "item.test-online", "quality:2");
        await fixture.Economy.CommitAsync(new(Guid.NewGuid(), $"test.seed:{Guid.NewGuid():N}", "test.seed", player,
            [new(player, key.AssetId, key.Variant, 3)]));
        var readSet = await fixture.Store.LoadConsumableBalancesAsync([key]);
        var request = new EconomyTransactionRequest(Guid.NewGuid(), $"participation.consume:{fixture.SessionId}:1",
            "participation.consumable", player, [new(player, key.AssetId, key.Variant, -1)]);
        var economy = new ParticipationSessionEconomyCommit(request, readSet);
        var at = fixture.StartedAt.AddSeconds(1);
        var delivery = new ParticipationInboxItem(fixture.SessionId, player, 1, "{\"hp\":1}", at);
        await Assert.ThrowsAsync<PostgresException>(async () => await fixture.Store.CommitAdvanceAsync(
            fixture.SessionId, 0, "{\"wave\":1}", at, [delivery with { PlayerId = Guid.NewGuid() }], economy: economy));
        Assert.Equal(0, (await fixture.Store.LoadAsync(fixture.SessionId))!.Version);
        Assert.Equal(readSet, await fixture.Store.LoadConsumableBalancesAsync([key]));
        Assert.Empty(await fixture.Store.LoadPendingAsync(player));
        Assert.Equal(0L, await fixture.ScalarAsync(
            "SELECT count(*) FROM economy.transactions WHERE transaction_id=$1", request.TransactionId));

        var insufficient = economy with { Transaction = request with { Deltas = [new(player, key.AssetId, key.Variant, -4)] } };
        await Assert.ThrowsAsync<EconomyInsufficientBalanceException>(async () => await fixture.Store.CommitAdvanceAsync(
            fixture.SessionId, 0, "{\"wave\":1}", at, [delivery], economy: insufficient));
        Assert.Equal(0, (await fixture.Store.LoadAsync(fixture.SessionId))!.Version);
        Assert.Equal(readSet, await fixture.Store.LoadConsumableBalancesAsync([key]));

        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 0, "{\"wave\":1}", at, [delivery], economy: economy));
        Assert.False(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 0, "{\"wave\":9}", at, [delivery], economy: economy));
        var current = await fixture.Store.LoadConsumableBalancesAsync([key]);
        Assert.Equal(2d, Assert.Single(current).Quantity);
        Assert.False(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 1, "{\"wave\":2}", at.AddSeconds(1), [],
            economy: economy with { ReadSet = current }));
        Assert.Equal(1, (await fixture.Store.LoadAsync(fixture.SessionId))!.Version);
        Assert.Equal(current, await fixture.Store.LoadConsumableBalancesAsync([key]));
        Assert.Single(await fixture.Store.LoadPendingAsync(player));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT count(*) FROM economy.transaction_lines WHERE transaction_id=$1", request.TransactionId));
    }

    /// <summary>缺失键零余额也参与读集；读取后的并发首次入账使整批失效，不会推进缺货计算结果。</summary>
    [Fact]
    public async Task MissingBalanceConcurrentCreditRejectsWholeAdvance()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var player = fixture.Players[0];
        var key = new ParticipationAssetKey(player, "item.test-concurrent", "");
        var readSet = await fixture.Store.LoadConsumableBalancesAsync([key, key]);
        Assert.Equal(new AssetBalanceRecord(player, key.AssetId, key.Variant, 0, 0), Assert.Single(readSet));
        await using var connection = await fixture.Source.OpenConnectionAsync();
        await using var credit = await connection.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO economy.player_asset_balances(player_id,asset_id,variant,quantity,version,updated_at)
            VALUES ($1,$2,$3,1,1,now())
            """, connection, credit))
        {
            insert.Parameters.AddWithValue(player);
            insert.Parameters.AddWithValue(key.AssetId);
            insert.Parameters.AddWithValue(key.Variant);
            await insert.ExecuteNonQueryAsync();
        }
        var at = fixture.StartedAt.AddSeconds(1);
        var advance = fixture.Store.CommitAdvanceAsync(fixture.SessionId, 0, "{\"missing\":true}", at,
            [new(fixture.SessionId, player, 1, "{}", at)], economy: new(null, readSet)).AsTask();
        await credit.CommitAsync();
        Assert.False(await advance);
        Assert.Equal(0, (await fixture.Store.LoadAsync(fixture.SessionId))!.Version);
        Assert.Empty(await fixture.Store.LoadPendingAsync(player));
        Assert.Equal(1d, Assert.Single(await fixture.Store.LoadConsumableBalancesAsync([key])).Quantity);
        Assert.Equal(0L, await fixture.ScalarAsync(
            "SELECT count(*) FROM economy.transactions WHERE transaction_type='participation.consumable' AND actor_id=$1", player));
    }

    /// <summary>无货批次只建立零行锁证明、不制造经济流水；后续必须使用真实零行版本，余额相同也不能忽略版本冲突。</summary>
    [Fact]
    public async Task ReadOnlyMissingBalanceUsesPersistedVersionWithoutEmptyEconomyTransaction()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var player = fixture.Players[0];
        var key = new ParticipationAssetKey(player, "item.test-missing", "");
        var unrelated = new ParticipationAssetKey(player, "item.test-unrelated", "");
        await fixture.Economy.CommitAsync(new(Guid.NewGuid(), $"test.seed:{Guid.NewGuid():N}", "test.seed", player,
            [new(player, unrelated.AssetId, unrelated.Variant, 8)]));
        var missing = await fixture.Store.LoadConsumableBalancesAsync([key]);
        var at = fixture.StartedAt.AddSeconds(1);
        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 0, "{\"wave\":1}", at, [],
            economy: new(null, missing)));
        var persisted = Assert.Single(await fixture.Store.LoadConsumableBalancesAsync([key]));
        Assert.Equal(0d, persisted.Quantity);
        Assert.Equal(1, persisted.Version);
        Assert.False(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 1, "{\"wave\":2}", at.AddSeconds(1), [],
            economy: new(null, missing)));
        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 1, "{\"wave\":2}", at.AddSeconds(1), [],
            economy: new(null, [persisted])));

        await fixture.Economy.CommitAsync(new(Guid.NewGuid(), $"test.credit:{Guid.NewGuid():N}", "test.credit", player,
            [new(player, key.AssetId, key.Variant, 1)]));
        await fixture.Economy.CommitAsync(new(Guid.NewGuid(), $"test.debit:{Guid.NewGuid():N}", "test.debit", player,
            [new(player, key.AssetId, key.Variant, -1)]));
        Assert.False(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 2, "{\"wave\":3}", at.AddSeconds(2), [],
            economy: new(null, [persisted])));
        Assert.Equal(2, (await fixture.Store.LoadAsync(fixture.SessionId))!.Version);
        Assert.Equal(0d, Assert.Single(await fixture.Store.LoadConsumableBalancesAsync([key])).Quantity);
        Assert.Equal(3, Assert.Single(await fixture.Store.LoadConsumableBalancesAsync([key])).Version);
        Assert.Equal(8d, Assert.Single(await fixture.Store.LoadConsumableBalancesAsync([unrelated])).Quantity);
        Assert.Equal(0L, await fixture.ScalarAsync(
            "SELECT count(*) FROM economy.transactions WHERE transaction_type='participation.consumable' AND actor_id=$1", player));
    }

    /// <summary>物品级读取包含全部变体及真实零行，补足缺失默认键，且不会泄漏其他物品或成员的钱包。</summary>
    [Fact]
    public async Task ConsumableItemReadIncludesEveryVariantAndMissingDefaultOnlyForRequestedMembers()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        var player = fixture.Players[0];
        var other = fixture.Players[1];
        const string assetId = "item.test-variants";
        const string missingId = "item.test-entirely-missing";
        await fixture.Economy.CommitAsync(new(Guid.NewGuid(), $"test.seed:{Guid.NewGuid():N}", "test.seed", player,
        [
            new(player, assetId, "quality:2", 3),
            new(player, assetId, "quality:3", 1),
            new(player, "item.test-unrelated", "", 7),
            new(other, assetId, "quality:9", 8)
        ]));
        await fixture.Economy.CommitAsync(new(Guid.NewGuid(), $"test.debit:{Guid.NewGuid():N}", "test.debit", player,
            [new(player, assetId, "quality:3", -1)]));
        var balances = await fixture.Store.LoadConsumableBalancesForItemsAsync(
            [new(player, assetId, ""), new(player, assetId, "ignored"), new(player, missingId, "")]);
        Assert.Equal(4, balances.Count);
        Assert.Contains(new AssetBalanceRecord(player, assetId, "", 0, 0), balances);
        Assert.Contains(new AssetBalanceRecord(player, assetId, "quality:2", 3, 1), balances);
        Assert.Contains(new AssetBalanceRecord(player, assetId, "quality:3", 0, 2), balances);
        Assert.Contains(new AssetBalanceRecord(player, missingId, "", 0, 0), balances);
        Assert.DoesNotContain(balances, balance => balance.PlayerId == other || balance.AssetId == "item.test-unrelated");

        Assert.True(await fixture.Store.CommitAdvanceAsync(fixture.SessionId, 0, "{\"wave\":1}",
            fixture.StartedAt.AddSeconds(1), [], economy: new(null, balances)));
        var persisted = await fixture.Store.LoadConsumableBalancesForItemsAsync([new(player, assetId, "quality:2")]);
        Assert.Equal(3, persisted.Count);
        Assert.Contains(new AssetBalanceRecord(player, assetId, "", 0, 1), persisted);
        Assert.Contains(new AssetBalanceRecord(player, assetId, "quality:3", 0, 2), persisted);
        Assert.Empty(await fixture.Store.LoadConsumableBalancesForItemsAsync([]));
        Assert.Equal(new AssetBalanceRecord(player, assetId, "quality:2", 3, 1),
            Assert.Single(await fixture.Store.LoadConsumableBalancesAsync([new(player, assetId, "quality:2")])));
    }

    /// <summary>成员失败结算不留下余额、分段或消费回执；精确重放不重复发放，不同幂等键不能二次消费。</summary>
    [Fact]
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

    /// <summary>旧托管按已提交余量退款一次；坏检查点整笔回滚，终局时钟和既有奖励不丢失。</summary>
    [Fact]
    public async Task WalletMigrationRefundsOldActiveEscrowExactlyOnceAndPreservesPendingRewards()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        await using var connection = await fixture.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var at = fixture.StartedAt;
        var seconds = at.ToUnixTimeMilliseconds() / 1000d;
        var stats = CombatantStats.Create(100, 50, 10, 1);
        var spec = new ActionParticipationSpec(fixture.SessionId, "encounter.qi-beast", "node.test", 97, 0, seconds,
            fixture.Players.Select((player, index) => new ActionParticipantSnapshot(player.ToString("D"),
                $"迁移成员{index}", 1, 0, 0, 100, 50, stats, [], CombatSkills: PlayerCombatSkillState.Empty)).ToArray())
        { ActionId = new ActionId("combat.hunt-qi-beast") };
        var catalog = GameDataCatalog.LoadEmbedded();
        var old = JsonNode.Parse(JsonSerializer.Serialize(ActionParticipationSessionEngine.Create(spec, catalog).Export(), PersistenceJson.Options))!.AsObject();
        old["sequence"] = 1;
        old["expedition"]!["playerHitpoints"] = 0;
        foreach (var field in new[] { "inventoryMode", "modeSinceUnixSeconds", "nextConsumableEvaluationAtUnixSeconds", "offlineBatchBoundaryUnixSeconds", "offlineReserves" })
            old.Remove(field);
        var potion = "item.test-migration-potion";
        var durable = "item.test-migration-durable";
        old["spec"]!["members"]![0]!["consumableSlots"] = JsonSerializer.SerializeToNode(new[]
        {
            new { itemId = potion, actionType = "combat", group = "potion", slotIndex = 0, cooldownUntilUnixSeconds = seconds + 12, sustainedRemainingSeconds = 30d, escrowQuantity = 7d },
            new { itemId = potion, actionType = "combat", group = "potion", slotIndex = 1, cooldownUntilUnixSeconds = seconds + 15, sustainedRemainingSeconds = 0d, escrowQuantity = 0d }
        });
        old["spec"]!["members"]![1]!["consumableSlots"] = JsonSerializer.SerializeToNode(new[]
        {
            new { itemId = durable, actionType = "combat", group = "general", slotIndex = 0, cooldownUntilUnixSeconds = 0d, sustainedRemainingSeconds = 0d, escrowQuantity = 1d }
        });
        old["consumableLedger"] = JsonSerializer.SerializeToNode(new[]
        {
            new { memberIndex = 0, itemId = potion, group = "potion", slotIndex = 0, remainingQuantity = 3d, cooldownUntilUnixSeconds = seconds + 12, sustainedExpiresAtUnixSeconds = seconds + 30 },
            new { memberIndex = 0, itemId = potion, group = "potion", slotIndex = 1, remainingQuantity = 4d, cooldownUntilUnixSeconds = seconds + 15, sustainedExpiresAtUnixSeconds = 0d },
            new { memberIndex = 1, itemId = durable, group = "general", slotIndex = 0, remainingQuantity = 1d, cooldownUntilUnixSeconds = 0d, sustainedExpiresAtUnixSeconds = 0d }
        });
        var endedId = fixture.SessionId + ":ended";
        var invalidId = fixture.SessionId + ":invalid";
        var ended = old.DeepClone();
        ended["spec"]!["sessionId"] = endedId;
        ended["endsAtUnixSeconds"] = seconds;
        var invalid = old.DeepClone();
        invalid["spec"]!["sessionId"] = invalidId;
        invalid["consumableLedger"]![0]!["memberIndex"] = 99;

        // 所有播种与降版本均位于最终回滚的事务内，不改共享集成库的其他会话证据。
        await ExecuteAsync("UPDATE social.party_sessions SET spec=$1::jsonb,runtime_state=NULL,last_simulated_at=NULL,ended_at=coalesce(ended_at,started_at)", old["spec"]!.ToJsonString());
        await ExecuteAsync("UPDATE game.player_runtime_leases SET lease_until=now()-interval '1 second'; UPDATE market.partition_leases SET expires_at=now()-interval '1 second'; UPDATE platform.compute_jobs SET lease_until=now()-interval '1 second' WHERE status='leased'; DELETE FROM platform.schema_version WHERE version>36");
        await ExecuteAsync("UPDATE social.party_sessions SET spec=$2::jsonb,runtime_state=$3::jsonb,state_version=4,last_simulated_at=$4,ended_at=NULL,end_reason=NULL WHERE session_id=$1",
            fixture.SessionId, old["spec"]!.ToJsonString(), old.ToJsonString(), at);
        await ExecuteAsync("INSERT INTO social.party_sessions(session_id,party_id,spec,started_at,runtime_state,state_version,last_simulated_at,ended_at) VALUES($1,$2,$3::jsonb,$4,$5::jsonb,4,$4,$4)",
            endedId, Guid.NewGuid(), ended["spec"]!.ToJsonString(), at, ended.ToJsonString());
        await ExecuteAsync("INSERT INTO social.party_sessions(session_id,party_id,spec,started_at,runtime_state,state_version,last_simulated_at) VALUES($1,$2,$3::jsonb,$4,$5::jsonb,4,$4)",
            invalidId, Guid.NewGuid(), invalid["spec"]!.ToJsonString(), at, invalid.ToJsonString());
        await ExecuteAsync("INSERT INTO economy.player_asset_balances(player_id,asset_id,variant,quantity,version,updated_at) VALUES($1,$2,'',2,5,$3)", fixture.Players[0], potion, at);
        const string pendingReward = "{\"assetDeltas\":[{\"quantity\":11}],\"legacyEscrowRefund\":5}";
        foreach (var sessionId in new[] { fixture.SessionId, endedId })
            await ExecuteAsync("INSERT INTO social.party_session_inbox(session_id,player_id,sequence,payload,created_at) VALUES($1,$2,1,$3::jsonb,$4)", sessionId, fixture.Players[0], pendingReward, at);
        var oldInbox = (string)(await ScalarAsync("SELECT payload::text FROM social.party_session_inbox WHERE session_id=$1 AND player_id=$2 AND sequence=1", endedId, fixture.Players[0]))!;
        var migration = ReadMigrationSql();
        await transaction.SaveAsync("before_migration");
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(migration));
        await transaction.RollbackAsync("before_migration");
        Assert.Equal(2d, await ScalarAsync("SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id=$2", fixture.Players[0], potion));
        Assert.Equal(36, await ScalarAsync("SELECT max(version) FROM platform.schema_version"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM economy.transactions WHERE idempotency_key=$1", "migration.v37.refund:" + fixture.SessionId));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM social.party_session_inbox WHERE session_id=$1", fixture.SessionId));

        await ExecuteAsync("UPDATE social.party_sessions SET runtime_state=NULL,last_simulated_at=NULL WHERE session_id=$1", invalidId);
        await ExecuteAsync(migration);
        await ExecuteAsync(migration);
        Assert.Equal(37, await ScalarAsync("SELECT max(version) FROM platform.schema_version"));
        Assert.Equal(9d, await ScalarAsync("SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id=$2", fixture.Players[0], potion));
        Assert.Equal(6L, await ScalarAsync("SELECT version FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id=$2", fixture.Players[0], potion));
        Assert.Equal(1d, await ScalarAsync("SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id=$2", fixture.Players[1], durable));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM economy.transactions WHERE idempotency_key=$1", "migration.v37.refund:" + fixture.SessionId));
        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM economy.transaction_lines l JOIN economy.transactions t USING(transaction_id) WHERE t.idempotency_key=$1", "migration.v37.refund:" + fixture.SessionId));
        Assert.Equal(oldInbox, await ScalarAsync("SELECT (payload-'consumableSlots')::text FROM social.party_session_inbox WHERE session_id=$1 AND player_id=$2 AND sequence=1", endedId, fixture.Players[0]));
        Assert.Equal(2, await ScalarAsync("SELECT jsonb_array_length(payload->'consumableSlots') FROM social.party_session_inbox WHERE session_id=$1 AND player_id=$2 AND sequence=1", endedId, fixture.Players[0]));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM social.party_session_inbox WHERE session_id=$1", endedId));
        Assert.Equal(at.UtcDateTime, await ScalarAsync("SELECT ended_at FROM social.party_sessions WHERE session_id=$1", invalidId));
        var checkpoint = JsonSerializer.Deserialize<ActionParticipationSessionDocument>((string)(await ScalarAsync("SELECT runtime_state::text FROM social.party_sessions WHERE session_id=$1", fixture.SessionId))!, PersistenceJson.Options)!;
        Assert.Equal(2, checkpoint.Sequence);
        Assert.Equal(seconds, checkpoint.EndsAtUnixSeconds);
        Assert.Empty(checkpoint.OfflineReserves!);
        Assert.Equal(3, checkpoint.ConsumableLedger!.Count);
        Assert.Equal(seconds + 30, checkpoint.ConsumableLedger[0].SustainedExpiresAtUnixSeconds);
        Assert.Equal(1, checkpoint.Expedition!.PlayerHitpoints);
        var payload = JsonSerializer.Deserialize<ActionParticipationMemberSettlement>((string)(await ScalarAsync("SELECT payload::text FROM social.party_session_inbox WHERE session_id=$1 AND player_id=$2 AND sequence=2", fixture.SessionId, fixture.Players[0]))!, PersistenceJson.Options)!;
        Assert.Equal(fixture.Players[0], payload.PlayerId.Value);
        Assert.Equal(1, payload.Hitpoints);
        Assert.Empty(payload.AssetDeltas);
        Assert.Equal(2, payload.ConsumableSlots!.Count);
        Assert.Equal(seconds + 12, payload.ConsumableSlots[0].CooldownUntilUnixSeconds);
        Assert.Equal(seconds, payload.EndsAtUnixSeconds);
        Assert.Equal(SectionCodec.CurrentSchemaVersion, await ScalarAsync("SELECT schema_version FROM game.player_state_sections WHERE player_id=$1 LIMIT 1", fixture.Players[0]));
        await transaction.RollbackAsync();

        async Task ExecuteAsync(string sql, params object[] values)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            await command.ExecuteNonQueryAsync();
        }

        async Task<object?> ScalarAsync(string sql, params object[] values)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            return await command.ExecuteScalarAsync();
        }
    }

    private static string ReadMigrationSql()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "migrations", "0037_v36_to_v37.sql")))
            directory = directory.Parent;
        var path = Path.Combine((directory ?? throw new DirectoryNotFoundException("找不到会话钱包迁移脚本。")).FullName, "migrations", "0037_v36_to_v37.sql");
        return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\nBEGIN;\n", "\n", StringComparison.Ordinal).Replace("\nCOMMIT;", "\n", StringComparison.Ordinal);
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
