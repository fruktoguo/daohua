using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// platform.compute_jobs 仓库级回归：入队去重、租约领取、完成/失败终态、
/// SKIP LOCKED 并发语义与冷挂机候选筛选。依赖 DAOHUA_MARKET_TEST_CONNECTION 指向的专用库。
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ComputeJobRepositoryTests
{
    private const int MaxAttempts = 3;

    /// <summary>派生 job_id 必须对相同输入确定、对不同输入区分。</summary>
    [Fact]
    public void DeriveJobIdIsDeterministicAndDistinct()
    {
        var player = Guid.NewGuid();
        var first = PostgresComputeJobRepository.DeriveJobId(player, 1_700_000_000_000);
        Assert.Equal(first, PostgresComputeJobRepository.DeriveJobId(player, 1_700_000_000_000));
        Assert.NotEqual(first, PostgresComputeJobRepository.DeriveJobId(player, 1_700_000_000_001));
        Assert.NotEqual(first, PostgresComputeJobRepository.DeriveJobId(Guid.NewGuid(), 1_700_000_000_000));
    }

    /// <summary>同一玩家的未完结任务去重：pending 行就地刷新而非重复入队。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task EnqueueDeduplicatesOpenJobPerPlayer()
    {
        var (source, jobs) = await OpenRepositoryAsync();
        var player = await SeedPlayerAsync(source);
        var payload = new byte[] { 1, 2, 3 };

        Assert.Equal(ComputeEnqueueResult.Inserted,
            await jobs.EnqueueAsync(player, DateTimeOffset.UtcNow, payload));
        Assert.Equal(ComputeEnqueueResult.Refreshed,
            await jobs.EnqueueAsync(player, DateTimeOffset.UtcNow.AddMinutes(5), new byte[] { 9 }));

        Assert.Equal(1, await CountRowsAsync(source, player));
        // 同玩家同目标时刻派生出相同 job_id：等价重复入队被跳过。
        var target = DateTimeOffset.UtcNow.AddMinutes(9);
        Assert.Equal(ComputeEnqueueResult.Refreshed, await jobs.EnqueueAsync(player, target, payload));
        Assert.Equal(ComputeEnqueueResult.SkippedExisting, await jobs.EnqueueAsync(player, target, payload));
        Assert.Equal(1, await CountRowsAsync(source, player));

        // leased 行不打扰：拨回到期后领取，再次入队被跳过。
        await SqlAsync(source,
            "UPDATE platform.compute_jobs SET target_time = now() - interval '1 minute' WHERE player_id = $1",
            player);
        var claims = await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1));
        Assert.Contains(claims, claim => claim.PlayerId == player);
        Assert.Equal(ComputeEnqueueResult.SkippedExisting,
            await jobs.EnqueueAsync(player, DateTimeOffset.UtcNow.AddMinutes(30), payload));
    }

    /// <summary>领取只拿到期 pending 与过期租约；活跃租约内的任务不可被重取。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ClaimDueRespectsLeaseAndSchedule()
    {
        var (source, jobs) = await OpenRepositoryAsync();
        var due = await SeedPlayerAsync(source);
        var future = await SeedPlayerAsync(source);
        await jobs.EnqueueAsync(due, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        await jobs.EnqueueAsync(future, DateTimeOffset.UtcNow.AddHours(1), new byte[] { 1 });

        var claims = await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(2));
        var claim = Assert.Single(claims, entry => entry.PlayerId == due);
        Assert.DoesNotContain(claims, entry => entry.PlayerId == future);
        Assert.Equal(1, claim.Attempts);
        Assert.NotEqual(Guid.Empty, claim.LeaseToken);

        // 租约未到期：同一任务不可被重取。
        Assert.DoesNotContain(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(2)),
            entry => entry.JobId == claim.JobId);

        // 租约过期后孤儿行可被重取，attempts 继续累加。
        await SqlAsync(source,
            "UPDATE platform.compute_jobs SET lease_until = now() - interval '1 second' WHERE job_id = $1",
            claim.JobId);
        var orphan = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(2)),
            entry => entry.JobId == claim.JobId);
        Assert.Equal(2, orphan.Attempts);
    }

    /// <summary>完成必须匹配租约令牌；错误令牌不得篡改他人任务。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task CompleteRequiresMatchingLeaseToken()
    {
        var (source, jobs) = await OpenRepositoryAsync();
        var player = await SeedPlayerAsync(source);
        await jobs.EnqueueAsync(player, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        var claim = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1)),
            entry => entry.PlayerId == player);

        Assert.False(await jobs.CompleteAsync(claim.JobId, Guid.NewGuid(), new byte[] { 2 }));
        Assert.True(await jobs.CompleteAsync(claim.JobId, claim.LeaseToken, new byte[] { 2 }));
        Assert.Equal("succeeded", await ScalarAsync(source,
            "SELECT status FROM platform.compute_jobs WHERE job_id = $1", claim.JobId));
        Assert.Equal(new byte[] { 2 }, (byte[])(await ScalarAsync(source,
            "SELECT output_payload FROM platform.compute_jobs WHERE job_id = $1", claim.JobId))!);
        // 已完结的任务不再被任何令牌更新，也不再被领取。
        Assert.False(await jobs.CompleteAsync(claim.JobId, claim.LeaseToken, new byte[] { 3 }));
        Assert.DoesNotContain(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1)),
            entry => entry.JobId == claim.JobId);
    }

    /// <summary>可重试失败退回 pending 并设重试闸门；不可重试与超上限进入 dead 终态。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task FailRetriesWithBackoffThenDead()
    {
        var (source, jobs) = await OpenRepositoryAsync();
        var player = await SeedPlayerAsync(source);
        await jobs.EnqueueAsync(player, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        var claim = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1)),
            entry => entry.PlayerId == player);

        Assert.True(await jobs.FailAsync(claim.JobId, claim.LeaseToken, "节点超时",
            retryable: true, TimeSpan.FromMinutes(10), MaxAttempts));
        Assert.Equal("pending", await ScalarAsync(source,
            "SELECT status FROM platform.compute_jobs WHERE job_id = $1", claim.JobId));
        Assert.Equal("节点超时", await ScalarAsync(source,
            "SELECT last_error FROM platform.compute_jobs WHERE job_id = $1", claim.JobId));
        // 重试闸门未到期：暂不可领取。
        Assert.DoesNotContain(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1)),
            entry => entry.JobId == claim.JobId);

        await SqlAsync(source,
            "UPDATE platform.compute_jobs SET next_attempt_at = now() - interval '1 second' WHERE job_id = $1",
            claim.JobId);
        var retry = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1)),
            entry => entry.JobId == claim.JobId);
        Assert.Equal(2, retry.Attempts);

        // 领取次数达到上限后即使可重试错误也直接 dead。
        Assert.True(await jobs.FailAsync(retry.JobId, retry.LeaseToken, "再次失败",
            retryable: true, TimeSpan.Zero, maxAttempts: 2));
        Assert.Equal("dead", await ScalarAsync(source,
            "SELECT status FROM platform.compute_jobs WHERE job_id = $1", claim.JobId));
        Assert.DoesNotContain(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1)),
            entry => entry.JobId == claim.JobId);

        // 不可重试错误直接 dead。
        var other = await SeedPlayerAsync(source);
        await jobs.EnqueueAsync(other, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        var fatal = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1)),
            entry => entry.PlayerId == other);
        Assert.True(await jobs.FailAsync(fatal.JobId, fatal.LeaseToken, "载荷非法",
            retryable: false, TimeSpan.Zero, MaxAttempts));
        Assert.Equal("dead", await ScalarAsync(source,
            "SELECT status FROM platform.compute_jobs WHERE job_id = $1", fatal.JobId));
    }

    /// <summary>HTTP 层失败的整批释放：同令牌指定 jobIds 的 leased 行退回 pending，超上限进 dead。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ReleaseLeaseRequeuesWholeBatch()
    {
        var (source, jobs) = await OpenRepositoryAsync();
        var first = await SeedPlayerAsync(source);
        var second = await SeedPlayerAsync(source);
        await jobs.EnqueueAsync(first, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        await jobs.EnqueueAsync(second, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        var claims = await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1));
        Assert.Equal(2, claims.Count(entry => entry.PlayerId == first || entry.PlayerId == second));
        var token = Assert.Single(claims.Where(entry => entry.PlayerId == first || entry.PlayerId == second)
            .Select(entry => entry.LeaseToken).Distinct());
        var jobIds = claims.Where(entry => entry.PlayerId == first || entry.PlayerId == second)
            .Select(entry => entry.JobId).ToArray();

        Assert.Equal(2, await jobs.ReleaseLeaseAsync(token, jobIds, "连接重置",
            TimeSpan.FromMinutes(1), MaxAttempts));
        // 释放会清空租约字段并退回 pending；两条任务应可再次被领取。
        Assert.Equal(2, (long)(await ScalarAsync(source,
            "SELECT count(*) FROM platform.compute_jobs WHERE player_id IN ($1, $2) AND status = 'pending' AND lease_token IS NULL",
            first, second))!);

        // 不在 jobIds 作用域内的行不被误放：重新领取后只释放其中一条。
        // 释放已按各自 attempts 施加退避闸门，先拨回过去使两任务立即可再领取。
        await SqlAsync(source,
            "UPDATE platform.compute_jobs SET next_attempt_at = now() - interval '1 second' WHERE player_id IN ($1, $2)",
            first, second);
        var reclaims = await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1));
        var scoped = Assert.Single(reclaims, entry => entry.PlayerId == first);
        Assert.Equal(1, await jobs.ReleaseLeaseAsync(scoped.LeaseToken, [scoped.JobId], "部分释放",
            TimeSpan.FromMinutes(1), MaxAttempts));
        Assert.Equal("pending", await ScalarAsync(source,
            "SELECT status FROM platform.compute_jobs WHERE player_id = $1", first));
        Assert.Equal("leased", await ScalarAsync(source,
            "SELECT status FROM platform.compute_jobs WHERE player_id = $1", second));
    }

    /// <summary>SKIP LOCKED：被其他事务持有的行被跳过，未被锁的行仍可领取。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ClaimSkipsRowsLockedByAnotherTransaction()
    {
        var (source, jobs) = await OpenRepositoryAsync();
        var locked = await SeedPlayerAsync(source);
        var free = await SeedPlayerAsync(source);
        await jobs.EnqueueAsync(locked, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        await jobs.EnqueueAsync(free, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });

        // 另一个事务对目标任务行加锁并保持未提交；领取器必须跳过它。
        await using var locker = await source.OpenConnectionAsync();
        await using var transaction = await locker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT job_id FROM platform.compute_jobs WHERE player_id = $1 FOR UPDATE", locker, transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter { Value = locked });
            Assert.NotNull(await lockCommand.ExecuteScalarAsync());
        }

        var claims = await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1));
        Assert.DoesNotContain(claims, entry => entry.PlayerId == locked);
        Assert.Contains(claims, entry => entry.PlayerId == free);
        await transaction.RollbackAsync();
    }

    /// <summary>冷挂机扫描只筛出离线、在窗口内、过宽限且无未完结任务/存活租约的玩家；
    /// 模拟欠债窗口按提交头 last_simulated_at 判定（JOIN player_state_heads），投影活跃时间不替代。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ScanColdPlayersFiltersPresenceWindowAndOpenJobs()
    {
        var (source, jobs) = await OpenRepositoryAsync();
        var minIdle = TimeSpan.FromMinutes(5);
        var window = TimeSpan.FromHours(48);
        var now = DateTimeOffset.UtcNow;

        var eligible = await SeedPlayerAsync(source);
        var online = await SeedPlayerAsync(source);
        var tooOld = await SeedPlayerAsync(source);
        var tooFresh = await SeedPlayerAsync(source);
        var busy = await SeedPlayerAsync(source);
        var leased = await SeedPlayerAsync(source);
        var headless = await SeedPlayerAsync(source);
        var simTooOld = await SeedPlayerAsync(source);
        var simTooFresh = await SeedPlayerAsync(source);
        var seeded = new HashSet<Guid> { eligible, online, tooOld, tooFresh, busy, leased, headless, simTooOld, simTooFresh };

        await UpsertProjectionAsync(source, eligible, "offline", now.AddMinutes(-10));
        await UpsertProjectionAsync(source, online, "online", now.AddMinutes(-10));
        await UpsertProjectionAsync(source, tooOld, "offline", now.AddHours(-60));
        await UpsertProjectionAsync(source, tooFresh, "offline", now.AddMinutes(-1));
        await UpsertProjectionAsync(source, busy, "offline", now.AddMinutes(-10));
        await UpsertProjectionAsync(source, leased, "offline", now.AddMinutes(-10));
        await UpsertProjectionAsync(source, headless, "offline", now.AddMinutes(-10));
        await UpsertProjectionAsync(source, simTooOld, "offline", now.AddMinutes(-10));
        await UpsertProjectionAsync(source, simTooFresh, "offline", now.AddMinutes(-10));

        // 提交头 last_simulated_at 才是补算窗口口径：simTooOld/simTooFresh 活跃投影在窗内但模拟时间出窗，必须被 SQL 排除。
        await InsertHeadAsync(source, eligible, now.AddMinutes(-10));
        await InsertHeadAsync(source, online, now.AddMinutes(-10));
        await InsertHeadAsync(source, tooOld, now.AddHours(-60));
        await InsertHeadAsync(source, tooFresh, now.AddMinutes(-10));
        await InsertHeadAsync(source, busy, now.AddMinutes(-10));
        await InsertHeadAsync(source, leased, now.AddMinutes(-10));
        await InsertHeadAsync(source, simTooOld, now.AddHours(-60));
        await InsertHeadAsync(source, simTooFresh, now.AddMinutes(-1));

        // busy 已有未完结任务；leased 仍被运行时实例持有。
        await jobs.EnqueueAsync(busy, now.AddMinutes(-1), new byte[] { 1 });
        await SqlAsync(source,
            "INSERT INTO game.player_runtime_leases VALUES ($1, $2, now() + interval '5 minutes', now())",
            leased, Guid.NewGuid());

        var candidates = await jobs.ScanColdPlayersAsync(minIdle, window, 64);
        var picked = candidates.Select(candidate => candidate.PlayerId).ToHashSet();
        Assert.Contains(eligible, picked);
        Assert.Equal(now.AddMinutes(-10), Assert.Single(candidates, c => c.PlayerId == eligible).LastSimulatedAt, TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(picked, id => seeded.Contains(id) && id != eligible);
    }

    /// <summary>积压快照必须按状态聚合：pending/due/leased/终态计数与最老积压年龄。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task BacklogAggregatesByStatusAndDueGate()
    {
        var (source, jobs) = await OpenRepositoryAsync();
        var due = await SeedPlayerAsync(source);
        var gated = await SeedPlayerAsync(source);

        await jobs.EnqueueAsync(due, DateTimeOffset.UtcNow.AddMinutes(-5), new byte[] { 1 });
        // gated 行加未到期重试闸门：pending 计入总数但不计入 pendingDue。
        await jobs.EnqueueAsync(gated, DateTimeOffset.UtcNow.AddMinutes(-5), new byte[] { 1 });
        await SqlAsync(source,
            "UPDATE platform.compute_jobs SET next_attempt_at = now() + interval '30 minutes' WHERE player_id = $1",
            gated);

        var before = await jobs.GetBacklogAsync();
        var dueBefore = before.PendingDue;
        var pendingBefore = before.Pending;

        var claim = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1)),
            entry => entry.PlayerId == due);
        var after = await jobs.GetBacklogAsync();
        Assert.Equal(pendingBefore - 1, after.Pending);
        Assert.Equal(dueBefore - 1, after.PendingDue);
        Assert.True(after.Leased >= 1);
        Assert.True(after.OldestPendingAgeSeconds is >= 200,
            "最老 pending 行 target_time 5 分钟前应反映为积压年龄。");

        // 完结后终态计入 succeeded。
        Assert.True(await jobs.CompleteAsync(claim.JobId, claim.LeaseToken, new byte[] { 2 }));
        var done = await jobs.GetBacklogAsync();
        Assert.True(done.Succeeded >= 1);
        var deadBefore = done.Dead;

        // 不可重试失败进入 dead 计数。
        await jobs.EnqueueAsync(due, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        var fatal = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1)),
            entry => entry.PlayerId == due);
        Assert.True(await jobs.FailAsync(fatal.JobId, fatal.LeaseToken, "载荷非法",
            retryable: false, TimeSpan.Zero, MaxAttempts));
        var dead = await jobs.GetBacklogAsync();
        Assert.True(dead.Dead >= deadBefore + 1);
    }

    /// <summary>领取索引口径：pending 到期与 leased 孤儿回收各自由部分索引服务，旧组合索引不再存在。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ComputeJobsClaimIndexesArePartial()
    {
        var (source, _) = await OpenRepositoryAsync();
        var names = (string?)(await ScalarAsync(source,
            "SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes WHERE schemaname = 'platform' AND tablename = 'compute_jobs'"))!;
        Assert.Contains("ix_compute_jobs_pending_due", names);
        Assert.Contains("ix_compute_jobs_lease_orphan", names);
        Assert.DoesNotContain("ix_compute_jobs_ready", names);
    }

    private static async Task<(NpgsqlDataSource Source, PostgresComputeJobRepository Repository)> OpenRepositoryAsync()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("冷挂机仓库集成测试");
        var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        return (source, new PostgresComputeJobRepository(source));
    }

    private static async Task<Guid> SeedPlayerAsync(NpgsqlDataSource source)
    {
        var playerId = Guid.NewGuid();
        await SqlAsync(source,
            "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())",
            playerId, playerId.ToString());
        await SqlAsync(source,
            "INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,invite_code,created_at,last_seen_at,status) VALUES ($1,$1,$2,$2,(left(replace($1::text,'-',''),16)),now(),now(),1)",
            playerId, playerId.ToString());
        return playerId;
    }

    private static Task UpsertProjectionAsync(
        NpgsqlDataSource source, Guid playerId, string presence, DateTimeOffset lastActiveAt) =>
        SqlAsync(source, """
            INSERT INTO game.player_projection
                (player_id, realm_rank, current_node_id, presence, last_active_at, projection_version, updated_at)
            VALUES ($1, 0, 'node.test', $2, $3, 1, now())
            """, playerId, presence, lastActiveAt);

    private static Task InsertHeadAsync(
        NpgsqlDataSource source, Guid playerId, DateTimeOffset lastSimulatedAt) =>
        SqlAsync(source, """
            INSERT INTO game.player_state_heads
                (player_id, state_version, schema_version, last_simulated_at, game_data_fingerprint, checksum, updated_at)
            VALUES ($1, 1, 3, $2, 'fp.test', '0000000000000000000000000000000000000000000000000000000000000000', now())
            """, playerId, lastSimulatedAt);

    private static async Task<long> CountRowsAsync(NpgsqlDataSource source, Guid playerId) =>
        (long)(await ScalarAsync(source,
            "SELECT count(*) FROM platform.compute_jobs WHERE player_id = $1", playerId))!;

    private static async Task<object?> ScalarAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return await command.ExecuteScalarAsync();
    }

    private static async Task SqlAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync();
    }
}
