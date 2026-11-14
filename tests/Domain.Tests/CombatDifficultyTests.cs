using Domain.Combat;
using Domain.GameData;
using Xunit;

namespace Domain.Tests;

public sealed class CombatDifficultyTests
{
    [Fact]
    public void DifficultyMultipliers_AndBrackets_FollowSpecifications()
    {
        // 匹配区间：默认 0，T1~T3 为 1，T4~T5 为 2
        Assert.Equal(CombatDifficultyRules.BracketDefault, CombatDifficultyRules.GetBracket(0));
        Assert.Equal(CombatDifficultyRules.BracketT1ToT3, CombatDifficultyRules.GetBracket(1));
        Assert.Equal(CombatDifficultyRules.BracketT1ToT3, CombatDifficultyRules.GetBracket(2));
        Assert.Equal(CombatDifficultyRules.BracketT1ToT3, CombatDifficultyRules.GetBracket(3));
        Assert.Equal(CombatDifficultyRules.BracketT4ToT5, CombatDifficultyRules.GetBracket(4));
        Assert.Equal(CombatDifficultyRules.BracketT4ToT5, CombatDifficultyRules.GetBracket(5));

        // 默认档
        Assert.Equal(1.0, CombatDifficultyRules.AttributeMultiplier(0));
        Assert.Equal(1.0, CombatDifficultyRules.ExperienceMultiplier(0));
        Assert.Equal(1.0, CombatDifficultyRules.DropRateMultiplier(0));
        Assert.Equal(1.0, CombatDifficultyRules.DropQuantityMultiplier(0));

        // T1
        Assert.Equal(1.25, CombatDifficultyRules.AttributeMultiplier(1));
        Assert.Equal(1.60, CombatDifficultyRules.ExperienceMultiplier(1));
        Assert.Equal(1.25, CombatDifficultyRules.DropRateMultiplier(1));
        Assert.Equal(1.25, CombatDifficultyRules.DropQuantityMultiplier(1));

        // T5
        // 属性指数提升：1.25^5 ≈ 3.0517578125
        Assert.Equal(Math.Pow(1.25, 5), CombatDifficultyRules.AttributeMultiplier(5), precision: 6);
        // 经验线性提升：1 + 0.60 * 5 = 4.0 (提升 300%)
        Assert.Equal(4.0, CombatDifficultyRules.ExperienceMultiplier(5));
        // 掉落线性提升：1 + 0.25 * 5 = 2.25 (提升 125%)
        Assert.Equal(2.25, CombatDifficultyRules.DropRateMultiplier(5));
        Assert.Equal(2.25, CombatDifficultyRules.DropQuantityMultiplier(5));
    }

    [Fact]
    public void MonsterStats_Ceil_AndDifficultyScaling_RoundsUp()
    {
        var rawStats = CombatantStats.Create(
            maxHitpoints: 100.2,
            attackPower: 15.1,
            defense: 8.3,
            attackIntervalSeconds: 1.0,
            accuracy: 10.4,
            evasion: 5.2) with
        {
            SpellAttack = 12.3,
            MaxManapoints = 50.1,
            MagicResistance = 7.4,
            Critical = 6.2,
            CriticalResistance = 4.1
        };

        var ceiled = CombatDifficultyRules.CeilStats(rawStats);
        Assert.Equal(101.0, ceiled.MaxHitpoints);
        Assert.Equal(16.0, ceiled.AttackPower);
        Assert.Equal(9.0, ceiled.Defense);
        Assert.Equal(1.0, ceiled.AttackIntervalSeconds); // 普攻时间不改
        Assert.Equal(11.0, ceiled.Accuracy);
        Assert.Equal(6.0, ceiled.Evasion);
        Assert.Equal(13.0, ceiled.SpellAttack);
        Assert.Equal(51.0, ceiled.MaxManapoints);
        Assert.Equal(8.0, ceiled.MagicResistance);
        Assert.Equal(7.0, ceiled.Critical);
        Assert.Equal(5.0, ceiled.CriticalResistance);

        // 难度 T1 (1.25x)
        var t1Stats = CombatDifficultyRules.ScaleMonsterStats(ceiled, 1);
        Assert.Equal(Math.Ceiling(101.0 * 1.25), t1Stats.MaxHitpoints); // 127
        Assert.Equal(Math.Ceiling(16.0 * 1.25), t1Stats.AttackPower);   // 20
        Assert.Equal(Math.Ceiling(9.0 * 1.25), t1Stats.Defense);       // 12
        Assert.Equal(Math.Ceiling(13.0 * 1.25), t1Stats.SpellAttack);  // 17
    }

