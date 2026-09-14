using Domain.Combat;
using Domain.GameData;

namespace Domain.Tests;
public sealed class EncounterWaveSelectorTests
{
    [Fact] public void SameSeedAndWaveAreDeterministicAndSpecialIntervalsAreHonored()
    {
        var normal = new CombatMonsterDefinition("n", "普通", 10, CombatantStats.Create(10, 1, 0, 1), []);
        var elite = new CombatMonsterDefinition("e", "精英", 20, CombatantStats.Create(20, 2, 0, 1), []);
        var boss = new CombatMonsterDefinition("b", "首领", 30, CombatantStats.Create(30, 3, 0, 1), []);
        var encounter = new CombatEncounterDefinition("x", "测试", 30, 30, 30, 1, 1, 180, [new("n", 1, 10)], ["e"], ["b"], 20);
        var monsters = new Dictionary<string, CombatMonsterDefinition> { ["n"] = normal, ["e"] = elite, ["b"] = boss };
        Assert.Equal(EncounterWaveSelector.Select(encounter, monsters, 42, 1).Select(x => x.Id), EncounterWaveSelector.Select(encounter, monsters, 42, 1).Select(x => x.Id));
        var normalWave = EncounterWaveSelector.Select(encounter, monsters, 42, 1);
        Assert.Equal(30, normalWave.Sum(x => x.BudgetCost));
        var eliteWave = EncounterWaveSelector.Select(encounter, monsters, 42, 5);
        Assert.Equal("e", eliteWave[0].Id);
        Assert.Equal(30, eliteWave.Sum(x => x.BudgetCost));
        Assert.Equal("b", Assert.Single(EncounterWaveSelector.Select(encounter, monsters, 42, 20)).Id);
    }
}
