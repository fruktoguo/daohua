using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Server.Clean;
using Xunit;
using StoredHead = Domain.Persistence.PlayerStateHead;

namespace Server.Tests;

/// <summary>
/// 根基凝练服务端命令与协议投影门禁（TASK-20261119-01）：
/// 验证 progression.genji 的批量/单次凝练、属性增量标记、幂等重放与快照字段投影。
/// </summary>
public sealed class GenjiCommandTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameDataCatalog Catalog = GameDataCatalog.LoadEmbedded();

    [Fact]
    public async Task CondenseGenjiCommand_DeductsExpMarksWireAttributesChangedAndReplaysIdempotently()
    {
        var playerId = Guid.NewGuid();
        var realm = Catalog.RealmProgression!;
        var cap30 = HeavenGateRules.ResolveExpCapWithLinggen(
            30, new LinggenValues(100, 0, 0, 0, 0), entered: true, realm);

        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId, aggregate =>
            {
                aggregate.Progression.HeavenGate.Apply(
                    new LinggenValues(100, 0, 0, 0, 0),
                    new LinggenValues(100, 0, 0, 0, 0),
                    entered: true,
                    rerollCount: 0);
                aggregate.Progression.RealmLevel = 30;
                aggregate.Progression.Experience = cap30;
                aggregate.Loadout.CoreAttributes = aggregate.Loadout.CoreAttributes with
                {
                    AttackExp = realm.RootCumulativeExpFor(90)
                };
            }),
            new EmptyAssets(),
            new NoopEconomy(),
            Catalog,
            TestSimulationClock.Fixed(Epoch),
            Epoch);

        await runtime.LoadAsync(playerId);

        var requestId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new { requestId = requestId.ToString("D"), count = 2 });
        var result = await runtime.ExecuteAsync(playerId, "progression.genji", payload, 0, requestId);

        Assert.True(result.Success, $"{result.Code}: {result.Message}");
        Assert.Equal(2, result.Snapshot.State.Progression.GenjiPoints);
        Assert.True(result.Snapshot.State.Progression.GenjiUnlocked);
        Assert.True(result.Snapshot.WireAttributesChanged);
        Assert.Contains(PlayerSectionKind.Progression, result.Snapshot.TouchedSections!);

        // 同 requestId 幂等重放不重复扣减修为或叠加点数
        var replay = await runtime.ExecuteAsync(playerId, "progression.genji", payload, 0, requestId);
        Assert.True(replay.Success);
        Assert.Equal(2, replay.Snapshot.State.Progression.GenjiPoints);
        Assert.Equal(result.Snapshot.State.Progression.Experience, replay.Snapshot.State.Progression.Experience, 6);

        // 验证协议快照投影包含完整根基四元组
        var dto = ProtocolSnapshotMapper.ToSnapshot(result.Snapshot, Catalog.Fingerprint, Catalog);
        var progSection = dto.Sections.Single(s => s.Section == Domain.Persistence.PlayerSectionKinds.Progression);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(progSection.Data, Protocol.ProtocolJson.Options));
        Assert.Equal(2, doc.RootElement.GetProperty("genjiPoints").GetInt32());
        Assert.True(doc.RootElement.GetProperty("genjiUnlocked").GetBoolean());
        Assert.Equal(30, doc.RootElement.GetProperty("genjiCap").GetInt32());
        Assert.True(doc.RootElement.GetProperty("genjiNextCost").GetDouble() > 0);
    }

    private sealed class FakeRepository : IPlayerStateRepository
    {
        private readonly PlayerStateBundle _bundle;

        public FakeRepository(Guid playerId, Action<PlayerAggregate> configure)
        {
            var aggregate = PlayerAggregate.Create(new PlayerId(playerId), AccountId.New(), "根基测试", Epoch, Catalog.Fingerprint);
            configure(aggregate);
            var state = aggregate.ExportState();
            var writes = Domain.Persistence.SectionCodec.EncodeAggregateFull(state, Epoch);
            var sections = writes.ToDictionary(
                write => write.SectionKind,
                write => new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion,
                    write.SchemaVersion, write.Codec, write.Payload.ToArray(), write.RawBytes, write.Checksum, write.UpdatedAt),
                StringComparer.Ordinal);
            var head = new StoredHead(playerId, state.StateVersion, Domain.Persistence.SectionCodec.CurrentSchemaVersion,
                Epoch, state.GameDataFingerprint, Domain.Persistence.SectionCodec.ComputeHeadChecksum(sections.Values), Epoch);
            _bundle = new PlayerStateBundle(new PlayerIdentity(playerId, Guid.NewGuid(), "根基测试", Epoch), head, sections, []);
        }

        public ValueTask<PlayerStateBundle?> LoadAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlayerStateBundle?>(_bundle);
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections,
            IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    private sealed class EmptyAssets : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>([]);
    }

    private sealed class NoopEconomy : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey,
                true, false, Epoch, request.ResultPayload));
    }
}
