using Domain.Common;
using Domain.Player;
using Domain.Economy;
using Domain.GameData;
using Domain.Simulation;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Xunit;
using CleanSectionCodec = Domain.Persistence.SectionCodec;

namespace Server.Tests;

/// <summary>
/// 玩家 section 持久化集成测试（PostgreSQL）：latest-only 队列合批落盘、
/// 冷挂机结果合批 upsert + 提交头 CAS、action_queue 关系表重建的端到端回归。
/// 依赖 DAOHUA_MARKET_TEST_CONNECTION 指向的专用库。
/// </summary>
[Collection("PostgresIntegration")]
public sealed class SectionRepositoryTests
{
    /// <summary>
    /// 全量 8 段入队后一次 FlushPending 合批落库：提交头建立、section 行齐、
    /// 关系型行动队列按 action_queue 段载荷重建、队列行清空单语句完成，LoadAsync 可整包回读。
    /// </summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task FlushPendingAppliesBatchedSectionsAndRebuildsActionQueue()
    {
        var (source, repository) = await OpenRepositoryAsync();
        var player = await SeedPlayerAsync(source);
        var now = DateTimeOffset.UtcNow;
        var writes = BuildAllSections(player, 1, now, actionCount: 2);

        await repository.EnqueueSectionsAsync(writes);
        Assert.Equal(8L, await Scalar(source,
            "SELECT count(*) FROM platform.section_flush_queue WHERE player_id = $1", player));

        var result = await FlushUntilDrainedAsync(repository, source, player);
        Assert.True(result.Claimed >= 8);

        Assert.Equal(8L, await Scalar(source,
            "SELECT count(*) FROM game.player_state_sections WHERE player_id = $1 AND section_version = 1", player));
        Assert.Equal(2L, await Scalar(source,
            "SELECT count(*) FROM game.player_action_queue WHERE player_id = $1", player));
        Assert.Equal(0L, await Scalar(source,
            "SELECT count(*) FROM platform.section_flush_queue WHERE player_id = $1", player));
        Assert.Equal(1L, await Scalar(source,
            "SELECT state_version FROM game.player_state_heads WHERE player_id = $1", player));

        var bundle = await repository.LoadAsync(player);
        Assert.NotNull(bundle);
        Assert.Equal(1, bundle!.Head.StateVersion);
        Assert.Equal(8, bundle.Sections.Count);
        Assert.Equal(2, bundle.Actions.Count);
        Assert.All(bundle.Actions, action => Assert.Equal("cultivation.draw-qi", action.ActionId));
    }

    /// <summary>
    /// 冷挂机结果合批落库：提交头版本 CAS 通过时整批 upsert + 头更新同事务生效；
    /// 携带未推进版本的 action_queue 段被 upsert 谓词跳过（RETURNING 不含），不得拿旧载荷重建队列。
    /// </summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ApplyComputeResultAppliesBatchedSectionsAtomically()
    {
        var (source, repository) = await OpenRepositoryAsync();
        var player = await SeedPlayerAsync(source);
        var now = DateTimeOffset.UtcNow;
        await repository.EnqueueSectionsAsync(BuildAllSections(player, 1, now, actionCount: 2));
        await FlushUntilDrainedAsync(repository, source, player);

        // v2 结果里 action_queue 故意携带旧版本（与已存相同）：该段被跳过，关系型队列保持 v1 的 2 条。
        var writes = BuildAllSections(player, 2, now.AddMinutes(30), actionCount: 5);
        var staleAction = writes.Single(write => write.SectionKind == PlayerSectionKinds.ActionQueue);
        writes[Array.IndexOf(writes, staleAction)] = staleAction with { StateVersion = 1 };

        var outcome = await repository.ApplyComputeResultAsync(
            player, 1, 2, writes, now.AddMinutes(30), "fp.test");
        Assert.Equal(ComputeApplyOutcome.Applied, outcome);

        Assert.Equal(2L, await Scalar(source,
            "SELECT state_version FROM game.player_state_heads WHERE player_id = $1", player));
        // 除 action_queue 外的 7 段推进到 v2；被跳过的旧版本段仍是 v1。
        Assert.Equal(7L, await Scalar(source,
            "SELECT count(*) FROM game.player_state_sections WHERE player_id = $1 AND section_version = 2", player));
        Assert.Equal(1L, await Scalar(source,
            "SELECT section_version FROM game.player_state_sections WHERE player_id = $1 AND section_kind = 'action_queue'", player));
        Assert.Equal(2L, await Scalar(source,
            "SELECT count(*) FROM game.player_action_queue WHERE player_id = $1", player));
    }

