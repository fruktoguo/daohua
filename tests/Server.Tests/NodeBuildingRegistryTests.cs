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
[Trait("Category", "PostgresIntegration")]
public sealed class NodeBuildingRegistryTests
{
    private static async Task<NodeBuildingContributionTransaction> SeedContributorAsync(NpgsqlDataSource source, double quantity)
    {
        var playerId = Guid.NewGuid();
        await ExecuteSqlAsync(source, """
            INSERT INTO identity.accounts(account_id, login_normalized, password_hash, password_salt, password_iterations, created_at, updated_at)
            VALUES ($1, $2, '\x00'::bytea, '\x00'::bytea, 100000, now(), now())
            """, playerId, playerId.ToString("N"));
        await ExecuteSqlAsync(source, """
            INSERT INTO identity.players(player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at)
            VALUES ($1, $1, $2, $2, now(), now())
            """, playerId, playerId.ToString("N"));
        await ExecuteSqlAsync(source, """
            INSERT INTO economy.player_asset_balances(player_id, asset_id, variant, quantity, version, updated_at)
            VALUES ($1, 'item.test-wood', '', $2, 1, now())
            """, playerId, quantity);
        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid();
        return new(playerId, requestId, $"building-test:{requestId:D}", "building.contribute", now,
            Receipts: [new CommandReceipt(playerId, requestId, "building.contribute", new string('a', 64), 1, now, now.AddMinutes(30))]);
    }

