using System.Text.Json;
using Domain.Combat;
using Domain.GameData;
using Domain.Player;

namespace Domain.Tests;

/// <summary>
/// 百分比点数属性模型测试（战斗设计 §7.5/§7.1）：增减伤 Linear/NonLinear 结算链、
/// 速率点数读条与冷却结算、装备 flatStats 点数键汇总到属性面板。
/// </summary>
public sealed class CombatAttributePointsTests
{
    /// <summary>基础伤害锚点：100 AD 对 0 甲目标的普攻实伤 = 100×100/101。</summary>
    private const double BaseDamage = 100d * 100 / 101;

    private static double FirstHitDamage(CombatantStats attacker, CombatantStats defender)
    {
        var state = DeterministicCombatEngine.Start("points", 7, attacker, defender);
        var hit = DeterministicCombatEngine.AdvanceTo(state, 1.01).Events[0];
        Assert.True(hit.Hit);
        return hit.Damage;
    }

    /// <summary>增伤点数按 Linear 逐项乘算：大类 ×(1+x/100)、五行按攻击元素匹配、总增伤通用。</summary>
    [Fact]
    public void DamageBonusPointsMultiplyLinearly()
    {
        var defender = CombatantStats.Create(100000, 0, 0, 2);
        Assert.Equal(BaseDamage, FirstHitDamage(
            CombatantStats.Create(1000, 100, 0, 1), defender), 10);
        Assert.Equal(BaseDamage * 1.5, FirstHitDamage(
            CombatantStats.Create(1000, 100, 0, 1) with { PhysicalDamageBonusPoints = 50 }, defender), 10);
        // 五行增伤仅在攻击携带对应元素时生效。
        Assert.Equal(BaseDamage * 2, FirstHitDamage(
            CombatantStats.Create(1000, 100, 0, 1) with { Element = CombatElement.Fire, FireDamageBonusPoints = 100 }, defender), 10);
        Assert.Equal(BaseDamage, FirstHitDamage(
            CombatantStats.Create(1000, 100, 0, 1) with { Element = CombatElement.Metal, FireDamageBonusPoints = 100 }, defender), 10);
        Assert.Equal(BaseDamage * 2 * 1.5, FirstHitDamage(
            CombatantStats.Create(1000, 100, 0, 1) with { TotalDamageBonusPoints = 100, PhysicalDamageBonusPoints = 50 }, defender), 10);
    }

    /// <summary>减伤点数按 NonLinear 逐项收敛：大类/五行/总减伤 ×100/(100+x)，永不归零。</summary>
    [Fact]
    public void DamageReductionPointsConvergeNonLinearly()
    {
        var attacker = CombatantStats.Create(1000, 100, 0, 1) with { Element = CombatElement.Fire };
        Assert.Equal(BaseDamage * 0.5, FirstHitDamage(attacker,
            CombatantStats.Create(100000, 0, 0, 2) with { PhysicalDamageReductionPoints = 100 }), 10);
        Assert.Equal(BaseDamage * 0.5, FirstHitDamage(attacker,
            CombatantStats.Create(100000, 0, 0, 2) with { FireDamageReductionPoints = 100 }), 10);
        // 非对应五行减伤不生效。
        Assert.Equal(BaseDamage, FirstHitDamage(attacker,
            CombatantStats.Create(100000, 0, 0, 2) with { WoodDamageReductionPoints = 100 }), 10);
        Assert.Equal(BaseDamage * 0.5, FirstHitDamage(attacker,
            CombatantStats.Create(100000, 0, 0, 2) with { DamageAbsorptionPoints = 100 }), 10);
        // 三项收敛乘算：0.5³ = 0.125。
        Assert.Equal(BaseDamage * 0.125, FirstHitDamage(attacker,
            CombatantStats.Create(100000, 0, 0, 2) with
            { PhysicalDamageReductionPoints = 100, FireDamageReductionPoints = 100, DamageAbsorptionPoints = 100 }), 10);
    }

