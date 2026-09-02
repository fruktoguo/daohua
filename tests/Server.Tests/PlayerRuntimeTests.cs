using Domain.GameData;
using Domain.Player;
using Microsoft.Extensions.Logging.Abstractions;
using Protocol;
using Server.Clean;
using System.Text.Json;
using StoredHead = Server.Clean.PlayerStateHead;
using Xunit;

namespace Server.Tests;

public sealed class PlayerRuntimeTests
{
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

    private sealed class EmptyAssetRepository : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>([]);
    }

    private sealed class NoopEconomyRepository : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload));
    }

    private sealed class LostOwnershipStore : IPlayerOwnershipStore
    {
        public ValueTask<bool> AcquireAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask<bool> RenewAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask ReleaseAsync(Guid playerId, Guid ownerInstanceId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
