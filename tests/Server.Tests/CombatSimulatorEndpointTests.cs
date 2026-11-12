using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Domain.Social;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Protocol;
using Server;
using Server.Clean;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 模拟器只读端点的 HTTP 边界回归：鉴权声明、本人/同队授权、loaded 优先与
/// SectionCodec 解码路径，全部经生产 <see cref="SimulatorEndpoints.Map"/> 最小宿主验证。
/// </summary>
public sealed class CombatSimulatorEndpointAuthTests
{
    private static readonly Guid Self = Guid.Parse("40000000-0000-4000-8000-000000000001");
    private static readonly Guid Ally = Guid.Parse("40000000-0000-4000-8000-000000000002");
    private static readonly Guid Stranger = Guid.Parse("40000000-0000-4000-8000-000000000003");

    private sealed class HostFixture : IAsyncDisposable
    {
        public required WebApplication App;
        public required HttpClient Client;
        public required FakePlayerRuntime Runtime;
        public required FakePlayerStateRepository Sections;
        public required FakeAssetBalanceRepository Assets;
        public required FakeSocialRepository Social;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private static async Task<HostFixture> StartAsync(GameDataCatalog catalog)
    {
        var runtime = new FakePlayerRuntime();
        var sections = new FakePlayerStateRepository();
        var assets = new FakeAssetBalanceRepository();
        var social = new FakeSocialRepository();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton<IPlayerRuntime>(runtime);
        builder.Services.AddSingleton<IPlayerStateRepository>(sections);
        builder.Services.AddSingleton<IAssetBalanceRepository>(assets);
        builder.Services.AddSingleton<ISocialRepository>(social);
        builder.Services.AddSingleton(new PostgresZongmenRepository(
            NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused"), catalog));
        builder.Services.AddSingleton(new NodeBuildingRegistry(
            new PostgresNodeBuildingRepository(NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused")),
            catalog, NullLogger<NodeBuildingRegistry>.Instance));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        var players = app.MapGroup("/api/v1/players").RequireAuthorization();
        Server.SimulatorEndpoints.Map(players);
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        return new HostFixture
        {
            App = app, Client = client, Runtime = runtime,
            Sections = sections, Assets = assets, Social = social
        };
    }

    private static PlayerRuntimeSnapshot LoadedSnapshot(GameDataCatalog catalog, Guid playerId, string name)
    {
        var state = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(playerId),
            name, DateTimeOffset.UnixEpoch, catalog.Fingerprint).ExportState();
        return new PlayerRuntimeSnapshot(playerId, playerId.ToString("D"), name, 1,
            DateTimeOffset.UnixEpoch, state, 0,
            [new AssetBalanceRecord(playerId, "item.zhixue-powder", "", 7, 1)]);
    }

    private static void As(HttpClient client, Guid playerId)
    {
        client.DefaultRequestHeaders.Remove("x-test-player");
        client.DefaultRequestHeaders.Add("x-test-player", playerId.ToString("D"));
    }

    [Fact]
    public async Task Profile_Anonymous_Returns401()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        await using var host = await StartAsync(catalog);
        var response = await host.Client.GetAsync($"/api/v1/players/{Self}/simulator-profile");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Runtime.LoadCalls);
        Assert.Equal(0, host.Sections.LoadCalls);
    }

