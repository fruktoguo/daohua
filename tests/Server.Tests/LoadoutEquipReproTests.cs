using System.Text.Json;
using Domain.GameData;
using Domain.Persistence;
using Protocol;
using Server.Clean;
using Xunit;
using StoredHead = Domain.Persistence.PlayerStateHead;

namespace Server.Tests;

/// <summary>临时复现：玩家钱包持有装备时 loadout.replace 装配是否生效。</summary>
public sealed class LoadoutEquipReproTests
{
    [Fact]
    public async Task LoadoutReplace_OwnedWeapon_EquipsAndResolvesAttributes()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), new EquipmentAssets(playerId), new NoopEconomy(),
            GameDataCatalog.LoadEmbedded());
        var catalog = GameDataCatalog.LoadEmbedded();
        var before = await runtime.LoadAsync(playerId);
        var attackBefore = Domain.Combat.PlayerAttributeResolver.Resolve(before.State, catalog).PhysicalAttack;

        var requestId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new
        {
            equipment = new[] { new { itemId = "equipment.qingfeng-sword", enhancementLevel = 0d } },
            abilitySlots = Enumerable.Range(0, 4)
                .Select(_ => new { abilityId = (string?)null, level = 1d, triggers = Array.Empty<JsonElement>() })
                .ToArray(),
            requestId = requestId.ToString("D"),
        }, ProtocolJson.Options);

        var result = await runtime.ExecuteAsync(playerId, "loadout.replace", payload, 0, requestId);
        Assert.True(result.Success, $"{result.Code}: {result.Message}");
        Assert.Contains(result.Snapshot.State.Loadout.Equipment, slot => slot.ItemId == "equipment.qingfeng-sword");
        // 装备装配重建属性输入修订号 → 锁内属性面板必须重新解析。
        Assert.True(result.Snapshot.WireAttributesChanged);
        // 青锋剑 flatStats.physicalAttack=10、baseStatRatios.physicalAttack=0.2：最终物攻必须高于裸装。
        var attackAfter = Domain.Combat.PlayerAttributeResolver.Resolve(result.Snapshot.State, catalog).PhysicalAttack;
        Assert.True(attackAfter > attackBefore, $"装备后物攻 {attackAfter} 未高于装备前 {attackBefore}");
    }

    private sealed class FakeRepository(Guid playerId) : IPlayerStateRepository
    {
        private readonly PlayerStateBundle _bundle = new(
            new PlayerIdentity(playerId, Guid.NewGuid(), "装备复现", DateTimeOffset.UtcNow),
            new StoredHead(playerId, 0, 1, DateTimeOffset.UtcNow, string.Empty, string.Empty, DateTimeOffset.UtcNow),
            new Dictionary<string, StoredSection>(), []);

        public ValueTask<PlayerStateBundle?> LoadAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlayerStateBundle?>(_bundle);
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new FlushBatchResult(0, 0, 0, TimeSpan.Zero));
    }

    private sealed class EquipmentAssets(Guid playerId) : IAssetBalanceRepository
    {
        public ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AssetBalanceRecord>>(
                [new AssetBalanceRecord(playerId, "equipment.qingfeng-sword", string.Empty, 1, 0)]);
    }

    private sealed class NoopEconomy : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload));
    }
}
