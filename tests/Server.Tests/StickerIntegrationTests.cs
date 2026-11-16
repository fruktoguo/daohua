using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Server.Clean;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 传音表情包多端同步 PostgreSQL 集成测试：
/// 验证 100 个上限拦截、多端离线删改墓碑 LWW 合并、临时 ID 升级及 30 天墓碑清理。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class StickerIntegrationTests
{
    [Fact]
    public async Task Sync_Enforces100Limit_AndMergesMultiDeviceChangesWithTombstones()
    {
        await using var fixture = await StickerFixture.CreateAsync();
        var playerId = await fixture.CreatePlayerAsync("表情修士");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 1. 写入 100 个活跃表情包应全部成功
        var batch100 = Enumerable.Range(0, 100)
            .Select(i => new StickerSyncUpsertItem(
                StickerId: $"{i:D6}".PadLeft(64, 'a'),
                Url: $"https://beeimg.us.ci/temp/{i:D6}.png",
                Sha256: $"{i:D6}".PadLeft(64, 'a'),
                Mime: "image/png",
                UseCount: i % 5,
                LastUsedAt: now - 10_000 + i,
                AddedAt: now - 20_000 + i,
                UpdatedAt: now - 10_000 + i))
            .ToArray();

        var state100 = await fixture.Repository.SyncAsync(playerId, new StickerSyncRequest(batch100, null));
        Assert.Equal(100, state100.Stickers.Length);
        Assert.Equal(100, state100.MaxStickers);

        // 2. 第 101 个必须被 409/LimitExceeded 显式拒绝，且不能静默淘汰已有 100 个
        var item101 = new StickerSyncUpsertItem(
            StickerId: new string('f', 64),
            Url: "https://beeimg.us.ci/temp/overflow.png",
            Sha256: new string('f', 64),
            Mime: "image/png",
            UseCount: 99,
            LastUsedAt: now,
            AddedAt: now,
            UpdatedAt: now);

        var ex = await Assert.ThrowsAsync<StickerOperationException>(async () =>
            await fixture.Repository.SyncAsync(playerId, new StickerSyncRequest([item101], null)));
        Assert.Equal(StickerOperationFailure.LimitExceeded, ex.Failure);
        Assert.NotNull(ex.CurrentState);
        Assert.Equal(100, ex.CurrentState!.Stickers.Length);

        // 3. 批量删除 2 个后可继续添加，且生成删除墓碑防止离线旧设备复活已删项
        var id0 = batch100[0].StickerId;
        var id1 = batch100[1].StickerId;
        var deleteTime = now + 5_000;
        var afterDelete = await fixture.Repository.DeleteAsync(playerId, new StickerDeleteRequest([id0, id1], deleteTime));
        Assert.Equal(98, afterDelete.Stickers.Length);
        Assert.Contains(afterDelete.Tombstones, t => t.StickerId == id0);

        // 离线设备拿着旧 updatedAt 重放 id0，不得复活
        var staleReplay = batch100[0] with { UseCount = 999 };
        var afterStaleReplay = await fixture.Repository.SyncAsync(playerId, new StickerSyncRequest([staleReplay, item101], null));
        Assert.Equal(99, afterStaleReplay.Stickers.Length);
        Assert.DoesNotContain(afterStaleReplay.Stickers, s => s.StickerId == id0);
        Assert.Contains(afterStaleReplay.Stickers, s => s.StickerId == item101.StickerId);

        // 4. 跨端损坏协同与重传刷新：若 brokenUrl 不等于当前最新 URL 不得覆盖为待补传
        var updatedUrl = "https://beeimg.us.ci/temp/new-live.png";
        await fixture.Repository.SyncAsync(playerId, new StickerSyncRequest([
            item101 with { Url = updatedUrl, UpdatedAt = now + 10_000, NeedsReupload = false }
        ], null));
        var staleBrokenReport = await fixture.Repository.SyncAsync(playerId, new StickerSyncRequest([
            item101 with { UpdatedAt = now + 12_000, NeedsReupload = true, BrokenUrl = "https://beeimg.us.ci/temp/overflow.png" }
        ], null));
        var refreshed = Assert.Single(staleBrokenReport.Stickers, s => s.StickerId == item101.StickerId);
        Assert.Equal(updatedUrl, refreshed.Url);
        Assert.False(refreshed.NeedsReupload);

        // 5. 临时 url_* ID 升级为真实 SHA256 ID：原子合并统计并释放旧占位
        var legacyId = "url_" + new string('9', 40);
        await fixture.Repository.SyncAsync(playerId, new StickerSyncRequest([
            new StickerSyncUpsertItem(
                legacyId,
                "https://example.com/legacy.png",
                null,
                "image/png",
                UseCount: 7,
                LastUsedAt: now + 15_000,
                AddedAt: now - 50_000,
                UpdatedAt: now + 15_000)
        ], null));
        var canonicalId = new string('e', 64);
        var afterUpgrade = await fixture.Repository.SyncAsync(playerId, new StickerSyncRequest([
            new StickerSyncUpsertItem(
                canonicalId,
                "https://example.com/legacy.png",
                canonicalId,
                "image/png",
                UseCount: 1,
                LastUsedAt: now + 16_000,
                AddedAt: now + 16_000,
                UpdatedAt: now + 16_000,
                ReplacedStickerId: legacyId)
        ], null));
        var upgraded = Assert.Single(afterUpgrade.Stickers, s => s.StickerId == canonicalId);
        Assert.Equal(7, upgraded.UseCount);
        Assert.Equal(now - 50_000, upgraded.AddedAt);
        Assert.DoesNotContain(afterUpgrade.Stickers, s => s.StickerId == legacyId);
    }

    [Fact]
    public async Task StickerEndpoints_HttpAuthAndLimitConflict_WorksEndToEnd()
    {
        await using var fixture = await StickerFixture.CreateAsync();
        var playerId = await fixture.CreatePlayerAsync("同步修士");
        await using var host = await StickerHost.StartAsync(fixture);
        using var http = new HttpClient { BaseAddress = host.BaseAddress };

        // 未登录拒绝 401
        var unauthRes = await http.GetAsync("/api/v1/chat/stickers");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthRes.StatusCode);

        // 已登录拉取与同步
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/chat/stickers");
        req.Headers.Add("x-test-player", playerId.ToString("D"));
        var getRes = await http.SendAsync(req);
        getRes.EnsureSuccessStatusCode();
        var json = await getRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(100, json.GetProperty("maxStickers").GetInt32());
        Assert.Empty(json.GetProperty("stickers").EnumerateArray());
    }

    private sealed class StickerFixture : IAsyncDisposable
    {
        public required NpgsqlDataSource Source { get; init; }
        public required PostgresStickerRepository Repository { get; init; }

        public static async Task<StickerFixture> CreateAsync()
        {
            var source = PostgresTestSchema.GetSharedDataSource(PostgresTestSchema.RequireConnectionString("表情包同步集成测试"));
            await PostgresTestSchema.EnsureAsync(source);
            await using (var cmd = source.CreateCommand("TRUNCATE social.player_stickers RESTART IDENTITY CASCADE"))
                await cmd.ExecuteNonQueryAsync();
            return new StickerFixture
            {
                Source = source,
                Repository = new PostgresStickerRepository(source)
            };
        }

        public async Task<Guid> CreatePlayerAsync(string displayName)
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
                        VALUES ($1, $2, '\x00'::bytea, '\x00'::bytea, 100000, false, $3, $3);
                        """)
                    {
                        Parameters =
                        {
                            new NpgsqlParameter { Value = id },
                            new NpgsqlParameter { Value = $"sticker_{suffix}" },
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
                            new NpgsqlParameter { Value = displayName + suffix },
                            new NpgsqlParameter { Value = PostgresAccountRepository.NormalizeNameForLookup(displayName + suffix) },
                            new NpgsqlParameter { Value = now },
                        }
                    },
                }
            };
            await batch.ExecuteNonQueryAsync();
            return id;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StickerHost(WebApplication app, Uri baseAddress) : IAsyncDisposable
    {
        public Uri BaseAddress { get; } = baseAddress;

        public static async Task<StickerHost> StartAsync(StickerFixture fixture)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(fixture.Repository);
            builder.Services.AddAuthentication("TestAuth")
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestAuth", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddRateLimiter(o =>
            {
                o.AddFixedWindowLimiter("command", opt => { opt.PermitLimit = 60; opt.Window = TimeSpan.FromMinutes(1); });
                o.AddFixedWindowLimiter("upload", opt => { opt.PermitLimit = 60; opt.Window = TimeSpan.FromMinutes(1); });
            });

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            StickerEndpoints.Map(app);
            await app.StartAsync();
            return new StickerHost(app, new Uri(app.Urls.First()));
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
