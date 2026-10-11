using System.Reflection;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Domain.Simulation;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 主服本地执行器（总纲18 §14.4）回归：compute_jobs 队列的「领取 → 本地重演 → 统一事务写回」闭环，
/// 批算目标判定（ComputeBatchTarget）与终末批（补到收益边界即停扫）。
/// </summary>
[Collection("PostgresIntegration")]
public sealed class LocalComputeExecutorTests
{
    private static readonly GameDataCatalog Catalog = GameDataCatalog.LoadEmbedded();
    private static readonly TimeSpan BatchInterval = TimeSpan.FromHours(1);

    /// <summary>批算目标判定：不足批算间隔 null；满间隔返回 now；越过边界返回边界（终末批不受间隔限）；已到边界 null。</summary>
    [Fact]
    public void ComputeBatchTargetHonorsIntervalAndBoundary()
    {
        var now = DateTimeOffset.UtcNow;
        var anchor = now.AddHours(-3);
        // 距上次结算 30min < 1h 间隔且未到边界 → 未到批算点。
        Assert.Null(BatchTarget(now.AddMinutes(-30), anchor, now, BatchInterval));
        // 距上次结算 2h ≥ 间隔 → 返回 now。
        Assert.Equal(now, BatchTarget(now.AddHours(-2), anchor, now, BatchInterval));
        // 锚点 50h 前 → 边界 now-2h 已过：欠债仅 30min 不足间隔，仍补到边界（终末批）。
        var staleAnchor = now.AddHours(-50);
        var staleBoundary = staleAnchor.Add(PlayerLifecycleRules.OfflineIdleLimit);
        Assert.Equal(staleBoundary, BatchTarget(staleBoundary.AddMinutes(-30), staleAnchor, now, BatchInterval));
        // from 已到/越过边界 → 无欠债可补。
        Assert.Null(BatchTarget(staleBoundary, staleAnchor, now, BatchInterval));
        Assert.Null(BatchTarget(staleBoundary.AddMinutes(1), staleAnchor, now, BatchInterval));
    }

    /// <summary>本地执行闭环：离线玩家任务被领取、重演并同一事务写回——提交头推进到目标、
    /// 收益经经济事务入账、projection.last_active_at 不因冷挂机补算推进。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task LocalExecutorAppliesAdvanceThroughSharedCommitBoundary()
    {
        await using var fixture = await Fixture.CreateAsync(anchorAge: TimeSpan.FromHours(3), simLag: TimeSpan.FromHours(3));
        var lastActiveBefore = fixture.LastActiveAt;
        Assert.True(await fixture.EnqueueViaWorkerAsync());
        var jobId = await fixture.JobIdAsync();

        await fixture.RunUntilJobTerminalAsync(jobId);
        Assert.Equal("succeeded", await fixture.ScalarAsync(
            "SELECT status FROM platform.compute_jobs WHERE job_id = $1", jobId));
        Assert.Equal("Applied", await fixture.ScalarAsync(
            "SELECT convert_from(output_payload, 'UTF8')::jsonb->>'outcome' FROM platform.compute_jobs WHERE job_id = $1", jobId));
        Assert.True((long)(await fixture.ScalarAsync(
            "SELECT state_version FROM game.player_state_heads WHERE player_id = $1", fixture.PlayerId))! > 1);
        Assert.Equal(
            await fixture.TimeAsync("SELECT target_time FROM platform.compute_jobs WHERE job_id = $1", jobId),
            await fixture.TimeAsync("SELECT last_simulated_at FROM game.player_state_heads WHERE player_id = $1", fixture.PlayerId),
            TimeSpan.FromSeconds(5));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT count(*) FROM economy.transactions WHERE transaction_id = $1", jobId));
        Assert.True((double)(await fixture.ScalarAsync(
            "SELECT quantity FROM economy.player_asset_balances WHERE player_id = $1 AND asset_id = 'item.ningxue-grass'",
            fixture.PlayerId))! > 0);
        // 冷挂机补算不算玩家活跃：投影活跃时间不推进。
        Assert.Equal(lastActiveBefore, await fixture.ScalarAsync(
            "SELECT last_active_at FROM game.player_projection WHERE player_id = $1", fixture.PlayerId));
    }

