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
/// 1. section_flush_queue 达最大重试次数后转 dead 并退出活跃刷盘但保留权威加载水位；
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
    public async Task SectionFlushQueue_DeadAuthorityBlocksLoadAndComputeUntilSameSectionsSuperseded()
    {
        var cs = PostgresTestSchema.RequireConnectionString("flush 死信回归测试");
        await using var source = NpgsqlDataSource.Create(cs);
        await PostgresTestSchema.EnsureAsync(source);
        var repository = new PostgresSectionRepository(source, NullLogger<PostgresSectionRepository>.Instance);
        var economy = new PostgresEconomyRepository(source, NullLogger<PostgresEconomyRepository>.Instance);
        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Storage:ConnectionString"] = cs }).Build());
        var retention = new PostgresRetentionRepository(source, options, NullLogger<PostgresRetentionRepository>.Instance);

        var player = await SeedPlayerAsync(source);
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var catalog = GameDataCatalog.LoadEmbedded();
        var agg = PlayerAggregate.Create(new PlayerId(player), new AccountId(player), player.ToString("N"), now, catalog.Fingerprint);
        var validWrites = SectionCodec.EncodeAggregateFull(agg.ExportState(), now);
        await repository.EnqueueSectionsAsync(validWrites);
        await repository.FlushPendingNowAsync(256, TimeSpan.FromMinutes(1));
        Assert.Equal(8L, await ScalarAsync(source,
            "SELECT count(*) FROM game.player_state_sections WHERE player_id = $1 AND section_version = 1", player));

        // 真实资产事务：有效 loadout 和随后导致刷盘失败的 settings 与余额同时 durable。
        var loadout = validWrites.Single(w => w.SectionKind == PlayerSectionKinds.Loadout);
        var settings = validWrites.Single(w => w.SectionKind == PlayerSectionKinds.Settings);
        Assert.True((await economy.CommitAsync(new EconomyTransactionRequest(Guid.NewGuid(), $"dead:{player:D}",
            "simulation.reward", player, [new(player, "currency.spirit-stone", "", 5)], now,
            Sections: [loadout with { StateVersion = 2 }, settings with { StateVersion = 2 }], PersistLedger: false))).Applied);
        await SqlAsync(source, """
            UPDATE platform.section_flush_queue SET attempts = $2,
                schema_version = CASE WHEN section_kind = 'settings' THEN $3 ELSE schema_version END
            WHERE player_id = $1 AND state_version = 2
            """, player, PostgresSectionRepository.MaxFlushAttempts - 1, SectionCodec.CurrentSchemaVersion + 99);
        await repository.FlushPendingNowAsync(256, TimeSpan.FromMinutes(1));
        Assert.Equal(2L, await ScalarAsync(source,
            "SELECT count(*) FROM platform.section_flush_queue WHERE player_id = $1 AND state_version = 2 AND status = 'dead' AND last_error IS NOT NULL", player));
        Assert.Equal(5d, await ScalarAsync(source,
            "SELECT quantity FROM economy.player_asset_balances WHERE player_id = $1 AND asset_id = 'currency.spirit-stone'", player));

        // 其他分段推进全局 HEAD，不解除 loadout 的隔离。即便 target 包含替换写入，也不能从旧源基线开始计算。
        var world = validWrites.Single(w => w.SectionKind == PlayerSectionKinds.World);
        await repository.EnqueueSectionsAsync([world with { StateVersion = 3 }]);
        await repository.FlushPendingNowAsync(256, TimeSpan.FromMinutes(1));
        Assert.Equal(3L, await ScalarAsync(source, "SELECT state_version FROM game.player_state_heads WHERE player_id = $1", player));
        Assert.Equal(3L, (await repository.LoadSectionAsync(player, world.SectionKind))!.StateVersion);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync(player).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadComputeInputAsync(player).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadSectionAsync(player, loadout.SectionKind).AsTask());
        var healthy = await SeedPlayerAsync(source);
        await repository.EnqueueSectionsAsync([loadout with { PlayerId = healthy }]);
        var derived = await repository.LoadSectionsAsync([player, healthy], loadout.SectionKind);
        Assert.Equal(healthy, Assert.Single(derived).Key); // 不回退旧分段，不拖垮同批健康玩家。
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ApplyComputeResultAsync(player, 3, 4,
            [loadout with { StateVersion = 4 }], now.AddMinutes(1), catalog.Fingerprint).AsTask());

        // 新 pending 可参与有效读取，但不能作为删除证据：它自己仍可能转 dead。
        await repository.EnqueueSectionsAsync([loadout with { StateVersion = 4 }, settings with { StateVersion = 4 }]);
        await SqlAsync(source, "UPDATE platform.section_flush_queue SET updated_at = now() - interval '8 days' WHERE player_id = $1 AND status = 'dead'", player);
        await retention.PruneAsync(DateTimeOffset.UtcNow);
        Assert.Equal(2L, await ScalarAsync(source,
            "SELECT count(*) FROM platform.section_flush_queue WHERE player_id = $1 AND status = 'dead'", player));
        // 版本更高但 schema 错误的待刷盘行不构成替代证据，仍要隔离且保留两条原死信。
        await SqlAsync(source, "UPDATE platform.section_flush_queue SET schema_version = $2 WHERE player_id = $1 AND state_version = 4 AND section_kind = 'loadout'",
            player, SectionCodec.CurrentSchemaVersion + 1);
        var broken = await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadComputeInputAsync(player).AsTask());
        Assert.Contains("刷盘死信", broken.Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadSectionAsync(player, loadout.SectionKind).AsTask());
        Assert.DoesNotContain(player, (await repository.LoadSectionsAsync([player, healthy], loadout.SectionKind)).Keys);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.ApplyComputeResultAsync(player, 4, 5,
            [loadout with { StateVersion = 5 }], now.AddMinutes(2), catalog.Fingerprint).AsTask());
        await SqlAsync(source, "UPDATE platform.section_flush_queue SET schema_version = $2 WHERE player_id = $1 AND state_version = 4 AND section_kind = 'loadout'",
            player, SectionCodec.CurrentSchemaVersion);
        var frozen = Assert.IsType<ComputeFrozenInput>(await repository.LoadComputeInputAsync(player));
        Assert.Equal(4L, frozen.Bundle.Sections[loadout.SectionKind].StateVersion);
        Assert.Equal(5d, Assert.Single(frozen.Balances).Quantity);

        await repository.FlushPendingNowAsync(256, TimeSpan.FromMinutes(1));
        await SqlAsync(source, "UPDATE game.player_state_sections SET codec = 'invalid' WHERE player_id = $1 AND section_kind = 'loadout'", player);
        await retention.PruneAsync(DateTimeOffset.UtcNow);
        Assert.Equal(1L, await ScalarAsync(source,
            "SELECT count(*) FROM platform.section_flush_queue WHERE player_id = $1 AND section_kind = 'loadout' AND status = 'dead'", player));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync(player).AsTask());
        await SqlAsync(source, "UPDATE game.player_state_sections SET codec = $2 WHERE player_id = $1 AND section_kind = 'loadout'", player, loadout.Codec);
        await retention.PruneAsync(DateTimeOffset.UtcNow);
        Assert.Equal(0L, await ScalarAsync(source,
            "SELECT count(*) FROM platform.section_flush_queue WHERE player_id = $1 AND status = 'dead'", player));
        Assert.Equal(4L, (await repository.LoadAsync(player))!.Sections[loadout.SectionKind].StateVersion);
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
    public async Task OwnershipRepository_RenewWaitingForIdentity_DoesNotReviveExpiredLease()
    {
        var cs = PostgresTestSchema.RequireConnectionString("等待身份锁跨租约到期");
        await using var source = NpgsqlDataSource.Create(cs);
        await PostgresTestSchema.EnsureAsync(source);
        var ownership = new PostgresPlayerOwnershipRepository(source);
        var player = await SeedPlayerAsync(source);
        var owner = Guid.NewGuid();
        Assert.True(await ownership.AcquireAsync(player, owner, TimeSpan.FromSeconds(30)));

        await using var blocker = await source.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await PostgresPlayerOwnershipRepository.LockPlayerIdentityAsync(blocker, transaction, player, CancellationToken.None);
        var blockerPid = (int)(await ScalarAsync(blocker, transaction, "SELECT pg_backend_pid()"))!;
        var expiration = Assert.IsType<DateTime>(await ScalarAsync(source, """
            UPDATE game.player_runtime_leases SET lease_until = clock_timestamp() + interval '3 seconds'
            WHERE player_id = $1 RETURNING lease_until
            """, player));
        Assert.Equal(DateTimeKind.Utc, expiration.Kind);

        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var renew = ownership.RenewAsync(player, owner, TimeSpan.FromSeconds(30), testTimeout.Token).AsTask();
        // 必须观察续租已进入 SQL 且仍在等待锁，而不是在到期后才调用。
        while (await ScalarAsync(source, """
            SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity a WHERE $1 = ANY(pg_blocking_pids(a.pid)))
            """, blockerPid) is not true)
            await Task.Delay(20, testTimeout.Token);
        Assert.Equal(true, await ScalarAsync(source, "SELECT clock_timestamp() < $1", expiration));
        await using (var wait = new NpgsqlCommand(
            "SELECT pg_sleep(GREATEST(0, EXTRACT(epoch FROM ($1 - clock_timestamp()))) + 0.05)", blocker, transaction))
        {
            wait.Parameters.Add(new NpgsqlParameter { Value = expiration });
            await wait.ExecuteNonQueryAsync(testTimeout.Token);
        }
        await transaction.CommitAsync(testTimeout.Token);
        Assert.False(await renew.WaitAsync(testTimeout.Token));
        Assert.Equal(expiration, await ScalarAsync(source, "SELECT lease_until FROM game.player_runtime_leases WHERE player_id = $1", player));
    }

    [Fact]
    public async Task RetentionRepository_LockedStaleLease_IsNotDeletedAfterTakeover()
    {
        var cs = PostgresTestSchema.RequireConnectionString("过期租约清理与接管竞态");
        await using var source = NpgsqlDataSource.Create(cs);
        await PostgresTestSchema.EnsureAsync(source);
        var ownership = new PostgresPlayerOwnershipRepository(source);
        var player = await SeedPlayerAsync(source);
        var successor = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.True(await ownership.AcquireAsync(player, Guid.NewGuid(), TimeSpan.FromSeconds(30)));
        await SqlAsync(source, "UPDATE game.player_runtime_leases SET lease_until = $2 WHERE player_id = $1", player, now.AddHours(-2));
        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Storage:ConnectionString"] = cs }).Build());
        var repository = new PostgresRetentionRepository(source, options, NullLogger<PostgresRetentionRepository>.Instance);
        await using var successorConnection = await source.OpenConnectionAsync();
        await using var takeover = await successorConnection.BeginTransactionAsync();
        await PostgresPlayerOwnershipRepository.LockPlayerIdentityAsync(successorConnection, takeover, player, CancellationToken.None);
        await ScalarAsync(successorConnection, takeover, """
            UPDATE game.player_runtime_leases SET owner_instance_id = $2, lease_until = clock_timestamp() + interval '45 seconds'
            WHERE player_id = $1 RETURNING player_id
            """, player, successor);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        // 清理看到旧快照，但必须跳过正在接管的行，不能阻塞并在接管提交后按旧候选删除。
        await repository.PruneAsync(now, timeout.Token);
        await takeover.CommitAsync(timeout.Token);
        await repository.PruneAsync(now, timeout.Token);
        Assert.Equal(successor, await ScalarAsync(source, "SELECT owner_instance_id FROM game.player_runtime_leases WHERE player_id = $1", player));
    }

    [Fact]
    public async Task RetentionRepository_PrunesDeadOutboxAndStaleLeasesButRetainsUnreplacedSection()
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
            VALUES ($1, 'loadout', 1, $3, 'json', '{}' , 2, repeat('a', 64), 'dead', 12, $2, $2)
            """, player, expiredAt, SectionCodec.CurrentSchemaVersion);
        await SqlAsync(source, """
            INSERT INTO platform.integration_outbox
                (outbox_id, aggregate_type, aggregate_id, event_type, payload, occurred_at, status, attempts, visible_at)
            VALUES ($1, 'account', $2, 'test.dead', '{}', $3, 'dead', 20, $3)
            """, Guid.NewGuid(), player, expiredAt);

        var workerRetention = new PostgresRetentionRepository(source, options with { ProcessRole = ServerProcessRole.PersistenceWorker },
            NullLogger<PostgresRetentionRepository>.Instance);
        var workerResult = await workerRetention.PruneAsync(DateTimeOffset.UtcNow);
        Assert.True(workerResult.DeadOutboxRows >= 1);
        Assert.Equal(0, workerResult.StaleRuntimeLeases);
        Assert.Equal(1L, await ScalarAsync(source, "SELECT count(*) FROM game.player_runtime_leases WHERE player_id = $1", player));
        var result = await retention.PruneAsync(DateTimeOffset.UtcNow);
        Assert.True(result.StaleRuntimeLeases >= 1);
        Assert.Equal(1L, await ScalarAsync(source,
            "SELECT count(*) FROM platform.section_flush_queue WHERE player_id = $1 AND status = 'dead'", player));
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

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, params object[] values)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
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
