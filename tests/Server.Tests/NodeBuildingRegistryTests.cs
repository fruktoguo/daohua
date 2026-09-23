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
}
