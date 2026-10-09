using System.Collections.Concurrent;
using System.Text.Json;
using DaoHua.Ecs;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Domain.Simulation;
using Domain.World;
using Microsoft.Extensions.Logging.Abstractions;
using Protocol;
using Server.Clean;
using Server.Clean.Social;
using Server;
using Xunit;

namespace Ecs.Tests;

/// <summary>适配器跨边界门禁：同玩家串行、请求幂等和卸载后从 section/资产恢复。</summary>
public sealed class EcsAdapterBoundaryTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DuplicateEnqueueRequestDoesNotCreateSecondAction()
    {
        var fixture = CreateFixture();
        await using var adapter = fixture.Adapter;
        var requestId = Guid.Parse("10000000-0000-4000-8000-000000000021");
        var payload = JsonSerializer.SerializeToElement(new ActionIntentRequest(
            requestId.ToString("D"), "cultivation.draw-qi", 1), ProtocolJson.Options);

        var first = await adapter.ExecuteAsync(fixture.PlayerId, "action.enqueue", payload, 0, requestId);
        var replay = await adapter.ExecuteAsync(fixture.PlayerId, "action.enqueue", payload, 0, requestId);

        Assert.True(first.Success);
        Assert.Equal("IDEMPOTENT_REPLAY", replay.Code);
        Assert.Single(replay.Snapshot.State.Actions);
        await adapter.FlushAsync(fixture.PlayerId, true);
        Assert.Equal(8, fixture.Sections.LatestSections.Count);
    }

    [Fact]
    public async Task ConcurrentSamePlayerCommandsRemainSerialized()
    {
        var fixture = CreateFixture();
        await using var adapter = fixture.Adapter;
        var commands = Enumerable.Range(0, 8).Select(async index =>
        {
            var requestId = Guid.Parse($"10000000-0000-4000-8000-0000000000{31 + index:00}");
            var payload = JsonSerializer.SerializeToElement(new ActionIntentRequest(
                requestId.ToString("D"), "cultivation.draw-qi", 1), ProtocolJson.Options);
            return await adapter.ExecuteAsync(fixture.PlayerId, "action.enqueue", payload, 0, requestId);
        });

        var results = await Task.WhenAll(commands);
        var snapshot = await adapter.LoadAsync(fixture.PlayerId);

        Assert.All(results, result => Assert.True(result.Success, result.Message));
        Assert.Equal(8, snapshot.State.Actions.Count);
        Assert.Equal(8, snapshot.State.Actions.Select(action => action.Id).Distinct().Count());
    }

    [Fact]
    public async Task RemoveAndReloadKeepsActionVersionAndAssets()
    {
        var fixture = CreateFixture(new AssetBalanceRecord(
            PlayerId: Guid.Parse("10000000-0000-4000-8000-000000000023"),
            AssetId: "item.ningxue-grass", Variant: "", Quantity: 3, Version: 4));
        await using var adapter = fixture.Adapter;
        var requestId = Guid.Parse("10000000-0000-4000-8000-000000000024");
        var payload = JsonSerializer.SerializeToElement(new ActionIntentRequest(
            requestId.ToString("D"), "explore.gather-ningxue-grass", -1), ProtocolJson.Options);
        var enqueued = await adapter.ExecuteAsync(fixture.PlayerId, "action.enqueue", payload, 0, requestId);
        Assert.True(enqueued.Success, enqueued.Message);
        var beforeUnload = await adapter.LoadAsync(fixture.PlayerId);

        await adapter.RemoveAsync(fixture.PlayerId, flush: true);
        var restored = await adapter.LoadAsync(fixture.PlayerId);

        Assert.Equal(beforeUnload.State.Actions.Select(action => action.Id), restored.State.Actions.Select(action => action.Id));
        Assert.True(restored.State.StateVersion >= beforeUnload.State.StateVersion);
        Assert.Equal(3, restored.Assets.Single(asset => asset.AssetId == "item.ningxue-grass").Quantity);
        Assert.Equal(4, restored.Assets.Single(asset => asset.AssetId == "item.ningxue-grass").Version);
        Assert.Equal(PresenceState.Online, restored.State.Presence);
    }

    [Fact]
    public async Task BatchAbilityLearnDebitsAllManualsAndGrantsPerBookExperience()
    {
        var fixture = CreateFixture(
            new AssetBalanceRecord(PlayerId: Guid.Empty, AssetId: "item.tome.heavy-slash", Variant: "", Quantity: 5, Version: 1),
            new AssetBalanceRecord(PlayerId: Guid.Empty, AssetId: "item.fragment.heavy-slash", Variant: "", Quantity: 3, Version: 1));
        await using var adapter = fixture.Adapter;

        // 未习得批量研读 5 本秘籍：首本领悟、其余 4 本各 +10000 经验，钱包整笔扣 5。
        var tomePayload = JsonSerializer.SerializeToElement(new { itemId = "item.tome.heavy-slash", quantity = 5 }, ProtocolJson.Options);
        var learned = await adapter.ExecuteAsync(fixture.PlayerId, "ability.learn", tomePayload, 0, Guid.NewGuid());

        Assert.True(learned.Success, learned.Message);
        Assert.Contains(new AbilityId("ability.heavy-slash"), learned.Snapshot.State.LearnedAbilities);
        Assert.Equal(40_000d, learned.Snapshot.State.Loadout.AbilityExperiences!["ability.heavy-slash"]);
        var snapshot = await adapter.LoadAsync(fixture.PlayerId);
        Assert.Equal(0, snapshot.Assets.Single(asset => asset.AssetId == "item.tome.heavy-slash").Quantity);

        // 已习得后批量研读 3 本残卷：每本 +500，钱包扣 3。
        var fragmentPayload = JsonSerializer.SerializeToElement(new { itemId = "item.fragment.heavy-slash", quantity = 3 }, ProtocolJson.Options);
        var fragments = await adapter.ExecuteAsync(fixture.PlayerId, "ability.learn", fragmentPayload, 0, Guid.NewGuid());

        Assert.True(fragments.Success, fragments.Message);
        Assert.Equal(41_500d, fragments.Snapshot.State.Loadout.AbilityExperiences!["ability.heavy-slash"]);
        snapshot = await adapter.LoadAsync(fixture.PlayerId);
        Assert.Equal(0, snapshot.Assets.Single(asset => asset.AssetId == "item.fragment.heavy-slash").Quantity);
    }

    /// <summary>
    /// 活跃会话成员携带已发布版本执行命令：命令管线内的会话对账会消费本次推进新投递的结算
    /// 并推高 StateVersion——乐观并发基线必须取命令进入玩家门时的版本，否则会话成员每条
    /// 带版本命令都与自身刚推进的版本错位而必然冲突；真正落后于进入版本的请求仍须冲突。
    /// </summary>
    [Fact]
    public async Task SessionMemberCommandChecksEntryVersionAndStillRejectsStale()
    {
        var fixture = CreateParticipationFixture(
            new AssetBalanceRecord(Guid.Empty, "item.tome.heavy-slash", "", 3, 1));
        await using var adapter = fixture.Adapter;
        var loaded = await adapter.LoadAsync(fixture.PlayerId);
        var payload = JsonSerializer.SerializeToElement(new { itemId = "item.tome.heavy-slash", quantity = 1 },
            ProtocolJson.Options);

        var learned = await adapter.ExecuteAsync(fixture.PlayerId, "ability.learn", payload,
            loaded.StateVersion, Guid.NewGuid());
        var second = await adapter.ExecuteAsync(fixture.PlayerId, "ability.learn", payload,
            learned.Snapshot.StateVersion, Guid.NewGuid());
        var stale = await adapter.ExecuteAsync(fixture.PlayerId, "ability.learn", payload,
            learned.Snapshot.StateVersion, Guid.NewGuid());

        Assert.True(learned.Success, learned.Message);
        Assert.True(second.Success, second.Message);
        Assert.Equal(1, second.Snapshot.Assets.Single(asset => asset.AssetId == "item.tome.heavy-slash").Quantity);
        Assert.False(stale.Success);
        Assert.Equal("STATE_VERSION_CONFLICT", stale.Code);
    }

    [Theory]
    [InlineData("ability.learn")]
    [InlineData("tutorial.skip")]
    public async Task AtomicCommandFailureRestoresStateAndExactDirtyBoundary(string command)
    {
        var fixture = CreateFixtureWithTutorial(Epoch, null, command != "tutorial.skip",
            new AssetBalanceRecord(Guid.Empty, "item.tome.heavy-slash", "", 1, 1));
        await using var adapter = fixture.Adapter;
        var before = await adapter.LoadAsync(fixture.PlayerId);
        // 直接标一段尚未进入宿主 _dirtyAt 的旧脏数据，防止回滚误用宿主缓存丢失真实 dirty。
        fixture.Ecs.MarkDirty(fixture.PlayerId, PlayerSectionKind.World);
        var dirtyBefore = fixture.Ecs.GetDirtySections(fixture.PlayerId).OrderBy(kind => kind).ToArray();
        fixture.Economy.FailNextCommit = true;
        var payload = command == "ability.learn"
            ? JsonSerializer.SerializeToElement(new { itemId = "item.tome.heavy-slash" })
            : JsonSerializer.SerializeToElement(new { });
        var requestId = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(async () => await adapter.ExecuteAsync(fixture.PlayerId, command, payload, 0, requestId));
        Assert.Equal(JsonSerializer.Serialize(before.State), JsonSerializer.Serialize(fixture.Ecs.ExportSnapshot(fixture.PlayerId).State));
        Assert.Equal(dirtyBefore, fixture.Ecs.GetDirtySections(fixture.PlayerId).OrderBy(kind => kind));
        Assert.Empty(fixture.Sections.Receipts);
        var success = await adapter.ExecuteAsync(fixture.PlayerId, command, payload, 0, requestId);
        Assert.True(success.Success);
        Assert.Contains(fixture.Sections.Receipts, receipt => receipt.RequestId == requestId && receipt.Command == command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClockOnlyAdvanceIsDurableAndAcknowledgesWrittenSections(bool batch)
    {
        var fixture = CreateFixture();
        await using var adapter = fixture.Adapter;
        await adapter.LoadAsync(fixture.PlayerId);
        await adapter.FlushAsync(fixture.PlayerId, true);
        await AdvanceAsync(fixture, Epoch.AddSeconds(10), batch);
        await AdvanceAsync(fixture, Epoch.AddSeconds(20), batch);
        Assert.Equal(2, fixture.Economy.Requests.Count);
        Assert.All(fixture.Economy.Requests, request =>
        {
            Assert.Empty(request.Deltas);
            Assert.Contains(request.Sections!, section => section.SectionKind == PlayerSectionKinds.Progression);
        });
        var persisted = SectionCodec.DecodeAggregate((await fixture.Sections.LoadAsync(fixture.PlayerId))!);
        Assert.Equal(Epoch.AddSeconds(20), persisted.LastSimulatedAt);
        Assert.Empty(fixture.Ecs.GetDirtySections(fixture.PlayerId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownCommitReplaysExactRequestAndFencesWhenConfirmationFails(bool unavailable)
    {
        var fixture = CreateFixture(new AssetBalanceRecord(Guid.Empty, "item.tome.heavy-slash", "", 1, 1));
        await using var adapter = fixture.Adapter;
        await adapter.LoadAsync(fixture.PlayerId);
        fixture.Economy.CommitThenUnknown = true;
        fixture.Economy.FailConfirmation = unavailable;
        var payload = JsonSerializer.SerializeToElement(new { itemId = "item.tome.heavy-slash" });
        var id = Guid.NewGuid();
        if (unavailable)
        {
            var unknown = await Assert.ThrowsAsync<EconomyCommitOutcomeUnknownException>(async () =>
                await adapter.ExecuteAsync(fixture.PlayerId, "ability.learn", payload, 0, id));
            Assert.IsType<IOException>(unknown.InnerException);
            Assert.IsType<IOException>(unknown.Data["ConfirmationError"]);
            Assert.Same(unknown, await Assert.ThrowsAsync<EconomyCommitOutcomeUnknownException>(async () => await adapter.LoadAsync(fixture.PlayerId)));
            await Assert.ThrowsAsync<EconomyCommitOutcomeUnknownException>(async () => await adapter.FlushAsync(fixture.PlayerId, true));
            await Assert.ThrowsAsync<EconomyCommitOutcomeUnknownException>(async () => await adapter.MutateOwnedAsync(fixture.PlayerId, aggregate => aggregate.SkipTutorial()));
            var frozen = fixture.Ecs.ExportSnapshot(fixture.PlayerId).State;
            await adapter.AdvanceAllAsync(Epoch.AddSeconds(60));
            Assert.Equal(JsonSerializer.Serialize(frozen), JsonSerializer.Serialize(fixture.Ecs.ExportSnapshot(fixture.PlayerId).State));
        }
        else
        {
            Assert.True((await adapter.ExecuteAsync(fixture.PlayerId, "ability.learn", payload, 0, id)).Success);
            Assert.Single(fixture.Economy.Requests);
            Assert.Same(fixture.Economy.Attempts[0], fixture.Economy.Attempts[1]);
            Assert.Equal(0d, (await adapter.LoadAsync(fixture.PlayerId)).Assets.Single(asset => asset.AssetId == "item.tome.heavy-slash").Quantity);
        }
    }

    [Fact]
    public async Task OwnedMutationRenewsBeforeTouchingStateAndRejectsLostLease()
    {
        var ownership = new MemoryOwnership();
        var fixture = CreateOwnedFixture(Epoch, null, true, ownership);
        await using var adapter = fixture.Adapter;
        var before = await adapter.LoadAsync(fixture.PlayerId);
        ownership.AllowRenew = false;
        var called = false;
        await Assert.ThrowsAsync<PlayerOwnershipLostException>(async () => await adapter.MutateOwnedAsync(
            fixture.PlayerId, aggregate => { called = true; aggregate.SetLoopEnabled(true); }));
        Assert.False(called);
        Assert.Equal(1, ownership.RenewCalls);
        Assert.Equal(JsonSerializer.Serialize(before.State), JsonSerializer.Serialize(fixture.Ecs.ExportSnapshot(fixture.PlayerId).State));
        ownership.AllowRenew = true;
    }

    [Fact]
    public async Task ManualConsumableIsRejectedFromAutoUseSlots()
    {
        var fixture = CreateFixture(
            new AssetBalanceRecord(Guid.Empty, "item.tome.heavy-slash", "", 1, 1),
            new AssetBalanceRecord(Guid.Empty, "item.qi-gathering-pill", "", 2, 1));
        await using var adapter = fixture.Adapter;
        // 手动消耗品（category=manual）即使声明了行动域也不得进自动使用槽（含通用栏）。
        var manualPayload = JsonSerializer.SerializeToElement(
            new { actionType = "exploration", slotIndex = 0, itemId = "item.tome.heavy-slash", quantity = 1, enabled = true, group = "general" },
            ProtocolJson.Options);

        var rejected = await adapter.ExecuteAsync(fixture.PlayerId, "queue.auto_use.configure", manualPayload, 0, Guid.NewGuid());

        Assert.False(rejected.Success);
        Assert.Equal("INVALID_CONSUMABLE", rejected.Code);
        Assert.Empty(rejected.Snapshot.State.Settings.AutoUseSlots ?? []);

        // 正例对照：自动消耗品正常入槽，防止准入门禁误伤。
        var pillPayload = JsonSerializer.SerializeToElement(
            new { actionType = "exploration", slotIndex = 0, itemId = "item.qi-gathering-pill", quantity = 1, enabled = true, group = "general" },
            ProtocolJson.Options);

        var accepted = await adapter.ExecuteAsync(fixture.PlayerId, "queue.auto_use.configure", pillPayload, 0, Guid.NewGuid());

        Assert.True(accepted.Success, accepted.Message);
        Assert.Single(accepted.Snapshot.State.Settings.AutoUseSlots ?? []);
    }

    /// <summary>物品栏域词表与物品栏域准入：旧行动类型 cultivation 已并入历练栏域不再是合法标识；
    /// 物品未声明目标栏域时拒绝装配；历练栏 6 槽上限允许第 6 槽（下标 5）入槽。</summary>
    [Fact]
    public async Task AutoUseConfigureValidatesBarVocabularyAndItemBarMembership()
    {
        var fixture = CreateFixture(
            new AssetBalanceRecord(Guid.Empty, "item.qi-gathering-pill", "", 3, 1));
        await using var adapter = fixture.Adapter;

        var staleBarPayload = JsonSerializer.SerializeToElement(
            new { actionType = "cultivation", slotIndex = 0, itemId = "item.qi-gathering-pill", quantity = 1, enabled = true, group = "general" },
            ProtocolJson.Options);
        var rejectedBar = await adapter.ExecuteAsync(fixture.PlayerId, "queue.auto_use.configure", staleBarPayload, 0, Guid.NewGuid());
        Assert.False(rejectedBar.Success);
        Assert.Equal("INVALID_ACTION_TYPE", rejectedBar.Code);

        // 引气丹只声明历练栏域：炼器栏域拒绝装配。
        var wrongBarPayload = JsonSerializer.SerializeToElement(
            new { actionType = "forging", slotIndex = 0, itemId = "item.qi-gathering-pill", quantity = 1, enabled = true, group = "general" },
            ProtocolJson.Options);
        var rejectedItem = await adapter.ExecuteAsync(fixture.PlayerId, "queue.auto_use.configure", wrongBarPayload, 0, Guid.NewGuid());
        Assert.False(rejectedItem.Success);
        Assert.Equal("INVALID_CONSUMABLE", rejectedItem.Code);

        var sixthSlotPayload = JsonSerializer.SerializeToElement(
            new { actionType = "exploration", slotIndex = 5, itemId = "item.qi-gathering-pill", quantity = 1, enabled = true, group = "general" },
            ProtocolJson.Options);
        var accepted = await adapter.ExecuteAsync(fixture.PlayerId, "queue.auto_use.configure", sixthSlotPayload, 0, Guid.NewGuid());
        Assert.True(accepted.Success, accepted.Message);
        Assert.Single(accepted.Snapshot.State.Settings.AutoUseSlots ?? []);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2.5)]
    public async Task AbilityLearnRejectsNonPositiveOrFractionalQuantity(double quantity)
    {
        var fixture = CreateFixture(new AssetBalanceRecord(PlayerId: Guid.Empty, AssetId: "item.tome.heavy-slash", Variant: "", Quantity: 5, Version: 1));
        await using var adapter = fixture.Adapter;
        var payload = JsonSerializer.SerializeToElement(new { itemId = "item.tome.heavy-slash", quantity }, ProtocolJson.Options);

        var result = await adapter.ExecuteAsync(fixture.PlayerId, "ability.learn", payload, 0, Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal("INVALID_QUANTITY", result.Code);
        Assert.Empty(fixture.Economy.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedSimulationPublishesMappedLogsAndSnapshotOutsidePlayerLock(bool batch)
    {
        var fixture = CreateFixture();
        await using var adapter = fixture.Adapter;
        var before = await EnqueueGatheringAsync(fixture);
        var expectedPlayer = PlayerAggregate.Restore(before.State);
        var expected = SimulationEngine.AdvanceTo(expectedPlayer, Epoch.AddSeconds(37), fixture.Catalog,
            new AssetWallet(expectedPlayer.PlayerId));
        var expectedBuffer = new PlayerLogBuffer(200);
        var expectedLogs = SimulationLogMapper.ToLogEvents(expected.Events, fixture.Catalog, expectedBuffer);
        Assert.NotEmpty(expectedLogs);
        var order = new List<string>();
        IReadOnlyList<LogEventDto>? emitted = null;
        adapter.LogBatchEmitted += async (playerId, logs) =>
        {
            Assert.Equal(fixture.PlayerId, playerId);
            // 真实订阅者可重入读快照；若回调仍在玩家锁内，此处会在超时后失败。
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var snapshot = await adapter.LoadAsync(playerId, timeout.Token);
            await adapter.FlushAsync(playerId, true, timeout.Token);
            Assert.Equal(snapshot.StateVersion, fixture.Sections.LatestSections.Values.Max(section => section.StateVersion));
            Assert.Equal(expectedBuffer.SnapshotRecent(), adapter.GetRecentLogEvents(playerId));
            emitted = logs;
            order.Add("日志");
        };
        adapter.SnapshotUpdated += snapshot =>
        {
            Assert.Equal(expectedPlayer.StateVersion, snapshot.StateVersion);
            order.Add("快照");
            return ValueTask.CompletedTask;
        };

        var result = await AdvanceAsync(fixture, Epoch.AddSeconds(37), batch);

        Assert.Equal(expectedLogs, emitted);
        Assert.Equal(new[] { "日志", "快照" }, order);
        if (!batch) Assert.Equal(expectedLogs, result!.LogEvents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSimulationPreservesSnapshotLogsAndSequenceUntilRetry(bool batch)
    {
        var fixture = CreateFixture();
        await using var adapter = fixture.Adapter;
        var seed = await EnqueueGatheringAsync(fixture);
        var expectedPlayer = PlayerAggregate.Restore(seed.State);
        var expectedWallet = new AssetWallet(expectedPlayer.PlayerId);
        var expectedBuffer = new PlayerLogBuffer(200);
        var first = SimulationEngine.AdvanceTo(expectedPlayer, Epoch.AddSeconds(37), fixture.Catalog, expectedWallet);
        foreach (var delta in first.AssetDeltas) expectedWallet.Apply(delta);
        SimulationLogMapper.ToLogEvents(first.Events, fixture.Catalog, expectedBuffer);
        await AdvanceAsync(fixture, Epoch.AddSeconds(37), batch);
        var before = await adapter.LoadAsync(fixture.PlayerId);
        var logsBefore = adapter.GetRecentLogEvents(fixture.PlayerId);
        var emitted = new List<LogEventDto>();
        var snapshots = new List<PlayerRuntimeSnapshot>();
        adapter.LogBatchEmitted += (_, logs) => { emitted.AddRange(logs); return ValueTask.CompletedTask; };
        adapter.SnapshotUpdated += snapshot => { snapshots.Add(snapshot); return ValueTask.CompletedTask; };
        fixture.Economy.FailNextCommit = true;
        var requestId = Guid.NewGuid();

        await Assert.ThrowsAsync<IOException>(async () => await AdvanceAsync(fixture, Epoch.AddSeconds(74), batch, requestId));

        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await adapter.LoadAsync(fixture.PlayerId)));
        Assert.Equal(logsBefore, adapter.GetRecentLogEvents(fixture.PlayerId));
        Assert.Empty(emitted);
        Assert.Empty(snapshots);
        var second = SimulationEngine.AdvanceTo(expectedPlayer, Epoch.AddSeconds(74), fixture.Catalog, expectedWallet);
        var expectedLogs = SimulationLogMapper.ToLogEvents(second.Events, fixture.Catalog, expectedBuffer);

        await AdvanceAsync(fixture, Epoch.AddSeconds(74), batch, requestId);

        Assert.Equal(expectedLogs, emitted);
        Assert.Equal(expectedBuffer.SnapshotRecent(), adapter.GetRecentLogEvents(fixture.PlayerId));
        Assert.Single(snapshots);
        Assert.Equal(JsonSerializer.Serialize(expectedPlayer.ExportState()), JsonSerializer.Serialize(snapshots[0].State));
    }

    [Fact]
    public async Task OfflineLoadFailureLeavesNoLogsAndRetryCommitsBeforePublishing()
    {
        var fixture = CreateFixtureCore(Epoch.AddSeconds(37), initialAction: "explore.gather-ningxue-grass");
        await using var adapter = fixture.Adapter;
        var expectedPlayer = PlayerAggregate.Restore(fixture.InitialState);
        var expected = SimulationEngine.AdvanceTo(expectedPlayer, Epoch.AddSeconds(37), fixture.Catalog,
            new AssetWallet(expectedPlayer.PlayerId));
        var expectedBuffer = new PlayerLogBuffer(200);
        var expectedLogs = SimulationLogMapper.ToLogEvents(expected.Events, fixture.Catalog, expectedBuffer);
        var emitted = new List<LogEventDto>();
        adapter.LogBatchEmitted += async (playerId, logs) =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var snapshot = await adapter.LoadAsync(playerId, timeout.Token);
            Assert.Equal(PresenceState.Online, snapshot.State.Presence);
            await adapter.FlushAsync(playerId, true, timeout.Token);
            Assert.Equal(Epoch.AddSeconds(37), SectionCodec.DecodeAggregate((await fixture.Sections.LoadAsync(playerId))!).LastSimulatedAt);
            emitted.AddRange(logs);
        };
        fixture.Economy.FailNextCommit = true;

        await Assert.ThrowsAsync<IOException>(async () => await adapter.LoadAsync(fixture.PlayerId));

        Assert.Equal(0, adapter.LoadedCount);
        Assert.Empty(adapter.GetRecentLogEvents(fixture.PlayerId));
        Assert.Empty(emitted);
        var loaded = await adapter.LoadAsync(fixture.PlayerId);
        expectedPlayer.SetPresence(PresenceState.Online);
        Assert.Equal(JsonSerializer.Serialize(expectedPlayer.ExportState()), JsonSerializer.Serialize(loaded.State));
        Assert.Equal(expectedLogs, emitted);
        Assert.Equal(expectedBuffer.SnapshotRecent(), adapter.GetRecentLogEvents(fixture.PlayerId));
    }

    [Fact]
    public async Task SimulationUsesOopTransactionDeltasIdentityAndDurableAssetVersions()
    {
        var fixture = CreateFixture(new AssetBalanceRecord(Guid.Empty, "item.ningxue-grass", "", 3, 4));
        await using var adapter = fixture.Adapter;
        var before = await EnqueueGatheringAsync(fixture);
        var expectedPlayer = PlayerAggregate.Restore(before.State);
        var wallet = new AssetWallet(expectedPlayer.PlayerId);
        wallet.Restore(before.Assets.Select(row => new AssetBalance(expectedPlayer.PlayerId,
            new AssetKey(new ItemId(row.AssetId), row.Variant), row.Quantity, row.Version)));
        var expectedSimulation = SimulationEngine.AdvanceTo(expectedPlayer, Epoch.AddSeconds(37), fixture.Catalog, wallet);
        var expectedRequest = SimulationEconomyTransaction.Create(fixture.PlayerId, expectedSimulation,
            new DeterministicSimulationTransactionIdentity());

        var result = await AdvanceAsync(fixture, Epoch.AddSeconds(37), batch: false);

        var actual = Assert.Single(fixture.Economy.Requests);
        Assert.Equal(expectedRequest.TransactionId, actual.TransactionId);
        Assert.Equal(expectedRequest.IdempotencyKey, actual.IdempotencyKey);
        Assert.Equal(expectedRequest.TransactionType, actual.TransactionType);
        Assert.Equal(expectedRequest.OccurredAt, actual.OccurredAt);
        Assert.Equal(expectedRequest.Deltas, actual.Deltas);
        Assert.Equal(expectedRequest.ResultPayload.ToArray(), actual.ResultPayload.ToArray());
        Assert.NotEmpty(actual.Sections!);
        Assert.Contains(actual.Sections!, section => section.SectionKind == PlayerSectionKinds.Progression
            && section.LastSimulatedAt == Epoch.AddSeconds(37));
        Assert.Contains(actual.Receipts!, receipt => receipt.Command == "simulation.advance");
        Assert.NotNull(result!.Simulation);
        var balance = result.Snapshot.Assets.Single(row => row.AssetId == "item.ningxue-grass");
        // 15 = 初始 3 + 四轮产出 12（fixture 玩家引导已终态，无首轮赠送）。
        Assert.Equal(15, balance.Quantity);
        Assert.Equal(5, balance.Version);
        await adapter.RemoveAsync(fixture.PlayerId, true);
        var reloaded = await adapter.LoadAsync(fixture.PlayerId);
        Assert.Equal(balance, reloaded.Assets.Single(row => row.AssetId == balance.AssetId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnloadMatchesOopOfflineLedgerAndPreservesSuspended(bool suspended)
    {
        var fixture = CreateFixtureCore(Epoch.AddSeconds(37), "explore.gather-ningxue-grass");
        await using var adapter = fixture.Adapter;
        var loaded = await adapter.LoadAsync(fixture.PlayerId);
        Assert.NotEmpty(loaded.State.Duel.OfflineReport!.Items);
        if (suspended)
        {
            var requestId = Guid.NewGuid();
            var presence = await adapter.ExecuteAsync(fixture.PlayerId, "player.presence",
                JsonSerializer.SerializeToElement(new { requestId, presence = "suspended" }), 0, requestId);
            loaded = presence.Snapshot;
        }
        var expected = PlayerAggregate.Restore(loaded.State);
        if (!suspended) expected.SetPresence(PresenceState.Offline);

        await adapter.RemoveAsync(fixture.PlayerId, true);

        var actual = SectionCodec.DecodeAggregate((await fixture.Sections.LoadAsync(fixture.PlayerId))!);
        Assert.Equal(JsonSerializer.Serialize(expected.ExportState()), JsonSerializer.Serialize(actual));
        Assert.Equal(0, adapter.LoadedCount);
    }

    [Fact]
    public async Task NodeProjectionAndOfflineFlushMatchOopWorldLedgerAndVersion()
    {
        var fixture = CreateFixtureCore(Epoch, "explore.gather-ningxue-grass");
        await using var adapter = fixture.Adapter;
        var initial = await adapter.LoadAsync(fixture.PlayerId);
        var expected = PlayerAggregate.Restore(initial.State);
        // OOP RefreshNodeBuildingsAsync 的空节点投影：只标 World 脏，不推进业务版本。
        expected.World.NodeBuildings = [];
        expected.World.NodeBuildingEffects = BuildingEffectSet.Empty;
        expected.World.NodeBuildingNodeId = expected.World.NodeId;
        expected.World.NodeBuildingUsages = [];
        expected.MarkDirty(PlayerSectionKind.World);

        Assert.True(fixture.Ecs.ApplyNodeBuildingProjection(fixture.PlayerId,
            new(0, [], BuildingEffectSet.Empty, expected.World.NodeId, []), Epoch));
        Assert.Equal(initial.StateVersion, fixture.Ecs.ExportSnapshot(fixture.PlayerId).State.StateVersion);
        Assert.False(fixture.Ecs.ApplyNodeBuildingProjection(fixture.PlayerId,
            new(0, [], BuildingEffectSet.Empty, expected.World.NodeId, []), Epoch));
        SimulationEngine.AdvanceTo(expected, Epoch.AddSeconds(3.6), fixture.Catalog, new AssetWallet(expected.PlayerId));
        await AdvanceAsync(fixture, Epoch.AddSeconds(3.6), batch: false);
        expected.SetPresence(PresenceState.Offline);
        expected.MarkDirty(PlayerSectionKind.Progression);

        await adapter.RemoveAsync(fixture.PlayerId, true);

        var persisted = SectionCodec.DecodeAggregate((await fixture.Sections.LoadAsync(fixture.PlayerId))!);
        Assert.Equal(expected.World.NodeId, persisted.World.NodeBuildingNodeId);
        Assert.Equal(expected.StateVersion, persisted.StateVersion);
        Assert.Equal(JsonSerializer.Serialize(expected.ExportState()), JsonSerializer.Serialize(persisted));
    }

    [Fact]
    public async Task InvalidPresenceIsRejectedWithoutChangingOnlineState()
    {
        var fixture = CreateFixture();
        await using var adapter = fixture.Adapter;
        var before = await adapter.LoadAsync(fixture.PlayerId);
        var requestId = Guid.NewGuid();

        var rejected = await adapter.ExecuteAsync(fixture.PlayerId, "player.presence",
            JsonSerializer.SerializeToElement(new { requestId, presence = "unexpected" }), 0, requestId);

        Assert.False(rejected.Success);
        Assert.Equal("INVALIDARGUMENT", rejected.Code);
        Assert.Equal(before.StateVersion, rejected.Snapshot.StateVersion);
        Assert.Equal(PresenceState.Online, rejected.Snapshot.State.Presence);
    }

    [Fact]
    public async Task FlushUsesDirtySectionsAndRetriesFailedDurableQueueWithoutLosingReceipt()
    {
        var fixture = CreateFixture();
        await using var adapter = fixture.Adapter;
        await adapter.LoadAsync(fixture.PlayerId);
        await adapter.FlushAsync(fixture.PlayerId, false);
        Assert.Equal(0, fixture.Sections.EnqueueCount);
        await adapter.FlushAsync(fixture.PlayerId, true);
        Assert.Single(fixture.Sections.LastWrites);
        Assert.Equal(PlayerSectionKinds.Settings, fixture.Sections.LastWrites.Single().SectionKind);
        var originalWorld = fixture.Sections.LatestSections[PlayerSectionKinds.World];
        await EnqueueGatheringAsync(fixture);
        fixture.Sections.FailNextEnqueue = true;

        await Assert.ThrowsAsync<IOException>(async () => await adapter.FlushAsync(fixture.PlayerId, false));
        await adapter.FlushAsync(fixture.PlayerId, false);

        Assert.Single(fixture.Sections.LastWrites);
        Assert.Equal(PlayerSectionKinds.ActionQueue, fixture.Sections.LastWrites.Single().SectionKind);
        Assert.Single(fixture.Sections.Receipts);
        Assert.Equal(originalWorld, fixture.Sections.LatestSections[PlayerSectionKinds.World]);
        var enqueues = fixture.Sections.EnqueueCount;
        await adapter.FlushAsync(fixture.PlayerId, true);
        Assert.Equal(enqueues, fixture.Sections.EnqueueCount);
    }

    [Fact]
    public async Task BatchSkipsExpiredTargetWithoutRollingBackOrAddingTransaction()
    {
        var fixture = CreateFixture();
        await using var adapter = fixture.Adapter;
        await EnqueueGatheringAsync(fixture);
        await AdvanceAsync(fixture, Epoch.AddSeconds(37), batch: false);
        var before = adapter.TryGetLoaded(fixture.PlayerId)!;
        var transactions = fixture.Economy.Requests.Count;

        await adapter.AdvanceAllAsync(Epoch.AddSeconds(1));

        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(adapter.TryGetLoaded(fixture.PlayerId)));
        Assert.Equal(transactions, fixture.Economy.Requests.Count);
    }

    [Theory]
    [InlineData("sections")]
    [InlineData("wallet")]
    [InlineData("committed-wallet")]
    public async Task LoadFailureCleansPartialRuntimeAndLeaseBeforeReload(string failure)
    {
        var ownership = new MemoryOwnership();
        var fixture = CreateOwnedFixture(Epoch.AddSeconds(37), "explore.gather-ningxue-grass", true, ownership);
        await using var adapter = fixture.Adapter;
        if (failure == "sections") fixture.Sections.FailNextLoad = true;
        else fixture.Assets.FailOnLoadCall = failure == "wallet" ? 1 : 2;
        // 同步钱包故障可发生在 durable 提交成功、_loaded 已暂存新时钟之后。
        fixture.Economy.ReturnBalances = failure != "committed-wallet";

        await Assert.ThrowsAsync<IOException>(async () => await adapter.LoadAsync(fixture.PlayerId));

        Assert.Null(adapter.TryGetLoaded(fixture.PlayerId));
        Assert.Throws<KeyNotFoundException>(() => fixture.Ecs.ExportSnapshot(fixture.PlayerId));
        Assert.Equal(1, ownership.ReleaseCalls);
        Assert.Empty(adapter.GetRecentLogEvents(fixture.PlayerId));
        var committed = failure == "committed-wallet" ? Assert.Single(fixture.Economy.Requests) : null;
        var restored = await adapter.LoadAsync(fixture.PlayerId);
        Assert.Equal(PresenceState.Online, restored.State.Presence);
        Assert.Equal(Epoch.AddSeconds(37), restored.LastSimulatedAt);
        Assert.Equal(2, ownership.AcquireCalls);
        if (committed is not null) Assert.Same(committed, Assert.Single(fixture.Economy.Requests));
    }

    [Fact]
    public async Task UnknownCommitDuringLoadRetainsEntityAndLeaseForRecovery()
    {
        var ownership = new MemoryOwnership();
        var fixture = CreateOwnedFixture(Epoch.AddSeconds(37), "explore.gather-ningxue-grass", true, ownership);
        await using var adapter = fixture.Adapter;
        fixture.Economy.CommitThenUnknown = true;
        fixture.Economy.FailConfirmation = true;

        var unknown = await Assert.ThrowsAsync<EconomyCommitOutcomeUnknownException>(async () => await adapter.LoadAsync(fixture.PlayerId));

        Assert.NotNull(adapter.TryGetLoaded(fixture.PlayerId));
        Assert.Equal(0, ownership.ReleaseCalls);
        var frozen = JsonSerializer.Serialize(fixture.Ecs.ExportSnapshot(fixture.PlayerId).State);
        await adapter.AdvanceAllAsync(Epoch.AddSeconds(74));
        Assert.Equal(frozen, JsonSerializer.Serialize(fixture.Ecs.ExportSnapshot(fixture.PlayerId).State));
        Assert.Same(unknown, await Assert.ThrowsAsync<EconomyCommitOutcomeUnknownException>(async () => await adapter.LoadAsync(fixture.PlayerId)));
    }

    [Fact]
    public async Task BatchRechecksLoadedPlayersAfterWaitingForUnloadGate()
    {
        var fixture = CreateFixture();
        await using var adapter = fixture.Adapter;
        await adapter.LoadAsync(fixture.PlayerId);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Sections.BeforeEnqueueAsync = async () =>
        {
            entered.SetResult();
            await release.Task;
        };
        // 卸载持有 gate 等待刷盘；批次取样该玩家后只能等卸载完成再取得 gate。
        var unloading = adapter.RemoveAsync(fixture.PlayerId, true).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var advancing = adapter.AdvanceAllAsync(Epoch.AddSeconds(37)).AsTask();
        Assert.False(advancing.IsCompleted);
        release.SetResult();
        await unloading.WaitAsync(TimeSpan.FromSeconds(5));
        await advancing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(adapter.TryGetLoaded(fixture.PlayerId));
        Assert.Empty(fixture.Economy.Requests);
    }

    [Fact]
    public async Task BatchDoesNotAdvanceEntityWhoseLoadStillOwnsItsGate()
    {
        var fixture = CreateFixtureCore(Epoch.AddSeconds(37), null);
        await using var adapter = fixture.Adapter;
        await adapter.LoadAsync(fixture.PlayerId);
        var newId = Guid.Parse("10000000-0000-4000-8000-000000000099");
        var newPlayer = PlayerAggregate.Create(new PlayerId(newId), fixture.InitialState.AccountId,
            "装载中玩家", Epoch, fixture.Catalog.Fingerprint);
        newPlayer.SkipTutorial();
        newPlayer.SetPresence(PresenceState.Offline);
        fixture.Sections.Register(ToBundle(newPlayer.ExportState()));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Economy.BeforeCommitAsync = async request =>
        {
            if (request.ActorId != newId) return;
            entered.SetResult();
            await release.Task;
        };
        var loading = adapter.LoadAsync(newId).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(adapter.TryGetLoaded(newId));
        try
        {
            await adapter.AdvanceAllAsync(Epoch.AddSeconds(74));
            Assert.Equal(Epoch.AddSeconds(37), fixture.Ecs.ExportSnapshot(newId).State.LastSimulatedAt);
            Assert.Null(fixture.Ecs.DrainSimulationResult(newId));
        }
        finally
        {
            release.SetResult();
            await loading.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Economy.BeforeCommitAsync = null;
        }
        Assert.Equal(Epoch.AddSeconds(37), adapter.TryGetLoaded(newId)!.LastSimulatedAt);
    }

    private static async Task<PlayerRuntimeSnapshot> EnqueueGatheringAsync(Fixture fixture)
    {
        var requestId = Guid.Parse("10000000-0000-4000-8000-000000000025");
        var payload = JsonSerializer.SerializeToElement(new ActionIntentRequest(
            requestId.ToString("D"), "explore.gather-ningxue-grass", -1), ProtocolJson.Options);
        var result = await fixture.Adapter.ExecuteAsync(fixture.PlayerId, "action.enqueue", payload, 0, requestId);
        Assert.True(result.Success, result.Message);
        return result.Snapshot;
    }

    private static async Task<PlayerCommandResult?> AdvanceAsync(Fixture fixture, DateTimeOffset target, bool batch,
        Guid? requestId = null)
    {
        if (batch)
        {
            await fixture.Adapter.AdvanceAllAsync(target);
            return null;
        }
        var payload = JsonSerializer.SerializeToElement(new { targetTime = target }, ProtocolJson.Options);
        var result = await fixture.Adapter.ExecuteAsync(fixture.PlayerId, "simulation.advance", payload, 0, requestId ?? Guid.NewGuid());
        Assert.True(result.Success, result.Message);
        return result;
    }

    private static Fixture CreateFixture(params AssetBalanceRecord[] balances)
        => CreateFixtureCore(Epoch, null, balances);

    private static Fixture CreateFixtureCore(DateTimeOffset clockAt, string? initialAction, params AssetBalanceRecord[] balances)
        => CreateFixtureWithTutorial(clockAt, initialAction, true, balances);

    private static Fixture CreateFixtureWithTutorial(DateTimeOffset clockAt, string? initialAction, bool skipTutorial,
        params AssetBalanceRecord[] balances)
        => CreateOwnedFixture(clockAt, initialAction, skipTutorial, null, balances);

    private static Fixture CreateOwnedFixture(DateTimeOffset clockAt, string? initialAction, bool skipTutorial,
        IPlayerOwnershipStore? ownership, params AssetBalanceRecord[] balances)
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.Parse("10000000-0000-4000-8000-000000000023");
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId),
            new AccountId(Guid.Parse("20000000-0000-4000-8000-000000000023")), "适配器测试", Epoch, catalog.Fingerprint);
        // 适配器边界测试与引导语义无关：统一终态，避免教程里程碑推进对绑定行动的
        // 强制出队截断干扰无限重复类 parity 断言。
        if (skipTutorial) aggregate.SkipTutorial();
        if (initialAction is not null) aggregate.Enqueue(new(new ActionId(initialAction), -1), Epoch, catalog.Actions);
        aggregate.SetPresence(PresenceState.Offline);
        var initialState = aggregate.ExportState();
        var sections = new MemorySections(ToBundle(initialState));
        var assets = new MemoryAssets(balances.Length == 0
            ? []
            : balances.Select(balance => balance with { PlayerId = playerId }).ToArray());
        var economy = new MemoryEconomy(sections, assets);
        var clock = new FixedClock(clockAt);
        var ecs = new EcsGameRuntime(catalog, Epoch);
        var adapter = new EcsPlayerRuntimeAdapter(ecs, sections, assets, economy, catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock, simulationTransactionIdentity: new DeterministicSimulationTransactionIdentity(), ownership: ownership);
        return new Fixture(playerId, adapter, sections, economy, catalog, initialState, ecs, assets);
    }

    /// <summary>
    /// 装配含会话对账依赖的适配器：会话宿主桩每次成员推进都调用真实会话引擎产出一条新结算，
    /// 与生产中「命令前推进会话 + 消费收件箱」的版本推进行为一致，用于回归版本基线口径。
    /// </summary>
    private static Fixture CreateParticipationFixture(params AssetBalanceRecord[] balances)
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.Parse("10000000-0000-4000-8000-000000000023");
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId),
            new AccountId(Guid.Parse("20000000-0000-4000-8000-000000000023")), "适配器测试", Epoch, catalog.Fingerprint);
        aggregate.SkipTutorial();
        aggregate.SetPresence(PresenceState.Offline);
        var frozen = aggregate.CaptureActionParticipantSnapshot(catalog);
        var spec = new ActionParticipationSpec("session.adapter-boundary", "encounter.qi-beast",
            aggregate.World.NodeId, 91, 0, Epoch.ToUnixTimeSeconds(), [frozen])
        {
            ActionId = new("combat.hunt-qi-beast"),
        };
        var initialState = aggregate.ExportState();
        var sections = new MemorySections(ToBundle(initialState));
        var assets = new MemoryAssets(balances.Length == 0
            ? []
            : balances.Select(balance => balance with { PlayerId = playerId }).ToArray());
        var economy = new MemoryEconomy(sections, assets);
        var clock = new FixedClock(Epoch);
        var ecs = new EcsGameRuntime(catalog, Epoch);
        var adapter = new EcsPlayerRuntimeAdapter(ecs, sections, assets, economy, catalog,
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock,
            simulationTransactionIdentity: new DeterministicSimulationTransactionIdentity(),
            socialRepository: new SessionSocialStub(new PartySessionRecord(spec.SessionId, Guid.NewGuid(),
                spec.EncounterId, spec.NodeId, Epoch, [playerId],
                JsonSerializer.Serialize(spec, PersistenceJson.Options))),
            participationRuntime: new SessionAdvanceStub(spec, catalog),
            participationSessions: new SessionStoreStub());
        return new Fixture(playerId, adapter, sections, economy, catalog, initialState, ecs, assets);
    }

    private static PlayerStateBundle ToBundle(PlayerAggregateState state)
    {
        var writes = SectionCodec.EncodeAggregateFull(state, state.LastSimulatedAt);
        var sections = writes.ToDictionary(write => write.SectionKind, ToStored, StringComparer.Ordinal);
        return new PlayerStateBundle(
            new PlayerIdentity(state.PlayerId.Value, state.AccountId.Value, state.DisplayName, state.CreatedAt),
            new PlayerStateHead(state.PlayerId.Value, state.StateVersion, SectionCodec.CurrentSchemaVersion,
                state.LastSimulatedAt, state.GameDataFingerprint, SectionCodec.ComputeHeadChecksum(sections.Values), state.LastSimulatedAt),
            sections, []);
    }

    private static StoredSection ToStored(SectionWrite write)
        => new(write.PlayerId, write.SectionKind, write.StateVersion, write.SchemaVersion, write.Codec,
            write.Payload.ToArray(), write.RawBytes, write.Checksum, write.UpdatedAt);

    private sealed record Fixture(Guid PlayerId, EcsPlayerRuntimeAdapter Adapter, MemorySections Sections,
        MemoryEconomy Economy, GameDataCatalog Catalog, PlayerAggregateState InitialState, EcsGameRuntime Ecs, MemoryAssets Assets);

    private sealed class MemoryOwnership : IPlayerOwnershipStore
    {
        public bool AllowRenew { get; set; } = true;
        public int AcquireCalls { get; private set; }
        public int RenewCalls { get; private set; }
        public int ReleaseCalls { get; private set; }
        public ValueTask<bool> AcquireAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default)
        {
            AcquireCalls++;
            return ValueTask.FromResult(true);
        }
        public ValueTask<bool> RenewAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default)
        {
            RenewCalls++;
            return ValueTask.FromResult(AllowRenew);
        }
        public ValueTask ReleaseAsync(Guid playerId, Guid ownerInstanceId, CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset value) : IPlayerSimulationClock
    {
        public DateTimeOffset UtcNow => value;
        public bool IsDeterministic => true;
        public void AdvanceTo(DateTimeOffset target) { }
    }

    private sealed class MemorySections(PlayerStateBundle initial) : IPlayerStateRepository
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, PlayerStateBundle> _bundles = new() { [initial.Identity.PlayerId] = initial };
        public bool FailNextLoad { get; set; }
        public bool FailNextEnqueue { get; set; }
        public Func<ValueTask>? BeforeEnqueueAsync { get; set; }
        public int EnqueueCount { get; private set; }
        public IReadOnlyCollection<SectionWrite> LastWrites { get; private set; } = [];
        public List<CommandReceipt> Receipts { get; } = [];
        public IReadOnlyDictionary<string, StoredSection> LatestSections { get { lock (_sync) return _bundles[initial.Identity.PlayerId].Sections; } }
        public void Register(PlayerStateBundle bundle) { lock (_sync) _bundles.Add(bundle.Identity.PlayerId, bundle); }
        public ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default)
        {
            if (FailNextLoad) { FailNextLoad = false; throw new IOException("注入装载分段读取失败。"); }
            lock (_sync) return ValueTask.FromResult(_bundles.GetValueOrDefault(playerId));
        }
        public async ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default)
        {
            if (BeforeEnqueueAsync is { } beforeEnqueue) await beforeEnqueue();
            lock (_sync)
            {
                if (FailNextEnqueue) { FailNextEnqueue = false; throw new IOException("注入分段队列写入失败。"); }
                EnqueueCount++;
                LastWrites = sections.ToArray();
                if (sections.Count == 0) return;
                var first = sections.First();
                var bundle = _bundles[first.PlayerId];
                var values = bundle.Sections.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                foreach (var write in sections) values[write.SectionKind] = ToStored(write);
                _bundles[first.PlayerId] = bundle with
                {
                    Head = new PlayerStateHead(first.PlayerId, sections.Max(section => section.StateVersion), first.SchemaVersion,
                        sections.Max(section => section.LastSimulatedAt ?? first.UpdatedAt), first.GameDataFingerprint,
                        SectionCodec.ComputeHeadChecksum(values.Values), first.UpdatedAt),
                    Sections = values
                };
            }
        }
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
        public async ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> writes,
            IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default)
        {
            await EnqueueSectionsAsync(writes, cancellationToken);
            Receipts.AddRange(receipts);
        }
    }

    private sealed class MemoryAssets(params AssetBalanceRecord[] values) : IAssetBalanceRepository
    {
        private readonly ConcurrentDictionary<(Guid, string, string), AssetBalanceRecord> _rows = new(
            values.ToDictionary(value => (value.PlayerId, value.AssetId, value.Variant)));
        public int FailOnLoadCall { get; set; }
        private int _loadCalls;
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default)
        {
            if (++_loadCalls == FailOnLoadCall) throw new IOException("注入权威钱包读取失败。");
            return ValueTask.FromResult(ReadBalances(playerId));
        }
        public IReadOnlyList<AssetBalanceRecord> ReadBalances(Guid playerId)
            => _rows.Values.Where(value => value.PlayerId == playerId).ToArray();
        public void Apply(IEnumerable<Server.Clean.AssetDelta> deltas)
        {
            foreach (var delta in deltas)
            {
                var key = (delta.PlayerId, delta.AssetId, delta.Variant);
                var old = _rows.TryGetValue(key, out var value) ? value : new AssetBalanceRecord(delta.PlayerId, delta.AssetId, delta.Variant, 0, 0);
                _rows[key] = old with { Quantity = old.Quantity + delta.Delta, Version = old.Version + 1 };
            }
        }
    }

    private sealed class MemoryEconomy(MemorySections sections, MemoryAssets assets) : IEconomyTransactionStore
    {
        private readonly ConcurrentDictionary<string, EconomyTransactionResult> _committed = new();
        public List<EconomyTransactionRequest> Requests { get; } = [];
        public bool FailNextCommit { get; set; }
        public bool CommitThenUnknown { get; set; }
        public bool FailConfirmation { get; set; }
        public bool ReturnBalances { get; set; } = true;
        public Func<EconomyTransactionRequest, ValueTask>? BeforeCommitAsync { get; set; }
        public int Calls { get; private set; }
        public List<EconomyTransactionRequest> Attempts { get; } = [];
        public async ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
        {
            if (BeforeCommitAsync is { } beforeCommit) await beforeCommit(request);
            Calls++;
            Attempts.Add(request);
            if (FailConfirmation && Calls > 1) throw new IOException("注入结局核验不可达。");
            if (FailNextCommit)
            {
                FailNextCommit = false;
                throw new IOException("注入资产事务失败，验证未提交日志不可见。");
            }
            if (_committed.TryGetValue(request.IdempotencyKey, out var previous)) return previous with { AlreadyCommitted = true };
            Requests.Add(request);
            if (request.Sections is not null || request.Receipts is not null)
                await sections.EnqueueSectionsAsync(request.Sections ?? [], request.Receipts ?? [], cancellationToken);
            assets.Apply(request.Deltas);
            var result = new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false,
                DateTimeOffset.UtcNow, request.ResultPayload, ReturnBalances ? assets.ReadBalances(request.ActorId) : null);
            _committed[request.IdempotencyKey] = result;
            if (CommitThenUnknown)
            {
                CommitThenUnknown = false;
                throw new EconomyCommitOutcomeUnknownException(request.TransactionId, new IOException("注入 COMMIT 回包丢失。"));
            }
            return result;
        }
    }

    /// <summary>
    /// 会话宿主桩：每次成员推进都用真实会话引擎产出一条新个人结算并以收件箱条目回投，
    /// 与生产宿主「命令前推进会话 + 投递收件箱」的版本推进行为一致。
    /// </summary>
    private sealed class SessionAdvanceStub(ActionParticipationSpec spec, GameDataCatalog catalog)
        : IActionParticipationRuntime
    {
        private ActionParticipationSessionEngine? _engine;
        public DateTimeOffset? NextAdvanceBoundaryUtc => null;
        public bool RequiresReconciliation(Guid playerId) => true;
        public ValueTask CreateAsync(Guid partyId, ActionParticipationSpec sessionSpec,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask AdvanceOnlineAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask EndAsync(string sessionId, DateTimeOffset at, string reason,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<ParticipationInboxItem>> LoadPendingAsync(Guid playerId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ParticipationInboxItem>>([]);
        public ValueTask<IReadOnlyList<ParticipationInboxItem>> AdvanceForMemberAsync(Guid playerId,
            DateTimeOffset target, CancellationToken cancellationToken = default)
        {
            _engine ??= ActionParticipationSessionEngine.Create(spec, catalog);
            var settlement = _engine.AdvanceTo(target).Single();
            return ValueTask.FromResult<IReadOnlyList<ParticipationInboxItem>>(
                [new ParticipationInboxItem(spec.SessionId, playerId, settlement.Sequence,
                    JsonSerializer.Serialize(settlement, PersistenceJson.Options), settlement.To)]);
        }
    }

    /// <summary>会话检查点存储桩：适配器只做非空校验，本条链路不触达。</summary>
    private sealed class SessionStoreStub : IParticipationSessionStore
    {
        public ValueTask<ActionParticipationSessionRecord?> LoadAsync(string sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ActionParticipationSessionRecord>> ListActiveAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ActionParticipationSessionRecord>> ListActiveForMemberAsync(
            Guid playerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> GetInvalidationReasonAsync(string sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<DateTimeOffset?> GetOfflineExpiryAsync(string sessionId, TimeSpan offlineLimit,
            IReadOnlyCollection<Guid> onlinePlayerIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<ActionParticipationSessionRecord> CreateOrInitializeAsync(
            ActionParticipationSessionRecord session, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<bool> CommitAdvanceAsync(string sessionId, long expectedVersion, string stateJson,
            DateTimeOffset lastSimulatedAt, IReadOnlyList<ParticipationInboxItem> inbox,
            DateTimeOffset? endedAt = null, string? endReason = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ParticipationInboxItem>> LoadPendingAsync(Guid playerId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>社交仓储桩：仅实现会话查询，其余通道本条链路不触达。</summary>
    private sealed class SessionSocialStub(PartySessionRecord session) : ISocialRepository
    {
        public ValueTask<PartySessionRecord?> GetPartySessionAsync(string sessionId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(session.SessionId == sessionId ? session : null);
        public ValueTask<Guid?> GetPartyIdAsync(Guid playerId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Guid?>(session.PartyId);
        public ValueTask<PartySessionRecord?> GetActivePartySessionAsync(Guid partyId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(partyId == session.PartyId ? session : null);
        public ValueTask<Guid?> FindPlayerByNameAsync(string displayName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> GetDisplayNameAsync(Guid playerId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<FriendRecord>> ListFriendsAsync(Guid playerId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<FriendRequestView>> ListPendingRequestsAsync(Guid playerId,
            bool incoming, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SocialStateSnapshot> LoadStateSnapshotAsync(Guid playerId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> AreFriendsAsync(Guid left, Guid right,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryCreateFriendRequestAsync(Guid requestId, Guid requesterId, Guid targetId,
            DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryAcceptFriendRequestAsync(Guid requesterId, Guid targetId,
            DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryRejectFriendRequestAsync(Guid requesterId, Guid targetId,
            DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryRemoveFriendAsync(Guid playerId, Guid friendId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PartyRecord?> GetPartyAsync(Guid partyId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<Guid> CreatePartyAsync(Guid leaderId, PartyProfileRecord profile,
            DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryUpdatePartyProfileAsync(Guid partyId, PartyProfileRecord profile,
            DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PartyJoinResult> TryJoinPartyAsync(Guid partyId, Guid playerId,
            DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PartyMemberRecord>?> DisbandPartyAsync(Guid partyId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryKickPartyMemberAsync(Guid partyId, Guid playerId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryRemovePartyMemberAsync(Guid playerId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DeletePartyIfEmptyAsync(Guid partyId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PartyRecord>> ListPublicPartiesAsync(string activityKind,
            string nodeId, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask UpsertPartySeekerAsync(Guid playerId, string activityKind, string nodeId,
            double minValuation, DateTimeOffset now, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<bool> TryClearPartySeekerAsync(Guid playerId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PartySeekerRecord?> GetPartySeekerAsync(Guid playerId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PartySeekerRecord>> ListPartySeekersAsync(string activityKind,
            string nodeId, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask UpsertPartyInvitationAsync(Guid partyId, Guid inviteeId, Guid inviterId,
            DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PartyInvitationRecord?> GetPartyInvitationAsync(Guid inviteeId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryClearPartyInvitationAsync(Guid inviteeId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask TryTransferPartyLeaderAsync(Guid partyId, Guid leaderId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TrySetPartyMemberReadyAsync(Guid playerId, bool ready,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryUpdatePartyOpenSlotsAsync(Guid partyId, int openSlots,
            DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyDictionary<Guid, string>> GetPlayerNodeMapAsync(
            IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PartyMemberEviction>> DeleteInactivePartyMembersAsync(
            DateTimeOffset cutoff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
