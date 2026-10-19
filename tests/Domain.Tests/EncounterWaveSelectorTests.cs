using Domain.Combat;
using Domain.GameData;

namespace Domain.Tests;
/// <summary>
/// 遭遇波次选择器测试：同种子同波次确定性选怪、强度池/预算封顶、
/// 精英波（每 5 波）与 Boss 波（每 20 波）的间隔优先级，以及预算不足时兜底最廉价普通怪。
/// </summary>
public sealed class EncounterWaveSelectorTests
{
    [Fact] public void SameSeedAndWaveAreDeterministicAndSpecialIntervalsAreHonored()
    {
        var normal = new CombatMonsterDefinition("n", "普通", 10, 1, CombatMonsterRank.Minion,
            CombatantStats.Create(10, 1, 0, 1), [], []);
        var elite = new CombatMonsterDefinition("e", "精英", 20, 5, CombatMonsterRank.Elite,
            CombatantStats.Create(20, 2, 0, 1), [], []);
        var boss = new CombatMonsterDefinition("b", "首领", 40, 10, CombatMonsterRank.Boss,
            CombatantStats.Create(30, 3, 0, 1), [], []);
        var encounter = new CombatEncounterDefinition("x", "测试", 30, 50, 30, 1, 1, 180,
            [new("n", 1, 10)], ["e"], ["b"], 20);
        var monsters = new Dictionary<string, CombatMonsterDefinition> { ["n"] = normal, ["e"] = elite, ["b"] = boss };

        // 同种子同波次必须完全确定。
        Assert.Equal(EncounterWaveSelector.Select(encounter, monsters, 42, 1).Select(x => x.Id),
            EncounterWaveSelector.Select(encounter, monsters, 42, 1).Select(x => x.Id));

        // 普通波：预算内最多 3 只普通怪。
        var normalWave = EncounterWaveSelector.Select(encounter, monsters, 42, 1);
        Assert.Equal(3, normalWave.Count);
        Assert.All(normalWave, monster => Assert.Equal("n", monster.Id));

        // 精英波（每 5 波）：1 只精英 + 预算内普通护从。
        var eliteWave = EncounterWaveSelector.Select(encounter, monsters, 42, 5);
        Assert.Equal("e", eliteWave[0].Id);
        Assert.Equal(3, eliteWave.Count);
        Assert.All(eliteWave.Skip(1), monster => Assert.Equal("n", monster.Id));

        // Boss 波（每 20 波，优先级高于精英波）：1 只 Boss + 预算允许的护从。
        var bossWave = EncounterWaveSelector.Select(encounter, monsters, 42, 20);
        Assert.Equal("b", bossWave[0].Id);
        Assert.Equal(2, bossWave.Count);
        Assert.Equal("n", bossWave[1].Id);
    }

    [Fact] public void WaveNeverExceedsTotalStrengthAndFallsBackToCheapestMinion()
    {
        var normal = new CombatMonsterDefinition("n", "普通", 10, 1, CombatMonsterRank.Minion,
            CombatantStats.Create(10, 1, 0, 1), [], []);
        // 强度池只够 2 只普通怪。
        var encounter = new CombatEncounterDefinition("x", "测试", 30, 30, 2, 1, 1, 180,
            [new("n", 1, 1)], [], [], 20);
        var monsters = new Dictionary<string, CombatMonsterDefinition> { ["n"] = normal };
        var wave = EncounterWaveSelector.Select(encounter, monsters, 7, 1);
        Assert.Equal(2, wave.Count);

        // 预算为 0 剩余时仍保底最便宜的普通怪。
        var tight = encounter with { MinBudget = 1, MaxBudget = 1, MaxTotalStrength = 100 };
        var fallback = EncounterWaveSelector.Select(tight, monsters, 7, 1);
        Assert.Equal("n", Assert.Single(fallback).Id);
    }

