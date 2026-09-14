using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Microsoft.Extensions.Logging.Abstractions;
using Protocol;
using Server.Clean;
using System.Text.Json;
using StoredHead = Server.Clean.PlayerStateHead;
using Xunit;

namespace Server.Tests;

/// <summary>包含针对 Player Runtime Tests 的测试用例与断言集合。</summary>
public sealed class PlayerRuntimeTests
{
    /// <summary>验证 Loadout Cannot Grant Unlearned Abilities。</summary>
    /// <returns>表示异步操作的任务。</returns>
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
            [{"id":"ability.test","style":"sword","category":"physical","element":"none","damageRatio":1,"flatDamage":1,"cooldownMilliseconds":1000,"castMilliseconds":1000,"manaCostRatio":0}]
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
    /// <summary>验证 New Player Command Is Serialized Through Single Writer And Flush Queue。</summary>
    /// <returns>表示异步操作的任务。</returns>

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
    /// <summary>验证 Lost Player Ownership Fails Closed。</summary>
    /// <returns>表示异步操作的任务。</returns>

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
    /// <summary>验证 Failed Actor Creation Is Removed And Retried On Next Command。</summary>
    /// <returns>表示异步操作的任务。</returns>

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
    /// <summary>验证 Advance All Async Emits Snapshot Updated Only When Simulation Changes。</summary>
    /// <returns>表示异步操作的任务。</returns>

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
    /// <summary>验证 Catalog Fingerprint Change Rebinds Stored State Instead Of Rejecting Session。</summary>
    /// <returns>表示异步操作的任务。</returns>

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
        var writes = Server.Clean.SectionCodec.EncodeAggregate(persisted, storedAt, persisted.StateVersion);
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
                Server.Clean.SectionCodec.CurrentSchemaVersion,
                persisted.LastSimulatedAt,
                storedFingerprint,
                Server.Clean.SectionCodec.ComputeHeadChecksum(sections.Values),
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
    /// <summary>验证 Simulation Advance Commits Sustained Pill Debit With Authoritative State。</summary>
    /// <returns>表示异步操作的任务。</returns>

    [Fact]
    public async Task SimulationAdvanceCommitsSustainedPillDebitWithAuthoritativeState()
    {
        var playerId = Guid.NewGuid();
        var repository = CreateEmptyPlayerRepository(playerId, "持久事务");
        var assets = new MutableAssetRepository(new AssetBalanceRecord(playerId, "item.qi-gathering-pill", "", 1d, 0));
        var economy = new RecordingEconomyRepository(assets, apply: true);
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
        var debit = Assert.Single(transaction.Deltas);
        Assert.Equal("item.qi-gathering-pill", debit.AssetId);
        Assert.Equal(-1d, debit.Delta);
    }
    /// <summary>验证 Failed Economy Commit Rolls Back Pill Duration And Cultivation Progress。</summary>
    /// <returns>表示异步操作的任务。</returns>

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
    /// <summary>验证 World Travel Command Enqueues Priority Action And Suspends Current。</summary>
    /// <returns>表示异步操作的任务。</returns>

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
    /// <summary>验证 World Travel To Current Node At Rest Does Not Add Action And Mid Travel Returns To Origin。</summary>
    /// <returns>表示异步操作的任务。</returns>

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

    /// <summary>执行 Create Empty Player Repository。</summary>
    /// <param name="playerId">playerId：玩家唯一标识。</param>
    /// <param name="name">name：相关参数。</param>
    /// <returns>返回操作结果。</returns>
    private static FakeStateRepository CreateEmptyPlayerRepository(Guid playerId, string name)
    {
        var createdAt = DateTimeOffset.UtcNow;
        return new FakeStateRepository(new PlayerStateBundle(
            new PlayerIdentity(playerId, Guid.NewGuid(), name, createdAt),
            new StoredHead(playerId, 0, 1, createdAt, string.Empty, string.Empty, createdAt),
            new Dictionary<string, StoredSection>(),
            []));
    }