    /// <summary>版本漂移整体丢弃：提交头不在发起版本时不写任何 section；无头玩家返回 MissingPlayer。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ApplyComputeResultDropsStaleVersionAndMissingPlayer()
    {
        var (source, repository) = await OpenRepositoryAsync();
        var player = await SeedPlayerAsync(source);
        var now = DateTimeOffset.UtcNow;
        await repository.EnqueueSectionsAsync(BuildAllSections(player, 1, now, actionCount: 1));
        await FlushUntilDrainedAsync(repository, source, player);

        var writes = BuildAllSections(player, 2, now.AddMinutes(30), actionCount: 4);
        // 源版本错位（实际头已在 v1，谎称 v0 → 版本谓词拒绝；再谎称 v1 但头已推进则同样拒绝）。
        Assert.Equal(ComputeApplyOutcome.StaleVersion,
            await repository.ApplyComputeResultAsync(player, 0, 2, writes, now.AddMinutes(30), "fp.test"));
        Assert.Equal(1L, await Scalar(source,
            "SELECT state_version FROM game.player_state_heads WHERE player_id = $1", player));
        Assert.Equal(8L, await Scalar(source,
            "SELECT count(*) FROM game.player_state_sections WHERE player_id = $1 AND section_version = 1", player));
        Assert.Equal(1L, await Scalar(source,
            "SELECT count(*) FROM game.player_action_queue WHERE player_id = $1", player));

        var missing = await SeedPlayerAsync(source);
        Assert.Equal(ComputeApplyOutcome.MissingPlayer,
            await repository.ApplyComputeResultAsync(missing, 0, 1,
                BuildAllSections(missing, 1, now, actionCount: 0), now, "fp.test"));
        Assert.Equal(0L, await Scalar(source,
            "SELECT count(*) FROM game.player_state_sections WHERE player_id = $1", missing));
    }

    /// <summary>同玩家同 (kind) 重复段在合批 upsert 中只生效首条，与旧逐条语义一致。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ApplyComputeResultDeduplicatesSameKindKeepingFirst()
    {
        var (source, repository) = await OpenRepositoryAsync();
        var player = await SeedPlayerAsync(source);
        var now = DateTimeOffset.UtcNow;
        await repository.EnqueueSectionsAsync(BuildAllSections(player, 1, now, actionCount: 1));
        await FlushUntilDrainedAsync(repository, source, player);

        var writes = BuildAllSections(player, 2, now.AddMinutes(30), actionCount: 2).ToList();
        var progression = writes.Single(write => write.SectionKind == PlayerSectionKinds.Progression);
        // 追加一条同 (player, kind) 但载荷不同的重复写：旧语义下首条生效、后者按版本谓词被跳过。
        writes.Add(progression with
        {
            Payload = CleanSectionCodec.Encode(player, PlayerSectionKinds.Progression, 2,
                new { realmLevel = 999d }, now).Payload
        });

        var outcome = await repository.ApplyComputeResultAsync(
            player, 1, 2, writes, now.AddMinutes(30), "fp.test");
        Assert.Equal(ComputeApplyOutcome.Applied, outcome);
        Assert.Equal(8L, await Scalar(source,
            "SELECT count(*) FROM game.player_state_sections WHERE player_id = $1 AND section_version = 2", player));
    }