    [Fact]
    public void EarlyGameEncounters_HerbValleyAndCloudGate_MatchSpecifiedBudgetAndLevels()
    {
        var catalog = GameDataCatalog.LoadEmbedded();

        // 1. 百草谷（node.herb-valley）：单独放野兔，预算 30~60，稳定出 2~4 只魔化野兔
        Assert.True(catalog.WorldGraph.TryGetNode("node.herb-valley", out var herbNode));
        var herbEncounterId = Assert.Single(herbNode.EncounterIds);
        Assert.Equal("encounter.herb-valley", herbEncounterId);
        var herbEncounter = catalog.CombatEncounters[herbEncounterId];
        Assert.Empty(herbEncounter.EliteEnemyIds);
        Assert.Empty(herbEncounter.BossEnemyIds);
        Assert.Equal(30, herbEncounter.MinBudget);
        Assert.Equal(60, herbEncounter.MaxBudget);

        for (long seed = 1; seed <= 50; seed++)
        {
            var wave = EncounterWaveSelector.Select(herbEncounter, catalog.CombatMonsters, seed, 1);
            Assert.InRange(wave.Count, 2, 4);
            Assert.All(wave, monster => Assert.Equal("enemy.demonized-rabbit", monster.Id));
        }

        // 2. 青云山门（node.cloud-gate）：单独放火灵，预算 30~60，稳定出 2~4 只火灵
        Assert.True(catalog.WorldGraph.TryGetNode("node.cloud-gate", out var gateNode));
        var gateEncounterId = Assert.Single(gateNode.EncounterIds);
        Assert.Equal("encounter.twilight-pass", gateEncounterId);
        var gateEncounter = catalog.CombatEncounters[gateEncounterId];
        Assert.Empty(gateEncounter.EliteEnemyIds);
        Assert.Empty(gateEncounter.BossEnemyIds);
        Assert.Equal(30, gateEncounter.MinBudget);
        Assert.Equal(60, gateEncounter.MaxBudget);

        for (long seed = 1; seed <= 50; seed++)
        {
            var wave = EncounterWaveSelector.Select(gateEncounter, catalog.CombatMonsters, seed, 1);
            Assert.InRange(wave.Count, 2, 4);
            Assert.All(wave, monster => Assert.Equal("enemy.fire-spirit", monster.Id));
        }

        // 3. 漱玉溪（node.shuyu-brook）：单独放水灵，预算 30~60，稳定出 2~4 只水灵
        Assert.True(catalog.WorldGraph.TryGetNode("node.shuyu-brook", out var shuyuNode));
        var shuyuEncounter = catalog.CombatEncounters[Assert.Single(shuyuNode.EncounterIds)];
        Assert.Equal(30, shuyuEncounter.MinBudget);
        Assert.Equal(60, shuyuEncounter.MaxBudget);
        for (long seed = 1; seed <= 50; seed++)
        {
            var wave = EncounterWaveSelector.Select(shuyuEncounter, catalog.CombatMonsters, seed, 1);
            Assert.InRange(wave.Count, 2, 4);
            Assert.All(wave, monster => Assert.Equal("enemy.water-spirit", monster.Id));
        }

        // 4. 望云茶肆（node.tea-house）：单独放木灵，预算 30~60，稳定出 2~4 只木灵
        Assert.True(catalog.WorldGraph.TryGetNode("node.tea-house", out var teaNode));
        var teaEncounter = catalog.CombatEncounters[Assert.Single(teaNode.EncounterIds)];
        Assert.Equal(30, teaEncounter.MinBudget);
        Assert.Equal(60, teaEncounter.MaxBudget);
        for (long seed = 1; seed <= 50; seed++)
        {
            var wave = EncounterWaveSelector.Select(teaEncounter, catalog.CombatMonsters, seed, 1);
            Assert.InRange(wave.Count, 2, 4);
            Assert.All(wave, monster => Assert.Equal("enemy.wood-spirit", monster.Id));
        }

        // 5. 清石水滩（node.rock-beach）：放木灵、水灵、火灵三灵，预算 30~60，稳定出 2~4 只
        Assert.True(catalog.WorldGraph.TryGetNode("node.rock-beach", out var rockNode));
        var rockEncounter = catalog.CombatEncounters[Assert.Single(rockNode.EncounterIds)];
        Assert.Equal(30, rockEncounter.MinBudget);
        Assert.Equal(60, rockEncounter.MaxBudget);
        var validIds = new HashSet<string> { "enemy.wood-spirit", "enemy.water-spirit", "enemy.fire-spirit" };
        for (long seed = 1; seed <= 50; seed++)
        {
            var wave = EncounterWaveSelector.Select(rockEncounter, catalog.CombatMonsters, seed, 1);
            Assert.InRange(wave.Count, 2, 4);
            Assert.All(wave, monster => Assert.Contains(monster.Id, validIds));
        }

        // 6. 四种怪物点数消耗一致性验证（全部为 15 点）
        Assert.Equal(15, catalog.CombatMonsters["enemy.demonized-rabbit"].BudgetCost);
        Assert.Equal(15, catalog.CombatMonsters["enemy.wood-spirit"].BudgetCost);
        Assert.Equal(15, catalog.CombatMonsters["enemy.water-spirit"].BudgetCost);
        Assert.Equal(15, catalog.CombatMonsters["enemy.fire-spirit"].BudgetCost);

        // 7. 铁甲熊（enemy.armored-bear）：等级下调为 5
        Assert.Equal(5, catalog.CombatMonsters["enemy.armored-bear"].Level);
    }
}
