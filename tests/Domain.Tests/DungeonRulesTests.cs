using Domain.Combat;
using Domain.GameData;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 追忆副本领域规则与配表不变量单测：
/// 覆盖周清体力懒恢复与整点守恒扣费、虚境/现世 11 档双轴缩放红线、现世动态怪物等级下限保护与保差值重算，以及首批 4 座追忆副本配表与历练行动注入。
/// </summary>
public sealed class DungeonRulesTests
{
    [Fact]
    public void EnergyRules_InitializesTo240_AndRecoversConservingRemainder()
    {
        const double t0 = 1_760_000_000.0;

        // 旧存档/新角色未初始化锚点（updatedAt <= 0）默认赋予首日额度 240 点
        var initial = DungeonEnergyRules.Compute(0, 0, t0);
        Assert.Equal(DungeonEnergyRules.InitialEnergy, initial.Current);
        Assert.Equal(DungeonEnergyRules.MaxEnergy, initial.Max);
        Assert.Equal(DungeonEnergyRules.RecoveryIntervalSeconds, initial.NextRecoverySeconds);
        Assert.Equal((DungeonEnergyRules.MaxEnergy - DungeonEnergyRules.InitialEnergy) * DungeonEnergyRules.RecoveryIntervalSeconds, initial.FullRecoverySeconds);

        // 经过 750 秒 = 恢复 2 点（720s），保留余数 30s，距下一点剩 330s
        var snap = DungeonEnergyRules.Compute(100, t0, t0 + 750);
        Assert.Equal(102, snap.Current);
        Assert.Equal(330.0, snap.NextRecoverySeconds, precision: 6);
        Assert.Equal(t0 + 720, snap.EffectiveAnchorUpdatedAtUnixSeconds, precision: 6);

        // 在第 750 秒扣除 40 体力：余数 30s 必须守恒不丢失（再过 330 秒即可恢复 1 点）
        var ok = DungeonEnergyRules.TryConsume(100, t0, 40, t0 + 750, out var nextVal, out var nextAt);
        Assert.True(ok);
        Assert.Equal(62, nextVal);
        Assert.Equal(t0 + 720, nextAt, precision: 6);

        var after330 = DungeonEnergyRules.Compute(nextVal, nextAt, t0 + 750 + 330);
        Assert.Equal(63, after330.Current);
    }

    [Fact]
    public void EnergyRules_CapsAt1680_AndRejectsInsufficientBalance()
    {
        const double t0 = 1_760_000_000.0;

        // 超过 7 日自然蓄满并封顶 1680
        var full = DungeonEnergyRules.Compute(0, t0, t0 + 10 * 86_400);
        Assert.Equal(DungeonEnergyRules.MaxEnergy, full.Current);
        Assert.Equal(0.0, full.NextRecoverySeconds);
        Assert.Equal(0.0, full.FullRecoverySeconds);

        // 满体态首次消费从消耗时刻重新计时
        var consumedFromFull = DungeonEnergyRules.TryConsume(
            DungeonEnergyRules.MaxEnergy, t0, 60, t0 + 10_000, out var nextVal, out var nextAt);
        Assert.True(consumedFromFull);
        Assert.Equal(DungeonEnergyRules.MaxEnergy - 60, nextVal);
        Assert.Equal(t0 + 10_000, nextAt, precision: 6);

        // 体力不足时扣费拒绝且不改变原锚点
        var denied = DungeonEnergyRules.TryConsume(20, t0, 30, t0 + 100, out var stayVal, out var stayAt);
        Assert.False(denied);
        Assert.Equal(20, stayVal);
        Assert.Equal(t0, stayAt, precision: 6);
    }

