using System.Collections.Concurrent;
using System.Text.Json;
using DaoHua.Ecs;
using Domain.Combat;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Protocol;
using Server.Clean;
using Server.Clean.Social;
using Xunit;
using StoredHead = Domain.Persistence.PlayerStateHead;

namespace Server.Tests;

/// <summary>
/// 批次二韧性与并发不变量回归测试：
/// 1. 多条带门闩中途取消回退已占条带；
/// 2. 引用计数门闩释放/取消后零泄漏回收；
/// 3. 多播事件单订阅者抛异常或取消不影响其余订阅者；
/// 4. 模拟主循环 NextAdvanceBoundaryUtc 抛异常时不崩溃退避续跑；
/// 5. LoadOwnedAsync 被提交栅栏阻断时彻底清理半成品 ECS 实体并在解除后归还孤立租约；
/// 6. queue.move 上顶 1 号位触发队首斗法弃战与 ECS 遭遇销毁，且与 AdvanceAllAsync 并发无 AB-BA 死锁；
/// 7. ActionParticipationRuntime 隔离损坏会话行且离线推进不因坏会话阻断健康队伍。
/// </summary>
public sealed class RuntimeResilienceTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PlayerCommitGates_AcquireCanceledMidway_ReleasesAlreadyAcquiredStripes()
    {
        var playerA = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid playerB = Guid.Empty;
        var stripeA = PlayerCommitGates.StripesFor([playerA])[0];
        int stripeB = stripeA;
        for (var i = 2; i < 500; i++)
        {
            var candidate = new Guid(i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
            var s = PlayerCommitGates.StripesFor([candidate])[0];
            if (s != stripeA)
            {
                playerB = candidate;
                stripeB = s;
                break;
            }
        }
        Assert.NotEqual(stripeA, stripeB);

        // 确保 pairSorted[0] 先被占住的顺序是 stripe[0] 空闲、stripe[1] 被另一任务占住
        var firstStripe = Math.Min(stripeA, stripeB);
        var secondStripe = Math.Max(stripeA, stripeB);
        var pair = stripeA < stripeB ? new[] { playerA, playerB } : new[] { playerB, playerA };
        var stripes = PlayerCommitGates.StripesFor(pair);
        Assert.Equal([firstStripe, secondStripe], stripes);

        // 先独立锁住第二个条带，再触发同一套有序获取并中途取消
        await PlayerCommitGates.AcquireAllAsync([secondStripe], CancellationToken.None);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await PlayerCommitGates.AcquireAllAsync(stripes, cts.Token));

            // 第一个条带必须已被自动释放，能够立刻重新获取
            using var immediateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await PlayerCommitGates.AcquireAllAsync([firstStripe], immediateTimeout.Token);
            PlayerCommitGates.ReleaseAll([firstStripe]);
        }
        finally
        {
            PlayerCommitGates.ReleaseAll([secondStripe]);
        }
    }

    [Fact]
    public async Task RefCountedGateMap_AcquireAndCancel_ReclaimsUnusedEntries()
    {
        var map = new RefCountedGateMap<Guid>();
        var key = Guid.NewGuid();

        var first = await map.AcquireAsync(key, CancellationToken.None);
        Assert.Equal(1, map.ActiveCount);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await map.AcquireAsync(key, cts.Token));
        Assert.Equal(1, map.ActiveCount);

        first.Dispose();
        Assert.Equal(0, map.ActiveCount);

        var other = Guid.NewGuid();
        var batch = await map.AcquireOrderedAsync([other, key], cancellationToken: CancellationToken.None);
        Assert.Equal(2, map.ActiveCount);
        batch.Dispose();
        Assert.Equal(0, map.ActiveCount);
    }

    [Fact]
    public async Task AsyncEventDispatch_SubscriberFaultOrCancel_ContinuesRemainingSubscribers()
    {
        var delivered = new List<string>();
        Func<Guid, ValueTask>? asyncHandlers = null;
        asyncHandlers += _ => throw new OperationCanceledException("模拟断连取消");
        asyncHandlers += _ => throw new InvalidOperationException("模拟订阅者故障");
        asyncHandlers += id =>
        {
            delivered.Add(id.ToString("N"));
            return ValueTask.CompletedTask;
        };

        var targetId = Guid.NewGuid();
        await AsyncEventDispatch.InvokeEachAsync(asyncHandlers, targetId, NullLogger.Instance, "test");
        Assert.Single(delivered);
        Assert.Equal(targetId.ToString("N"), delivered[0]);

        var syncDelivered = 0;
        Action<Guid>? syncHandlers = null;
        syncHandlers += _ => throw new InvalidOperationException("同步订阅者失败");
        syncHandlers += _ => syncDelivered++;
        AsyncEventDispatch.InvokeEach(syncHandlers, targetId, NullLogger.Instance, "test");
        Assert.Equal(1, syncDelivered);
    }

    [Fact]
    public async Task PlayerSimulationHostedService_BoundaryThrows_DoesNotCrashLoop()
    {
        var clock = new MutableClock(Epoch);
        var runtime = new FaultyBoundaryRuntime();
        using var service = new PlayerSimulationHostedService(
            runtime, clock, NullLogger<PlayerSimulationHostedService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.StartAsync(cts.Token);
        await runtime.ReachedThirdAdvance.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        Assert.True(runtime.AdvanceCalls >= 3);
    }

    [Fact]
    public async Task EcsPlayerRuntimeAdapter_LoadOwnedAsync_WhenFenced_CleansEcsAndSweepsIdleLease()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.NewGuid();
        var clock = new MutableClock(Epoch);
        var sections = new InMemorySections();
        var assets = new InMemoryAssets();
        var ownership = new TrackingOwnershipStore();

        var agg = PlayerAggregate.Create(new(playerId), new(playerId), "栅栏道友", Epoch, catalog.Fingerprint);
        agg.SkipTutorial();
        agg.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), -1), Epoch, catalog.Actions);
        sections.Seed(BundleFromState(agg.ExportState()));

        var economy = new ThrowingUnknownEconomy();
        using var ecs = new EcsGameRuntime(catalog, Epoch);
        await using var adapter = new EcsPlayerRuntimeAdapter(
            ecs, sections, assets, economy, catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock,
            ownership: ownership,
            serverOptions: DefaultOptions());

        // 离线期间挂着采集行动，LoadOwnedAsync 补算产出资产 delta 并在 CommitAsync 触发 Unknown 异常
        clock.Advance(TimeSpan.FromMinutes(15));
        await Assert.ThrowsAsync<EconomyCommitOutcomeUnknownException>(() => adapter.LoadAsync(playerId).AsTask());

        // 结局未知栅栏需暂留快照、ECS 实体与租约用于核实护航（不可被他实例覆写）
        Assert.NotNull(adapter.TryGetLoaded(playerId));
        Assert.Contains(ecs.ExportSnapshots(), s => s.PlayerId == playerId);
        Assert.Equal(playerId, ownership.CurrentOwnerPlayerId);

        // 修复经济核验返回值后，AdvanceAllAsync 主动清扫 _fencedCommits 应解除栅栏、销毁陈旧实体并归还空闲租约
        economy.ShouldResolveCommit = true;
        clock.Advance(TimeSpan.FromSeconds(2));
        await adapter.AdvanceAllAsync(clock.UtcNow);

        Assert.Null(adapter.TryGetLoaded(playerId));
        Assert.DoesNotContain(ecs.ExportSnapshots(), s => s.PlayerId == playerId);
        Assert.Null(ownership.CurrentOwnerPlayerId);
        Assert.Equal(1, economy.CommitCalls); // 核实只读，不是第二次提交。
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EcsPlayerRuntimeAdapter_ConfirmedCheckpoint_WalletReadFailureNeverRestoresBufferedAssets(bool cancelRead)
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.NewGuid();
        var clock = new MutableClock(Epoch);
        var sections = new InMemorySections();
        var agg = PlayerAggregate.Create(new(playerId), new(playerId), "检查点道友", Epoch, catalog.Fingerprint);
        agg.SkipTutorial();
        agg.Enqueue(new ActionIntent(new ActionId("explore.gather-ningxue-grass"), -1), Epoch, catalog.Actions);
        sections.Seed(BundleFromState(agg.ExportState()));
        var storage = new CheckpointStorage(cancelRead);
        using var ecs = new EcsGameRuntime(catalog, Epoch);
        await using var adapter = new EcsPlayerRuntimeAdapter(ecs, sections, storage, storage, catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock, serverOptions: DefaultOptions());
        await adapter.LoadAsync(playerId);
        clock.Advance(TimeSpan.FromMinutes(2));
        await adapter.AdvanceAllAsync(clock.UtcNow);
        Assert.Empty(storage.Commits); // 有采集收益，但未到检查点，仍在缓冲。
        var checkpointAt = Epoch.AddMinutes(10);
        clock.UtcNow = checkpointAt;
        if (cancelRead)
            await Assert.ThrowsAsync<OperationCanceledException>(() => adapter.AdvanceAllAsync(checkpointAt).AsTask());
        else
            await Assert.ThrowsAsync<IOException>(() => adapter.AdvanceAllAsync(checkpointAt).AsTask());
        var checkpoint = Assert.Single(storage.Commits);
        Assert.NotEmpty(checkpoint.Deltas);
        Assert.Equal(checkpointAt, adapter.TryGetLoaded(playerId)!.LastSimulatedAt);
        Assert.Equal(checkpointAt, Assert.Single(ecs.ExportSnapshots()).State.LastSimulatedAt);

        // 相同时钟重试并强制刷盘只允许重读余额；若内层或整批回滚恢复历史缓冲，将在此额外入账。
        await adapter.AdvanceAllAsync(checkpointAt);
        await adapter.FlushAsync(playerId, force: true);
        Assert.Single(storage.Commits);
        Assert.Equal(checkpointAt, adapter.TryGetLoaded(playerId)!.LastSimulatedAt);
        Assert.True(storage.ReadRecoveredBalance);
    }

    [Fact]
    public async Task EcsPlayerRuntimeAdapter_QueueMoveUpward_AndConcurrentForfeit_NoDeadlockAndDestroysEncounters()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        // 故意令 playerA > playerB：AdvanceAllAsync 按升序先拿 playerB 再拿 playerA；
        // 若 playerA 持锁调用 RouteDuelForfeitAsync(playerB) 就会引发经典 AB-BA 死锁
        var id1 = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var id2 = Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff");
        var playerLow = id1;
        var playerHigh = id2;

        var clock = new MutableClock(Epoch);
        var sections = new InMemorySections();
        var assets = new InMemoryAssets();
        var economy = new RecordingNoopEconomy();

        sections.Seed(CreateDuelReadyBundle(playerLow, "低序散修", catalog));
        sections.Seed(CreateDuelReadyBundle(playerHigh, "高序剑修", catalog));

        using var ecs = new EcsGameRuntime(catalog, Epoch, duelAdvance: new DuelAdvanceOptions(MatchingEnabled: true));
        await using var adapter = new EcsPlayerRuntimeAdapter(
            ecs, sections, assets, economy, catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock,
            serverOptions: DefaultOptions());

        await adapter.AttachConnectionAsync(playerLow);
        await adapter.AttachConnectionAsync(playerHigh);

        // 给 playerHigh 先入队「讨伐」（开启 PVP），再入队一个「采集」排在 1 号位
        await EnqueueActionAsync(adapter, playerHigh, "combat.hunt-qi-beast", pvpEnabled: true);
        var secondReq = Guid.NewGuid();
        var secondEnqueue = await adapter.ExecuteAsync(playerHigh, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                secondReq.ToString("D"), "explore.gather-ningxue-grass", -1), ProtocolJson.Options),
            0, secondReq);
        Assert.True(secondEnqueue.Success, secondEnqueue.Message);
        var secondActionId = adapter.TryGetLoaded(playerHigh)!.State.Actions[1].Id;

        await EnqueueActionAsync(adapter, playerLow, "combat.hunt-qi-beast", pvpEnabled: true);

        // 推进直到撮合完成并挂载双方斗法回放
        for (var i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await adapter.AdvanceAllAsync(clock.UtcNow);
            if (adapter.TryGetLoaded(playerHigh)!.State.Duel.ActiveDuelId is not null
                && adapter.TryGetLoaded(playerLow)!.State.Duel.ActiveDuelId is not null)
                break;
        }
        Assert.NotNull(adapter.TryGetLoaded(playerHigh)!.State.Duel.ActiveDuelId);
        Assert.NotNull(adapter.TryGetLoaded(playerLow)!.State.Duel.ActiveDuelId);

        // 并发压测：高序玩家用 queue.move把 1 号位采集上顶（direction=-1）弃战，同时循环跑 AdvanceAllAsync
        using var concurrentAdvanceCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var advanceLoop = Task.Run(async () =>
        {
            for (var i = 0; i < 20 && !concurrentAdvanceCts.IsCancellationRequested; i++)
            {
                await adapter.AdvanceAllAsync(clock.UtcNow, concurrentAdvanceCts.Token);
                await Task.Delay(5, concurrentAdvanceCts.Token);
            }
        }, concurrentAdvanceCts.Token);

        var moveReq = Guid.NewGuid();
        var moveRes = await adapter.ExecuteAsync(playerHigh, "queue.move",
            JsonSerializer.SerializeToElement(new { requestId = moveReq, actionInstanceId = secondActionId, direction = -1 }, ProtocolJson.Options),
            0, moveReq);
        await concurrentAdvanceCts.CancelAsync();
        try { await advanceLoop; } catch (OperationCanceledException) { }

        Assert.True(moveRes.Success, moveRes.Message);
        Assert.Null(adapter.TryGetLoaded(playerHigh)!.State.Duel.ActiveDuelId);
        Assert.Equal("explore.gather-ningxue-grass", adapter.TryGetLoaded(playerHigh)!.State.Actions[0].Intent.ActionId.Value);
        // 队列换位上顶也必须在对手侧记胜（Resolved + Forfeited + PlayerWon）且本侧解除斗法与过期遭遇
        var peerDuel = adapter.TryGetLoaded(playerLow)!.State.Actions[0].Expedition?.Duel;
        Assert.NotNull(peerDuel);
        Assert.True(peerDuel!.Resolved);
        Assert.True(peerDuel.Forfeited);
        Assert.Equal(CombatOutcome.PlayerWon, peerDuel.ViewerOutcome);
    }

    [Fact]
    public async Task ActionParticipationRuntime_CorruptRowOnRestoreAndOfflineDue_IsolatesBrokenSession()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var clock = new MutableClock(Epoch);
        var presence = new OnlinePresenceRegistry();
        var store = new CorruptCapableParticipationStore();

        var healthyLeader = Guid.NewGuid();
        var healthyMate = Guid.NewGuid();
        var partyId = Guid.NewGuid();
        var stats = CombatantStats.Create(50_000, 2_000, 500, 1);
        var healthySpec = new ActionParticipationSpec(
            "session-healthy",
            "encounter.twilight-pass",
            "node.yunxi-village",
            12345,
            0,
            Epoch.ToUnixTimeMilliseconds() / 1000d,
            [
                new ActionParticipantSnapshot(healthyLeader.ToString("D"), "甲", 1, 0, 0, stats.MaxHitpoints, stats.MaxManapoints, stats, [], CombatSkills: PlayerCombatSkillState.Empty),
                new ActionParticipantSnapshot(healthyMate.ToString("D"), "乙", 1, 0, 0, stats.MaxHitpoints, stats.MaxManapoints, stats, [], CombatSkills: PlayerCombatSkillState.Empty)
            ])
        { ActionId = new ActionId("combat.hunt-qi-beast") };

        var seedRuntime = new ActionParticipationRuntime(
            catalog, store, presence, clock, NullLogger<ActionParticipationRuntime>.Instance);
        await seedRuntime.CreateAsync(partyId, healthySpec);

        // 注入一条规格 JSON 损坏的活跃会话行
        store.SeedRawSession(new ActionParticipationSessionRecord(
            "session-corrupt",
            Guid.NewGuid(),
            "{invalid-json",
            "{invalid-doc",
            1,
            Epoch));

        var recoveredRuntime = new ActionParticipationRuntime(
            catalog, store, presence, clock, NullLogger<ActionParticipationRuntime>.Instance);
        // EnsureRelationsRestoredAsync 在 SynchronizePresenceAsync 中运行，坏行不得阻塞健康成员映射恢复
        await recoveredRuntime.SynchronizePresenceAsync(healthyLeader, clock.UtcNow);
        Assert.True(recoveredRuntime.RequiresReconciliation(healthyLeader));

        // 推进离线队列：坏行应被跳过隔离，健康离线队伍成员正常返回
        clock.Advance(TimeSpan.FromHours(1));
        var advanced = await recoveredRuntime.AdvanceOfflineDueAsync(clock.UtcNow, TimeSpan.FromMinutes(10));
        Assert.Contains(healthyLeader, advanced);
        Assert.Contains(healthyMate, advanced);
    }

    private static async Task EnqueueActionAsync(EcsPlayerRuntimeAdapter adapter, Guid playerId, string actionId, bool pvpEnabled)
    {
        var req = Guid.NewGuid();
        var res = await adapter.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(new ActionIntentRequest(
                req.ToString("D"), actionId, -1, PvpEnabled: pvpEnabled), ProtocolJson.Options),
            0, req);
        Assert.True(res.Success, res.Message);
    }

    private static PlayerStateBundle CreateDuelReadyBundle(Guid playerId, string name, GameDataCatalog catalog)
    {
        var agg = PlayerAggregate.Create(new(playerId), new(playerId), name, Epoch, catalog.Fingerprint);
        agg.SkipTutorial();
        agg.Duel.EncounterCharges = 2;
        return BundleFromState(agg.ExportState());
    }

    private static PlayerStateBundle BundleFromState(PlayerAggregateState state)
    {
        var writes = SectionCodec.EncodeAggregateFull(state, state.LastSimulatedAt);
        var map = writes.ToDictionary(
            w => w.SectionKind,
            w => new StoredSection(w.PlayerId, w.SectionKind, w.StateVersion, w.SchemaVersion, w.Codec, w.Payload.ToArray(), w.RawBytes, w.Checksum, w.UpdatedAt),
            StringComparer.Ordinal);
        var head = new StoredHead(state.PlayerId.Value, state.StateVersion, SectionCodec.CurrentSchemaVersion,
            state.LastSimulatedAt, state.GameDataFingerprint, SectionCodec.ComputeHeadChecksum(map.Values), state.LastSimulatedAt);
        return new PlayerStateBundle(new PlayerIdentity(state.PlayerId.Value, state.AccountId.Value, state.DisplayName, state.CreatedAt),
            head, map, []);
    }

    private static ServerOptions DefaultOptions() =>
        ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = "Host=unused;Database=unused",
            ["Storage:OnlineCheckpointIntervalSeconds"] = "600"
        }).Build());

    private sealed class MutableClock(DateTimeOffset initial) : IPlayerSimulationClock
    {
        public DateTimeOffset UtcNow { get; set; } = initial;
        public bool IsDeterministic => true;
        public void AdvanceTo(DateTimeOffset target) => UtcNow = target;
        public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
    }

    private sealed class FaultyBoundaryRuntime : IPlayerRuntime
    {
        public int AdvanceCalls;
        public readonly TaskCompletionSource ReachedThirdAdvance = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LoadedCount => 0;
        public event Func<PlayerRuntimeSnapshot, ValueTask>? SnapshotUpdated { add { } remove { } }
        public event Func<Guid, IReadOnlyList<LogEventDto>, ValueTask>? LogBatchEmitted { add { } remove { } }

        public ValueTask<PlayerRuntimeSnapshot> LoadAsync(Guid playerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlayerRuntimeSnapshot> AttachConnectionAsync(Guid playerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public PlayerRuntimeSnapshot? TryGetLoaded(Guid playerId) => null;
        public ValueTask<PlayerCommandResult> ExecuteAsync(Guid playerId, string command, JsonElement payload, long expectedStateVersion, Guid requestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask FlushAsync(Guid playerId, bool force, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask FlushAllAsync(bool force, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(Guid playerId, bool flush, DateTimeOffset? offlineSince, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<PlayerRuntimeSnapshot> MutateOwnedAsync(Guid playerId, Action<PlayerAggregate> mutate, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<LogEventDto> GetRecentLogEvents(Guid playerId) => [];
        public ValueTask<IReadOnlyList<PlayerRuntimeSnapshot>> GetLoadedSnapshotsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<PlayerRuntimeSnapshot>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask AdvanceAllAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref AdvanceCalls) >= 3)
                ReachedThirdAdvance.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public DateTimeOffset? NextAdvanceBoundaryUtc => Volatile.Read(ref AdvanceCalls) == 1
            ? throw new InvalidOperationException("模拟边界计算故障")
            : Epoch.AddMilliseconds(50);
    }

    private sealed class InMemorySections : IPlayerStateRepository
    {
        private readonly ConcurrentDictionary<Guid, PlayerStateBundle> _bundles = new();
        public void Seed(PlayerStateBundle bundle) => _bundles[bundle.Identity.PlayerId] = bundle;
        public ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_bundles.TryGetValue(playerId, out var bundle) ? bundle : null);
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    private sealed class InMemoryAssets : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>([]);
    }

    private sealed class RecordingNoopEconomy : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, ReadOnlyMemory<byte>.Empty));
    }

    private sealed class CheckpointStorage(bool cancelRead) : IAssetBalanceRepository, IEconomyTransactionStore
    {
        private readonly Dictionary<(string, string), AssetBalanceRecord> balances = [];
        private bool failRead;
        public bool ReadRecoveredBalance;
        public List<EconomyTransactionRequest> Commits { get; } = [];

        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default)
        {
            if (failRead)
            {
                failRead = false;
                if (cancelRead) throw new OperationCanceledException("提交后钱包读取被取消");
                throw new IOException("提交后钱包读取失败");
            }
            ReadRecoveredBalance = balances.Count > 0;
            return ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(balances.Values.ToArray());
        }

        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
        {
            Commits.Add(request);
            foreach (var delta in request.Deltas)
            {
                var key = (delta.AssetId, delta.Variant);
                var prior = balances.GetValueOrDefault(key);
                balances[key] = new(delta.PlayerId, delta.AssetId, delta.Variant,
                    (prior?.Quantity ?? 0) + delta.Delta, (prior?.Version ?? 0) + 1);
            }
            failRead = true;
            // 存储已成功、结果走幂等确认口径，不带余额。
            return ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey,
                true, true, DateTimeOffset.UtcNow, ReadOnlyMemory<byte>.Empty));
        }
    }

    private sealed class ThrowingUnknownEconomy : IEconomyTransactionStore
    {
        public bool ShouldResolveCommit;
        public int CommitCalls;
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
        {
            CommitCalls++;
            throw new EconomyCommitOutcomeUnknownException(request.TransactionId, new IOException("模拟提交回包超时"));
        }

        public ValueTask<EconomyTransactionResult?> FindCommittedAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<EconomyTransactionResult?>(ShouldResolveCommit
                ? new(request.TransactionId, request.IdempotencyKey, true, true, DateTimeOffset.UtcNow, request.ResultPayload)
                : null);
    }

    private sealed class TrackingOwnershipStore : IPlayerOwnershipStore
    {
        public Guid? CurrentOwnerPlayerId;
        public ValueTask<bool> AcquireAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default)
        {
            CurrentOwnerPlayerId = playerId;
            return ValueTask.FromResult(true);
        }
        public ValueTask<bool> RenewAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(CurrentOwnerPlayerId == playerId);
        public ValueTask ReleaseAsync(Guid playerId, Guid ownerInstanceId, CancellationToken cancellationToken = default)
        {
            if (CurrentOwnerPlayerId == playerId) CurrentOwnerPlayerId = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CorruptCapableParticipationStore : IParticipationSessionStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ActionParticipationSessionRecord> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, List<ParticipationInboxItem>> _inbox = new();

        public void SeedRawSession(ActionParticipationSessionRecord record)
        {
            lock (_gate) _sessions[record.SessionId] = record;
        }

        public ValueTask<ActionParticipationSessionRecord?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            lock (_gate) return ValueTask.FromResult(_sessions.GetValueOrDefault(sessionId));
        }

        public ValueTask<IReadOnlyList<ActionParticipationSessionRecord>> ListActiveAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate) return ValueTask.FromResult<IReadOnlyList<ActionParticipationSessionRecord>>(_sessions.Values.Where(s => s.EndedAt is null).ToArray());
        }

        public ValueTask<IReadOnlyList<ActionParticipationSessionRecord>> ListActiveForMemberAsync(Guid playerId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                var matches = new List<ActionParticipationSessionRecord>();
                foreach (var item in _sessions.Values)
                {
                    if (item.EndedAt is not null) continue;
                    try
                    {
                        var spec = JsonSerializer.Deserialize<ActionParticipationSpec>(item.SpecJson, PersistenceJson.Options);
                        if (spec is not null && spec.Members.Any(m => m.PlayerId == playerId.ToString("D")))
                            matches.Add(item);
                    }
                    catch (JsonException) { }
                }
                return ValueTask.FromResult<IReadOnlyList<ActionParticipationSessionRecord>>(matches);
            }
        }

        public ValueTask<DateTimeOffset?> GetOfflineExpiryAsync(string sessionId, TimeSpan offlineLimit, IReadOnlyCollection<Guid> onlinePlayerIds, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<DateTimeOffset?>(null);

        public ValueTask<string?> GetInvalidationReasonAsync(string sessionId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);

        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadConsumableBalancesAsync(IReadOnlyList<ParticipationAssetKey> keys, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(keys.Select(k => new AssetBalanceRecord(k.PlayerId, k.AssetId, k.Variant, 0, 0)).ToArray());

        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadConsumableBalancesForItemsAsync(IReadOnlyList<ParticipationAssetKey> keys, CancellationToken cancellationToken = default)
            => LoadConsumableBalancesAsync(keys, cancellationToken);

        public ValueTask<ActionParticipationSessionRecord> CreateOrInitializeAsync(ActionParticipationSessionRecord session,
            ParticipationSessionEconomyCommit? economy = null, IReadOnlyList<ParticipationInboxItem>? inbox = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _sessions[session.SessionId] = session;
                AppendInbox(inbox);
                return ValueTask.FromResult(session);
            }
        }

        public ValueTask<bool> CommitAdvanceAsync(string sessionId, long expectedVersion, string stateJson, DateTimeOffset lastSimulatedAt,
            IReadOnlyList<ParticipationInboxItem> inbox, DateTimeOffset? endedAt = null, string? endReason = null,
            ParticipationSessionEconomyCommit? economy = null, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(sessionId, out var cur) || cur.Version != expectedVersion) return ValueTask.FromResult(false);
                _sessions[sessionId] = cur with { Version = expectedVersion + 1, StateJson = stateJson, LastSimulatedAt = lastSimulatedAt, EndedAt = endedAt, EndReason = endReason };
                AppendInbox(inbox);
                return ValueTask.FromResult(true);
            }
        }

        public ValueTask<IReadOnlyList<ParticipationInboxItem>> LoadPendingAsync(Guid playerId, CancellationToken cancellationToken = default)
        {
            lock (_gate) return ValueTask.FromResult<IReadOnlyList<ParticipationInboxItem>>(_inbox.GetValueOrDefault(playerId)?.ToArray() ?? []);
        }

        private void AppendInbox(IReadOnlyList<ParticipationInboxItem>? items)
        {
            if (items is null) return;
            foreach (var item in items)
            {
                if (!_inbox.TryGetValue(item.PlayerId, out var list)) _inbox[item.PlayerId] = list = [];
                list.Add(item);
            }
        }
    }
}