    /// <summary>攻击速度点数（NonLinear）只缩短普攻读条：100 点 → 读条减半。</summary>
    [Fact]
    public void AttackSpeedPointsHalveBasicAttackInterval()
    {
        var enemy = CombatantStats.Create(100000, 0, 0, 2);
        var state = DeterministicCombatEngine.Start("speed", 7,
            CombatantStats.Create(1000, 100, 0, 2) with { AttackSpeedPoints = 100 }, enemy);
        Assert.Equal(1, state.Player.NextAttackAtSeconds, 9);
        // 行动速度同样作用于普攻读条（双重除数）。
        var both = DeterministicCombatEngine.Start("speed", 7,
            CombatantStats.Create(1000, 100, 0, 2) with { AttackSpeedPoints = 100, ActionSpeedPoints = 100 }, enemy);
        Assert.Equal(0.5, both.Player.NextAttackAtSeconds, 9);
    }

    /// <summary>行动速度点数（NonLinear）缩短技能吟唱读条；冷却缩减点数缩短冷却窗。</summary>
    [Fact]
    public void ActionSpeedAndCooldownReductionCompressCastAndCooldown()
    {
        var ability = JsonDocument.Parse("""
            {"id":"ability.test","category":"physical","element":"none","castMilliseconds":4000,
             "cooldownMilliseconds":8000,"manaCost":0,"damageRatio":1}
            """).RootElement;
        var enemy = CombatantStats.Create(100000, 0, 0, 60);
        var slow = DeterministicCombatEngine.Start("cdr", 7,
            new CombatantTemplate(CombatantStats.Create(1000, 100, 0, 60) with { MaxManapoints = 100 },
                [new SkillSlot(ability.Clone(), 1)]),
            [new CombatantTemplate(enemy)]);
        var fast = DeterministicCombatEngine.Start("cdr", 7,
            new CombatantTemplate(CombatantStats.Create(1000, 100, 0, 60) with
            { MaxManapoints = 100, ActionSpeedPoints = 100, CooldownReductionPoints = 100 },
                [new SkillSlot(ability.Clone(), 1)]),
            [new CombatantTemplate(enemy)]);
        // 吟唱 4s：行动速度 100 点 → 2s；冷却 8s：冷却缩减 100 点 → 4s（自吟唱完成起算）。
        Assert.Equal(4, slow.Player.NextAttackAtSeconds, 9);
        Assert.Equal(4 + 8, slow.Player.SkillCooldownUntil![0], 9);
        Assert.Equal(2, fast.Player.NextAttackAtSeconds, 9);
        Assert.Equal(2 + 4, fast.Player.SkillCooldownUntil![0], 9);
    }

