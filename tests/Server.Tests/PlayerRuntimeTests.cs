using System.Collections.Concurrent;
using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Domain.Simulation;
using Microsoft.Extensions.Logging.Abstractions;
using Protocol;
using Server.Clean;
using StoredHead = Domain.Persistence.PlayerStateHead;
using Xunit;

namespace Server.Tests;

/// <summary>
/// PlayerRuntime 装配门禁：命令经单写者串行执行、所有权租约失败关闭、快照按需推送、
/// 配表指纹重绑、经济事务与权威状态同提交/同回滚，以及 world.travel 移动指令插队语义。
/// </summary>
public sealed class PlayerRuntimeTests
{
    /// <summary>装配未学习技能的 loadout.replace 必须失败，不得反向写入习得列表（防刷技能）。</summary>
    [Fact]
    public async Task LoadoutCannotGrantUnlearnedAbilities()
    {
        var playerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var repository = new FakeStateRepository(new PlayerStateBundle(
            new PlayerIdentity(playerId, Guid.NewGuid(), "装配权限", now),
            new StoredHead(playerId, 0, 2, now, string.Empty, string.Empty, now),
            new Dictionary<string, StoredSection>(), []));
        var catalog = GameDataCatalog.Parse([System.Text.Encoding.UTF8.GetBytes("""
            [{"id":"ability.test","style":"sword","category":"physical","element":"none","damageRatio":1,"flatDamage":1,"cooldownMilliseconds":1000,"castMilliseconds":1000,"manaCost":0}]
            """)]);
        await using var runtime = new PlayerRuntime(repository, new EmptyAssetRepository(), new NoopEconomyRepository(), catalog,
            NullLogger<PlayerRuntime>.Instance);
        var result = await runtime.ExecuteAsync(playerId, "loadout.replace", JsonSerializer.SerializeToElement(new
        {
            equipment = Array.Empty<object>(), learnedAbilityIds = new[] { "ability.test" },
            abilitySlots = new[] { new { abilityId = "ability.test", level = 99, triggers = Array.Empty<object>() } }
        }));
        Assert.False(result.Success);
        Assert.Empty(result.Snapshot.State.LearnedAbilities);
    }
    /// <summary>命令经单写者串行执行：状态版本递增，脏分段与命令回执进入刷写队列。</summary>