    private static async Task ExecuteSqlAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlDataSource source, string sql, Guid id)
    {
        await using var command = source.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<double> BalanceAsync(NpgsqlDataSource source, Guid playerId)
    {
        await using var command = source.CreateCommand("SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id='item.test-wood'");
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        return (double)(await command.ExecuteScalarAsync())!;
    }

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
        var contributor = await SeedContributorAsync(source, 8);
        var contribute = await registry.ContributeAsync(nodeId, "building.test-hut",
            new Dictionary<string, double> { ["item.test-wood"] = 8 }, contributor, CancellationToken.None);
        Assert.True(contribute.Success);
        Assert.Equal(3d, await BalanceAsync(source, contributor.PlayerId));
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

        // 仓储余额不足：材料、流水、回执全部回滚，缓存版本不变。
        var contributor = await SeedContributorAsync(source, 0);
        var before = await registry.BuildingsForAsync(nodeId, now, CancellationToken.None);
        var denied = await registry.ContributeAsync(nodeId, "building.test-hut",
            new Dictionary<string, double> { ["item.test-wood"] = 2 }, contributor, CancellationToken.None);
        Assert.False(denied.Success);
        Assert.Equal("INSUFFICIENT_MATERIALS", denied.ErrorCode);
        var after = await registry.BuildingsForAsync(nodeId, now, CancellationToken.None);
        Assert.Equal(before.Version, after.Version);
        Assert.Empty(after.Buildings[0].ContributedMaterials);

        // 无工程或工程已结束时贡献按原错误码拒绝：先贡献满材料并保持时间未满，工程仍进行中；
        // 贡献 0/未知物品无效；对完工后建筑贡献报 BUILDING_IDLE。
        var idleProbe = await registry.ContributeAsync(nodeId, "building.test-hut",
            new Dictionary<string, double> { ["item.unrelated"] = 1 },
            contributor, CancellationToken.None);
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
        var contributor = await SeedContributorAsync(source, 4);
        var result = await registry.ContributeAsync(nodeId, "building.test-hut",
            new Dictionary<string, double> { ["item.test-wood"] = 4 }, contributor, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(2d, result.AppliedMaterials!["item.test-wood"]);
        Assert.Equal(2d, await BalanceAsync(source, contributor.PlayerId));
        var replay = await registry.ContributeAsync(nodeId, "building.test-hut",
            new Dictionary<string, double> { ["item.test-wood"] = 4 }, contributor, CancellationToken.None);
        Assert.True(replay.Economy!.AlreadyCommitted);
        Assert.Equal(2d, replay.AppliedMaterials!["item.test-wood"]);
        Assert.Equal(2d, await BalanceAsync(source, contributor.PlayerId));
        await Assert.ThrowsAsync<EconomyIdempotencyConflictException>(async () => await registry.ContributeAsync(
            nodeId, "building.test-hut", new Dictionary<string, double> { ["item.test-wood"] = 3 }, contributor, CancellationToken.None));

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

        // 旧缓存认为工程仍在进行：行锁内读活行应按 BUILDING_IDLE 拒绝，不产生扣款流水。
        var contributor = await SeedContributorAsync(source, 2);
        var idle = await registry.ContributeAsync(nodeId, "building.test-hut",
            new Dictionary<string, double> { ["item.test-wood"] = 1 }, contributor, CancellationToken.None);
        Assert.False(idle.Success);
        Assert.Equal("BUILDING_IDLE", idle.ErrorCode);
        Assert.Equal(2d, await BalanceAsync(source, contributor.PlayerId));

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
            contributor,
            CancellationToken.None);
        Assert.True(foreign.Success);
        Assert.Equal(2d, foreign.AppliedMaterials!["item.test-wood"]);
        var foreignSnapshot = await registry.BuildingsForAsync(foreignNodeId, now, CancellationToken.None);
        Assert.Equal(2d, Assert.Single(foreignSnapshot.Buildings).ContributedMaterials["item.test-wood"]);
    }

    /// <summary>建筑更新在扣款、流水和回执之后失败时，数据库必须整体回滚；同一原请求可以安全重试。</summary>
    [Fact]
    public async Task BuildingWriteFailureRollsBackDebitReceiptAndLedgerTogether()
    {
        await using var source = NpgsqlDataSource.Create(PostgresTestSchema.RequireConnectionString("建筑原子回滚测试"));
        await PostgresTestSchema.EnsureAsync(source);
        var catalog = CatalogWithTestBuilding();
        var repository = new PostgresNodeBuildingRepository(source);
        var registry = new NodeBuildingRegistry(repository, catalog, NullLogger<NodeBuildingRegistry>.Instance);
        var nodeId = $"node.atomic-{Guid.NewGuid():N}";
        var contributor = await SeedContributorAsync(source, 5);
        await registry.StartConstructionAsync(nodeId, catalog.Buildings["building.test-hut"], DateTimeOffset.UtcNow, CancellationToken.None);
        var name = $"reject_building_{Guid.NewGuid():N}";
        // 触发器仅针对本用例节点，避免污染同库其他测试；真实故障发生在经济写入之后的建筑 UPDATE。
        await ExecuteSqlAsync(source, $"""
            CREATE FUNCTION game.{name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.node_id = '{nodeId}' THEN RAISE EXCEPTION '注入建筑写失败'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {name} BEFORE UPDATE ON game.node_buildings FOR EACH ROW EXECUTE FUNCTION game.{name}();
            """);
        var requested = new Dictionary<string, double> { ["item.test-wood"] = 5 };
        try
        {
            await Assert.ThrowsAsync<PostgresException>(async () => await registry.ContributeAsync(
                nodeId, "building.test-hut", requested, contributor, CancellationToken.None));
            Assert.Equal(5d, await BalanceAsync(source, contributor.PlayerId));
            Assert.Equal(0L, await CountAsync(source, "SELECT count(*) FROM economy.transactions WHERE transaction_id=$1", contributor.RequestId));
            Assert.Equal(0L, await CountAsync(source, "SELECT count(*) FROM platform.command_receipts WHERE request_id=$1", contributor.RequestId));
            Assert.Empty(Assert.Single((await registry.BuildingsForAsync(nodeId, DateTimeOffset.UtcNow, CancellationToken.None)).Buildings).ContributedMaterials);
        }
        finally
        {
            await ExecuteSqlAsync(source, $"DROP TRIGGER {name} ON game.node_buildings; DROP FUNCTION game.{name}();");
        }
        var result = await registry.ContributeAsync(nodeId, "building.test-hut", requested, contributor, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(0d, await BalanceAsync(source, contributor.PlayerId));
        Assert.Equal(1L, await CountAsync(source, "SELECT count(*) FROM platform.command_receipts WHERE request_id=$1", contributor.RequestId));
    }

    /// <summary>
    /// 使用人数读模型（player_action_queue×player_projection GROUP BY 派生）：Running 行动按驻留
    /// 节点与行动类别聚合——离线挂机计入、suspended 与 pending 不计、跨节点隔离、未配表行动忽略；
    /// 使用计数同时驱动建筑效果衰减与「使用中 n/cap」展示，两个出口同一份数据。
    /// </summary>
    [Fact]
    public async Task UsageReadModelCountsRunningActionsPerNodeAndKind()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("建筑使用人数读模型集成测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var repository = new PostgresNodeBuildingRepository(source);
        var activity = new PostgresNodeActivityRepository(source);
        var catalog = CatalogWithTestBuilding();
        var registry = new NodeBuildingRegistry(repository, catalog, NullLogger<NodeBuildingRegistry>.Instance, activity);
        var nodeId = $"node.test-{Guid.NewGuid():N}";
        var otherNode = $"node.test-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var accountId = Guid.NewGuid();

        await using (var connection = await source.OpenConnectionAsync())
        {
            await using var account = new NpgsqlCommand("""
                INSERT INTO identity.accounts (account_id, login_normalized, password_hash, password_salt, password_iterations, created_at, updated_at)
                VALUES ($1, $2, '\x00'::bytea, '\x00'::bytea, 100000, now(), now())
                """, connection);
            account.Parameters.Add(new NpgsqlParameter { Value = accountId });
            account.Parameters.Add(new NpgsqlParameter { Value = $"usage-{accountId:N}" });
            await account.ExecuteNonQueryAsync();

            // 一座已建成的修炼增益建筑（Lv1 → 承载 100）；直建行，本用例只测使用计数链路。
            await using var building = new NpgsqlCommand("""
                INSERT INTO game.node_buildings (node_id, building_id, level, target_level, started_at_unix_seconds, contributed_materials, updated_at)
                VALUES ($1, 'building.test-hut', 1, 0, 0, '{}'::jsonb, now())
                """, connection);
            building.Parameters.Add(new NpgsqlParameter { Value = nodeId });
            await building.ExecuteNonQueryAsync();

            var ordinal = 0;
            async Task SeedAsync(string node, string presence, string actionId, string status)
            {
                var playerId = Guid.NewGuid();
                await using var player = new NpgsqlCommand("""
                    INSERT INTO identity.players (player_id, account_id, display_name, display_name_normalized, created_at, last_seen_at)
                    VALUES ($1, $2, $3, $4, now(), now())
                    """, connection);
                player.Parameters.Add(new NpgsqlParameter { Value = playerId });
                player.Parameters.Add(new NpgsqlParameter { Value = accountId });
                player.Parameters.Add(new NpgsqlParameter { Value = $"u{ordinal}" });
                player.Parameters.Add(new NpgsqlParameter { Value = $"u{ordinal++}-{accountId:N}" });
                await player.ExecuteNonQueryAsync();
                await using var projection = new NpgsqlCommand("""
                    INSERT INTO game.player_projection (player_id, current_node_id, presence, last_active_at, updated_at)
                    VALUES ($1, $2, $3, now(), now())
                    """, connection);
                projection.Parameters.Add(new NpgsqlParameter { Value = playerId });
                projection.Parameters.Add(new NpgsqlParameter { Value = node });
                projection.Parameters.Add(new NpgsqlParameter { Value = presence });
                await projection.ExecuteNonQueryAsync();
                await using var queue = new NpgsqlCommand("""
                    INSERT INTO game.player_action_queue (player_id, slot, action_id, action_payload, action_version, status, idempotency_key, updated_at)
                    VALUES ($1, 0, $2, '\x7b7d'::bytea, 1, $3, $4, now())
                    """, connection);
                queue.Parameters.Add(new NpgsqlParameter { Value = playerId });
                queue.Parameters.Add(new NpgsqlParameter { Value = actionId });
                queue.Parameters.Add(new NpgsqlParameter { Value = status });
                queue.Parameters.Add(new NpgsqlParameter { Value = $"usage-{playerId:N}" });
                await queue.ExecuteNonQueryAsync();
            }

            await SeedAsync(nodeId, "online", "cultivation.draw-qi", "running");
            await SeedAsync(nodeId, "offline", "cultivation.draw-qi", "running");
            await SeedAsync(nodeId, "suspended", "cultivation.draw-qi", "running");
            await SeedAsync(nodeId, "online", "world.travel", "running");
            await SeedAsync(nodeId, "online", "cultivation.draw-qi", "pending");
            await SeedAsync(otherNode, "online", "cultivation.draw-qi", "running");
            await SeedAsync(nodeId, "online", "action.unmapped", "running");
        }

        // 节点级类别计数：修炼在线+离线挂机=2（suspended/pending/跨节点/未配表不计），赶路=1。
        var counts = await registry.UsageForAsync(nodeId, now, CancellationToken.None);
        Assert.Equal(2, counts[ActionKind.Cultivation]);
        Assert.Equal(1, counts[ActionKind.Travel]);
        Assert.Equal(2, counts.Count);

        // 展示与衰减共用同一份计数：建筑服务修炼类别 → 使用中 2/100，满效。
        var view = await registry.BuildingsForAsync(nodeId, now, CancellationToken.None);
        var usages = registry.UsagesFor(view.Buildings, counts, now.ToUnixTimeMilliseconds() / 1000d);
        var usage = Assert.Single(usages);
        Assert.Equal("building.test-hut", usage.BuildingId);
        Assert.Equal(2, usage.ActiveUsers);
        Assert.Equal(NodeBuildingRules.CrowdCapacity(1), usage.Capacity);
        Assert.Equal(1, usage.Efficiency);

        var effects = await registry.EffectsForAsync(nodeId, now, CancellationToken.None);
        Assert.Equal(0.05, effects.CultivationOutputRatio, 6);
    }

    /// <summary>测试配表：测试小屋 + 声明自带建筑的测试地图节点（节点 ID 由调用方注入以保证库内唯一）。</summary>
    private static GameDataCatalog CatalogWithPrebuiltBuilding(string nodeId) => GameDataCatalog.Parse(
    [
        System.Text.Encoding.UTF8.GetBytes("""
            [{"id":"building.test-hut","name":"测试小屋","group":"production","requiredRealmLevel":1,
              "materials":[{"itemId":"item.test-wood","quantity":5}],
              "modifiers":{"cultivationOutputRatio":0.05}}]
            """),
        System.Text.Encoding.UTF8.GetBytes($$"""
            [{"id":"map.test","name":"测试图","nodes":[{"id":"{{nodeId}}","name":"测试村","x":1,"y":1,
               "prebuiltBuildings":[{"buildingId":"building.test-hut","level":30}]}]}]
            """)
    ]);

    /// <summary>
    /// 节点自带建筑播种：首次装载即落座竣工态（Level=30、无工程）并参与效果聚合；
    /// 播种幂等——重复实例/外部已建行不重复落座，已有升级结果不被种子等级覆盖。
    /// </summary>
    [Fact]
    public async Task RegistrySeedsPrebuiltBuildingsIdempotently()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("节点自带建筑播种集成测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var repository = new PostgresNodeBuildingRepository(source);
        var nodeId = $"node.test-prebuilt-{Guid.NewGuid():N}";
        var catalog = CatalogWithPrebuiltBuilding(nodeId);
        var now = DateTimeOffset.UtcNow;

        var registry = new NodeBuildingRegistry(repository, catalog, NullLogger<NodeBuildingRegistry>.Instance);
        var snapshot = await registry.BuildingsForAsync(nodeId, now, CancellationToken.None);
        var built = Assert.Single(snapshot.Buildings);
        Assert.Equal("building.test-hut", built.BuildingId);
        Assert.Equal(30, built.Level);
        Assert.Equal(0, built.TargetLevel);
        // 竣工态立刻参与效果聚合：0.05 × 30 = 1.5。
        var effects = await registry.EffectsForAsync(nodeId, now, CancellationToken.None);
        Assert.Equal(1.5, effects.CultivationOutputRatio, 6);

        // 幂等：第二个注册表实例同库装载不重复建行。
        var second = new NodeBuildingRegistry(repository, catalog, NullLogger<NodeBuildingRegistry>.Instance);
        Assert.Single((await second.BuildingsForAsync(nodeId, now, CancellationToken.None)).Buildings);
        await using (var connection = await source.OpenConnectionAsync())
        {
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM game.node_buildings WHERE node_id = $1", connection);
            count.Parameters.Add(new NpgsqlParameter { Value = nodeId });
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        }

        // 已存在行现状权威：玩家已发起升级（TargetLevel=31），第三次装载采纳活行而非回落种子。
        Assert.True((await registry.StartUpgradeAsync(nodeId, "building.test-hut", now, CancellationToken.None)).Success);
        var third = new NodeBuildingRegistry(repository, catalog, NullLogger<NodeBuildingRegistry>.Instance);
        var adopted = Assert.Single((await third.BuildingsForAsync(nodeId, now, CancellationToken.None)).Buildings);
        Assert.Equal(30, adopted.Level);
        Assert.Equal(31, adopted.TargetLevel);
    }
}
