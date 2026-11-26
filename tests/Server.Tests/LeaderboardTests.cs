using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Domain.Combat;
using Domain.Economy;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Domain.Social;
using Domain.World;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Protocol;
using Server;
using Server.Clean;
using Server.Clean.Leaderboards;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 全服排行榜（TASK-20261113-08）契约与物化回归：
/// 覆盖 12 榜排序与同分决胜 / Top 50 截断 / 零值剔除、HTTP 端点 401/404/503/200/304 及防串号 ETag，
/// 以及真实 PostgreSQL 投影列提取 + 整点 RebuildAsync + 启机数据库快速恢复闭环。
/// </summary>
[Collection("PostgresIntegration")]
public sealed class LeaderboardTests
{
    private static readonly Guid PlayerA = Guid.Parse("50000000-0000-4000-8000-000000000001");
    private static readonly Guid PlayerB = Guid.Parse("50000000-0000-4000-8000-000000000002");
    private static readonly Guid UnrankedPlayer = Guid.Parse("50000000-0000-4000-8000-000000000003");

    /// <summary>纯汇编器验证 12 个固定榜单顺序、零值剔除、同分决胜与 Top 50 之外的名次保留。</summary>
    [Fact]
    public void Assembler_BuildsAll12BoardsWithDeterministicSortingAndMyRanks()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var now = new DateTimeOffset(2026, 11, 14, 15, 22, 30, TimeSpan.Zero);

        var players = new List<LeaderboardPlayerInput>(60);
        for (var i = 1; i <= 55; i++)
        {
            var id = Guid.Parse($"51000000-0000-4000-8000-{i:000000000000}");
            var techExps = new Dictionary<string, double>(StringComparer.Ordinal);
            if (i <= 3)
            {
                techExps["alchemy"] = 1000 + i * 100;
            }
            players.Add(new LeaderboardPlayerInput(
                PlayerId: id,
                PlayerSeq: 1000 + i,
                DisplayName: $"修士{i}",
                Avatar: "player",
                RealmLevel: i <= 2 ? 40 : 10,
                Experience: i * 100d,
                EquipmentValuation: i == 1 ? 5000 : 1000 - i,
                AbilitiesValuation: i == 2 ? 6000 : 500,
                ToolsValuation: 100,
                SpiritStones: i == 5 ? 99999 : (i <= 50 ? i * 10 : 0),
                DuelWins: i == 4 ? 12 : (i == 5 ? 12 : 0),
                DuelLosses: i == 4 ? 1 : (i == 5 ? 9 : 0),
                TechniqueExperiences: techExps));
        }

        // Player 1 和 Player 2 同为 40 层境界，Player 2 修为（200）> Player 1（100）应排第 1。
        var sectA = Guid.Parse("52000000-0000-4000-8000-000000000001");
        var sectB = Guid.Parse("52000000-0000-4000-8000-000000000002");
        var zongmens = new[]
        {
            new LeaderboardZongmenInput(sectA, "青云宗", "青", 8, 3, now.AddDays(-1), [players[0].PlayerId, players[1].PlayerId]),
            new LeaderboardZongmenInput(sectB, "太初阁", "太", 8, 5, now.AddDays(-2), [players[2].PlayerId]),
        };

        var result = LeaderboardAssembler.Assemble(now, players, zongmens, catalog.RealmProgression);

        Assert.Equal(new DateTimeOffset(2026, 11, 14, 16, 0, 0, TimeSpan.Zero), result.NextComputeAt);
        Assert.Equal(12, result.Boards.Count);
        Assert.Equal(LeaderboardAssembler.CanonicalBoards.Select(b => b.BoardId), result.Index.Boards.Select(b => b.BoardId));

        // 1. 境界榜：Top 50 截断，全体 55 人都在 MyRanks 中；同 40 级时 Experience 高者居首。
        var realm = result.Boards[LeaderboardBoardIds.Realm];
        Assert.Equal(55, realm.Snapshot.ParticipantCount);
        Assert.Equal(LeaderboardAssembler.TopN, realm.Snapshot.Entries.Count);
        Assert.Equal(55, realm.MyRanks.Count);
        Assert.Equal(players[1].PlayerId.ToString("D"), realm.Snapshot.Entries[0].PlayerId);
        Assert.Equal(players[0].PlayerId.ToString("D"), realm.Snapshot.Entries[1].PlayerId);
        Assert.Equal(55, realm.MyRanks[players[2].PlayerId].Rank);

        // 2. 技艺榜：剔除 exp <= 0 玩家（仅 3 人入榜）。
        var alchemy = result.Boards[LeaderboardBoardIds.Technique("alchemy")];
        Assert.Equal(3, alchemy.Snapshot.ParticipantCount);
        Assert.Equal(players[2].PlayerId.ToString("D"), alchemy.Snapshot.Entries[0].PlayerId);
        Assert.True(alchemy.Snapshot.Entries[0].TechniqueLevel > 0);
        Assert.False(alchemy.MyRanks.ContainsKey(players[10].PlayerId));

