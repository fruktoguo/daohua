using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// platform.compute_jobs 仓库级回归：入队去重、租约领取、完成/失败终态、
/// SKIP LOCKED 并发语义与冷挂机候选筛选。依赖 DAOHUA_MARKET_TEST_CONNECTION 指向的专用库。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class ComputeJobRepositoryTests
{
    private const int MaxAttempts = 3;

    /// <summary>同一玩家的未完结任务去重：pending 行就地刷新而非重复入队。</summary>
    [Fact]
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
        var claims = await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        Assert.Contains(claims, claim => claim.PlayerId == player);
        Assert.Equal(ComputeEnqueueResult.SkippedExisting,
            await jobs.EnqueueAsync(player, DateTimeOffset.UtcNow.AddMinutes(30), payload));
    }

    /// <summary>领取只拿到期 pending 与过期租约；活跃租约内的任务不可被重取。</summary>
    [Fact]
    public async Task ClaimDueRespectsLeaseAndSchedule()
    {
        var (source, jobs) = await OpenRepositoryAsync();
        var due = await SeedPlayerAsync(source);
        var future = await SeedPlayerAsync(source);
        await jobs.EnqueueAsync(due, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        await jobs.EnqueueAsync(future, DateTimeOffset.UtcNow.AddHours(1), new byte[] { 1 });

        var claims = await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(2), TimeSpan.Zero);
        var claim = Assert.Single(claims, entry => entry.PlayerId == due);
        Assert.DoesNotContain(claims, entry => entry.PlayerId == future);
        Assert.Equal(1, claim.Attempts);
        Assert.NotEqual(Guid.Empty, claim.LeaseToken);

        // 租约未到期：同一任务不可被重取。
        Assert.DoesNotContain(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(2), TimeSpan.Zero),
            entry => entry.JobId == claim.JobId);

        // 租约过期后孤儿行可被重取，attempts 继续累加。
        await SqlAsync(source,
            "UPDATE platform.compute_jobs SET lease_until = now() - interval '1 second' WHERE job_id = $1",
            claim.JobId);
        var orphan = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(2), TimeSpan.Zero),
            entry => entry.JobId == claim.JobId);
        Assert.Equal(2, orphan.Attempts);
    }

    /// <summary>完成必须匹配租约令牌；错误令牌不得篡改他人任务。</summary>
    [Fact]
    public async Task CompleteRequiresMatchingLeaseToken()
    {
        var (source, jobs) = await OpenRepositoryAsync();
        var player = await SeedPlayerAsync(source);
        await jobs.EnqueueAsync(player, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        var claim = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero),
            entry => entry.PlayerId == player);

        Assert.False(await jobs.CompleteAsync(claim.JobId, Guid.NewGuid(), new byte[] { 2 }));
        Assert.True(await jobs.CompleteAsync(claim.JobId, claim.LeaseToken, new byte[] { 2 }));
        Assert.Equal("succeeded", await ScalarAsync(source,
            "SELECT status FROM platform.compute_jobs WHERE job_id = $1", claim.JobId));
        Assert.Equal(new byte[] { 2 }, (byte[])(await ScalarAsync(source,
            "SELECT output_payload FROM platform.compute_jobs WHERE job_id = $1", claim.JobId))!);
        // 已完结的任务不再被任何令牌更新，也不再被领取。
        Assert.False(await jobs.CompleteAsync(claim.JobId, claim.LeaseToken, new byte[] { 3 }));
        Assert.DoesNotContain(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero),
            entry => entry.JobId == claim.JobId);
    }

    /// <summary>可重试失败退回 pending 并设重试闸门；不可重试与超上限进入 dead 终态。</summary>
    [Fact]
    public async Task FailRetriesWithBackoffThenDead()
    {
        var (source, jobs) = await OpenRepositoryAsync();
        var player = await SeedPlayerAsync(source);
        await jobs.EnqueueAsync(player, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        var claim = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero),
            entry => entry.PlayerId == player);

        Assert.True(await jobs.FailAsync(claim.JobId, claim.LeaseToken, "节点超时",
            retryable: true, TimeSpan.FromMinutes(10), MaxAttempts));
        Assert.Equal("pending", await ScalarAsync(source,
            "SELECT status FROM platform.compute_jobs WHERE job_id = $1", claim.JobId));
        Assert.Equal("节点超时", await ScalarAsync(source,
            "SELECT last_error FROM platform.compute_jobs WHERE job_id = $1", claim.JobId));
        // 重试闸门未到期：暂不可领取。
        Assert.DoesNotContain(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero),
            entry => entry.JobId == claim.JobId);

        await SqlAsync(source,
            "UPDATE platform.compute_jobs SET next_attempt_at = now() - interval '1 second' WHERE job_id = $1",
            claim.JobId);
        var retry = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero),
            entry => entry.JobId == claim.JobId);
        Assert.Equal(2, retry.Attempts);

        // 领取次数达到上限后即使可重试错误也直接 dead。
        Assert.True(await jobs.FailAsync(retry.JobId, retry.LeaseToken, "再次失败",
            retryable: true, TimeSpan.Zero, maxAttempts: 2));
        Assert.Equal("dead", await ScalarAsync(source,
            "SELECT status FROM platform.compute_jobs WHERE job_id = $1", claim.JobId));
        Assert.DoesNotContain(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero),
            entry => entry.JobId == claim.JobId);

        // 不可重试错误直接 dead。
        var other = await SeedPlayerAsync(source);
        await jobs.EnqueueAsync(other, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        var fatal = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero),
            entry => entry.PlayerId == other);
        Assert.True(await jobs.FailAsync(fatal.JobId, fatal.LeaseToken, "载荷非法",
            retryable: false, TimeSpan.Zero, MaxAttempts));
        Assert.Equal("dead", await ScalarAsync(source,
            "SELECT status FROM platform.compute_jobs WHERE job_id = $1", fatal.JobId));
    }

    /// <summary>HTTP 层失败的整批释放：同令牌指定 jobIds 的 leased 行退回 pending，超上限进 dead。</summary>
    [Fact]
    public async Task ReleaseLeaseRequeuesWholeBatch()
    {
        var (source, jobs) = await OpenRepositoryAsync();
        var first = await SeedPlayerAsync(source);
        var second = await SeedPlayerAsync(source);
        await jobs.EnqueueAsync(first, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        await jobs.EnqueueAsync(second, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        var claims = await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero);
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
        var reclaims = await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero);
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

        var claims = await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        Assert.DoesNotContain(claims, entry => entry.PlayerId == locked);
        Assert.Contains(claims, entry => entry.PlayerId == free);
        await transaction.RollbackAsync();
    }

    /// <summary>离线挂机批算扫描：投影离线 + 有离线锚点 + 提交头欠债在收益边界内 +
    /// 距上次结算满批算间隔（或已到边界终末批）+ 无未完结任务/存活租约/活跃会话/待消费收件箱。</summary>
    [Fact]
    public async Task ScanColdPlayersFiltersPresenceAnchorAndBatchInterval()
    {
        var (source, jobs) = await OpenRepositoryAsync();
        var batchInterval = TimeSpan.FromHours(1);
        var now = DateTimeOffset.UtcNow;

        var eligible = await SeedPlayerAsync(source);
        var tooFresh = await SeedPlayerAsync(source);
        var terminal = await SeedPlayerAsync(source);
        var capped = await SeedPlayerAsync(source);
        var online = await SeedPlayerAsync(source);
        var busy = await SeedPlayerAsync(source);
        var leased = await SeedPlayerAsync(source);
        var inSession = await SeedPlayerAsync(source);
        var hasInbox = await SeedPlayerAsync(source);
        var noAnchor = await SeedPlayerAsync(source);
        var headless = await SeedPlayerAsync(source);
        var seeded = new HashSet<Guid> { eligible, tooFresh, terminal, capped, online, busy, leased, inSession, hasInbox, noAnchor, headless };

        // eligible：锚点 now-3h、距上次结算 2h（满批算间隔）→ 选中。
        await UpsertProjectionAsync(source, eligible, "offline", now.AddHours(-3), now.AddMinutes(-30));
        // tooFresh：距上次结算 30min（不足批算间隔且未到边界）→ 不选。
        await UpsertProjectionAsync(source, tooFresh, "offline", now.AddHours(-3), now.AddMinutes(-30));
        // terminal：锚点 now-49h → 边界 now-1h 已过，欠债仍在（last_sim now-90min < 边界）→ 终末批选中。
        await UpsertProjectionAsync(source, terminal, "offline", now.AddHours(-49), now.AddHours(-49));
        // capped：last_sim 已 ≥ 锚点+48h → 无收益欠债，不选。
        await UpsertProjectionAsync(source, capped, "offline", now.AddHours(-50), now.AddHours(-50));
        // online：在线位不参与离线批算。
        await UpsertProjectionAsync(source, online, "online", null, now.AddMinutes(-10));
        // busy/leased/inSession/hasInbox：未完结任务、存活运行时租约、活跃会话成员、待消费收件箱各挡一。
        await UpsertProjectionAsync(source, busy, "offline", now.AddHours(-3), now.AddMinutes(-30));
        await UpsertProjectionAsync(source, leased, "offline", now.AddHours(-3), now.AddMinutes(-30));
        await UpsertProjectionAsync(source, inSession, "offline", now.AddHours(-3), now.AddMinutes(-30));
        await UpsertProjectionAsync(source, hasInbox, "offline", now.AddHours(-3), now.AddMinutes(-30));
        // noAnchor：离线但缺锚点（v30 遗留投影）→ 不选。
        await UpsertProjectionAsync(source, noAnchor, "offline", null, now.AddMinutes(-30));
        // headless：有投影无提交头，JOIN 即排除。
        await UpsertProjectionAsync(source, headless, "offline", now.AddHours(-3), now.AddMinutes(-30));

        await InsertHeadAsync(source, eligible, now.AddHours(-2));
        await InsertHeadAsync(source, tooFresh, now.AddMinutes(-30));
        await InsertHeadAsync(source, terminal, now.AddMinutes(-90));
        await InsertHeadAsync(source, capped, now.AddHours(-1));
        await InsertHeadAsync(source, online, now.AddHours(-2));
        await InsertHeadAsync(source, busy, now.AddHours(-2));
        await InsertHeadAsync(source, leased, now.AddHours(-2));
        await InsertHeadAsync(source, inSession, now.AddHours(-2));
        await InsertHeadAsync(source, hasInbox, now.AddHours(-2));
        await InsertHeadAsync(source, noAnchor, now.AddHours(-2));

        // busy 已有未完结任务；leased 仍被运行时实例持有；inSession 在活跃会话名册；
        // hasInbox 有待消费结算行（需一条会话行供外键）。
        await jobs.EnqueueAsync(busy, now.AddMinutes(-1), new byte[] { 1 });
        await SqlAsync(source,
            "INSERT INTO game.player_runtime_leases VALUES ($1, $2, now() + interval '5 minutes', now())",
            leased, Guid.NewGuid());
        var sessionId = $"scan-test-{Guid.NewGuid():N}";
        var spec = $$"""{"actionId":"action.test","members":[{"playerId":"{{inSession}}"}]}""";
        await SqlAsync(source,
            "INSERT INTO social.party_sessions(session_id,party_id,spec,started_at) VALUES ($1,$2,$3::jsonb,$4)",
            sessionId, Guid.NewGuid(), spec, now.AddMinutes(-10));
        await SqlAsync(source,
            "INSERT INTO social.party_session_inbox(session_id,player_id,sequence,payload,created_at) VALUES ($1,$2,1,'{}'::jsonb,$3)",
            sessionId, hasInbox, now.AddMinutes(-5));

        var candidates = await jobs.ScanColdPlayersAsync(batchInterval, 2048);
        var picked = candidates.Select(candidate => candidate.PlayerId).ToHashSet();
        Assert.Contains(eligible, picked);
        Assert.Contains(terminal, picked);
        Assert.Equal(now.AddHours(-3), Assert.Single(candidates, c => c.PlayerId == eligible).OfflineSince, TimeSpan.FromSeconds(5));
        Assert.Equal(now.AddHours(-2), Assert.Single(candidates, c => c.PlayerId == eligible).LastSimulatedAt, TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(picked, id => seeded.Contains(id) && id != eligible && id != terminal);
    }

    /// <summary>领取的 pending 门槛：不满 minPendingAge 的新 pending 不领，老 pending 领，
    /// 过期租约孤儿行不受门槛影响；零门槛即领（本地执行器口径）。</summary>
    [Fact]
    public async Task ClaimDueHonorsMinPendingAge()
    {
        var (source, jobs) = await OpenRepositoryAsync();
        var fresh = await SeedPlayerAsync(source);
        var aged = await SeedPlayerAsync(source);
        var orphan = await SeedPlayerAsync(source);
        // orphan 先单独入队领取（此刻库内只有它到期），再拨过期租约制造失联孤儿行；
        // 零门槛领取会把同期 pending 一并租走，必须在 fresh/aged 入队前完成。
        await jobs.EnqueueAsync(orphan, DateTimeOffset.UtcNow.AddMinutes(-10), new byte[] { 1 });
        var lease = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero),
            entry => entry.PlayerId == orphan);
        await SqlAsync(source,
            "UPDATE platform.compute_jobs SET lease_until = now() - interval '1 second' WHERE job_id = $1",
            lease.JobId);
        await jobs.EnqueueAsync(fresh, DateTimeOffset.UtcNow.AddMinutes(-1), new byte[] { 1 });
        await jobs.EnqueueAsync(aged, DateTimeOffset.UtcNow.AddMinutes(-10), new byte[] { 1 });

        // 溢出阈值 5 分钟：fresh 的 target_time 距今仅 1 分钟不领；aged 与孤儿行照领。
        var nodeClaims = await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        Assert.DoesNotContain(nodeClaims, entry => entry.PlayerId == fresh);
        Assert.Contains(nodeClaims, entry => entry.PlayerId == aged);
        Assert.Contains(nodeClaims, entry => entry.JobId == lease.JobId);

        // 零门槛（本地执行器）：剩余 fresh 立即领取。
        var localClaims = await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero);
        Assert.Contains(localClaims, entry => entry.PlayerId == fresh);
    }

    /// <summary>积压快照必须按状态聚合：pending/due/leased/终态计数与最老积压年龄。</summary>
    [Fact]
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

        var claim = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero),
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
        var fatal = Assert.Single(await jobs.ClaimDueAsync(64, TimeSpan.FromMinutes(1), TimeSpan.Zero),
            entry => entry.PlayerId == due);
        Assert.True(await jobs.FailAsync(fatal.JobId, fatal.LeaseToken, "载荷非法",
            retryable: false, TimeSpan.Zero, MaxAttempts));
        var dead = await jobs.GetBacklogAsync();
        Assert.True(dead.Dead >= deadBefore + 1);
    }

    /// <summary>领取索引口径：pending 到期与 leased 孤儿回收各自由部分索引服务，旧组合索引不再存在。</summary>
    [Fact]
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
        NpgsqlDataSource source, Guid playerId, string presence,
        DateTimeOffset? offlineSince, DateTimeOffset lastActiveAt) =>
        SqlAsync(source, """
            INSERT INTO game.player_projection
                (player_id, realm_rank, current_node_id, presence, offline_since, last_active_at, projection_version, updated_at)
            VALUES ($1, 0, 'node.test', $2, $3, $4, 1, now())
            ON CONFLICT (player_id) DO UPDATE SET
                presence = EXCLUDED.presence, offline_since = EXCLUDED.offline_since,
                last_active_at = EXCLUDED.last_active_at,
                projection_version = game.player_projection.projection_version + 1, updated_at = now()
            """, playerId, presence, (object?)offlineSince ?? DBNull.Value, lastActiveAt);

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
