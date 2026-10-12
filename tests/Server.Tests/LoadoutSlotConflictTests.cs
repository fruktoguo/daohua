using System.Text.Json;
using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Server.Clean;
using Xunit;

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
}
