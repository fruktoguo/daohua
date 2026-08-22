// ==============================================================================
// 文件名称: MarketLoadScenario.cs
// 主要职责: 灵市高并发挂单与交叉撮合吞吐量（TPS）压测场景。
// 核心逻辑 / 定义内容:
//   1. 模拟多玩家多线程并发提交买单与卖单至 64 分片撮合引擎。
//   2. 压测价格-时间优先撮合、部分成交、资产冻结结算与订单状态流转的吞吐极限。
//   3. 度量每秒撮合成交笔数（TPS）、内存分配及各交易品类分片指标。
// ==============================================================================

using System.Diagnostics;
using Domain.Market;
using Domain.Models;
using Domain.Simulation;

namespace LoadTests;

/// <summary>
/// 灵市高并发挂单与交叉撮合吞吐量压测场景。
/// 模拟多玩家并发挂牌买卖单，跨 64 分片进行并行撮合，度量撮合引擎每秒订单处理能力（TPS）与内存增长。
/// </summary>
public static class MarketLoadScenario
{
    private static readonly string[] TradableItems =
    [
        ItemIds.SpiritHerb,
        ItemIds.QiGatheringPill,
        ItemIds.QiBeastCore,
        ItemIds.FireTalisman
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
        var engine = new MarketEngine(
            shardCount: 64,
            isTradable: itemId => TradableItems.Contains(itemId, StringComparer.Ordinal));
        var sellers = Math.Max(1, options.Players / 2);
        var buyers = options.Players - sellers;
        var placementStopwatch = Stopwatch.StartNew();
        var sellerSuccesses = 0;
        Parallel.For(0, sellers, index =>
        {
            var instrument = CreateInstrument(index);
            var result = engine.Place(new MarketOrderRequest(
                $"sell-{index}",
                CreatePlayerId(index),
                instrument,
                MarketSide.Sell,
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
            var result = engine.Place(new MarketOrderRequest(
                $"buy-{orderIndex}",
                CreatePlayerId(orderIndex),
                instrument,
                MarketSide.Buy,
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

    private static MarketInstrument CreateInstrument(int index)
    {
        return new MarketInstrument(
            TradableItems[index % TradableItems.Length],
            index % 3);
    }

    private static long GetSellerPrice(MarketInstrument instrument, int index)
    {
        var vendorPrice = GetVendorPrice(instrument.ItemId);
        // 负载使用目录回收价作为下限，再通过同一官方档位策略生成合法挂牌价。
        return MarketPricePolicy.Normalize(
            checked(Math.Max(10, vendorPrice) + index % 5),
            vendorPrice,
            roundUp: true);
    }

    private static long GetBuyerPrice(MarketInstrument instrument)
    {
        var vendorPrice = GetVendorPrice(instrument.ItemId);
        return MarketPricePolicy.Normalize(
            Math.Max(1_000_000, vendorPrice),
            vendorPrice,
            roundUp: true);
    }

    private static long GetVendorPrice(string itemId)
    {
        return ItemCatalog.TryGet(itemId, out var definition)
            ? definition.SellPrice
            : MarketPricePolicy.AbsoluteMinimumPrice;
    }

    private static Guid CreatePlayerId(int index)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, index + 1);
        bytes[15] = 0x4D;
        return new Guid(bytes);
    }
}
