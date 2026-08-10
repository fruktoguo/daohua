using System.Diagnostics;
using Xiuxian.Domain.Market;
using Xiuxian.Domain.Models;
using Xiuxian.Domain.Simulation;

namespace Xiuxian.LoadTests;

/// <summary>
/// 验证订单到期索引的低频回收成本。该场景只创建卖单，不把撮合和回收耗时混在一起。
/// </summary>
public static class MarketExpiryLoadScenario
{
    private static readonly string[] TradableItems =
    [
        XiuxianItemIds.SpiritHerb,
        XiuxianItemIds.QiGatheringPill,
        XiuxianItemIds.QiBeastCore,
        XiuxianItemIds.FireTalisman
    ];

    public static LoadTestReport Run(LoadTestOptions options)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var process = Process.GetCurrentProcess();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var cpuBefore = process.TotalProcessorTime;
        var now = DateTimeOffset.UtcNow;
        var createdAt = now - XiuxianMarketPricePolicy.ListingLifetime - TimeSpan.FromSeconds(1);
        var engine = new XiuxianMarketEngine(
            shardCount: 64,
            isTradable: itemId => TradableItems.Contains(itemId, StringComparer.Ordinal));
        var placementStopwatch = Stopwatch.StartNew();
        var placementSuccesses = 0;
        var placementErrors = 0;
        Parallel.For(0, options.Players, index =>
        {
            var instrument = CreateInstrument(index);
            var result = engine.Place(new XiuxianMarketOrderRequest(
                $"expiry-{index}",
                CreatePlayerId(index),
                instrument,
                XiuxianMarketSide.Sell,
                GetPrice(instrument, index),
                1,
                createdAt));
            if (result.Success) Interlocked.Increment(ref placementSuccesses);
            else Interlocked.Increment(ref placementErrors);
        });
        placementStopwatch.Stop();

        var expiryStopwatch = Stopwatch.StartNew();
        var expired = engine.Expire(now);
        expiryStopwatch.Stop();
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var managedAfter = GC.GetTotalMemory(forceFullCollection: false);
        var metrics = engine.GetMetrics();
        return new LoadTestReport(
            "market-expiry",
            options.Players,
            options.DurationSeconds,
            expiryStopwatch.Elapsed.TotalMilliseconds,
            (cpuAfter - cpuBefore).TotalMilliseconds,
            managedBefore,
            managedAfter,
            process.WorkingSet64,
            0,
            0,
            0,
            0,
            expired.Count,
            placementSuccesses,
            placementErrors + (placementSuccesses - expired.Count),
            new
            {
                placementElapsedMilliseconds = placementStopwatch.Elapsed.TotalMilliseconds,
                expiryElapsedMilliseconds = expiryStopwatch.Elapsed.TotalMilliseconds,
                placementSuccesses,
                expiredOrders = expired.Count,
                remainingActiveOrders = metrics.ActiveOrderCount,
                metrics
            },
            "批量回收已到期卖单；到期时间通过订单簿时间索引访问，不扫描终态历史。" );
    }

    private static XiuxianMarketInstrument CreateInstrument(int index) =>
        new(TradableItems[index % TradableItems.Length], index % 3);

    private static long GetPrice(XiuxianMarketInstrument instrument, int index)
    {
        var vendorPrice = XiuxianItemCatalog.TryGet(instrument.ItemId, out var definition)
            ? definition.SellPrice
            : XiuxianMarketPricePolicy.AbsoluteMinimumPrice;
        return XiuxianMarketPricePolicy.Normalize(
            checked(Math.Max(10, vendorPrice) + index % 5),
            vendorPrice,
            roundUp: true);
    }

    private static Guid CreatePlayerId(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index + 1);
        bytes[15] = 0x45;
        return new Guid(bytes);
    }
}
