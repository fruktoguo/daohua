using System.Diagnostics;
using Daoxuan.Domain.Market;
using Daoxuan.Domain.Models;
using Daoxuan.Domain.Simulation;

namespace Daoxuan.LoadTests;

public static class MarketLoadScenario
{
    private static readonly string[] TradableItems =
    [
        DaoxuanItemIds.SpiritHerb,
        DaoxuanItemIds.QiGatheringPill,
        DaoxuanItemIds.QiBeastCore,
        DaoxuanItemIds.FireTalisman
    ];

    public static LoadTestReport Run(LoadTestOptions options)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var process = Process.GetCurrentProcess();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var cpuBefore = process.TotalProcessorTime;
        var engine = new DaoxuanMarketEngine(
            shardCount: 64,
            isTradable: itemId => TradableItems.Contains(itemId, StringComparer.Ordinal));
        var sellers = Math.Max(1, options.Players / 2);
        var buyers = options.Players - sellers;
        var placementStopwatch = Stopwatch.StartNew();
        var sellerSuccesses = 0;
        Parallel.For(0, sellers, index =>
        {
            var instrument = CreateInstrument(index);
            var result = engine.Place(new DaoxuanMarketOrderRequest(
                $"sell-{index}",
                CreatePlayerId(index),
                instrument,
                DaoxuanMarketSide.Sell,
                GetSellerPrice(instrument, index),
                1 + index % 3));
            if (result.Success) Interlocked.Increment(ref sellerSuccesses);
        });

        var sellerElapsed = placementStopwatch.Elapsed;
        var buyerSuccesses = 0;
        var fills = 0L;
        Parallel.For(0, buyers, index =>
        {
            var orderIndex = sellers + index;
            var instrument = CreateInstrument(orderIndex);
            var result = engine.Place(new DaoxuanMarketOrderRequest(
                $"buy-{orderIndex}",
                CreatePlayerId(orderIndex),
                instrument,
                DaoxuanMarketSide.Buy,
                GetBuyerPrice(instrument),
                1 + orderIndex % 2));
            if (result.Success)
            {
                Interlocked.Increment(ref buyerSuccesses);
                Interlocked.Add(ref fills, result.Fills.Count);
            }
        });
        placementStopwatch.Stop();

        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var managedAfter = GC.GetTotalMemory(forceFullCollection: false);
        var metrics = engine.GetMetrics();
        var elapsedMilliseconds = placementStopwatch.Elapsed.TotalMilliseconds;
        return new LoadTestReport(
            "market",
            options.Players,
            options.DurationSeconds,
            elapsedMilliseconds,
            (cpuAfter - cpuBefore).TotalMilliseconds,
            managedBefore,
            managedAfter,
            process.WorkingSet64,
            0,
            0,
            0,
            0,
            fills,
            metrics.PlacementCount,
            options.Players - sellerSuccesses - buyerSuccesses,
            new
            {
                shardCount = engine.ShardCount,
                sellerOrders = sellers,
                buyerOrders = buyers,
                sellerSuccesses,
                buyerSuccesses,
                fills,
                sellerPhaseElapsedMilliseconds = sellerElapsed.TotalMilliseconds,
                totalOrdersPerSecond = elapsedMilliseconds <= 0 ? 0 : options.Players / (elapsedMilliseconds / 1_000d),
                metrics
            },
            "固定品种/品阶的并发挂牌与交叉撮合；订单簿按分片串行，未包含外部账本和网络序列化耗时。" );
    }

    private static DaoxuanMarketInstrument CreateInstrument(int index)
    {
        return new DaoxuanMarketInstrument(
            TradableItems[index % TradableItems.Length],
            index % 3);
    }

    private static long GetSellerPrice(DaoxuanMarketInstrument instrument, int index)
    {
        var vendorPrice = GetVendorPrice(instrument.ItemId);
        // 负载使用目录回收价作为下限，再通过同一官方档位策略生成合法挂牌价。
        return DaoxuanMarketPricePolicy.Normalize(
            checked(Math.Max(10, vendorPrice) + index % 5),
            vendorPrice,
            roundUp: true);
    }

    private static long GetBuyerPrice(DaoxuanMarketInstrument instrument)
    {
        var vendorPrice = GetVendorPrice(instrument.ItemId);
        return DaoxuanMarketPricePolicy.Normalize(
            Math.Max(1_000_000, vendorPrice),
            vendorPrice,
            roundUp: true);
    }

    private static long GetVendorPrice(string itemId)
    {
        return DaoxuanItemCatalog.TryGet(itemId, out var definition)
            ? definition.SellPrice
            : DaoxuanMarketPricePolicy.AbsoluteMinimumPrice;
    }

    private static Guid CreatePlayerId(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index + 1);
        bytes[15] = 0x4D;
        return new Guid(bytes);
    }
}
