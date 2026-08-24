// ==============================================================================
// 文件名称: DefinitionTests.cs
// 主要职责: 验证强类型 ID（Domain.Common）的构造与值相等性契约。
// 核心逻辑 / 定义内容:
//   1. 验证 PlayerId、ItemId、AbilityId、ActionId、EncounterId 强类型值对象的值语义与有效性检查。
// ==============================================================================

namespace Domain.Tests;

using Domain.Common;

/// <summary>
/// 强类型标识符与只读配置定义载体测试用例。
/// </summary>
public sealed class DefinitionTests
{
    /// <summary>
    /// 验证强类型标识符（PlayerId、ItemId、AbilityId、ActionId、EncounterId）的值相等性、非空性与字符串表示。
    /// </summary>
    [Fact]
    public void Strongly_typed_identifiers_support_value_equality_and_validation()
    {
        var rawGuid = Guid.NewGuid();
        var p1 = new PlayerId(rawGuid);
        var p2 = new PlayerId(rawGuid);
        var p3 = PlayerId.New();

        Assert.Equal(p1, p2);
        Assert.NotEqual(p1, p3);
        Assert.False(p1.IsEmpty);
        Assert.True(PlayerId.Empty.IsEmpty);
        Assert.Equal(rawGuid.ToString(), p1.ToString());

        var itemA1 = new ItemId("item.herb.spirit-herb");
        var itemA2 = new ItemId("item.herb.spirit-herb");
        var itemB = new ItemId("item.pill.qi-gathering");

        Assert.Equal(itemA1, itemA2);
        Assert.NotEqual(itemA1, itemB);
        Assert.True(itemA1.IsValid);
        Assert.False(ItemId.Empty.IsValid);
        Assert.Equal("item.herb.spirit-herb", itemA1.ToString());

        var ability1 = new AbilityId("ability.sword.slash");
        var ability2 = new AbilityId("ability.sword.slash");
        Assert.Equal(ability1, ability2);
        Assert.True(ability1.IsValid);

        var action1 = new ActionId("action.cultivation.draw-qi");
        var action2 = new ActionId("action.cultivation.draw-qi");
        Assert.Equal(action1, action2);
        Assert.True(action1.IsValid);

        var encounter1 = new EncounterId("encounter.qi-beast");
        var encounter2 = new EncounterId("encounter.qi-beast");
        Assert.Equal(encounter1, encounter2);
        Assert.True(encounter1.IsValid);
    }

}
