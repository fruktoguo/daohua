using System.Text.Json;

namespace Domain.Tests;

/// <summary>
/// 节点公共建筑规则测试：需求公式、双进度惰性推算、完工跳变、效果汇总与可建列表解析。
/// </summary>
public sealed class NodeBuildingRulesTests
{
    private static BuildingDefinition Definition(IReadOnlyList<BuildingMaterial>? materials = null, BuildingEffectSet? modifiers = null)
        => new(
            "building.test",
            "测试建筑",
            "描述",
            "效果",
            1,
            null,
            NodeBuildingRules.GroupProduction,
            materials ?? [new BuildingMaterial("item.peach-wood", 100), new BuildingMaterial("item.beast-hide", 50)])
        { Modifiers = modifiers ?? new BuildingEffectSet(ExplorationOutputRatio: 0.05) };

    [Fact]
    public void RequiredBuildSecondsScalesLinearly()
    {
        Assert.Equal(600, NodeBuildingRules.RequiredBuildSeconds(1));
        Assert.Equal(720, NodeBuildingRules.RequiredBuildSeconds(2));
        Assert.Equal(6600, NodeBuildingRules.RequiredBuildSeconds(51), 6);
    }

    [Fact]
    public void RequiredMaterialsScalesExponentially()
    {
        var definition = Definition();
        var level1 = NodeBuildingRules.RequiredMaterials(definition, 1);
        Assert.Equal(100, level1["item.peach-wood"], 6);
        var level2 = NodeBuildingRules.RequiredMaterials(definition, 2);
        Assert.Equal(110, level2["item.peach-wood"], 6);
        Assert.Equal(55, level2["item.beast-hide"], 6);
    }

    [Fact]
    public void MaterialFractionCapsEachItemAtRequired()
    {
        var definition = Definition();
        var state = new NodeBuildingState("building.test", 0, 1, 0,
            new Dictionary<string, double> { ["item.peach-wood"] = 1000 });
        // 桃木已超量仍按需求封顶：100/100 + 0/50 → 100/150。
        Assert.Equal(100.0 / 150.0, NodeBuildingRules.MaterialFraction(state, definition), 6);
    }

    [Fact]
    public void BuildProgressIsMinOfMaterialAndTime()
    {
        var definition = Definition();
        var halfMaterials = new NodeBuildingState("building.test", 0, 1, 0,
            new Dictionary<string, double> { ["item.peach-wood"] = 50, ["item.beast-hide"] = 25 });
        Assert.Equal(0.5, NodeBuildingRules.MaterialFraction(halfMaterials, definition), 6);
        // 时间已满、材料一半 → 总进度 50%。
        Assert.Equal(0.5, NodeBuildingRules.BuildProgress(halfMaterials, definition, 600), 6);
        // 材料满、时间一半 → 总进度 50%。
        var fullMaterials = new NodeBuildingState("building.test", 0, 1, 0,
            new Dictionary<string, double> { ["item.peach-wood"] = 100, ["item.beast-hide"] = 50 });
        Assert.Equal(0.5, NodeBuildingRules.BuildProgress(fullMaterials, definition, 300), 6);
        Assert.True(NodeBuildingRules.IsComplete(fullMaterials, definition, 600));
        Assert.False(NodeBuildingRules.IsComplete(fullMaterials, definition, 599));
    }

    [Fact]
    public void FinalizeIfCompletePromotesLevelAndClearsMaterials()
    {
        var definition = Definition();
        var state = new NodeBuildingState("building.test", 0, 1, 0,
            new Dictionary<string, double> { ["item.peach-wood"] = 100, ["item.beast-hide"] = 50 });
        var finalized = NodeBuildingRules.FinalizeIfComplete(state, definition, 600);
        Assert.Equal(1, finalized.Level);
        Assert.Equal(0, finalized.TargetLevel);
        Assert.Empty(finalized.ContributedMaterials);
        // 未完工不跳变。
        Assert.Same(state, NodeBuildingRules.FinalizeIfComplete(state, definition, 599));
    }

    [Fact]
    public void AggregateEffectsScalesByLevelAndSkipsUnfinished()
    {
        var definition = Definition();
        var definitions = new Dictionary<string, BuildingDefinition> { [definition.Id] = definition };
        var built = new NodeBuildingState("building.test", 3, 0, 0, new Dictionary<string, double>());
        var unfinished = new NodeBuildingState("building.test", 0, 1, 0, new Dictionary<string, double>());
        var effects = NodeBuildingRules.AggregateEffects([built, unfinished], definitions, 0);
        Assert.Equal(0.15, effects.ExplorationOutputRatio, 6);
        Assert.Equal(BuildingEffectSet.Empty.AlchemyOutputRatio, effects.AlchemyOutputRatio);
    }

