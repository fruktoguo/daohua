using System.Text.Json;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Domain.Social;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Protocol;
using Server.Clean;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>对弈八阶全局匹配、十倍底注验资托管担保金与独立离开结算面板集成测试。</summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class DouDiZhuEscrowTests
{
    [Fact]
    public async Task CreateEscrowsTenFoldBaseStakeWithoutPartyAndSettlementRecoversGuarantee()
    {
        var source = PostgresTestSchema.GetSharedDataSource(PostgresTestSchema.RequireConnectionString("斗地主担保测试"));
        await PostgresTestSchema.EnsureAsync(source);
        var repo = new PostgresSocialRepository(source);
        var economy = new PostgresEconomyRepository(source, NullLogger<PostgresEconomyRepository>.Instance);

        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var p3 = Guid.NewGuid();
        var players = new[] { p1, p2, p3 };
        await SeedIdentityAsync(source, players);

        // 二阶（黄阶）：底注 100，入场十倍验资/托管门槛 = 1,000。先令 p2 差 1 灵石验证原子拒单。
        const int tier = 2;
        var baseStake = DouDiZhuRules.GetBaseStake(tier);
        var requiredEscrow = DouDiZhuRules.GetRequiredStones(tier);
        Assert.Equal(100d, baseStake);
        Assert.Equal(1_000d, requiredEscrow);

        await SeedStonesAsync(economy, p1, 1_500d);
        await SeedStonesAsync(economy, p2, 999d);
        await SeedStonesAsync(economy, p3, 1_500d);

        var now = DateTimeOffset.UtcNow;
        var rejectedGame = DouDiZhuGame.Create(Guid.NewGuid(), tier, players, now);
        var rejected = await repo.TryCreateDouDiZhuGameAsync(rejectedGame);
        Assert.False(rejected.Created);
        Assert.NotNull(rejected.InsufficientPlayerIds);
        Assert.Contains(p2, rejected.InsufficientPlayerIds!);
        Assert.Equal(1_500d, await ReadStoneAsync(source, p1));
        Assert.Equal(999d, await ReadStoneAsync(source, p2));
        Assert.Equal(1_500d, await ReadStoneAsync(source, p3));

        // 补足 p2 至 1,500 灵石，三人均不处于任何队伍，直接全局建局并预扣十倍底注（1,500 -> 500）。
        await SeedStonesAsync(economy, p2, 501d);
        foreach (var pid in players)
            Assert.Null(await repo.GetPartyIdAsync(pid));

        var game = DouDiZhuGame.Create(Guid.NewGuid(), tier, players, now);
        var created = await repo.TryCreateDouDiZhuGameAsync(game);
        Assert.True(created.Created);

        foreach (var pid in players)
            Assert.Equal(500d, await ReadStoneAsync(source, pid));

        // 模拟两名农民在局内把剩余自由灵石全部花光（500 -> 0）。
        var landlord = players[0];
        var farmerA = players[1];
        var farmerB = players[2];
        await SeedStonesAsync(economy, farmerA, -500d);
        await SeedStonesAsync(economy, farmerB, -500d);

        // 地主胜出，倍率 = 16（理论应收 1,600/人，超出十倍托管），农民自由余额为 0，最多从每人十倍托管金扣满 1,000。
        var finished = game with
        {
            Phase = DouDiZhuGamePhase.Finished,
            LandlordSeat = 0,
            WinnerSeat = 0,
            LandlordWon = true,
            FinalMultiplier = 16,
            Version = game.Version + 1,
            UpdatedAt = now.AddMinutes(1)
        };
        Assert.True(await repo.TryCommitDouDiZhuGameAsync(finished, game.Version));

        // 地主原本剩 500 + 退回托管 1,000 + 赢取 2,000 = 3,500；两名农民终局余额归零，严格零和。
        Assert.Equal(3_500d, await ReadStoneAsync(source, landlord));
        Assert.Equal(0d, await ReadStoneAsync(source, farmerA));
        Assert.Equal(0d, await ReadStoneAsync(source, farmerB));

        // 三人独立关闭赛后结算面板：p1 离开后仅本人不可见，全员离开后终态行立即清理。
        foreach (var pid in players)
            Assert.Equal(game.GameId, (await repo.GetVisibleDouDiZhuGameAsync(pid))?.GameId);

        Assert.True(await repo.TryDismissFinishedDouDiZhuGameAsync(p1, game.GameId));
        Assert.Null(await repo.GetVisibleDouDiZhuGameAsync(p1));
        Assert.Equal(game.GameId, (await repo.GetVisibleDouDiZhuGameAsync(p2))?.GameId);
        Assert.Equal(game.GameId, (await repo.GetVisibleDouDiZhuGameAsync(p3))?.GameId);

        Assert.True(await repo.TryDismissFinishedDouDiZhuGameAsync(p2, game.GameId));
        Assert.True(await repo.TryDismissFinishedDouDiZhuGameAsync(p3, game.GameId));
        Assert.Null(await repo.GetVisibleDouDiZhuGameAsync(p2));
        Assert.Null(await repo.GetVisibleDouDiZhuGameAsync(p3));
        Assert.Equal(0L, await CountGameRowsAsync(source, game.GameId));
    }

    [Fact]
    public async Task GlobalMatchmaking_QueuesByTierEvictsInsufficientWaiterAndAutoStartsAtThreePlayers()
    {
        var source = PostgresTestSchema.GetSharedDataSource(PostgresTestSchema.RequireConnectionString("斗地主全局匹配测试"));
        await PostgresTestSchema.EnsureAsync(source);
        var catalog = GameDataCatalog.LoadEmbedded();
        var repo = new PostgresSocialRepository(source);
        var sections = new PostgresSectionRepository(source, NullLogger<PostgresSectionRepository>.Instance);
        var chatRepo = new PostgresChatRepository(source);
        var assetRepo = new PostgresAssetRepository(source);
        var economy = new PostgresEconomyRepository(source, NullLogger<PostgresEconomyRepository>.Instance);
        var presence = new OnlinePresenceRegistry();
        var runtimeStub = new NullPlayerRuntime();
        var valuation = new PartyValuationService(runtimeStub, sections, null, catalog);
        var chat = new ChatRuntime(chatRepo, repo, presence);
        var social = new SocialRuntime(
            repo,
            presence,
            valuation,
            chat,
            catalog,
            NullLogger<SocialRuntime>.Instance,
            sections: sections,
            assetBalances: assetRepo);

        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var p3 = Guid.NewGuid();
        var p4 = Guid.NewGuid();
        var players = new[] { p1, p2, p3, p4 };
        await SeedIdentityAsync(source, players);
        foreach (var pid in players)
            await presence.RegisterAsync(pid, pid.ToString("N")[..8], null);

        // 三阶（玄阶）：底注 10,000，十倍验资 = 100,000。
        const int tier = 3;
        await SeedStonesAsync(economy, p1, 500_000d);
        await SeedStonesAsync(economy, p2, 500_000d);
        await SeedStonesAsync(economy, p3, 500_000d);
        await SeedStonesAsync(economy, p4, 99_999d);

        // p4 差 1 灵石，入队验资直接拦截。
        var lowOutcome = await social.ApplyAsync(p4, new SocialActionDto("r0", SocialActions.DouDiZhuMatchJoin, DouDiZhuTier: tier));
        Assert.False(lowOutcome.Success);
        Assert.Equal(SocialFailure.DouDiZhuUnavailable, lowOutcome.Failure);

        // p1、p2 依次进入玄阶队列。
        Assert.True((await social.ApplyAsync(p1, new SocialActionDto("r1", SocialActions.DouDiZhuMatchJoin, DouDiZhuTier: tier))).Success);
        Assert.Equal(tier, social.GetDouDiZhuMatchingTier(p1));
        Assert.Equal(1, social.GetDouDiZhuQueueCounts()[tier - 1]);

        Assert.True((await social.ApplyAsync(p2, new SocialActionDto("r2", SocialActions.DouDiZhuMatchJoin, DouDiZhuTier: tier))).Success);
        Assert.Equal(2, social.GetDouDiZhuQueueCounts()[tier - 1]);

        // p2 在排队期间花掉灵石导致跌破十倍底注（500,000 -> 50,000）。
        await SeedStonesAsync(economy, p2, -450_000d);

        // p3 入队触发凑满 3 人复核：仅剔除余额不足的 p2，保留 p1 与 p3 继续排队。
        Assert.True((await social.ApplyAsync(p3, new SocialActionDto("r3", SocialActions.DouDiZhuMatchJoin, DouDiZhuTier: tier))).Success);
        Assert.Null(social.GetDouDiZhuMatchingTier(p2));
        Assert.Equal(tier, social.GetDouDiZhuMatchingTier(p1));
        Assert.Equal(tier, social.GetDouDiZhuMatchingTier(p3));
        Assert.Equal(2, social.GetDouDiZhuQueueCounts()[tier - 1]);

        // p4 补齐 1 灵石（恰满 100,000）入队，凑齐 p1/p3/p4 自动开局并预扣十倍底注。
        await SeedStonesAsync(economy, p4, 1d);
        Assert.True((await social.ApplyAsync(p4, new SocialActionDto("r4", SocialActions.DouDiZhuMatchJoin, DouDiZhuTier: tier))).Success);
        Assert.Equal(0, social.GetDouDiZhuQueueCounts()[tier - 1]);
        Assert.Null(social.GetDouDiZhuMatchingTier(p1));
        Assert.Null(social.GetDouDiZhuMatchingTier(p3));
        Assert.Null(social.GetDouDiZhuMatchingTier(p4));

        var started = await repo.GetVisibleDouDiZhuGameAsync(p1);
        Assert.NotNull(started);
        Assert.Equal(tier, started!.Tier);
        Assert.Equal(10_000d, started.BaseStake);
        Assert.Equal(100_000d, started.EscrowedStake);
        Assert.Equal(DouDiZhuGamePhase.Bidding, started.Phase);
        Assert.Equal(0d, await ReadStoneAsync(source, p4));
        Assert.Equal(400_000d, await ReadStoneAsync(source, p1));
        Assert.Equal(400_000d, await ReadStoneAsync(source, p3));
    }

    private static async Task SeedIdentityAsync(NpgsqlDataSource source, Guid[] players)
    {
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
    }

    private static async Task SeedStonesAsync(PostgresEconomyRepository economy, Guid playerId, double delta)
    {
        var txId = Guid.NewGuid();
        await economy.CommitAsync(new EconomyTransactionRequest(
            txId, $"seed:{txId:N}", "test.seed", playerId,
            [new AssetDelta(playerId, "currency.spirit-stone", string.Empty, delta)]));
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

    private static async Task<long> CountGameRowsAsync(NpgsqlDataSource source, Guid gameId)
    {
        await using var cmd = source.CreateCommand("SELECT count(*) FROM social.doudizhu_games WHERE game_id = $1");
        cmd.Parameters.AddWithValue(gameId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private sealed class NullPlayerRuntime : IPlayerRuntime
    {
        public int LoadedCount => 0;
        public DateTimeOffset? NextAdvanceBoundaryUtc => null;
#pragma warning disable CS0067
        public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated;
        public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted;
#pragma warning restore CS0067
        public PlayerRuntimeSnapshot? TryGetLoaded(Guid playerId) => null;
        public ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerRuntimeSnapshot> AttachConnectionAsync(Guid playerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, long expectedStateVersion, Guid requestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask FlushAsync(Guid playerId, bool force, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask FlushAllAsync(bool force, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(Guid playerId, bool flush, DateTimeOffset? offlineSince, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<PlayerRuntimeSnapshot> MutateOwnedAsync(Guid playerId, Action<PlayerAggregate> mutate, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId) => [];
        public ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<PlayerRuntimeSnapshot>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
