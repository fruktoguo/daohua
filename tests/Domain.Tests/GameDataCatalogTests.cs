using System.Text.Json;

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
    /// 行动 → 物品栏域权威映射：修炼/历练归历练栏，制造大类按 technique 归对应技艺栏，
    /// 未声明制造技艺的制造行动与赶路行动无栏域。
    /// </summary>
    [Fact]
    public void AutoUseBarForResolvesActionBar()
    {
        static ActionDefinition Action(string id, ActionKind kind, string? technique = null) =>
            new(new ActionId(id), id, kind, 1, new Dictionary<ItemId, double>(), [], Technique: technique);

        Assert.Equal(AutoUseBar.Exploration, AutoUseBar.For(Action("t.cultivate", ActionKind.Cultivation)));
        Assert.Equal(AutoUseBar.Exploration, AutoUseBar.For(Action("t.gather", ActionKind.Exploration)));
        Assert.Equal(AutoUseBar.Alchemy, AutoUseBar.For(Action("t.refine", ActionKind.Alchemy, "alchemy")));
        Assert.Equal(AutoUseBar.Forging, AutoUseBar.For(Action("t.forge", ActionKind.Alchemy, "forging")));
        Assert.Equal(AutoUseBar.Talisman, AutoUseBar.For(Action("t.draw", ActionKind.Alchemy, "talisman")));
        Assert.Equal(AutoUseBar.Formation, AutoUseBar.For(Action("t.deploy", ActionKind.Alchemy, "formation")));
        Assert.Null(AutoUseBar.For(Action("t.craft", ActionKind.Alchemy)));
        Assert.Null(AutoUseBar.For(Action("t.mine", ActionKind.Alchemy, "gathering")));
        Assert.Equal(AutoUseBar.Combat, AutoUseBar.For(Action("t.hunt", ActionKind.Combat)));
        Assert.Equal(AutoUseBar.Enhancement, AutoUseBar.For(Action("t.enhance", ActionKind.Enhancement)));
        Assert.Null(AutoUseBar.For(Action("t.travel", ActionKind.Travel)));
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
        Assert.Contains("exploration", consumable.UsableInBars);
    }

    /// <summary>
    /// 怪物同构建模底座回归：必须以每级期望玩家面板 levelExpected[].finalExpected 派生，而非裸 baseAttributeAnchors。
    /// 坠星兽 Lv24 Boss = 期望面板（HP≈3081.0/AD≈224.5/AP≈349.4）× Boss 倍率（HP×20/属性×1.5）× 倾向权重。
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
        Assert.Equal(41285.35, stats.MaxHitpoints, 2);
        Assert.Equal(350.33, stats.AttackPower, 2);
        Assert.Equal(544.63, stats.SpellAttack, 2);
        Assert.Equal(320.86, stats.Defense, 2);
        Assert.Equal(222.01, stats.MagicResistance, 2);
    }

    /// <summary>
    /// 怪物配表反应效果键回归（总纲 §7.12.1）：threatOnTakenPoints 与汲取/反弹/治疗加成
    /// 同构，直接声明在怪物根对象并解析进 Stats.Effects，供受击涨威胁钩子消费。
    /// </summary>
    [Fact]
    public void MonsterThreatOnTakenPointsParsesIntoEffects()
    {
        using var monster = JsonDocument.Parse("""
            {"id":"enemy.threat-test","name":"受击涨威胁测试兽","level":1,"threatOnTakenPoints":75}
            """);
        var attributes = new BaseAttributeCatalog([new(1, 1000, 100, 10, 5, 10, 0, 100, 0, 0, 0, 0, 0, 7, 3)]);
        var catalog = new GameDataCatalog("test", "test", new ActionCatalog([]),
            monsters: new Dictionary<string, JsonElement> { ["enemy.threat-test"] = monster.RootElement },
            baseAttributes: attributes, expectedAttributes: attributes);

        var parsed = catalog.CombatMonsters["enemy.threat-test"];
        Assert.Equal(75, parsed.Stats.Effects.ThreatOnTakenPoints);
        Assert.Equal(0, parsed.Stats.Effects.HpDrainPoints);
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
        Assert.Equal(13, catalog.CombatMonsters.Count);
        foreach (var (_, monster) in catalog.CombatMonsters)
            Assert.Equal(realm.MonsterKillExperience(monster.Level, monster.Rank), monster.Experience);
        Assert.Equal(10, catalog.CombatMonsters["enemy.demonized-rabbit"].Experience, 6);
        Assert.Equal(realm.ExpCapFor(12) / 1000d * Math.Pow(0.9, 11) * 10,
            catalog.CombatMonsters["enemy.chang-tiger"].Experience, 6);
    }

    /// <summary>
    /// 验证玄元门遗址隐藏节点、灵石矿脉行动与玄元残魄 BOSS 正确解析且属性对齐。
    /// </summary>
    [Fact]
    public void XuanyuanRemnantBossAndRuinsNodeParsedCorrectly()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        Assert.True(catalog.WorldGraph.TryGetNode("node.xuanyuan-ruins", out var ruinsNode));
        Assert.True(ruinsNode.Hidden);
        Assert.Equal(30, ruinsNode.RequiredConsciousness);

        var mineAction = catalog.Actions.Get(new ActionId("explore.mine-spirit-stone"));
        Assert.NotNull(mineAction);
        var spiritStoneYield = Assert.Single(mineAction.Yields, y => y.Kind == ActionYieldKind.Item);
        Assert.Equal("currency.spirit-stone", spiritStoneYield.Item?.Value);
        Assert.Equal(10, spiritStoneYield.AmountMin);
        Assert.Equal(40, spiritStoneYield.AmountMax);

        Assert.True(catalog.CombatMonsters.TryGetValue("enemy.xuanyuan-remnant", out var boss));
        Assert.Equal(18, boss.Level);
        Assert.Equal(CombatMonsterRank.Boss, boss.Rank);
        Assert.Equal(4, boss.Skills.Count);
        Assert.Equal("ability.blazing-blast", boss.Skills[0].Ability.GetProperty("id").GetString());
        Assert.Equal("ability.lightning-attraction", boss.Skills[1].Ability.GetProperty("id").GetString());
        Assert.Equal("ability.qingping-melody", boss.Skills[2].Ability.GetProperty("id").GetString());
        Assert.Equal("ability.palm-lightning", boss.Skills[3].Ability.GetProperty("id").GetString());

        Assert.Contains(boss.DropTable, d => d.ItemId.Value == "currency.spirit-stone");
        Assert.Contains(boss.DropTable, d => d.ItemId.Value == "item.tome.xuanyuan-yiqi-gong");
        Assert.Contains(boss.DropTable, d => d.ItemId.Value == "item.fragment.xuanyuan-yiqi-gong");
        Assert.Contains(boss.DropTable, d => d.ItemId.Value == "item.tome.blazing-blast");
        Assert.Contains(boss.DropTable, d => d.ItemId.Value == "item.tome.palm-lightning");
    }
}