    /// <summary>回线抢占：玩家持有存活运行时租约时，本地执行结果按 StaleVersion 归档丢弃，
    /// 提交头与钱包均不变。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task LocalExecutorDiscardsResultUnderLiveRuntimeLease()
    {
        await using var fixture = await Fixture.CreateAsync(anchorAge: TimeSpan.FromHours(3), simLag: TimeSpan.FromHours(3));
        Assert.True(await fixture.EnqueueViaWorkerAsync());
        var jobId = await fixture.JobIdAsync();
        var sourceVersion = (long)(await fixture.ScalarAsync(
            "SELECT state_version FROM game.player_state_heads WHERE player_id = $1", fixture.PlayerId))!;
        await fixture.SqlAsync(
            "INSERT INTO game.player_runtime_leases VALUES ($1, $2, now() + interval '5 minutes', now())",
            fixture.PlayerId, Guid.NewGuid());

        await fixture.RunUntilJobTerminalAsync(jobId);
        Assert.Equal("succeeded", await fixture.ScalarAsync(
            "SELECT status FROM platform.compute_jobs WHERE job_id = $1", jobId));
        Assert.Equal("StaleVersion", await fixture.ScalarAsync(
            "SELECT convert_from(output_payload, 'UTF8')::jsonb->>'outcome' FROM platform.compute_jobs WHERE job_id = $1", jobId));
        Assert.Equal(sourceVersion, await fixture.ScalarAsync(
            "SELECT state_version FROM game.player_state_heads WHERE player_id = $1", fixture.PlayerId));
        Assert.Equal(0L, await fixture.ScalarAsync(
            "SELECT count(*) FROM economy.transactions WHERE transaction_id = $1", jobId));
        Assert.Equal(0L, await fixture.ScalarAsync(
            "SELECT count(*) FROM economy.player_asset_balances WHERE player_id = $1", fixture.PlayerId));
    }

    /// <summary>终末批：锚点 now-50h、欠债到边界 now-2h 的玩家，扫描+入队得到 target = 锚点+48h，
    /// 执行后提交头 last_simulated_at 停在边界，此后扫描不再选中。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task TerminalBatchClampsToBoundaryAndStopsScanning()
    {
        await using var fixture = await Fixture.CreateAsync(anchorAge: TimeSpan.FromHours(50), simLag: TimeSpan.FromHours(3));
        var boundary = fixture.Anchor.Add(PlayerLifecycleRules.OfflineIdleLimit);
        Assert.True(await fixture.EnqueueViaWorkerAsync());
        var jobId = await fixture.JobIdAsync();
        Assert.Equal(boundary, await fixture.TimeAsync(
            "SELECT target_time FROM platform.compute_jobs WHERE job_id = $1", jobId), TimeSpan.FromSeconds(5));

        await fixture.RunUntilJobTerminalAsync(jobId);
        Assert.Equal("succeeded", await fixture.ScalarAsync(
            "SELECT status FROM platform.compute_jobs WHERE job_id = $1", jobId));
        Assert.Equal("Applied", await fixture.ScalarAsync(
            "SELECT convert_from(output_payload, 'UTF8')::jsonb->>'outcome' FROM platform.compute_jobs WHERE job_id = $1", jobId));
        Assert.Equal(boundary, await fixture.TimeAsync(
            "SELECT last_simulated_at FROM game.player_state_heads WHERE player_id = $1", fixture.PlayerId), TimeSpan.FromSeconds(5));

        // 欠债补到边界后不再入选：last_simulated_at ≥ anchor + 48h。
        var candidates = await fixture.Jobs.ScanColdPlayersAsync(BatchInterval, 64);
        Assert.DoesNotContain(candidates, candidate => candidate.PlayerId == fixture.PlayerId);
    }

