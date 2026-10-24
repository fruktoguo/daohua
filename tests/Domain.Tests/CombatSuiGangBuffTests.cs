using System.Text.Json;
using DaoHua.Ecs.Systems;

namespace Domain.Tests;

/// <summary>
/// 碎罡层数派生门禁：等级成长与技能增幅只提升层数百分比，
/// 每层物理攻击力/命中/暴击保持配置基础值；已损气血通道按等效阈值动态重算。
/// </summary>
public sealed class CombatSuiGangBuffTests
{
    /// <summary>
    /// 双抗 30+20=50、等级 2、技能增幅 25 → 成长 1.02×1.25=1.275：
    /// 煞力 = 1 + floor(50×0.3×1.275) = 20 层，每层 +1 物攻；
    /// 罡锋（最大气血 1000、当前 200） = floor(800×1.275/5) = 204 层，每层 +1 命中/+1 暴击；
    /// 碎罡固定 −400 双抗。后续血线 100 时罡锋动态重算为 floor(900×1.275/5) = 229 层。
    /// </summary>
    [Fact]
    public void StackPercentagesScaleWithLevelAndSkillBonus()
    {
        var ability = JsonDocument.Parse("""
        {
          "id": "ability.test.sui-gang",
          "category": "physical",
          "element": "none",
          "targetType": "self",
          "damageRatio": 0,
          "cooldownMilliseconds": 60000,
          "castMilliseconds": 1000,
          "manaCost": 0,
          "buffs": [
            {
              "buffId": "buff.test.sui-gang",
              "name": "碎罡",
              "durationSeconds": 20,
              "resistancePoints": -400,
              "stackPolicy": "maxKeep"
            },
            {
              "buffId": "buff.test.sha-li",
              "name": "煞力",
              "durationSeconds": 20,
              "attackPower": 1,
              "resistancePerStack": 1,
              "resistanceStackRatio": 0.3,
              "stackPolicy": "maxKeep"
            },
            {
              "buffId": "buff.test.gang-feng",
              "name": "罡锋",
              "durationSeconds": 20,
              "accuracy": 1,
              "critical": 1,
              "missingHitpointsPerStack": 5,
              "stackPolicy": "maxKeep"
            }
          ]
        }
        """).RootElement.Clone();
        var caster = CombatantStats.Create(1_000, 10, 30, 1.0) with
        {
            MagicResistance = 20,
            SkillBonusPoints = 25,
            MaxManapoints = 0,
        };
        var enemy = CombatantStats.Create(1_000_000, 0, 0, 1_000);
        var encounter = DeterministicCombatEngine.StartAllied("sui-gang", 11,
            [new CombatantTemplate(caster, [new SkillSlot(ability, 2)])],
            selfIndex: 0, [new CombatantTemplate(enemy)]);
        // 开局已进入吟唱；把自身打到 20% 血线，罡锋按该血线授予。
        var self = encounter.PlayerAllies[0];
        self.Hitpoints = 200;

        DeterministicCombatEngine.AdvanceTo(encounter, 1.1);

        Assert.Equal(-400, Buff(self, "buff.test.sui-gang").EffectValue(BuffEffectChannel.ResistancePoints));
        var sha = Buff(self, "buff.test.sha-li");
        Assert.Equal(20, sha.Stacks);
        Assert.Equal(1.0, sha.EffectValue(BuffEffectChannel.AttackPower));
        var gang = Buff(self, "buff.test.gang-feng");
        Assert.Equal(204, gang.Stacks);
        Assert.Equal(1.0, gang.EffectValue(BuffEffectChannel.Accuracy));
        Assert.Equal(1.0, gang.EffectValue(BuffEffectChannel.Critical));

        // 血线继续下降时，罡锋按存储的等效每层阈值动态重算，成长口径不丢失。
        self.Hitpoints = 100;
        self.SyncDerivedStacks();
        Assert.Equal(229, Buff(self, "buff.test.gang-feng").Stacks);
    }

    private static CombatBuffState Buff(CombatantState unit, string buffId)
        => Assert.Single(unit.Buffs, buff => buff.BuffId == buffId);
}