    [Fact]
    public async Task NewPlayerCommandIsSerializedThroughSingleWriterAndFlushQueue()
    {
        var playerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var repository = new FakeStateRepository(new PlayerStateBundle(
            new PlayerIdentity(playerId, accountId, "单写者", DateTimeOffset.UtcNow),
            new StoredHead(playerId, 0, 1, DateTimeOffset.UtcNow, string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new Dictionary<string, StoredSection>(),
            []));
        var runtime = new PlayerRuntime(
            repository,
            new EmptyAssetRepository(),
            new NoopEconomyRepository(),
            GameDataCatalog.BuiltIn,
            NullLogger<PlayerRuntime>.Instance);

        var initial = await runtime.LoadAsync(playerId);
        var requestId = Guid.NewGuid().ToString("D");
        var payload = JsonSerializer.SerializeToElement(new ActionIntentRequest(requestId, "cultivation.draw-qi", 2), ProtocolJson.Options);
        var result = await runtime.ExecuteAsync(playerId, "action.enqueue", payload);

        Assert.True(result.Success);
        Assert.Single(result.Snapshot.State.Actions);
        Assert.True(result.Snapshot.StateVersion > initial.StateVersion);
        await runtime.FlushAllAsync(false);
        Assert.NotEmpty(repository.Enqueued);
        Assert.Single(repository.Receipts);
    }
    /// <summary>所有权租约续约失败时命令失败关闭并返回 PLAYER_OWNERSHIP_LOST，不得在他人所有权下继续结算。</summary>

    [Fact]
    public async Task LostPlayerOwnershipFailsClosed()
    {
        var playerId = Guid.NewGuid();
        var repository = new FakeStateRepository(new PlayerStateBundle(
            new PlayerIdentity(playerId, Guid.NewGuid(), "租约测试", DateTimeOffset.UtcNow),
            new StoredHead(playerId, 0, 1, DateTimeOffset.UtcNow, string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new Dictionary<string, StoredSection>(), []));
        var runtime = new PlayerRuntime(repository, new EmptyAssetRepository(), new NoopEconomyRepository(),
            GameDataCatalog.BuiltIn, NullLogger<PlayerRuntime>.Instance, ownership: new LostOwnershipStore());

        var result = await runtime.ExecuteAsync(playerId, "player.presence", JsonSerializer.SerializeToElement(new { presence = "online" }));

        Assert.False(result.Success);
        Assert.Equal("PLAYER_OWNERSHIP_LOST", result.Code);
    }
    /// <summary>非法presence必须返回明确错误，不能静默转入Offline并重置离线报告。</summary>
    [Fact]
    public async Task InvalidPresenceDoesNotChangePlayerState()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = new PlayerRuntime(CreateEmptyPlayerRepository(playerId, "在线边界"),
            new EmptyAssetRepository(), new NoopEconomyRepository(), GameDataCatalog.BuiltIn,
            NullLogger<PlayerRuntime>.Instance);
        var before = await runtime.LoadAsync(playerId);
        var result = await runtime.ExecuteAsync(playerId, "player.presence",
            JsonSerializer.SerializeToElement(new { presence = "unrecognized" }));
        Assert.False(result.Success);
        Assert.Equal(DomainErrorCode.InvalidArgument.ToString().ToUpperInvariant(), result.Code);
        Assert.Equal(before.StateVersion, result.Snapshot.StateVersion);
        Assert.Equal(before.State.Presence, result.Snapshot.State.Presence);
    }

    /// <summary>Actor 创建失败（如数据库瞬时故障）不得残留半初始化实例，下一条命令可重试成功。</summary>

    [Fact]
    public async Task FailedActorCreationIsRemovedAndRetriedOnNextCommand()
    {
        var playerId = Guid.NewGuid();
        var repository = new FlakyStateRepository(new PlayerStateBundle(
            new PlayerIdentity(playerId, Guid.NewGuid(), "重试测试", DateTimeOffset.UtcNow),
            new StoredHead(playerId, 0, 1, DateTimeOffset.UtcNow, string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new Dictionary<string, StoredSection>(), []));
        var runtime = new PlayerRuntime(repository, new EmptyAssetRepository(), new NoopEconomyRepository(),
            GameDataCatalog.BuiltIn, NullLogger<PlayerRuntime>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.LoadAsync(playerId).AsTask());
        Assert.Equal(0, runtime.LoadedCount);

        var snapshot = await runtime.LoadAsync(playerId);
        Assert.Equal(playerId, snapshot.PlayerId);
    }
    /// <summary>仅模拟产生实际变更时才推送 SnapshotUpdated，空推进不广播（降低无效流量）。</summary>

    [Fact]
    public async Task AdvanceAllAsync_EmitsSnapshotUpdatedOnlyWhenSimulationChanges()
    {
        var playerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var repository = new FakeStateRepository(new PlayerStateBundle(
            new PlayerIdentity(playerId, accountId, "模拟推进测试", DateTimeOffset.UtcNow),
            new StoredHead(playerId, 0, 1, DateTimeOffset.UtcNow, string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new Dictionary<string, StoredSection>(),
            []));
        var runtime = new PlayerRuntime(
            repository,
            new EmptyAssetRepository(),
            new NoopEconomyRepository(),
            GameDataCatalog.BuiltIn,
            NullLogger<PlayerRuntime>.Instance);

        var pushedSnapshots = new List<PlayerRuntimeSnapshot>();
        runtime.SnapshotUpdated += snapshot =>
        {
            pushedSnapshots.Add(snapshot);
            return ValueTask.CompletedTask;
        };

        // 1. 无行动排队时推进 -> 不触发 SnapshotUpdated
        await runtime.LoadAsync(playerId);
        await runtime.AdvanceAllAsync(DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Empty(pushedSnapshots);

        // 2. 排队行动后推进 -> 触发 SnapshotUpdated 且仅包含该玩家
        var payload = JsonSerializer.SerializeToElement(new ActionIntentRequest(Guid.NewGuid().ToString("D"), "cultivation.draw-qi", 5), ProtocolJson.Options);
        await runtime.ExecuteAsync(playerId, "action.enqueue", payload);

        await runtime.AdvanceAllAsync(DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Single(pushedSnapshots);
        Assert.Equal(playerId, pushedSnapshots[0].PlayerId);
    }
    /// <summary>存储指纹与当前配表不一致时重绑新指纹并刷写分段，而非拒绝加载存档。</summary>

    [Fact]
    public async Task CatalogFingerprintChange_RebindsStoredStateInsteadOfRejectingSession()
    {
        const string storedFingerprint = "catalog-old";
        const string currentFingerprint = "catalog-current";
        var playerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var persisted = PlayerAggregate.Create(
            new PlayerId(playerId),
            new AccountId(accountId),
            "配表切换测试",
            createdAt,
            storedFingerprint);
        persisted.SetPresence(PresenceState.Offline);
        var storedAt = DateTimeOffset.UtcNow;
        var writes = Domain.Persistence.SectionCodec.EncodeAggregate(persisted, storedAt, persisted.StateVersion);
        var sections = writes.ToDictionary(
            write => write.SectionKind,
            write => new StoredSection(
                write.PlayerId,
                write.SectionKind,
                write.StateVersion,
                write.SchemaVersion,
                write.Codec,
                write.Payload.ToArray(),
                write.RawBytes,
                write.Checksum,
                write.UpdatedAt),
            StringComparer.Ordinal);
        var repository = new FakeStateRepository(new PlayerStateBundle(
            new PlayerIdentity(playerId, accountId, "配表切换测试", createdAt),
            new StoredHead(
                playerId,
                persisted.StateVersion,
                Domain.Persistence.SectionCodec.CurrentSchemaVersion,
                persisted.LastSimulatedAt,
                storedFingerprint,
                Domain.Persistence.SectionCodec.ComputeHeadChecksum(sections.Values),
                storedAt),
            sections,
            []));
        var runtime = new PlayerRuntime(
            repository,
            new EmptyAssetRepository(),
            new NoopEconomyRepository(),
            new GameDataCatalog("test-2", currentFingerprint, ActionCatalog.Default),
            NullLogger<PlayerRuntime>.Instance);

        var snapshot = await runtime.LoadAsync(playerId);
        await runtime.FlushAllAsync(true);

        Assert.Equal(currentFingerprint, snapshot.State.GameDataFingerprint);
        Assert.True(snapshot.StateVersion > persisted.StateVersion);
        Assert.Contains(repository.Enqueued, write =>
            write.SectionKind == PlayerSectionKinds.Settings
            && write.GameDataFingerprint == currentFingerprint);
    }
    /// <summary>模拟推进中自动用药扣件与权威状态同事务提交：资产流水、快照余额与剩余时长三者一致。</summary>

    [Fact]
    public async Task SimulationAdvanceCommitsSustainedPillDebitWithAuthoritativeState()
    {
        using var telemetry = new BenchmarkTelemetry();
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "持久事务");
        var assets = new MutableAssetRepository(new AssetBalanceRecord(playerId, "item.qi-gathering-pill", "", 1d, 0));
        var economy = new RecordingEconomyRepository(assets, apply: true);
        var transactionIdentity = new DeterministicSimulationTransactionIdentity();
        var runtime = new PlayerRuntime(repository, assets, economy, GameDataCatalog.LoadEmbedded(), NullLogger<PlayerRuntime>.Instance,
            transactionIdentity: transactionIdentity);

        await runtime.LoadAsync(playerId);
        await runtime.ExecuteAsync(playerId, "queue.auto_use.configure", JsonSerializer.SerializeToElement(new
        {
            actionType = "cultivation",
            slotIndex = 0,
            itemId = "item.qi-gathering-pill",
            quantity = 1,
            enabled = true
        }));
        var enqueued = await runtime.ExecuteAsync(playerId, "action.enqueue", JsonSerializer.SerializeToElement(
            new ActionIntentRequest(Guid.NewGuid().ToString("D"), "cultivation.draw-qi", -1), ProtocolJson.Options));

        var result = await runtime.ExecuteAsync(playerId, "simulation.advance", JsonSerializer.SerializeToElement(new
        {
            targetTime = enqueued.Snapshot.LastSimulatedAt.AddSeconds(10)
        }));

        Assert.True(result.Success);
        Assert.Equal(13d, result.Snapshot.State.Progression.Qi, 10);
        Assert.Equal(13d, result.Snapshot.State.Progression.Experience, 10);
        Assert.Equal(290d, Assert.Single(result.Snapshot.State.Settings.AutoUseSlots!).RemainingDurationSeconds);
        Assert.Equal(0d, Assert.Single(result.Snapshot.Assets).Quantity);
        var transaction = Assert.Single(economy.Requests);
        // 确定性回放身份必须穿过运行时与Actor；提交后余额版本仍来自经济仓储。
        Assert.Equal(transactionIdentity.Create(transaction.IdempotencyKey), transaction.TransactionId);
        Assert.Equal(1, Assert.Single(result.Snapshot.Assets).Version);
        var observed = telemetry.Snapshot();
        foreach (var name in new[] { "core.simulation|other", "snapshot.export|all", "queue.actor_wait|all" })
        {
            var measured = observed.Measurements[name];
            Assert.True(measured.Count > 0);
            Assert.Equal(measured.Count, measured.Buckets.Sum());
            Assert.True(measured.TotalMs >= 0 && double.IsFinite(measured.TotalMs));
        }
        Assert.True(observed.Counters["lifecycle.player_loaded"] > 0);
        var debit = Assert.Single(transaction.Deltas);
        Assert.Equal("item.qi-gathering-pill", debit.AssetId);
        Assert.Equal(-1d, debit.Delta);
    }
    /// <summary>经济事务提交失败时内存侧整体回滚：丹药时长、修为经验、行动进度与资产全部还原，不留半提交状态。</summary>

    [Fact]
    public async Task FailedEconomyCommitRollsBackPillDurationAndCultivationProgress()
    {
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "回滚事务");
        var assets = new MutableAssetRepository(new AssetBalanceRecord(playerId, "item.qi-gathering-pill", "", 1d, 0));
        var economy = new RecordingEconomyRepository(assets, apply: false);
        var runtime = new PlayerRuntime(repository, assets, economy, GameDataCatalog.LoadEmbedded(), NullLogger<PlayerRuntime>.Instance);

        await runtime.LoadAsync(playerId);
        await runtime.ExecuteAsync(playerId, "queue.auto_use.configure", JsonSerializer.SerializeToElement(new
        {
            actionType = "cultivation",
            slotIndex = 0,
            itemId = "item.qi-gathering-pill",
            quantity = 1,
            enabled = true
        }));
        var before = await runtime.ExecuteAsync(playerId, "action.enqueue", JsonSerializer.SerializeToElement(
            new ActionIntentRequest(Guid.NewGuid().ToString("D"), "cultivation.draw-qi", -1), ProtocolJson.Options));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(
            playerId,
            "simulation.advance",
            JsonSerializer.SerializeToElement(new { targetTime = before.Snapshot.LastSimulatedAt.AddSeconds(10) })).AsTask());
        var after = await runtime.LoadAsync(playerId);

        Assert.Equal(before.Snapshot.LastSimulatedAt, after.LastSimulatedAt);
        Assert.Equal(before.Snapshot.State.Progression.Qi, after.State.Progression.Qi);
        Assert.Equal(before.Snapshot.State.Progression.Experience, after.State.Progression.Experience);
        Assert.Equal(0d, Assert.Single(after.State.Settings.AutoUseSlots!).RemainingDurationSeconds);
        Assert.Equal(0d, Assert.Single(after.State.Actions).ProgressSeconds);
        Assert.Equal(1d, Assert.Single(after.Assets).Quantity);
        Assert.Single(economy.Requests);
    }
    /// <summary>推进事务失败按分段检查点回滚：聚合导出逐位一致、操作前已脏分段保留、本次新标脏不泄漏。</summary>

    [Fact]
    public async Task FailedAdvanceRestoresAggregateBitwiseAndKeepsPriorDirtySections()
    {
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "逐位回滚");
        var assets = new MutableAssetRepository(new AssetBalanceRecord(playerId, "item.qi-gathering-pill", "", 1d, 0));
        // 经济提交恒失败：推进中持续药激活扣件触发的事务拒绝提交，驱动回滚路径。
        var economy = new RecordingEconomyRepository(assets, apply: false);
        var runtime = new PlayerRuntime(repository, assets, economy, GameDataCatalog.LoadEmbedded(), NullLogger<PlayerRuntime>.Instance);

        await runtime.LoadAsync(playerId);
        await runtime.ExecuteAsync(playerId, "queue.auto_use.configure", JsonSerializer.SerializeToElement(new
        {
            actionType = "cultivation",
            slotIndex = 0,
            itemId = "item.qi-gathering-pill",
            quantity = 1,
            enabled = true
        }));
        var enqueued = await runtime.ExecuteAsync(playerId, "action.enqueue", JsonSerializer.SerializeToElement(
            new ActionIntentRequest(Guid.NewGuid().ToString("D"), "cultivation.draw-qi", -1), ProtocolJson.Options));
        Assert.True(enqueued.Success);

        // 强刷清空全部脏分段，再让 ActionQueue 重新标脏——构造「部分分段已脏」的混合基线。
        await runtime.FlushAllAsync(true);
        var flushedCount = repository.Enqueued.Count;
        var enqueue2 = await runtime.ExecuteAsync(playerId, "action.enqueue", JsonSerializer.SerializeToElement(
            new ActionIntentRequest(Guid.NewGuid().ToString("D"), "cultivation.draw-qi", 2), ProtocolJson.Options));
        Assert.True(enqueue2.Success);
        var baselineJson = JsonSerializer.Serialize(enqueue2.Snapshot.State);
        var baselineAssets = enqueue2.Snapshot.Assets
            .Select(asset => (asset.AssetId, asset.Variant, asset.Quantity, asset.Version)).ToArray();

        // 推进 10s：行动启动时激活持续丹药产生扣件增量，经济提交失败触发整体回滚。
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(
            playerId,
            "simulation.advance",
            JsonSerializer.SerializeToElement(new { targetTime = enqueue2.Snapshot.LastSimulatedAt.AddSeconds(10) })).AsTask());
        var after = await runtime.LoadAsync(playerId);

        // 逐位一致：聚合导出快照与资产余额与操作前完全一致（含模拟时钟、行动进度、版本号）。
        Assert.Equal(baselineJson, JsonSerializer.Serialize(after.State));
        Assert.Equal(baselineAssets, after.Assets.Select(a => (a.AssetId, a.Variant, a.Quantity, a.Version)).ToArray());

        // 脏集合精确复原：失败推进新标脏的分段不得泄漏，操作前已脏的 ActionQueue 必须保留。
        await runtime.FlushAllAsync(true);
        var kinds = repository.Enqueued.Skip(flushedCount).Select(write => write.SectionKind).ToArray();
        Assert.Equal([PlayerSectionKinds.ActionQueue], kinds);
    }
    /// <summary>功法学习事务失败按分段检查点回滚：EncodeAggregate 已消费的脏集合精确复原，习得结果不留痕。</summary>

