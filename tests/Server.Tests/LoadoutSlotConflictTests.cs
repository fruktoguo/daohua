using System.Text.Json;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Protocol;
using Server.Clean;
using Xunit;
using StoredHead = Domain.Persistence.PlayerStateHead;

namespace Server.Tests;

/// <summary>装备槽位互斥回归（总纲 04 副手）：单手武器与副手可同时装配，双手武器与任一单手槽互斥。</summary>
public sealed class LoadoutSlotConflictTests
{
    private static readonly (string Id, string Slot)[] Items =
    [
        ("equipment.blade", "weapon"),
        ("equipment.greatsword", "two-hand"),
        ("equipment.dart", "off-hand"),
    ];

    private static GameDataCatalog CreateCatalog()
    {
        var raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (id, slot) in Items)
        {
            using var doc = JsonDocument.Parse($$"""{"id":"{{id}}","slot":"{{slot}}"}""");
            raw[id] = doc.RootElement.Clone();
        }
        return new GameDataCatalog("test", "test", new ActionCatalog([]), rawEquipments: raw);
    }

    private static (PlayerAggregate Player, AssetWallet Wallet) CreateOwner(GameDataCatalog catalog)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "装配", DateTimeOffset.UnixEpoch, catalog.Fingerprint);
        var wallet = new AssetWallet(player.PlayerId);
        wallet.Restore(Items.Select(item => new AssetBalance(player.PlayerId, new AssetKey(new ItemId(item.Id)), 1, 0)));
        return (player, wallet);
    }

    private static JsonElement Payload(params string[] itemIds)
    {
        var equipment = string.Join(",", itemIds.Select(id => $"{{\"itemId\":\"{id}\",\"enhancementLevel\":0}}"));
        using var doc = JsonDocument.Parse($"{{\"equipment\":[{equipment}],\"abilitySlots\":[]}}");
        return doc.RootElement.Clone();
    }

    /// <summary>单手武器与副手同时装配合法，二者分属不同槽位。</summary>
    [Fact]
    public void SingleHandWeaponAndOffHandCoexist()
    {
        var catalog = CreateCatalog();
        var (player, wallet) = CreateOwner(catalog);
        LoadoutCommand.Apply(Payload("equipment.blade", "equipment.dart"), player, wallet, catalog);
        Assert.Equal(2, player.Loadout.Equipment.Count);
    }

    /// <summary>双手武器独占武器与副手两个槽位：与副手或单手武器共存均判槽位冲突。</summary>
    [Fact]
    public void TwoHandConflictsWithOffHandAndWeapon()
    {
        var catalog = CreateCatalog();
        foreach (var conflict in new[] { new[] { "equipment.greatsword", "equipment.dart" }, new[] { "equipment.greatsword", "equipment.blade" } })
        {
            var (player, wallet) = CreateOwner(catalog);
            var error = Assert.Throws<DomainException>(() => LoadoutCommand.Apply(Payload(conflict), player, wallet, catalog));
            Assert.Equal(DomainErrorCode.InvalidArgument, error.Code);
        }
    }

    /// <summary>双手武器单独装配合法。</summary>
    [Fact]
    public void TwoHandAloneIsAccepted()
    {
        var catalog = CreateCatalog();
        var (player, wallet) = CreateOwner(catalog);
        LoadoutCommand.Apply(Payload("equipment.greatsword"), player, wallet, catalog);
        Assert.Single(player.Loadout.Equipment);
    }

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
        Assert.True(result.Snapshot.WireAttributesChanged);
        var attackAfter = Domain.Combat.PlayerAttributeResolver.Resolve(result.Snapshot.State, catalog).PhysicalAttack;
        Assert.True(attackAfter > attackBefore, $"装备后物攻 {attackAfter} 未高于装备前 {attackBefore}");
    }

    /// <summary>技艺工具槽（tool-*）与战斗槽互不冲突：武器与桃木铲可同时装配。</summary>
    [Fact]
    public async Task LoadoutReplace_ToolSlotCoexistsWithWeapon()
    {
        var playerId = Guid.NewGuid();
        await using var runtime = EcsRuntimeFactory.Create(
            new FakeRepository(playerId), new EquipmentAssets(playerId), new NoopEconomy(),
            GameDataCatalog.LoadEmbedded());
        var requestId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new
        {
            equipment = new[]
            {
                new { itemId = "equipment.qingfeng-sword", enhancementLevel = 0d },
                new { itemId = "equipment.tool-herb-hoe", enhancementLevel = 0d }
            },
            abilitySlots = Enumerable.Range(0, 4)
                .Select(_ => new { abilityId = (string?)null, level = 1d, triggers = Array.Empty<JsonElement>() })
                .ToArray(),
            requestId = requestId.ToString("D"),
        }, ProtocolJson.Options);

        var result = await runtime.ExecuteAsync(playerId, "loadout.replace", payload, 0, requestId);
        Assert.True(result.Success, $"{result.Code}: {result.Message}");
        Assert.Contains(result.Snapshot.State.Loadout.Equipment, slot => slot.ItemId == "equipment.tool-herb-hoe");
        Assert.Contains(result.Snapshot.State.Loadout.Equipment, slot => slot.ItemId == "equipment.qingfeng-sword");
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
            [
                new AssetBalanceRecord(playerId, "equipment.qingfeng-sword", string.Empty, 1, 0),
                new AssetBalanceRecord(playerId, "equipment.tool-herb-hoe", string.Empty, 1, 0)
            ]);
    }

    private sealed class NoopEconomy : IEconomyTransactionStore
    {
        public ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, DateTimeOffset.UtcNow, request.ResultPayload));
    }
}
