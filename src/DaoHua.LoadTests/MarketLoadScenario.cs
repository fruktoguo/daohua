using System.Diagnostics;
using DaoHua.Domain.Market;
using DaoHua.Domain.Models;
using DaoHua.Domain.Simulation;

namespace DaoHua.LoadTests;

/// <summary>
/// 灵市高并发挂单与交叉撮合吞吐量压测场景。
/// 模拟多玩家并发挂牌买卖单，跨 64 分片进行并行撮合，度量撮合引擎每秒订单处理能力（TPS）与内存增长。
/// </summary>
public static class MarketLoadScenario
{
    private static readonly string[] TradableItems =
    [
        DaoHuaItemIds.SpiritHerb,
        DaoHuaItemIds.QiGatheringPill,
        DaoHuaItemIds.QiBeastCore,
        DaoHuaItemIds.FireTalisman
    ];

    /// <summary>
    /// 执行灵市撮合吞吐量压测：
    /// 1. 并发提交卖方挂单建立订单簿盘口；
    /// 2. 并发提交买方撮合单触发交叉成交；
    /// 3. 统计成交笔数（Fills）、撮合耗时、TPS 及各分片指标。
    /// </summary>
    public static LoadTestReport Run(LoadTestOptions options)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var process = Process.GetCurrentProcess();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var cpuBefore = process.TotalProcessorTime;
        var engine = new DaoHuaMarketEngine(
            shardCount: 64,
            isTradable: itemId => TradableItems.Contains(itemId, StringComparer.Ordinal));
        var sellers = Math.Max(1, options.Players / 2);
        var buyers = options.Players - sellers;
        var placementStopwatch = Stopwatch.StartNew();
        var sellerSuccesses = 0;
        Parallel.For(0, sellers, index =>
        {
            var instrument = CreateInstrument(index);
            var result = engine.Place(new DaoHuaMarketOrderRequest(
                $"sell-{index}",
                CreatePlayerId(index),
                instrument,
                DaoHuaMarketSide.Sell,
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
            var result = engine.Place(new DaoHuaMarketOrderRequest(
                $"buy-{orderIndex}",
                CreatePlayerId(orderIndex),
                instrument,
                DaoHuaMarketSide.Buy,
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

    private static DaoHuaMarketInstrument CreateInstrument(int index)
    {
        return new DaoHuaMarketInstrument(
            TradableItems[index % TradableItems.Length],
            index % 3);
    }

    private static long GetSellerPrice(DaoHuaMarketInstrument instrument, int index)
    {
        var vendorPrice = GetVendorPrice(instrument.ItemId);
        // 负载使用目录回收价作为下限，再通过同一官方档位策略生成合法挂牌价。
        return DaoHuaMarketPricePolicy.Normalize(
            checked(Math.Max(10, vendorPrice) + index % 5),
            vendorPrice,
            roundUp: true);
    }

    private static long GetBuyerPrice(DaoHuaMarketInstrument instrument)
    {
        var vendorPrice = GetVendorPrice(instrument.ItemId);
        return DaoHuaMarketPricePolicy.Normalize(
            Math.Max(1_000_000, vendorPrice),
            vendorPrice,
            roundUp: true);
    }

    private static long GetVendorPrice(string itemId)
    {
        return DaoHuaItemCatalog.TryGet(itemId, out var definition)
            ? definition.SellPrice
            : DaoHuaMarketPricePolicy.AbsoluteMinimumPrice;
    }

    private static Guid CreatePlayerId(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index + 1);
        bytes[15] = 0x4D;
        return new Guid(bytes);
    }
}
