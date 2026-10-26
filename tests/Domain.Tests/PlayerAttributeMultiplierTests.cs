namespace Domain.Tests;

/// <summary>
/// 百分比加成三大乘区收拢门禁（TASK-20261026-03）：
/// 1. 装备百分比、特质百分比与灵根百分比在【常驻外物大乘区】内严格加算合并，杜绝多系统复合连乘指数膨胀；
/// 2. 不同的 Buff 百分比在【Buff 乘区】内严格加算合并（如两个 20% 加算为 40% 即 1.4x，而非 1.44x）；
/// 3. 六维资质百分比独立放大。
/// </summary>
public sealed class PlayerAttributeMultiplierTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlayerAggregate CreatePlayer(GameDataCatalog catalog)
        => PlayerAggregate.Create(PlayerId.New(), AccountId.New(), "乘区测试", Baseline, catalog.Fingerprint);

    [Fact]
    public void Equipment_Trait_Linggen_CombineAdditivelyInSingleGeneralMultiplier()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var baseAttr = catalog.BaseAttributes!.At(player.Progression.RealmLevel);

        // 1. 金灵根 100 点：物攻 +50% (0.50)
        player.Progression.HeavenGate.Apply(
            new LinggenValues(Metal: 100, Wood: 0, Water: 0, Fire: 0, Earth: 0),
            new LinggenValues(Metal: 100, Wood: 0, Water: 0, Fire: 0, Earth: 0),
            entered: true, rerollCount: 0);

        // 2. 特质天生蛮力 (trait.tianshengmanli)：物攻 +10% (0.10)
        player.Progression.TraitIds.Add("trait.tianshengmanli");
        player.Progression.UnlockedTraits["trait.tianshengmanli"] = new("trait.tianshengmanli", 1, 0);

        // 3. 装备青锋剑 (equipment.qingfeng-sword)：物攻 +20% (0.20)，物攻固定值 +10
        const string swordId = "equipment.qingfeng-sword";
        player.Loadout.EquippedItemIds.Add(new ItemId(swordId));
        player.Loadout.Equipment.Add(new PlayerEquipmentSlot(swordId, EnhancementLevel: 0));

        var resolved = PlayerAttributeResolver.Resolve(player, catalog);

        // 配表属性提取
        var sword = catalog.RawEquipments[swordId];
        var swordFlat = sword.GetProperty("flatStats").GetProperty("physicalAttack").GetDouble();
        var swordRatio = sword.GetProperty("baseStatRatios").GetProperty("physicalAttack").GetDouble();

        // 验证三者比例累加：灵根 0.50 + 特质 0.10 + 装备 0.20 = 0.80 (1.80x)
        var expectedTotalRatio = 0.50 + 0.10 + swordRatio;
        var expectedAttack = (baseAttr.Ad + swordFlat) * (1.0 + expectedTotalRatio);

        // 必须严格等于加算后的 1.8x 系数，绝不能等于旧口径的独立连乘 (1 + 0.5 + 0.2) * 1.1 = 1.87x
        Assert.Equal(expectedAttack, resolved.PhysicalAttack, precision: 6);

        var oldMultiplicative = (baseAttr.Ad + swordFlat) * (1.0 + 0.50 + swordRatio) * (1.0 + 0.10);
        Assert.NotEqual(oldMultiplicative, resolved.PhysicalAttack);
    }

    [Fact]
    public void MultipleBuffs_CombineAdditivelyInBuffMultiplier()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);
        var baseAttr = catalog.BaseAttributes!.At(player.Progression.RealmLevel);
        var expires = Baseline.AddMinutes(5).ToUnixTimeSeconds();

        // 施加两个不同的面板 Buff，各自提供 +20% (0.20) 物理攻击
        player.Buffs.Active.Add(new ActiveBuff(
            "buff.test-attack-a",
            expires,
            StatRatios: new Dictionary<string, double> { ["physicalAttack"] = 0.20 }));

        player.Buffs.Active.Add(new ActiveBuff(
            "buff.test-attack-b",
            expires,
            StatRatios: new Dictionary<string, double> { ["physicalAttack"] = 0.20 }));

        var resolved = PlayerAttributeResolver.Resolve(player, catalog);

        // 两个 Buff 加算：20% + 20% = 40% (1.40x)
        var expectedAttack = baseAttr.Ad * (1.0 + 0.40);
        Assert.Equal(expectedAttack, resolved.PhysicalAttack, precision: 6);

        // 严禁连乘 1.2 * 1.2 = 1.44x
        var forbiddenMultiplicative = baseAttr.Ad * 1.20 * 1.20;
        Assert.NotEqual(forbiddenMultiplicative, resolved.PhysicalAttack);
    }

    [Fact]
    public void CultivationExpBonus_ScalesWithInternalAbilityLevel()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var player = CreatePlayer(catalog);

        // 1. 装备 1 级《炼精化气诀》
        const string internalId = "ability.lianjing-huaqi-jue";
        player.LearnAbility(new(internalId));
        player.Loadout.AbilitySlots[0] = new PlayerAbilitySlot(internalId, 1);

        var resolvedLv1 = PlayerAttributeResolver.Resolve(player, catalog);
        // Lv1 基础点数为 20
        Assert.Equal(20.0, resolvedLv1.CultivationExpBonusPoints, precision: 6);
        Assert.Equal(0.0, resolvedLv1.CombatExpBonusPoints, precision: 6);

        // 2. 升为 2 级（+5% 成长：20 * 1.05 = 21）
        player.Loadout.AbilitySlots[0] = new PlayerAbilitySlot(internalId, 2);
        var resolvedLv2 = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Equal(21.0, resolvedLv2.CultivationExpBonusPoints, precision: 6);

        // 3. 升为 5 级（成长 1 + 0.05 * 4 = 1.20：20 * 1.2 = 24）
        player.Loadout.AbilitySlots[0] = new PlayerAbilitySlot(internalId, 5);
        var resolvedLv5 = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Equal(24.0, resolvedLv5.CultivationExpBonusPoints, precision: 6);

        // 4. 换装 1 级《焚血功》：产生 -10 战斗修为获取
        const string fenxueId = "ability.fenxue-gong";
        player.LearnAbility(new(fenxueId));
        player.Loadout.AbilitySlots[0] = new PlayerAbilitySlot(fenxueId, 1);
        var resolvedFenxue = PlayerAttributeResolver.Resolve(player, catalog);
        Assert.Equal(0.0, resolvedFenxue.CultivationExpBonusPoints, precision: 6);
        Assert.Equal(-10.0, resolvedFenxue.CombatExpBonusPoints, precision: 6);
    }
}
