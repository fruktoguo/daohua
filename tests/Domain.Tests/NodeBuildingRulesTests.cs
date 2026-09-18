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
    public void WorldStateRoundTripsNodeBuildings()
    {
        var aggregate = PlayerAggregate.Create(new PlayerId(Guid.NewGuid()), new AccountId(Guid.NewGuid()), "测试", DateTimeOffset.UtcNow, "fp");
        aggregate.World.NodeBuildings.Add(new NodeBuildingState("building.test", 2, 3, 100,
            new Dictionary<string, double> { ["item.peach-wood"] = 30 }));
        aggregate.World.NodeBuildingEffects = new BuildingEffectSet(ExplorationOutputRatio: 0.1);
        aggregate.World.NodeBuildingNodeId = aggregate.World.NodeId;
        var restored = PlayerAggregate.Restore(aggregate.ExportState());
        var entry = Assert.Single(restored.World.NodeBuildings);
        Assert.Equal("building.test", entry.BuildingId);
        Assert.Equal(2, entry.Level);
        Assert.Equal(3, entry.TargetLevel);
        Assert.Equal(30, entry.ContributedMaterials["item.peach-wood"]);
        Assert.Equal(0.1, restored.World.NodeBuildingEffects.ExplorationOutputRatio, 6);
        Assert.Equal(restored.World.NodeId, restored.World.NodeBuildingNodeId);
        Assert.Equal(0.1, restored.World.EffectiveNodeBuildingEffects.ExplorationOutputRatio, 6);
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
