using System.Text.Json;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Protocol;
using Server.Clean;
using Xunit;
using StoredHead = Domain.Persistence.PlayerStateHead;

namespace Server.Tests;

/// <summary>
/// 队列暂停开关与一键清空的命令路径门禁：
/// queue.configure 载荷 paused 校验布尔且三键至少其一；
/// queue.clear 空队列返回 QUEUE_EMPTY，中队列清空后快照队列为空并携带 ActionQueue 触及段。
/// </summary>
public sealed class QueuePauseClearCommandTests
{
    /// <summary>
    /// queue.configure 携带 paused 布尔键即合法：同值翻转落进快照 settings，
    /// 非布尔或三键全缺一律 INVALID_PAYLOAD。
    /// </summary>
    [Fact]
    public async Task QueueConfigure_PausedKeyValidatesAndApplies()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), new EmptyAssets(), new NoopEconomy(), GameDataCatalog.BuiltIn);
        await runtime.LoadAsync(playerId);

        var applied = await runtime.ExecuteAsync(playerId, "queue.configure",
            JsonSerializer.SerializeToElement(new { paused = true }), 0, Guid.NewGuid());
        Assert.Equal("OK", applied.Code);
        Assert.True(applied.Snapshot.State.Settings.QueuePaused);
        var touched = Assert.IsAssignableFrom<IReadOnlySet<PlayerSectionKind>>(applied.Snapshot.TouchedSections);
        Assert.Contains(PlayerSectionKind.Settings, touched);

        var badType = await runtime.ExecuteAsync(playerId, "queue.configure",
            JsonSerializer.SerializeToElement(new { paused = "yes" }), 0, Guid.NewGuid());
        Assert.False(badType.Success);
        Assert.Equal("INVALID_PAYLOAD", badType.Code);

        var noKeys = await runtime.ExecuteAsync(playerId, "queue.configure",
            JsonSerializer.SerializeToElement(new { }), 0, Guid.NewGuid());
        Assert.False(noKeys.Success);
        Assert.Equal("INVALID_PAYLOAD", noKeys.Code);
    }

    /// <summary>
    /// queue.clear 空队列直接拒绝：code QUEUE_EMPTY、消息「队列已为空。」，不产生任何变更。
    /// </summary>
    [Fact]
    public async Task QueueClear_EmptyQueueRejected()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), new EmptyAssets(), new NoopEconomy(), GameDataCatalog.BuiltIn);
        await runtime.LoadAsync(playerId);

        var requestId = Guid.NewGuid();
        var result = await runtime.ExecuteAsync(playerId, "queue.clear",
            JsonSerializer.SerializeToElement(new { requestId = requestId.ToString("D") }), 0, requestId);

        Assert.False(result.Success);
        Assert.Equal("QUEUE_EMPTY", result.Code);
        Assert.Equal("队列已为空。", result.Message);
    }

    /// <summary>
    /// queue.clear 中队列：先补算再整队清空，回执快照队列为空、触及差集含 ActionQueue；
    /// 幂等清单已收录 queue.clear，重放路径返回既有回执。
    /// </summary>
    [Fact]
    public async Task QueueClear_NonEmptyQueueClearsAll()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), new EmptyAssets(), new NoopEconomy(), GameDataCatalog.BuiltIn);
        await runtime.LoadAsync(playerId);

        var enqueueRequestId = Guid.NewGuid();
        var enqueued = await runtime.ExecuteAsync(playerId, "action.enqueue",
            JsonSerializer.SerializeToElement(
                new ActionIntentRequest(enqueueRequestId.ToString("D"), "cultivation.draw-qi", 3), ProtocolJson.Options),
            0, enqueueRequestId);
        Assert.True(enqueued.Success);

        var clearRequestId = Guid.NewGuid();
        var cleared = await runtime.ExecuteAsync(playerId, "queue.clear",
            JsonSerializer.SerializeToElement(new { requestId = clearRequestId.ToString("D") }), 0, clearRequestId);

        Assert.True(cleared.Success);
        Assert.Empty(cleared.Snapshot.State.Actions);
        var touched = Assert.IsAssignableFrom<IReadOnlySet<PlayerSectionKind>>(cleared.Snapshot.TouchedSections);
        Assert.Contains(PlayerSectionKind.ActionQueue, touched);
    }

    /// <summary>单玩家假仓库。</summary>
    private sealed class FakeRepository(Guid playerId) : IPlayerStateRepository
    {
        private readonly PlayerStateBundle _bundle = new(
            new PlayerIdentity(playerId, Guid.NewGuid(), "停泊", DateTimeOffset.UtcNow),
            new StoredHead(playerId, 0, 1, DateTimeOffset.UtcNow, string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new Dictionary<string, StoredSection>(), []);

        public ValueTask<PlayerStateBundle?> LoadAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlayerStateBundle?>(_bundle);
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default) => ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    private sealed class EmptyAssets : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>([]);
    }

    private sealed class NoopEconomy : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload));
    }
}