    [Fact]
    public void CrowdCapacityGrowsTenPercentOfBasePerLevel()
    {
        Assert.Equal(100, NodeBuildingRules.CrowdCapacity(1));
        // 未建成按一级口径给展示基数。
        Assert.Equal(100, NodeBuildingRules.CrowdCapacity(0));
        Assert.Equal(110, NodeBuildingRules.CrowdCapacity(2));
        Assert.Equal(200, NodeBuildingRules.CrowdCapacity(11));
        Assert.Equal(600, NodeBuildingRules.CrowdCapacity(51));
    }

    [Fact]
    public void CrowdEfficiencyDecaysLinearlyAndFloorsAtHalf()
    {
        // 承载内满效（含边界恰好等于上限）。
        Assert.Equal(1, NodeBuildingRules.CrowdEfficiency(1, 0));
        Assert.Equal(1, NodeBuildingRules.CrowdEfficiency(1, 100));
        // 超载线性衰减：每超 1 人 -0.1%，即每超 100 人 -10%。
        Assert.Equal(0.9, NodeBuildingRules.CrowdEfficiency(1, 200), 6);
        Assert.Equal(0.8, NodeBuildingRules.CrowdEfficiency(1, 300), 6);
        // 减幅封顶 50%：cap+500 处触底，之后恒 50%，永不归零。
        Assert.Equal(0.5, NodeBuildingRules.CrowdEfficiency(1, 600), 6);
        Assert.Equal(0.5, NodeBuildingRules.CrowdEfficiency(1, 1000), 6);
        Assert.Equal(0.5, NodeBuildingRules.CrowdEfficiency(1, 10000), 6);
        // 更高等级承载同步上移：Lv2 cap=110，110 人仍满效。
        Assert.Equal(1, NodeBuildingRules.CrowdEfficiency(2, 110));
        Assert.Equal(0.99, NodeBuildingRules.CrowdEfficiency(2, 120), 6);
    }

    [Fact]
    public void ServedKindsMapsNonZeroModifiersToActionKinds()
    {
        Assert.Empty(NodeBuildingRules.ServedKinds(BuildingEffectSet.Empty));
        Assert.Equal([ActionKind.Exploration],
            NodeBuildingRules.ServedKinds(new BuildingEffectSet(ExplorationOutputRatio: 0.05)));
        Assert.Equal([ActionKind.Exploration, ActionKind.Alchemy, ActionKind.Cultivation],
            NodeBuildingRules.ServedKinds(new BuildingEffectSet(
                ExplorationSpeedRatio: 0.1, AlchemyOutputRatio: 0.1, CultivationOutputRatio: 0.2)));
        Assert.Equal([ActionKind.Combat],
            NodeBuildingRules.ServedKinds(new BuildingEffectSet(CombatHitpointRegen: 5)));
        // 封魔阵类怪物削弱同样映射为战斗服务类别（参与拥挤计数与衰减）。
        Assert.Equal([ActionKind.Combat],
            NodeBuildingRules.ServedKinds(new BuildingEffectSet(MonsterWeakenRatio: 0.01)));
    }

    [Fact]
    public void WeakenMonsterStatsAppliesHyperbolicDampingWithoutHardCap()
    {
        var stats = CombatantStats.Create(1000, 100, 50, 2) with
        {
            SpellAttack = 80, MagicResistance = 30, MaxManapoints = 200
        };
        // 零削弱原位透传，不分配新对象。
        Assert.Same(stats, NodeBuildingRules.WeakenMonsterStats(stats, 0));

        // 削弱 50%（0.5）：1000 / (1 + 0.5) = 666.666667（相当于 100 变成 66.67）
        var halfWeakened = NodeBuildingRules.WeakenMonsterStats(stats, 0.5);
        Assert.Equal(666.666667, halfWeakened.MaxHitpoints, 5);
        Assert.Equal(66.666667, halfWeakened.AttackPower, 5);
        Assert.Equal(53.333333, halfWeakened.SpellAttack, 5);
        Assert.Equal(33.333333, halfWeakened.Defense, 5);
        Assert.Equal(20.0, halfWeakened.MagicResistance, 5);

        // 削弱 100%（1.0）：1000 / (1 + 1.0) = 500（相当于 100 变成 50）
        var fullWeakened = NodeBuildingRules.WeakenMonsterStats(stats, 1.0);
        Assert.Equal(500, fullWeakened.MaxHitpoints, 6);
        Assert.Equal(50, fullWeakened.AttackPower, 6);

        // 削弱 900%（9.0）：1000 / (1 + 9.0) = 100（相当于 100 变成 10）
        var extremeWeakened = NodeBuildingRules.WeakenMonsterStats(stats, 9.0);
        Assert.Equal(100, extremeWeakened.MaxHitpoints, 6);
        Assert.Equal(10, extremeWeakened.AttackPower, 6);

        // 灵力上限与攻击间隔等非削弱字段不受影响。
        Assert.Equal(200, halfWeakened.MaxManapoints, 6);
        Assert.Equal(2, halfWeakened.AttackIntervalSeconds, 6);
    }

