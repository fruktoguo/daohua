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
}
