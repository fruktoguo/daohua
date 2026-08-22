using System.Diagnostics;
using DaoHua.Domain.Market;
using DaoHua.Domain.Models;
using DaoHua.Domain.Simulation;

namespace DaoHua.LoadTests;

/// <summary>
/// 灵市订单 30 天到期低频批量回收压测场景。
/// 并发挂单模拟海量已超期卖单，通过时间索引高效检索并批量释放超期订单至待认领列表，不扫描全量历史订单。
/// </summary>
public static class MarketExpiryLoadScenario
{
    private static readonly string[] TradableItems =
    [
        DaoHuaItemIds.SpiritHerb,
        DaoHuaItemIds.QiGatheringPill,
        DaoHuaItemIds.QiBeastCore,
        DaoHuaItemIds.FireTalisman
    ];

    /// <summary>
    /// 执行灵市过期订单批量回收压测：
    /// 1. 并发生成指定玩家规模的已过期卖单并注入 64 分片撮合引擎；
    /// 2. 触发 Expire 批量回收逻辑，度量检索与状态流转耗时；
    /// 3. 统计回收前后内存变化与盘口活跃订单数。
    /// </summary>
    public static LoadTestReport Run(LoadTestOptions options)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var process = Process.GetCurrentProcess();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var cpuBefore = process.TotalProcessorTime;
        var now = DateTimeOffset.UtcNow;
        var createdAt = now - DaoHuaMarketPricePolicy.ListingLifetime - TimeSpan.FromSeconds(1);
        var engine = new DaoHuaMarketEngine(
            shardCount: 64,
            isTradable: itemId => TradableItems.Contains(itemId, StringComparer.Ordinal));
        var placementStopwatch = Stopwatch.StartNew();
        var placementSuccesses = 0;
        var placementErrors = 0;
        Parallel.For(0, options.Players, index =>
        {
            var instrument = CreateInstrument(index);
            var result = engine.Place(new DaoHuaMarketOrderRequest(
                $"expiry-{index}",
                CreatePlayerId(index),
                instrument,
                DaoHuaMarketSide.Sell,
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

    private static DaoHuaMarketInstrument CreateInstrument(int index) =>
        new(TradableItems[index % TradableItems.Length], index % 3);

    private static long GetPrice(DaoHuaMarketInstrument instrument, int index)
    {
        var vendorPrice = DaoHuaItemCatalog.TryGet(instrument.ItemId, out var definition)
            ? definition.SellPrice
            : DaoHuaMarketPricePolicy.AbsoluteMinimumPrice;
        return DaoHuaMarketPricePolicy.Normalize(
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