    [Fact]
    public void Catalog_ParsedMonsterStats_AreCeiled()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        foreach (var (id, monster) in catalog.CombatMonsters)
        {
            Assert.Equal(Math.Ceiling(monster.Stats.MaxHitpoints), monster.Stats.MaxHitpoints);
            Assert.Equal(Math.Ceiling(monster.Stats.AttackPower), monster.Stats.AttackPower);
            Assert.Equal(Math.Ceiling(monster.Stats.Defense), monster.Stats.Defense);
            Assert.Equal(Math.Ceiling(monster.Stats.SpellAttack), monster.Stats.SpellAttack);
            Assert.Equal(Math.Ceiling(monster.Stats.MaxManapoints), monster.Stats.MaxManapoints);
            Assert.Equal(Math.Ceiling(monster.Stats.MagicResistance), monster.Stats.MagicResistance);
            Assert.Equal(Math.Ceiling(monster.Stats.Accuracy), monster.Stats.Accuracy);
            Assert.Equal(Math.Ceiling(monster.Stats.Evasion), monster.Stats.Evasion);
            Assert.Equal(Math.Ceiling(monster.Stats.Critical), monster.Stats.Critical);
            Assert.Equal(Math.Ceiling(monster.Stats.CriticalResistance), monster.Stats.CriticalResistance);
        }
    }

    /// <summary>
    /// 怪物难度缩放刻意边界：基础攻防血等属性随难度乘算并 Ceiling 向上取整，
    /// 但 10 项五行增减伤点数、普攻周期与反应参数（Effects）保持原值不放大。
    /// </summary>
    [Fact]
    public void ScaleMonsterStats_PreservesElementalPointsAndReactionEffects()
    {
        var effects = new CombatEffectProfile(HpDrainPoints: 12, MpDrainPoints: 8, DamageReflectPoints: 15, HealingBonusPoints: 20, ThreatOnTakenPoints: 30);
        var baseStats = CombatantStats.Create(100, 20, 10, 1.8) with
        {
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

        var scaled = CombatDifficultyRules.ScaleMonsterStats(baseStats, difficulty: 3);

        Assert.True(scaled.MaxHitpoints > baseStats.MaxHitpoints);
        Assert.True(scaled.AttackPower > baseStats.AttackPower);
        Assert.Equal(baseStats.AttackIntervalSeconds, scaled.AttackIntervalSeconds);
        Assert.Equal(11, scaled.MetalDamageBonusPoints);
        Assert.Equal(12, scaled.WoodDamageBonusPoints);
        Assert.Equal(13, scaled.WaterDamageBonusPoints);
        Assert.Equal(14, scaled.FireDamageBonusPoints);
        Assert.Equal(15, scaled.EarthDamageBonusPoints);
        Assert.Equal(21, scaled.MetalDamageReductionPoints);
        Assert.Equal(22, scaled.WoodDamageReductionPoints);
        Assert.Equal(23, scaled.WaterDamageReductionPoints);
        Assert.Equal(24, scaled.FireDamageReductionPoints);
        Assert.Equal(25, scaled.EarthDamageReductionPoints);
        Assert.Equal(effects, scaled.Effects);
    }

    /// <summary>怪物 JSON 解析应与 DataTool 对齐，从 modifiers 对象读取 5 项反应效果参数（而非顶层）。</summary>
    [Fact]
    public void GameDataCatalog_ParsesMonsterEffectsFromModifiers()
    {
        var header = System.Text.Encoding.UTF8.GetBytes("""
        {
          "contentVersion": "test",
          "baseAttributeAnchors": [
            { "level": 1, "hp": 100, "mp": 50, "ad": 10, "ap": 5, "armor": 10, "mr": 0, "acc": 1, "eva": 1, "crit": 1, "cavoid": 1, "breakPt": 1, "parry": 1, "hpRegen": 2, "mpRegen": 1 }
          ],
          "levelExpected": [
            { "level": 1, "finalExpected": { "maxHitpoints": 100, "maxManapoints": 50, "physicalAttack": 10, "magicAttack": 5, "armor": 10, "magicResistance": 0, "accuracyRating": 1, "evasionRating": 1, "criticalRating": 1, "criticalAvoidance": 1, "breakRating": 1, "parryRating": 1 } }
          ]
        }
        """);
        var monsters = System.Text.Encoding.UTF8.GetBytes("""
        [
          {
            "id": "enemy.mod-effects",
            "name": "吸血妖兽",
            "level": 1,
            "hpDrainPoints": 999,
            "modifiers": {
              "hpDrainPoints": 12,
              "mpDrainPoints": 7,
              "damageReflectPoints": 15,
              "healingBonusPoints": 22,
              "threatOnTakenPoints": 35
            }
          }
        ]
        """);

        var catalog = GameDataCatalog.Parse([header, monsters]);
        var monster = catalog.CombatMonsters["enemy.mod-effects"];
        Assert.Equal(new CombatEffectProfile(12, 7, 15, 22, 35), monster.Stats.Effects);
    }
}
