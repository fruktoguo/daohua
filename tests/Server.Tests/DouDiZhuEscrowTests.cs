using Domain.Social;
using Npgsql;
using Server.Clean;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>斗地主开局托管担保金与活跃牌局防逐出集成测试。</summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class DouDiZhuEscrowTests
{
    [Fact]
    public async Task CreateEscrowsStakeAndSettlementRecoversGuaranteeEvenWhenLosersSpendFreeBalance()
    {
        var source = PostgresTestSchema.GetSharedDataSource(PostgresTestSchema.RequireConnectionString("斗地主担保测试"));
        await PostgresTestSchema.EnsureAsync(source);
        var repo = new PostgresSocialRepository(source);
        var economy = new PostgresEconomyRepository(source, Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresEconomyRepository>.Instance);

        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var p3 = Guid.NewGuid();
        var players = new[] { p1, p2, p3 };

        foreach (var sql in new[]
        {
            """
            INSERT INTO identity.accounts(account_id, login_normalized, password_hash, password_salt, password_iterations, created_at, updated_at)
            SELECT id, id::text, '\x00'::bytea, '\x00'::bytea, 100000, now(), now() FROM unnest($1::uuid[]) AS t(id)
            """,
            """
            INSERT INTO identity.players(player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at)
            SELECT id, id, left(id::text, 12), id::text, now(), now() FROM unnest($1::uuid[]) AS t(id)
            """,
            """
            INSERT INTO game.player_projection(player_id, offline_since, presence, last_active_at, updated_at)
            SELECT id, now() - interval '72 hours', 'offline', now(), now() FROM unnest($1::uuid[]) AS t(id)
            """
        })
        {
            await using var cmd = source.CreateCommand(sql);
            cmd.Parameters.AddWithValue(players);
            await cmd.ExecuteNonQueryAsync();
        }

        const double initialStone = 50d;
        const double stake = 20d;
        foreach (var pid in players)
        {
            await economy.CommitAsync(new EconomyTransactionRequest(
                Guid.NewGuid(), $"seed:{pid:D}", "test.seed", pid,
                [new AssetDelta(pid, "currency.spirit-stone", string.Empty, initialStone)]));
        }

        var now = DateTimeOffset.UtcNow;
        var partyId = await repo.CreatePartyAsync(p1, PartyProfileRecord.Default, now);
        Assert.Equal(PartyJoinResult.Joined, await repo.TryJoinPartyAsync(partyId, p2, now));
        Assert.Equal(PartyJoinResult.Joined, await repo.TryJoinPartyAsync(partyId, p3, now));
        Assert.True(await repo.TryUpdateDouDiZhuStakeAsync(partyId, p1, stake, now));
        foreach (var pid in players) Assert.True(await repo.TrySetDouDiZhuReadyAsync(pid, true));

        var loadedParty = await repo.GetPartyAsync(partyId);
        Assert.NotNull(loadedParty);
        var orderedPlayers = loadedParty!.Members.Select(m => m.PlayerId).ToArray();
        var game = DouDiZhuGame.Create(Guid.NewGuid(), partyId, orderedPlayers, stake, now);

        Assert.True(await repo.TryCreateDouDiZhuGameAsync(game, p1));

        // 开局后三人可用灵石均应扣除 stake（50 -> 30），且 48h 离线清理不得逐出活跃牌局成员。
        foreach (var pid in orderedPlayers)
            Assert.Equal(initialStone - stake, await ReadStoneAsync(source, pid));
        var evicted = await repo.DeleteInactivePartyMembersAsync(now.AddHours(-48));
        Assert.DoesNotContain(evicted, item => item.PartyId == partyId);

        // 模拟两名农民在局内把剩余自由灵石全部花光（30 -> 0）。
        var landlord = orderedPlayers[0];
        var farmerA = orderedPlayers[1];
        var farmerB = orderedPlayers[2];
        await economy.CommitAsync(new EconomyTransactionRequest(
            Guid.NewGuid(), $"drain:{farmerA:D}", "test.drain", farmerA,
            [new AssetDelta(farmerA, "currency.spirit-stone", string.Empty, -(initialStone - stake))]));
        await economy.CommitAsync(new EconomyTransactionRequest(
            Guid.NewGuid(), $"drain:{farmerB:D}", "test.drain", farmerB,
            [new AssetDelta(farmerB, "currency.spirit-stone", string.Empty, -(initialStone - stake))]));

        // 地主胜出，倍率 = 2（claim = 40/人），但农民自由余额已为 0，仍必须从开局托管金中赔付每人 20（地主净赢 +40）。
        var finished = game with
        {
            Phase = DouDiZhuGamePhase.Finished,
            LandlordSeat = 0,
            WinnerSeat = 0,
            LandlordWon = true,
            FinalMultiplier = 2,
            Version = game.Version + 1,
            UpdatedAt = now.AddMinutes(1)
        };
        Assert.True(await repo.TryCommitDouDiZhuGameAsync(finished, game.Version));

        // 地主原本剩 30 + 退回托管 20 + 赢取 40 = 90；两名农民自由余额为 0 且托管金全赔，终局余额为 0。
        Assert.Equal(90d, await ReadStoneAsync(source, landlord));
        Assert.Equal(0d, await ReadStoneAsync(source, farmerA));
        Assert.Equal(0d, await ReadStoneAsync(source, farmerB));
    }

    private static async Task<double> ReadStoneAsync(NpgsqlDataSource source, Guid playerId)
    {
        await using var cmd = source.CreateCommand("""
            SELECT quantity FROM economy.player_asset_balances
            WHERE player_id = $1 AND asset_id = 'currency.spirit-stone' AND variant = ''
            """);
        cmd.Parameters.AddWithValue(playerId);
        var result = await cmd.ExecuteScalarAsync();
        return result is double value ? value : 0d;
    }
}
