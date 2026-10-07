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

    [Fact]
    public async Task ManualConsumableIsRejectedFromAutoUseSlots()
    {
        var fixture = CreateFixture(
            new AssetBalanceRecord(Guid.Empty, "item.tome.heavy-slash", "", 1, 1),
            new AssetBalanceRecord(Guid.Empty, "item.qi-gathering-pill", "", 2, 1));
        await using var adapter = fixture.Adapter;
        // 手动消耗品（category=manual）即使声明了行动域也不得进自动使用槽（含通用栏）。
        var manualPayload = JsonSerializer.SerializeToElement(
            new { actionType = "cultivation", slotIndex = 0, itemId = "item.tome.heavy-slash", quantity = 1, enabled = true, group = "general" },
            ProtocolJson.Options);

        var rejected = await adapter.ExecuteAsync(fixture.PlayerId, "queue.auto_use.configure", manualPayload, 0, Guid.NewGuid());

        Assert.False(rejected.Success);
        Assert.Equal("INVALID_CONSUMABLE", rejected.Code);
        Assert.Empty(rejected.Snapshot.State.Settings.AutoUseSlots ?? []);

        // 正例对照：自动消耗品正常入槽，防止准入门禁误伤。
        var pillPayload = JsonSerializer.SerializeToElement(
            new { actionType = "cultivation", slotIndex = 0, itemId = "item.qi-gathering-pill", quantity = 1, enabled = true, group = "general" },
            ProtocolJson.Options);

        var accepted = await adapter.ExecuteAsync(fixture.PlayerId, "queue.auto_use.configure", pillPayload, 0, Guid.NewGuid());

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
        Assert.Null(actual.Sections);
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
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var playerId = Guid.Parse("10000000-0000-4000-8000-000000000023");
        var aggregate = PlayerAggregate.Create(new PlayerId(playerId),
            new AccountId(Guid.Parse("20000000-0000-4000-8000-000000000023")), "适配器测试", Epoch, catalog.Fingerprint);
        // 适配器边界测试与引导语义无关：统一终态，避免教程里程碑推进对绑定行动的
        // 强制出队截断干扰无限重复类 parity 断言。
        aggregate.SkipTutorial();
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
            NullLogger<EcsPlayerRuntimeAdapter>.Instance, clock, simulationTransactionIdentity: new DeterministicSimulationTransactionIdentity());
        return new Fixture(playerId, adapter, sections, economy, catalog, initialState, ecs);
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
        MemoryEconomy Economy, GameDataCatalog Catalog, PlayerAggregateState InitialState, EcsGameRuntime Ecs);

    private sealed class FixedClock(DateTimeOffset value) : IPlayerSimulationClock
    {
        public DateTimeOffset UtcNow => value;
        public bool IsDeterministic => true;
        public void AdvanceTo(DateTimeOffset target) { }
    }

    private sealed class MemorySections(PlayerStateBundle initial) : IPlayerStateRepository
    {
        private readonly object _sync = new();
        private PlayerStateBundle _bundle = initial;
        public bool FailNextEnqueue { get; set; }
        public int EnqueueCount { get; private set; }
        public IReadOnlyCollection<SectionWrite> LastWrites { get; private set; } = [];
        public List<CommandReceipt> Receipts { get; } = [];
        public IReadOnlyDictionary<string, StoredSection> LatestSections { get { lock (_sync) return _bundle.Sections; } }
        public ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PlayerStateBundle?>(_bundle.Identity.PlayerId == playerId ? _bundle : null);
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (FailNextEnqueue) { FailNextEnqueue = false; throw new IOException("注入分段队列写入失败。"); }
                EnqueueCount++;
                LastWrites = sections.ToArray();
                if (sections.Count == 0) return ValueTask.CompletedTask;
                var values = _bundle.Sections.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                foreach (var write in sections) values[write.SectionKind] = ToStored(write);
                var first = sections.First();
                _bundle = _bundle with
                {
                    Head = new PlayerStateHead(first.PlayerId, sections.Max(section => section.StateVersion), first.SchemaVersion,
                        sections.Max(section => section.LastSimulatedAt ?? first.UpdatedAt), first.GameDataFingerprint,
                        SectionCodec.ComputeHeadChecksum(values.Values), first.UpdatedAt),
                    Sections = values
                };
            }
            return ValueTask.CompletedTask;
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
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(_rows.Values.Where(value => value.PlayerId == playerId).ToArray());
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
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
        {
            if (FailNextCommit)
            {
                FailNextCommit = false;
                throw new IOException("注入资产事务失败，验证未提交日志不可见。");
            }
            if (_committed.TryGetValue(request.IdempotencyKey, out var previous)) return ValueTask.FromResult(previous with { AlreadyCommitted = true });
            Requests.Add(request);
            assets.Apply(request.Deltas);
            if (request.Sections is { Count: > 0 }) sections.EnqueueSectionsAsync(request.Sections, cancellationToken).GetAwaiter().GetResult();
            var result = new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload, assets.LoadBalancesAsync(request.ActorId).GetAwaiter().GetResult());
            _committed[request.IdempotencyKey] = result;
            return ValueTask.FromResult(result);
        }
    }
}
