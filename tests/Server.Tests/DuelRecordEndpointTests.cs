using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Protocol;
using Server;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 斗法紧凑记录服务端边界回归：
/// 1) WS 快照剥离 duelRecords 明细、仅保留 duelRecordCount；
/// 2) GET /api/v1/players/{id}/duel-records 本人授权、实时防剧透切片与跨玩家 403 拒绝。
/// </summary>
public sealed class DuelRecordEndpointTests
{
    private static readonly Guid SelfId = Guid.Parse("50000000-0000-4000-8000-000000000001");
    private static readonly Guid StrangerId = Guid.Parse("50000000-0000-4000-8000-000000000002");

    private static DuelBattleRecordDocument MakeRecord(string duelId, params double[] timestamps)
        => new(
            duelId,
            1000,
            timestamps.Length == 0 ? 1 : timestamps[^1] + 1,
            CombatOutcome.PlayerWon,
            Units: [new DuelRecordUnitDocument("甲", true), new DuelRecordUnitDocument("乙", false)],
            Defs: ["ability.test"],
            Actions: timestamps.Select(t => new DuelRecordActionDocument(
                t, 0, DuelRecordActionKind.Skill, 0,
                Effects: [new DuelRecordEffectDocument(0, 1, DuelRecordEffectKind.Damage, CombatDamageKind.Spell, CombatElement.Fire, DuelRecordHitMark.Hit, 30)])).ToArray());