    [Fact]
    public void DifficultyRules_ElevenTiers_VirtualAndReality_MatchSpec()
    {
        int[] expectedCosts = [30, 40, 50, 60, 80, 100, 120, 150, 180, 210, 240];
        for (var d = 0; d < DungeonDifficultyRules.TierCount; d++)
        {
            Assert.True(DungeonDifficultyRules.IsValid(d));
            Assert.Equal(d >= 3, DungeonDifficultyRules.IsReality(d));
            Assert.Equal(expectedCosts[d], DungeonDifficultyRules.EnergyCost(d));
            var expectedEff = 1.0 + 0.15 * d;
            var expectedTotal = (expectedCosts[d] / 30.0) * expectedEff;
            Assert.Equal(expectedEff, DungeonDifficultyRules.EnergyEfficiencyMultiplier(d), precision: 9);
            Assert.Equal(expectedTotal, DungeonDifficultyRules.TotalRewardMultiplier(d), precision: 9);
            Assert.Equal(1.0 + 0.25 * d, DungeonDifficultyRules.RareDropRateMultiplier(d), precision: 9);
        }

        // 现世·凡阶 (d=3) 继续从 d=3 叠乘，确保凡阶也高于虚境·地狱 (d=2)
        Assert.True(DungeonDifficultyRules.HitpointMultiplier(3) > DungeonDifficultyRules.HitpointMultiplier(2));
        Assert.True(DungeonDifficultyRules.StatMultiplier(3) > DungeonDifficultyRules.StatMultiplier(2));

        Assert.False(DungeonDifficultyRules.IsValid(-1));
        Assert.False(DungeonDifficultyRules.IsValid(11));
        Assert.Equal(0, DungeonDifficultyRules.Clamp(-3));
        Assert.Equal(10, DungeonDifficultyRules.Clamp(99));
        // 现世·帝阶 (d=10) 总倍率：8.0 * 2.50 = 20.0
        Assert.Equal(20.0, DungeonDifficultyRules.TotalRewardMultiplier(10), precision: 9);
    }

    [Fact]
    public void RealityDynamicLevel_PreservesGapWithFloorProtection_AndRebuildsMonsterStatsWithoutInflatingDropRates()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var dungeon = catalog.Dungeons["dungeon.qingyun-secret-gorge"];
        var minLvl = dungeon.MinMonsterLevel;
        var bossId = dungeon.Waves[9].EnemyIds[0];
        var baseBoss = catalog.CombatMonsters[bossId];
        Assert.True(baseBoss.Level > minLvl);
        var gap = baseBoss.Level - minLvl;

        // 1. 虚境（d=0..2）：即使玩家 60 级，怪物依然维持原等级
        Assert.Equal(baseBoss.Level, DungeonDifficultyRules.ResolveDynamicMonsterLevel(baseBoss.Level, minLvl, maxPartyRealmLevel: 60, difficulty: 2));
        var virtualBoss = catalog.ResolveDungeonMonster(dungeon, bossId, difficulty: 2, maxPartyRealmLevel: 60);
        Assert.Equal(baseBoss.Level, virtualBoss.Level);

        // 2. 现世下限保护（d=3，队伍最高仅 1 级 < minLvl）：目标基准取 minLvl，不降级
        Assert.Equal(baseBoss.Level, DungeonDifficultyRules.ResolveDynamicMonsterLevel(baseBoss.Level, minLvl, maxPartyRealmLevel: 1, difficulty: 3));
        var floorBoss = catalog.ResolveDungeonMonster(dungeon, bossId, difficulty: 3, maxPartyRealmLevel: 1);
        Assert.Equal(baseBoss.Level, floorBoss.Level);

        // 3. 现世动态升阶（d=3，队伍最高 30 级）：最低级怪变 30 级，关底 Boss 变 30 + gap 级，面板与击杀经验随通用曲线显著增长，但材料基础掉率不变
        var realityBoss = catalog.ResolveDungeonMonster(dungeon, bossId, difficulty: 3, maxPartyRealmLevel: 30);
        Assert.Equal(30 + gap, realityBoss.Level);
        Assert.True(realityBoss.Stats.MaxHitpoints > baseBoss.Stats.MaxHitpoints * 3);
        Assert.True(realityBoss.Experience > baseBoss.Experience * 2);
        Assert.Equal(baseBoss.DropTable.Count, realityBoss.DropTable.Count);
        for (var i = 0; i < baseBoss.DropTable.Count; i++)
        {
            Assert.Equal(baseBoss.DropTable[i].DropChance, realityBoss.DropTable[i].DropChance, precision: 9);
            Assert.Equal(baseBoss.DropTable[i].MinQuantity, realityBoss.DropTable[i].MinQuantity, precision: 9);
            Assert.Equal(baseBoss.DropTable[i].MaxQuantity, realityBoss.DropTable[i].MaxQuantity, precision: 9);
        }

