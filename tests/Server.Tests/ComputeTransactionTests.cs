using System.Data;
using System.Reflection;
using System.Text.Json;
using DaoHua.ComputeNode;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Domain.Simulation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>可信重演之后的事务边界：合法源漂移、任务 fence、Complete 失败及 ownership MVCC fence。</summary>
[Collection("PostgresIntegration")]
public sealed class ComputeTransactionTests
{
    private static readonly GameDataCatalog Catalog = GameDataCatalog.LoadEmbedded();

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task HonestResultCommitsSectionsRewardsAndTaskAtomically()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal(ComputeApplyOutcome.Applied, await fixture.CommitAsync());
        Assert.True((long)(await fixture.ScalarAsync("SELECT state_version FROM game.player_state_heads WHERE player_id = $1", fixture.PlayerId))! > fixture.Request.SourceStateVersion);
        Assert.Equal(0d, await fixture.ScalarAsync("SELECT quantity FROM economy.player_asset_balances WHERE player_id = $1 AND asset_id = 'item.ningxue-grass'", fixture.PlayerId));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT count(*) FROM economy.transactions WHERE transaction_id = $1", fixture.Claim.JobId));
        Assert.Equal("succeeded", await fixture.ScalarAsync("SELECT status FROM platform.compute_jobs WHERE job_id = $1", fixture.Claim.JobId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "PostgresIntegration")]
    public async Task DuplicateOrExtraBatchResultsRejectWholeBatch(bool duplicate)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var worker = fixture.CreateWorker();
        var node = new ComputeLinkNode("transaction-test", 1);
        var method = typeof(ComputeDispatcherWorker).GetMethod("RunBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dispatch = (Task)method.Invoke(worker, [node, new[] { fixture.Claim }, CancellationToken.None])!;
        var outbound = await node.Outbound.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var batch = (ComputeLinkBatch)ComputeLinkCodec.Decode(outbound);
        var extra = duplicate ? fixture.Result : fixture.Result with { JobId = Guid.NewGuid() };
        Assert.True(node.CompleteResult(batch.BatchId, new ComputeAdvanceBatchResponse(
            ComputeDispatchContract.Version, Catalog.Fingerprint, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [fixture.Result, extra])));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("dead", await fixture.ScalarAsync("SELECT status FROM platform.compute_jobs WHERE job_id = $1", fixture.Claim.JobId));
        Assert.Equal(fixture.Request.SourceStateVersion, await fixture.ScalarAsync("SELECT state_version FROM game.player_state_heads WHERE player_id = $1", fixture.PlayerId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task TerminalDiagnosticSurvivesConsumedRetryAndFailureThrottle()
    {
        await using var fixture = await Fixture.CreateAsync();
        var logger = new TerminalLogger();
        using var worker = fixture.CreateWorker(logger);
        // 复现容量场景：预期 SSI 重试与基础设施错误刚占满节流窗口，紧接着真实任务判死。
        foreach (var name in new[] { "_serializationFailureLog", "_failureLog" })
        {
            var throttle = (LogThrottle)typeof(ComputeDispatcherWorker).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
            Assert.True(throttle.TryAcquire());
        }
        var method = typeof(ComputeDispatcherWorker).GetMethod("RunBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var badClaim = fixture.Claim with { InputPayload = System.Text.Encoding.UTF8.GetBytes("payload-secret-not-for-logs") };
        await (Task)method.Invoke(worker, [new ComputeLinkNode("diagnostic-test", 1), new[] { badClaim }, CancellationToken.None])!;
        Assert.Equal("dead", await fixture.ScalarAsync("SELECT status FROM platform.compute_jobs WHERE job_id = $1", fixture.Claim.JobId));
        var log = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal(fixture.Claim.JobId, log.State["JobId"]);
        Assert.Equal("INVALID_INPUT", log.State["Category"]);
        Assert.Equal(fixture.Claim.Attempts, log.State["Attempts"]);
        Assert.DoesNotContain("payload-secret-not-for-logs", log.Message, StringComparison.Ordinal);
    }

    private sealed class TerminalLogger : ILogger<ComputeDispatcherWorker>
    {
        public List<(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> State)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(row => row.Key, row => row.Value);
            Entries.Add((level, formatter(state, exception), values));
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task WalletVersionAndOnlineDriftDiscardHonestResults()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SqlAsync("UPDATE economy.player_asset_balances SET version = version + 1 WHERE player_id = $1", fixture.PlayerId);
        Assert.Equal(ComputeApplyOutcome.StaleVersion, await fixture.CommitAsync());
        Assert.Equal(fixture.Request.SourceStateVersion, await fixture.ScalarAsync("SELECT state_version FROM game.player_state_heads WHERE player_id = $1", fixture.PlayerId));
        Assert.Equal("succeeded", await fixture.ScalarAsync("SELECT status FROM platform.compute_jobs WHERE job_id = $1", fixture.Claim.JobId));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM economy.transactions WHERE transaction_id = $1", fixture.Claim.JobId));

        await using var online = await Fixture.CreateAsync();
        await online.SqlAsync("UPDATE game.player_projection SET presence = 'online' WHERE player_id = $1", online.PlayerId);
        Assert.Equal(ComputeApplyOutcome.StaleVersion, await online.CommitAsync());
        Assert.Equal(online.Request.SourceStateVersion, await online.ScalarAsync("SELECT state_version FROM game.player_state_heads WHERE player_id = $1", online.PlayerId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task LostLeaseRejectsAllWrites()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SqlAsync("UPDATE platform.compute_jobs SET lease_token = $2 WHERE job_id = $1", fixture.Claim.JobId, Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CommitAsync());
        Assert.Equal(fixture.Request.SourceStateVersion, await fixture.ScalarAsync("SELECT state_version FROM game.player_state_heads WHERE player_id = $1", fixture.PlayerId));
        Assert.Equal(9d, await fixture.ScalarAsync("SELECT quantity FROM economy.player_asset_balances WHERE player_id = $1 AND asset_id = 'item.ningxue-grass'", fixture.PlayerId));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CompleteReturningFalseRollsBackSectionsAndEconomy()
    {
        await using var fixture = await Fixture.CreateAsync();
        var name = "compute_complete_" + Guid.NewGuid().ToString("N");
        // 精确绑定本任务的测试触发器模拟 UPDATE 返回零行；其他任务完全不受影响。
        await fixture.SqlAsync($"""
            CREATE FUNCTION platform.{name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW.job_id = '{fixture.Claim.JobId:D}'::uuid AND NEW.status = 'succeeded' THEN RETURN NULL; END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER {name} BEFORE UPDATE ON platform.compute_jobs
            FOR EACH ROW EXECUTE FUNCTION platform.{name}();
            """);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CommitAsync());
            Assert.Equal(fixture.Request.SourceStateVersion, await fixture.ScalarAsync("SELECT state_version FROM game.player_state_heads WHERE player_id = $1", fixture.PlayerId));
            Assert.Equal(9d, await fixture.ScalarAsync("SELECT quantity FROM economy.player_asset_balances WHERE player_id = $1 AND asset_id = 'item.ningxue-grass'", fixture.PlayerId));
            Assert.Equal(0L, await fixture.ScalarAsync("SELECT count(*) FROM economy.transactions WHERE transaction_id = $1", fixture.Claim.JobId));
            Assert.Equal("leased", await fixture.ScalarAsync("SELECT status FROM platform.compute_jobs WHERE job_id = $1", fixture.Claim.JobId));
        }
        finally
        {
            await fixture.SqlAsync($"DROP TRIGGER {name} ON platform.compute_jobs; DROP FUNCTION platform.{name}();");
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task AcquireInvalidatesOlderSerializableSnapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var connection = await fixture.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        await using (var snapshot = new NpgsqlCommand("SELECT player_id FROM identity.players WHERE player_id = $1", connection, transaction))
        {
            snapshot.Parameters.AddWithValue(fixture.PlayerId);
            Assert.Equal(fixture.PlayerId, await snapshot.ExecuteScalarAsync());
        }
        var ownership = new PostgresPlayerOwnershipRepository(fixture.Source);
        var owner = Guid.NewGuid();
        Assert.True(await ownership.AcquireAsync(fixture.PlayerId, owner, TimeSpan.FromMinutes(1)));
        await using var identityLock = new NpgsqlCommand("SELECT player_id FROM identity.players WHERE player_id = $1 FOR UPDATE", connection, transaction);
        identityLock.Parameters.AddWithValue(fixture.PlayerId);
        var error = await Assert.ThrowsAsync<PostgresException>(() => identityLock.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.SerializationFailure, error.SqlState);
        await transaction.RollbackAsync();
        Assert.Equal(ComputeApplyOutcome.StaleVersion, await fixture.CommitAsync());
        await ownership.ReleaseAsync(fixture.PlayerId, owner);
    }

    private sealed class Fixture(NpgsqlDataSource source, PostgresSectionRepository sections,
        PostgresComputeJobRepository jobs, EcsComputeResultVerifier verifier, ComputeAdvanceJobRequest request,
        ComputeAdvanceJobResult result, ComputeVerificationResult verification, ComputeJobClaim claim) : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; } = source;
        public Guid PlayerId => Request.PlayerId;
        public ComputeAdvanceJobRequest Request { get; } = request;
        public ComputeAdvanceJobResult Result { get; } = result;
        public ComputeJobClaim Claim { get; } = claim;

        public ComputeDispatcherWorker CreateWorker(ILogger<ComputeDispatcherWorker>? logger = null) => new(jobs, sections, Source, new ComputeLinkRegistry(), new ComputeLinkMetrics(),
            new ServerOptions("compute-test", Source.ConnectionString, 256, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1),
                128, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1), 65536, 64, 1, 0, false),
            Catalog, verifier, logger ?? NullLogger<ComputeDispatcherWorker>.Instance);

        public async Task<ComputeApplyOutcome> CommitAsync()
        {
            using var worker = CreateWorker();
            var method = typeof(ComputeDispatcherWorker).GetMethod("CommitResultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var task = (Task<ComputeApplyOutcome>)method.Invoke(worker,
                [Claim, Request, Result, verification.Writes.ToArray(), verification.AssetDeltas.ToArray(), verification.LastSimulatedAt, CancellationToken.None])!;
            return await task;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var source = NpgsqlDataSource.Create(PostgresTestSchema.RequireConnectionString("Compute 事务测试"));
            await PostgresTestSchema.EnsureAsync(source);
            var playerId = Guid.NewGuid();
            var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var baseline = now.AddMinutes(-1);
            await SqlAsync(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,$3,$3)", playerId, playerId.ToString(), baseline);
            await SqlAsync(source, """
                INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,created_at,last_seen_at,status)
                VALUES ($1,$1,$2,$2,$3,$3,1)
                """, playerId, playerId.ToString(), baseline);
            var player = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(playerId), playerId.ToString(), baseline, Catalog.Fingerprint);
            player.SetPresence(PresenceState.Offline);
            player.Enqueue(new ActionIntent(new ActionId("alchemy.refine-qi-pill"), 3), baseline, Catalog.Actions);
            var sections = new PostgresSectionRepository(source, NullLogger<PostgresSectionRepository>.Instance);
            var writes = SectionCodec.EncodeAggregate(player, baseline);
            await sections.EnqueueSectionsAsync(writes);
            await sections.FlushPendingAsync(256, TimeSpan.FromMinutes(1));
            await SqlAsync(source, "INSERT INTO economy.player_asset_balances(player_id,asset_id,variant,quantity,version,updated_at) VALUES ($1,'item.ningxue-grass','',9,1,$2)", playerId, baseline);
            var frozen = (await sections.LoadComputeInputAsync(playerId))!;
            Assert.Equal(1, Assert.Single(frozen.Balances).Version);
            var target = baseline.AddSeconds(40);
            var request = new ComputeAdvanceJobRequest(PostgresComputeJobRepository.DeriveJobId(playerId, target.ToUnixTimeMilliseconds()),
                playerId, playerId, playerId.ToString(), baseline.ToUnixTimeMilliseconds(), frozen.Bundle.Head.StateVersion,
                frozen.Bundle.Head.LastSimulatedAt.ToUnixTimeMilliseconds(), target.ToUnixTimeMilliseconds(), frozen.Bundle.Head.Checksum,
                frozen.Bundle.Head.GameDataFingerprint, frozen.Bundle.Sections.Values.Select(ComputeSectionCodec.ToWire).ToArray(),
                frozen.Balances.Select(row => new ComputeAssetBalance(row.AssetId, row.Variant, row.Quantity, row.Version)).ToArray());
            var jobs = new PostgresComputeJobRepository(source);
            await jobs.EnqueueAsync(playerId, target, JsonSerializer.SerializeToUtf8Bytes(request, ServerJson.Options));
            var claim = Assert.Single(await jobs.ClaimDueAsync(256, TimeSpan.FromMinutes(1)), row => row.PlayerId == playerId);
            using var engine = new DeterministicBatchEngine(Catalog);
            var nodeRequest = JsonSerializer.Deserialize<AdvanceJobRequest>(JsonSerializer.SerializeToUtf8Bytes(request, ServerJson.Options), ServerJson.Options)!;
            var candidate = await engine.AdvanceAsync(nodeRequest);
            Assert.True(candidate.Success, candidate.ErrorMessage);
            var result = JsonSerializer.Deserialize<ComputeAdvanceJobResult>(JsonSerializer.SerializeToUtf8Bytes(candidate, ServerJson.Options), ServerJson.Options)!;
            var verifier = new EcsComputeResultVerifier(Catalog);
            var verification = await verifier.VerifyAsync(request, result);
            Assert.True(verification.Accepted, verification.Error);
            Assert.NotEmpty(verification.AssetDeltas);
            return new Fixture(source, sections, jobs, verifier, request, result, verification, claim);
        }

        public Task SqlAsync(string sql, params object[] values) => SqlAsync(Source, sql, values);
        private static async Task SqlAsync(NpgsqlDataSource source, string sql, params object[] values)
        {
            await using var command = source.CreateCommand(sql);
            foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
            await command.ExecuteNonQueryAsync();
        }
        public async Task<object?> ScalarAsync(string sql, params object[] values)
        {
            await using var command = Source.CreateCommand(sql);
            foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
            return await command.ExecuteScalarAsync();
        }
        public async ValueTask DisposeAsync()
        {
            verifier.Dispose();
            await Source.DisposeAsync();
        }
    }
}