    /// <summary>
    /// compute.v2 两类空写落库：空推进（目标版本持平、零 section）只前移提交头模拟时钟与指纹，
    /// 版本与 checksum 不变——防止无变化玩家被无限重派；封顶尾段（版本前进、零 section）照常升级提交头。
    /// 同版本携带 section 属契约违反必须抛错。
    /// </summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ApplyComputeResultEmptyWritesAdvanceHeadOnly()
    {
        var (source, repository) = await OpenRepositoryAsync();
        var player = await SeedPlayerAsync(source);
        var now = DateTimeOffset.UtcNow;
        await repository.EnqueueSectionsAsync(BuildAllSections(player, 1, now, actionCount: 1));
        await FlushUntilDrainedAsync(repository, source, player);
        var checksumBefore = (string)(await Scalar(source,
            "SELECT checksum FROM game.player_state_heads WHERE player_id = $1", player))!;
        var simulatedAt = now.AddMinutes(30);

        // 空推进：版本持平 + 零 writes → 仅前移模拟时钟/指纹。
        var outcome = await repository.ApplyComputeResultAsync(player, 1, 1, [], simulatedAt, "fp.next");
        Assert.Equal(ComputeApplyOutcome.Applied, outcome);
        Assert.Equal(1L, await Scalar(source,
            "SELECT state_version FROM game.player_state_heads WHERE player_id = $1", player));
        Assert.Equal(checksumBefore, await Scalar(source,
            "SELECT checksum FROM game.player_state_heads WHERE player_id = $1", player));
        var simulatedStored = (DateTime)(await Scalar(source,
            "SELECT last_simulated_at FROM game.player_state_heads WHERE player_id = $1", player))!;
        Assert.True(Math.Abs((simulatedStored - simulatedAt.UtcDateTime).TotalMilliseconds) < 1,
            $"last_simulated_at 未前移：stored={simulatedStored:o} expected={simulatedAt.UtcDateTime:o}");
        Assert.Equal("fp.next", await Scalar(source,
            "SELECT game_data_fingerprint FROM game.player_state_heads WHERE player_id = $1", player));

        // 同版本携带 section 属契约违反：脏段必然伴随版本递增。
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ApplyComputeResultAsync(
            player, 1, 1, BuildAllSections(player, 1, now, actionCount: 1), simulatedAt, "fp.test").AsTask());

