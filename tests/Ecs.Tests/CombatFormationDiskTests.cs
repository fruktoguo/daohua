using DaoHua.Ecs;
using Domain.Combat;
using Xunit;

namespace Ecs.Tests;

/// <summary>
/// 三大阵盘（天雷阵盘、血煞阵盘、迷踪阵盘）效果与机制不变量门禁。
/// </summary>
public sealed class CombatFormationDiskTests
{
    private static (CombatEncounterState Encounter, CombatantState Player, CombatantState Enemy) CreateEncounter(
        CombatantStats playerStats, CombatantStats enemyStats)
    {
        var encounter = CombatEncounterState.Create(
            "test-encounter",
            42,
            new CombatantTemplate(playerStats),
            [new CombatantTemplate(enemyStats)]);
        return (encounter, encounter.Player, encounter.Enemies[0]);
    }

    [Fact]
    public void TianleiFormation_TriggersOnEnemyHit_ConsumesMana_AndHasOneSecondCooldown()
    {
        // 玩家：SpellAttack = 100, MaxManapoints = 200, 满蓝
        var playerStats = CombatantStats.Create(1000, 20, 10, 2.0) with
        {
            SpellAttack = 100,
            MaxManapoints = 200
        };
        // 敌人：气血 1000，法抗 0
        var enemyStats = CombatantStats.Create(1000, 10, 0, 2.0) with { MagicResistance = 0 };

        var (encounter, player, enemy) = CreateEncounter(playerStats, enemyStats);
        player.Manapoints = 200;
        player.AddBuff(new CombatBuffState("formation.tianlei", 300, 1));

        // 构造命中上下文：敌人受到 50 点普通伤害
        var context = new CombatHitContext
        {
            Encounter = encounter,
            Attacker = player,
            Target = enemy,
            AttackStats = player.EffectiveStats,
            Side = 0,
            Count = 2,
            AtSeconds = 1.0,
            Hit = true,
            Damage = 50,
            RawDamage = 50
        };

        // 第一次派发阵盘反击（第 1 秒）
        CombatTriggerMath.FormationReactiveStrike(context);

        // 验证：
        // 1. 产生衍生事件
        Assert.NotNull(context.DerivedEvents);
        Assert.Single(context.DerivedEvents);
        var derived = context.DerivedEvents[0];
        Assert.Equal("item.tianlei-formation-disk", derived.AbilityId);
        Assert.Equal(CombatDamageKind.Spell, derived.DamageKind);

        // 2. 扣除 1% 最大法力值 (200 * 0.01 = 2)
        Assert.Equal(198, player.Manapoints);

        // 3. 伤害：使用者 50% * 100 + 10 = 60 点木系法伤，经 0 抗性穿甲收口 60*(60/61)
        var expectedDamage = 60.0 * 60.0 / 61.0;
        Assert.Equal(expectedDamage, derived.Damage, 5);
        Assert.Equal(1000 - expectedDamage, enemy.Hitpoints, 5);

        // 4. 验证 1.0 秒内置冷却：在第 1.5 秒再次命中敌人，不应再次触发
        var context2 = new CombatHitContext
        {
            Encounter = encounter,
            Attacker = player,
            Target = enemy,
            AttackStats = player.EffectiveStats,
            Side = 0,
            Count = 6,
            AtSeconds = 1.5, // 距上次仅 0.5s，在 1.0s ICD 内
            Hit = true,
            Damage = 50,
            RawDamage = 50
        };
        CombatTriggerMath.FormationReactiveStrike(context2);
        Assert.Null(context2.DerivedEvents); // 未触发
        Assert.Equal(198, player.Manapoints); // 法力未多扣

        // 5. 在第 2.1 秒再次命中敌人（已过 1.0s），应再次触发
        var context3 = new CombatHitContext
        {
            Encounter = encounter,
            Attacker = player,
            Target = enemy,
            AttackStats = player.EffectiveStats,
            Side = 0,
            Count = 6,
            AtSeconds = 2.1,
            Hit = true,
            Damage = 50,
            RawDamage = 50
        };
        CombatTriggerMath.FormationReactiveStrike(context3);
        Assert.NotNull(context3.DerivedEvents);
        Assert.Equal(196, player.Manapoints); // 再次扣 2 点蓝
    }

    [Fact]
    public void XueshaFormation_DealsTenPercentMaxHpPhysicalDamage_AndLeechesOneHundredPercent()
    {
        // 玩家：MaxHitpoints = 800，当前 400（半血）
        var playerStats = CombatantStats.Create(800, 20, 10, 2.0);
        // 敌人：气血 1000，护甲 0
        var enemyStats = CombatantStats.Create(1000, 10, 0, 2.0) with { Defense = 0 };

        var (encounter, player, enemy) = CreateEncounter(playerStats, enemyStats);
        player.Hitpoints = 400;
        player.AddBuff(new CombatBuffState("formation.xuesha", 300, 1));

        var context = new CombatHitContext
        {
            Encounter = encounter,
            Attacker = player,
            Target = enemy,
            AttackStats = player.EffectiveStats,
            Side = 0,
            Count = 2,
            AtSeconds = 1.0,
            Hit = true,
            Damage = 30,
            RawDamage = 30
        };

        CombatTriggerMath.FormationReactiveStrike(context);

        // 验证：
        Assert.NotNull(context.DerivedEvents);
        Assert.Single(context.DerivedEvents);
        var derived = context.DerivedEvents[0];
        Assert.Equal("item.xuesha-formation-disk", derived.AbilityId);
        Assert.Equal(CombatDamageKind.Physical, derived.DamageKind);

        // 伤害：使用者 10% * 800 = 80 点物理伤害（无五行），经 0 物防穿甲收口 80*(80/81)
        var expectedDamage = 80.0 * 80.0 / 81.0;
        Assert.Equal(expectedDamage, derived.Damage, 5);
        Assert.Equal(1000 - expectedDamage, enemy.Hitpoints, 5);

        // 100% 吸血治疗：使用者气血从 400 + expectedDamage
        Assert.Equal(400 + expectedDamage, player.Hitpoints, 5);
        Assert.Equal(expectedDamage, derived.Healing, 5);
    }

    [Fact]
    public void TianleiFormation_DoesNotTrigger_WhenManaIsInsufficient()
    {
        var playerStats = CombatantStats.Create(1000, 20, 10, 2.0) with
        {
            SpellAttack = 100,
            MaxManapoints = 200
        };
        var enemyStats = CombatantStats.Create(1000, 10, 0, 2.0);

        var (encounter, player, enemy) = CreateEncounter(playerStats, enemyStats);
        player.Manapoints = 1.0; // 1% 需要 2.0，当前仅 1.0，不足
        player.AddBuff(new CombatBuffState("formation.tianlei", 300, 1));

        var context = new CombatHitContext
        {
            Encounter = encounter,
            Attacker = player,
            Target = enemy,
            AttackStats = player.EffectiveStats,
            Side = 0,
            Count = 2,
            AtSeconds = 1.0,
            Hit = true,
            Damage = 50,
            RawDamage = 50
        };

        CombatTriggerMath.FormationReactiveStrike(context);

        // 法力不足，不触发雷击
        Assert.Null(context.DerivedEvents);
        Assert.Equal(1.0, player.Manapoints);
    }
}
