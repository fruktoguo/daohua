using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Domain.GameData;
using Domain.Player;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Protocol;
using Server.Clean;
using Server.Clean.Ops;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 双轨邮件系统集成回归测试：
/// 验证零人行群发存储、历史存量/新号可见性、Bot 隔离、Serializable 原子领奖与幂等性、
/// 未领附件删除保护、过期状态位图压缩、个人 100 封上限裁剪及 REST/Ops 鉴权端点。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class MailIntegrationTests
{
    private const string OpsToken = "test-mail-ops-token-123456";

    /// <summary>
    /// 1. 群发公告全服仅写 1 行，未读未领玩家在 player_mail_states 为 0 行；
    /// 2. 列表省略正文 body，点击详情按需加载并标已读且不产生未领取冲突；
    /// 3. onlyExistingPlayers 严格区分存量老号与公告后注册新号；
    /// 4. Bot 账号在摘要、列表和领奖上彻底隔离。
    /// </summary>
    [Fact]
    public async Task DualTrackBroadcast_ZeroIdleStateRows_ExistingScope_AndBotIsolation()
    {
        await using var fixture = await MailFixture.CreateAsync();
        var oldHuman = await fixture.CreatePlayerAsync("老修士", createdAtOffset: TimeSpan.FromHours(-2), isBot: false);
        var idleHuman = await fixture.CreatePlayerAsync("挂机修士", createdAtOffset: TimeSpan.FromHours(-3), isBot: false);
        var botPlayer = await fixture.CreatePlayerAsync("试炼傀儡", createdAtOffset: TimeSpan.FromHours(-3), isBot: true);

        // 1. 发两条全服群发：一条仅存量老玩家（带灵石奖励），一条面向包含未来新号的所有人。
        var existingOnly = await fixture.Runtime.SendBroadcastMailAsync(new OpsSendBroadcastMailRequest(
            Title: "老修士补偿",
            Body: "仅停服前已创角修士可见，含 {{item:currency.spirit-stone|灵石}}。",
            SenderName: "仙门执事",
            Attachments: [new MailAttachmentDto("currency.spirit-stone", 300)],
            OnlyExistingPlayers: true,
            ExpireDays: 7));

        // 随后创一个晚于第一条广播、早于第二条广播的新号。
        await Task.Delay(30);
        var newcomer = await fixture.CreatePlayerAsync("新飞升修士", createdAtOffset: TimeSpan.Zero, isBot: false);

        var openAll = await fixture.Runtime.SendBroadcastMailAsync(new OpsSendBroadcastMailRequest(
            Title: "全员欢迎函",
            Body: "新老修士皆可见。",
            SenderName: "道化司",
            Attachments: [],
            OnlyExistingPlayers: false,
            ExpireDays: 7));

        // 物理存储验证：broadcast_mails 仅 2 行，player_mail_states 此时必须为 0 行（零按人写放大）。
        Assert.Equal(2L, await fixture.CountAsync("SELECT count(*) FROM social.broadcast_mails"));
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM social.player_mail_states"));

        // 老玩家与挂机不打开邮件的玩家均可算得红点摘要；Bot 恒为 0。
        var oldSummary = await fixture.Runtime.GetSummaryAsync(oldHuman);
        Assert.Equal(2, oldSummary.UnreadCount);
        Assert.Equal(1, oldSummary.ClaimableCount);

        var idleSummary = await fixture.Runtime.GetSummaryAsync(idleHuman);
        Assert.Equal(2, idleSummary.UnreadCount);
        Assert.Equal(1, idleSummary.ClaimableCount);
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM social.player_mail_states"));

        var botSummary = await fixture.Runtime.GetSummaryAsync(botPlayer);
        Assert.Equal(0, botSummary.UnreadCount);
        Assert.Equal(0, botSummary.ClaimableCount);
        Assert.Empty((await fixture.Runtime.ListMailsAsync(botPlayer)).Mails);

        // 新号只能看到 openAll，看不到 existingOnly。
        var newcomerList = await fixture.Runtime.ListMailsAsync(newcomer);
        var newcomerMail = Assert.Single(newcomerList.Mails);
        Assert.Equal(openAll.MailKey, newcomerMail.MailKey);

        // 老玩家在列表中看到 2 封，点击 details 后懒建 1 行 state 并标记已读；未交互玩家依然是 0 行。
        var oldDetail = await fixture.Runtime.GetDetailAndMarkReadAsync(oldHuman, existingOnly.MailKey);
        Assert.Contains("currency.spirit-stone", oldDetail.Mail.Body);
        Assert.True(oldDetail.Mail.IsRead);
        Assert.Equal(1, oldDetail.Summary.UnreadCount);
        Assert.Equal(1, oldDetail.Summary.ClaimableCount);
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM social.player_mail_states WHERE player_id = $1", oldHuman));
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM social.player_mail_states WHERE player_id = $1", idleHuman));

        // Bot 强行领奖必须被拒绝。
        var ex = await Assert.ThrowsAsync<MailOperationException>(async () =>
            await fixture.Runtime.ClaimSingleAsync(botPlayer, existingOnly.MailKey));
        Assert.Equal(MailOperationFailure.BotRestricted, ex.Failure);
        Assert.Equal(0d, await fixture.GetAssetBalanceAsync(botPlayer, "currency.spirit-stone"));
    }

    /// <summary>
    /// 领奖原子性、幂等性（重复领取不重复发奖）、Wallet 热同步回调、
    /// 未领附件禁止删除（409）、批量删除已读跳过未领，以及过期广播状态位图紧缩回收。
    /// </summary>
    [Fact]
    public async Task ClaimIdempotency_WalletSync_DeleteProtection_AndStateCompaction()
    {
        await using var fixture = await MailFixture.CreateAsync();
        var playerId = await fixture.CreatePlayerAsync("领奖测试", createdAtOffset: TimeSpan.FromHours(-1), isBot: false);

        var broadcast = await fixture.Runtime.SendBroadcastMailAsync(new OpsSendBroadcastMailRequest(
            Title: "全服灵石",
            Body: "全服补贴",
            SenderName: "道化司",
            Attachments: [new MailAttachmentDto("currency.spirit-stone", 250)],
            OnlyExistingPlayers: true,
            ExpireDays: 7));

        var personal = await fixture.Runtime.SendPlayerMailAsync(new OpsSendPlayerMailRequest(
            TargetPlayerId: playerId.ToString("D"),
            TargetDisplayName: null,
            Title: "私信丹药",
            Body: "凝气草补偿",
            SenderName: "宗门执事",
            Attachments: [new MailAttachmentDto("item.ningxue-grass", 5)],
            ExpireDays: 7));
        var personalKey = MailKeyCodec.FormatPersonal(personal.MailId);

        var personalNoAttach = await fixture.Runtime.SendPlayerMailAsync(new OpsSendPlayerMailRequest(
            TargetPlayerId: null,
            TargetDisplayName: "领奖测试",
            Title: "纯文本通知",
            Body: "无附件纯文",
            SenderName: "道化司",
            Attachments: null,
            ExpireDays: 7));
        var noAttachKey = MailKeyCodec.FormatPersonal(personalNoAttach.MailId);

        // 先读 personalNoAttach 与 personal（后者保留未领附件），再执行删除拦截与删除已读验证。
        await fixture.Runtime.MarkReadAsync(playerId, noAttachKey);
        await fixture.Runtime.MarkReadAsync(playerId, personalKey);

        // 未领附件单封删除必须抛出 HasUnclaimedAttachments。
        var blocked = await Assert.ThrowsAsync<MailOperationException>(async () =>
            await fixture.Runtime.DeleteSingleAsync(playerId, personalKey));
        Assert.Equal(MailOperationFailure.HasUnclaimedAttachments, blocked.Failure);

        // 批量删除已读：应当删掉已读无附件的 noAttachKey，保留已读但未领附件的 personalKey。
        var delRead = await fixture.Runtime.DeleteAllReadAsync(playerId);
        Assert.Equal([noAttachKey], delRead.DeletedMailKeys);
        Assert.Equal(1, delRead.SkippedUnclaimedCount);

        // 单封领取广播附件并二次重复领（幂等）：余额精确 250，且触发 1 次 Wallet 热同步。
        var claim1 = await fixture.Runtime.ClaimSingleAsync(playerId, broadcast.MailKey);
        Assert.Equal([broadcast.MailKey], claim1.ClaimedMailKeys);
        Assert.Equal(250d, await fixture.GetAssetBalanceAsync(playerId, "currency.spirit-stone"));
        Assert.Contains(playerId, fixture.WalletReloadedPlayers);
        Assert.Contains(playerId, fixture.ParticipationEnteredPlayers);

        var claimReplay = await fixture.Runtime.ClaimSingleAsync(playerId, broadcast.MailKey);
        Assert.Empty(claimReplay.ClaimedMailKeys);
        Assert.Equal(250d, await fixture.GetAssetBalanceAsync(playerId, "currency.spirit-stone"));

        // 一键领取剩余的 personalKey：精确发 5 株凝气草，并写入 economy.transactions(transaction_type='mail.claim')。
        var claimAll = await fixture.Runtime.ClaimAllAsync(playerId);
        Assert.Equal([personalKey], claimAll.ClaimedMailKeys);
        Assert.Equal(5d, await fixture.GetAssetBalanceAsync(playerId, "item.ningxue-grass"));
        Assert.True(await fixture.CountAsync(
            "SELECT count(*) FROM economy.transactions WHERE actor_id = $1 AND transaction_type = 'mail.claim'", playerId) >= 2);

        // 领取完毕后，允许删除已读且已领的 personalKey。
        var delClaimed = await fixture.Runtime.DeleteSingleAsync(playerId, personalKey);
        Assert.Equal([personalKey], delClaimed.DeletedMailKeys);

        // 使广播在数据库中过期并失效缓存：player_mail_states 中的旧广播 ID 应在下一次状态写入时被紧缩清除。
        await fixture.Sql("UPDATE social.broadcast_mails SET expires_at = now() - interval '1 second' WHERE broadcast_id = $1", broadcast.BroadcastId);
        await fixture.Runtime.ForceRefreshBroadcastCacheAsync();

        var b2 = await fixture.Runtime.SendBroadcastMailAsync(new OpsSendBroadcastMailRequest(
            Title: "触发紧缩",
            Body: "触发 state 数组按活跃 ID 交集压实",
            SenderName: null,
            Attachments: null,
            OnlyExistingPlayers: true,
            ExpireDays: 7));
        await fixture.Runtime.MarkReadAsync(playerId, b2.MailKey);
        // MarkRead 会按当前活跃广播压实数组，已过期的 broadcast.BroadcastId 必须从 claimed_ids 移除。
        Assert.Equal(0L, await fixture.CountAsync("SELECT cardinality(claimed_ids)::bigint FROM social.player_mail_states WHERE player_id = $1", playerId));

        // 撤销 b2 后通过「删除已读」触发滞留状态压实，此时全部交集为空，稀疏状态行应被物理回收。
        Assert.True(await fixture.Runtime.RevokeBroadcastMailAsync(b2.BroadcastId));
        await fixture.Runtime.DeleteAllReadAsync(playerId);
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM social.player_mail_states WHERE player_id = $1", playerId));
    }

    /// <summary>
    /// 单玩家超过 100 封个人邮件时自动按「最老且无待领附件（已读优先）」顺序修剪，保留待领奖励及最新邮件。
    /// </summary>
    [Fact]
    public async Task PersonalMailbox_Enforces100MailCap_PreservingUnreadOrUnclaimedFirst()
    {
        await using var fixture = await MailFixture.CreateAsync();
        var playerId = await fixture.CreatePlayerAsync("邮箱满上限", createdAtOffset: TimeSpan.FromHours(-1), isBot: false);

        // 插入 100 封测试个人邮件：第 1 封未领附件且较旧（应被保留），第 2 封已读无附件（应被优先修剪）。
        var now = DateTimeOffset.UtcNow;
        Guid protectedAttachmentId = Guid.Empty;
        Guid prunableReadId = Guid.Empty;

        await using (var conn = await fixture.Source.OpenConnectionAsync())
        {
            for (var i = 0; i < MailRules.MaxPlayerMails; i++)
            {
                var mailId = Guid.NewGuid();
                var createdAt = now.AddMinutes(-200 + i);
                var isFirstWithAttachment = i == 0;
                var isSecondReadNoAttach = i == 1;
                if (isFirstWithAttachment) protectedAttachmentId = mailId;
                if (isSecondReadNoAttach) prunableReadId = mailId;

                var attachmentsJson = isFirstWithAttachment
                    ? """[{"itemId":"currency.spirit-stone","quantity":50}]"""
                    : "[]";
                var isRead = !isFirstWithAttachment;

                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO social.player_mails
                        (mail_id, player_id, title, body, sender_name, attachments, has_attachments, is_read, is_claimed, created_at, expires_at)
                    VALUES ($1, $2, $3, '正文', '道化司', $4::jsonb, $5, $6, false, $7, $8);
                    """, conn);
                cmd.Parameters.AddWithValue(mailId);
                cmd.Parameters.AddWithValue(playerId);
                cmd.Parameters.AddWithValue($"历史#{i}");
                cmd.Parameters.AddWithValue(attachmentsJson);
                cmd.Parameters.AddWithValue(isFirstWithAttachment);
                cmd.Parameters.AddWithValue(isRead);
                cmd.Parameters.AddWithValue(createdAt);
                cmd.Parameters.AddWithValue(now.AddDays(10));
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // 发入第 101 封，触发修剪回 100 封。
        await fixture.Runtime.SendPlayerMailAsync(new OpsSendPlayerMailRequest(
            TargetPlayerId: playerId.ToString("D"),
            TargetDisplayName: null,
            Title: "第101封新邮件",
            Body: "最新邮件",
            SenderName: "道化司",
            Attachments: null,
            ExpireDays: 7));

        Assert.Equal(100L, await fixture.CountAsync("SELECT count(*) FROM social.player_mails WHERE player_id = $1", playerId));
        // 最老已读无附件的第 2 封被优先裁剪；未领附件的第 1 封虽然更早但保留。
        Assert.Equal(0L, await fixture.CountAsync("SELECT count(*) FROM social.player_mails WHERE mail_id = $1", prunableReadId));
        Assert.Equal(1L, await fixture.CountAsync("SELECT count(*) FROM social.player_mails WHERE mail_id = $1", protectedAttachmentId));
    }

    /// <summary>
    /// 真实 HTTP 端点集成测试：玩家 /api/v1/mails/* 列表省略正文、详情标已读、409 未领删拦，
    /// 以及 /ops/api/actions/mail-* 未携带 X-Ops-Token 返回 401、带合法令牌成功发信与撤销公告。
    /// </summary>
    [Fact]
    public async Task RestAndOpsEndpoints_EnforceAuthListProjectionAndRevocation()
    {
        await using var fixture = await MailFixture.CreateAsync();
        var playerId = await fixture.CreatePlayerAsync("端点验证号", createdAtOffset: TimeSpan.FromHours(-1), isBot: false);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
            options.AddFixedWindowLimiter("command", opt => { opt.PermitLimit = 100; opt.Window = TimeSpan.FromMinutes(1); }));
        builder.Services.AddSingleton(fixture.Runtime);
        builder.Services.AddSingleton(new OpsOptions(true, "/ops", 500, OpsToken));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        MailEndpoints.Map(app);
        var opsGroup = app.MapGroup("/ops");
        opsGroup.AddEndpointFilter(async (ctx, next) =>
        {
            if (OpsPanel.RejectUnauthorized(ctx.HttpContext, new OpsOptions(true, "/ops", 500, OpsToken)) is { } rejected)
                return rejected;
            return await next(ctx);
        });
        opsGroup.MapOpsMailActions();
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

            // 1. Ops 未传令牌拒绝 401。
            var unauthorizedOps = await client.PostAsJsonAsync("/ops/api/actions/mail-send-broadcast", new
            {
                title = "未鉴权公告",
                body = "不应通过",
                onlyExistingPlayers = true,
                attachments = Array.Empty<object>(),
            });
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedOps.StatusCode);

            // 2. 带正确令牌发布带附件的全服公告。
            client.DefaultRequestHeaders.Add(OpsPanel.TokenHeader, OpsToken);
            var sendBroadcastResp = await client.PostAsJsonAsync("/ops/api/actions/mail-send-broadcast", new
            {
                title = "HTTP 测试群发",
                body = "请领取灵石",
                senderName = "司库长老",
                onlyExistingPlayers = true,
                expireDays = 7,
                attachments = new[] { new { itemId = "currency.spirit-stone", quantity = 88 } },
            });
            Assert.Equal(HttpStatusCode.OK, sendBroadcastResp.StatusCode);
            var sentJson = await sendBroadcastResp.Content.ReadFromJsonAsync<JsonElement>();
            var broadcastId = sentJson.GetProperty("broadcastId").GetInt64();
            var mailKey = MailKeyCodec.FormatBroadcast(broadcastId);

            // 3. 玩家端点：未登录 401；登录后 GET 列表不带 body。
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/mails/")).StatusCode);
            client.DefaultRequestHeaders.Add("x-test-player", playerId.ToString("D"));

            var listResp = await client.GetAsync("/api/v1/mails/");
            Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
            using (var doc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync()))
            {
                var row = doc.RootElement.GetProperty("mails")[0];
                Assert.Equal(mailKey, row.GetProperty("mailKey").GetString());
                Assert.False(row.TryGetProperty("body", out _));
            }

            // 4. 点开详情（同步标已读并下发 body），未领时 DELETE 必须返回 409 Conflict。
            var detailResp = await client.GetAsync($"/api/v1/mails/{Uri.EscapeDataString(mailKey)}");
            Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
            using (var doc = JsonDocument.Parse(await detailResp.Content.ReadAsStringAsync()))
            {
                Assert.Equal("请领取灵石", doc.RootElement.GetProperty("mail").GetProperty("body").GetString());
                Assert.True(doc.RootElement.GetProperty("mail").GetProperty("isRead").GetBoolean());
            }

            var conflictDelete = await client.DeleteAsync($"/api/v1/mails/{Uri.EscapeDataString(mailKey)}");
            Assert.Equal(HttpStatusCode.Conflict, conflictDelete.StatusCode);

            // 5. Ops 列表查询 & 撤销公告后玩家列表中立即消失。
            var listOpsResp = await client.GetAsync("/ops/api/mails/broadcasts");
            Assert.Equal(HttpStatusCode.OK, listOpsResp.StatusCode);
            var revokeResp = await client.PostAsJsonAsync("/ops/api/actions/mail-revoke-broadcast", new { broadcastId });
            Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

            var afterRevoke = await client.GetFromJsonAsync<MailListResponseDto>("/api/v1/mails/", ProtocolJson.Options);
            Assert.DoesNotContain(afterRevoke!.Mails, m => m.MailKey == mailKey);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private sealed class MailFixture : IAsyncDisposable, IParticipationAssetBoundary, IPlayerRuntime
    {
        public NpgsqlDataSource Source { get; }
        public GameDataCatalog Catalog { get; } = GameDataCatalog.LoadEmbedded();
        public MailRuntime Runtime { get; }
        public List<Guid> WalletReloadedPlayers { get; } = [];
        public List<Guid> ParticipationEnteredPlayers { get; } = [];

        private MailFixture(NpgsqlDataSource source)
        {
            Source = source;
            var repo = new PostgresMailRepository(source, participationAssets: this);
            Runtime = new MailRuntime(repo, Catalog, sessions: null, playerRuntime: this);
        }

        public static async Task<MailFixture> CreateAsync()
        {
            var source = PostgresTestSchema.GetSharedDataSource(PostgresTestSchema.RequireConnectionString("邮件系统集成测试"));
            await PostgresTestSchema.EnsureAsync(source);
            var fixture = new MailFixture(source);
            await fixture.Sql("TRUNCATE social.broadcast_mails, social.player_mail_states, social.player_mails RESTART IDENTITY");
            return fixture;
        }

        public async Task<Guid> CreatePlayerAsync(string displayName, TimeSpan createdAtOffset, bool isBot)
        {
            var id = Guid.NewGuid();
            var uniqueSuffix = id.ToString("N")[..8];
            var loginName = $"mail_{uniqueSuffix}";
            var createdAt = DateTimeOffset.UtcNow + createdAtOffset;
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
                            new NpgsqlParameter { Value = loginName },
                            new NpgsqlParameter { Value = isBot },
                            new NpgsqlParameter { Value = createdAt },
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
                            new NpgsqlParameter { Value = createdAt },
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

        public ValueTask<IDisposable> AcquireAsync(
            IReadOnlyList<ParticipationAssetKey> keys,
            CancellationToken cancellationToken = default)
        {
            foreach (var key in keys) ParticipationEnteredPlayers.Add(key.PlayerId);
            return new ValueTask<IDisposable>(NoopDisposable.Instance);
        }

        public ValueTask ReloadWalletAndNotifyAsync(Guid playerId, CancellationToken ct = default)
        {
            WalletReloadedPlayers.Add(playerId);
            return ValueTask.CompletedTask;
        }

        // 其余 IPlayerRuntime 成员仅按接口契约占位。
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
