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

        // 1. 百草谷（node.herb-valley）：单独放野兔，预算 10~25，出 1~2 只魔化野兔（小概率 2 只）
        Assert.True(catalog.WorldGraph.TryGetNode("node.herb-valley", out var herbNode));
        var herbEncounterId = Assert.Single(herbNode.EncounterIds);
        Assert.Equal("encounter.herb-valley", herbEncounterId);
        var herbEncounter = catalog.CombatEncounters[herbEncounterId];
        Assert.Empty(herbEncounter.EliteEnemyIds);
        Assert.Empty(herbEncounter.BossEnemyIds);
        Assert.Equal(10, herbEncounter.MinBudget);
        Assert.Equal(25, herbEncounter.MaxBudget);

        var rabbitCountDistribution = new HashSet<int>();
        for (long seed = 1; seed <= 50; seed++)
        {
            var wave = EncounterWaveSelector.Select(herbEncounter, catalog.CombatMonsters, seed, 1);
            Assert.InRange(wave.Count, 1, 2);
            Assert.All(wave, monster => Assert.Equal("enemy.demonized-rabbit", monster.Id));
            rabbitCountDistribution.Add(wave.Count);
        }
        Assert.Contains(1, rabbitCountDistribution);
        Assert.Contains(2, rabbitCountDistribution);

        // 2. 青云山门（node.cloud-gate）：单独放火灵，威胁度保证严格只出 1 只火灵，无精英首领
        Assert.True(catalog.WorldGraph.TryGetNode("node.cloud-gate", out var gateNode));
        var gateEncounterId = Assert.Single(gateNode.EncounterIds);
        Assert.Equal("encounter.twilight-pass", gateEncounterId);
        var gateEncounter = catalog.CombatEncounters[gateEncounterId];
        Assert.Empty(gateEncounter.EliteEnemyIds);
        Assert.Empty(gateEncounter.BossEnemyIds);

        for (long seed = 1; seed <= 50; seed++)
        {
            for (int waveNumber = 1; waveNumber <= 25; waveNumber++)
            {
                var wave = EncounterWaveSelector.Select(gateEncounter, catalog.CombatMonsters, seed, waveNumber);
                var singleMonster = Assert.Single(wave);
                Assert.Equal("enemy.fire-spirit", singleMonster.Id);
            }
        }

        // 3. 铁甲熊（enemy.armored-bear）：等级下调为 5
        Assert.Equal(5, catalog.CombatMonsters["enemy.armored-bear"].Level);
    }
}
