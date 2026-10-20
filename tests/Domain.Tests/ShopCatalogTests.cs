using System.Text;

namespace Domain.Tests;

/// <summary>
/// 系统商店配表与限购规则测试：目录解析与校验、UTC+8 自然日切分、当日余量换算。
/// </summary>
public sealed class ShopCatalogTests
{
    /// <summary>内置配表必须上架两档觉醒石：100 功德每日限购 1 件、1,000 功德不限购。</summary>
    [Fact]
    public void EmbeddedCatalogExposesAwakeningStoneOffers()
    {
        var catalog = GameDataCatalog.LoadEmbedded();

        Assert.Equal(2, catalog.ShopOffers.Count);
        var daily = catalog.ShopOffers.All[0];
        Assert.Equal("shop.awakening-stone.daily", daily.Id);
        Assert.Equal("item.awakening-stone", daily.ItemId);
        Assert.Equal(100d, daily.UnitPrice);
        Assert.Equal(1, daily.DailyLimit);
        Assert.True(daily.IsDailyLimited);

        var open = catalog.ShopOffers.All[1];
        Assert.Equal("shop.awakening-stone.open", open.Id);
        Assert.Equal("item.awakening-stone", open.ItemId);
        Assert.Equal(1_000d, open.UnitPrice);
        Assert.Null(open.DailyLimit);
        Assert.False(open.IsDailyLimited);
    }

    /// <summary>目录按 sortOrder 升序稳定输出，并按商品 ID 查表。</summary>
    [Fact]
    public void CatalogOrdersBySortOrderAndLooksUpById()
    {
        var catalog = new ShopCatalog([
            new ShopOfferDefinition("shop.b", "item.awakening-stone", 20, null, 20),
            new ShopOfferDefinition("shop.a", "item.awakening-stone", 10, 1, 10),
        ]);

        Assert.Equal(["shop.a", "shop.b"], catalog.All.Select(offer => offer.Id).ToArray());
        Assert.True(catalog.TryGet("shop.a", out var offer));
        Assert.Equal(10d, offer.UnitPrice);
        Assert.False(catalog.TryGet("shop.missing", out _));
        Assert.False(catalog.TryGet(null, out _));
    }

    /// <summary>商品引用不存在的物品时，配表解析即失败（不允许带病上架）。</summary>
    [Fact]
    public void ParseRejectsOfferReferencingUnknownItem()
    {
        var documents = new[]
        {
            Json("[{\"id\":\"item.awakening-stone\",\"name\":\"觉醒石\",\"kind\":\"material\",\"category\":\"consumable\",\"sellPrice\":100}]"),
            Json("[{\"id\":\"shop.ghost\",\"itemId\":\"item.missing\",\"unitPrice\":100}]"),
        };

        Assert.Throws<DomainException>(() => GameDataCatalog.Parse(documents));
    }

    /// <summary>非法商品定义（价格非正整数 / 限购非正 / ID 重复 / 缺前缀）在目录构造时被拒绝。</summary>
    [Theory]
    [InlineData("shop.a", "item.awakening-stone", 0d, null)]
    [InlineData("shop.a", "item.awakening-stone", 99.5d, null)]
    [InlineData("shop.a", "", 100d, null)]
    [InlineData("shop.a", "item.awakening-stone", 100d, 0)]
    public void CatalogRejectsInvalidOffers(string id, string itemId, double price, int? dailyLimit)
    {
        Assert.Throws<DomainException>(() => new ShopCatalog([new ShopOfferDefinition(id, itemId, price, dailyLimit)]));
    }

    /// <summary>同一商品 ID 重复登记时拒绝，避免货架出现双份同价/异价条目。</summary>
    [Fact]
    public void CatalogRejectsDuplicateOfferIds()
    {
        Assert.Throws<DomainException>(() => new ShopCatalog([
            new ShopOfferDefinition("shop.a", "item.awakening-stone", 100),
            new ShopOfferDefinition("shop.a", "item.awakening-stone", 200),
        ]));
    }

    /// <summary>每日限购按 UTC+8 自然日切分：UTC 16:00 即次日零点，跨过该点序号 +1。</summary>
    [Fact]
    public void DayIndexUsesUtcPlusEightBoundaries()
    {
        var beforeMidnight = new DateTimeOffset(2026, 1, 1, 15, 59, 59, TimeSpan.Zero);
        var atMidnight = new DateTimeOffset(2026, 1, 1, 16, 0, 0, TimeSpan.Zero);

        Assert.Equal(ShopDailyWindow.DayIndexOf(beforeMidnight) + 1, ShopDailyWindow.DayIndexOf(atMidnight));
        // 同一 UTC+8 自然日内的任意时刻序号一致（含 UTC 当日 00:00 与 15:59:59）。
        Assert.Equal(
            ShopDailyWindow.DayIndexOf(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            ShopDailyWindow.DayIndexOf(beforeMidnight));
    }

    /// <summary>当日余量：限购商品按上限扣减已购并钳到 0；不限购商品恒为无上限。</summary>
    [Fact]
    public void RemainingTodayClampsToZero()
    {
        var limited = new ShopOfferDefinition("shop.a", "item.awakening-stone", 1_000, 1);
        var open = new ShopOfferDefinition("shop.b", "item.awakening-stone", 2_000);

        Assert.Equal(1, ShopPurchaseRules.RemainingToday(limited, 0));
        Assert.Equal(0, ShopPurchaseRules.RemainingToday(limited, 1));
        Assert.Equal(0, ShopPurchaseRules.RemainingToday(limited, 5));
        Assert.Equal(int.MaxValue, ShopPurchaseRules.RemainingToday(open, 9));
        Assert.True(ShopPurchaseRules.IsQuantityValid(1));
        Assert.False(ShopPurchaseRules.IsQuantityValid(0));
        Assert.False(ShopPurchaseRules.IsQuantityValid(ShopPurchaseRules.MaxQuantityPerPurchase + 1));
    }

    private static ReadOnlyMemory<byte> Json(string text) => Encoding.UTF8.GetBytes(text);
}
