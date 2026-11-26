using System.Text.Json;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Protocol;
using Server.Clean;
using Server.Clean.Leaderboards;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 道友攻略板块与角色名片配装付费查阅（TASK-20261124-02）确定性与真实 PostgreSQL 集成测试：
/// 1. 校验万字 Unicode Rune 计数、10% 手续费向下取整、付费帖导语不泄露正文及北京时间零点窗口计算；
/// 2. 校验个人榜 Top 50 发帖资格（排除未上榜与宗门榜）、每日 1 帖硬上限（删帖不退额度，跨零点重置）、
///    付费解锁、自由打赏、热度/打赏双排序与第⑤类白名单账本 guide.reward 沉淀；
/// 3. 校验隐私付费查看角色名片配装（滑点保护、privacy.inspect 结算、当日零点前有效、跨过零点自动恢复隐藏）。
/// </summary>
[Collection("PostgresIntegration")]
public sealed class GuideAndInspectIntegrationTests
{
    private sealed class MutableSimulationClock(DateTimeOffset initialUtc) : IPlayerSimulationClock
    {
        public DateTimeOffset UtcNow { get; set; } = initialUtc;
        public bool IsDeterministic => true;
        public void AdvanceTo(DateTimeOffset target) => UtcNow = target;
    }

    [Fact]
    public void PureRules_Validate10kRunesTaxAndCstMidnightWindow()
    {
        // 含四字节 Emoji 与颜文字的 10,000 字正文合法，10,001 字拒绝。
        var body10k = string.Concat(Enumerable.Repeat("剑😀", 5000));
        Assert.Equal(10_000, GuideRuntime.CountUnicodeRunes(body10k));
        var validated = GuideRuntime.ValidatePostContent("剑修攻略", "导语", body10k, 100d);
        Assert.Equal(100d, validated.UnlockPrice);

        var bodyOver = body10k + "溢";
        var ex = Assert.Throws<GuideOperationException>(() =>
            GuideRuntime.ValidatePostContent("超长攻略", "", bodyOver, 0d));
        Assert.Equal(GuideOperationFailure.InvalidArgument, ex.Failure);

        // 10% 手续费向下取整：9 灵石税 0 实收 9；100 灵石税 10 实收 90。
        Assert.Equal((0d, 9d), PostgresGuideRepository.ComputeTaxAndNet(9d));
        Assert.Equal((10d, 90d), PostgresGuideRepository.ComputeTaxAndNet(100d));

        // 付费帖未填导语时绝不截取泄露正文。
        Assert.Equal("", PostgresGuideRepository.BuildSafeExcerpt("", "核心绝密功法连招", unlockPrice: 50d));
        Assert.Contains("公开心得", PostgresGuideRepository.BuildSafeExcerpt("", "公开心得正文", unlockPrice: 0d));

        // 北京时间 2026-11-24 23:30:00+08:00 (UTC 15:30) -> 当日结束零点为 2026-11-25 00:00:00+08:00 (UTC 16:00)。
        var sampleUtc = new DateTimeOffset(2026, 11, 24, 15, 30, 0, TimeSpan.Zero);
        var (accessDate, nextMidnightUtc) = PostgresGuideRepository.ResolveCstDayWindow(sampleUtc);
        Assert.Equal(new DateOnly(2026, 11, 24), accessDate);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 16, 0, 0, TimeSpan.Zero), nextMidnightUtc);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task GuideLifecycle_EnforcesTop50PersonalRank_DailyLimit_Paywall_Tipping_AndDualSorting()
    {
        var source = PostgresTestSchema.GetSharedDataSource(PostgresTestSchema.RequireConnectionString("道友攻略全链路测试"));
        await PostgresTestSchema.EnsureAsync(source);

        var catalog = GameDataCatalog.LoadEmbedded();
        var sections = new PostgresSectionRepository(source, NullLogger<PostgresSectionRepository>.Instance);
        // 北京时间 2026-11-24 20:00:00+08:00 (UTC 12:00)
        var clock = new MutableSimulationClock(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));

        var rankedAuthorId = Guid.NewGuid();
        var secondRankedId = Guid.NewGuid();
        var sectOnlyPlayerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var sectId = Guid.NewGuid();

        await SeedPlayersAsync(
            source,
            clock.UtcNow,
            sections,
            catalog.Fingerprint,
            (rankedAuthorId, "榜首剑修", 45, 500d, true, 0d),
            (secondRankedId, "丹道大师", 38, 500d, true, 0d),
            (sectOnlyPlayerId, "宗门杂役", 1, 500d, true, 0d),
            (viewerId, "求道散修", 10, 1000d, true, 0d));

        // 构造榜单名次：rankedAuthorId 在境界榜 #1，secondRankedId 在炼丹榜 #2；
        // sectOnlyPlayerId 仅在宗门榜或名次 > 50（51 名），无个人榜 Top 50 资格。
        await using (var conn = await source.OpenConnectionAsync())
        {
            var sectName = $"太初{sectId:N}"[..12];
            await using var batch = new NpgsqlBatch(conn)
            {
                BatchCommands =
                {
                    new NpgsqlBatchCommand("DELETE FROM game.leaderboard_ranks WHERE player_id = ANY($1)")
                    {
                        Parameters = { new NpgsqlParameter { Value = new[] { rankedAuthorId, secondRankedId, sectOnlyPlayerId, viewerId } } }
                    },
                    new NpgsqlBatchCommand("""
                        INSERT INTO game.leaderboard_ranks(player_id, board_id, rank, score, updated_at)
                        VALUES ($1, 'realm', 1, 45, $4),
                               ($2, 'technique.alchemy', 2, 20, $4),
                               ($3, 'realm', 51, 1, $4)
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = rankedAuthorId },
                            new NpgsqlParameter { Value = secondRankedId },
                            new NpgsqlParameter { Value = sectOnlyPlayerId },
                            new NpgsqlParameter { Value = clock.UtcNow },
                        }
                    },
                    new NpgsqlBatchCommand("""
                        INSERT INTO social.zongmen(zongmen_id, name, name_normalized, prefix, leader_id, anchor_node_id, created_at)
                        VALUES ($1, $2, $2, '剑', $3, 'node.yunxi-village', $4)
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = sectId },
                            new NpgsqlParameter { Value = sectName },
                            new NpgsqlParameter { Value = sectOnlyPlayerId },
                            new NpgsqlParameter { Value = clock.UtcNow },
                        }
                    },
                    new NpgsqlBatchCommand("""
                        INSERT INTO social.zongmen_members(zongmen_id, player_id, role, joined_at)
                        VALUES ($1, $2, 0, $3)
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = sectId },
                            new NpgsqlParameter { Value = sectOnlyPlayerId },
                            new NpgsqlParameter { Value = clock.UtcNow },
                        }
                    },
                }
            };
            await batch.ExecuteNonQueryAsync();
        }

        // 未装载预热内存快照时，LeaderboardRuntime 回退直读 game.leaderboard_ranks 表
        var leaderboards = new LeaderboardRuntime(source, sections, new NullPlayerRuntime(), null, catalog, NullLogger<LeaderboardRuntime>.Instance);
        var guideRepo = new PostgresGuideRepository(source);
        var guideRuntime = new GuideRuntime(guideRepo, leaderboards, catalog, clock: clock);

        // 1. 排名 51（未进前 50）且仅在宗门中的玩家无发帖资格
        var unrankedPerms = await guideRuntime.GetPermissionsAsync(sectOnlyPlayerId);
        Assert.False(unrankedPerms.IsRanked);
        Assert.False(unrankedPerms.CanCreate);
        var notRankedEx = await Assert.ThrowsAsync<GuideOperationException>(() =>
            guideRuntime.CreateAsync(sectOnlyPlayerId, new CreateGuidePostRequest("越权帖", "", "正文", 0)).AsTask());
        Assert.Equal(GuideOperationFailure.NotRanked, notRankedEx.Failure);

        // 2. 榜首剑修发布付费攻略（解锁价 100 灵石），含内置表情、颜文字与图片直链
        var createdPaid = await guideRuntime.CreateAsync(
            rankedAuthorId,
            new CreateGuidePostRequest(
                "破军一剑流终极指南",
                "筑基期越阶斩妖核心配装导语",
                "# 核心配装\n起手 [微笑] 配合 (๑•̀ㅂ•́)و✧ https://example.com/build.webp",
                UnlockPrice: 100d));
        Assert.False(createdPaid.Locked);
        Assert.NotNull(createdPaid.AuthorTopRank);
        Assert.Equal(1, createdPaid.AuthorTopRank!.Rank);

        // 3. 同日再次发布第 2 篇被每日限额拦截
        var quotaEx = await Assert.ThrowsAsync<GuideOperationException>(() =>
            guideRuntime.CreateAsync(rankedAuthorId, new CreateGuidePostRequest("同日第二篇", "", "正文", 0)).AsTask());
        Assert.Equal(GuideOperationFailure.DailyQuotaExceeded, quotaEx.Failure);

        // 4. 第二位上榜修士发布免费高赞攻略（用于对比热度排序 vs 打赏排序）
        var createdFree = await guideRuntime.CreateAsync(
            secondRankedId,
            new CreateGuidePostRequest("炼丹冲级省材攻略", "", "免费公开正文", UnlockPrice: 0d));
        await guideRuntime.SetLikeAsync(viewerId, Guid.Parse(createdFree.PostId), true);
        await guideRuntime.SetLikeAsync(rankedAuthorId, Guid.Parse(createdFree.PostId), true);
        await guideRuntime.SetLikeAsync(sectOnlyPlayerId, Guid.Parse(createdFree.PostId), true);

        // 5. 未解锁读者查看付费帖：locked=true 且 body 严格为 null，不泄露正文
        var lockedView = await guideRuntime.GetDetailAsync(viewerId, Guid.Parse(createdPaid.PostId));
        Assert.True(lockedView.Locked);
        Assert.Null(lockedView.Body);
        Assert.Equal("筑基期越阶斩妖核心配装导语", lockedView.Summary);

        // 6. 读者支付 100 灵石永久解锁付费帖，再追加打赏 200 灵石（共付 300，作者实得 90 + 180 = 270）
        var unlockedView = await guideRuntime.UnlockAsync(
            viewerId,
            Guid.Parse(createdPaid.PostId),
            new UnlockGuidePostRequest("req-unlock-1", ExpectedPrice: 100d));
        Assert.False(unlockedView.Locked);
        Assert.Contains("https://example.com/build.webp", unlockedView.Body);

        var tipRes = await guideRuntime.TipAsync(
            viewerId,
            Guid.Parse(createdPaid.PostId),
            new TipGuidePostRequest("req-tip-1", Amount: 200d));
        Assert.Equal(300d, tipRes.TotalRewardStones);
        Assert.Equal(1, tipRes.TipperCount);

        Assert.Equal(700d, await ReadSpiritStonesAsync(source, viewerId));
        Assert.Equal(770d, await ReadSpiritStonesAsync(source, rankedAuthorId));

        // 7. 排序对比：reward 打赏排序下 300 灵石的付费帖排在 0 灵石的免费帖前面
        var byReward = await guideRuntime.ListAsync(viewerId, "reward", mineOnly: false);
        var targetIdsInReward = byReward.Items
            .Where(i => i.PostId == createdPaid.PostId || i.PostId == createdFree.PostId)
            .Select(i => i.PostId)
            .ToArray();
        Assert.Equal([createdPaid.PostId, createdFree.PostId], targetIdsInReward);

        // 8. 删帖不退当日额度；跨过北京时间次日零点后恢复可发布
        await guideRuntime.DeleteAsync(secondRankedId, Guid.Parse(createdFree.PostId));
        var afterDeleteEx = await Assert.ThrowsAsync<GuideOperationException>(() =>
            guideRuntime.CreateAsync(secondRankedId, new CreateGuidePostRequest("删后重发", "", "正文", 0)).AsTask());
        Assert.Equal(GuideOperationFailure.DailyQuotaExceeded, afterDeleteEx.Failure);

        // 拨至北京时间次日 00:05:00+08:00 (UTC 16:05)
        clock.AdvanceTo(new DateTimeOffset(2026, 11, 24, 16, 5, 0, TimeSpan.Zero));
        var nextDayPost = await guideRuntime.CreateAsync(
            secondRankedId,
            new CreateGuidePostRequest("次日新篇", "", "跨零点恢复额度", 0));
        Assert.Equal("次日新篇", nextDayPost.Title);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PaidLoadoutInspection_UnlocksUntilBeijingMidnight_AndExpiresAfterZeroHour()
    {
        var source = PostgresTestSchema.GetSharedDataSource(PostgresTestSchema.RequireConnectionString("名片配装付费查阅测试"));
        await PostgresTestSchema.EnsureAsync(source);

        var catalog = GameDataCatalog.LoadEmbedded();
        var sections = new PostgresSectionRepository(source, NullLogger<PostgresSectionRepository>.Instance);
        // 北京时间 2026-11-24 23:50:00+08:00 (UTC 15:50)
        var clock = new MutableSimulationClock(new DateTimeOffset(2026, 11, 24, 15, 50, 0, TimeSpan.Zero));

        var targetId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();

        await SeedPlayersAsync(
            source,
            clock.UtcNow,
            sections,
            catalog.Fingerprint,
            (targetId, "隐世高人", 35, 100d, false, 80d),
            (viewerId, "观摩道友", 20, 500d, true, 0d));

        var socialRepo = new PostgresSocialRepository(source);
        var chatRepo = new PostgresChatRepository(source);
        var presence = new OnlinePresenceRegistry();
        var runtimeStub = new NullPlayerRuntime();
        var valuation = new PartyValuationService(runtimeStub, sections, null, catalog);
        var chatRuntime = new ChatRuntime(chatRepo, socialRepo, presence);
        var guideRepo = new PostgresGuideRepository(source);
        var leaderboards = new LeaderboardRuntime(source, sections, runtimeStub, null, catalog, NullLogger<LeaderboardRuntime>.Instance);
        var assetRepo = new PostgresAssetRepository(source);

        var socialRuntime = new SocialRuntime(
            socialRepo,
            presence,
            valuation,
            chatRuntime,
            catalog,
            NullLogger<SocialRuntime>.Instance,
            simulationClock: clock,
            sections: sections,
            assetBalances: assetRepo,
            guides: guideRepo);

        var guideRuntime = new GuideRuntime(
            guideRepo,
            leaderboards,
            catalog,
            sectionRepository: sections,
            clock: clock);

        // 1. 付费前查询名片：配装隐藏，且返回查阅价格 80 灵石
        var hiddenCard = await socialRuntime.BuildPlayerCardAsync(viewerId, new PlayerCardRequestDto("req-1", targetId.ToString("D")));
        Assert.True(hiddenCard.LoadoutHidden);
        Assert.Equal(80d, hiddenCard.LoadoutInspectPrice);
        Assert.Null(hiddenCard.Equipment);

        // 2. 防涨价滑点校验：客户端预期 50 < 实际 80 时拒绝扣款
        var priceEx = await Assert.ThrowsAsync<GuideOperationException>(() =>
            guideRuntime.UnlockPlayerLoadoutAsync(viewerId, targetId, new UnlockPlayerLoadoutRequest("req-low", 50d)).AsTask());
        Assert.Equal(GuideOperationFailure.PriceChanged, priceEx.Failure);

        // 3. 按 80 灵石付费解锁（访客 -80 -> 420，目标 +72 -> 172）
        var unlockResp = await guideRuntime.UnlockPlayerLoadoutAsync(
            viewerId,
            targetId,
            new UnlockPlayerLoadoutRequest("req-ok", 80d));
        Assert.Equal(80d, unlockResp.PaidStones);
        Assert.Equal(72d, unlockResp.TargetReceivedStones);
        Assert.Equal(420d, await ReadSpiritStonesAsync(source, viewerId));
        Assert.Equal(172d, await ReadSpiritStonesAsync(source, targetId));

        // 4. 北京时间当日 23:59:59+08:00 (UTC 15:59:59) 仍可查看完整配装
        clock.AdvanceTo(new DateTimeOffset(2026, 11, 24, 15, 59, 59, TimeSpan.Zero));
        var unlockedCard = await socialRuntime.BuildPlayerCardAsync(viewerId, new PlayerCardRequestDto("req-2", targetId.ToString("D")));
        Assert.False(unlockedCard.LoadoutHidden);
        Assert.NotNull(unlockedCard.Equipment);
        Assert.NotNull(unlockedCard.LoadoutUnlockedUntilUnixSeconds);

        // 5. 跨过北京时间当日零点（2026-11-25 00:00:01+08:00 / UTC 16:00:01），授权立即失效，恢复隐藏态
        clock.AdvanceTo(new DateTimeOffset(2026, 11, 24, 16, 0, 1, TimeSpan.Zero));
        var expiredCard = await socialRuntime.BuildPlayerCardAsync(viewerId, new PlayerCardRequestDto("req-3", targetId.ToString("D")));
        Assert.True(expiredCard.LoadoutHidden);
        Assert.Equal(80d, expiredCard.LoadoutInspectPrice);
        Assert.Null(expiredCard.Equipment);
    }

    private static async Task SeedPlayersAsync(
        NpgsqlDataSource source,
        DateTimeOffset now,
        PostgresSectionRepository sections,
        string fingerprint,
        params (Guid Id, string Name, int Realm, double Stones, bool PublicLoadout, double InspectPrice)[] specs)
    {
        var allWrites = new List<SectionWrite>();
        await using (var conn = await source.OpenConnectionAsync())
        {
            foreach (var spec in specs)
            {
                await using var batch = new NpgsqlBatch(conn)
                {
                    BatchCommands =
                    {
                        new NpgsqlBatchCommand("""
                            INSERT INTO identity.accounts(account_id, login_normalized, password_hash, password_salt, password_iterations, created_at, updated_at, is_bot)
                            VALUES ($1, $2, '\x00'::bytea, '\x00'::bytea, 100000, $3, $3, false)
                            """)
                        {
                            Parameters =
                            {
                                new NpgsqlParameter { Value = spec.Id },
                                new NpgsqlParameter { Value = $"login_{spec.Id:N}" },
                                new NpgsqlParameter { Value = now },
                            }
                        },
                        new NpgsqlBatchCommand("""
                            INSERT INTO identity.players(player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at, status)
                            VALUES ($1, $1, $2, $3, $4, $4, 1)
                            """)
                        {
                            Parameters =
                            {
                                new NpgsqlParameter { Value = spec.Id },
                                new NpgsqlParameter { Value = spec.Name },
                                new NpgsqlParameter { Value = $"NAME_{spec.Id:N}" },
                                new NpgsqlParameter { Value = now },
                            }
                        },
                        new NpgsqlBatchCommand("""
                            INSERT INTO economy.player_asset_balances(player_id, asset_id, variant, quantity, version, updated_at)
                            VALUES ($1, 'currency.spirit-stone', '', $2, 1, $3)
                            """)
                        {
                            Parameters =
                            {
                                new NpgsqlParameter { Value = spec.Id },
                                new NpgsqlParameter { Value = spec.Stones },
                                new NpgsqlParameter { Value = now },
                            }
                        },
                    }
                };
                await batch.ExecuteNonQueryAsync();

                var agg = PlayerAggregate.Create(new(spec.Id), new(spec.Id), spec.Name, now, fingerprint);
                agg.Progression.RealmLevel = spec.Realm;
                agg.SetLoadoutPrivacy(spec.PublicLoadout, spec.InspectPrice);
                allWrites.AddRange(SectionCodec.EncodeAggregateFull(agg.ExportState(), now));
            }
        }

        await sections.EnqueueSectionsAsync(allWrites);
        await sections.FlushPendingAsync(256, TimeSpan.FromSeconds(30));
    }

    private static async Task<double> ReadSpiritStonesAsync(NpgsqlDataSource source, Guid playerId)
    {
        await using var conn = await source.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT COALESCE((
                SELECT quantity FROM economy.player_asset_balances
                WHERE player_id = $1 AND asset_id = 'currency.spirit-stone' AND variant = ''
            ), 0)
            """, conn);
        cmd.Parameters.AddWithValue(playerId);
        return Convert.ToDouble(await cmd.ExecuteScalarAsync() ?? 0d);
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