    /// <summary>内部静态判定经反射驱动：批算目标函数不属于公开契约。</summary>
    private static DateTimeOffset? BatchTarget(
        DateTimeOffset from, DateTimeOffset anchor, DateTimeOffset now, TimeSpan interval) =>
        (DateTimeOffset?)typeof(ColdCatchUpWorker)
            .GetMethod("ComputeBatchTarget", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [from, anchor, now, interval]);

    /// <summary>
    /// 离线玩家播种夹具：真实 PlayerAggregate 编码为 8 段落库（投影同步 presence/offline_since），
    /// 可选把提交头 last_simulated_at 拨回更早制造欠债。入队走真实 ColdCatchUpWorker.TryEnqueueAsync，
    /// 执行走真实 LocalComputeExecutorWorker.RunOnceAsync（内部入口经反射驱动）。
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServerOptions _options;

        private Fixture(NpgsqlDataSource source, PostgresSectionRepository sections,
            PostgresComputeJobRepository jobs, EcsComputeResultVerifier verifier,
            Guid playerId, DateTimeOffset anchor)
        {
            Source = source;
            Sections = sections;
            Jobs = jobs;
            Verifier = verifier;
            PlayerId = playerId;
            Anchor = anchor;
            _options = new ServerOptions("local-executor-test", source.ConnectionString, 256,
                TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 128, TimeSpan.FromSeconds(1),
                TimeSpan.FromDays(1), 65536, 64, 1, 0, false)
            {
                OfflineBatchInterval = BatchInterval
            };
        }

        public NpgsqlDataSource Source { get; }
        public PostgresSectionRepository Sections { get; }
        public PostgresComputeJobRepository Jobs { get; }
        public EcsComputeResultVerifier Verifier { get; }
        public Guid PlayerId { get; }
        public DateTimeOffset Anchor { get; }

        /// <param name="anchorAge">离线锚点距今时长（同时是该玩家的播种离线时刻）。</param>
        /// <param name="simLag">提交头 last_simulated_at 距今时长；> anchorAge 表示欠债仍在边界内。</param>
        public static async Task<Fixture> CreateAsync(TimeSpan anchorAge, TimeSpan simLag)
        {
            var source = NpgsqlDataSource.Create(PostgresTestSchema.RequireConnectionString("本地执行器测试"));
            await PostgresTestSchema.EnsureAsync(source);
            var playerId = Guid.NewGuid();
            var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var anchor = now - anchorAge;
            var createdAt = anchor.AddMinutes(-1);
            await SqlAsync(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,$3,$3)",
                playerId, playerId.ToString(), createdAt);
            await SqlAsync(source, """
                INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,created_at,last_seen_at,status)
                VALUES ($1,$1,$2,$2,$3,$3,1)
                """, playerId, playerId.ToString(), createdAt);

