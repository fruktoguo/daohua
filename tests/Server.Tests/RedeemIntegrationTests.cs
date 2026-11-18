using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Protocol;
using Server.Clean;
using Server.Clean.Ops;
using Server.Clean.Redeem;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 兑换码系统 PostgreSQL 集成测试（总纲 §29）：
/// 覆盖通用口令码、批次单次码、强化装备变体入账、并发防双花、同批次每人限领 1 次、
/// 白名单流水 ops.redeem、机器人隔离、停用/过期控制及玩家与 Ops HTTP 端点契约。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class RedeemIntegrationTests
{
    [Fact]
    public async Task UniversalCode_GrantsRewardsAtomically_EnforcesPerPlayerAndCap_AndWritesOpsLedger()
    {
        await using var fixture = await RedeemFixture.CreateAsync();
        var player1 = await fixture.CreatePlayerAsync("兑礼甲", isBot: false);
        var player2 = await fixture.CreatePlayerAsync("兑礼乙", isBot: false);
        var player3 = await fixture.CreatePlayerAsync("兑礼丙", isBot: false);
        var equipmentId = fixture.GameData.RawEquipments.Keys.First();

        var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.FromHours(8));
        fixture.Clock.SetNow(now);

        var created = await fixture.Runtime.CreateUniversalAsync(new OpsCreateUniversalRedeemRequest(
            Code: "  daohua666 ",
            Title: "开宗庆典礼",
            Rewards:
            [
                new RedeemRewardDto(DailyCheckinRules.RewardItemId, 200),
                new RedeemRewardDto(EnhancementRules.SpiritStoneItemId, 1500),
                new RedeemRewardDto(equipmentId, 1, EnhancementLevel: 5)
            ],
            MaxClaims: 2,
            ExpireDays: 7,
            NeverExpire: false));

        Assert.Equal("DAOHUA666", created.Batch.UniversalCode);

        // 相同口令重复建码必须报 CodeConflict。
        var conflict = await Assert.ThrowsAsync<RedeemOperationException>(() =>
            fixture.Runtime.CreateUniversalAsync(new OpsCreateUniversalRedeemRequest(
                "DAOHUA666", "冲突礼", [new RedeemRewardDto(DailyCheckinRules.RewardItemId, 10)], null, 7)).AsTask());
        Assert.Equal(RedeemOperationFailure.CodeConflict, conflict.Failure);

        // 玩家 1 小写带空格输入：成功入账货币、绑定功德与 +5 装备变体，热刷新在线钱包并落 ops.redeem 流水。
        var res1 = await fixture.Runtime.RedeemAsync(player1, "  DaoHua666 ");
        Assert.Equal(created.Batch.BatchId, res1.BatchId);
        Assert.Equal("开宗庆典礼", res1.Title);
        Assert.Equal(3, res1.Rewards.Count);
        Assert.Equal(200d, await fixture.GetAssetBalanceAsync(player1, DailyCheckinRules.RewardItemId, ""));
        Assert.Equal(1500d, await fixture.GetAssetBalanceAsync(player1, EnhancementRules.SpiritStoneItemId, ""));
        Assert.Equal(1d, await fixture.GetAssetBalanceAsync(player1, equipmentId, "+5"));
        Assert.Contains(player1, fixture.WalletReloadedPlayers);
        Assert.Contains((player1, equipmentId, "+5"), fixture.ParticipationEnteredAssets);
        Assert.Equal(1L, await fixture.CountAsync(
            "SELECT count(*) FROM economy.transactions WHERE actor_id = $1 AND transaction_type = 'ops.redeem'",
            player1));

        // 玩家 1 再次兑换同一通用口令：被永久防重表拦截。
        var dup = await Assert.ThrowsAsync<RedeemOperationException>(() =>
            fixture.Runtime.RedeemAsync(player1, "DAOHUA666").AsTask());
        Assert.Equal(RedeemOperationFailure.AlreadyClaimedBatch, dup.Failure);

        // 玩家 2 兑换占满第 2 个名额。
        await fixture.Runtime.RedeemAsync(player2, "DAOHUA666");
        Assert.Equal(200d, await fixture.GetAssetBalanceAsync(player2, DailyCheckinRules.RewardItemId, ""));

        // 玩家 3 在满额（maxClaims = 2）后兑换：返回 Exhausted。
        var exhausted = await Assert.ThrowsAsync<RedeemOperationException>(() =>
            fixture.Runtime.RedeemAsync(player3, "DAOHUA666").AsTask());
        Assert.Equal(RedeemOperationFailure.Exhausted, exhausted.Failure);
    }

    [Fact]
    public async Task SingleUseBatch_PreventsConcurrentDoubleSpend_AndLimitsOneCodePerPlayerInSameBatch()
    {
        await using var fixture = await RedeemFixture.CreateAsync();
        var contenders = new Guid[8];
        for (var i = 0; i < contenders.Length; i++)
            contenders[i] = await fixture.CreatePlayerAsync($"抢码修士{i + 1}", isBot: false);

        fixture.Clock.SetNow(new DateTimeOffset(2026, 11, 18, 14, 0, 0, TimeSpan.Zero));
        var created = await fixture.Runtime.CreateSingleUseAsync(new OpsCreateSingleUseRedeemRequest(
            Count: 2,
            Prefix: "TAP-",
            Title: "TapTap 独家礼包",
            Rewards: [new RedeemRewardDto(DailyCheckinRules.RewardItemId, 88)],
            ExpireDays: null,
            NeverExpire: true));

        Assert.Equal(2, created.Codes.Count);
        var codeA = created.Codes[0];
        var codeB = created.Codes[1];

        // 8 名不同玩家并发抢兑同一枚单次码 codeA：严格仅 1 人成功。
        var tasks = contenders.Select(async pid =>
        {
            try
            {
                await fixture.Runtime.RedeemAsync(pid, codeA);
                return (PlayerId: pid, Succeeded: true, Failure: (RedeemOperationFailure?)null);
            }
            catch (RedeemOperationException ex)
            {
                return (PlayerId: pid, Succeeded: false, Failure: ex.Failure);
            }
        }).ToArray();

        var outcomes = await Task.WhenAll(tasks);
        var winners = outcomes.Where(o => o.Succeeded).ToArray();
        Assert.Single(winners);
        var winnerId = winners[0].PlayerId;
        Assert.Equal(88d, await fixture.GetAssetBalanceAsync(winnerId, DailyCheckinRules.RewardItemId, ""));
        Assert.All(outcomes.Where(o => !o.Succeeded), o => Assert.Equal(RedeemOperationFailure.CodeAlreadyUsed, o.Failure));

        // 同一中奖玩家尝试继续兑换同批次未使用的第二枚码 codeB：被「同批次每人限兑 1 次」拦截。
        var sameBatchDup = await Assert.ThrowsAsync<RedeemOperationException>(() =>
            fixture.Runtime.RedeemAsync(winnerId, codeB).AsTask());
        Assert.Equal(RedeemOperationFailure.AlreadyClaimedBatch, sameBatchDup.Failure);

        // 另一名未中奖玩家可正常兑换第二枚码 codeB。
        var runnerUp = contenders.First(id => id != winnerId);
        await fixture.Runtime.RedeemAsync(runnerUp, codeB);
        Assert.Equal(88d, await fixture.GetAssetBalanceAsync(runnerUp, DailyCheckinRules.RewardItemId, ""));
    }

    [Fact]
    public async Task Endpoints_EnforceAuthRateLimitBotDisableExpiry_AndOpsManagement()
    {
        await using var fixture = await RedeemFixture.CreateAsync();
        var human = await fixture.CreatePlayerAsync("散修道友", isBot: false);
        var bot = await fixture.CreatePlayerAsync("坊市傀儡", isBot: true);

        var baseTime = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        fixture.Clock.SetNow(baseTime);

        await using var host = await RedeemTestHost.StartAsync(fixture);
        using var client = new HttpClient { BaseAddress = host.BaseAddress };

        // 1. 未登录 401
        var unauth = await client.PostAsJsonAsync("/api/v1/redeem", new RedeemCodeRequestDto("TESTCODE"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        // 2. 通过 Ops API 创建通用口令码
        using var opsReq = new HttpRequestMessage(HttpMethod.Post, "/ops/api/actions/redeem-create-universal")
        {
            Content = JsonContent.Create(new OpsCreateUniversalRedeemRequest(
                "GIFT2026", "限时福利", [new RedeemRewardDto(DailyCheckinRules.RewardItemId, 50)], null, ExpireDays: 1))
        };
        opsReq.Headers.Add(OpsPanel.TokenHeader, RedeemTestHost.OpsSecret);
        var opsCreateRes = await client.SendAsync(opsReq);
        Assert.Equal(HttpStatusCode.OK, opsCreateRes.StatusCode);
        var createdBatch = (await opsCreateRes.Content.ReadFromJsonAsync<OpsCreateRedeemBatchResponse>(ProtocolJson.Options))!;

        // 3. 机器人账号返回 403
        using var botReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/redeem")
        {
            Content = JsonContent.Create(new RedeemCodeRequestDto("GIFT2026"))
        };
        botReq.Headers.Add("x-test-player", bot.ToString("D"));
        var botRes = await client.SendAsync(botReq);
        Assert.Equal(HttpStatusCode.Forbidden, botRes.StatusCode);

        // 4. 停用批次后人类玩家兑换返回 409，恢复并推进时间过期后仍返回 409，回调时间后兑换成功
        await fixture.Runtime.SetBatchDisabledAsync(createdBatch.Batch.BatchId, disabled: true);
        using (var disabledReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/redeem")
        {
            Content = JsonContent.Create(new RedeemCodeRequestDto("GIFT2026"))
        })
        {
            disabledReq.Headers.Add("x-test-player", human.ToString("D"));
            var disabledRes = await client.SendAsync(disabledReq);
            Assert.Equal(HttpStatusCode.Conflict, disabledRes.StatusCode);
        }

        await fixture.Runtime.SetBatchDisabledAsync(createdBatch.Batch.BatchId, disabled: false);
        fixture.Clock.SetNow(baseTime.AddDays(2));
        using (var expiredReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/redeem")
        {
            Content = JsonContent.Create(new RedeemCodeRequestDto("GIFT2026"))
        })
        {
            expiredReq.Headers.Add("x-test-player", human.ToString("D"));
            var expiredRes = await client.SendAsync(expiredReq);
            Assert.Equal(HttpStatusCode.Conflict, expiredRes.StatusCode);
        }

        fixture.Clock.SetNow(baseTime.AddHours(2));
        using (var okReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/redeem")
        {
            Content = JsonContent.Create(new RedeemCodeRequestDto("gift2026"))
        })
        {
            okReq.Headers.Add("x-test-player", human.ToString("D"));
            var okRes = await client.SendAsync(okReq);
            Assert.Equal(HttpStatusCode.OK, okRes.StatusCode);
        }

        // 5. 触发 "redeem" 独立限流（测试宿主配置 PermitLimit = 4，前已消耗 3 次，第 5 次必返回 429）
        for (var i = 0; i < 2; i++)
        {
            using var extraReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/redeem")
            {
                Content = JsonContent.Create(new RedeemCodeRequestDto("UNKNOWN99"))
            };
            extraReq.Headers.Add("x-test-player", human.ToString("D"));
            var extraRes = await client.SendAsync(extraReq);
            if (i == 1)
                Assert.Equal(HttpStatusCode.TooManyRequests, extraRes.StatusCode);
        }
    }

    private sealed class MutableClock : IPlayerSimulationClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;
        public bool IsDeterministic => true;
        public void AdvanceTo(DateTimeOffset target) => UtcNow = target.ToUniversalTime();
        public void SetNow(DateTimeOffset value) => UtcNow = value.ToUniversalTime();
    }

    private sealed class RedeemFixture : IAsyncDisposable, IPlayerRuntime, IParticipationAssetBoundary
    {
        public NpgsqlDataSource Source { get; }
        public GameDataCatalog GameData { get; } = GameDataCatalog.LoadEmbedded();
        public MutableClock Clock { get; } = new();
        public PostgresRedeemRepository Repository { get; }
        public RedeemRuntime Runtime { get; }
        public List<Guid> WalletReloadedPlayers { get; } = [];
        public List<(Guid PlayerId, string AssetId, string Variant)> ParticipationEnteredAssets { get; } = [];

        private RedeemFixture(NpgsqlDataSource source)
        {
            Source = source;
            Repository = new PostgresRedeemRepository(source, participationAssets: this);
            Runtime = new RedeemRuntime(Repository, GameData, playerRuntime: this, clock: Clock);
        }

        public static async Task<RedeemFixture> CreateAsync()
        {
            var source = PostgresTestSchema.GetSharedDataSource(PostgresTestSchema.RequireConnectionString("兑换码集成测试"));
            await PostgresTestSchema.EnsureAsync(source);
            var fixture = new RedeemFixture(source);
            await fixture.Sql("""
                TRUNCATE economy.player_redeem_claims,
                         economy.redeem_codes,
                         economy.redeem_batches,
                         economy.player_asset_balances,
                         economy.transactions
                RESTART IDENTITY CASCADE
                """);
            return fixture;
        }

        public async Task<Guid> CreatePlayerAsync(string displayName, bool isBot)
        {
            var id = Guid.NewGuid();
            var suffix = id.ToString("N")[..8];
            var now = DateTimeOffset.UtcNow;
            await using var conn = await Source.OpenConnectionAsync();
            await using var batch = new NpgsqlBatch(conn)
            {
                BatchCommands =
                {
                    new NpgsqlBatchCommand("""
                        INSERT INTO identity.accounts(account_id, login_normalized, password_hash, password_salt, password_iterations, is_bot, created_at, updated_at)
                        VALUES ($1, $2, '\x00'::bytea, '\x00'::bytea, 100000, $3, $4, $4);
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = id },
                            new NpgsqlParameter { Value = $"redeem_{suffix}" },
                            new NpgsqlParameter { Value = isBot },
                            new NpgsqlParameter { Value = now },
                        }
                    },
                    new NpgsqlBatchCommand("""
                        INSERT INTO identity.players(player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at, status)
                        VALUES ($1, $1, $2, $3, $4, $4, 1);
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = id },
                            new NpgsqlParameter { Value = $"{displayName}_{suffix}" },
                            new NpgsqlParameter { Value = PostgresAccountRepository.NormalizeNameForLookup($"{displayName}_{suffix}") },
                            new NpgsqlParameter { Value = now },
                        }
                    },
                }
            };
            await batch.ExecuteNonQueryAsync();
            return id;
        }

        public async Task<long> CountAsync(string sql, params object[] values)
        {
            await using var cmd = Source.CreateCommand(sql);
            foreach (var v in values) cmd.Parameters.AddWithValue(v);
            return (long)(await cmd.ExecuteScalarAsync())!;
        }

        public async Task<double> GetAssetBalanceAsync(Guid playerId, string assetId, string variant)
        {
            await using var cmd = Source.CreateCommand(
                "SELECT COALESCE(SUM(quantity), 0) FROM economy.player_asset_balances WHERE player_id = $1 AND asset_id = $2 AND variant = $3");
            cmd.Parameters.AddWithValue(playerId);
            cmd.Parameters.AddWithValue(assetId);
            cmd.Parameters.AddWithValue(variant);
            return Convert.ToDouble(await cmd.ExecuteScalarAsync());
        }

        public async Task Sql(string sql, params object[] values)
        {
            await using var cmd = Source.CreateCommand(sql);
            foreach (var v in values) cmd.Parameters.AddWithValue(v);
            await cmd.ExecuteNonQueryAsync();
        }

        public ValueTask<IDisposable> AcquireAsync(IReadOnlyList<ParticipationAssetKey> keys, CancellationToken cancellationToken = default)
        {
            lock (ParticipationEnteredAssets)
            {
                foreach (var key in keys)
                    ParticipationEnteredAssets.Add((key.PlayerId, key.AssetId, key.Variant));
            }
            return new ValueTask<IDisposable>(NoopDisposable.Instance);
        }

        public ValueTask ReloadWalletAndNotifyAsync(Guid playerId, CancellationToken ct = default)
        {
            lock (WalletReloadedPlayers)
            {
                WalletReloadedPlayers.Add(playerId);
            }
            return ValueTask.CompletedTask;
        }

        public int LoadedCount => 0;
        public DateTimeOffset? NextAdvanceBoundaryUtc => null;