    [Fact]
    public void BuildingDefinitionEffectAtLevelSupportsBaseAndGrowth()
    {
        var def = new BuildingDefinition(
            "building.test", "测试建筑", "描述", "效果", 1)
        {
            BaseModifiers = new BuildingEffectSet(GatheringSpeedRatio: 0.12),
            GrowthModifiers = new BuildingEffectSet(GatheringSpeedRatio: 0.02)
        };

        // Lv.0 未建成为 0
        Assert.Equal(0, def.EffectAtLevel(0).GatheringSpeedRatio);
        // Lv.1 为初始基准 12%
        Assert.Equal(0.12, def.EffectAtLevel(1).GatheringSpeedRatio, 6);
        // Lv.2 为 12% + 2% = 14%
        Assert.Equal(0.14, def.EffectAtLevel(2).GatheringSpeedRatio, 6);
        // Lv.10 为 12% + 2% * 9 = 30%
        Assert.Equal(0.30, def.EffectAtLevel(10).GatheringSpeedRatio, 6);
    }

    [Fact]
    public void AggregateEffectsAppliesCrowdingIndependentlyPerBuilding()
    {
        var exploration = new BuildingDefinition("building.exp", "历练台", "", "", 1, null, NodeBuildingRules.GroupProduction, [])
        { Modifiers = new BuildingEffectSet(ExplorationOutputRatio: 0.1) };
        var alchemy = new BuildingDefinition("building.alc", "丹房", "", "", 1, null, NodeBuildingRules.GroupProduction, [])
        { Modifiers = new BuildingEffectSet(AlchemyOutputRatio: 0.1) };
        var definitions = new Dictionary<string, BuildingDefinition>
        {
            [exploration.Id] = exploration,
            [alchemy.Id] = alchemy,
        };
        var buildings = new[]
        {
            new NodeBuildingState("building.exp", 1, 0, 0, new Dictionary<string, double>()),
            new NodeBuildingState("building.alc", 1, 0, 0, new Dictionary<string, double>()),
        };
        // 探索 300 人（超 cap100 的 200 人 → 效率 0.8），炼丹 50 人（满效）：按服务类别独立计数。
        var counts = new Dictionary<ActionKind, double>
        {
            [ActionKind.Exploration] = 300,
            [ActionKind.Alchemy] = 50,
        };
        var effects = NodeBuildingRules.AggregateEffects(buildings, definitions, 0, counts);
        Assert.Equal(0.08, effects.ExplorationOutputRatio, 6);
        Assert.Equal(0.1, effects.AlchemyOutputRatio, 6);
        // 无关类别（修炼 5000 人）不影响这两座建筑的结算。
        counts[ActionKind.Cultivation] = 5000;
        var unaffected = NodeBuildingRules.AggregateEffects(buildings, definitions, 0, counts);
        Assert.Equal(effects, unaffected);
        // counts 为 null 退化为旧满效口径。
        var legacy = NodeBuildingRules.AggregateEffects(buildings, definitions, 0);
        Assert.Equal(0.1, legacy.ExplorationOutputRatio, 6);
    }