        // 封顶尾段：版本前进 + 零 writes → 提交头升级、section 表保持 v1。
        var cappedAt = simulatedAt.AddHours(49);
        outcome = await repository.ApplyComputeResultAsync(player, 1, 2, [], cappedAt, "fp.next");
        Assert.Equal(ComputeApplyOutcome.Applied, outcome);
        Assert.Equal(2L, await Scalar(source,
            "SELECT state_version FROM game.player_state_heads WHERE player_id = $1", player));
        Assert.Equal(8L, await Scalar(source,
            "SELECT count(*) FROM game.player_state_sections WHERE player_id = $1 AND section_version = 1", player));
        var cappedStored = (DateTime)(await Scalar(source,
            "SELECT last_simulated_at FROM game.player_state_heads WHERE player_id = $1", player))!;
        Assert.True(Math.Abs((cappedStored - cappedAt.UtcDateTime).TotalMilliseconds) < 1,
            $"last_simulated_at 未前移：stored={cappedStored:o} expected={cappedAt.UtcDateTime:o}");
    }

    /// <summary>真实下线必须递增提交版本；否则已落盘duel同版本拒写会丢失新离线会话起点。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RuntimeUnloadPersistsOfflineReportAtNewSectionAndHeadVersion()
    {
        var (source, repository) = await OpenRepositoryAsync();
        await using var sourceLifetime = source;
        var playerId = await SeedPlayerAsync(source);
        var initial = (await repository.LoadAsync(playerId))!;
        var catalog = GameDataCatalog.LoadEmbedded();
        // 固定过去的模拟窗口，避免flush队列按业务时间暂缓领取未来3.6秒的载荷。
        var createdAt = initial.Identity.CreatedAt.AddMinutes(-1);
        await SqlAsync(source, "UPDATE identity.players SET created_at=$2 WHERE player_id=$1", playerId, createdAt);
        var player = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(initial.Identity.AccountId),
            initial.Identity.DisplayName, createdAt, catalog.Fingerprint);
        player.SetPresence(PresenceState.Online);
        player.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), -1), player.CreatedAt, catalog.Actions);
        var target = player.CreatedAt.AddSeconds(3.6);
        SimulationEngine.AdvanceTo(player, target, catalog, new AssetWallet(player.PlayerId));
        // 固定v7复现真实回放已有duel v7、下线后应提交v8的冲突边界。
        var seeded = player.ExportState() with { StateVersion = 7 };
        await repository.EnqueueSectionsAsync(CleanSectionCodec.EncodeAggregateFull(seeded, DateTimeOffset.UtcNow));
        await FlushUntilDrainedAsync(repository, source, playerId);
        await using var runtime = EcsRuntimeFactory.Create(repository, new PostgresAssetRepository(source),
            new PostgresEconomyRepository(source, NullLogger<PostgresEconomyRepository>.Instance),
            catalog, simulationClock: new FixedSimulationClock(target));
        var loaded = await runtime.LoadAsync(playerId);
        Assert.Equal(7, loaded.StateVersion);
        Assert.Equal(0, loaded.State.Duel!.OfflineReport!.StartedAtUnixSeconds);

        await runtime.RemoveAsync(playerId, flush: true);
        Assert.Equal(3L, await Scalar(source,
            "SELECT count(*) FROM platform.section_flush_queue WHERE player_id=$1 AND state_version=8 AND section_kind IN ('duel','settings','progression')", playerId));
        await FlushUntilDrainedAsync(repository, source, playerId);
        Assert.Equal(3L, await Scalar(source,
            "SELECT count(*) FROM game.player_state_sections WHERE player_id=$1 AND section_version=8 AND section_kind IN ('duel','settings','progression')", playerId));
        Assert.Equal(8L, await Scalar(source,
            "SELECT state_version FROM game.player_state_heads WHERE player_id=$1", playerId));
        var persisted = CleanSectionCodec.DecodeAggregate((await repository.LoadAsync(playerId))!);
        Assert.Equal(PresenceState.Offline, persisted.Presence);
        Assert.Equal(8, persisted.StateVersion);
        Assert.Equal(target.ToUnixTimeMilliseconds() / 1000d, persisted.Duel!.OfflineReport!.StartedAtUnixSeconds);
        Assert.Equal(persisted.Duel.OfflineReport.StartedAtUnixSeconds, persisted.Duel.OfflineReport.EndedAtUnixSeconds);
    }

    private sealed class FixedSimulationClock(DateTimeOffset value) : IPlayerSimulationClock
    {
        public DateTimeOffset UtcNow => value;
        public bool IsDeterministic => true;
        public void AdvanceTo(DateTimeOffset target) => throw new NotSupportedException();
    }

    private static SectionWrite[] BuildAllSections(Guid player, long version, DateTimeOffset now, int actionCount)
    {
        var writes = new List<SectionWrite>(PlayerSectionKinds.All.Count);
        foreach (var kind in PlayerSectionKinds.All)
        {
            object payload = kind == PlayerSectionKinds.ActionQueue
                ? Enumerable.Range(0, actionCount).Select(_ => new QueuedActionState(
                    Guid.NewGuid(),
                    new ActionIntent(new ActionId("cultivation.draw-qi"), -1),
                    now, ActionQueueStatus.Pending, 0, -1, 0)).ToList()
                : new { kind, version };
            writes.Add(CleanSectionCodec.Encode(player, kind, version, payload, now) with
            {
                GameDataFingerprint = "fp.test",
                LastSimulatedAt = now
            });
        }
        return writes.ToArray();
    }

    private static async Task<(NpgsqlDataSource Source, PostgresSectionRepository Repository)> OpenRepositoryAsync()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("section 仓储集成测试");
        var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        return (source, new PostgresSectionRepository(source, NullLogger<PostgresSectionRepository>.Instance));
    }

    /// <summary>
    /// 循环刷盘直到本玩家队列清空：队列认领是全库口径，其他测试遗留的行会被一并领取，
    /// 所以单批 FlushPendingAsync 不保证本玩家行全部落盘，也不能对 Claimed/Applied 做等值断言。
    /// </summary>
    private static async Task<FlushBatchResult> FlushUntilDrainedAsync(
        PostgresSectionRepository repository, NpgsqlDataSource source, Guid player)
    {
        FlushBatchResult result = new(0, 0, 0, TimeSpan.Zero);
        for (var guard = 0; guard < 8; guard++)
        {
            result = await repository.FlushPendingAsync(256, TimeSpan.FromMinutes(1));
            Assert.Equal(0, result.Failed);
            var pending = (long)(await Scalar(source,
                "SELECT count(*) FROM platform.section_flush_queue WHERE player_id = $1", player))!;
            if (pending == 0) return result;
        }
        throw new InvalidOperationException("本玩家 flush 队列未在有限批次内清空。");
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

    private static async Task<object?> Scalar(NpgsqlDataSource source, string sql, params object[] values)
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
