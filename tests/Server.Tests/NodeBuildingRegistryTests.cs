using Domain.GameData;
using Domain.World;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// NodeBuildingRegistry 集成门禁（依赖 DAOHUA_MARKET_TEST_CONNECTION 专用库）：
/// 建造/贡献/升级的叠加写入与错误码语义、读路径不可变快照的版本戳单调性、
/// 到期完工在锁内惰性结算并落库的行为均逐位对齐原实现。
/// </summary>
[Collection("PostgresIntegration")]
public sealed class NodeBuildingRegistryTests
{
    /// <summary>测试配表：单座需 5 份 item.test-wood 的采集小屋（一级工期 600s）。</summary>
    private static GameDataCatalog CatalogWithTestBuilding() => GameDataCatalog.Parse([System.Text.Encoding.UTF8.GetBytes("""
        [
          {"id":"building.test-hut","name":"测试小屋","group":"production","requiredRealmLevel":1,
           "materials":[{"itemId":"item.test-wood","quantity":5}],
           "modifiers":{"cultivationOutputRatio":0.05}}
        ]
        """)]);

    /// <summary>建造后快照版本单调递增；贡献按剩余需求封顶叠加；双进度满格后读路径惰性完工并把等级落库。</summary>
    [Fact]
    public async Task RegistryPublishesVersionedSnapshotsAndFinalizesLazilyOnRead()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("节点建筑注册表集成测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var repository = new PostgresNodeBuildingRepository(source);
        var catalog = CatalogWithTestBuilding();
        var registry = new NodeBuildingRegistry(repository, catalog, NullLogger<NodeBuildingRegistry>.Instance);
        var nodeId = $"node.test-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        // 空节点：版本 0 空快照，重复读版本不变。
        var empty = await registry.BuildingsForAsync(nodeId, now, CancellationToken.None);
        Assert.Equal(0, empty.Version);
        Assert.Empty(empty.Buildings);
        Assert.Equal(0, (await registry.BuildingsForAsync(nodeId, now, CancellationToken.None)).Version);

        // 发起建造：开工时刻回拨 700s 使时间进度已满（仅差材料），便于观测惰性完工。
        var definition = catalog.Buildings["building.test-hut"];
        var started = await registry.StartConstructionAsync(nodeId, definition, now.AddSeconds(-700), CancellationToken.None);
        Assert.True(started.Success);

        var underConstruction = await registry.BuildingsForAsync(nodeId, now, CancellationToken.None);
        Assert.True(underConstruction.Version > 0);
        var draft = Assert.Single(underConstruction.Buildings);
        Assert.Equal(0, draft.Level);
        Assert.Equal(1, draft.TargetLevel);

        // 材料为 0 时尚未到期：重复读命中已发布快照，版本不漂。
        var reread = await registry.BuildingsForAsync(nodeId, now, CancellationToken.None);
        Assert.Equal(underConstruction.Version, reread.Version);

        // 贡献超量材料按剩余需求封顶：申请 8，实际入账 5。
        var debited = new Dictionary<string, double>(StringComparer.Ordinal);
        var contribute = await registry.ContributeAsync(nodeId, "building.test-hut",
            new Dictionary<string, double> { ["item.test-wood"] = 8 },
            debit =>
            {
                foreach (var pair in debit) debited[pair.Key] = pair.Value;
                return ValueTask.FromResult(true);
            },
            CancellationToken.None);
        Assert.True(contribute.Success);
        Assert.Equal(5d, debited["item.test-wood"]);
        Assert.Equal(5d, contribute.AppliedMaterials!["item.test-wood"]);

        // 材料+时间双满：下一次读即触发惰性完工，版本再递增且数据库行落成 level=1。
        var finalized = await registry.BuildingsForAsync(nodeId, now.AddSeconds(1), CancellationToken.None);
        Assert.True(finalized.Version > underConstruction.Version);
        var built = Assert.Single(finalized.Buildings);
        Assert.Equal(1, built.Level);
        Assert.Equal(0, built.TargetLevel);
        Assert.Empty(built.ContributedMaterials);

        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT level, target_level FROM game.node_buildings WHERE node_id = $1 AND building_id = $2", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = nodeId });
        command.Parameters.Add(new NpgsqlParameter { Value = "building.test-hut" });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));

        // 完工后再读：无到期工程，版本稳定不再漂移。
        var after = await registry.BuildingsForAsync(nodeId, now.AddSeconds(2), CancellationToken.None);
        Assert.Equal(finalized.Version, after.Version);
    }

    /// <summary>写路径保持单写者串行语义：重复建造/未建成升级/扣减失败不落库等错误码与原实现一致。</summary>
    [Fact]
    public async Task RegistryWritePathKeepsSingleWriterSemanticsAndErrorCodes()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("节点建筑注册表集成测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var repository = new PostgresNodeBuildingRepository(source);
        var catalog = CatalogWithTestBuilding();
        var registry = new NodeBuildingRegistry(repository, catalog, NullLogger<NodeBuildingRegistry>.Instance);
        var nodeId = $"node.test-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        var missing = await registry.StartUpgradeAsync(nodeId, "building.test-hut", now, CancellationToken.None);
        Assert.False(missing.Success);
        Assert.Equal("BUILDING_NOT_FOUND", missing.ErrorCode);

        var definition = catalog.Buildings["building.test-hut"];
        Assert.True((await registry.StartConstructionAsync(nodeId, definition, now, CancellationToken.None)).Success);

        // 同节点同种重复建造拒绝。
        var duplicate = await registry.StartConstructionAsync(nodeId, definition, now, CancellationToken.None);
        Assert.False(duplicate.Success);
        Assert.Equal("BUILDING_EXISTS", duplicate.ErrorCode);

        // 未建成（Level=0）不可升级。
        var premature = await registry.StartUpgradeAsync(nodeId, "building.test-hut", now, CancellationToken.None);
        Assert.False(premature.Success);
        Assert.Equal("BUILDING_INCOMPLETE", premature.ErrorCode);

        // 扣减回调返回 false：材料不叠加、不落库、版本不变。
        var before = await registry.BuildingsForAsync(nodeId, now, CancellationToken.None);
        var denied = await registry.ContributeAsync(nodeId, "building.test-hut",
            new Dictionary<string, double> { ["item.test-wood"] = 2 },
            _ => ValueTask.FromResult(false), CancellationToken.None);
        Assert.False(denied.Success);
        Assert.Equal("INSUFFICIENT_MATERIALS", denied.ErrorCode);
        var after = await registry.BuildingsForAsync(nodeId, now, CancellationToken.None);
        Assert.Equal(before.Version, after.Version);
        Assert.Empty(after.Buildings[0].ContributedMaterials);

        // 无工程或工程已结束时贡献按原错误码拒绝：先贡献满材料并保持时间未满，工程仍进行中；
        // 贡献 0/未知物品无效；对完工后建筑贡献报 BUILDING_IDLE。
        var idleProbe = await registry.ContributeAsync(nodeId, "building.test-hut",
            new Dictionary<string, double> { ["item.unrelated"] = 1 },
            _ => ValueTask.FromResult(true), CancellationToken.None);
        Assert.False(idleProbe.Success);
        Assert.Equal("NOTHING_TO_CONTRIBUTE", idleProbe.ErrorCode);
    }

    /// <summary>
    /// 交叉写防丢（多实例贡献合并）：读快照后行被外部 UPDATE（模拟另一实例已落库的贡献），
    /// 本实例再写贡献必须按数据库活行封顶并叠加合并，不得用缓存投影整行覆盖吞掉外部贡献。
    /// </summary>
    [Fact]
    public async Task ContributeMergesOntoLiveRowAfterExternalWrite()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("节点建筑注册表集成测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var repository = new PostgresNodeBuildingRepository(source);
        var catalog = CatalogWithTestBuilding();
        var registry = new NodeBuildingRegistry(repository, catalog, NullLogger<NodeBuildingRegistry>.Instance);
        var nodeId = $"node.test-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        var definition = catalog.Buildings["building.test-hut"];
        Assert.True((await registry.StartConstructionAsync(nodeId, definition, now, CancellationToken.None)).Success);

        // 另一实例的贡献已落库：活行材料 {wood:3}，本实例缓存仍是空材料。
        await using (var connection = await source.OpenConnectionAsync())
        {
            await using var external = new NpgsqlCommand("""
                UPDATE game.node_buildings
                SET contributed_materials = '{"item.test-wood": 3}'::jsonb, updated_at = now()
                WHERE node_id = $1 AND building_id = $2
                """, connection);
            external.Parameters.Add(new NpgsqlParameter { Value = nodeId });
            external.Parameters.Add(new NpgsqlParameter { Value = "building.test-hut" });
            Assert.Equal(1, await external.ExecuteNonQueryAsync());
        }

        // 本实例按旧缓存看到的剩余需求是 5：若整行覆盖会扣 min(5,4)=4 并吞掉外部的 3；
        // 行锁内按活行封顶只接纳剩余 2，合并后落库为 3+2=5。
        var debited = new Dictionary<string, double>(StringComparer.Ordinal);
        var result = await registry.ContributeAsync(nodeId, "building.test-hut",
            new Dictionary<string, double> { ["item.test-wood"] = 4 },
            debit =>
            {
                foreach (var pair in debit) debited[pair.Key] = pair.Value;
                return ValueTask.FromResult(true);
            },
            CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(2d, result.AppliedMaterials!["item.test-wood"]);
        Assert.Equal(2d, debited["item.test-wood"]);

        // 数据库行为权威：两份贡献合并保留，外部写入未被覆盖。
        await using var verify = await source.OpenConnectionAsync();
        await using var check = new NpgsqlCommand(
            "SELECT contributed_materials FROM game.node_buildings WHERE node_id = $1 AND building_id = $2", verify);
        check.Parameters.Add(new NpgsqlParameter { Value = nodeId });
        check.Parameters.Add(new NpgsqlParameter { Value = "building.test-hut" });
        var json = Assert.IsType<string>(await check.ExecuteScalarAsync());
        using (var document = System.Text.Json.JsonDocument.Parse(json))
            Assert.Equal(5d, document.RootElement.GetProperty("item.test-wood").GetDouble());

        // 缓存快照同步采纳活行：读路径看得到合并结果而非旧投影。
        var snapshot = await registry.BuildingsForAsync(nodeId, now, CancellationToken.None);
        Assert.Equal(5d, Assert.Single(snapshot.Buildings).ContributedMaterials["item.test-wood"]);
    }

    /// <summary>
    /// 裁决只认数据库活行：外部实例完工后再贡献按 BUILDING_IDLE 拒绝且不触发扣减，缓存同步采纳完工行；
    /// 外部新建、缓存未收录的建筑可直接贡献并把活行纳入快照（跨实例创建可见性）。
    /// </summary>
    [Fact]
    public async Task ContributeDecidesOnLiveRowAfterExternalFinalizeAndCreate()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("节点建筑注册表集成测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var repository = new PostgresNodeBuildingRepository(source);
        var catalog = CatalogWithTestBuilding();
        var registry = new NodeBuildingRegistry(repository, catalog, NullLogger<NodeBuildingRegistry>.Instance);
        var nodeId = $"node.test-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        var definition = catalog.Buildings["building.test-hut"];
        Assert.True((await registry.StartConstructionAsync(nodeId, definition, now.AddSeconds(-700), CancellationToken.None)).Success);

        // 另一实例已把到期工程完工落库（level=1,target=0），本实例缓存仍是 target=1 在建行。
        await using (var connection = await source.OpenConnectionAsync())
        {
            await using var external = new NpgsqlCommand("""
                UPDATE game.node_buildings
                SET level = 1, target_level = 0, contributed_materials = '{}'::jsonb, updated_at = now()
                WHERE node_id = $1 AND building_id = $2
                """, connection);
            external.Parameters.Add(new NpgsqlParameter { Value = nodeId });
            external.Parameters.Add(new NpgsqlParameter { Value = "building.test-hut" });
            Assert.Equal(1, await external.ExecuteNonQueryAsync());
        }

        // 旧缓存认为工程仍在进行：行锁内读活行应按 BUILDING_IDLE 拒绝，且扣减回调不被触发。
        var debitInvoked = false;
        var idle = await registry.ContributeAsync(nodeId, "building.test-hut",
            new Dictionary<string, double> { ["item.test-wood"] = 1 },
            _ =>
            {
                debitInvoked = true;
                return ValueTask.FromResult(true);
            },
            CancellationToken.None);
        Assert.False(idle.Success);
        Assert.Equal("BUILDING_IDLE", idle.ErrorCode);
        Assert.False(debitInvoked);

        // 缓存采纳完工活行：快照即刻反映已建成状态。
        var snapshot = await registry.BuildingsForAsync(nodeId, now, CancellationToken.None);
        var built = Assert.Single(snapshot.Buildings);
        Assert.Equal(1, built.Level);
        Assert.Equal(0, built.TargetLevel);

        // 外部新建的建筑行不在本实例缓存内：贡献按活行成立，并把活行采纳进快照。
        var foreignNodeId = $"node.test-{Guid.NewGuid():N}";
        await using (var connection = await source.OpenConnectionAsync())
        {
            await using var external = new NpgsqlCommand("""
                INSERT INTO game.node_buildings (node_id, building_id, level, target_level, started_at_unix_seconds, contributed_materials, updated_at)
                VALUES ($1, 'building.test-hut', 0, 1, $2, '{}'::jsonb, now())
                """, connection);
            external.Parameters.Add(new NpgsqlParameter { Value = foreignNodeId });
            external.Parameters.Add(new NpgsqlParameter { Value = now.ToUnixTimeMilliseconds() / 1000d });
            Assert.Equal(1, await external.ExecuteNonQueryAsync());
        }

        var foreign = await registry.ContributeAsync(foreignNodeId, "building.test-hut",
            new Dictionary<string, double> { ["item.test-wood"] = 2 },
            _ => ValueTask.FromResult(true),
            CancellationToken.None);
        Assert.True(foreign.Success);
        Assert.Equal(2d, foreign.AppliedMaterials!["item.test-wood"]);
        var foreignSnapshot = await registry.BuildingsForAsync(foreignNodeId, now, CancellationToken.None);
        Assert.Equal(2d, Assert.Single(foreignSnapshot.Buildings).ContributedMaterials["item.test-wood"]);
    }
}
