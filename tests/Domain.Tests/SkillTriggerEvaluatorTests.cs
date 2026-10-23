using System.Text.Json;
using Domain.Combat;
using Domain.GameData;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 触发条件求值引擎门禁：
/// 1. 5 大对象（自身/目标/友方/敌方/战场）求值与作用域矩阵校验；
/// 2. 群体大盘与极值（总生命/总灵力百分比、最低/最高生命百分比、规模数量）；
/// 3. 单体身份与群体存在性（玩家/精英/妖王/召唤物）；
/// 4. 战斗时钟与出厂默认触发条件推导。
/// </summary>
public sealed class SkillTriggerEvaluatorTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static CombatantStats CreateStats(double maxHp = 100, double maxMana = 100)
        => new(maxHp, 10, 10, 2.0) { MaxManapoints = maxMana };

    [Fact]
    public void BattleElapsedSeconds_EvaluatesCorrectly()
    {
        var caster = new CombatantState("player", CreateStats());
        var trigger = Json("""{"dependency":"battle","condition":"elapsed-seconds","comparator":"greater-than-equal","value":10.0}""");

        Assert.True(SkillTriggerEvaluator.MatchesAll([trigger], caster, null, [caster], [], 15.0));
        Assert.False(SkillTriggerEvaluator.MatchesAll([trigger], caster, null, [caster], [], 5.0));
    }

    [Fact]
    public void AlliesAggregateConditions_EvaluateCorrectly()
    {
        var caster = new CombatantState("player", CreateStats(100, 100)) { Hitpoints = 80, Manapoints = 60 };
        var ally2 = new CombatantState("ally:1", CreateStats(100, 100)) { Hitpoints = 40, Manapoints = 20 };
        var allies = new[] { caster, ally2 };

        // lowest-hp-percentage < 75% -> ally2 is 40% < 75% -> true
        var lowHpTrigger = Json("""{"dependency":"allies","condition":"lowest-hp-percentage","comparator":"less-than","value":75.0}""");
        Assert.True(SkillTriggerEvaluator.MatchesAll([lowHpTrigger], caster, null, allies, []));

        // total-hp-percentage: (80 + 40) / 200 = 60%
        var totalHpTrigger = Json("""{"dependency":"allies","condition":"total-hp-percentage","comparator":"less-than-equal","value":60.0}""");
        Assert.True(SkillTriggerEvaluator.MatchesAll([totalHpTrigger], caster, null, allies, []));

        var totalHpStrictLess = Json("""{"dependency":"allies","condition":"total-hp-percentage","comparator":"less-than","value":60.0}""");
        Assert.False(SkillTriggerEvaluator.MatchesAll([totalHpStrictLess], caster, null, allies, []));

        // total-mana-percentage: (60 + 20) / 200 = 40%
        var totalManaTrigger = Json("""{"dependency":"allies","condition":"total-mana-percentage","comparator":"greater-than-equal","value":40.0}""");
        Assert.True(SkillTriggerEvaluator.MatchesAll([totalManaTrigger], caster, null, allies, []));
    }

    [Fact]
    public void EntityTagConditions_EvaluateCorrectly()
    {
        var caster = new CombatantState("player", CreateStats());
        var bossEnemy = new CombatantState("enemy:0", CreateStats(1000, 500))
        {
            Rank = CombatMonsterRank.Boss
        };
        var minionEnemy = new CombatantState("enemy:1", CreateStats(100, 50))
        {
            Rank = CombatMonsterRank.Minion,
            IsSummon = true
        };
        var enemies = new[] { bossEnemy, minionEnemy };

        // 目标是妖王
        var targetIsBoss = Json("""{"dependency":"target-enemy","condition":"is-boss","comparator":"is-active"}""");
        Assert.True(SkillTriggerEvaluator.MatchesAll([targetIsBoss], caster, bossEnemy, [caster], enemies));
        Assert.False(SkillTriggerEvaluator.MatchesAll([targetIsBoss], caster, minionEnemy, [caster], enemies));

        // 目标是召唤物
        var targetIsSummon = Json("""{"dependency":"target-enemy","condition":"is-summon","comparator":"is-active"}""");
        Assert.True(SkillTriggerEvaluator.MatchesAll([targetIsSummon], caster, minionEnemy, [caster], enemies));
        Assert.False(SkillTriggerEvaluator.MatchesAll([targetIsSummon], caster, bossEnemy, [caster], enemies));

        // 敌方存在妖王
        var enemyHasBoss = Json("""{"dependency":"enemies","condition":"has-boss","comparator":"is-active"}""");
        Assert.True(SkillTriggerEvaluator.MatchesAll([enemyHasBoss], caster, bossEnemy, [caster], enemies));

        // 敌方存在召唤物
        var enemyHasSummon = Json("""{"dependency":"enemies","condition":"has-summon","comparator":"is-active"}""");
        Assert.True(SkillTriggerEvaluator.MatchesAll([enemyHasSummon], caster, bossEnemy, [caster], enemies));

        // 敌方不存在玩家
        var enemyHasPlayer = Json("""{"dependency":"enemies","condition":"has-player","comparator":"is-active"}""");
        Assert.False(SkillTriggerEvaluator.MatchesAll([enemyHasPlayer], caster, bossEnemy, [caster], enemies));
    }

    [Fact]
    public void InferDefaultTriggers_BehavesAccordingToRules()
    {
        // 伤害技能：目标生命 > 1
        var damageAbility = Json("""
            {"id":"ability.test.dmg","name":"测试伤害","damageRatio":1.5,"flatDamage":10,"castMilliseconds":1000,"cooldownMilliseconds":5000}
            """);
        var dmgTriggers = SkillSlot.InferDefaultTriggers(damageAbility);
        Assert.Single(dmgTriggers);
        Assert.Equal("target-enemy", dmgTriggers[0].GetProperty("dependency").GetString());
        Assert.Equal("current-hp", dmgTriggers[0].GetProperty("condition").GetString());
        Assert.Equal("greater-than", dmgTriggers[0].GetProperty("comparator").GetString());
        Assert.Equal(1.0, dmgTriggers[0].GetProperty("value").GetDouble());

        // 治疗技能：友方最低生命百分比 < 75%
        var healAbility = Json("""
            {"id":"ability.test.heal","name":"测试治疗","effectType":"heal","healingRatio":0.6,"castMilliseconds":1000,"cooldownMilliseconds":5000}
            """);
        var healTriggers = SkillSlot.InferDefaultTriggers(healAbility);
        Assert.Single(healTriggers);
        Assert.Equal("allies", healTriggers[0].GetProperty("dependency").GetString());
        Assert.Equal("lowest-hp-percentage", healTriggers[0].GetProperty("condition").GetString());
        Assert.Equal("less-than", healTriggers[0].GetProperty("comparator").GetString());
        Assert.Equal(75.0, healTriggers[0].GetProperty("value").GetDouble());

        // Buff技能：目标身上无该Buff (层数 == 0)
        var buffAbility = Json("""
            {"id":"ability.test.buff","name":"测试增益","damageRatio":0,"flatDamage":0,"buff":{"buffId":"buff.test","name":"护体"},"castMilliseconds":1000,"cooldownMilliseconds":5000}
            """);
        var buffTriggers = SkillSlot.InferDefaultTriggers(buffAbility);
        Assert.Single(buffTriggers);
        Assert.Equal("self", buffTriggers[0].GetProperty("dependency").GetString());
        Assert.Equal("buff-stacks", buffTriggers[0].GetProperty("condition").GetString());
        Assert.Equal("equal", buffTriggers[0].GetProperty("comparator").GetString());
        Assert.Equal(0.0, buffTriggers[0].GetProperty("value").GetDouble());
        Assert.Equal("buff.test", buffTriggers[0].GetProperty("buffId").GetString());
    }
}
