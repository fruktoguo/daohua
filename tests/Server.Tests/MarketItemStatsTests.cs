using Domain.Common;
using Domain.Market;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 坊市行情统计集成测试（PostgreSQL）：挂单盘口聚合、成交量惰性累加、
/// 撤单清空回落以及跨日/跨周掩零与写侧换日归位的真实数据库回归。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class MarketItemStatsTests
{
    [Fact]
    public async Task PlacementFillsAndCancellationMaintainItemStatsAccurately()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("坊市行情卡集成测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);

        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = connectionString,
        }).Build()) with { MarketPartitionId = 0, MarketPartitionCount = 1 };

        var testAsset = $"item.stats-test-{Guid.NewGuid():N}";
        var seller = PlayerId.New();
        var seller2 = PlayerId.New();
        var buyer = PlayerId.New();
        var buyer2 = PlayerId.New();
        await Seed(source, seller.Value, testAsset, 100);
        await Seed(source, seller2.Value, testAsset, 100);
        await Seed(source, buyer.Value, "currency.spirit-stone", 50_000);
        await Seed(source, buyer2.Value, "currency.spirit-stone", 50_000);

        var repository = new PostgresMarketRepository(source, options, NullLogger<PostgresMarketRepository>.Instance);
        var instrument = new MarketInstrument(new ItemId(testAsset));
        await using var runtime = new MarketRuntime(repository, options, NullLogger<MarketRuntime>.Instance);
        await runtime.StartAsync(default);

        try
        {
            var now = DateTimeOffset.UtcNow;

            // 1. 初始挂卖单（卖一：100 元，10 件）
            var sellOrder1 = new PlaceOrderRequest(OrderId.New(), seller, instrument, MarketSide.Sell, 100, 10, now, new IdempotencyKey(Guid.NewGuid().ToString()));
            await runtime.PlaceAsync(sellOrder1);

            var statsList1 = await repository.LoadItemStatsAsync(testAsset);
            var stats1 = Assert.Single(statsList1);
            Assert.Equal(testAsset, stats1.AssetId);
            Assert.Equal(string.Empty, stats1.Variant);
            Assert.Equal(100d, stats1.BestAskPrice);
            Assert.Equal(10d, stats1.AskQuantity);
            Assert.Null(stats1.BestBidPrice);
            Assert.Equal(0d, stats1.BidQuantity);
            Assert.Equal(0d, stats1.DayVolume);
            Assert.Equal(0d, stats1.WeekVolume);

            // 2. 挂更优卖单（卖一更新为 90 元，总在售 15 件）+ 挂买单（买一：80 元，4 件）
            // 每标的每玩家至多一笔活动挂单，因此第二笔卖单与后续吃单买单分别由独立玩家发出。
            var sellOrder2 = new PlaceOrderRequest(OrderId.New(), seller2, instrument, MarketSide.Sell, 90, 5, now, new IdempotencyKey(Guid.NewGuid().ToString()));
            var buyOrder1 = new PlaceOrderRequest(OrderId.New(), buyer, instrument, MarketSide.Buy, 80, 4, now, new IdempotencyKey(Guid.NewGuid().ToString()));
            await runtime.PlaceAsync(sellOrder2);
            await runtime.PlaceAsync(buyOrder1);

            var statsList2 = await repository.LoadItemStatsAsync(testAsset);
            var stats2 = Assert.Single(statsList2);
            Assert.Equal(90d, stats2.BestAskPrice);
            Assert.Equal(15d, stats2.AskQuantity);
            Assert.Equal(80d, stats2.BestBidPrice);
            Assert.Equal(4d, stats2.BidQuantity);

            // 3. 买单吃单 3 件（以 90 元成交 3 件，卖一档剩余 2 件）
            var buyOrder2 = new PlaceOrderRequest(OrderId.New(), buyer2, instrument, MarketSide.Buy, 90, 3, now, new IdempotencyKey(Guid.NewGuid().ToString()));
            var matchResult = await runtime.PlaceAsync(buyOrder2);
            Assert.Single(matchResult.Fills);
            Assert.Equal(3d, matchResult.Fills[0].Quantity);

            var statsList3 = await repository.LoadItemStatsAsync(testAsset);
            var stats3 = Assert.Single(statsList3);
            Assert.Equal(90d, stats3.BestAskPrice);
            Assert.Equal(12d, stats3.AskQuantity); // 5 - 3 + 10 = 12
            Assert.Equal(80d, stats3.BestBidPrice);
            Assert.Equal(4d, stats3.BidQuantity);
            Assert.Equal(3d, stats3.DayVolume);
            Assert.Equal(3d, stats3.WeekVolume);
            Assert.Equal(90d, stats3.ReferencePrice);
            Assert.Equal(90d, runtime.TryGetReferencePrice(instrument));

            // 4. 撤销 90 元卖单余量（2 件）：卖一价自动回落到 100 元，成交量不受撤单影响保留
            await runtime.CancelAsync(sellOrder2.OrderId, seller2, instrument, $"market-cancel:{seller2.Value:D}:{sellOrder2.OrderId.Value:D}");
            var statsList4 = await repository.LoadItemStatsAsync(testAsset);
            var stats4 = Assert.Single(statsList4);
            Assert.Equal(100d, stats4.BestAskPrice);
            Assert.Equal(10d, stats4.AskQuantity);
            Assert.Equal(3d, stats4.DayVolume);
            Assert.Equal(3d, stats4.WeekVolume);

            // 5. 撤销最后笔卖单：卖侧盘口清空回退为 null/0，成交量仍保留
            await runtime.CancelAsync(sellOrder1.OrderId, seller, instrument, $"market-cancel:{seller.Value:D}:{sellOrder1.OrderId.Value:D}");
            var statsList5 = await repository.LoadItemStatsAsync(testAsset);
            var stats5 = Assert.Single(statsList5);
            Assert.Null(stats5.BestAskPrice);
            Assert.Equal(0d, stats5.AskQuantity);
            Assert.Equal(80d, stats5.BestBidPrice);
            Assert.Equal(4d, stats5.BidQuantity);
            Assert.Equal(3d, stats5.DayVolume);
            Assert.Equal(3d, stats5.WeekVolume);
        }
        finally
        {
            await runtime.StopAsync(default);
        }
    }

    [Fact]
    public async Task OutdatedDayAndWeekKeysAreLazilyMaskedAndResetOnNewFills()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("坊市行情卡集成测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);

        var options = ServerOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:ConnectionString"] = connectionString,
        }).Build()) with { MarketPartitionId = 0, MarketPartitionCount = 1 };

        var testAsset = $"item.stats-cycle-{Guid.NewGuid():N}";
        var seller = PlayerId.New();
        var buyer = PlayerId.New();
        await Seed(source, seller.Value, testAsset, 100);
        await Seed(source, buyer.Value, "currency.spirit-stone", 50_000);

        var repository = new PostgresMarketRepository(source, options, NullLogger<PostgresMarketRepository>.Instance);
        var instrument = new MarketInstrument(new ItemId(testAsset));
        await using var runtime = new MarketRuntime(repository, options, NullLogger<MarketRuntime>.Instance);
        await runtime.StartAsync(default);

        try
        {
            var now = DateTimeOffset.UtcNow;
            var sell = new PlaceOrderRequest(OrderId.New(), seller, instrument, MarketSide.Sell, 50, 20, now, new IdempotencyKey(Guid.NewGuid().ToString()));
            var buy = new PlaceOrderRequest(OrderId.New(), buyer, instrument, MarketSide.Buy, 50, 5, now, new IdempotencyKey(Guid.NewGuid().ToString()));
            await runtime.PlaceAsync(sell);
            await runtime.PlaceAsync(buy);

            var currentStats = Assert.Single(await repository.LoadItemStatsAsync(testAsset));
            Assert.Equal(5d, currentStats.DayVolume);
            Assert.Equal(5d, currentStats.WeekVolume);

            // 模拟跨日：将 day_key 改为昨天，week_key 保留当周
            await Sql(source, "UPDATE market.item_stats SET day_key = CURRENT_DATE - 1 WHERE asset_id = $1", testAsset);
            var maskedDayStats = Assert.Single(await repository.LoadItemStatsAsync(testAsset));
            Assert.Equal(0d, maskedDayStats.DayVolume);   // 读侧按 key 掩零
            Assert.Equal(5d, maskedDayStats.WeekVolume);  // 当周依然有效

            // 模拟跨周：将 week_key 改为上周
            await Sql(source, "UPDATE market.item_stats SET week_key = CURRENT_DATE - 8 WHERE asset_id = $1", testAsset);
            var maskedWeekStats = Assert.Single(await repository.LoadItemStatsAsync(testAsset));
            Assert.Equal(0d, maskedWeekStats.DayVolume);
            Assert.Equal(0d, maskedWeekStats.WeekVolume); // 读侧按 key 掩零

            // 当日新成交一笔（2 件）：写侧换日归位，dayVolume 从 2 开始累加，不再受旧日数据污染
            var buy2 = new PlaceOrderRequest(OrderId.New(), buyer, instrument, MarketSide.Buy, 50, 2, now, new IdempotencyKey(Guid.NewGuid().ToString()));
            await runtime.PlaceAsync(buy2);

            var resetStats = Assert.Single(await repository.LoadItemStatsAsync(testAsset));
            Assert.Equal(2d, resetStats.DayVolume);
            Assert.Equal(2d, resetStats.WeekVolume);
        }
        finally
        {
            await runtime.StopAsync(default);
        }
    }

    private static async Task Seed(NpgsqlDataSource source, Guid playerId, string asset, double amount)
    {
        await Sql(source, "INSERT INTO identity.accounts VALUES ($1,$2,'\\x00','\\x00',100000,1,now(),now())", playerId, playerId.ToString());
        await Sql(source, "INSERT INTO identity.players(player_id,account_id,display_name,display_name_normalized,invite_code,created_at,last_seen_at,status) VALUES ($1,$1,$2,$2,(left(replace($1::text,'-',''),16)),now(),now(),1)", playerId, playerId.ToString());
        await Sql(source, "INSERT INTO economy.player_asset_balances(player_id,asset_id,variant,quantity,version,updated_at) VALUES ($1,$2,'',$3,1,now())", playerId, asset, amount);
    }

    private static async Task Sql(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        await command.ExecuteNonQueryAsync();
    }
}
