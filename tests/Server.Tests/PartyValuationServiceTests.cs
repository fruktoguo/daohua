using System;
using System.Collections.Generic;
using Domain.Common;
using Domain.GameData;
using Domain.Player;
using Protocol;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

public sealed class PartyValuationServiceTests
{
    [Fact]
    public void ValuationResult_SumsAllComponentsCorrectly()
    {
        var result = new ValuationResult(Equipment: 1000, Abilities: 5000, Tools: 300);
        Assert.Equal(6300, result.Total);

        var dto = result.ToDto();
        Assert.Equal(1000, dto.Equipment);
        Assert.Equal(5000, dto.Abilities);
        Assert.Equal(300, dto.Tools);
    }

    [Fact]
    public void Compute_CalculatesAbilitiesByExperienceAndFragmentPrice()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var valuation = new PartyValuationService(null!, null!, null, catalog);

        // ability.palm-lightning: 10,000 经验 => 20 本残卷，配表回收价 10 灵石 => 200 灵石
        var loadout = new PlayerLoadoutDocument(
            new PlayerCoreAttributes(),
            [],
            [],
            new Dictionary<string, double>
            {
                ["ability.palm-lightning"] = 10_000
            });

        var computeMethod = typeof(PartyValuationService)
            .GetMethod("Compute", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var result = (ValuationResult)computeMethod.Invoke(valuation, [loadout])!;

        Assert.Equal(200, result.Abilities);
        Assert.Equal(0, result.Equipment);
        Assert.Equal(0, result.Tools);
        Assert.Equal(200, result.Total);
    }

    [Fact]
    public void Compute_CalculatesEquipmentSellPriceWithEnhancement()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var valuation = new PartyValuationService(null!, null!, null, catalog);

        // tool-flame-ding: 0 强化 => 回收价 144
        // suma-robe: 16 强化 => 基础 432, 16级强化折旧 8434，总计 8866
        var loadout = new PlayerLoadoutDocument(
            new PlayerCoreAttributes(),
            [],
            [
                new PlayerEquipmentSlot("equipment.tool-flame-ding", 0),
                new PlayerEquipmentSlot("equipment.suma-robe", 16)
            ],
            new Dictionary<string, double>());

        var computeMethod = typeof(PartyValuationService)
            .GetMethod("Compute", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var result = (ValuationResult)computeMethod.Invoke(valuation, [loadout])!;

        Assert.Equal(8866, result.Equipment);
        Assert.Equal(144, result.Tools);
        Assert.Equal(0, result.Abilities);
        Assert.Equal(9010, result.Total);
    }

    [Fact]
    public void Compute_IsThreadSafeUnderHighConcurrency()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var valuation = new PartyValuationService(null!, null!, null, catalog);
        var computeMethod = typeof(PartyValuationService)
            .GetMethod("Compute", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var loadout = new PlayerLoadoutDocument(
            new PlayerCoreAttributes(),
            [],
            [
                new PlayerEquipmentSlot("equipment.tool-flame-ding", 0),
                new PlayerEquipmentSlot("equipment.suma-robe", 16)
            ],
            new Dictionary<string, double>());

        System.Threading.Tasks.Parallel.For(0, 100, _ =>
        {
            var result = (ValuationResult)computeMethod.Invoke(valuation, [loadout])!;
            Assert.Equal(9010, result.Total);
        });
    }

    [Fact]
    public void ComputeDaohang_ComputesRealmGenjiCombatEquipmentCarriedAbilitiesAndTraitsWithoutTools()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var progression = new PlayerProgressionState(
            RealmLevel: 31,
            Experience: 0,
            CompletedActions: 0,
            TraitIds: ["trait.tianshengmanli", "trait.jiuyoushiling"],
            UnlockedTraits:
            [
                new Domain.Combat.TraitProgressState("trait.tianshengmanli", 1, 0),
                new Domain.Combat.TraitProgressState("trait.jiuyoushiling", 11, 0),
            ],
            GenjiPoints: 34,
            GenjiUnlocked: true);
        var loadout = new PlayerLoadoutDocument(
            new PlayerCoreAttributes(),
            [
                new PlayerAbilitySlot("ability.xuanyuan-yiqi-gong", 46),
                new PlayerAbilitySlot("ability.palm-lightning", 20),
                new PlayerAbilitySlot(null, 1),
                new PlayerAbilitySlot("item.talisman.shenxing", 1),
            ],
            [
                new PlayerEquipmentSlot("equipment.xuantie-sword", 10), // level=9 -> 9*(1+10*0.05) = 13.5
                new PlayerEquipmentSlot("equipment.tool-flame-ding", 20), // 工具不计入道行
            ],
            new Dictionary<string, double>
            {
                ["ability.heavy-slash"] = 2_056_503, // 已学未携带不计入
            });

        var result = ValuationMath.ComputeDaohang(progression.RealmLevel, progression, loadout, catalog);

        Assert.Equal(113d, result.RealmAndGenji); // 31*2 + 34*1.5 = 62 + 51
        Assert.Equal(13.5d, result.Equipment);
        Assert.Equal(0d, result.Tools);
        Assert.Equal(66d, result.Abilities); // 46 + 20
        Assert.Equal(33.1d, result.Traits); // 2*(1+0.05) + 20*(1+0.55) = 2.1 + 31.0
        Assert.Equal(225.6d, result.Total);

        var dto = result.ToDto();
        Assert.Equal(113d, dto.RealmAndGenji);
        Assert.Equal(13.5d, dto.Equipment);
        Assert.Equal(66d, dto.Abilities);
        Assert.Equal(0d, dto.Tools);
        Assert.Equal(33.1d, dto.Traits);
    }
}
