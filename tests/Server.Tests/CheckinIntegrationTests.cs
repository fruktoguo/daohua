using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Domain.Economy;
using Domain.Player;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Protocol;
using Server.Clean;
using Server.Clean.Checkin;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 每日签到系统 PostgreSQL 集成测试：
/// 验证首签入账绑定功德、跨日连续签到递增、断签重置、并发抢领幂等防重、
/// 不写入长期流水账本（遵守 5 类白名单）、Bot 隔离及 HTTP 端点鉴权与限流。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class CheckinIntegrationTests
{
    [Fact]
    public async Task Claim_UpdatesStreakTotalAssetsAtomically_AndRespectsLedgerWhitelist()
    {
        await using var fixture = await CheckinFixture.CreateAsync();
        var playerId = await fixture.CreatePlayerAsync("签到修士", isBot: false);

        var day1 = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.FromHours(8));
        var initialStatus = await fixture.Repository.GetStatusAsync(playerId, day1);
        Assert.True(initialStatus.CanClaimToday);
        Assert.Equal(0, initialStatus.StreakDays);
        Assert.Equal(0, initialStatus.TotalDays);
        Assert.Null(initialStatus.TodayRewardMerit);
        Assert.Null(initialStatus.TodayFortuneTier);
        Assert.Equal(0, initialStatus.WeekDays);
        Assert.Equal(0, initialStatus.MonthDays);
        Assert.Equal(DailyCheckinRules.RewardItemId, initialStatus.RewardItemId);

        // 第 1 天签到（2026-11-10 周二）：入账 item.gongde-bound，不污染 economy.transactions。
        fixture.Clock.SetNow(day1);
        var claim1 = await fixture.Runtime.ClaimAsync(playerId);
        Assert.True(claim1.Claimed);
        Assert.InRange(claim1.RewardMerit, DailyCheckinRules.MinMerit, DailyCheckinRules.MaxMerit);
        Assert.False(claim1.Status.CanClaimToday);
        Assert.Equal(1, claim1.Status.StreakDays);
        Assert.Equal(1, claim1.Status.TotalDays);
        Assert.Equal(claim1.RewardMerit, claim1.Status.TodayRewardMerit);
        Assert.NotNull(claim1.Status.TodayFortuneTier);
        Assert.Equal(1, claim1.Status.WeekDays);
        Assert.Equal(claim1.RewardMerit, claim1.Status.WeekMerit);
        Assert.NotNull(claim1.Status.WeekFortuneTier);
        Assert.Equal(1, claim1.Status.MonthDays);
        Assert.Equal(claim1.RewardMerit, claim1.Status.MonthMerit);
        Assert.Equal(claim1.RewardMerit, await fixture.GetAssetBalanceAsync(playerId, DailyCheckinRules.RewardItemId));
        Assert.Single(fixture.WalletReloadedPlayers);
        Assert.Contains(playerId, fixture.ParticipationEnteredPlayers);
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM economy.transactions WHERE transaction_type = 'checkin.claim'"));

        // 同一自然日内重复领取：幂等返回 Claimed=false，余额不增加。
        var duplicate = await fixture.Runtime.ClaimAsync(playerId);
        Assert.False(duplicate.Claimed);
        Assert.Equal(0, duplicate.RewardMerit);
        Assert.Equal(claim1.RewardMerit, await fixture.GetAssetBalanceAsync(playerId, DailyCheckinRules.RewardItemId));

        // 次日跨零点后签到（2026-11-11 周三）：连签天数递增为 2，周/月累计同步推进。
        var day2 = new DateTimeOffset(2026, 11, 11, 0, 0, 5, TimeSpan.FromHours(8));
        fixture.Clock.SetNow(day2);
        var summaryDay2 = await fixture.Runtime.GetSummaryAsync(playerId);
        Assert.True(summaryDay2.CanClaimToday);
        Assert.Equal(1, summaryDay2.StreakDays);

        var claim2 = await fixture.Runtime.ClaimAsync(playerId);
        Assert.True(claim2.Claimed);
        Assert.Equal(2, claim2.Status.StreakDays);
        Assert.Equal(2, claim2.Status.TotalDays);
        Assert.Equal(2, claim2.Status.WeekDays);
        Assert.Equal(claim1.RewardMerit + claim2.RewardMerit, claim2.Status.WeekMerit);
        Assert.Equal(2, claim2.Status.MonthDays);
        Assert.Equal(claim1.RewardMerit + claim2.RewardMerit, claim2.Status.MonthMerit);
        Assert.Equal(claim1.RewardMerit + claim2.RewardMerit, await fixture.GetAssetBalanceAsync(playerId, DailyCheckinRules.RewardItemId));

        // 跨周（2026-11-16 下周一，同一月内）：本周归零，本月保留累计 2 天；因漏签连签重置为 1。
        var nextWeekSameMonth = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.FromHours(8));
        fixture.Clock.SetNow(nextWeekSameMonth);
        var beforeNextWeek = await fixture.Runtime.GetStatusAsync(playerId);
        Assert.True(beforeNextWeek.CanClaimToday);
        Assert.Equal(0, beforeNextWeek.StreakDays);
        Assert.Equal(0, beforeNextWeek.WeekDays);
        Assert.Equal(0, beforeNextWeek.WeekMerit);
        Assert.Null(beforeNextWeek.WeekFortuneTier);
        Assert.Equal(2, beforeNextWeek.MonthDays);

        var claimWeek2 = await fixture.Runtime.ClaimAsync(playerId);
        Assert.True(claimWeek2.Claimed);
        Assert.Equal(1, claimWeek2.Status.StreakDays);
        Assert.Equal(3, claimWeek2.Status.TotalDays);
        Assert.Equal(1, claimWeek2.Status.WeekDays);
        Assert.Equal(claimWeek2.RewardMerit, claimWeek2.Status.WeekMerit);
        Assert.Equal(3, claimWeek2.Status.MonthDays);
        Assert.Equal(claim1.RewardMerit + claim2.RewardMerit + claimWeek2.RewardMerit, claimWeek2.Status.MonthMerit);
    }

    [Fact]
    public async Task Claim_ConcurrentRequestsOnSameDay_GrantExactlyOnce()
    {
        await using var fixture = await CheckinFixture.CreateAsync();
        var playerId = await fixture.CreatePlayerAsync("并发修士", isBot: false);
        var now = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.FromHours(8));
        fixture.Clock.SetNow(now);

        var tasks = Enumerable.Range(0, 6)
            .Select(_ => fixture.Runtime.ClaimAsync(playerId).AsTask())
            .ToArray();
        var results = await Task.WhenAll(tasks);

        var claimedResults = results.Where(r => r.Claimed).ToArray();
        Assert.Single(claimedResults);
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM game.player_daily_checkins WHERE player_id = $1", playerId));
        Assert.Equal(claimedResults[0].RewardMerit, await fixture.GetAssetBalanceAsync(playerId, DailyCheckinRules.RewardItemId));
    }

    [Fact]
    public async Task Endpoints_EnforceAuthBotIsolation_AndRoundTripStatusClaim()
    {
        await using var fixture = await CheckinFixture.CreateAsync();
        var humanId = await fixture.CreatePlayerAsync("端到端修士", isBot: false);
        var botId = await fixture.CreatePlayerAsync("机器人傀儡", isBot: true);
        fixture.Clock.SetNow(new DateTimeOffset(2026, 11, 16, 20, 0, 0, TimeSpan.FromHours(8)));

        await using var host = await CheckinHost.StartAsync(fixture);
        using var client = new HttpClient { BaseAddress = host.BaseAddress };

        var unauth = await client.GetAsync("/api/v1/checkin");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        using var botReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkin/claim");
        botReq.Headers.Add("x-test-player", botId.ToString("D"));
        var botResp = await client.SendAsync(botReq);
        Assert.Equal(HttpStatusCode.Forbidden, botResp.StatusCode);

        using var getReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/checkin");
        getReq.Headers.Add("x-test-player", humanId.ToString("D"));
        var statusResp = await client.SendAsync(getReq);
        statusResp.EnsureSuccessStatusCode();
        var status = await statusResp.Content.ReadFromJsonAsync<CheckinStatusDto>(ProtocolJson.Options);
        Assert.NotNull(status);
        Assert.True(status!.CanClaimToday);

        using var claimReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkin/claim");
        claimReq.Headers.Add("x-test-player", humanId.ToString("D"));
        var claimResp = await client.SendAsync(claimReq);
        claimResp.EnsureSuccessStatusCode();
        var claimed = await claimResp.Content.ReadFromJsonAsync<CheckinClaimResponseDto>(ProtocolJson.Options);
        Assert.NotNull(claimed);
        Assert.True(claimed!.Claimed);
        Assert.InRange(claimed.RewardMerit, DailyCheckinRules.MinMerit, DailyCheckinRules.MaxMerit);
        Assert.False(claimed.Status.CanClaimToday);
    }

    private sealed class MutableClock : IPlayerSimulationClock
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public DateTimeOffset UtcNow => _now;
        public bool IsDeterministic => true;
        public void AdvanceTo(DateTimeOffset target) => _now = target;
        public void SetNow(DateTimeOffset value) => _now = value;
    }

    private sealed class CheckinFixture : IAsyncDisposable, IParticipationAssetBoundary, IPlayerRuntime
    {
        public NpgsqlDataSource Source { get; }
        public MutableClock Clock { get; } = new();
        public PostgresCheckinRepository Repository { get; }
        public CheckinRuntime Runtime { get; }
        public List<Guid> WalletReloadedPlayers { get; } = [];
        public List<Guid> ParticipationEnteredPlayers { get; } = [];

        private CheckinFixture(NpgsqlDataSource source)
        {
            Source = source;
            Repository = new PostgresCheckinRepository(source, participationAssets: this);
            Runtime = new CheckinRuntime(Repository, playerRuntime: this, clock: Clock);
        }

        public static async Task<CheckinFixture> CreateAsync()
        {
            var source = PostgresTestSchema.GetSharedDataSource(PostgresTestSchema.RequireConnectionString("每日签到集成测试"));
            await PostgresTestSchema.EnsureAsync(source);
            var fixture = new CheckinFixture(source);
            await fixture.Sql("TRUNCATE game.player_daily_checkins, economy.transient_idempotency, economy.player_asset_balances RESTART IDENTITY CASCADE");
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
                            new NpgsqlParameter { Value = $"checkin_{suffix}" },
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
                            new NpgsqlParameter { Value = displayName },
                            new NpgsqlParameter { Value = PostgresAccountRepository.NormalizeNameForLookup(displayName) },
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

        public async Task<double> GetAssetBalanceAsync(Guid playerId, string assetId)
        {
            await using var cmd = Source.CreateCommand(
                "SELECT COALESCE(SUM(quantity), 0) FROM economy.player_asset_balances WHERE player_id = $1 AND asset_id = $2");
            cmd.Parameters.AddWithValue(playerId);
            cmd.Parameters.AddWithValue(assetId);
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
            foreach (var key in keys) ParticipationEnteredPlayers.Add(key.PlayerId);
            return new ValueTask<IDisposable>(NoopDisposable.Instance);
        }

        public ValueTask ReloadWalletAndNotifyAsync(Guid playerId, CancellationToken ct = default)
        {
            WalletReloadedPlayers.Add(playerId);
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
        public ValueTask<PlayerRuntimeSnapshot> MutateOwnedAsync(Guid playerId, Action<PlayerAggregate> mutate, CancellationToken ct = default) => throw new NotSupportedException();
        public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId) => [];
        public ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken ct = default) => new([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class CheckinHost(WebApplication app, Uri baseAddress) : IAsyncDisposable
    {
        public Uri BaseAddress { get; } = baseAddress;

        public static async Task<CheckinHost> StartAsync(CheckinFixture fixture)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(fixture.Repository);
            builder.Services.AddSingleton(fixture.Runtime);
            builder.Services.AddAuthentication("TestAuth")
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestAuth", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddRateLimiter(o =>
                o.AddFixedWindowLimiter("command", opt => { opt.PermitLimit = 60; opt.Window = TimeSpan.FromMinutes(1); }));

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            CheckinEndpoints.Map(app);
            await app.StartAsync();
            var url = app.Urls.First();
            return new CheckinHost(app, new Uri(url));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
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