#pragma warning disable CS0067
        public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated;
        public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted;
#pragma warning restore CS0067
        public ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<PlayerRuntimeSnapshot> AttachConnectionAsync(Guid playerId, CancellationToken ct = default) => throw new NotSupportedException();
        public PlayerRuntimeSnapshot? TryGetLoaded(Guid playerId) => null;
        public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, long expectedStateVersion, Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask FlushAsync(Guid playerId, bool force, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask FlushAllAsync(bool force, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(Guid playerId, bool flush, DateTimeOffset? offlineSince, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<PlayerRuntimeSnapshot> MutateOwnedAsync(Guid playerId, Action<Domain.Player.PlayerAggregate> mutate, CancellationToken ct = default) => throw new NotSupportedException();
        public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId) => [];
        public ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken ct = default) => new([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class RedeemTestHost(WebApplication app, Uri baseAddress) : IAsyncDisposable
    {
        public const string OpsSecret = "test-ops-secret-token";
        public Uri BaseAddress { get; } = baseAddress;

        public static async Task<RedeemTestHost> StartAsync(RedeemFixture fixture)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(fixture.Repository);
            builder.Services.AddSingleton(fixture.Runtime);
            builder.Services.AddSingleton(new OpsOptions(true, "/ops", 500, OpsSecret));
            builder.Services.AddAuthentication("TestAuth")
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestAuth", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddRateLimiter(o =>
            {
                o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                o.AddPolicy("redeem", ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    ctx.User.FindFirstValue("player_id") ?? "anon",
                    _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 4,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
            });

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            RedeemEndpoints.Map(app);

            var opsGroup = app.MapGroup("/ops");
            opsGroup.AddEndpointFilter(async (ctx, next) =>
            {
                var opts = ctx.HttpContext.RequestServices.GetRequiredService<OpsOptions>();
                if (OpsPanel.RejectUnauthorized(ctx.HttpContext, opts) is { } rejected)
                    return rejected;
                return await next(ctx);
            });
            opsGroup.MapOpsRedeemActions();

            await app.StartAsync();
            return new RedeemTestHost(app, new Uri(app.Urls.First()));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class TestAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
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