        // 3. 身家榜：Player 2 总身家 = 998(装备) + 6000(功法) + 100(工具) = 7098 > Player 1 总身家 5600。
        var valuation = result.Boards[LeaderboardBoardIds.Valuation];
        Assert.Equal(players[1].PlayerId.ToString("D"), valuation.Snapshot.Entries[0].PlayerId);
        Assert.Equal(7098d, valuation.Snapshot.Entries[0].Valuation);
        Assert.Equal(6000d, valuation.Snapshot.Entries[0].ValuationBreakdown?.Abilities);

        // 4. 灵石榜：排除灵石为 0 玩家（51..55 被剔除），Player 5 排第一。
        var stone = result.Boards[LeaderboardBoardIds.SpiritStone];
        Assert.Equal(50, stone.Snapshot.ParticipantCount);
        Assert.Equal(players[4].PlayerId.ToString("D"), stone.Snapshot.Entries[0].PlayerId);

        // 5. 斗法榜：排除 0 胜玩家，同胜场 12 时负场少（1 < 9）排前。
        var duel = result.Boards[LeaderboardBoardIds.Duel];
        Assert.Equal(2, duel.Snapshot.ParticipantCount);
        Assert.Equal(players[3].PlayerId.ToString("D"), duel.Snapshot.Entries[0].PlayerId);
        Assert.Equal(1d, duel.MyRanks[players[3].PlayerId].DuelLosses);

