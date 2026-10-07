using Domain.Common;
using Domain.Market;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 市场持久化集成测试（PostgreSQL）：撮合成交税费、30 天过期退款、
/// 事务原子性、幂等重放与 Serializable 并发竞争的端到端回归。
/// </summary>
[Collection("PostgresIntegration")]
public sealed class MarketPersistenceTests
{
    /// <summary>
    /// 部分成交→税费落库→重启过期退款全链路：幂等键重放不重复结算，
    /// 退款中途失败整笔回滚不丢单，并发 Expire 经 40001 重试后金额仍精确一致。
    /// </summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task PartialTradeTaxAndExpirationAreDurableAtomicAndIdempotent()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("市场集成测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
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

    /// <summary>
    /// 租约重获回归：写路径命中失效 fence 立即降级 fail-closed（写请求被拒、IsReady 回落）；
    /// 抢占方租约自然过期后，监管循环自动重获租约并重建盘口恢复写能力，无需进程重启。
    /// </summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task LeaseLossTriggersFailClosedThenAutoRecovery()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("市场租约重获测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = connectionString,
        }).Build()) with { MarketPartitionId = 0, MarketPartitionCount = 1 };
        var seller = PlayerId.New();
        await Seed(source, seller.Value, "item.market-recover", 10);
        var repository = new PostgresMarketRepository(source, options, NullLogger<PostgresMarketRepository>.Instance);
        var instrument = new MarketInstrument(new ItemId("item.market-recover"));
        await using var runtime = new MarketRuntime(repository, options, NullLogger<MarketRuntime>.Instance);
        await runtime.StartAsync(default);
        Assert.True(runtime.IsReady);

        // 模拟其他实例抢占写者租约：owner 换人且 fence 前移，本实例持有的 fence 立即失效。
        var usurper = Guid.NewGuid();
        await Sql(source, "UPDATE market.partition_leases SET owner_instance_id=$1, fence_token=fence_token+100, expires_at=now()+interval '30 seconds' WHERE partition_id=$2", usurper, options.MarketPartitionId);
        var order = new PlaceOrderRequest(OrderId.New(), seller, instrument, MarketSide.Sell, 10, 1, DateTimeOffset.UtcNow, new IdempotencyKey(Guid.NewGuid().ToString()));
        await Assert.ThrowsAsync<MarketLeaseLostException>(async () => await runtime.PlaceAsync(order));
        Assert.False(runtime.IsReady);

        // 抢占方租约不归还期间保持 fail-closed；其过期后监管循环重获写权并恢复就绪。
        await Sql(source, "UPDATE market.partition_leases SET expires_at=now()-interval '1 second' WHERE partition_id=$1", options.MarketPartitionId);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (!runtime.IsReady && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(250);
        Assert.True(runtime.IsReady);

        // 恢复后原请求可重新提交：订单落库且冻结资产按正常路径扣除。
        var result = await runtime.PlaceAsync(order);
        Assert.Equal(MarketOrderStatus.Active, result.IncomingOrder.Status);
        Assert.Equal(9d, await Balance(source, seller.Value, "item.market-recover"));
        await runtime.StopAsync(default);
    }

    /// <summary>
    /// 变体摘要只回有单簿：基础簿与 "+N" 簿各自独立统计卖一/买一价，
    /// 全部成交后的空簿跳过，其他品种簿不混入；输出按基础簿居前、等级升序。
    /// </summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task VariantSummariesReturnOnlyBooksWithActiveOrders()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("市场变体摘要测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = connectionString,
        }).Build()) with { MarketPartitionId = 0, MarketPartitionCount = 1 };
        var seller = PlayerId.New();
        var buyer = PlayerId.New();
        var itemId = new ItemId("equipment.variant-test");
        await Seed(source, seller.Value, "equipment.variant-test", 5);
        await SeedVariant(source, seller.Value, "equipment.variant-test", "+2", 5);
        await SeedVariant(source, seller.Value, "equipment.variant-test", "+4", 5);
        await Seed(source, buyer.Value, "currency.spirit-stone", 2000);
        var repository = new PostgresMarketRepository(source, options, NullLogger<PostgresMarketRepository>.Instance);
        await using var runtime = new MarketRuntime(repository, options, NullLogger<MarketRuntime>.Instance);
        await runtime.StartAsync(default);
        var now = DateTimeOffset.UtcNow;
        // 基础簿挂卖单、+2 簿挂卖单、+3 簿挂买单：三簿各带一档；+4 簿买卖全额成交后成空簿。
        await runtime.PlaceAsync(new PlaceOrderRequest(OrderId.New(), seller, new MarketInstrument(itemId), MarketSide.Sell, 100, 1, now, new IdempotencyKey(Guid.NewGuid().ToString())));
        await runtime.PlaceAsync(new PlaceOrderRequest(OrderId.New(), seller, new MarketInstrument(itemId, "+2"), MarketSide.Sell, 200, 1, now, new IdempotencyKey(Guid.NewGuid().ToString())));
        await runtime.PlaceAsync(new PlaceOrderRequest(OrderId.New(), buyer, new MarketInstrument(itemId, "+3"), MarketSide.Buy, 50, 1, now, new IdempotencyKey(Guid.NewGuid().ToString())));
        await runtime.PlaceAsync(new PlaceOrderRequest(OrderId.New(), seller, new MarketInstrument(itemId, "+4"), MarketSide.Sell, 60, 1, now, new IdempotencyKey(Guid.NewGuid().ToString())));
        await runtime.PlaceAsync(new PlaceOrderRequest(OrderId.New(), buyer, new MarketInstrument(itemId, "+4"), MarketSide.Buy, 60, 1, now, new IdempotencyKey(Guid.NewGuid().ToString())));

        var summaries = runtime.GetVariantSummaries(itemId);

        Assert.Equal(3, summaries.Count);
        Assert.Equal("", summaries[0].Variant);
        Assert.Equal(100, summaries[0].BestAskPrice);
        Assert.Null(summaries[0].BestBidPrice);
        Assert.Equal("+2", summaries[1].Variant);
        Assert.Equal(200, summaries[1].BestAskPrice);
        Assert.Null(summaries[1].BestBidPrice);
        Assert.Equal("+3", summaries[2].Variant);
        Assert.Null(summaries[2].BestAskPrice);
        Assert.Equal(50, summaries[2].BestBidPrice);
        await runtime.StopAsync(default);
    }

    /// <summary>造数：建账号、建玩家并预置一笔资产余额。</summary>
    private static async Task Seed(NpgsqlDataSource source, Guid playerId, string asset, double amount)
    {
        await Sql(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())", playerId, playerId.ToString());
        await Sql(source, "INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,invite_code,created_at,last_seen_at,status) VALUES ($1,$1,$2,$2,(left(replace($1::text,'-',''),16)),now(),now(),1)", playerId, playerId.ToString());
        await Sql(source, "INSERT INTO economy.player_asset_balances(player_id,asset_id,variant,quantity,version,updated_at) VALUES ($1,$2,'',$3,1,now())", playerId, asset, amount);
    }

    /// <summary>向已建账号的玩家追加一笔带变体的资产余额行（建号由 <see cref="Seed"/> 负责）。</summary>
    private static async Task SeedVariant(NpgsqlDataSource source, Guid playerId, string asset, string variant, double amount)
    {
        await Sql(source, "INSERT INTO economy.player_asset_balances(player_id,asset_id,variant,quantity,version,updated_at) VALUES ($1,$2,$3,$4,1,now())", playerId, asset, variant, amount);
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
