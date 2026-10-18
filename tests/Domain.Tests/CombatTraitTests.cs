namespace Domain.Tests;

/// <summary>
/// 特质系统（Trait）数值与规则回归门禁（总纲 §7.12）：
/// 覆盖特质点容量推算、品阶消耗阶梯、属性百分比独立乘区、点数通道汇入及容量溢出防护。
/// </summary>
public sealed class CombatTraitTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(string name = "特质道友", int realmLevel = 1)
    {
        var player = PlayerAggregate.Create(PlayerId.New(), AccountId.New(), name, Baseline, "fp");
        player.Progression.RealmLevel = realmLevel;
        return player;
    }

    [Fact]
    public void TraitCapacityFollowsProgressionRules()
    {
        // Lv1 凡胎：初始 5 点
        Assert.Equal(5, CombatTraitRules.CalculateCapacity(1));
        // Lv6 凡俗武极：5 + 5 = 10 点
        Assert.Equal(10, CombatTraitRules.CalculateCapacity(6));
        // Lv7 跨入玄阶段：10 + 1(升1级) + 1(阶段) = 12 点
        Assert.Equal(12, CombatTraitRules.CalculateCapacity(7));
        // Lv12 凡俗玄极：12 + 5 = 17 点
        Assert.Equal(17, CombatTraitRules.CalculateCapacity(12));
        // Lv13 跨入天阶段：17 + 1 + 1 = 19 点
        Assert.Equal(19, CombatTraitRules.CalculateCapacity(13));
        // Lv18 叩天门（凡俗圆满）：19 + 5 = 24 点
        Assert.Equal(24, CombatTraitRules.CalculateCapacity(18));
        // Lv19 破境入练气：24 + 1(升级) + 1(练气大境界) + 1(练气初期) = 27 点
        Assert.Equal(27, CombatTraitRules.CalculateCapacity(19));
        // Lv30 练气圆满：27 + 11(升级) + 3(中/后/极阶段) = 41 点
        Assert.Equal(41, CombatTraitRules.CalculateCapacity(30));
        // Lv31 破境入筑基：41 + 1(升级) + 1(筑基大境界) + 1(筑基初期) = 44 点
        Assert.Equal(44, CombatTraitRules.CalculateCapacity(31));
        // Lv126 渡劫圆满：177 点
        Assert.Equal(177, CombatTraitRules.CalculateCapacity(126));
        // Lv127 飞升：182 点
        Assert.Equal(182, CombatTraitRules.CalculateCapacity(127));
    }

    [Fact]
    public void TraitCostAndAllTraitsRegistryAreConsistent()
    {
        // 注册表包含全部 28 个基础数值特质 + 2 个首批机制特质
        Assert.True(CombatTraitRules.AllTraits.Count >= 30);

        // 验证品阶与消耗严格对应
        Assert.Equal(2, CombatTraitRules.AllTraits["trait.tianshengmanli"].Cost); // 凡品
        Assert.Equal(5, CombatTraitRules.AllTraits["trait.tianshengjiangu"].Cost); // 黄品
        Assert.Equal(10, CombatTraitRules.AllTraits["trait.chunyangzhenmai"].Cost); // 玄品
        Assert.Equal(15, CombatTraitRules.AllTraits["trait.houtukunyushen"].Cost); // 地品
        Assert.Equal(20, CombatTraitRules.AllTraits["trait.yaodutongyuan"].Cost); // 天品
        Assert.Equal(20, CombatTraitRules.AllTraits["trait.wanjianguiyi"].Cost); // 天品

        // 总消耗计算
        string[] selected = ["trait.tianshengmanli", "trait.tianshengjiangu", "trait.yaodutongyuan"];
        Assert.Equal(2 + 5 + 20, CombatTraitRules.TotalCost(selected));
    }

    [Fact]
    public void SetTraitsEnforcesCapacityLimit()
    {
        var player = CreatePlayer(realmLevel: 1); // 容量 5 点
        // 凡品 2 点 + 凡品 2 点 = 4 点 <= 5 点，成功
        player.Progression.SetTraits(["trait.tianshengmanli", "trait.qiganchongying"]);
        Assert.Equal(2, player.Progression.TraitIds.Count);

        // 尝试装配 天品 (20 点) > 5 点，抛出 InvalidArgument 异常
        var ex = Assert.Throws<DomainException>(() =>
            player.Progression.SetTraits(["trait.yaodutongyuan"]));
        Assert.Equal(DomainErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void SingleTraitAmplifiesAttributeCorrectly()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var playerNoTrait = CreatePlayer(realmLevel: 1);
        var statsBase = PlayerAttributeResolver.Resolve(playerNoTrait, catalog);

        var playerWithTrait = CreatePlayer(realmLevel: 1);
        playerWithTrait.Progression.SetTraits(["trait.tianshengmanli"]); // 物攻 +10%
        var statsAmplified = PlayerAttributeResolver.Resolve(playerWithTrait, catalog);

        // 物理攻击严格放大 1.10 倍（浮点容差）
        Assert.Equal(statsBase.PhysicalAttack * 1.10, statsAmplified.PhysicalAttack, 3);
        // 其他非关联属性不受影响
        Assert.Equal(statsBase.SpellAttack, statsAmplified.SpellAttack, 3);
        Assert.Equal(statsBase.MaxHitpoints, statsAmplified.MaxHitpoints, 3);
    }

    [Fact]
    public void DualTraitCompoundsInSamePipeline()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(realmLevel: 18); // 容量 24 点
        // 天生蛮力 (物攻 +10%) + 天生剑骨 (物攻 +20%, 暴击 +10%)
        player.Progression.SetTraits(["trait.tianshengmanli", "trait.tianshengjiangu"]);

        var stats = PlayerAttributeResolver.Resolve(player, catalog);
        var baseline = PlayerAttributeResolver.Resolve(CreatePlayer(realmLevel: 18), catalog);

        // 物攻百分比累加为 +30% -> SoftRatio(0.30) = 1.30
        Assert.Equal(baseline.PhysicalAttack * 1.30, stats.PhysicalAttack, 3);
        // 暴击等级同样享受 +10% 放大
        Assert.Equal(baseline.Critical * 1.10, stats.Critical, 3);
    }

    [Fact]
    public void CoreHeavenGradeTraitsApplyExtremeModifiersAndPenalties()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var baseStats = PlayerAttributeResolver.Resolve(CreatePlayer(realmLevel: 18), catalog);

        // 1. 药毒同源：自动回复 +100%、最大生命 +40%、全伤害降低 35%
        var playerMedic = CreatePlayer(realmLevel: 18);
        playerMedic.Progression.SetTraits(["trait.yaodutongyuan"]);
        var statsMedic = PlayerAttributeResolver.Resolve(playerMedic, catalog);

        Assert.Equal(baseStats.MaxHitpoints * 1.40, statsMedic.MaxHitpoints, 3);
        Assert.Equal(baseStats.HpRegen * 2.00, statsMedic.HpRegen, 3);
        Assert.Equal(baseStats.MpRegen * 2.00, statsMedic.MpRegen, 3);
        Assert.Equal(-35, statsMedic.TotalDamageBonusPoints);

        // 2. 万剑归一：物攻 +65%、暴击 +30%、破招 +25%、最大生命 -30%、双减伤各 -25 点
        var playerSword = CreatePlayer(realmLevel: 18);
        playerSword.Progression.SetTraits(["trait.wanjianguiyi"]);
        var statsSword = PlayerAttributeResolver.Resolve(playerSword, catalog);

        Assert.Equal(baseStats.PhysicalAttack * 1.65, statsSword.PhysicalAttack, 3);
        Assert.Equal(baseStats.Critical * 1.30, statsSword.Critical, 3);
        Assert.Equal(baseStats.BreakPower * 1.25, statsSword.BreakPower, 3);
        // 负百分比软着陆：1 / (1 + 0.30) ≈ 0.7692 倍
        Assert.Equal(baseStats.MaxHitpoints / 1.30, statsSword.MaxHitpoints, 3);
        Assert.Equal(-25, statsSword.PhysicalDamageReductionPoints);
        Assert.Equal(-25, statsSword.SpellDamageReductionPoints);

        // 3. 不动明王：双抗 +80%、最大生命 +50%、全伤害降低 45%
        var playerTank = CreatePlayer(realmLevel: 18);
        playerTank.Progression.SetTraits(["trait.budongmingwang"]);
        var statsTank = PlayerAttributeResolver.Resolve(playerTank, catalog);

        Assert.Equal(baseStats.Armor * 1.80, statsTank.Armor, 3);
        Assert.Equal(baseStats.MagicResistance * 1.80, statsTank.MagicResistance, 3);
        Assert.Equal(baseStats.MaxHitpoints * 1.50, statsTank.MaxHitpoints, 3);
        Assert.Equal(-45, statsTank.TotalDamageBonusPoints);

        // 4. 九幽噬灵：术法 +65%、最大灵力 +50%、暴击 +20%、物理护甲 -35%、闪避 -30%
        var playerMage = CreatePlayer(realmLevel: 18);
        playerMage.Progression.SetTraits(["trait.jiuyoushiling"]);
        var statsMage = PlayerAttributeResolver.Resolve(playerMage, catalog);

        Assert.Equal(baseStats.SpellAttack * 1.65, statsMage.SpellAttack, 3);
        Assert.Equal(baseStats.MaxManapoints * 1.50, statsMage.MaxManapoints, 3);
        Assert.Equal(baseStats.Critical * 1.20, statsMage.Critical, 3);
        Assert.Equal(baseStats.Armor / 1.35, statsMage.Armor, 3);
        Assert.Equal(baseStats.Evasion / 1.30, statsMage.Evasion, 3);
    }

    [Fact]
    public void EarthGradeTraitsApplyModifiersCorrectly()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var baseStats = PlayerAttributeResolver.Resolve(CreatePlayer(realmLevel: 18), catalog);

        // 厚土坤舆身 (地品 15 点)：双抗 +45%、最大生命 +35%、全伤害降低 15%
        var playerEarth = CreatePlayer(realmLevel: 18);
        playerEarth.Progression.SetTraits(["trait.houtukunyushen"]);
        var statsEarth = PlayerAttributeResolver.Resolve(playerEarth, catalog);

        Assert.Equal(baseStats.Armor * 1.45, statsEarth.Armor, 3);
        Assert.Equal(baseStats.MagicResistance * 1.45, statsEarth.MagicResistance, 3);
        Assert.Equal(baseStats.MaxHitpoints * 1.35, statsEarth.MaxHitpoints, 3);
        Assert.Equal(-15, statsEarth.TotalDamageBonusPoints);
    }
}