        // 6. 宗门榜：同 8 名弟子时大殿等级高（5 > 3）排前；成员均可反查本宗名次。
        var sect = result.Boards[LeaderboardBoardIds.Zongmen];
        Assert.Equal(2, sect.Snapshot.ParticipantCount);
        Assert.Equal(sectB.ToString("D"), sect.Snapshot.Entries[0].ZongmenId);
        Assert.Equal(1, sect.MyRanks[players[2].PlayerId].Rank);
        Assert.Equal(2, sect.MyRanks[players[0].PlayerId].Rank);
        Assert.Equal(2, sect.MyRanks[players[1].PlayerId].Rank);
    }

    /// <summary>个人荣誉头衔（传音最高排名徽标 / 攻略作者头衔）内存快照查询：仅取个人 11 榜 Top 50、排除宗门榜、同排名按 境界 &gt; 斗法 &gt; 身家 &gt; 灵石 &gt; 七技艺 裁决。</summary>
    [Fact]
    public async Task PersonalHonorRankLookup_RespectsPriorityOrderTop50CutoffAndExcludesZongmen()
    {
        // 验证 PersonalHonorBoards 固定 11 榜且顺序严格一致
        var expectedOrder = new[]
        {
            LeaderboardBoardIds.Realm,
            LeaderboardBoardIds.Duel,
            LeaderboardBoardIds.Valuation,
            LeaderboardBoardIds.SpiritStone,
            LeaderboardBoardIds.Technique("alchemy"),
            LeaderboardBoardIds.Technique("forging"),
            LeaderboardBoardIds.Technique("talisman"),
            LeaderboardBoardIds.Technique("enhancement"),
            LeaderboardBoardIds.Technique("formation"),
            LeaderboardBoardIds.Technique("gathering"),
            LeaderboardBoardIds.Technique("mining"),
        };
        Assert.Equal(expectedOrder, LeaderboardAssembler.PersonalHonorBoards.Select(b => b.BoardId));

        var catalog = GameDataCatalog.LoadEmbedded();
        var unusedSource = NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused");
        var sections = new PostgresSectionRepository(unusedSource, NullLogger<PostgresSectionRepository>.Instance);
        var runtime = new LeaderboardRuntime(
            unusedSource, sections, new StubPlayerRuntime(), market: null, catalog, NullLogger<LeaderboardRuntime>.Instance);

        var pRealmKing = Guid.NewGuid();
        var pDuelTie = Guid.NewGuid();
        var pValuationTie = Guid.NewGuid();
        var pAlchemyKing = Guid.NewGuid();
        var pSectOnlyRank51 = Guid.NewGuid();

        // 构造 52 名玩家使 pSectOnlyRank51 在境界榜落到第 52（榜外），且持有宗门榜第 1。
        var inputs = new List<LeaderboardPlayerInput>(52)
        {
            // pRealmKing：境界第 1、斗法第 1、身家第 1 → 同为第 1 时境界胜出
            new(pRealmKing, 1, "境界尊者", "player", 60, 100, EquipmentValuation: 9000, SpiritStones: 9000, DuelWins: 99,
                TechniqueExperiences: new Dictionary<string, double> { ["forging"] = 9000 }),
            // pDuelTie：境界第 2、斗法第 2、身家第 2、灵石第 2、炼丹第 2 → 同为第 2，但先设境界为第 5（更靠后），验证斗法 #2 战胜身家/灵石/炼丹 #2
            new(pDuelTie, 2, "斗法剑修", "player", 50, 100, EquipmentValuation: 8000, SpiritStones: 8000, DuelWins: 50,
                TechniqueExperiences: new Dictionary<string, double> { ["alchemy"] = 8000 }),
            // pValuationTie：身家第 3、灵石第 3、炼丹第 3（无斗法胜场）→ 验证身家 #3 战胜灵石/炼丹 #3
            new(pValuationTie, 3, "身家巨贾", "player", 40, 100, EquipmentValuation: 7000, SpiritStones: 7000, DuelWins: 0,
                TechniqueExperiences: new Dictionary<string, double> { ["alchemy"] = 7000 }),
            // pAlchemyKing：境界第 4，但炼丹第 1 → 绝对名次 #1 压倒境界 #4
            new(pAlchemyKing, 4, "丹道宗师", "player", 45, 100, EquipmentValuation: 100, SpiritStones: 100, DuelWins: 0,
                TechniqueExperiences: new Dictionary<string, double> { ["alchemy"] = 99999 }),
        };
        for (var i = 0; i < 47; i++)
        {
            // 填充 47 名 55 级修士，将 pDuelTie/pValuationTie/pAlchemyKing/pSectOnlyRank51 境界压到靠后位置
            inputs.Add(new LeaderboardPlayerInput(Guid.NewGuid(), 10 + i, $"众修{i}", "player", 55, 10 + i));
        }
        inputs.Add(new LeaderboardPlayerInput(pSectOnlyRank51, 99, "大宗萌新", "player", 1, 0));

        var zongmens = new[]
        {
            new LeaderboardZongmenInput(Guid.NewGuid(), "太初第一宗", "太初", 1, 10, DateTimeOffset.UtcNow, [pSectOnlyRank51]),
        };
        var assembly = LeaderboardAssembler.Assemble(DateTimeOffset.UtcNow, inputs, zongmens, catalog.RealmProgression);
        runtime.SeedSnapshotForTesting(assembly);

        var bestRealm = await runtime.GetBestPersonalRankAsync(pRealmKing);
        Assert.Equal(new GuideRankBadgeDto(LeaderboardBoardIds.Realm, "realm", null, 1), bestRealm);

        var bestDuel = await runtime.GetBestPersonalRankAsync(pDuelTie);
        Assert.Equal(new GuideRankBadgeDto(LeaderboardBoardIds.Duel, "duel", null, 2), bestDuel);

        var bestVal = await runtime.GetBestPersonalRankAsync(pValuationTie);
        Assert.Equal(new GuideRankBadgeDto(LeaderboardBoardIds.Valuation, "valuation", null, 3), bestVal);

        var bestAlchemy = await runtime.GetBestPersonalRankAsync(pAlchemyKing);
        Assert.Equal(new GuideRankBadgeDto(LeaderboardBoardIds.Technique("alchemy"), "technique", "alchemy", 1), bestAlchemy);

        // 宗门第 1 + 境界第 52（超 Top 50）不授予个人聊天/攻略徽标
        Assert.Null(await runtime.GetBestPersonalRankAsync(pSectOnlyRank51));

        // 批量查询与单查口径一致
        var batch = await runtime.GetBestPersonalRanksAsync([pRealmKing, pDuelTie, pValuationTie, pAlchemyKing, pSectOnlyRank51]);
        Assert.Equal(4, batch.Count);
        Assert.Equal(bestDuel, batch[pDuelTie]);
        Assert.False(batch.ContainsKey(pSectOnlyRank51));

        // 列表查询按 (Rank, 荣誉优先序) 稳定排序
        var duelAll = await runtime.GetPersonalTopRanksAsync(pDuelTie);
        Assert.Equal(
            [LeaderboardBoardIds.Duel, LeaderboardBoardIds.Valuation, LeaderboardBoardIds.SpiritStone, LeaderboardBoardIds.Technique("alchemy")],
            duelAll.Where(b => b.Rank == 2).Select(b => b.BoardId));
    }

    /// <summary>HTTP 端点边界：未登录 401、未物化 503 + Retry-After:5、未知榜 404、已物化 200/ETag-304 与跨玩家 ETag 防串号。</summary>
    [Fact]
    public async Task Endpoints_EnforceAuthWarmup404ETag304AndPerViewerIsolation()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var unusedSource = NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused");
        var sections = new PostgresSectionRepository(unusedSource, NullLogger<PostgresSectionRepository>.Instance);
        var players = new StubPlayerRuntime();
        var runtime = new LeaderboardRuntime(
            unusedSource, sections, players, market: null, catalog, NullLogger<LeaderboardRuntime>.Instance);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(runtime);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        LeaderboardEndpoints.Map(app);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

            // 1. 匿名访问必须拒绝 401。
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/leaderboards/")).StatusCode);

            // 2. 已认证但在预热完成前必须返回 503 且附带 Retry-After: 5。
            As(client, PlayerA);
            var warmupIdx = await client.GetAsync("/api/v1/leaderboards/");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, warmupIdx.StatusCode);
            Assert.Equal("5", warmupIdx.Headers.RetryAfter?.Delta is { } d ? ((int)d.TotalSeconds).ToString() : warmupIdx.Headers.GetValues("Retry-After").FirstOrDefault());

            var warmupBoard = await client.GetAsync("/api/v1/leaderboards/valuation");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, warmupBoard.StatusCode);

            // 3. 未知榜单固定返回 404（即便尚未预热也不应伪装为可存在榜单）。
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/leaderboards/no-such-board")).StatusCode);

            // 4. 注入首版物化快照。
            var computedAt = new DateTimeOffset(2026, 11, 14, 15, 0, 12, TimeSpan.Zero);
            var assembly = LeaderboardAssembler.Assemble(
                computedAt,
                [
                    new LeaderboardPlayerInput(PlayerA, 101, "青尘", "player", 25, 420, EquipmentValuation: 8000, SpiritStones: 500),
                    new LeaderboardPlayerInput(PlayerB, 102, "赤霄", "player", 20, 100, EquipmentValuation: 3000, SpiritStones: 900),
                ],
                [],
                catalog.RealmProgression);
            runtime.SeedSnapshotForTesting(assembly);

            // 5. 总览目录返回 200、ETag、Vary: Cookie 与 Cache-Control: private, max-age=60；带上相同 ETag（含 W/ 弱标签）命中 304。
            var idxOk = await client.GetAsync("/api/v1/leaderboards/");
            Assert.Equal(HttpStatusCode.OK, idxOk.StatusCode);
            var idxETag = idxOk.Headers.ETag!.ToString();
            Assert.Contains("Cookie", idxOk.Headers.Vary);
            Assert.True(idxOk.Headers.CacheControl?.Private);
            var indexDto = await idxOk.Content.ReadFromJsonAsync<LeaderboardIndexDto>(ProtocolJson.Options);
            Assert.Equal(12, indexDto!.Boards.Count);

            using (var notModReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/leaderboards/"))
            {
                notModReq.Headers.TryAddWithoutValidation("If-None-Match", idxETag);
                var idx304 = await client.SendAsync(notModReq);
                Assert.Equal(HttpStatusCode.NotModified, idx304.StatusCode);
            }

            // 6. 单榜按认证玩家附带 myRank，且不同玩家持有互异 ETag，防止同浏览器切号串号命中 304。
            var boardAResp = await client.GetAsync("/api/v1/leaderboards/valuation");
            Assert.Equal(HttpStatusCode.OK, boardAResp.StatusCode);
            var etagA = boardAResp.Headers.ETag!.ToString();
            var boardADto = await boardAResp.Content.ReadFromJsonAsync<LeaderboardSnapshotDto>(ProtocolJson.Options);
            Assert.Equal(1, boardADto!.MyRank?.Rank);
            Assert.Equal(8000d, boardADto.MyRank?.Score);

            using (var reqA304 = new HttpRequestMessage(HttpMethod.Get, "/api/v1/leaderboards/valuation"))
            {
                reqA304.Headers.TryAddWithoutValidation("If-None-Match", etagA);
                Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(reqA304)).StatusCode);
            }

            // 切号为未入榜玩家 UnrankedPlayer 并故意携带 PlayerA 的 ETag：必须返回 200 且 myRank 为 null、ETag 不同。
            As(client, UnrankedPlayer);
            using (var reqCross = new HttpRequestMessage(HttpMethod.Get, "/api/v1/leaderboards/valuation"))
            {
                reqCross.Headers.TryAddWithoutValidation("If-None-Match", etagA);
                var crossResp = await client.SendAsync(reqCross);
                Assert.Equal(HttpStatusCode.OK, crossResp.StatusCode);
                Assert.NotEqual(etagA, crossResp.Headers.ETag!.ToString());
                var crossDto = await crossResp.Content.ReadFromJsonAsync<LeaderboardSnapshotDto>(ProtocolJson.Options);
                Assert.Null(crossDto!.MyRank);
            }
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>真实 PostgreSQL 集成：投影列读取、RebuildAsync 写入双表（含 BINARY COPY）以及新实例启机快速恢复。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RebuildAndRestore_PostgresEndToEndRoundTrip()
    {
        var source = PostgresTestSchema.GetSharedDataSource(PostgresTestSchema.RequireConnectionString("全服排行榜集成测试"));
        await PostgresTestSchema.EnsureAsync(source);

        var catalog = GameDataCatalog.LoadEmbedded();
        var sections = new PostgresSectionRepository(source, NullLogger<PostgresSectionRepository>.Instance);
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var sectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var conn = await source.OpenConnectionAsync())
        {
            await using var batch = new NpgsqlBatch(conn)
            {
                BatchCommands =
                {
                    new NpgsqlBatchCommand("""
                        INSERT INTO identity.accounts(account_id, login_normalized, password_hash, password_salt, password_iterations, created_at, updated_at)
                        VALUES ($1, $2, '\x00'::bytea, '\x00'::bytea, 100000, $5, $5),
                               ($3, $4, '\x00'::bytea, '\x00'::bytea, 100000, $5, $5);
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = p1 },
                            new NpgsqlParameter { Value = p1.ToString("N") },
                            new NpgsqlParameter { Value = p2 },
                            new NpgsqlParameter { Value = p2.ToString("N") },
                            new NpgsqlParameter { Value = now },
                        }
                    },
                    new NpgsqlBatchCommand("""
                        INSERT INTO identity.players(player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at, status)
                        VALUES ($1, $1, '天榜甲', $2, $5, $5, 1),
                               ($3, $3, '天榜乙', $4, $5, $5, 1);
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = p1 },
                            new NpgsqlParameter { Value = p1.ToString("N") },
                            new NpgsqlParameter { Value = p2 },
                            new NpgsqlParameter { Value = p2.ToString("N") },
                            new NpgsqlParameter { Value = now },
                        }
                    },
                }
            };
            await batch.ExecuteNonQueryAsync();
        }

        // p1 写入双段（高修为、高胜场、炼丹经验、灵石）；p2 加入宗门并持有更高灵石。
        var agg1 = PlayerAggregate.Create(new(p1), new(p1), "天榜甲", now, catalog.Fingerprint);
        agg1.Progression.RealmLevel = 320;
        agg1.Progression.Experience = 9876;
        agg1.Progression.DuelWins = 999999;
        agg1.Progression.DuelLosses = 2;
        agg1.Settings.Avatar = PlayerSettingsState.DefaultMaleAvatar;
        agg1.Loadout.TechniqueExperiences["alchemy"] = 5000;

        var agg2 = PlayerAggregate.Create(new(p2), new(p2), "天榜乙", now, catalog.Fingerprint);
        agg2.Progression.RealmLevel = 20;

        await sections.EnqueueSectionsAsync(
            SectionCodec.EncodeAggregateFull(agg1.ExportState(), now)
                .Concat(SectionCodec.EncodeAggregateFull(agg2.ExportState(), now))
                .ToArray());
        await sections.FlushPendingAsync(256, TimeSpan.FromSeconds(30));

        await using (var conn = await source.OpenConnectionAsync())
        {
            var sectName = $"测试榜宗{sectId:N}"[..16];
            await using var batch = new NpgsqlBatch(conn)
            {
                BatchCommands =
                {
                    new NpgsqlBatchCommand("""
                        INSERT INTO economy.player_asset_balances(player_id, asset_id, variant, quantity, version, updated_at)
                        VALUES ($1, 'currency.spirit-stone', '', 999999999, 1, $2);
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = p2 },
                            new NpgsqlParameter { Value = now },
                        }
                    },
                    new NpgsqlBatchCommand("""
                        INSERT INTO social.zongmen(zongmen_id, name, name_normalized, prefix, leader_id, anchor_node_id, created_at)
                        VALUES ($1, $2, $2, '榜', $3, 'node.yunxi-village', $4);
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = sectId },
                            new NpgsqlParameter { Value = sectName },
                            new NpgsqlParameter { Value = p1 },
                            new NpgsqlParameter { Value = now },
                        }
                    },
                    new NpgsqlBatchCommand("""
                        INSERT INTO game.node_buildings(node_id, building_id, level, target_level, started_at_unix_seconds, contributed_materials, updated_at)
                        VALUES ($1, $2, 4, 0, 0, '{}'::jsonb, $3);
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = ZongmenRules.ResidenceNodeId(sectId) },
                            new NpgsqlParameter { Value = ZongmenRules.HallBuildingId },
                            new NpgsqlParameter { Value = now },
                        }
                    },
                    new NpgsqlBatchCommand("""
                        INSERT INTO social.zongmen_members(player_id, zongmen_id, role, joined_at)
                        VALUES ($1, $2, 0, $3);
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = p1 },
                            new NpgsqlParameter { Value = sectId },
                            new NpgsqlParameter { Value = now },
                        }
                    },
                }
            };
            await batch.ExecuteNonQueryAsync();
        }

        // 先验证投影列提取静态辅助方法。
        var loaded1 = await sections.LoadSectionsAsync([p1], PlayerSectionKinds.Progression);
        var (_, exp1, _, wins1, losses1) = PostgresSectionRepository.ReadProgressionProjection(loaded1[p1]);
        Assert.Equal(9876d, exp1);
        Assert.Equal(999999d, wins1);
        Assert.Equal(2d, losses1);

        var stubRuntime = new StubPlayerRuntime();
        var runtime1 = new LeaderboardRuntime(
            source, sections, stubRuntime, market: null, catalog, NullLogger<LeaderboardRuntime>.Instance);
        await runtime1.RebuildAsync();
        Assert.True(runtime1.HasSnapshot);

        // 校验 p1 的斗法榜与宗门榜名次、以及 p2 的灵石榜名次均已写入内存并与新启实例 DB 恢复一致。
        var duel1 = runtime1.TryGetBoard(LeaderboardBoardIds.Duel, p1, default);
        Assert.NotNull(duel1);
        var duelDto1 = JsonSerializer.Deserialize<LeaderboardSnapshotDto>(duel1.Value.JsonUtf8.Span, ProtocolJson.Options)!;
        Assert.NotNull(duelDto1.MyRank);
        Assert.Equal(999999d, duelDto1.MyRank!.Score);
        Assert.Equal(2d, duelDto1.MyRank.DuelLosses);

        // 使用全新 LeaderboardRuntime 仅走 DB 快速恢复，不触发冷重建也必须完整还原。
        var runtime2 = new LeaderboardRuntime(
            source, sections, stubRuntime, market: null, catalog, NullLogger<LeaderboardRuntime>.Instance);
        var restored = await runtime2.TryRestoreRecentFromDatabaseAsync();
        Assert.True(restored);
        Assert.True(runtime2.HasSnapshot);

        var stone2 = runtime2.TryGetBoard(LeaderboardBoardIds.SpiritStone, p2, default);
        Assert.NotNull(stone2);
        var stoneDto2 = JsonSerializer.Deserialize<LeaderboardSnapshotDto>(stone2.Value.JsonUtf8.Span, ProtocolJson.Options)!;
        Assert.Equal(999999999d, stoneDto2.MyRank?.Score);

        var sect1 = runtime2.TryGetBoard(LeaderboardBoardIds.Zongmen, p1, default);
        Assert.NotNull(sect1);
        var sectDto1 = JsonSerializer.Deserialize<LeaderboardSnapshotDto>(sect1.Value.JsonUtf8.Span, ProtocolJson.Options)!;
        Assert.NotNull(sectDto1.MyRank);
        Assert.Equal(1d, sectDto1.MyRank!.Score);
    }

    /// <summary>真实 PostgreSQL 集成：is_bot = true 的账号必须从全部个人榜与宗门成员统计（含全机器人宗门）中剔除。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RebuildAsync_ExcludesIsBotAccountsAndBotZongmen()
    {
        var source = PostgresTestSchema.GetSharedDataSource(PostgresTestSchema.RequireConnectionString("全服排行榜机器人剔除测试"));
        await PostgresTestSchema.EnsureAsync(source);

        var catalog = GameDataCatalog.LoadEmbedded();
        var sections = new PostgresSectionRepository(source, NullLogger<PostgresSectionRepository>.Instance);
        var humanId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        var sectBotId = Guid.NewGuid();
        var humanSectId = Guid.NewGuid();
        var botSectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var conn = await source.OpenConnectionAsync())
        {
            await using var batch = new NpgsqlBatch(conn)
            {
                BatchCommands =
                {
                    new NpgsqlBatchCommand("""
                        INSERT INTO identity.accounts(account_id, login_normalized, password_hash, password_salt, password_iterations, created_at, updated_at, is_bot)
                        VALUES ($1, $2, '\x00'::bytea, '\x00'::bytea, 100000, $7, $7, false),
                               ($3, $4, '\x00'::bytea, '\x00'::bytea, 100000, $7, $7, true),
                               ($5, $6, '\x00'::bytea, '\x00'::bytea, 100000, $7, $7, true);
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = humanId },
                            new NpgsqlParameter { Value = $"human_{humanId:N}" },
                            new NpgsqlParameter { Value = botId },
                            new NpgsqlParameter { Value = $"bot_{botId:N}" },
                            new NpgsqlParameter { Value = sectBotId },
                            new NpgsqlParameter { Value = $"bot_{sectBotId:N}" },
                            new NpgsqlParameter { Value = now },
                        }
                    },
                    new NpgsqlBatchCommand("""
                        INSERT INTO identity.players(player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at, status)
                        VALUES ($1, $1, '真人修士', $2, $7, $7, 1),
                               ($3, $3, '坊市机器人甲', $4, $7, $7, 1),
                               ($5, $5, '坊市机器人乙', $6, $7, $7, 1);
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = humanId },
                            new NpgsqlParameter { Value = $"HUMAN_{humanId:N}" },
                            new NpgsqlParameter { Value = botId },
                            new NpgsqlParameter { Value = $"BOTA_{botId:N}" },
                            new NpgsqlParameter { Value = sectBotId },
                            new NpgsqlParameter { Value = $"BOTB_{sectBotId:N}" },
                            new NpgsqlParameter { Value = now },
                        }
                    },
                }
            };
            await batch.ExecuteNonQueryAsync();
        }

        var humanAgg = PlayerAggregate.Create(new(humanId), new(humanId), "真人修士", now, catalog.Fingerprint);
        humanAgg.Progression.RealmLevel = 15;
        humanAgg.Progression.DuelWins = 3;

        var botAgg = PlayerAggregate.Create(new(botId), new(botId), "坊市机器人", now, catalog.Fingerprint);
        botAgg.Progression.RealmLevel = 999;
        botAgg.Progression.Experience = 999999;
        botAgg.Progression.DuelWins = 88888;
        botAgg.Loadout.TechniqueExperiences["alchemy"] = 50000;

        await sections.EnqueueSectionsAsync(
            SectionCodec.EncodeAggregateFull(humanAgg.ExportState(), now)
                .Concat(SectionCodec.EncodeAggregateFull(botAgg.ExportState(), now))
                .ToArray());
        await sections.FlushPendingAsync(256, TimeSpan.FromSeconds(30));

        await using (var conn = await source.OpenConnectionAsync())
        {
            var humanSectName = $"真宗{humanSectId:N}"[..16];
            var botSectName = $"机宗{botSectId:N}"[..16];
            await using var batch = new NpgsqlBatch(conn)
            {
                BatchCommands =
                {
                    new NpgsqlBatchCommand("""
                        INSERT INTO economy.player_asset_balances(player_id, asset_id, variant, quantity, version, updated_at)
                        VALUES ($1, 'currency.spirit-stone', '', 100, 1, $3),
                               ($2, 'currency.spirit-stone', '', 999999999, 1, $3);
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = humanId },
                            new NpgsqlParameter { Value = botId },
                            new NpgsqlParameter { Value = now },
                        }
                    },
                    new NpgsqlBatchCommand("""
                        INSERT INTO social.zongmen(zongmen_id, name, name_normalized, prefix, leader_id, anchor_node_id, created_at)
                        VALUES ($1, $2, $2, '真', $3, 'node.yunxi-village', $6),
                               ($4, $5, $5, '机', $7, 'node.yunxi-village', $6);
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = humanSectId },
                            new NpgsqlParameter { Value = humanSectName },
                            new NpgsqlParameter { Value = humanId },
                            new NpgsqlParameter { Value = botSectId },
                            new NpgsqlParameter { Value = botSectName },
                            new NpgsqlParameter { Value = now },
                            new NpgsqlParameter { Value = botId },
                        }
                    },
                    new NpgsqlBatchCommand("""
                        INSERT INTO social.zongmen_members(player_id, zongmen_id, role, joined_at)
                        VALUES ($1, $2, 0, $6),
                               ($3, $2, 1, $6),
                               ($4, $5, 0, $6);
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = humanId },
                            new NpgsqlParameter { Value = humanSectId },
                            new NpgsqlParameter { Value = sectBotId },
                            new NpgsqlParameter { Value = botId },
                            new NpgsqlParameter { Value = botSectId },
                            new NpgsqlParameter { Value = now },
                        }
                    },
                }
            };
            await batch.ExecuteNonQueryAsync();
        }

        // 数据库当前有效非机器人账号数（同 Collection 前序测试可能已留存若干真人账号，精确对账即可，不假定净库）。
        long expectedHumanCount;
        await using (var conn = await source.OpenConnectionAsync())
        {
            await using var countCmd = new NpgsqlCommand("""
                SELECT count(*)
                FROM identity.players i
                JOIN identity.accounts a ON a.account_id = i.account_id
                WHERE i.status = 1 AND NOT a.is_bot
                """, conn);
            expectedHumanCount = (long)(await countCmd.ExecuteScalarAsync())!;
        }

        var runtime = new LeaderboardRuntime(
            source, sections, new StubPlayerRuntime(), market: null, catalog, NullLogger<LeaderboardRuntime>.Instance);
        await runtime.RebuildAsync();

        var botIdText = botId.ToString("D");
        var sectBotIdText = sectBotId.ToString("D");

        // 1. 境界 / 灵石 / 斗法 / 技艺榜：botId 虽持有最高数值，但被完全排除；ParticipantCount 严格等于非机器人数量。
        var realmDto = JsonSerializer.Deserialize<LeaderboardSnapshotDto>(
            runtime.TryGetBoard(LeaderboardBoardIds.Realm, humanId, default)!.Value.JsonUtf8.Span, ProtocolJson.Options)!;
        Assert.Equal((int)expectedHumanCount, realmDto.ParticipantCount);
        Assert.NotNull(realmDto.MyRank);
        Assert.DoesNotContain(realmDto.Entries, e => e.PlayerId == botIdText || e.PlayerId == sectBotIdText);

        var stoneForBot = JsonSerializer.Deserialize<LeaderboardSnapshotDto>(
            runtime.TryGetBoard(LeaderboardBoardIds.SpiritStone, botId, default)!.Value.JsonUtf8.Span, ProtocolJson.Options)!;
        Assert.DoesNotContain(stoneForBot.Entries, e => e.PlayerId == botIdText || e.PlayerId == sectBotIdText);
        Assert.Null(stoneForBot.MyRank);

        var duelForBot = JsonSerializer.Deserialize<LeaderboardSnapshotDto>(
            runtime.TryGetBoard(LeaderboardBoardIds.Duel, botId, default)!.Value.JsonUtf8.Span, ProtocolJson.Options)!;
        Assert.DoesNotContain(duelForBot.Entries, e => e.PlayerId == botIdText || e.PlayerId == sectBotIdText);
        Assert.Null(duelForBot.MyRank);

        var alchemyBoardId = LeaderboardBoardIds.Technique("alchemy");
        var alchemyForBot = JsonSerializer.Deserialize<LeaderboardSnapshotDto>(
            runtime.TryGetBoard(alchemyBoardId, botId, default)!.Value.JsonUtf8.Span, ProtocolJson.Options)!;
        Assert.DoesNotContain(alchemyForBot.Entries, e => e.PlayerId == botIdText || e.PlayerId == sectBotIdText);
        Assert.Null(alchemyForBot.MyRank);

        // 2. 宗门榜：混合宗门不计入机器人成员（MemberCount == 1），全机器人宗门不入榜且机器人查不到本宗名次。
        var sectForHuman = JsonSerializer.Deserialize<LeaderboardSnapshotDto>(
            runtime.TryGetBoard(LeaderboardBoardIds.Zongmen, humanId, default)!.Value.JsonUtf8.Span, ProtocolJson.Options)!;
        var humanSectEntry = Assert.Single(sectForHuman.Entries, e => e.ZongmenId == humanSectId.ToString("D"));
        Assert.Equal(1, humanSectEntry.MemberCount);
        Assert.DoesNotContain(sectForHuman.Entries, e => e.ZongmenId == botSectId.ToString("D"));

        var sectForBot = JsonSerializer.Deserialize<LeaderboardSnapshotDto>(
            runtime.TryGetBoard(LeaderboardBoardIds.Zongmen, botId, default)!.Value.JsonUtf8.Span, ProtocolJson.Options)!;
        Assert.Null(sectForBot.MyRank);
        var sectForMixedBot = JsonSerializer.Deserialize<LeaderboardSnapshotDto>(
            runtime.TryGetBoard(LeaderboardBoardIds.Zongmen, sectBotId, default)!.Value.JsonUtf8.Span, ProtocolJson.Options)!;
        Assert.Null(sectForMixedBot.MyRank);

        // 3. 数据库名次表 game.leaderboard_ranks 中绝不落入任何机器人名次行。
        await using (var conn = await source.OpenConnectionAsync())
        {
            await using var rankCmd = new NpgsqlCommand(
                "SELECT count(*) FROM game.leaderboard_ranks WHERE player_id = ANY($1)", conn);
            rankCmd.Parameters.Add(new NpgsqlParameter { Value = new[] { botId, sectBotId } });
            Assert.Equal(0L, (long)(await rankCmd.ExecuteScalarAsync())!);
        }
    }

    private static void As(HttpClient client, Guid playerId)
    {
        client.DefaultRequestHeaders.Remove("x-test-player");
        client.DefaultRequestHeaders.Add("x-test-player", playerId.ToString("D"));
    }

    private sealed class StubPlayerRuntime : IPlayerRuntime
    {
        public int LoadedCount => 0;
        public DateTimeOffset? NextAdvanceBoundaryUtc => null;
#pragma warning disable CS0067
        public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated;
        public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted;
#pragma warning restore CS0067
        public ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<PlayerRuntimeSnapshot> AttachConnectionAsync(Guid playerId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public PlayerRuntimeSnapshot? TryGetLoaded(Guid playerId) => null;
        public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command,
            JsonElement payload, long expectedStateVersion, Guid requestId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask FlushAsync(Guid playerId, bool force, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask FlushAllAsync(bool force, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(Guid playerId, bool flush, DateTimeOffset? offlineSince, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<PlayerRuntimeSnapshot> MutateOwnedAsync(Guid playerId,
            Action<PlayerAggregate> mutate, CancellationToken ct = default) => throw new NotSupportedException();
        public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId) => [];
        public ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken ct = default)
            => new([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.TryGetValue("x-test-player", out var raw)
                && Guid.TryParse(raw, out var playerId))
            {
                var identity = new ClaimsIdentity(
                    [new Claim("player_id", playerId.ToString("D"))], Scheme.Name);
                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
            }
            return Task.FromResult(AuthenticateResult.NoResult());
        }
    }
}
