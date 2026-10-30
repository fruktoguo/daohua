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
}