            var player = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(playerId),
                playerId.ToString(), createdAt, Catalog.Fingerprint);
            player.SetPresence(PresenceState.Offline, anchor);
            // 跳过引导：教程里程碑的强制出队截断会改变无限重复边界。
            player.SkipTutorial();
            player.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), -1), createdAt, Catalog.Actions);
            var sections = new PostgresSectionRepository(source, NullLogger<PostgresSectionRepository>.Instance);
            await sections.EnqueueSectionsAsync(SectionCodec.EncodeAggregate(player, createdAt));
            await sections.FlushPendingAsync(256, TimeSpan.FromMinutes(1));

            // 提交头拨回更早制造欠债：head.last_simulated_at 是批算欠债的判定与重演起点真源。
            var lastSimulatedAt = now - simLag;
            await SqlAsync(source,
                "UPDATE game.player_state_heads SET last_simulated_at = $2 WHERE player_id = $1",
                playerId, lastSimulatedAt);

            var jobs = new PostgresComputeJobRepository(source);
            var verifier = new EcsComputeResultVerifier(Catalog);
            var fixture = new Fixture(source, sections, jobs, verifier, playerId, anchor);
            return fixture;
        }

        /// <summary>播种时由投影落库写入的活跃时刻；用于断言冷挂机补回不推进该字段。</summary>
        public object? LastActiveAt => ScalarAsync(
            "SELECT last_active_at FROM game.player_projection WHERE player_id = $1", PlayerId)
            .GetAwaiter().GetResult();

        /// <summary>真实扫描 + 真实入队路径：先 ScanColdPlayersAsync 命中本玩家，再经
        /// ColdCatchUpWorker.TryEnqueueAsync 生成任务。</summary>
        public async ValueTask<bool> EnqueueViaWorkerAsync()
        {
            var worker = new ColdCatchUpWorker(Jobs, Sections, Source, _options,
                NullLogger<ColdCatchUpWorker>.Instance);
            // 共享测试库可能堆有更早欠债的遗留候选：按 last_simulated_at 升序取满上限，覆盖本玩家。
            var candidates = await Jobs.ScanColdPlayersAsync(_options.OfflineBatchInterval, 2000);
            var candidate = Assert.Single(candidates, row => row.PlayerId == PlayerId);
            var method = typeof(ColdCatchUpWorker)
                .GetMethod("TryEnqueueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return await (ValueTask<bool>)method.Invoke(worker, [candidate, CancellationToken.None])!;
        }

        /// <summary>
        /// 共享测试库中可能存在其他遗留的到期任务：本地执行器按 target_time 顺序领取全局队列，
        /// 循环驱动直到目标任务进入终态（succeeded/dead）才停，中间处理掉的他人任务属正常队列语义。
        /// </summary>
        public async Task RunUntilJobTerminalAsync(Guid jobId)
        {
            for (var i = 0; i < 64; i++)
            {
                var status = (string?)(await ScalarAsync(
                    "SELECT status FROM platform.compute_jobs WHERE job_id = $1", jobId));
                if (status is "succeeded" or "dead") return;
                if (!await RunOnceAsync()) break;
            }
            var final = await ScalarAsync(
                "SELECT status FROM platform.compute_jobs WHERE job_id = $1", jobId);
            Assert.Fail($"任务未进入终态：status={final}");
        }

        /// <summary>真实本地执行器单步：领取 → 重演 → 统一提交边界写回。</summary>
        public async ValueTask<bool> RunOnceAsync()
        {
            var committer = new ComputeResultCommitter(Source, Sections, Jobs, Catalog, _options,
                NullLogger<ComputeResultCommitter>.Instance);
            var executor = new LocalComputeExecutorWorker(Jobs, committer, new ComputeLinkMetrics(),
                _options, Verifier, NullLogger<LocalComputeExecutorWorker>.Instance);
            var method = typeof(LocalComputeExecutorWorker)
                .GetMethod("RunOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return await (ValueTask<bool>)method.Invoke(executor, [CancellationToken.None])!;
        }

        /// <summary>Npgsql 对 timestamptz 标量返回 DateTime（Kind=Utc）或 DateTimeOffset，统一换算。</summary>
        public async Task<DateTimeOffset> TimeAsync(string sql, params object[] values) =>
            (await ScalarAsync(sql, values)) switch
            {
                DateTimeOffset value => value,
                DateTime value => new DateTimeOffset(value, TimeSpan.Zero),
                var other => throw new InvalidDataException($"timestamptz 标量类型异常：{other?.GetType().Name ?? "null"}")
            };

        public async Task<Guid> JobIdAsync() =>
            (Guid)(await ScalarAsync(
                "SELECT job_id FROM platform.compute_jobs WHERE player_id = $1", PlayerId))!;

        public async Task SqlAsync(string sql, params object[] values)
        {
            await using var command = Source.CreateCommand(sql);
            foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
            await command.ExecuteNonQueryAsync();
        }

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
            Verifier.Dispose();
            await Source.DisposeAsync();
        }
    }
}