    /// <summary>装备 flatStats 点数键逐项汇入属性面板（含增减伤/速度/汲取全套词汇表）。</summary>
    [Fact]
    public void EquipmentMechanicsPointsFlowIntoResolvedAttributes()
    {
        var attributes = new BaseAttributeCatalog([new(1, 1000, 100, 10, 5, 10, 0, 100, 0, 0, 0, 0, 0, 7, 3)]);
        using var equipment = JsonDocument.Parse("""
            {"flatStats":{"physicalDamageBonusPoints":50,"fireDamageReductionPoints":100,
             "attackSpeedPoints":25,"cooldownReductionPoints":50,"threatBonusPoints":200,
             "hpDrainPoints":10,"healingBonusPoints":50}}
            """);
        var item = new ItemId("equipment.points");
        var catalog = new GameDataCatalog("test", "test", new ActionCatalog([]), baseAttributes: attributes,
            rawEquipments: new Dictionary<string, JsonElement> { [item.Value] = equipment.RootElement });
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "点数", DateTimeOffset.UnixEpoch, "test");
        var state = player.ExportState();
        var resolved = PlayerAttributeResolver.Resolve(
            PlayerAggregate.Restore(state with
            {
                Loadout = state.Loadout with { CoreAttributes = new(0, 0, 0, 0, 0, 0) },
                EquippedItemIds = [item]
            }), catalog);
        Assert.Equal(50, resolved.PhysicalDamageBonusPoints);
        Assert.Equal(100, resolved.FireDamageReductionPoints);
        Assert.Equal(25, resolved.AttackSpeedPoints);
        Assert.Equal(50, resolved.CooldownReductionPoints);
        Assert.Equal(200, resolved.ThreatBonusPoints);
        Assert.Equal(10, resolved.Effects.HpDrainPoints);
        Assert.Equal(50, resolved.Effects.HealingBonusPoints);
        // 点数原样透传到战斗单位属性（ToCombatant 不缩放）。
        var stats = resolved.ToCombatant(0);
        Assert.Equal(resolved.PhysicalDamageBonusPoints, stats.PhysicalDamageBonusPoints);
        Assert.Equal(resolved.FireDamageReductionPoints, stats.FireDamageReductionPoints);
        Assert.Equal(resolved.Critical, stats.Critical);
        Assert.Equal(resolved.CriticalAvoidance, stats.CriticalResistance);
    }

    /// <summary>Buff 点数修饰键汇入属性面板：攻速与免伤按层数叠加。</summary>
    [Fact]
    public void BuffPointsAggregateIntoResolvedAttributes()
    {
        var attributes = new BaseAttributeCatalog([new(1, 1000, 100, 10, 5, 10, 0, 100, 0, 0, 0, 0, 0, 7, 3)]);
        var catalog = new GameDataCatalog("test", "test", new ActionCatalog([]), baseAttributes: attributes);
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "增益", DateTimeOffset.UnixEpoch, "test");
        player.Buffs.Active.Add(new ActiveBuff("buff.test", 100, 2, "测试", null, 0, 0, 25, 15));
        var resolved = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Equal(50, resolved.AttackSpeedPoints);
        Assert.Equal(30, resolved.DamageAbsorptionPoints);
    }

    /// <summary>
    /// 赤军重装生存套与破罡裂风输出套穿戴验证：
    /// 赤军套：物理减伤 +6% (各 2%)，威胁增幅 +50% (衣服 20%、头腿各 15%)，无物理增伤；
    /// 裂风套：物理增伤 +6% (各 2%)，物攻/命中/暴击提升，无物理减伤与威胁增幅。
    /// </summary>
    [Fact]
    public void RedArmyAndWindSetEquippedAttributesTest()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "装备测试", DateTimeOffset.UnixEpoch, catalog.Fingerprint);

        // 1. 穿戴赤军重装生存套（赤金盔、鱼鳞甲、踏阵胫）
        var redArmySlots = new[]
        {
            new PlayerEquipmentSlot("equipment.chijin-helmet", 0),
            new PlayerEquipmentSlot("equipment.yulin-armor", 0),
            new PlayerEquipmentSlot("equipment.tazhen-greaves", 0),
        };
        player.ReplaceCombatSlots([], redArmySlots);
        var redArmyResolved = PlayerAttributeResolver.Resolve(player, catalog);

        // 物理减伤 = 2 + 2 + 2 = 6%
        Assert.Equal(6, redArmyResolved.PhysicalDamageReductionPoints);
        // 威胁增幅 = 15 + 20 + 15 = 50%
        Assert.Equal(50, redArmyResolved.ThreatBonusPoints);
        // 无物理增伤
        Assert.Equal(0, redArmyResolved.PhysicalDamageBonusPoints);

        // 2. 穿戴破罡裂风输出套（斩风巾、裂罡甲、踏风裤）
        var windSlots = new[]
        {
            new PlayerEquipmentSlot("equipment.zhanfeng-scarf", 0),
            new PlayerEquipmentSlot("equipment.liegang-armor", 0),
            new PlayerEquipmentSlot("equipment.tafeng-pants", 0),
        };
        player.ReplaceCombatSlots([], windSlots);
        var windResolved = PlayerAttributeResolver.Resolve(player, catalog);

        // 物理增伤 = 2 + 2 + 2 = 6%
        Assert.Equal(6, windResolved.PhysicalDamageBonusPoints);
        // 无物理减伤
        Assert.Equal(0, windResolved.PhysicalDamageReductionPoints);
        // 无威胁增幅
        Assert.Equal(0, windResolved.ThreatBonusPoints);

        // 物理输出套攻击与暴击显著高于生存套
        Assert.True(windResolved.PhysicalAttack > redArmyResolved.PhysicalAttack);
        Assert.True(windResolved.Critical > redArmyResolved.Critical);
        Assert.True(windResolved.Accuracy > redArmyResolved.Accuracy);
        // 生存套双防与生命显著高于输出套
        Assert.True(redArmyResolved.Armor > windResolved.Armor);
        Assert.True(redArmyResolved.MagicResistance > windResolved.MagicResistance);
        Assert.True(redArmyResolved.MaxHitpoints > windResolved.MaxHitpoints);
    }
}