    [Fact]
    public void SnapshotMapper_StripsDuelRecordsAndProjectsCount()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(new PlayerId(SelfId), new AccountId(SelfId), "甲", DateTimeOffset.UnixEpoch, catalog.Fingerprint);
        player.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1, PvpEnabled: true), DateTimeOffset.UnixEpoch, catalog.Actions);
        var actionId = player.Actions[0].Id;
        player.EnsureActionReport(actionId, new ActionId("combat.hunt-qi-beast"), "讨伐", DateTimeOffset.UnixEpoch);
        var playback = new DuelPlaybackState(
            "duel-1",
            StrangerId.ToString("N"),
            "乙",
            true,
            CombatOutcome.PlayerWon,
            5,
            [],
            [],
            [],
            CombatantStats.Create(100, 10, 0, 1),
            100,
            0,
            ViewerRecord: MakeRecord("duel-1", 0.5, 2.0));
        ActionReportBook.RecordDuelBattle(player, actionId, playback, 100);

        var snapshot = new PlayerRuntimeSnapshot(SelfId, SelfId.ToString("D"), "甲", 1, DateTimeOffset.UnixEpoch, player.ExportState(), 0, []);
        var dto = ProtocolSnapshotMapper.ToSnapshot(snapshot, catalog.Fingerprint, catalog);
        var duelSection = dto.Sections.Single(s => s.Section == "duel");
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(duelSection.Data, ProtocolJson.Options));
        var currentReport = doc.RootElement.GetProperty("currentReport");
        Assert.Equal(1, currentReport.GetProperty("duelRecordCount").GetInt32());
        Assert.False(currentReport.TryGetProperty("duelRecords", out _));
    }

    [Fact]
    public async Task DuelRecordsEndpoint_EnforcesOwnershipAndSlicesLiveDuel()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(new PlayerId(SelfId), new AccountId(SelfId), "甲", DateTimeOffset.UnixEpoch, catalog.Fingerprint);
        player.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1, PvpEnabled: true), DateTimeOffset.UnixEpoch, catalog.Actions);
        var actionId = player.Actions[0].Id;
        player.EnsureActionReport(actionId, new ActionId("combat.hunt-qi-beast"), "讨伐", DateTimeOffset.UnixEpoch);

        // 1 条已完结历史记录
        var finished = new DuelPlaybackState("duel-old", StrangerId.ToString("N"), "乙", true, CombatOutcome.PlayerWon,
            5, [], [], [], CombatantStats.Create(100, 10, 0, 1), 100, 0, ViewerRecord: MakeRecord("duel-old", 0.5));
        ActionReportBook.RecordDuelBattle(player, actionId, finished, 50);

        // 1 条进行中斗法（BattleElapsedSeconds = 1.5，只应看到 t=0.5 和 t=1.2 的行动，看不到 t=3.0）
        var state = player.ExportState();
        var livePlayback = new DuelPlaybackState("duel-live", StrangerId.ToString("N"), "乙", true, CombatOutcome.PlayerWon,
            5, [], [], [], CombatantStats.Create(100, 10, 0, 1), 100, 0, ViewerRecord: MakeRecord("duel-live", 0.5, 1.2, 3.0));
        var actions = new[]
        {
            new QueuedActionState(
                actionId,
                new ActionIntent(new ActionId("combat.hunt"), -1, PvpEnabled: true),
                DateTimeOffset.UnixEpoch,
                ActionQueueStatus.Running,
                0, -1, 0,
                Expedition: new CombatExpeditionState("enc.test", CombatExpeditionPhase.Duel, 0, 0, 1, 1.5, 100, 0, 0, 0, 0, CombatOutcome.Running)
                {
                    Duel = livePlayback
                })
        };
        state = state with { Actions = actions };
        var runtime = new FakeRuntime(new PlayerRuntimeSnapshot(SelfId, SelfId.ToString("D"), "甲", 2, DateTimeOffset.UnixEpoch.AddSeconds(10), state, 0, []));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IPlayerRuntime>(runtime);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        var players = app.MapGroup("/api/v1/players").RequireAuthorization();
        DuelRecordEndpoints.Map(players);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

            // 跨玩家访问应返回 403
            client.DefaultRequestHeaders.Add("x-test-player", StrangerId.ToString("D"));
            var forbidden = await client.GetAsync($"/api/v1/players/{SelfId:D}/duel-records");
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            // 本人拉取：首位为实时切片（out=0, 2 条行动），次位为已完结记录
            client.DefaultRequestHeaders.Remove("x-test-player");
            client.DefaultRequestHeaders.Add("x-test-player", SelfId.ToString("D"));
            var ok = await client.GetAsync($"/api/v1/players/{SelfId:D}/duel-records");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            using var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
            var records = body.RootElement.GetProperty("records");
            Assert.Equal(2, records.GetArrayLength());
            Assert.Equal("duel-live", records[0].GetProperty("id").GetString());
            Assert.Equal(0, records[0].GetProperty("out").GetInt32());
            Assert.Equal(2, records[0].GetProperty("acts").GetArrayLength());
            Assert.Equal("duel-old", records[1].GetProperty("id").GetString());
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private sealed class FakeRuntime(PlayerRuntimeSnapshot snapshot) : IPlayerRuntime
    {
        public int LoadedCount => 1;
        public DateTimeOffset? NextAdvanceBoundaryUtc => null;
#pragma warning disable CS0067
        public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated;
        public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted;
#pragma warning restore CS0067
        public ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken ct = default) => ValueTask.FromResult(snapshot);
        public ValueTask<PlayerRuntimeSnapshot> AttachConnectionAsync(Guid playerId, CancellationToken ct = default) => throw new NotSupportedException();
        public PlayerRuntimeSnapshot? TryGetLoaded(Guid playerId) => snapshot;
        public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, long expectedStateVersion, Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask FlushAsync(Guid playerId, bool force, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask FlushAllAsync(bool force, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(Guid playerId, bool flush, DateTimeOffset? offlineSince, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<PlayerRuntimeSnapshot> MutateOwnedAsync(Guid playerId, Action<PlayerAggregate> mutate, CancellationToken ct = default) => throw new NotSupportedException();
        public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId) => [];
        public ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken ct = default) => new([snapshot]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var id = Request.Headers["x-test-player"].ToString();
            if (string.IsNullOrWhiteSpace(id)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new[] { new Claim("player_id", id) };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
