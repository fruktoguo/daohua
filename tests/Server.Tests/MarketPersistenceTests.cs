using Domain.Common;
using Domain.Market;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

public sealed class MarketPersistenceTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PartialTradeTaxAndExpirationAreDurableAtomicAndIdempotent()
    {
        var connectionString = Environment.GetEnvironmentVariable("DAOHUA_MARKET_TEST_CONNECTION")
            ?? throw new InvalidOperationException("市场集成测试需要专用 PostgreSQL 库：DAOHUA_MARKET_TEST_CONNECTION。");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await Sql(source, PostgresSchema.Sql);
        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = connectionString,
        }).Build()) with { MarketPartitionId = 0, MarketPartitionCount = 1 };
        var seller = PlayerId.New();
        var buyer = PlayerId.New();
        await Seed(source, seller.Value, "item.market-test", 10);
        await Seed(source, buyer.Value, "currency.spirit-stone", 2000);
        var repository = new PostgresMarketRepository(source, options, NullLogger<PostgresMarketRepository>.Instance);
        var instrument = new MarketInstrument(new ItemId("item.market-test"));
        await using var runtime = new MarketRuntime(repository, options, NullLogger<MarketRuntime>.Instance);
        await runtime.StartAsync(default);
        var now = DateTimeOffset.UtcNow;
        var sell = new PlaceOrderRequest(OrderId.New(), seller, instrument, MarketSide.Sell, 100, 10, now, new IdempotencyKey(Guid.NewGuid().ToString()));
        var buy = new PlaceOrderRequest(OrderId.New(), buyer, instrument, MarketSide.Buy, 110, 4, now, new IdempotencyKey(Guid.NewGuid().ToString()));
        await runtime.PlaceAsync(sell);
        var result = await runtime.PlaceAsync(buy);
        Assert.Single(result.Fills);
        await runtime.PlaceAsync(buy);
        Assert.Equal(392d, await Balance(source, seller.Value, "currency.spirit-stone"));
        Assert.Equal(1600d, await Balance(source, buyer.Value, "currency.spirit-stone"));
        Assert.Equal(4d, await Balance(source, buyer.Value, "item.market-test"));
        var stored = await repository.LoadOrderAsync(sell.OrderId.Value);
        Assert.Equal(TimeSpan.FromDays(30), stored!.ExpiresAt - stored.CreatedAt);
        await runtime.StopAsync(default);
        await Sql(source, "UPDATE market.orders SET created_at=now()-interval '31 days', expires_at=now()-interval '1 day' WHERE order_id=$1", sell.OrderId.Value);
        await using var restored = new MarketRuntime(repository, options, NullLogger<MarketRuntime>.Instance);
        await restored.StartAsync(default);
        Assert.Empty(restored.GetOrders(seller));
        Assert.Equal("expired", (await repository.LoadOrderAsync(sell.OrderId.Value))!.Status);
        Assert.Equal(6d, await Balance(source, seller.Value, "item.market-test"));
        var fence = (long)(await Scalar(source, "SELECT fence_token FROM market.partition_leases WHERE partition_id=$1", options.MarketPartitionId))!;
        Assert.True(await repository.ExpireAsync(sell.OrderId.Value, seller.Value, fence, DateTimeOffset.UtcNow));
        Assert.Equal(6d, await Balance(source, seller.Value, "item.market-test"));

        var pending = new MarketOrderInput(Guid.NewGuid(), options.MarketPartitionId, buyer.Value, "buy", instrument.ItemId.Value, "", 10, 3, Guid.NewGuid().ToString(), now.AddDays(-31));
        await repository.CommitPlacementAsync(pending, [], fence);
        // 外部持久化边界失败必须整笔回滚，不能先下架再丢失退款。
        await Sql(source, $"CREATE FUNCTION market.reject_{pending.OrderId:N}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION '测试退款失败'; END $$; CREATE TRIGGER reject_{pending.OrderId:N} BEFORE UPDATE ON economy.player_asset_balances FOR EACH ROW WHEN (OLD.player_id = '{buyer.Value}') EXECUTE FUNCTION market.reject_{pending.OrderId:N}();");
        try
        {
            await Assert.ThrowsAsync<PostgresException>(async () => await repository.ExpireAsync(pending.OrderId, buyer.Value, fence, DateTimeOffset.UtcNow));
            Assert.Equal("active", (await repository.LoadOrderAsync(pending.OrderId))!.Status);
            Assert.Equal(1570d, await Balance(source, buyer.Value, "currency.spirit-stone"));
        }
        finally
        {
            await Sql(source, $"DROP TRIGGER reject_{pending.OrderId:N} ON economy.player_asset_balances; DROP FUNCTION market.reject_{pending.OrderId:N}();");
        }
        var competing = await Task.WhenAll(ExpireWithSerializationRetry(), ExpireWithSerializationRetry());
        Assert.All(competing, Assert.True);
        Assert.Equal(1600d, await Balance(source, buyer.Value, "currency.spirit-stone"));
        await restored.StopAsync(default);

        async Task<bool> ExpireWithSerializationRetry()
        {
            try { return await repository.ExpireAsync(pending.OrderId, buyer.Value, fence, DateTimeOffset.UtcNow); }
            catch (PostgresException error) when (error.SqlState == "40001")
            {
                return await repository.ExpireAsync(pending.OrderId, buyer.Value, fence, DateTimeOffset.UtcNow);
            }
        }
    }

    private static async Task Seed(NpgsqlDataSource source, Guid playerId, string asset, double amount)
    {
        await Sql(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())", playerId, playerId.ToString());
        await Sql(source, "INSERT INTO identity.players VALUES ($1,$1,$2,$2,now(),now(),1)", playerId, playerId.ToString());
        await Sql(source, "INSERT INTO economy.player_asset_balances(player_id,asset_id,variant,quantity,version,updated_at) VALUES ($1,$2,'',$3,1,now())", playerId, asset, amount);
    }

    private static async Task<double> Balance(NpgsqlDataSource source, Guid player, string asset) =>
        Convert.ToDouble(await Scalar(source, "SELECT quantity FROM economy.player_asset_balances WHERE player_id=$1 AND asset_id=$2 AND variant=''", player, asset));

    private static async Task<object?> Scalar(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return await command.ExecuteScalarAsync();
    }

    private static async Task Sql(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync();
    }
}