    [Fact]
    public async Task FailedAbilityLearnRestoresAggregateAndDirtyTracking()
    {
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "学功回滚");
        var assets = new MutableAssetRepository(new AssetBalanceRecord(playerId, "item.tome.test-strike", "", 1d, 0));
        var economy = new RecordingEconomyRepository(assets, apply: false);
        var catalog = GameDataCatalog.Parse([
            System.Text.Encoding.UTF8.GetBytes("""[{"id":"item.tome.test-strike","name":"测试秘籍"}]"""),
            System.Text.Encoding.UTF8.GetBytes("""[{"id":"ability.test-strike","style":"sword","category":"physical","element":"none","damageRatio":1,"flatDamage":1,"cooldownMilliseconds":1000,"castMilliseconds":1000,"manaCost":0}]""")
        ]);
        var runtime = new PlayerRuntime(repository, assets, economy, catalog, NullLogger<PlayerRuntime>.Instance);
        await runtime.LoadAsync(playerId);

        // 清盘构造基线：ActionQueue 保持已脏，其余分段干净。
        await runtime.ExecuteAsync(playerId, "action.enqueue", JsonSerializer.SerializeToElement(
            new ActionIntentRequest(Guid.NewGuid().ToString("D"), "cultivation.draw-qi", -1), ProtocolJson.Options));
        await runtime.FlushAllAsync(true);
        var flushedCount = repository.Enqueued.Count;
        var enqueued2 = await runtime.ExecuteAsync(playerId, "action.enqueue", JsonSerializer.SerializeToElement(
            new ActionIntentRequest(Guid.NewGuid().ToString("D"), "cultivation.draw-qi", 1), ProtocolJson.Options));
        var baselineJson = JsonSerializer.Serialize(enqueued2.Snapshot.State);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(
            playerId,
            "ability.learn",
            JsonSerializer.SerializeToElement(new
            {
                itemId = "item.tome.test-strike",
                requestId = Guid.NewGuid().ToString("D")
            })).AsTask());
        var after = await runtime.LoadAsync(playerId);
        // 逐位一致：秘籍未习得、秘籍仍在钱包、行动队列与模拟时钟与操作前完全相同。
        Assert.Equal(baselineJson, JsonSerializer.Serialize(after.State));
        Assert.Empty(after.State.LearnedAbilities);
        Assert.Equal(1d, Assert.Single(after.Assets, a => a.AssetId == "item.tome.test-strike").Quantity);
        Assert.Single(economy.Requests);
        // 脏集合精确复原：Loadout 等本次新标脏分段不泄漏，操作前已脏的 ActionQueue 保留待刷。
        await runtime.FlushAllAsync(true);
        var kinds = repository.Enqueued.Skip(flushedCount).Select(write => write.SectionKind).ToArray();
        Assert.Equal([PlayerSectionKinds.ActionQueue], kinds);
    }
    /// <summary>world.travel 指令插队队首并挂起当前行动（保留进度）；取消移动后原行动恢复 Running。</summary>

    [Fact]
    public async Task WorldTravelCommandEnqueuesPriorityActionAndSuspendsCurrent()
    {
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "移动测试");
        var assets = new MutableAssetRepository();
        var economy = new RecordingEconomyRepository(assets, apply: true);
        var catalog = GameDataCatalog.LoadEmbedded();
        var runtime = new PlayerRuntime(repository, assets, economy, catalog, NullLogger<PlayerRuntime>.Instance);

        await runtime.LoadAsync(playerId);

        // 1. 先入队一个正在进行的修炼行动
        var enqueueResult = await runtime.ExecuteAsync(playerId, "action.enqueue", JsonSerializer.SerializeToElement(
            new ActionIntentRequest(Guid.NewGuid().ToString("D"), "cultivation.draw-qi", -1), ProtocolJson.Options));
        Assert.True(enqueueResult.Success);
        Assert.Single(enqueueResult.Snapshot.State.Actions);

        // 2. 模拟推进 3 秒，确保修炼处于运行状态且有一定进度
        var advanceResult = await runtime.ExecuteAsync(playerId, "simulation.advance", JsonSerializer.SerializeToElement(new
        {
            targetTime = enqueueResult.Snapshot.LastSimulatedAt.AddSeconds(3)
        }));
        Assert.True(advanceResult.Success);
        Assert.Equal(3d, advanceResult.Snapshot.State.Actions[0].ProgressSeconds, 5);

        // 3. 发送 world.travel 移动指令，目标为青云山门
        var travelResult = await runtime.ExecuteAsync(playerId, "world.travel", JsonSerializer.SerializeToElement(new
        {
            targetNodeId = "node.cloud-gate"
        }));
        Assert.True(travelResult.Success);

        // 验证队列：移动插在首位，原修炼被挂起
        var actions = travelResult.Snapshot.State.Actions;
        Assert.Equal(2, actions.Count);
        Assert.Equal("world.travel", actions[0].Intent.ActionId.Value);
        Assert.NotNull(actions[0].Travel);
        Assert.Equal("node.cloud-gate", actions[0].Travel!.TargetNodeId);
        Assert.NotEmpty(actions[0].Travel!.Segments);

        Assert.Equal("cultivation.draw-qi", actions[1].Intent.ActionId.Value);
        Assert.Equal(3d, actions[1].ProgressSeconds, 5);

        // 4. 取消移动行动 (action.remove)
        var removeResult = await runtime.ExecuteAsync(playerId, "action.remove", JsonSerializer.SerializeToElement(new
        {
            actionInstanceId = actions[0].Id
        }));
        Assert.True(removeResult.Success);
        Assert.Single(removeResult.Snapshot.State.Actions);
        Assert.Equal("cultivation.draw-qi", removeResult.Snapshot.State.Actions[0].Intent.ActionId.Value);
        Assert.Equal(ActionQueueStatus.Running, removeResult.Snapshot.State.Actions[0].Status);
    }
    /// <summary>静止时点选当前所在节点不产生移动行动；行走半途点选出发地自动规划折返路线，到达后恢复修炼。</summary>

    [Fact]
    public async Task WorldTravelToCurrentNodeAtRestDoesNotAddActionAndMidTravelReturnsToOrigin()
    {
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "原地测试");
        var assets = new MutableAssetRepository();
        var economy = new RecordingEconomyRepository(assets, apply: true);
        var catalog = GameDataCatalog.LoadEmbedded();
        var runtime = new PlayerRuntime(repository, assets, economy, catalog, NullLogger<PlayerRuntime>.Instance);

        await runtime.LoadAsync(playerId);

        // 1. 入队修炼
        await runtime.ExecuteAsync(playerId, "action.enqueue", JsonSerializer.SerializeToElement(
            new ActionIntentRequest(Guid.NewGuid().ToString("D"), "cultivation.draw-qi", -1), ProtocolJson.Options));

        // 2. 角色原本静止在云溪村，点击当前节点云溪村，不应加入移动行动，保持单项修炼
        var stayResult = await runtime.ExecuteAsync(playerId, "world.travel", JsonSerializer.SerializeToElement(new
        {
            targetNodeId = "node.yunxi-village"
        }));
        Assert.True(stayResult.Success);
        Assert.Single(stayResult.Snapshot.State.Actions);
        Assert.Equal("cultivation.draw-qi", stayResult.Snapshot.State.Actions[0].Intent.ActionId.Value);
        Assert.Equal(ActionQueueStatus.Running, stayResult.Snapshot.State.Actions[0].Status);

        // 3. 发起移动前往青云山门
        var travelResult = await runtime.ExecuteAsync(playerId, "world.travel", JsonSerializer.SerializeToElement(new
        {
            targetNodeId = "node.cloud-gate"
        }));
        Assert.True(travelResult.Success);
        Assert.Equal(2, travelResult.Snapshot.State.Actions.Count);
        Assert.Equal("world.travel", travelResult.Snapshot.State.Actions[0].Intent.ActionId.Value);

        // 4. 行走半途点击出发地云溪村，应当规划折返路线前往云溪村
        var returnResult = await runtime.ExecuteAsync(playerId, "world.travel", JsonSerializer.SerializeToElement(new
        {
            targetNodeId = "node.yunxi-village"
        }));
        Assert.True(returnResult.Success);
        Assert.Equal(2, returnResult.Snapshot.State.Actions.Count);
        Assert.Equal("world.travel", returnResult.Snapshot.State.Actions[0].Intent.ActionId.Value);
        Assert.Equal("node.yunxi-village", returnResult.Snapshot.State.Actions[0].Travel!.TargetNodeId);

        // 5. 模拟推进完成折返，抵达云溪村后自动出队并恢复修炼
        var advanceResult = await runtime.ExecuteAsync(playerId, "simulation.advance", JsonSerializer.SerializeToElement(new
        {
            targetTime = returnResult.Snapshot.LastSimulatedAt.AddSeconds(10)
        }));
        Assert.True(advanceResult.Success);
        Assert.Equal("node.yunxi-village", advanceResult.Snapshot.State.World.NodeId);
        Assert.Null(advanceResult.Snapshot.State.World.EdgeId);
        Assert.Single(advanceResult.Snapshot.State.Actions);
        Assert.Equal("cultivation.draw-qi", advanceResult.Snapshot.State.Actions[0].Intent.ActionId.Value);
        Assert.Equal(ActionQueueStatus.Running, advanceResult.Snapshot.State.Actions[0].Status);
    }

    /// <summary>item.discard：按指定数量原子扣减物品，事务增量与快照余额一致。</summary>

    [Fact]
    public async Task DiscardItemDeductsQuantityAtomically()
    {
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "丢弃测试");
        var assets = new MutableAssetRepository(new AssetBalanceRecord(playerId, "item.test-herb", "", 10d, 0));
        var economy = new RecordingEconomyRepository(assets, apply: true);
        var runtime = new PlayerRuntime(repository, assets, economy, CreateDisposalCatalog(), NullLogger<PlayerRuntime>.Instance);
        await runtime.LoadAsync(playerId);

        var result = await runtime.ExecuteAsync(playerId, "item.discard", JsonSerializer.SerializeToElement(new
        {
            itemId = "item.test-herb",
            quantity = 4d,
            requestId = Guid.NewGuid().ToString("D")
        }));

        Assert.True(result.Success);
        var transaction = Assert.Single(economy.Requests);
        var debit = Assert.Single(transaction.Deltas);
        Assert.Equal("item.test-herb", debit.AssetId);
        Assert.Equal(-4d, debit.Delta);
        Assert.Equal(6d, Assert.Single(result.Snapshot.Assets).Quantity);
    }
    /// <summary>item.discard：数量超库存、物品不存在与非法数量均被拒绝，且不产生任何资产事务。</summary>

    [Fact]
    public async Task DiscardItemRejectsOvershootMissingAndInvalidQuantity()
    {
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "丢弃校验");
        var assets = new MutableAssetRepository(new AssetBalanceRecord(playerId, "item.test-herb", "", 10d, 0));
        var economy = new RecordingEconomyRepository(assets, apply: true);
        var runtime = new PlayerRuntime(repository, assets, economy, CreateDisposalCatalog(), NullLogger<PlayerRuntime>.Instance);
        await runtime.LoadAsync(playerId);

        var overshoot = await runtime.ExecuteAsync(playerId, "item.discard", JsonSerializer.SerializeToElement(new
        {
            itemId = "item.test-herb",
            quantity = 20d,
            requestId = Guid.NewGuid().ToString("D")
        }));
        Assert.False(overshoot.Success);
        Assert.Equal("INSUFFICIENT_ITEM", overshoot.Code);

        var missing = await runtime.ExecuteAsync(playerId, "item.discard", JsonSerializer.SerializeToElement(new
        {
            itemId = "item.missing",
            quantity = 1d,
            requestId = Guid.NewGuid().ToString("D")
        }));
        Assert.False(missing.Success);
        Assert.Equal("ITEM_NOT_FOUND", missing.Code);

        var invalid = await runtime.ExecuteAsync(playerId, "item.discard", JsonSerializer.SerializeToElement(new
        {
            itemId = "item.test-herb",
            quantity = 0d,
            requestId = Guid.NewGuid().ToString("D")
        }));
        Assert.False(invalid.Success);
        Assert.Equal("INVALID_PAYLOAD", invalid.Code);

        Assert.Empty(economy.Requests);
        Assert.Equal(10d, Assert.Single((await runtime.LoadAsync(playerId)).Assets).Quantity);
    }
    /// <summary>item.sell：按 sellPrice 单价（double）扣物品并在同一事务内贷记灵石。</summary>

    [Fact]
    public async Task SellItemCreditsSpiritStonesInSameTransaction()
    {
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "回收测试");
        var assets = new MutableAssetRepository(new AssetBalanceRecord(playerId, "item.test-herb", "", 10d, 0));
        var economy = new RecordingEconomyRepository(assets, apply: true);
        var runtime = new PlayerRuntime(repository, assets, economy, CreateDisposalCatalog(), NullLogger<PlayerRuntime>.Instance);
        await runtime.LoadAsync(playerId);

        var result = await runtime.ExecuteAsync(playerId, "item.sell", JsonSerializer.SerializeToElement(new
        {
            itemId = "item.test-herb",
            quantity = 4d,
            requestId = Guid.NewGuid().ToString("D")
        }));

        Assert.True(result.Success);
        var transaction = Assert.Single(economy.Requests);
        Assert.Equal(2, transaction.Deltas.Count);
        var debit = Assert.Single(transaction.Deltas, delta => delta.AssetId == "item.test-herb");
        var credit = Assert.Single(transaction.Deltas, delta => delta.AssetId == "currency.spirit-stone");
        Assert.Equal(-4d, debit.Delta);
        Assert.Equal(10d, credit.Delta);
        Assert.Equal(6d, Assert.Single(result.Snapshot.Assets, asset => asset.AssetId == "item.test-herb").Quantity);
        Assert.Equal(10d, Assert.Single(result.Snapshot.Assets, asset => asset.AssetId == "currency.spirit-stone").Quantity);
    }
    /// <summary>item.sell：无回收价物品与装备均被拒绝；缺少 requestId 直接拒绝；货币资产不在物品目录按不存在拒绝。</summary>

    [Fact]
    public async Task SellItemRejectsUnsellableEquipmentCurrencyAndMissingRequestId()
    {
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "回收校验");
        var assets = new MutableAssetRepository(
            new AssetBalanceRecord(playerId, "item.no-price", "", 5d, 0),
            new AssetBalanceRecord(playerId, "equipment.test-sword", "", 1d, 0),
            new AssetBalanceRecord(playerId, "currency.spirit-stone", "", 3d, 0));
        var economy = new RecordingEconomyRepository(assets, apply: true);
        var runtime = new PlayerRuntime(repository, assets, economy, CreateDisposalCatalog(), NullLogger<PlayerRuntime>.Instance);
        await runtime.LoadAsync(playerId);

        var noPrice = await runtime.ExecuteAsync(playerId, "item.sell", JsonSerializer.SerializeToElement(new
        {
            itemId = "item.no-price",
            quantity = 1d,
            requestId = Guid.NewGuid().ToString("D")
        }));
        Assert.False(noPrice.Success);
        Assert.Equal("ITEM_NOT_SELLABLE", noPrice.Code);

        var equipment = await runtime.ExecuteAsync(playerId, "item.sell", JsonSerializer.SerializeToElement(new
        {
            itemId = "equipment.test-sword",
            quantity = 1d,
            requestId = Guid.NewGuid().ToString("D")
        }));
        Assert.False(equipment.Success);
        Assert.Equal("ITEM_NOT_SELLABLE", equipment.Code);

        var currency = await runtime.ExecuteAsync(playerId, "item.sell", JsonSerializer.SerializeToElement(new
        {
            itemId = "currency.spirit-stone",
            quantity = 1d,
            requestId = Guid.NewGuid().ToString("D")
        }));
        Assert.False(currency.Success);
        Assert.Equal("ITEM_NOT_FOUND", currency.Code);

        var noRequestId = await runtime.ExecuteAsync(playerId, "item.sell", JsonSerializer.SerializeToElement(new
        {
            itemId = "item.no-price",
            quantity = 1d
        }));
        Assert.False(noRequestId.Success);
        Assert.Equal("INVALID_REQUEST_ID", noRequestId.Code);

        Assert.Empty(economy.Requests);
    }
    /// <summary>item.sell 同 requestId 重放返回 IDEMPOTENT_REPLAY，不重复扣物品或重复入账灵石。</summary>

    [Fact]
    public async Task SellItemIdempotentReplayDoesNotDoubleApply()
    {
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "幂等测试");
        var assets = new MutableAssetRepository(new AssetBalanceRecord(playerId, "item.test-herb", "", 10d, 0));
        var economy = new RecordingEconomyRepository(assets, apply: true);
        var runtime = new PlayerRuntime(repository, assets, economy, CreateDisposalCatalog(), NullLogger<PlayerRuntime>.Instance);
        await runtime.LoadAsync(playerId);

        var payload = JsonSerializer.SerializeToElement(new
        {
            itemId = "item.test-herb",
            quantity = 4d,
            requestId = Guid.NewGuid().ToString("D")
        });
        var first = await runtime.ExecuteAsync(playerId, "item.sell", payload);
        var replay = await runtime.ExecuteAsync(playerId, "item.sell", payload);

        Assert.True(first.Success);
        Assert.True(replay.Success);
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Code);
        Assert.Single(economy.Requests);
        Assert.Equal(6d, Assert.Single(replay.Snapshot.Assets, asset => asset.AssetId == "item.test-herb").Quantity);
        Assert.Equal(10d, Assert.Single(replay.Snapshot.Assets, asset => asset.AssetId == "currency.spirit-stone").Quantity);
    }
    /// <summary>已穿戴装备不可丢弃或回收，必须先卸下。</summary>

    [Fact]
    public async Task EquippedItemCannotBeDiscardedOrSold()
    {
        var playerId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var persisted = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(Guid.NewGuid()), "装备处置", createdAt, "fp-test");
        persisted.ReplaceLoadout([], [], [new ItemId("equipment.test-sword")]);
        var storedAt = DateTimeOffset.UtcNow;
        var writes = Domain.Persistence.SectionCodec.EncodeAggregate(persisted, storedAt, persisted.StateVersion);
        var sections = writes.ToDictionary(
            write => write.SectionKind,
            write => new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion, write.SchemaVersion,
                write.Codec, write.Payload.ToArray(), write.RawBytes, write.Checksum, write.UpdatedAt),
            StringComparer.Ordinal);
        var repository = new FakeStateRepository(new PlayerStateBundle(
            new PlayerIdentity(playerId, Guid.NewGuid(), "装备处置", createdAt),
            new StoredHead(playerId, persisted.StateVersion, Domain.Persistence.SectionCodec.CurrentSchemaVersion, persisted.LastSimulatedAt,
                "fp-test", Domain.Persistence.SectionCodec.ComputeHeadChecksum(sections.Values), storedAt),
            sections, []));
        var assets = new MutableAssetRepository(new AssetBalanceRecord(playerId, "equipment.test-sword", "", 1d, 0));
        var economy = new RecordingEconomyRepository(assets, apply: true);
        var runtime = new PlayerRuntime(repository, assets, economy, CreateDisposalCatalog(), NullLogger<PlayerRuntime>.Instance);
        await runtime.LoadAsync(playerId);

        var discard = await runtime.ExecuteAsync(playerId, "item.discard", JsonSerializer.SerializeToElement(new
        {
            itemId = "equipment.test-sword",
            quantity = 1d,
            requestId = Guid.NewGuid().ToString("D")
        }));
        Assert.False(discard.Success);
        Assert.Equal("ITEM_EQUIPPED", discard.Code);

        var sell = await runtime.ExecuteAsync(playerId, "item.sell", JsonSerializer.SerializeToElement(new
        {
            itemId = "equipment.test-sword",
            quantity = 1d,
            requestId = Guid.NewGuid().ToString("D")
        }));
        Assert.False(sell.Success);
        Assert.Equal("ITEM_EQUIPPED", sell.Code);
        Assert.Empty(economy.Requests);
    }

    /// <summary>loadout.replace 槽位类别互斥：内功位只收心法内功、术法位只收神通术法，双向拒绝跨类装配。</summary>

    [Fact]
    public async Task LoadoutReplaceEnforcesInternalSpellSlotDiscipline()
    {
        var playerId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var persisted = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(Guid.NewGuid()), "槽位类别", createdAt, "fp-test");
        persisted.LearnAbility(new AbilityId("ability.test-heart"));
        persisted.LearnAbility(new AbilityId("ability.test-strike"));
        var storedAt = DateTimeOffset.UtcNow;
        var writes = Domain.Persistence.SectionCodec.EncodeAggregate(persisted, storedAt, persisted.StateVersion);
        var sections = writes.ToDictionary(
            write => write.SectionKind,
            write => new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion, write.SchemaVersion,
                write.Codec, write.Payload.ToArray(), write.RawBytes, write.Checksum, write.UpdatedAt),
            StringComparer.Ordinal);
        var repository = new FakeStateRepository(new PlayerStateBundle(
            new PlayerIdentity(playerId, Guid.NewGuid(), "槽位类别", createdAt),
            new StoredHead(playerId, persisted.StateVersion, Domain.Persistence.SectionCodec.CurrentSchemaVersion, persisted.LastSimulatedAt,
                "fp-test", Domain.Persistence.SectionCodec.ComputeHeadChecksum(sections.Values), storedAt),
            sections, []));
        var catalog = GameDataCatalog.Parse([System.Text.Encoding.UTF8.GetBytes("""
            [
              {"id":"ability.test-heart","name":"测试心法","category":"spell","element":"none","discipline":"internal","castMilliseconds":1000,"cooldownMilliseconds":5000,"manaCost":5,"effectType":"heal","healingRatio":0.5,"flatHealing":10,"targetType":"self"},
              {"id":"ability.test-strike","name":"测试术法","category":"spell","element":"fire","discipline":"spell","castMilliseconds":1000,"cooldownMilliseconds":5000,"manaCost":5,"damageRatio":1.0,"flatDamage":10}
            ]
            """)]);
        var runtime = new PlayerRuntime(repository, new EmptyAssetRepository(), new NoopEconomyRepository(),
            catalog, NullLogger<PlayerRuntime>.Instance);
        await runtime.LoadAsync(playerId);

        // 术法进内功位 -> 拒绝
        var spellInInternal = await runtime.ExecuteAsync(playerId, "loadout.replace", JsonSerializer.SerializeToElement(new
        {
            equipment = Array.Empty<object>(),
            abilitySlots = new object[]
            {
                new { abilityId = "ability.test-strike", level = 1d, triggers = Array.Empty<object>() },
                new { abilityId = (string?)null, level = 1d, triggers = Array.Empty<object>() }
            }
        }));
        Assert.False(spellInInternal.Success);
        Assert.Equal("INVALIDARGUMENT", spellInInternal.Code);

        // 内功进术法位 -> 拒绝
        var internalInSpell = await runtime.ExecuteAsync(playerId, "loadout.replace", JsonSerializer.SerializeToElement(new
        {
            equipment = Array.Empty<object>(),
            abilitySlots = new object[]
            {
                new { abilityId = (string?)null, level = 1d, triggers = Array.Empty<object>() },
                new { abilityId = "ability.test-heart", level = 1d, triggers = Array.Empty<object>() }
            }
        }));
        Assert.False(internalInSpell.Success);
        Assert.Equal("INVALIDARGUMENT", internalInSpell.Code);

        // 正确归位 -> 通过
        var correct = await runtime.ExecuteAsync(playerId, "loadout.replace", JsonSerializer.SerializeToElement(new
        {
            equipment = Array.Empty<object>(),
            abilitySlots = new object[]
            {
                new { abilityId = "ability.test-heart", level = 1d, triggers = Array.Empty<object>() },
                new { abilityId = "ability.test-strike", level = 1d, triggers = Array.Empty<object>() }
            }
        }));
        Assert.True(correct.Success);
        Assert.Equal("ability.test-heart", correct.Snapshot.State.Loadout.AbilitySlots[0].AbilityId);
        Assert.Equal("ability.test-strike", correct.Snapshot.State.Loadout.AbilitySlots[1].AbilityId);
    }

    /// <summary>租约续期去抖：距上次成功续租不足租约 1/3（45s 租约 ≈15s）时，命令、刷盘与模拟推进统一跳过 RenewAsync UPDATE。</summary>

    [Fact]
    public async Task LeaseRenewsAreDebouncedWithinThirdOfLeaseWindow()
    {
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "续租去抖");
        var ownership = new CountingOwnershipStore();
        var runtime = new PlayerRuntime(repository, new EmptyAssetRepository(), new NoopEconomyRepository(),
            GameDataCatalog.BuiltIn, NullLogger<PlayerRuntime>.Instance, ownership: ownership);

        // 加载路径即完成首次真实续租（actor 创建内嵌的初次推进命令）。
        await runtime.LoadAsync(playerId);
        var renewsAfterLoad = ownership.RenewCalls;
        Assert.True(renewsAfterLoad >= 1);

        // 距上次成功续租不足 15s：后续命令、刷写与推进都不再触发续租 UPDATE。
        var enqueue = await runtime.ExecuteAsync(playerId, "queue.configure", JsonSerializer.SerializeToElement(new { loopEnabled = true }));
        var presence = await runtime.ExecuteAsync(playerId, "player.presence", JsonSerializer.SerializeToElement(new { presence = "online" }));
        await runtime.FlushAsync(playerId, true);
        await runtime.AdvanceAllAsync(DateTimeOffset.UtcNow);

        Assert.True(enqueue.Success);
        Assert.True(presence.Success);
        Assert.Equal(renewsAfterLoad, ownership.RenewCalls);
    }

    /// <summary>simulation.advance 的客户端 targetTime 必须被服务端钳制：超出 30s 时钟容忍按 UtcNow 结算，快进刷收益失效。</summary>

    [Fact]
    public async Task SimulationAdvanceClampsClientSuppliedFutureTarget()
    {
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "快进封堵");
        var runtime = new PlayerRuntime(repository, new EmptyAssetRepository(), new NoopEconomyRepository(),
            GameDataCatalog.LoadEmbedded(), NullLogger<PlayerRuntime>.Instance);

        await runtime.LoadAsync(playerId);
        await runtime.ExecuteAsync(playerId, "action.enqueue", JsonSerializer.SerializeToElement(
            new ActionIntentRequest(Guid.NewGuid().ToString("D"), "cultivation.draw-qi", -1), ProtocolJson.Options));

        var result = await runtime.ExecuteAsync(playerId, "simulation.advance", JsonSerializer.SerializeToElement(new
        {
            targetTime = DateTimeOffset.UtcNow.AddHours(48)
        }));

        Assert.True(result.Success);
        // 目标被钳制到服务端当前时刻，不得推进到 48h 后的世界。
        Assert.True(result.Snapshot.LastSimulatedAt <= DateTimeOffset.UtcNow.AddSeconds(1));
        // 修为只按真实流逝结算（引气入体约 1/s），不可能结算出 48h 收益。
        Assert.True(result.Snapshot.State.Progression.Qi < 100);
    }

    /// <summary>actor 卸载时同步清理战报环形缓冲：RemoveAsync 后重连回放读取为空，缓冲不随卸载玩家滞留。</summary>

    [Fact]
    public async Task RemoveAsyncClearsReconnectLogBuffer()
    {
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "日志清理");
        var runtime = new PlayerRuntime(repository, new EmptyAssetRepository(), new NoopEconomyRepository(),
            GameDataCatalog.LoadEmbedded(), NullLogger<PlayerRuntime>.Instance);

        await runtime.LoadAsync(playerId);
        await runtime.ExecuteAsync(playerId, "action.enqueue", JsonSerializer.SerializeToElement(
            new ActionIntentRequest(Guid.NewGuid().ToString("D"), "cultivation.draw-qi", 1), ProtocolJson.Options));
        var advanced = await runtime.ExecuteAsync(playerId, "simulation.advance", JsonSerializer.SerializeToElement(new
        {
            targetTime = DateTimeOffset.UtcNow.AddSeconds(11)
        }));
        Assert.True(advanced.Success);
        // 完成一次行动产生战报事件并写入环形缓冲。
        Assert.NotEmpty(runtime.GetRecentLogEvents(playerId));

        await runtime.RemoveAsync(playerId, flush: false);

        Assert.Empty(runtime.GetRecentLogEvents(playerId));
    }

    /// <summary>
    /// 刷盘 sweep 有界并行：12 名在线玩家同时落盘，观测在途并发实际大于 1 且恒不超过并发上限，
    /// 一轮落盘的并行度由 FlushSweepMaxConcurrency 封顶而不是串行总和。
    /// </summary>
    [Fact]
    public async Task FlushAllAsyncRunsWithBoundedParallelism()
    {
        var playerIds = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        var repository = new SweepStateRepository(playerIds.ToDictionary(id => id, id => EmptyPlayerBundle(id, "并发落盘")))
        {
            EnqueueDelay = TimeSpan.FromMilliseconds(150)
        };
        await using var runtime = new PlayerRuntime(repository, new EmptyAssetRepository(), new NoopEconomyRepository(),
            GameDataCatalog.LoadEmbedded(), NullLogger<PlayerRuntime>.Instance);
        foreach (var playerId in playerIds) await runtime.LoadAsync(playerId);

        await runtime.FlushAllAsync(true);

        Assert.Equal(playerIds.Length, repository.EnqueueCalls.Count);
        foreach (var playerId in playerIds) Assert.True(repository.EnqueueCalls.ContainsKey(playerId));
        // 并发上限与 PlayerRuntime.FlushSweepMaxConcurrency（8）一致：
        // 低于 2 说明退化为串行，超过上限说明有界并发失效。
        Assert.InRange(repository.MaxInFlight, 2, 8);
    }

    /// <summary>
    /// 刷盘 sweep 单 actor 失败不拖垮整轮：失败玩家的异常照常上抛（单失败保持原异常类型），
    /// 其余玩家的分段仍完成 durable 入队，下一轮可重试。
    /// </summary>
    [Fact]
    public async Task FlushAllAsyncCollectsActorFailureWithoutBlockingOthers()
    {
        var playerIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var repository = new SweepStateRepository(playerIds.ToDictionary(id => id, id => EmptyPlayerBundle(id, "失败隔离")))
        {
            FailingPlayerId = playerIds[1]
        };
        await using var runtime = new PlayerRuntime(repository, new EmptyAssetRepository(), new NoopEconomyRepository(),
            GameDataCatalog.LoadEmbedded(), NullLogger<PlayerRuntime>.Instance);
        foreach (var playerId in playerIds) await runtime.LoadAsync(playerId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.FlushAllAsync(true).AsTask());

        // 三个 actor 都收到了落盘尝试；成功两人的分段已入队，失败玩家未留下半截写入。
        Assert.Equal(playerIds.Length, repository.EnqueueCalls.Count);
        Assert.Contains(repository.Enqueued, write => write.PlayerId == playerIds[0]);
        Assert.Contains(repository.Enqueued, write => write.PlayerId == playerIds[2]);
        Assert.DoesNotContain(repository.Enqueued, write => write.PlayerId == playerIds[1]);
    }

    /// <summary>
    /// 推进 sweep 保持串行撮合时序：匹配等待的寻求者与实力带内候选在同一拍内完成预约与挂载，
    /// 双方共享同一 duelId 进入 Duel 相位；重跑同一场景配对结果一致。
    /// </summary>
    [Fact]
    public async Task AdvanceAllAsyncDuelMatchingPairsSeekerWithCandidateDeterministically()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        for (var run = 0; run < 3; run++)
        {
            var seekerId = Guid.NewGuid();
            var candidateId = Guid.NewGuid();
            var repository = new SweepStateRepository(new Dictionary<Guid, PlayerStateBundle>
            {
                [seekerId] = PendingDuelSeekerBundle(seekerId, "寻斗者", catalog),
                [candidateId] = SearchingDuelCandidateBundle(candidateId, "应战者", catalog)
            });
            await using var runtime = new PlayerRuntime(repository, new EmptyAssetRepository(), new NoopEconomyRepository(),
                catalog, NullLogger<PlayerRuntime>.Instance);
            await runtime.LoadAsync(seekerId);
            await runtime.LoadAsync(candidateId);

            await runtime.AdvanceAllAsync(DateTimeOffset.UtcNow);

            var seeker = await runtime.LoadAsync(seekerId);
            var candidate = await runtime.LoadAsync(candidateId);
            Assert.Equal(CombatExpeditionPhase.Duel, seeker.State.Actions[0].Expedition?.Phase);
            Assert.Equal(CombatExpeditionPhase.Duel, candidate.State.Actions[0].Expedition?.Phase);
            Assert.NotNull(seeker.State.Duel.ActiveDuelId);
            Assert.Equal(seeker.State.Duel.ActiveDuelId, candidate.State.Duel.ActiveDuelId);
        }
    }

    /// <summary>
    /// 多寻求者竞争同一候选池时串行撮合次序成立：一轮 sweep 至多产出一对共享 duelId 的斗法，
    /// 未配对的第三方不被重复消耗（候选一次只属一场撮合，落选寻求者回退正常波次）。
    /// </summary>
    [Fact]
    public async Task AdvanceAllAsyncContendedSeekersYieldSingleDuelPair()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        for (var run = 0; run < 3; run++)
        {
            var seeker1Id = Guid.NewGuid();
            var seeker2Id = Guid.NewGuid();
            var candidateId = Guid.NewGuid();
            var repository = new SweepStateRepository(new Dictionary<Guid, PlayerStateBundle>
            {
                [seeker1Id] = PendingDuelSeekerBundle(seeker1Id, "寻斗者甲", catalog),
                [seeker2Id] = PendingDuelSeekerBundle(seeker2Id, "寻斗者乙", catalog),
                [candidateId] = SearchingDuelCandidateBundle(candidateId, "应战者", catalog)
            });
            await using var runtime = new PlayerRuntime(repository, new EmptyAssetRepository(), new NoopEconomyRepository(),
                catalog, NullLogger<PlayerRuntime>.Instance);
            await runtime.LoadAsync(seeker1Id);
            await runtime.LoadAsync(seeker2Id);
            await runtime.LoadAsync(candidateId);

            await runtime.AdvanceAllAsync(DateTimeOffset.UtcNow);

            var states = new[]
            {
                await runtime.LoadAsync(seeker1Id),
                await runtime.LoadAsync(seeker2Id),
                await runtime.LoadAsync(candidateId)
            };
            var duelers = states.Where(state => state.State.Actions[0].Expedition?.Phase == CombatExpeditionPhase.Duel).ToArray();
            Assert.Equal(2, duelers.Length);
            Assert.NotNull(duelers[0].State.Duel.ActiveDuelId);
            Assert.Equal(duelers[0].State.Duel.ActiveDuelId, duelers[1].State.Duel.ActiveDuelId);
            var unmatched = Assert.Single(states, state => state.State.Actions[0].Expedition?.Phase != CombatExpeditionPhase.Duel);
            Assert.Null(unmatched.State.Duel.ActiveDuelId);
        }
    }

    /// <summary>构造物品处置测试目录：含可回收草药、无价物品与一件装备原始定义。</summary>
    private static GameDataCatalog CreateDisposalCatalog() => GameDataCatalog.Parse([System.Text.Encoding.UTF8.GetBytes("""
        [
          {"id":"item.test-herb","name":"测试草药","description":"可回收","sellPrice":2.5},
          {"id":"item.no-price","name":"无价之物","description":"无回收价"},
          {"id":"equipment.test-sword","name":"测试飞剑","slot":"weapon","tier":1}
        ]
        """)]);

    /// <summary>构造仅含空头、无分段的存量玩家假仓库。</summary>
    private static FakeStateRepository CreateEmptyPlayerRepository(Guid playerId, string name)
    {
        var createdAt = DateTimeOffset.UtcNow;
        return new FakeStateRepository(new PlayerStateBundle(
            new PlayerIdentity(playerId, Guid.NewGuid(), name, createdAt),
            new StoredHead(playerId, 0, 1, createdAt, string.Empty, string.Empty, createdAt),
            new Dictionary<string, StoredSection>(),
            []));
    }

    /// <summary>构造仅含空头、无分段的全新玩家持久化包（多玩家仓库用）。</summary>
    private static PlayerStateBundle EmptyPlayerBundle(Guid playerId, string name)
    {
        var createdAt = DateTimeOffset.UtcNow;
        return new PlayerStateBundle(
            new PlayerIdentity(playerId, Guid.NewGuid(), name, createdAt),
            new StoredHead(playerId, 0, 1, createdAt, string.Empty, string.Empty, createdAt),
            new Dictionary<string, StoredSection>(), []);
    }

    /// <summary>把内存聚合编码成可加载的持久化包（全分段 + 校验头）。</summary>
    private static PlayerStateBundle PersistedBundle(Guid playerId, PlayerAggregate aggregate, DateTimeOffset createdAt)
    {
        var storedAt = DateTimeOffset.UtcNow;
        var writes = SectionCodec.EncodeAggregate(aggregate, storedAt, aggregate.StateVersion);
        var sections = writes.ToDictionary(
            write => write.SectionKind,
            write => new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion, write.SchemaVersion,
                write.Codec, write.Payload.ToArray(), write.RawBytes, write.Checksum, write.UpdatedAt),
            StringComparer.Ordinal);
        return new PlayerStateBundle(
            new PlayerIdentity(playerId, aggregate.AccountId.Value, aggregate.DisplayName, createdAt),
            new StoredHead(playerId, aggregate.StateVersion, SectionCodec.CurrentSchemaVersion, aggregate.LastSimulatedAt,
                aggregate.GameDataFingerprint, SectionCodec.ComputeHeadChecksum(sections.Values), storedAt),
            sections, []);
    }

    /// <summary>
    /// 构造一名已推进到斗法匹配等待相位的持久化玩家：勾选斗法的讨伐行动 + 预置遭遇计数，
    /// 推进越过首个搜寻边界后停在 DuelPending（搜寻等待 20s，+30s 必达）。
    /// </summary>
    private static PlayerStateBundle PendingDuelSeekerBundle(Guid playerId, string name, GameDataCatalog catalog)
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(Guid.NewGuid()), name, createdAt, catalog.Fingerprint);
        aggregate.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1, null, null, true), createdAt, catalog.Actions);
        aggregate.Duel.EncounterCharges = 1;
        SimulationEngine.AdvanceTo(aggregate, createdAt.AddSeconds(30), catalog,
            new AssetWallet(new PlayerId(playerId)), new DuelAdvanceOptions(MatchingEnabled: true));
        Assert.Equal(CombatExpeditionPhase.DuelPending, aggregate.Actions[0].Expedition?.Phase);
        return PersistedBundle(playerId, aggregate, createdAt);
    }

    /// <summary>
    /// 构造一名仍在搜寻相位、持有遭遇计数的持久化玩家（撮合池中的应战候选）：
    /// 只推进 3s，加载补算后也到不了首个搜寻边界，保持非等待相位。
    /// </summary>
    private static PlayerStateBundle SearchingDuelCandidateBundle(Guid playerId, string name, GameDataCatalog catalog)
    {
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId), new AccountId(Guid.NewGuid()), name, createdAt, catalog.Fingerprint);
        aggregate.Enqueue(new ActionIntent(new ActionId("combat.hunt-qi-beast"), -1, null, null, true), createdAt, catalog.Actions);
        aggregate.Duel.EncounterCharges = 1;
        SimulationEngine.AdvanceTo(aggregate, createdAt.AddSeconds(3), catalog,
            new AssetWallet(new PlayerId(playerId)), new DuelAdvanceOptions(MatchingEnabled: true));
        Assert.Equal(CombatExpeditionPhase.InitialSearch, aggregate.Actions[0].Expedition?.Phase);
        return PersistedBundle(playerId, aggregate, createdAt);
    }

    /// <summary>内存假玩家状态仓库：记录入队的分段与命令回执供断言。</summary>
    private class FakeStateRepository(PlayerStateBundle bundle) : IPlayerStateRepository
    {
        public List<SectionWrite> Enqueued { get; } = [];
        public List<CommandReceipt> Receipts { get; } = [];
        public virtual ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default) => ValueTask.FromResult<PlayerStateBundle?>(bundle);
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default)
        {
            Enqueued.AddRange(sections);
            return ValueTask.CompletedTask;
        }
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default)
        {
            Enqueued.AddRange(sections);
            Receipts.AddRange(receipts);
            return ValueTask.CompletedTask;
        }
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default) => ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    /// <summary>首次读取抛瞬时数据库错误，模拟 actor 创建失败。</summary>
    private sealed class FlakyStateRepository(PlayerStateBundle bundle) : FakeStateRepository(bundle)
    {
        private int _calls;
        public override ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            ++_calls == 1
                ? throw new InvalidOperationException("数据库暂不可用。")
                : base.LoadAsync(playerId, cancellationToken);
    }

    /// <summary>空资产假仓库。</summary>
    private sealed class EmptyAssetRepository : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>([]);
    }

    /// <summary>可变内存资产仓库：按事务增量改余额，透支抛错。</summary>
    private sealed class MutableAssetRepository(params AssetBalanceRecord[] initial) : IAssetBalanceRepository
    {
        private readonly Dictionary<(Guid PlayerId, string AssetId, string Variant), AssetBalanceRecord> _balances =
            initial.ToDictionary(balance => (balance.PlayerId, balance.AssetId, balance.Variant));

        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(_balances.Values.Where(balance => balance.PlayerId == playerId).ToArray());

        /// <summary>应用事务增量并返回受影响行的提交后余额（对齐真实仓储 RETURNING 回传形态）。</summary>
        public IReadOnlyList<AssetBalanceRecord> Apply(EconomyTransactionRequest request)
        {
            var touched = new List<(Guid PlayerId, string AssetId, string Variant)>();
            foreach (var delta in request.Deltas)
            {
                var key = (delta.PlayerId, delta.AssetId, delta.Variant);
                var current = _balances.TryGetValue(key, out var balance)
                    ? balance
                    : new AssetBalanceRecord(delta.PlayerId, delta.AssetId, delta.Variant, 0d, 0);
                var quantity = current.Quantity + delta.Delta;
                if (quantity < -1e-12) throw new InvalidOperationException("测试资产余额不足。");
                _balances[key] = current with { Quantity = Math.Max(0d, quantity), Version = current.Version + 1 };
                if (!touched.Contains(key)) touched.Add(key);
            }
            return touched.Select(key => _balances[key]).ToArray();
        }
    }

    /// <summary>记录每笔事务请求的经济假实现；apply=false 或命中 failOnCall 序号时模拟提交失败以触发回滚路径。</summary>
    private sealed class RecordingEconomyRepository(MutableAssetRepository assets, bool apply, int failOnCall = -1) : IEconomyTransactionStore
    {
        public List<EconomyTransactionRequest> Requests { get; } = [];

        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var applied = apply && Requests.Count != failOnCall;
            var balances = applied ? assets.Apply(request) : null;
            return ValueTask.FromResult(new EconomyTransactionResult(
                request.TransactionId,
                request.IdempotencyKey,
                applied,
                false,
                DateTimeOffset.UtcNow,
                request.ResultPayload,
                balances));
        }
    }

    /// <summary>直接返回成功回执的经济事务假实现。</summary>
    private sealed class NoopEconomyRepository : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload));
    }

    /// <summary>续约恒 false 的所有权假实现，用于触发「租约丢失失败关闭」路径。</summary>
    private sealed class LostOwnershipStore : IPlayerOwnershipStore
    {
        public ValueTask<bool> AcquireAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask<bool> RenewAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask ReleaseAsync(Guid playerId, Guid ownerInstanceId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    /// <summary>计数版所有权假实现：抢占/续租恒成功并记录续租次数，用于验证续租去抖窗口。</summary>
    private sealed class CountingOwnershipStore : IPlayerOwnershipStore
    {
        public int RenewCalls;
        public ValueTask<bool> AcquireAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask<bool> RenewAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default)
        {
            RenewCalls++;
            return ValueTask.FromResult(true);
        }
        public ValueTask ReleaseAsync(Guid playerId, Guid ownerInstanceId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// 多玩家内存假仓库：按 playerId 分发持久化包；统计每玩家落盘调用次数与
    /// 最大在途并发（供有界并行断言），可按玩家注入落盘延迟与失败。
    /// </summary>
    private sealed class SweepStateRepository(IReadOnlyDictionary<Guid, PlayerStateBundle> bundles) : IPlayerStateRepository
    {
        private readonly object _gate = new();
        private int _inFlight;

        /// <summary>EnqueueSectionsAsync 观察到的最大同时在途调用数。</summary>
        public int MaxInFlight;
        /// <summary>每次入队前的人工延迟，用于放大并发窗口。</summary>
        public TimeSpan EnqueueDelay = TimeSpan.Zero;
        /// <summary>命中该玩家的入队调用一律抛 InvalidOperationException。</summary>
        public Guid? FailingPlayerId;
        /// <summary>每玩家被尝试入队的次数（含失败尝试）。</summary>
        public ConcurrentDictionary<Guid, int> EnqueueCalls { get; } = new();
        /// <summary>已成功入队的分段写。</summary>
        public List<SectionWrite> Enqueued { get; } = [];

        public ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(bundles.TryGetValue(playerId, out var bundle) ? bundle : null);

        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default) =>
            EnqueueCoreAsync(sections, [], cancellationToken);

        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default) =>
            EnqueueCoreAsync(sections, receipts, cancellationToken);

        private async ValueTask EnqueueCoreAsync(IReadOnlyCollection<SectionWrite> sections, IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken)
        {
            var playerId = sections.Count > 0 ? sections.First().PlayerId : receipts.First().PlayerId;
            var arrived = Interlocked.Increment(ref _inFlight);
            int observed;
            do { observed = Volatile.Read(ref MaxInFlight); }
            while (arrived > observed && Interlocked.CompareExchange(ref MaxInFlight, arrived, observed) != observed);
            try
            {
                if (EnqueueDelay > TimeSpan.Zero) await Task.Delay(EnqueueDelay, cancellationToken);
                EnqueueCalls.AddOrUpdate(playerId, 1, (_, count) => count + 1);
                if (FailingPlayerId == playerId) throw new InvalidOperationException("模拟落盘失败。");
                lock (_gate) Enqueued.AddRange(sections);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }
}
