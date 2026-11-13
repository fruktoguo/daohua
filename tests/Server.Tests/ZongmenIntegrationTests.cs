using System.Text.Json;
using DaoHua.Ecs;
using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Domain.World;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class ZongmenIntegrationTests
{
    /// <summary>创建原子性、重复扣物、并发同名、迁址和解散各自覆盖数据库事务边界。</summary>
    [Fact]
    public async Task CreationReplayAndDuplicateNameKeepTokenAndOrganizationAtomic()
    {
        await using var fixture = await Fixture.CreateAsync(3);
        var name = "宗门" + Guid.NewGuid().ToString("N")[..10];
        var payload = new { name, prefix = "青", anchorNodeId = "node.yunxi-village" };
        var request = Guid.NewGuid();
        await fixture.Apply(fixture.Players[0], "create", payload, request);
        await fixture.Apply(fixture.Players[0], "create", payload, request);
        Assert.Equal(2d, await fixture.TokenBalance(fixture.Players[0]));
        await Assert.ThrowsAsync<DomainException>(async () => await fixture.Apply(fixture.Players[1], "create", payload));
        Assert.Equal(3d, await fixture.TokenBalance(fixture.Players[1]));
        Assert.Null(await fixture.Repository.FindForPlayerAsync(fixture.Players[1]));
        await fixture.Sql("UPDATE economy.player_asset_balances SET quantity=0 WHERE player_id=$1 AND asset_id=$2", fixture.Players[2], ZongmenRules.TokenItemId);
        await Assert.ThrowsAsync<DomainException>(async () => await fixture.Apply(fixture.Players[2], "create",
            new { name = "无令" + Guid.NewGuid().ToString("N")[..10], prefix = "空", anchorNodeId = "node.yunxi-village" }));
        Assert.Null(await fixture.Repository.FindForPlayerAsync(fixture.Players[2]));
        var own = (await fixture.Repository.FindForPlayerAsync(fixture.Players[0]))!;
        Assert.Equal(20, own.Capacity);
        Assert.Equal(1, own.HallLevel);
        var ownState = await fixture.Repository.StateAsync(fixture.Players[0], new(), "", default);
        Assert.Equal(own.Id.ToString("D"), ownState.Zongmen!.ZongmenId);
        Assert.Null((await fixture.Repository.StateAsync(fixture.Players[1], new(), "", default)).Zongmen);
        var destination = fixture.Catalog.WorldGraph.Nodes.First(node => !node.Hidden && node.Id != own.AnchorNodeId).Id;
        await fixture.Apply(fixture.Players[0], "move", new { anchorNodeId = destination }, node: destination);
        Assert.Equal(1d, await fixture.TokenBalance(fixture.Players[0]));
        Assert.Equal(own.NodeId, (await fixture.Repository.FindForPlayerAsync(fixture.Players[0]))!.NodeId);
        await fixture.Apply(fixture.Players[0], "disband", new { });
        Assert.True((await fixture.Repository.FindAsync(own.Id))!.Disbanded);
        Assert.Null(await fixture.Repository.FindForPlayerAsync(fixture.Players[0]));
    }

    /// <summary>宗门行锁与角色唯一键共同保护最后名额及单宗门；工程完成后才释放新名额。</summary>
    [Fact]
    public async Task ConcurrentAdmissionsCannotOverfillAndHallCompletionUnlocksCapacity()
    {
        await using var fixture = await Fixture.CreateAsync(22);
        var leader = fixture.Players[0];
        await fixture.Apply(leader, "create", new { name = "人数" + Guid.NewGuid().ToString("N")[..10], prefix = "众", anchorNodeId = "node.yunxi-village" });
        var own = (await fixture.Repository.FindForPlayerAsync(leader))!;
        foreach (var player in fixture.Players.Skip(1))
            await fixture.Apply(player, "apply", new { zongmenId = own.Id });
        foreach (var player in fixture.Players.Skip(1).Take(18))
            await fixture.Apply(leader, "approve", new { targetPlayerId = player });
        await fixture.Apply(leader, "role", new { targetPlayerId = fixture.Players[1], role = 1 });
        var last = await Task.WhenAll(fixture.Players.Skip(19).Take(2).Select(async (player, index) =>
        {
            try { await fixture.Apply(index == 0 ? leader : fixture.Players[1], "approve", new { targetPlayerId = player }); return true; }
            catch (DomainException) { return false; }
        }));
        Assert.Single(last, value => value);
        Assert.Equal(20, (await fixture.Repository.FindAsync(own.Id))!.MemberCount);
        var materials = JsonSerializer.Serialize(NodeBuildingRules.RequiredMaterials(fixture.Catalog.Buildings[ZongmenRules.HallBuildingId], 2));
        await fixture.Sql("UPDATE game.node_buildings SET target_level=2,started_at_unix_seconds=$2,contributed_materials=$3::jsonb WHERE node_id=$1",
            own.NodeId, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1000d, materials);
        await fixture.Apply(leader, "approve", new { targetPlayerId = fixture.Players[^1] });
        Assert.Equal(25, (await fixture.Repository.FindAsync(own.Id))!.Capacity);
    }

    /// <summary>真实适配器保护动态节点入口、共享建筑及离宗后补算退出，私密频道同样按成员裁决。</summary>
    [Fact]
    public async Task ResidenceRejectsOutsidersAndRevokedMemberReturnsToAnchor()
    {
        await using var fixture = await Fixture.CreateAsync(3);
        var leader = fixture.Players[0];
        var member = fixture.Players[1];
        var outsider = fixture.Players[2];
        await fixture.Apply(leader, "create", new { name = "驻地" + Guid.NewGuid().ToString("N")[..10], prefix = "隐", anchorNodeId = "node.yunxi-village" });
        var own = (await fixture.Repository.FindForPlayerAsync(leader))!;
        await fixture.Apply(leader, "invite", new { targetPlayerId = member });
        await fixture.Apply(member, "accept", new { zongmenId = own.Id });
        var sections = new PostgresSectionRepository(fixture.Source, NullLogger<PostgresSectionRepository>.Instance);
        var clock = new Clock(DateTimeOffset.UtcNow);
        foreach (var player in fixture.Players)
        {
            var aggregate = PlayerAggregate.Create(new(player), new(player), player.ToString("N"), clock.UtcNow, fixture.Catalog.Fingerprint);
            aggregate.TravelTo("node.yunxi-village", 20, 65);
            await sections.EnqueueSectionsAsync(SectionCodec.EncodeAggregateFull(aggregate.ExportState(), clock.UtcNow));
        }
        using var ecs = new EcsGameRuntime(fixture.Catalog, clock.UtcNow);
        var buildings = new NodeBuildingRegistry(new PostgresNodeBuildingRepository(fixture.Source), fixture.Catalog,
            NullLogger<NodeBuildingRegistry>.Instance);
        await using var adapter = new EcsPlayerRuntimeAdapter(ecs, sections, new PostgresAssetRepository(fixture.Source),
            new PostgresEconomyRepository(fixture.Source, NullLogger<PostgresEconomyRepository>.Instance), fixture.Catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock, nodeBuildings: buildings, zongmen: fixture.Repository);
        async Task<PlayerCommandResult> Command(Guid player, string command, object payload) =>
            await adapter.ExecuteAsync(player, command, JsonSerializer.SerializeToElement(payload), 0, Guid.NewGuid());
        Assert.False((await Command(outsider, "world.travel", new { targetNodeId = own.NodeId })).Success);
        var actionRequestId = Guid.NewGuid();
        var enqueue = await adapter.ExecuteAsync(member, "action.enqueue", JsonSerializer.SerializeToElement(
            new { requestId = actionRequestId.ToString(), actionId = "cultivation.draw-qi", repeatCount = -1 }), 0, actionRequestId);
        Assert.True(enqueue.Success, enqueue.Message);
        Assert.True((await Command(member, "world.travel", new { targetNodeId = own.NodeId })).Success);
        Assert.Equal(ZongmenRules.ResidenceCoordinates(20, 65),
            (adapter.TryGetLoaded(member)!.State.World.X, adapter.TryGetLoaded(member)!.State.World.Y));
        Assert.Equal(-1, Assert.Single(adapter.TryGetLoaded(member)!.State.Actions).RemainingRepeats);
        Assert.True((await Command(member, "building.build", new { buildingId = "building.meditation-circle" })).Success);
        Assert.Equal(own.NodeId, adapter.TryGetLoaded(member)!.State.World.NodeId);
        Assert.Contains(adapter.TryGetLoaded(member)!.State.World.NodeBuildings!, building => building.BuildingId == ZongmenRules.HallBuildingId);
        await Assert.ThrowsAsync<DomainException>(async () => await fixture.Apply(member, "kick", new { targetPlayerId = leader }));
        var chat = new ChatRuntime(new PostgresChatRepository(fixture.Source), new PostgresSocialRepository(fixture.Source), new(), fixture.Repository);
        Assert.Equal(ChatSendStatus.ChannelUnavailable, (await chat.SendAsync(outsider, "外人", new("id", "sect", "不可见"))).Status);
        Assert.Equal(ChatSendStatus.Ok, (await chat.SendAsync(member, "门人", new("id", "sect", "同宗可见"))).Status);
        await fixture.Apply(leader, "kick", new { targetPlayerId = member });
        clock.UtcNow = DateTimeOffset.UtcNow.AddSeconds(1);
        await adapter.AdvanceAllAsync(clock.UtcNow);
        Assert.Equal(own.AnchorNodeId, adapter.TryGetLoaded(member)!.State.World.NodeId);
        Assert.Equal(-1, Assert.Single(adapter.TryGetLoaded(member)!.State.Actions).RemainingRepeats);
        Assert.False((await Command(member, "world.travel", new { targetNodeId = own.NodeId })).Success);
        Assert.Null(await chat.LoadHistoryAsync(member, new("id", "sect")));
    }

    /// <summary>
    /// 回归：LoadOwnedAsync 离线补算期间必须沿用持久化快照中已冻结的 World.NodeBuildings 投影
    /// （不得把离线尾段刚建成的节点建筑倒灌回整段离线区间，否则会与冷挂机补算口径分叉）；
    /// 补算结束后再按当前节点刷新 target 时刻最新建筑投影，供后续在线阶段立即生效。
    /// </summary>
    [Fact]
    public async Task OfflineLoadKeepsFrozenBuildingProjectionDuringCatchUpAndRefreshesAtTarget()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        var withNewBuildingLeader = fixture.Players[0];
        var baselineLeader = fixture.Players[1];
        await fixture.Apply(withNewBuildingLeader, "create",
            new { name = "新建" + Guid.NewGuid().ToString("N")[..10], prefix = "甲", anchorNodeId = "node.yunxi-village" });
        await fixture.Apply(baselineLeader, "create",
            new { name = "对照" + Guid.NewGuid().ToString("N")[..10], prefix = "乙", anchorNodeId = "node.yunxi-village" });
        var builtSect = (await fixture.Repository.FindForPlayerAsync(withNewBuildingLeader))!;
        var baseSect = (await fixture.Repository.FindForPlayerAsync(baselineLeader))!;

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-60);
        var anchorPos = ZongmenRules.ResidenceCoordinates(20, 65);
        var sections = new PostgresSectionRepository(fixture.Source, NullLogger<PostgresSectionRepository>.Instance);
        foreach (var (playerId, sectNodeId) in new[] { (withNewBuildingLeader, builtSect.NodeId), (baselineLeader, baseSect.NodeId) })
        {
            var aggregate = PlayerAggregate.Create(new(playerId), new(playerId), playerId.ToString("N"), t0, fixture.Catalog.Fingerprint);
            aggregate.SkipTutorial();
            aggregate.TravelTo(sectNodeId, anchorPos.X, anchorPos.Y);
            aggregate.Enqueue(new ActionIntent(new ActionId("cultivation.draw-qi"), -1), t0, fixture.Catalog.Actions);
            aggregate.SetPresence(PresenceState.Offline, t0);
            await sections.EnqueueSectionsAsync(SectionCodec.EncodeAggregateFull(aggregate.ExportState(), t0));
        }

        // 甲宗门驻地在 target = t0 + 60m 前建成「蒲团」（cultivationOutputRatio +12%），而两名玩家离线前快照均无此建筑。
        await fixture.Sql("""
            INSERT INTO game.node_buildings(node_id, building_id, level, target_level, started_at_unix_seconds, contributed_materials, updated_at)
            VALUES ($1, 'building.meditation-circle', 1, 0, $2, '{}'::jsonb, now())
            """, builtSect.NodeId, t0.ToUnixTimeSeconds());

        var target = t0.AddMinutes(60);
        var clock = new Clock(target);
        using var ecs = new EcsGameRuntime(fixture.Catalog, target);
        var buildings = new NodeBuildingRegistry(new PostgresNodeBuildingRepository(fixture.Source), fixture.Catalog,
            NullLogger<NodeBuildingRegistry>.Instance);
        await using var adapter = new EcsPlayerRuntimeAdapter(ecs, sections, new PostgresAssetRepository(fixture.Source),
            new PostgresEconomyRepository(fixture.Source, NullLogger<PostgresEconomyRepository>.Instance), fixture.Catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock, nodeBuildings: buildings, zongmen: fixture.Repository);

        var loadedBuilt = await adapter.LoadAsync(withNewBuildingLeader);
        var loadedBase = await adapter.LoadAsync(baselineLeader);

        // 离线补算沿用冻结投影：两者 60 分钟离线修为完全相等，不得被新建建筑倒灌。
        Assert.Equal(loadedBase.State.Progression.Experience, loadedBuilt.State.Progression.Experience);
        // 装载完成后已刷新 target 时刻最新建筑投影：甲宗门玩家快照包含蒲团，乙宗门不含。
        Assert.Contains(loadedBuilt.State.World.NodeBuildings!, b => b.BuildingId == "building.meditation-circle" && b.Level >= 1);
        Assert.DoesNotContain(loadedBase.State.World.NodeBuildings!, b => b.BuildingId == "building.meditation-circle");

        // 重连上线后再推进 5 分钟：已刷新的蒲团立即对甲玩家产生 +12% 修炼增量。
        await adapter.AttachConnectionAsync(withNewBuildingLeader);
        await adapter.AttachConnectionAsync(baselineLeader);
        var expBeforeOnlineBuilt = loadedBuilt.State.Progression.Experience;
        var expBeforeOnlineBase = loadedBase.State.Progression.Experience;
        clock.UtcNow = target.AddMinutes(5);
        await adapter.AdvanceAllAsync(clock.UtcNow);
        var deltaBuilt = adapter.TryGetLoaded(withNewBuildingLeader)!.State.Progression.Experience - expBeforeOnlineBuilt;
        var deltaBase = adapter.TryGetLoaded(baselineLeader)!.State.Progression.Experience - expBeforeOnlineBase;
        Assert.True(deltaBuilt > deltaBase,
            $"上线后已刷新建筑应当立即生效：deltaBuilt={deltaBuilt}, deltaBase={deltaBase}");
    }

    private sealed class Clock(DateTimeOffset at) : IPlayerSimulationClock { public DateTimeOffset UtcNow { get; set; } = at; public bool IsDeterministic => true; public void AdvanceTo(DateTimeOffset target) => UtcNow = target; }

    private sealed class Fixture(NpgsqlDataSource source) : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; } = source;
        public GameDataCatalog Catalog { get; } = GameDataCatalog.LoadEmbedded();
        public PostgresZongmenRepository Repository => new(Source, Catalog);
        public Guid[] Players { get; private set; } = [];
        public static async Task<Fixture> CreateAsync(int count)
        {
            var fixture = new Fixture(PostgresTestSchema.GetSharedDataSource(PostgresTestSchema.RequireConnectionString("宗门集成测试")));
            await PostgresTestSchema.EnsureAsync(fixture.Source);
            fixture.Players = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
            var names = fixture.Players.Select(player => player.ToString("N")).ToArray();
            await fixture.Sql("""
                INSERT INTO identity.accounts(account_id,login_normalized,password_hash,password_salt,password_iterations,created_at,updated_at)
                SELECT id, name, '\x00'::bytea, '\x00'::bytea, 100000, now(), now()
                FROM unnest($1::uuid[], $2::text[]) AS t(id, name)
                """, fixture.Players, names);
            await fixture.Sql("""
                INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,created_at,last_seen_at)
                SELECT id, id, name, name, now(), now()
                FROM unnest($1::uuid[], $2::text[]) AS t(id, name)
                """, fixture.Players, names);
            await fixture.Sql("""
                INSERT INTO economy.player_asset_balances(player_id,asset_id,variant,quantity,version,updated_at)
                SELECT id, $2, '', 3, 1, now()
                FROM unnest($1::uuid[]) AS t(id)
                """, fixture.Players, ZongmenRules.TokenItemId);
            return fixture;
        }
        public async Task Apply(Guid player, string action, object payload, Guid? requestId = null, string node = "node.yunxi-village")
        {
            var request = requestId ?? Guid.NewGuid();
            var json = JsonSerializer.SerializeToElement(payload);
            var receipt = new CommandReceipt(player, request, "zongmen." + action, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json.GetRawText()))), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
            await Repository.ExecuteAsync(player, request, "zongmen." + action, json, node, false, receipt, default);
        }
        public async Task<double> TokenBalance(Guid player)
        {
            await using var command = Source.CreateCommand("SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id=$2");
            command.Parameters.AddWithValue(player); command.Parameters.AddWithValue(ZongmenRules.TokenItemId);
            return (double)(await command.ExecuteScalarAsync())!;
        }
        public async Task Sql(string sql, params object[] values)
        {
            await using var command = Source.CreateCommand(sql);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            await command.ExecuteNonQueryAsync();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
