using System.Text.Json;

namespace Domain.Tests;

public sealed class ZongmenRulesTests
{
    /// <summary>大殿首升基准不能影响公共建筑，且容量只按完工等级增加。</summary>
    [Fact]
    public void HallUsesFirstUpgradeMaterialBaselineAndOrdinaryConstructionCompletion()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        var hall = catalog.Buildings[ZongmenRules.HallBuildingId];
        Assert.Equal(10000, NodeBuildingRules.RequiredMaterials(hall, 2)["item.refined-iron"]);
        Assert.Equal(11000, NodeBuildingRules.RequiredMaterials(hall, 3)["item.refined-iron"]);
        var publicBuilding = catalog.Buildings["building.meditation-circle"];
        Assert.Equal(Math.Floor(publicBuilding.Materials![0].Quantity * 1.1),
            NodeBuildingRules.RequiredMaterials(publicBuilding, 2)[publicBuilding.Materials[0].ItemId]);
        var state = new NodeBuildingState(hall.Id, 1, 2, 0, new Dictionary<string, double>());
        Assert.Equal(20, ZongmenRules.MemberCapacity(NodeBuildingRules.EffectiveLevel(state, hall, 1000)));
        state = state with { ContributedMaterials = NodeBuildingRules.RequiredMaterials(hall, 2) };
        Assert.Equal(25, ZongmenRules.MemberCapacity(NodeBuildingRules.EffectiveLevel(state, hall, 1000)));
        Assert.DoesNotContain(hall.Id, NodeBuildingRules.ResolveBuildableIds(
            JsonSerializer.SerializeToElement(new { buildableTemplate = "all" }), catalog.Buildings)!);
    }

    /// <summary>唯一性规范化与前缀输入必须同时覆盖宽窄字符及非法长度。</summary>
    [Theory]
    [InlineData(" 青云宗 ", "青云宗")]
    [InlineData("ＡＢ宗", "AB宗")]
    public void NamesNormalizeBeforeUniqueness(string input, string expected)
    {
        Assert.Equal(expected, ZongmenRules.NormalizeName(input));
        Assert.Throws<DomainException>(() => ZongmenRules.NormalizePrefix("青云"));
        Assert.Throws<DomainException>(() => ZongmenRules.NormalizePrefix("A"));
        Assert.Equal("青", ZongmenRules.NormalizePrefix(" 青 "));
    }

    /// <summary>共享宗门读协议保持属性名及成员职位，防止前端解码单边偏移。</summary>
    [Fact]
    public void StateContractRoundTripsWithCamelCaseAndRoles()
    {
        var state = new ZongmenStateDto(new("id", "青云宗", "青", "leader", 0, "node.anchor", "node.private", 1, 20,
            [new("leader", "道友", 0, true)], []), [new("z1", "玄剑宗", "玄", 5, 20, "node.anchor")], [], [new("z1", "玄剑宗", "me", "道友", "application")]);
        var json = JsonSerializer.Serialize(state, ProtocolJson.Options);
        Assert.Contains("\"anchorNodeId\":\"node.anchor\"", json);
        Assert.Contains("\"applications\"", json);
        var restored = JsonSerializer.Deserialize<ZongmenStateDto>(json, ProtocolJson.Options)!;
        Assert.Equal(0, Assert.Single(restored.Zongmen!.Members).Role);
        Assert.Equal(state.Zongmen!.NodeId, restored.Zongmen.NodeId);
        Assert.Equal("node.anchor", Assert.Single(restored.Directory).AnchorNodeId);
        Assert.Equal("application", Assert.Single(restored.Applications).Kind);
    }
}
