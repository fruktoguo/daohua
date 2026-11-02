using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Protocol;
using Server.Clean;
using Xunit;
using StoredHead = Domain.Persistence.PlayerStateHead;

namespace Server.Tests;

/// <summary>
/// 储物袋使用命令（item.use）端到端集成测试：
/// 验证储物袋开启逻辑、持有扣减、资产入账与数量边界校验。
/// </summary>
public sealed class StorageBagCommandTests
{
    [Fact]
    public async Task ItemUse_StorageBag_ValidatesQuantityAndPayload()
    {
        var playerId = Guid.NewGuid();
        var store = new BagTestAssetStore(playerId, [("item.storage-bag.broken", 5)]);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), store, store, GameDataCatalog.BuiltIn);
        await runtime.LoadAsync(playerId);

        // 数量为 0
        var reqZero = Guid.NewGuid();
        var resZero = await runtime.ExecuteAsync(playerId, "item.use",
            JsonSerializer.SerializeToElement(new { itemId = "item.storage-bag.broken", quantity = 0, requestId = reqZero.ToString("D") }), 0, reqZero);
        Assert.False(resZero.Success);
        Assert.Equal("INVALID_PAYLOAD", resZero.Code);

        // 数量为负数
        var reqNeg = Guid.NewGuid();
        var resNeg = await runtime.ExecuteAsync(playerId, "item.use",
            JsonSerializer.SerializeToElement(new { itemId = "item.storage-bag.broken", quantity = -1, requestId = reqNeg.ToString("D") }), 0, reqNeg);
        Assert.False(resNeg.Success);
        Assert.Equal("INVALID_PAYLOAD", resNeg.Code);

        // 物品 ID 为空
        var reqEmpty = Guid.NewGuid();
        var resEmpty = await runtime.ExecuteAsync(playerId, "item.use",
            JsonSerializer.SerializeToElement(new { itemId = "", quantity = 1, requestId = reqEmpty.ToString("D") }), 0, reqEmpty);
        Assert.False(resEmpty.Success);
        Assert.Equal("INVALID_PAYLOAD", resEmpty.Code);
    }

    [Fact]
    public async Task ItemUse_StorageBag_InsufficientQuantity_ReturnsError()
    {
        var playerId = Guid.NewGuid();
        var store = new BagTestAssetStore(playerId, [("item.storage-bag.broken", 2)]);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), store, store, GameDataCatalog.BuiltIn);
        await runtime.LoadAsync(playerId);

        // 玩家只有 2 个袋子，尝试开 3 个
        var req = Guid.NewGuid();
        var res = await runtime.ExecuteAsync(playerId, "item.use",
            JsonSerializer.SerializeToElement(new { itemId = "item.storage-bag.broken", quantity = 3, requestId = req.ToString("D") }), 0, req);
        Assert.False(res.Success);
        Assert.Equal("INSUFFICIENT_RESOURCE", res.Code);
        Assert.Equal(2, store.GetBalance("item.storage-bag.broken"));
    }

    [Fact]
    public async Task ItemUse_StorageBag_SingleOpen_DebitsBagAndGrantsLoot()
    {
        var playerId = Guid.NewGuid();
        var store = new BagTestAssetStore(playerId, [("item.storage-bag.broken", 1)]);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), store, store, GameDataCatalog.BuiltIn);
        await runtime.LoadAsync(playerId);

        var req = Guid.NewGuid();
        var res = await runtime.ExecuteAsync(playerId, "item.use",
            JsonSerializer.SerializeToElement(new { itemId = "item.storage-bag.broken", quantity = 1, requestId = req.ToString("D") }), 0, req);
        Assert.True(res.Success);
        Assert.Equal("OK", res.Code);
        Assert.Contains("储物袋开启成功！获得", res.Message);
        Assert.Contains("灵石", res.Message);

        // 储物袋已扣减为 0
        Assert.Equal(0, store.GetBalance("item.storage-bag.broken"));
        // 灵石必得且 >= 1
        Assert.True(store.GetBalance("currency.spirit-stone") >= 1);
    }

    [Fact]
    public async Task ItemUse_StorageBag_BatchOpen_DebitsAllBagsAndAccumulatesRewards()
    {
        var playerId = Guid.NewGuid();
        const int batchCount = 10;
        var store = new BagTestAssetStore(playerId, [("item.storage-bag.broken", batchCount)]);
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), store, store, GameDataCatalog.BuiltIn);
        await runtime.LoadAsync(playerId);

        var req = Guid.NewGuid();
        var res = await runtime.ExecuteAsync(playerId, "item.use",
            JsonSerializer.SerializeToElement(new { itemId = "item.storage-bag.broken", quantity = batchCount, requestId = req.ToString("D") }), 0, req);
        Assert.True(res.Success);
        Assert.Equal("OK", res.Code);

        // 储物袋全部扣减
        Assert.Equal(0, store.GetBalance("item.storage-bag.broken"));
        // 批量开启 10 个袋子，灵石至少 10 * 1 = 10
        Assert.True(store.GetBalance("currency.spirit-stone") >= 10);
    }

    private sealed class FakeRepository(Guid playerId) : IPlayerStateRepository
    {
        private readonly PlayerStateBundle _bundle = new(
            new PlayerIdentity(playerId, Guid.NewGuid(), "散修", DateTimeOffset.UtcNow),
            new StoredHead(playerId, 0, 1, DateTimeOffset.UtcNow, string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new Dictionary<string, StoredSection>(), []);

        public ValueTask<PlayerStateBundle?> LoadAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlayerStateBundle?>(_bundle);
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default) => ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    private sealed class BagTestAssetStore : IAssetBalanceRepository, IEconomyTransactionStore
    {
        private readonly Guid _playerId;
        private readonly Dictionary<string, double> _balances = new(StringComparer.Ordinal);

        public BagTestAssetStore(Guid playerId, IEnumerable<(string ItemId, double Quantity)> initial)
        {
            _playerId = playerId;
            foreach (var (itemId, quantity) in initial)
            {
                _balances[itemId] = quantity;
            }
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