    [Fact]
    public async Task Profile_OtherPlayer_Returns403WithoutReadingState()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        await using var host = await StartAsync(catalog);
        As(host.Client, Self);
        var response = await host.Client.GetAsync($"/api/v1/players/{Stranger}/simulator-profile");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, host.Runtime.LoadCalls);
        Assert.Equal(0, host.Runtime.TryGetLoadedCalls);
        Assert.Equal(0, host.Sections.LoadCalls);
    }

    [Fact]
    public async Task Profile_NonPartyTarget_Returns403WithoutReadingState()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        await using var host = await StartAsync(catalog);
        As(host.Client, Self);
        var response = await host.Client.GetAsync(
            $"/api/v1/players/{Self}/simulator-profile?targetPlayerId={Stranger}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, host.Sections.LoadCalls);
    }

    [Fact]
    public async Task Profile_SelfLoaded_Returns200WithWallet()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        await using var host = await StartAsync(catalog);
        host.Runtime.Loaded[Self] = LoadedSnapshot(catalog, Self, "预演本人");
        As(host.Client, Self);
        var response = await host.Client.GetAsync($"/api/v1/players/{Self}/simulator-profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var profile = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("预演本人", profile.GetProperty("displayName").GetString());
        Assert.Contains(profile.GetProperty("assets").EnumerateArray(),
            asset => asset.GetProperty("itemId").GetString() == "item.zhixue-powder"
                && asset.GetProperty("quantity").GetDouble() == 7);
        Assert.Equal(0, host.Runtime.LoadCalls);
        Assert.Equal(0, host.Sections.LoadCalls);
        Assert.Equal(0, host.Assets.LoadCalls);
    }

    [Fact]
    public async Task Profile_PartyAllyNotLoaded_ReadsSectionsWithoutLoad()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        await using var host = await StartAsync(catalog);
        host.Social.Parties[Self] = Guid.NewGuid();
        var partyId = host.Social.Parties[Self];
        host.Social.PartyRecords[partyId] = new PartyRecord(partyId, Self,
            PartyProfileRecord.Default, DateTimeOffset.UnixEpoch,
            [new PartyMemberRecord(Self, "本人"), new PartyMemberRecord(Ally, "队友")]);
        var allyState = PlayerAggregate.Create(new PlayerId(Ally), new AccountId(Ally),
            "队友", DateTimeOffset.UnixEpoch, catalog.Fingerprint).ExportState();
        var now = DateTimeOffset.UtcNow;
        var writes = SectionCodec.EncodeAggregateFull(allyState, now);
        host.Sections.Bundles[Ally] = new PlayerStateBundle(
            new PlayerIdentity(Ally, Ally, "队友", now),
            new PlayerStateHead(Ally, 1, PlayerStateHead.CurrentSchemaVersion, now,
                catalog.Fingerprint, "test", now),
            writes.ToDictionary(write => write.SectionKind,
                write => new StoredSection(Ally, write.SectionKind, write.StateVersion,
                    write.SchemaVersion, write.Codec, write.Payload.ToArray(),
                    write.RawBytes, write.Checksum, write.UpdatedAt)),
            []);
        host.Assets.Balances[Ally] = [new AssetBalanceRecord(Ally, "item.zhixue-powder", "", 2, 1)];
        As(host.Client, Self);
        var response = await host.Client.GetAsync(
            $"/api/v1/players/{Self}/simulator-profile?targetPlayerId={Ally}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("队友", profile.GetProperty("displayName").GetString());
        Assert.Equal(1, host.Sections.LoadCalls);
        Assert.Equal(0, host.Runtime.LoadCalls);
        Assert.Equal(1, host.Assets.LoadCalls);
    }

    [Fact]
    public async Task Profile_MissingPlayer_Returns404()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        await using var host = await StartAsync(catalog);
        As(host.Client, Self);
        var response = await host.Client.GetAsync($"/api/v1/players/{Self}/simulator-profile");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Environment_OtherPlayer_Returns403()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        await using var host = await StartAsync(catalog);
        As(host.Client, Self);
        var response = await host.Client.GetAsync(
            $"/api/v1/players/{Stranger}/simulator-environment");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed class FakePlayerRuntime : IPlayerRuntime
    {
        public readonly Dictionary<Guid, PlayerRuntimeSnapshot> Loaded = new();
        public int LoadCalls;
        public int TryGetLoadedCalls;
        public int LoadedCount => Loaded.Count;
        public DateTimeOffset? NextAdvanceBoundaryUtc => null;
#pragma warning disable CS0067
        public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated;
        public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted;
#pragma warning restore CS0067
        public ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken ct = default)
        {
            LoadCalls++;
            throw new InvalidOperationException("测试边界不允许隐式装载。");
        }
        public ValueTask<PlayerRuntimeSnapshot> AttachConnectionAsync(Guid playerId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public PlayerRuntimeSnapshot? TryGetLoaded(Guid playerId)
        {
            TryGetLoadedCalls++;
            return Loaded.GetValueOrDefault(playerId);
        }
        public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command,
            JsonElement payload, long expectedStateVersion, Guid requestId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask FlushAsync(Guid playerId, bool force, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask FlushAllAsync(bool force, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask RemoveAsync(Guid playerId, bool flush, DateTimeOffset? offlineSince, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<PlayerRuntimeSnapshot> MutateOwnedAsync(Guid playerId,
            Action<PlayerAggregate> mutate, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId) => [];
        public ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken ct = default)
            => new((IReadOnlyList<PlayerRuntimeSnapshot>)Loaded.Values.ToArray());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePlayerStateRepository : IPlayerStateRepository
    {
        public readonly Dictionary<Guid, PlayerStateBundle> Bundles = new();
        public int LoadCalls;
        public ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken ct = default)
        {
            LoadCalls++;
            return new(Bundles.GetValueOrDefault(playerId));
        }
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeAssetBalanceRepository : IAssetBalanceRepository
    {
        public readonly Dictionary<Guid, IReadOnlyList<AssetBalanceRecord>> Balances = new();
        public int LoadCalls;
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(
            Guid playerId, CancellationToken ct = default)
        {
            LoadCalls++;
            return new(Balances.GetValueOrDefault(playerId) ?? []);
        }
    }

    private sealed class FakeSocialRepository : ISocialRepository
    {
        public readonly Dictionary<Guid, Guid> Parties = new();
        public readonly Dictionary<Guid, PartyRecord> PartyRecords = new();
        public ValueTask<Guid?> GetPartyIdAsync(Guid playerId, CancellationToken ct = default)
            => new(Parties.GetValueOrDefault(playerId));
        public ValueTask<PartyRecord?> GetPartyAsync(Guid partyId, CancellationToken ct = default)
            => new(PartyRecords.GetValueOrDefault(partyId));
        public ValueTask<Guid?> FindPlayerByNameAsync(string displayName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<string?> GetDisplayNameAsync(Guid playerId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<PlayerIdentityView?> GetPlayerIdentityAsync(Guid playerId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<FriendRecord>> ListFriendsAsync(Guid playerId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<FriendRequestView>> ListPendingRequestsAsync(Guid playerId, bool incoming, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<SocialStateSnapshot> LoadStateSnapshotAsync(Guid playerId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> AreFriendsAsync(Guid left, Guid right, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TryCreateFriendRequestAsync(Guid requestId, Guid requesterId, Guid targetId, DateTimeOffset now, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TryAcceptFriendRequestAsync(Guid requesterId, Guid targetId, DateTimeOffset now, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TryRejectFriendRequestAsync(Guid requesterId, Guid targetId, DateTimeOffset now, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TryRemoveFriendAsync(Guid playerId, Guid friendId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<Guid> CreatePartyAsync(Guid leaderId, PartyProfileRecord profile, DateTimeOffset now, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TryUpdatePartyProfileAsync(Guid partyId, PartyProfileRecord profile, DateTimeOffset now, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TryUpdateDouDiZhuStakeAsync(Guid partyId, Guid leaderId, double stake, DateTimeOffset now, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TrySetDouDiZhuReadyAsync(Guid playerId, bool ready, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TryCreateDouDiZhuGameAsync(DouDiZhuGameState state, Guid startedBy, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<DouDiZhuGameState?> GetDouDiZhuGameAsync(Guid partyId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<DouDiZhuGameState>> ListActiveDouDiZhuGamesAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TrySaveDouDiZhuGameAsync(DouDiZhuGameState state, long expectedVersion, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TryCommitDouDiZhuGameAsync(DouDiZhuGameState state, long expectedVersion, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<PartyJoinResult> TryJoinPartyAsync(Guid partyId, Guid playerId, DateTimeOffset now, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PartyMemberRecord>?> DisbandPartyAsync(Guid partyId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TryKickPartyMemberAsync(Guid partyId, Guid playerId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TryRemovePartyMemberAsync(Guid playerId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask DeletePartyIfEmptyAsync(Guid partyId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PartyRecord>> ListPublicPartiesAsync(string activityKind, string nodeId, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask UpsertPartySeekerAsync(Guid playerId, string activityKind, string nodeId, double minValuation, DateTimeOffset now, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TryClearPartySeekerAsync(Guid playerId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<PartySeekerRecord?> GetPartySeekerAsync(Guid playerId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PartySeekerRecord>> ListPartySeekersAsync(string activityKind, string nodeId, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask UpsertPartyInvitationAsync(Guid partyId, Guid inviteeId, Guid inviterId, DateTimeOffset now, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<PartyInvitationRecord?> GetPartyInvitationAsync(Guid inviteeId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TryClearPartyInvitationAsync(Guid inviteeId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask TryTransferPartyLeaderAsync(Guid partyId, Guid leaderId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TrySetPartyMemberReadyAsync(Guid playerId, bool ready, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TryUpdatePartyOpenSlotsAsync(Guid partyId, int openSlots, DateTimeOffset now, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<PartySessionRecord?> GetPartySessionAsync(string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<PartySessionRecord?> GetActivePartySessionAsync(Guid partyId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PartySessionRecord>> ListActivePartySessionsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyDictionary<Guid, string>> GetPlayerNodeMapAsync(IReadOnlyCollection<Guid> playerIds, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PartyMemberEviction>> DeleteInactivePartyMembersAsync(DateTimeOffset cutoff, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<int> CountOfflineIdlePlayersAsync(DateTimeOffset cutoff, IReadOnlyCollection<Guid> onlinePlayerIds, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask<OfflinePlayerCounts> CountOfflinePlayersAsync(DateTimeOffset cutoff, IReadOnlyCollection<Guid> onlinePlayerIds, CancellationToken ct = default)
            => throw new NotSupportedException();
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
