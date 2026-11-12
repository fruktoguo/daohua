using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Domain.GameData;
using Domain.World;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server;
using Server.Clean;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 模拟器环境端点的真实持久化回归：静态公共节点 ∪ 本人宗门动态驻地 ∪ 建筑拥挤投影。
/// 依赖 DAOHUA_MARKET_TEST_CONNECTION 指向本任务专用空库。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class CombatSimulatorEndpointEnvironmentTests
{
    private static readonly Guid Self = Guid.Parse("40000000-0000-4000-8000-0000000000e1");
    private static readonly Guid Other = Guid.Parse("40000000-0000-4000-8000-0000000000e2");

    private static async Task<NpgsqlDataSource> ConnectAsync()
    {
        var source = NpgsqlDataSource.Create(
            PostgresTestSchema.RequireConnectionString("模拟器环境端点测试"));
        await PostgresTestSchema.EnsureAsync(source);
        return source;
    }

    private static async Task ExecuteSqlAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values)
            command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(
        GameDataCatalog catalog, NpgsqlDataSource source)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton(new PostgresZongmenRepository(source, catalog));
        builder.Services.AddSingleton(new NodeBuildingRegistry(
            new PostgresNodeBuildingRepository(source), catalog,
            NullLogger<NodeBuildingRegistry>.Instance));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        var players = app.MapGroup("/api/v1/players").RequireAuthorization();
        Server.SimulatorEndpoints.Map(players);
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("x-test-player", Self.ToString("D"));
        return (app, client);
    }

    [Fact]
    public async Task Environment_Self_IncludesResidenceBuildingsAndCrowding()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        await using var source = await ConnectAsync();
        var nodeId = catalog.WorldGraph.Nodes
            .First(node => node.EncounterIds.Count > 0).Id;
        var buildingId = catalog.Buildings.Keys.OrderBy(id => id, StringComparer.Ordinal).First();
        var zongmenId = Guid.NewGuid();
        var otherZongmenId = Guid.NewGuid();
        var residenceNodeId = ZongmenRules.ResidenceNodeId(zongmenId);
        var otherResidenceNodeId = ZongmenRules.ResidenceNodeId(otherZongmenId);

        await ExecuteSqlAsync(source, """
            INSERT INTO identity.accounts(account_id, login_normalized, password_hash, password_salt, password_iterations, created_at, updated_at)
            VALUES ($1, $2, '\x00'::bytea, '\x00'::bytea, 100000, now(), now()),
                   ($3, $4, '\x00'::bytea, '\x00'::bytea, 100000, now(), now())
            """, Self, Self.ToString("N"), Other, Other.ToString("N"));
        await ExecuteSqlAsync(source, """
            INSERT INTO identity.players(player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at)
            VALUES ($1, $1, $2, $2, now(), now()), ($3, $3, $4, $4, now(), now())
            """, Self, "本人", Self.ToString("N"), Other, "路人", Other.ToString("N"));
        await ExecuteSqlAsync(source, """
            INSERT INTO social.zongmen(zongmen_id, name, name_normalized, prefix, leader_id, anchor_node_id, created_at)
            VALUES ($1, $2, $2, 'AB', $3, $4, now()), ($5, $6, $6, 'CD', $7, $8, now())
            """, zongmenId, "测试宗门", Self, nodeId, otherZongmenId, "别人宗门", Other, nodeId);
        await ExecuteSqlAsync(source, """
            INSERT INTO social.zongmen_members(player_id, zongmen_id, role, joined_at)
            VALUES ($1, $2, 0, now()), ($3, $4, 0, now())
            """, Self, zongmenId, Other, otherZongmenId);
        await ExecuteSqlAsync(source, """
            INSERT INTO game.node_buildings(node_id, building_id, level, target_level, started_at_unix_seconds, updated_at)
            VALUES ($1, $2, 3, 0, 0, now())
            """, nodeId, buildingId);

        var (app, client) = await StartAsync(catalog, source);
        await using (app)
        {
            var response = await client.GetAsync($"/api/v1/players/{Self}/simulator-environment");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var environment = await response.Content.ReadFromJsonAsync<JsonElement>();
            var nodes = environment.GetProperty("nodes").EnumerateArray().ToArray();

            // 静态节点全量（含空建筑条目），建筑拥挤投影真实可见。
            var target = nodes.Single(node => node.GetProperty("nodeId").GetString() == nodeId);
            var building = Assert.Single(target.GetProperty("buildings").EnumerateArray());
            Assert.Equal(buildingId, building.GetProperty("buildingId").GetString());
            Assert.Equal(3, building.GetProperty("level").GetInt32());
            Assert.Contains(target.GetProperty("usages").EnumerateArray(),
                usage => usage.GetProperty("buildingId").GetString() == buildingId);
            Assert.True(nodes.Any(node => node.GetProperty("buildings").GetArrayLength() == 0),
                "环境快照必须保留无建筑节点的已知空条目。");

            // 本人宗门动态驻地在环境中，他人驻地被过滤。
            Assert.Contains(nodes, node => node.GetProperty("nodeId").GetString() == residenceNodeId);
            Assert.DoesNotContain(nodes, node => node.GetProperty("nodeId").GetString() == otherResidenceNodeId);
            var residence = nodes.Single(node => node.GetProperty("nodeId").GetString() == residenceNodeId);
            Assert.Equal(0, residence.GetProperty("buildings").GetArrayLength());

            // 无敏感账号/行动队列字段。
            var text = environment.GetRawText();
            foreach (var forbidden in new[] { "accountId", "actions", "actionQueue", "password", "displayName" })
                Assert.DoesNotContain($"\"{forbidden}\"", text);
        }
    }

    [Fact]
    public async Task Environment_OtherPlayer_Still403WithRealRepositories()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        await using var source = await ConnectAsync();
        var (app, client) = await StartAsync(catalog, source);
        await using (app)
        {
            var response = await client.GetAsync($"/api/v1/players/{Other}/simulator-environment");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
