using System.Collections.Concurrent;
using System.Reflection;
using DaoHua.Ecs;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 资产事务「COMMIT 结局未知」后的恢复门禁（PostgreSQL）：COMMIT 已发出但连接中断时，
/// 已知成功只读查证，查无记录与失租均不能触发旧请求补交。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class UnknownCommitRecoveryTests
{
    [Fact]
    public async Task LookupConfirmsExistingCommitWithoutDoubleApplying()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("结局核实集成测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var player = Guid.NewGuid();
        await Sql(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())", player, player.ToString());
        await Sql(source, "INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,invite_code,created_at,last_seen_at,status) VALUES ($1,$1,$2,$2,(left(replace($1::text,'-',''),16)),now(),now(),1)", player, player.ToString());
        await Sql(source, "INSERT INTO economy.player_asset_balances VALUES ($1,'currency.spirit-stone','',5,1,now())", player);
        var repository = new PostgresEconomyRepository(source, NullLogger<PostgresEconomyRepository>.Instance);
        var now = DateTimeOffset.UtcNow;
        var transactionId = Guid.NewGuid();
        var request = new EconomyTransactionRequest(transactionId, $"fence:{transactionId:D}", "probe.fence", player,
            [new(player, "currency.spirit-stone", string.Empty, 3)], now, ReadOnlyMemory<byte>.Empty);

        Assert.Null(await repository.FindCommittedAsync(request));
        Assert.Equal(5d, await Scalar(source, "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1", player));
        var first = await repository.CommitAsync(request);
        Assert.True(first.Applied);
        Assert.False(first.AlreadyCommitted);

        // 核实只读，不经 CommitAsync；命中账本仍校验原请求指纹。
        var replay = Assert.IsType<EconomyTransactionResult>(await repository.FindCommittedAsync(request));
        Assert.True(replay.Applied);
        Assert.True(replay.AlreadyCommitted);
        Assert.Equal(transactionId, replay.TransactionId);
        Assert.Equal(8d, Convert.ToDouble(await Scalar(source, "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1", player)));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM economy.transactions WHERE idempotency_key=$1", request.IdempotencyKey));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM economy.transaction_lines WHERE transaction_id=$1", transactionId));
    }

    [Fact]
    public async Task PendingUnknownCheckpoint_RealTakeoverNeverReexecutesOldRequest()
    {
        var cs = PostgresTestSchema.RequireConnectionString("未知提交接管回归");
        await using var source = NpgsqlDataSource.Create(cs);
        await PostgresTestSchema.EnsureAsync(source);
        var player = Guid.NewGuid();
        var t0 = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await Sql(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())", player, player.ToString());
        await Sql(source, """
            INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,invite_code,created_at,last_seen_at,status)
            VALUES ($1,$1,$2,$2,left(replace($1::text,'-',''),16),$3,$3,1)
            """, player, player.ToString("N"), t0);
        var catalog = GameDataCatalog.LoadEmbedded();
        var original = PlayerAggregate.Create(new PlayerId(player), new AccountId(player), player.ToString("N"), t0, catalog.Fingerprint);
        original.SkipTutorial();
        original.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), -1), t0, catalog.Actions);
        var sections = new PostgresSectionRepository(source, NullLogger<PostgresSectionRepository>.Instance);
        await sections.EnqueueSectionsAsync(SectionCodec.EncodeAggregateFull(original.ExportState(), t0));
        var ownership = new PostgresPlayerOwnershipRepository(source);
        var repository = new PostgresEconomyRepository(source, NullLogger<PostgresEconomyRepository>.Instance);
        var injector = new UncommittedFaultEconomy(repository);
        using var ecs = new EcsGameRuntime(catalog, t0);
        var target = t0.AddMinutes(15);
        await using var old = new EcsPlayerRuntimeAdapter(ecs, sections, new PostgresAssetRepository(source), injector,
            catalog, NullLogger<EcsPlayerRuntimeAdapter>.Instance, new FixedClock(target), ownership: ownership);

        var unknown = await Assert.ThrowsAsync<EconomyCommitOutcomeUnknownException>(() => old.LoadAsync(player).AsTask());
        var originalAttempt = Assert.IsType<EconomyTransactionRequest>(injector.FirstRequest);
        var oldOwner = Assert.IsType<Guid>(originalAttempt.RequiredRuntimeOwner);
        await Sql(source, "UPDATE game.player_runtime_leases SET lease_until = now() - interval '1 minute' WHERE player_id = $1", player);
        await Assert.ThrowsAsync<PlayerOwnershipLostException>(() => repository.CommitAsync(originalAttempt).AsTask());
        var successor = Guid.NewGuid();
        Assert.True(await ownership.AcquireAsync(player, successor, TimeSpan.FromSeconds(45)));

        // 只缩短进程内续租去抖，不改 PostgreSQL 的真实租约判断或接管路径。
        var debounce = Assert.IsType<ConcurrentDictionary<Guid, DateTimeOffset>>(
            typeof(EcsPlayerRuntimeAdapter).GetField("_leaseRenewedAt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(old));
        debounce[player] = DateTimeOffset.UtcNow.AddMinutes(-1);
        await old.AdvanceAllAsync(target.AddSeconds(1));
        await old.AdvanceAllAsync(target.AddSeconds(2));
        Assert.Null(old.TryGetLoaded(player));
        Assert.Same(unknown, await Assert.ThrowsAsync<EconomyCommitOutcomeUnknownException>(() => old.LoadAsync(player).AsTask()));
        Assert.Equal(true, unknown.Data["OwnershipLost"]);
        Assert.Equal(1, injector.CommitCalls);
        Assert.Null(await repository.FindCommittedAsync(originalAttempt));
        await Assert.ThrowsAsync<PlayerOwnershipLostException>(() => repository.CommitAsync(originalAttempt).AsTask());
        Assert.Equal(successor, await Scalar(source, "SELECT owner_instance_id FROM game.player_runtime_leases WHERE player_id=$1", player));
        Assert.Equal(0L, await Scalar(source, "SELECT count(*) FROM economy.player_asset_balances WHERE player_id=$1", player));
        Assert.Equal(original.StateVersion, (await sections.LoadAsync(player))!.Head.StateVersion);
    }

    private sealed class UncommittedFaultEconomy(PostgresEconomyRepository repository) : IEconomyTransactionStore
    {
        public EconomyTransactionRequest? FirstRequest;
        public int CommitCalls;
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
        {
            if (++CommitCalls == 1)
            {
                FirstRequest = request;
                throw new EconomyCommitOutcomeUnknownException(request.TransactionId, new IOException("原事务实际未提交"));
            }
            return repository.CommitAsync(request, cancellationToken);
        }
        public ValueTask<EconomyTransactionResult?> FindCommittedAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
            => repository.FindCommittedAsync(request, cancellationToken);
    }

    private sealed class FixedClock(DateTimeOffset time) : IPlayerSimulationClock
    {
        private DateTimeOffset _time = time;
        public DateTimeOffset UtcNow => _time;
        public bool IsDeterministic => true;
        public void AdvanceTo(DateTimeOffset target) => _time = target;
    }

    private static async Task<object?> Scalar(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return await command.ExecuteScalarAsync();
    }

    private static async Task Sql(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync();
    }
}
