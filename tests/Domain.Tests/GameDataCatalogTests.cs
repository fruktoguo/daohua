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
    /// 验证 <c>JsonResourcesProduceStableFingerprintAndDefinitions</c>（Json Resources Produce Stable Fingerprint And Definitions）所描述的领域行为。
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
    /// 验证 <c>EmbeddedCatalogParsesSustainedConsumableEffects</c>（Embedded Catalog Parses Sustained Consumable Effects）所描述的领域行为。
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
}
