using Domain.Common;
using Domain.GameData;
using Domain.Market;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

public sealed class MarketCircuitBreakerTests
{
    [Fact]
    public async Task PlaceOrder_RejectsMaterialsExceedingFiftyTimesBasePrice()
    {
        var options = ServerOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = "Host=unused;Database=unused",
        }).Build()) with { MarketPartitionId = 0, MarketPartitionCount = 1 };
        var itemId = new ItemId("item.material.peach-wood");
        var itemDef = new ItemDefinition(
            Id: itemId,
            Name: "桃木",
            Description: "普通桃木材料",
            IsTradable: true,
            SellPrice: 5,
            Consumable: null,
            Category: "material");

        var catalog = new GameDataCatalog(
            "1.0",
            "test-fingerprint",
            ActionCatalog.Default,
            items: new Dictionary<ItemId, ItemDefinition> { [itemId] = itemDef });

        var repository = new PostgresMarketRepository(
            NpgsqlDataSource.Create("Host=unused;Database=unused"),
            options,
            NullLogger<PostgresMarketRepository>.Instance,
            catalog);

        var runtime = new MarketRuntime(
            repository,
            options,
            NullLogger<MarketRuntime>.Instance,
            gameData: catalog);

        var instrument = new MarketInstrument(new ItemId("item.material.peach-wood"));

        // 桃木回收价 5，上限 5 * 50 = 250。尝试挂 10000 灵石作为运钞车：
        var request = new PlaceOrderRequest(
            OrderId.New(),
            PlayerId.New(),
            instrument,
            MarketSide.Sell,
            10000,
            10,
            DateTimeOffset.UtcNow,
            new IdempotencyKey("money-laundering"));

        // 断言：直接被材料限价熔断阻断！
        var exception = await Assert.ThrowsAsync<DomainException>(async () => await runtime.PlaceAsync(request));
        Assert.Equal(DomainErrorCode.InvalidArgument, exception.Code);
        Assert.Contains("超出基准价值上限", exception.Message);

        // 合规挂单（200 <= 250）：越过限价校验，到达分片就绪检查
        var validRequest = new PlaceOrderRequest(
            OrderId.New(),
            PlayerId.New(),
            instrument,
            MarketSide.Sell,
            200,
            10,
            DateTimeOffset.UtcNow,
            new IdempotencyKey("valid-placement"));

        var readyException = await Assert.ThrowsAsync<InvalidOperationException>(async () => await runtime.PlaceAsync(validRequest));
        Assert.Contains("市场分片尚未就绪", readyException.Message);
    }

    [Fact]
    public async Task PlaceOrder_RejectsPriceBelowVendorFloor()
    {
        var options = ServerOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = "Host=unused;Database=unused",
        }).Build()) with { MarketPartitionId = 0, MarketPartitionCount = 1 };

        var darkIron = new ItemDefinition(new ItemId("item.dark-iron"), "乌铁", "", true, 5, Category: "material");
        var beastBone = new ItemDefinition(new ItemId("item.beast-bone"), "兽骨", "", true, 6, Category: "material");
        var swordId = "equipment.test-sword";
        var swordAction = new ActionDefinition(
            new ActionId("forge.test-sword"),
            "锻造测试剑",
            ActionKind.Alchemy,
            10,
            new Dictionary<ItemId, double> { [darkIron.Id] = 10, [beastBone.Id] = 2 }, // 成本: 10*5 + 2*6 = 62 -> V0 = 124
            [new ActionYield(ActionYieldKind.Item, ActionYieldPeriod.PerRepeat, 1, Item: new ItemId(swordId))]);

        var catalog = new GameDataCatalog(
            "1.0",
            "test-fingerprint",
            new ActionCatalog([swordAction]),
            items: new Dictionary<ItemId, ItemDefinition> { [darkIron.Id] = darkIron, [beastBone.Id] = beastBone },
            rawEquipments: new Dictionary<string, System.Text.Json.JsonElement>
            {
                [swordId] = System.Text.Json.JsonDocument.Parse("""{"id":"equipment.test-sword","name":"测试剑","slot":"weapon"}""").RootElement
            });

        var repository = new PostgresMarketRepository(
            NpgsqlDataSource.Create("Host=unused;Database=unused"),
            options,
            NullLogger<PostgresMarketRepository>.Instance,
            catalog);

        var runtime = new MarketRuntime(
            repository,
            options,
            NullLogger<MarketRuntime>.Instance,
            gameData: catalog);

        // 1. 普通材料（乌铁回收价 5）：尝试挂 2 灵石（低于回收价 5）
        var cheapMaterial = new PlaceOrderRequest(
            OrderId.New(),
            PlayerId.New(),
            new MarketInstrument(darkIron.Id),
            MarketSide.Sell,
            2,
            1,
            DateTimeOffset.UtcNow,
            new IdempotencyKey("cheap-material"));
        var matEx = await Assert.ThrowsAsync<DomainException>(async () => await runtime.PlaceAsync(cheapMaterial));
        Assert.Equal(DomainErrorCode.InvalidArgument, matEx.Code);
        Assert.Contains("挂单单价不得低于系统回收底价（最低 5 灵石）", matEx.Message);

        // 2. 装备白板（V0 = 124）：尝试以 2 灵石挂单
        var cheapSword = new PlaceOrderRequest(
            OrderId.New(),
            PlayerId.New(),
            new MarketInstrument(new ItemId(swordId)),
            MarketSide.Sell,
            2,
            1,
            DateTimeOffset.UtcNow,
            new IdempotencyKey("cheap-sword"));
        var swordEx = await Assert.ThrowsAsync<DomainException>(async () => await runtime.PlaceAsync(cheapSword));
        Assert.Equal(DomainErrorCode.InvalidArgument, swordEx.Code);
        Assert.Contains("挂单单价不得低于系统回收底价（最低 124 灵石）", swordEx.Message);

        // 3. 强化装备（+1 变体，回收价 round(124 * 1.5 + 10) = 196）：尝试挂 150 灵石
        var cheapPlus1 = new PlaceOrderRequest(
            OrderId.New(),
            PlayerId.New(),
            new MarketInstrument(new ItemId(swordId), "+1"),
            MarketSide.Sell,
            150,
            1,
            DateTimeOffset.UtcNow,
            new IdempotencyKey("cheap-sword-plus-1"));
        var plus1Ex = await Assert.ThrowsAsync<DomainException>(async () => await runtime.PlaceAsync(cheapPlus1));
        Assert.Equal(DomainErrorCode.InvalidArgument, plus1Ex.Code);
        Assert.Contains("挂单单价不得低于系统回收底价（最低 196 灵石）", plus1Ex.Message);

        // 4. 合规挂单（200 >= 196）：越过底价校验，到达分片就绪检查
        var validSword = new PlaceOrderRequest(
            OrderId.New(),
            PlayerId.New(),
            new MarketInstrument(new ItemId(swordId), "+1"),
            MarketSide.Sell,
            200,
            1,
            DateTimeOffset.UtcNow,
            new IdempotencyKey("valid-sword-plus-1"));
        var readyEx = await Assert.ThrowsAsync<InvalidOperationException>(async () => await runtime.PlaceAsync(validSword));
        Assert.Contains("市场分片尚未就绪", readyEx.Message);
    }
}
