namespace Domain.Tests;

/// <summary>
/// 游戏数据编目测试：内置目录有效性、JSON 指纹、嵌入资源与持续消耗品效果解析。
/// </summary>
public sealed class GameDataCatalogTests
{
    /// <summary>
    /// 验证内置目录与嵌入资源加载均非空且包含核心行动。
    /// </summary>
    [Fact]
    public void BuiltInAndEmbeddedCatalogsAreValid()
    {
        var builtIn = GameDataCatalog.BuiltIn;
        Assert.NotEmpty(builtIn.Actions.All);
        Assert.Contains(builtIn.Actions.All, action => action.Id.Value == "cultivation.draw-qi");

        var embedded = GameDataCatalog.LoadEmbedded();
        Assert.NotEmpty(embedded.Actions.All);
        Assert.NotEmpty(embedded.Items);
        Assert.NotEmpty(embedded.Maps);
        Assert.NotEmpty(embedded.Encounters);
        Assert.False(string.IsNullOrWhiteSpace(embedded.Fingerprint));
    }

    /// <summary>
    /// 相同 JSON 资源两次解析必须产生相同指纹（存档/协议一致性前提），且字段定义正确落位。
    /// </summary>
    [Fact]
    public void JsonResourcesProduceStableFingerprintAndDefinitions()
    {
        var manifest = "{\"schemaVersion\":1,\"contentVersion\":\"test-1\"}"u8.ToArray();
        var actions = "[{\"id\":\"world.travel\",\"name\":\"赶路\",\"type\":\"travel\",\"durationSeconds\":1,\"hiddenFromCatalog\":true}]"u8.ToArray();
        var first = GameDataCatalog.Parse([manifest, actions]);
        var second = GameDataCatalog.Parse([manifest, actions]);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal("test-1", first.ContentVersion);
        var travel = first.Actions.Get(new ActionId("world.travel"));
        Assert.True(travel.HiddenFromCatalog);
        Assert.Equal(ActionKind.Travel, travel.Kind);
    }

    /// <summary>
    /// 嵌入配表中的持续型消耗品（聚气丹）必须解析出持续时长、修炼倍率与适用行动类型，
    /// 防止配表结构调整后自动用药静默失效。
    /// </summary>
    [Fact]
    public void EmbeddedCatalogParsesSustainedConsumableEffects()
    {
        var catalog = GameDataCatalog.LoadEmbedded();

        var pill = catalog.Items[new ItemId("item.qi-gathering-pill")];
        var consumable = Assert.IsType<ConsumableDefinition>(pill.Consumable);
        Assert.True(consumable.IsContinuous);
        Assert.Equal(300d, consumable.DurationSeconds);
        Assert.Equal(1.3d, consumable.CultivationMultiplier);
        Assert.Contains(ActionKind.Cultivation, consumable.UsableInActionKinds);
    }

    /// <summary>
    /// 怪物同构建模底座回归：必须以期望玩家面板 tiers[].finalExpected 派生，而非裸 baseAttributeAnchors。
    /// 坠星兽 Lv24 Boss = 期望面板（HP≈3370.9/AD≈245.6/AP≈398.0）× Boss 倍率（HP×20/属性×1.5）× 倾向权重。
    /// 错用基础锚点时物攻只有 ~35、术攻 ~25（回归信号）。
    /// </summary>
    [Fact]
    public void MonsterStatsDeriveFromExpectedPlayerPanel()
    {
        var catalog = GameDataCatalog.LoadEmbedded();

        var beast = catalog.CombatMonsters["enemy.falling-star-beast"];
        Assert.Equal(24, beast.Level);
        Assert.Equal(CombatMonsterRank.Boss, beast.Rank);

        var stats = beast.Stats;
        Assert.Equal(45170.46, stats.MaxHitpoints, 2);
        Assert.Equal(383.18, stats.AttackPower, 2);
        Assert.Equal(597.0, stats.SpellAttack, 1);
        Assert.Equal(349.85, stats.Defense, 2);
        Assert.Equal(244.97, stats.MagicResistance, 2);
    }

    /// <summary>
    /// 击杀奖励缺省派生回归（总纲 §7.10.3）：全部配表怪均未声明 experience，
    /// 一律按 expToNext[level]/1000×0.9^(level-1)×层次倍率公式派生——
    /// Lv1 小怪锚定 1/1000=10，Lv12 妖王锚定 10× 倍率。
    /// </summary>
    [Fact]
    public void MonsterKillExperienceDerivesFromRealmCurve()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var realm = catalog.RealmProgression!;
        Assert.Equal(12, catalog.CombatMonsters.Count);
        foreach (var (_, monster) in catalog.CombatMonsters)
            Assert.Equal(realm.MonsterKillExperience(monster.Level, monster.Rank), monster.Experience);
        Assert.Equal(10, catalog.CombatMonsters["enemy.demonized-rabbit"].Experience, 6);
        Assert.Equal(realm.ExpCapFor(12) / 1000d * Math.Pow(0.9, 11) * 10,
            catalog.CombatMonsters["enemy.chang-tiger"].Experience, 6);
    }
}
