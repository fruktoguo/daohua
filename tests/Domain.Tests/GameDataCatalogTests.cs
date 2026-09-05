namespace Domain.Tests;

public sealed class GameDataCatalogTests
{
    [Fact]
    public void BuiltInCatalogIsValid()
    {
        var catalog = GameDataCatalog.BuiltIn;
        Assert.NotEmpty(catalog.Actions.All);
        Assert.Contains(catalog.Actions.All, action => action.Id.Value == "cultivation.draw-qi");
    }

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

    [Fact]
    public void EmbeddedCatalogCanBeLoadedByHost()
    {
        var catalog = GameDataCatalog.LoadEmbedded();
        Assert.NotEmpty(catalog.Actions.All);
        Assert.NotEmpty(catalog.Items);
        Assert.NotEmpty(catalog.Maps);
        Assert.NotEmpty(catalog.Encounters);
        Assert.False(string.IsNullOrWhiteSpace(catalog.Fingerprint));
    }
}