    [Fact]
    public void ResolveBuildableIdsPrefersExplicitListThenTemplate()
    {
        var definitions = new Dictionary<string, BuildingDefinition>
        {
            ["building.a"] = Definition(),
            ["building.b"] = new BuildingDefinition("building.b", "B", "", "", 1, null, NodeBuildingRules.GroupCombat),
        };
        var explicitNode = JsonDocument.Parse("""{"buildableList":["building.b"],"buildableTemplate":"production"}""").RootElement;
        var resolved = NodeBuildingRules.ResolveBuildableIds(explicitNode, definitions);
        Assert.NotNull(resolved);
        Assert.Equal(["building.b"], resolved.Order(StringComparer.Ordinal).ToArray());

        var templateNode = JsonDocument.Parse("""{"buildableTemplate":"combat"}""").RootElement;
        Assert.Equal(["building.b"], NodeBuildingRules.ResolveBuildableIds(templateNode, definitions)!.ToArray());

        var allNode = JsonDocument.Parse("""{"buildableTemplate":"all"}""").RootElement;
        Assert.Equal(2, NodeBuildingRules.ResolveBuildableIds(allNode, definitions)!.Count);

        var emptyNode = JsonDocument.Parse("""{"name":"x"}""").RootElement;
        Assert.Null(NodeBuildingRules.ResolveBuildableIds(emptyNode, definitions));
    }

    [Fact]
    public void ResolvePrebuiltBuildingsParsesSeedsAndSkipsInvalid()
    {
        var node = JsonDocument.Parse("""
            {"id":"node.x","prebuiltBuildings":[
                {"buildingId":"building.demon-seal-array","level":30},
                {"buildingId":"  ","level":5},
                {"buildingId":"building.b","level":0},
                {"buildingId":"building.c"},
                "not-an-object"
            ]}
            """).RootElement;
        var seed = Assert.Single(NodeBuildingRules.ResolvePrebuiltBuildings(node));
        Assert.Equal("building.demon-seal-array", seed.BuildingId);
        Assert.Equal(30, seed.Level);
        // 未配置 prebuiltBuildings 返回空列表。
        Assert.Empty(NodeBuildingRules.ResolvePrebuiltBuildings(JsonDocument.Parse("""{"id":"node.y"}""").RootElement));
    }

    [Fact]
    public void WorldStateRoundTripsNodeBuildings()
    {
        var aggregate = PlayerAggregate.Create(new PlayerId(Guid.NewGuid()), new AccountId(Guid.NewGuid()), "测试", DateTimeOffset.UtcNow, "fp");
        aggregate.World.NodeBuildings.Add(new NodeBuildingState("building.test", 2, 3, 100,
            new Dictionary<string, double> { ["item.peach-wood"] = 30 }));
        aggregate.World.NodeBuildingEffects = new BuildingEffectSet(ExplorationOutputRatio: 0.1);
        aggregate.World.NodeBuildingNodeId = aggregate.World.NodeId;
        aggregate.World.NodeBuildingUsages.Add(new NodeBuildingUsage("building.test", 150, 110, 0.9));
        var restored = PlayerAggregate.Restore(aggregate.ExportState());
        var entry = Assert.Single(restored.World.NodeBuildings);
        Assert.Equal("building.test", entry.BuildingId);
        Assert.Equal(2, entry.Level);
        Assert.Equal(3, entry.TargetLevel);
        Assert.Equal(30, entry.ContributedMaterials["item.peach-wood"]);
        Assert.Equal(0.1, restored.World.NodeBuildingEffects.ExplorationOutputRatio, 6);
        Assert.Equal(restored.World.NodeId, restored.World.NodeBuildingNodeId);
        Assert.Equal(0.1, restored.World.EffectiveNodeBuildingEffects.ExplorationOutputRatio, 6);
        var usage = Assert.Single(restored.World.NodeBuildingUsages);
        Assert.Equal("building.test", usage.BuildingId);
        Assert.Equal(150, usage.ActiveUsers);
        Assert.Equal(110, usage.Capacity);
        Assert.Equal(0.9, usage.Efficiency);
    }

    [Fact]
    public void EffectiveNodeBuildingEffectsIgnoresStaleProjection()
    {
        var aggregate = PlayerAggregate.Create(new PlayerId(Guid.NewGuid()), new AccountId(Guid.NewGuid()), "测试", DateTimeOffset.UtcNow, "fp");
        aggregate.World.NodeBuildingEffects = new BuildingEffectSet(ExplorationOutputRatio: 0.5);
        // 投影未标记节点或属于其他节点时，当前位置不生效。
        aggregate.World.NodeBuildingNodeId = null;
        Assert.Equal(0, aggregate.World.EffectiveNodeBuildingEffects.ExplorationOutputRatio);
        aggregate.World.NodeBuildingNodeId = "node.other";
        Assert.Equal(0, aggregate.World.EffectiveNodeBuildingEffects.ExplorationOutputRatio);
        aggregate.World.NodeBuildingNodeId = aggregate.World.NodeId;
        Assert.Equal(0.5, aggregate.World.EffectiveNodeBuildingEffects.ExplorationOutputRatio, 6);
    }
}
