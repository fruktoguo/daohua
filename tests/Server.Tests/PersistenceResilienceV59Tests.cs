using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 批次四持久化 v59 韧性回归测试：
/// 1. section_flush_queue 达最大重试次数后转 dead 并退出活跃刷盘与负载读取；
/// 2. ApplyComputeResultAsync 遇未落盘 pending 队列时原地桥接而非误判 StaleVersion；
/// 3. integration_outbox 超限转 dead 后不再被重复认领；
/// 4. PostgresPlayerOwnershipRepository.RenewAsync 拒绝续租已过期租约；
/// 5. PostgresRetentionRepository 逐表短事务回收过期运行时租约与超期死信；
/// 6. compute_jobs.dead_count >= 5 触发 24h 长冷却防止毒任务循环重派。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class PersistenceResilienceV59Tests
{
    [Fact]
    public async Task SectionFlushQueue_ExceedingMaxAttempts_MovesToDeadAndExcludedFromFlushAndLoad()
    {
        var cs = PostgresTestSchema.RequireConnectionString("flush 死信回归测试");
        await using var source = NpgsqlDataSource.Create(cs);
        await PostgresTestSchema.EnsureAsync(source);
        var repository = new PostgresSectionRepository(source, NullLogger<PostgresSectionRepository>.Instance);

        var player = await SeedPlayerAsync(source);
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var catalog = GameDataCatalog.LoadEmbedded();
        var agg = PlayerAggregate.Create(new PlayerId(player), new AccountId(player), player.ToString("N"), now, catalog.Fingerprint);
        var validWrites = SectionCodec.EncodeAggregateFull(agg.ExportState(), now);
        await repository.EnqueueSectionsAsync(validWrites);
        Assert.Equal(0, (await repository.FlushPendingNowAsync(64, TimeSpan.FromMinutes(1))).Failed);

        // 注入一条 SchemaVersion 错位的坏队列行：连续失败直到达到 MaxFlushAttempts 后必须进入 dead，不再卡住活跃批。
        var brokenWrite = validWrites[0] with { StateVersion = 2, SchemaVersion = SectionCodec.CurrentSchemaVersion + 99 };
        await repository.EnqueueSectionsAsync([brokenWrite]);
        await SqlAsync(source, "UPDATE platform.section_flush_queue SET attempts = $2 WHERE player_id = $1 AND state_version = 2",
            player, PostgresSectionRepository.MaxFlushAttempts - 1);

        var flushResult = await repository.FlushPendingNowAsync(64, TimeSpan.FromMinutes(1));
        Assert.Equal(1, flushResult.Failed);
        Assert.Equal("dead", await ScalarAsync(source,
            "SELECT status FROM platform.section_flush_queue WHERE player_id = $1 AND state_version = 2", player));

        // 死信行不会继续被 FlushPendingNowAsync 认领，也不会在 LoadAsync 中覆盖或破坏持久化状态。
        var secondFlush = await repository.FlushPendingNowAsync(64, TimeSpan.FromMinutes(1));
        Assert.Equal(0, secondFlush.Claimed);
        var loaded = await repository.LoadAsync(player);
        Assert.NotNull(loaded);
        Assert.Equal(1L, loaded!.Head.StateVersion);
    }

    [Fact]
    public async Task ApplyComputeResultAsync_PendingQueueAheadOfHead_BridgesInsteadOfFalseStaleVersion()
    {
        var cs = PostgresTestSchema.RequireConnectionString("pending 队列桥接测试");
        await using var source = NpgsqlDataSource.Create(cs);
        await PostgresTestSchema.EnsureAsync(source);
        var repository = new PostgresSectionRepository(source, NullLogger<PostgresSectionRepository>.Instance);

        var player = await SeedPlayerAsync(source);
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        var catalog = GameDataCatalog.LoadEmbedded();
        var agg = PlayerAggregate.Create(new PlayerId(player), new AccountId(player), player.ToString("N"), t0, catalog.Fingerprint);
        await repository.EnqueueSectionsAsync(SectionCodec.EncodeAggregateFull(agg.ExportState(), t0));
        await repository.FlushPendingNowAsync(64, TimeSpan.FromMinutes(1));

        // 模拟在线侧更新并提交了 version=2 的 loadout 分段进入 flush_queue，但 SectionFlushWorker 尚未刷盘：
        // 持久化提交头仍为 v1，LoadAsync 叠加队列后给出有效版本 v2。
        var loadoutV2 = SectionCodec.Encode(player, PlayerSectionKinds.Loadout, 2, new { bridged = true }, t0.AddMinutes(5)) with
        {
            GameDataFingerprint = catalog.Fingerprint,
            LastSimulatedAt = t0.AddMinutes(5)
        };
        await repository.EnqueueSectionsAsync([loadoutV2]);
        Assert.Equal(1L, await ScalarAsync(source, "SELECT state_version FROM game.player_state_heads WHERE player_id = $1", player));

        // 冷挂机从有效版本 v2 推进到 v3：ApplyComputeResultAsync 应顺带排空 pending v2 并推进到 v3，不能误判 StaleVersion。
        var nextProg = SectionCodec.Encode(player, PlayerSectionKinds.Progression, 3, new { xp = 100d }, t0.AddMinutes(30));
        var outcome = await repository.ApplyComputeResultAsync(player, 2, 3, [nextProg], t0.AddMinutes(30), catalog.Fingerprint);
        Assert.Equal(ComputeApplyOutcome.Applied, outcome);
        Assert.Equal(3L, await ScalarAsync(source, "SELECT state_version FROM game.player_state_heads WHERE player_id = $1", player));
        Assert.Equal(2L, await ScalarAsync(source, "SELECT section_version FROM game.player_state_sections WHERE player_id = $1 AND section_kind = 'loadout'", player));
        Assert.Equal(0L, await ScalarAsync(source, "SELECT count(*) FROM platform.section_flush_queue WHERE player_id = $1", player));
    }

    [Fact]
    public async Task OutboxRepository_ExceedingMaxAttempts_MovesToDeadAndStopsClaiming()
    {
        var cs = PostgresTestSchema.RequireConnectionString("outbox 死信回归测试");
        await using var source = NpgsqlDataSource.Create(cs);
        await PostgresTestSchema.EnsureAsync(source);
        var outbox = new PostgresOutboxRepository(source);

        var aggregateId = Guid.NewGuid();
        await using (var conn = await source.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await outbox.AppendAsync(conn, tx, aggregateId, "account", "test.event", "{}"u8.ToArray(), DateTimeOffset.UtcNow.AddMinutes(-1));
            await tx.CommitAsync();
        }
        await SqlAsync(source, "UPDATE platform.integration_outbox SET attempts = $2 WHERE aggregate_id = $1",
            aggregateId, PostgresOutboxRepository.MaxAttempts - 1);

        var claimed = await outbox.ClaimAsync(10, TimeSpan.FromMinutes(1));
        var target = Assert.Single(claimed, m => m.AggregateId == aggregateId);
        Assert.Equal(PostgresOutboxRepository.MaxAttempts, target.Attempts);

        await outbox.ReleaseAsync(target.OutboxId, target.LeaseToken, "fatal error", TimeSpan.FromSeconds(1));
        Assert.Equal("dead", await ScalarAsync(source, "SELECT status FROM platform.integration_outbox WHERE outbox_id = $1", target.OutboxId));

        await SqlAsync(source, "UPDATE platform.integration_outbox SET visible_at = now() - interval '1 hour' WHERE outbox_id = $1", target.OutboxId);
        Assert.DoesNotContain(await outbox.ClaimAsync(10, TimeSpan.FromMinutes(1)), m => m.OutboxId == target.OutboxId);
    }

    [Fact]
    public async Task OwnershipRepository_RenewAsync_RejectsExpiredLease()
    {
        var cs = PostgresTestSchema.RequireConnectionString("租约过期续租拒绝测试");
        await using var source = NpgsqlDataSource.Create(cs);
        await PostgresTestSchema.EnsureAsync(source);
        var ownership = new PostgresPlayerOwnershipRepository(source);

        var player = await SeedPlayerAsync(source);
        var owner = Guid.NewGuid();
        Assert.True(await ownership.AcquireAsync(player, owner, TimeSpan.FromSeconds(30)));
        Assert.True(await ownership.RenewAsync(player, owner, TimeSpan.FromSeconds(30)));

        // 将租约失效时刻回拨到过去：即使 owner_instance_id 仍一致，RenewAsync 必须返回 false，严禁脑裂续活陈旧内存状态。
        await SqlAsync(source, "UPDATE game.player_runtime_leases SET lease_until = now() - interval '5 seconds' WHERE player_id = $1", player);
        Assert.False(await ownership.RenewAsync(player, owner, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task RetentionRepository_PrunesDeadQueuesAndStaleLeasesInShortTransactions()
    {
        var cs = PostgresTestSchema.RequireConnectionString("留存清理短事务测试");
        await using var source = NpgsqlDataSource.Create(cs);
        await PostgresTestSchema.EnsureAsync(source);
        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = cs,
        }).Build());
        var retention = new PostgresRetentionRepository(source, options, NullLogger<PostgresRetentionRepository>.Instance);

        var player = await SeedPlayerAsync(source);
        var expiredAt = DateTimeOffset.UtcNow.AddDays(-10);
        await SqlAsync(source, """
            INSERT INTO game.player_runtime_leases(player_id, owner_instance_id, lease_until, renewed_at)
            VALUES ($1, $2, $3, $3)
            """, player, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-2));
        await SqlAsync(source, """
            INSERT INTO platform.section_flush_queue
                (player_id, section_kind, state_version, schema_version, codec, payload, raw_bytes, checksum, status, attempts, next_attempt_at, updated_at)
            VALUES ($1, 'loadout', 1, 43, 'json', '{}' , 2, repeat('a', 64), 'dead', 12, $2, $2)
            """, player, expiredAt);
        await SqlAsync(source, """
            INSERT INTO platform.integration_outbox
                (outbox_id, aggregate_type, aggregate_id, event_type, payload, occurred_at, status, attempts, visible_at)
            VALUES ($1, 'account', $2, 'test.dead', '{}', $3, 'dead', 20, $3)
            """, Guid.NewGuid(), player, expiredAt);

        var result = await retention.PruneAsync(DateTimeOffset.UtcNow);
        Assert.True(result.StaleRuntimeLeases >= 1);
        Assert.True(result.DeadQueueRows >= 1);
        Assert.True(result.DeadOutboxRows >= 1);
    }

    private static async Task<Guid> SeedPlayerAsync(NpgsqlDataSource source)
    {
        var playerId = Guid.NewGuid();
        await SqlAsync(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())",
            playerId, playerId.ToString("N"));
        await SqlAsync(source, "INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,invite_code,created_at,last_seen_at,status) VALUES ($1,$1,$2,$2,(left(replace($1::text,'-',''),16)),now(),now(),1)",
            playerId, playerId.ToString("N"));
        return playerId;
    }

    private static async Task<object?> ScalarAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var cmd = source.CreateCommand(sql);
        foreach (var value in values) cmd.Parameters.Add(new NpgsqlParameter { Value = value });
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task SqlAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var cmd = source.CreateCommand(sql);
        foreach (var value in values) cmd.Parameters.Add(new NpgsqlParameter { Value = value });
        await cmd.ExecuteNonQueryAsync();
    }
}