        // 4. 127 级硬上限封顶保护
        Assert.Equal(127, DungeonDifficultyRules.ResolveDynamicMonsterLevel(baseBoss.Level, minLvl, maxPartyRealmLevel: 126, difficulty: 10));
    }

    [Fact]
    public void DifficultyRules_DualAxisScaling_RoundsUpAndPreservesSpeedElementsAndEffects()
    {
        var effects = new CombatEffectProfile(HpDrainPoints: 12, MpDrainPoints: 8, DamageReflectPoints: 15, HealingBonusPoints: 20, ThreatOnTakenPoints: 30);
        var baseStats = CombatantStats.Create(100, 20, 10, 1.8, accuracy: 15, evasion: 12) with
        {
            SpellAttack = 18,
            MaxManapoints = 80,
            MagicResistance = 9,
            MetalDamageBonusPoints = 11,
            WoodDamageBonusPoints = 12,
            WaterDamageBonusPoints = 13,
            FireDamageBonusPoints = 14,
            EarthDamageBonusPoints = 15,
            MetalDamageReductionPoints = 21,
            WoodDamageReductionPoints = 22,
            WaterDamageReductionPoints = 23,
            FireDamageReductionPoints = 24,
            EarthDamageReductionPoints = 25,
            Effects = effects
        };

        var d5 = DungeonDifficultyRules.ScaleMonsterStats(baseStats, 5);
        Assert.Equal(Math.Ceiling(100 * Math.Pow(1.30, 5)), d5.MaxHitpoints);
        Assert.Equal(Math.Ceiling(20 * Math.Pow(1.15, 5)), d5.AttackPower);
        Assert.Equal(Math.Ceiling(10 * Math.Pow(1.15, 5)), d5.Defense);
        Assert.Equal(Math.Ceiling(18 * Math.Pow(1.15, 5)), d5.SpellAttack);
        Assert.Equal(Math.Ceiling(80 * Math.Pow(1.15, 5)), d5.MaxManapoints);

        // 红线不变量：攻速周期、10 项五行增减伤点数与反应参数绝不放大
        Assert.Equal(1.8, d5.AttackIntervalSeconds);
        Assert.Equal(11, d5.MetalDamageBonusPoints);
        Assert.Equal(12, d5.WoodDamageBonusPoints);
        Assert.Equal(13, d5.WaterDamageBonusPoints);
        Assert.Equal(14, d5.FireDamageBonusPoints);
        Assert.Equal(15, d5.EarthDamageBonusPoints);
        Assert.Equal(21, d5.MetalDamageReductionPoints);
        Assert.Equal(22, d5.WoodDamageReductionPoints);
        Assert.Equal(23, d5.WaterDamageReductionPoints);
        Assert.Equal(24, d5.FireDamageReductionPoints);
        Assert.Equal(25, d5.EarthDamageReductionPoints);
        Assert.Equal(effects, d5.Effects);
    }

    [Fact]
    public void Catalog_LoadsFourInitialDungeons_WithTenWavesExplorationActionsAndExclusiveGearLoop()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        string[] expectedDungeons =
        [
            "dungeon.qingyun-secret-gorge",
            "dungeon.trail-boundary-rift",
            "dungeon.starfall-ancient-ruin",
            "dungeon.wumeng-sword-tomb"
        ];

        Assert.Equal(expectedDungeons.Length, catalog.Dungeons.Count);
        foreach (var id in expectedDungeons)
        {
            Assert.True(catalog.Dungeons.TryGetValue(id, out var dungeon), $"缺少追忆副本 {id}");
            Assert.StartsWith("追忆·", dungeon!.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("秘境", dungeon.Name, StringComparison.Ordinal);
            Assert.Equal(10, dungeon.TotalWaves);
            Assert.Equal(10, dungeon.Waves.Count);
            Assert.Equal(DungeonWaveKind.Elite, dungeon.Waves[2].Kind);
            Assert.Equal(DungeonWaveKind.Elite, dungeon.Waves[5].Kind);
            Assert.Equal(DungeonWaveKind.Elite, dungeon.Waves[8].Kind);
            Assert.Equal(DungeonWaveKind.Boss, dungeon.Waves[9].Kind);
            Assert.Equal(11, dungeon.RecommendedValuation.Count);

            // 验证对应的追忆副本行动已公开注入 ActionCatalog（历练页可见，HiddenFromCatalog == false）
            Assert.True(catalog.Actions.TryGet(new Domain.Common.ActionId(id), out var action));
            Assert.Equal(ActionExecutionKind.Dungeon, action.ExecutionKind);
            Assert.False(action.HiddenFromCatalog);
            Assert.NotNull(action.AllowedNodeIds);
            Assert.Contains(dungeon.NodeId, action.AllowedNodeIds!);
            Assert.Contains(id, catalog.WorldGraph.GetNode(dungeon.NodeId).DungeonIds);

            // 通关战利品表必须含保底专属材料（D0 可得）与稀有专属装备（D3 现世·凡阶起解锁）
            Assert.Contains(dungeon.ClearLootTable, e => e.Kind == "guaranteed" && e.MinDifficulty == 0);
            Assert.Contains(dungeon.ClearLootTable, e => e.Kind == "rare" && e.MinDifficulty == 3);
        }
    }
}
