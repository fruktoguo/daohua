using System.Text.Json;
using DaoHua.Ecs.Systems;

namespace Domain.Tests;

/// <summary>
/// 甘露「法攻百分比层数」门禁：层数 = floor(术法攻击 × 8% × 等级成长 × 技能增幅)，
/// 等级与技能增幅只提升该百分比，每层每秒回血量固定为 1。
/// </summary>
public sealed class CombatGanluBuffTests
{
    /// <summary>配表契约：甘露单次施放消耗 20 点灵力（配表直读，无二次换算）。</summary>
    [Fact]
    public void EmbeddedCatalogGanluCostsTwentyMana()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        Assert.Equal(20, JsonRead.Number(catalog.Abilities["ability.ganlu"], "manaCost"), 9);
    }

    /// <summary>
    /// 术法攻击 125 时：等级 1、技能增幅 0 → 1 + floor(10) = 11 层；
    /// 等级 2、技能增幅 25 → 1 + floor(12.75) = 13 层；两种情况下每层回血均为 1.0。
    /// </summary>
    [Theory]
    [InlineData(1, 0, 11)]
    [InlineData(2, 25, 13)]
    public void StacksScaleWithSpellAttackLevelAndSkillBonus(double level, double skillBonus,
        double expectedStacks)
    {
        var ability = JsonDocument.Parse("""
        {
          "id": "ability.test.ganlu",
          "category": "spell",
          "element": "water",
          "targetType": "lowestHitpointsAlly",
          "damageRatio": 0,
          "cooldownMilliseconds": 20000,
          "castMilliseconds": 1000,
          "manaCost": 20,
          "buff": {
            "buffId": "buff.test.ganlu",
            "name": "甘露",
            "durationSeconds": 10,
            "hpRegenPerSecond": 1,
            "stackPolicy": "maxKeep",
            "spellAttackStackPercent": 0.08
          }
        }
        """).RootElement.Clone();
        var caster = CombatantStats.Create(10_000, 0, 0, 1_000) with
        {
            SpellAttack = 125,
            SkillBonusPoints = skillBonus,
            MaxManapoints = 1_000,
        };
        var ally = CombatantStats.Create(1_000, 0, 0, 1_000);
        var enemy = CombatantStats.Create(1_000_000, 0, 0, 1_000);
        var encounter = DeterministicCombatEngine.StartAllied("ganlu", 7,
            [new CombatantTemplate(caster, [new SkillSlot(ability, level)]), new CombatantTemplate(ally)],
            selfIndex: 0, [new CombatantTemplate(enemy)]);
        // 目标取生命比例最低的队友：开战后把队友打到半血，吟唱完成时按该血线选目标。
        encounter.PlayerAllies[1].Hitpoints = 500;
        CombatActSystem.DecideInitialActions(encounter);

        DeterministicCombatEngine.AdvanceTo(encounter, 1.1);

        var buff = Assert.Single(encounter.PlayerAllies[1].Buffs);
        Assert.Equal("buff.test.ganlu", buff.BuffId);
        Assert.Equal(expectedStacks, buff.Stacks);
        Assert.Equal(1.0, buff.EffectValue(BuffEffectChannel.HpRegenPerSecond));

        // 1 秒网格按层数结算：t=2/3/4/5 共 4 跳，每跳 expectedStacks 点。
        DeterministicCombatEngine.AdvanceTo(encounter, 5.5);
        Assert.Equal(500 + expectedStacks * 4, encounter.PlayerAllies[1].Hitpoints, 6);
    }
}
