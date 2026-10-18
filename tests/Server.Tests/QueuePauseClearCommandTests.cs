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
    /// player.set_avatar 指令更新玩家形象标识并标脏 Settings 分段。
    /// </summary>
    [Fact]
    public async Task PlayerSetAvatar_DefaultMaleAllowed_LockedAvatarRejected()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), new EmptyAssets(), new NoopEconomy(), GameDataCatalog.BuiltIn);
        await runtime.LoadAsync(playerId);

        // 默认男剑修：已解锁，允许直接设置
        var applied = await runtime.ExecuteAsync(playerId, "player.set_avatar",
            JsonSerializer.SerializeToElement(new { avatar = "male" }), 0, Guid.NewGuid());
        Assert.Equal("OK", applied.Code);
        Assert.Equal("player-male", applied.Snapshot.State.Settings.Avatar);
        var touched = Assert.IsAssignableFrom<IReadOnlySet<PlayerSectionKind>>(applied.Snapshot.TouchedSections);
        Assert.Contains(PlayerSectionKind.Settings, touched);

        // 未解锁形象（如 gothic-lolita）：直接切换被拒绝
        var locked = await runtime.ExecuteAsync(playerId, "player.set_avatar",
            JsonSerializer.SerializeToElement(new { avatar = "gothic-lolita" }), 0, Guid.NewGuid());
        Assert.False(locked.Success);
        Assert.Equal("AVATAR_LOCKED", locked.Code);
        Assert.Equal("player-male", locked.Snapshot.State.Settings.Avatar);
    }

    /// <summary>
    /// 功德不足时调用 player.unlock_avatar 失败返回 INSUFFICIENT_MERIT。
    /// </summary>
    [Fact]
    public async Task PlayerUnlockAvatar_InsufficientMerit_Fails()
    {
        var playerId = Guid.NewGuid();
        var assets = new MeritTestAssetStore(playerId, boundMerit: 1000, normalMerit: 2000);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), assets, assets, GameDataCatalog.BuiltIn);
        await runtime.LoadAsync(playerId);

        var res = await runtime.ExecuteAsync(playerId, "player.unlock_avatar",
            JsonSerializer.SerializeToElement(new { avatar = "gothic-lolita" }), 0, Guid.NewGuid());
        Assert.False(res.Success);
        Assert.Equal("INSUFFICIENT_MERIT", res.Code);
        Assert.Equal("player", res.Snapshot.State.Settings.Avatar);
        Assert.False(res.Snapshot.State.Settings.IsAvatarUnlocked("gothic-lolita"));
    }

    /// <summary>
    /// 功德充足时调用 player.unlock_avatar：优先扣除绑定功德，不够由非绑定功德补齐，解锁并佩戴形象。
    /// </summary>
    [Fact]
    public async Task PlayerUnlockAvatar_DeductsBoundMeritFirst_AndUnlocksAvatar()
    {
        var playerId = Guid.NewGuid();
        // 绑定功德 3000 + 流通功德 4000 = 7000，扣除 5000（3000 绑定 + 2000 流通）
        var assets = new MeritTestAssetStore(playerId, boundMerit: 3000, normalMerit: 4000);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), assets, assets, GameDataCatalog.BuiltIn);
        await runtime.LoadAsync(playerId);

        var res = await runtime.ExecuteAsync(playerId, "player.unlock_avatar",
            JsonSerializer.SerializeToElement(new { avatar = "gothic-lolita" }), 0, Guid.NewGuid());
        Assert.True(res.Success);
        Assert.Equal("OK", res.Code);
        Assert.Equal("gothic-lolita", res.Snapshot.State.Settings.Avatar);
        Assert.True(res.Snapshot.State.Settings.IsAvatarUnlocked("gothic-lolita"));

        // 扣除后余额：绑定功德 0，流通功德 2000
        Assert.Equal(0, assets.GetBalance("item.gongde-bound"));
        Assert.Equal(2000, assets.GetBalance("item.gongde"));

        // 解锁后，自由切回女剑修与切回哥特萝莉均成功
        var switchDefault = await runtime.ExecuteAsync(playerId, "player.set_avatar",
            JsonSerializer.SerializeToElement(new { avatar = "default" }), 0, Guid.NewGuid());
        Assert.True(switchDefault.Success);
        Assert.Equal("player", switchDefault.Snapshot.State.Settings.Avatar);

        var switchBack = await runtime.ExecuteAsync(playerId, "player.set_avatar",
            JsonSerializer.SerializeToElement(new { avatar = "gothic-lolita" }), 0, Guid.NewGuid());
        Assert.True(switchBack.Success);
        Assert.Equal("gothic-lolita", switchBack.Snapshot.State.Settings.Avatar);
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
    private sealed class MeritTestAssetStore : IAssetBalanceRepository, IEconomyTransactionStore
    {
        private readonly Guid _playerId;
        private readonly Dictionary<string, double> _balances = new(StringComparer.Ordinal);

        public MeritTestAssetStore(Guid playerId, double boundMerit, double normalMerit)
        {
            _playerId = playerId;
            _balances["item.gongde-bound"] = boundMerit;
            _balances["item.gongde"] = normalMerit;
        }

        public double GetBalance(string assetId) => _balances.GetValueOrDefault(assetId, 0d);

        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid playerId, CancellationToken cancellationToken = default)
        {
            var list = _balances.Select(kv => new AssetBalanceRecord(playerId, kv.Key, string.Empty, kv.Value, 1)).ToList();
            return ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(list);
        }

        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default)
        {
            foreach (var delta in request.Deltas)
            {
                var current = _balances.GetValueOrDefault(delta.AssetId, 0d);
                _balances[delta.AssetId] = current + delta.Delta;
            }
            return ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload));
        }
    }
}