    /// <summary>包含针对 Fake State Repository 的测试用例与断言集合。</summary>
    private class FakeStateRepository(PlayerStateBundle bundle) : IPlayerStateRepository
    {
        public List<SectionWrite> Enqueued { get; } = [];
        public List<CommandReceipt> Receipts { get; } = [];
        public virtual ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default) => ValueTask.FromResult<PlayerStateBundle?>(bundle);
        /// <summary>执行 Enqueue Sections Async。</summary>
        /// <param name="sections">sections：待写入的分区集合。</param>
        /// <param name="cancellationToken">cancellationToken：取消令牌。</param>
        /// <returns>表示异步操作的值任务。</returns>
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default)
        {
            Enqueued.AddRange(sections);
            return ValueTask.CompletedTask;
        }
        /// <summary>执行 Enqueue Sections Async。</summary>
        /// <param name="sections">sections：待写入的分区集合。</param>
        /// <param name="receipts">receipts：命令回执集合。</param>
        /// <param name="cancellationToken">cancellationToken：取消令牌。</param>
        /// <returns>表示异步操作的值任务。</returns>
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
        /// <summary>calls。</summary>
        private int _calls;
        /// <summary>执行 Load Async。</summary>
        /// <param name="playerId">playerId：玩家唯一标识。</param>
        /// <param name="cancellationToken">cancellationToken：取消令牌。</param>
        /// <returns>表示异步操作的值任务。</returns>
        public override ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            ++_calls == 1
                ? throw new InvalidOperationException("数据库暂不可用。")
                : base.LoadAsync(playerId, cancellationToken);
    }

    /// <summary>包含针对 Empty Asset Repository 的测试用例与断言集合。</summary>
    private sealed class EmptyAssetRepository : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>([]);
    }

    /// <summary>包含针对 Mutable Asset Repository 的测试用例与断言集合。</summary>
    private sealed class MutableAssetRepository(params AssetBalanceRecord[] initial) : IAssetBalanceRepository
    {
        private readonly Dictionary<(Guid PlayerId, string AssetId, string Variant), AssetBalanceRecord> _balances =
            initial.ToDictionary(balance => (balance.PlayerId, balance.AssetId, balance.Variant));

        /// <summary>执行 Load Balances Async。</summary>
        /// <param name="playerId">playerId：玩家唯一标识。</param>
        /// <param name="cancellationToken">cancellationToken：取消令牌。</param>
        /// <returns>表示异步操作的值任务。</returns>
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(_balances.Values.Where(balance => balance.PlayerId == playerId).ToArray());

        /// <summary>执行 Apply。</summary>
        /// <param name="request">request：事务请求。</param>
        public void Apply(EconomyTransactionRequest request)
        {
            foreach (var delta in request.Deltas)
            {
                var key = (delta.PlayerId, delta.AssetId, delta.Variant);
                var current = _balances.TryGetValue(key, out var balance)
                    ? balance
                    : new AssetBalanceRecord(delta.PlayerId, delta.AssetId, delta.Variant, 0d, 0);
                var quantity = current.Quantity + delta.Delta;
                if (quantity < -1e-12) throw new InvalidOperationException("测试资产余额不足。");
                _balances[key] = current with { Quantity = Math.Max(0d, quantity), Version = current.Version + 1 };
            }
        }
    }

    /// <summary>包含针对 Recording Economy Repository 的测试用例与断言集合。</summary>
    private sealed class RecordingEconomyRepository(MutableAssetRepository assets, bool apply) : IEconomyTransactionStore
    {
        public List<EconomyTransactionRequest> Requests { get; } = [];

        /// <summary>执行 Commit Async。</summary>
        /// <param name="request">request：事务请求。</param>
        /// <param name="cancellationToken">cancellationToken：取消令牌。</param>
        /// <returns>表示异步操作的值任务。</returns>
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (apply) assets.Apply(request);
            return ValueTask.FromResult(new EconomyTransactionResult(
                request.TransactionId,
                request.IdempotencyKey,
                apply,
                false,
                DateTimeOffset.UtcNow,
                request.ResultPayload));
        }
    }

    /// <summary>包含针对 Noop Economy Repository 的测试用例与断言集合。</summary>
    private sealed class NoopEconomyRepository : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload));
    }

    /// <summary>包含针对 Lost Ownership Store 的测试用例与断言集合。</summary>
    private sealed class LostOwnershipStore : IPlayerOwnershipStore
    {
        /// <summary>执行 From Result。</summary>
        /// <param name="true">true：相关参数。</param>
        /// <returns>表示异步操作的值任务。</returns>
        public ValueTask<bool> AcquireAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        /// <summary>执行 From Result。</summary>
        /// <param name="false">false：相关参数。</param>
        /// <returns>表示异步操作的值任务。</returns>
        public ValueTask<bool> RenewAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        /// <summary>Completed Task。</summary>
        public ValueTask ReleaseAsync(Guid playerId, Guid ownerInstanceId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
