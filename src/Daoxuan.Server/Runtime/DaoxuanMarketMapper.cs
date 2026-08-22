using Daoxuan.Domain.Market;
using Daoxuan.Protocol;

namespace Daoxuan.Server.Runtime;

/// <summary>
/// 市场领域对象与协议 DTO 转换映射器 (DaoxuanMarketMapper)。
/// <para>
/// 职责：
/// 1. 将撮合引擎的订单簿深度快照 (<see cref="DaoxuanMarketBookSnapshot"/>) 投影为 RESTful API 使用的 <see cref="DaoxuanMarketBookDto"/>。
/// 2. 映射市场运行监控指标 (<see cref="DaoxuanMarketEngineMetrics"/>)。
/// 3. 映射玩家订单详情 (<see cref="DaoxuanMarketOrderView"/>) 与挂单/撤单操作响应结果 (<see cref="DaoxuanMarketCommandOutcome"/>)。
/// </para>
/// </summary>
public static class DaoxuanMarketMapper
{
    /// <summary>
    /// 将订单簿深度快照投影为协议 DTO。
    /// </summary>
    public static DaoxuanMarketBookDto ToDto(DaoxuanMarketBookSnapshot snapshot)
    {
        return new DaoxuanMarketBookDto(
            snapshot.Instrument.ItemId,
            snapshot.Instrument.EnhancementLevel,
            snapshot.Asks.Select(ToLevel).ToArray(),
            snapshot.Bids.Select(ToLevel).ToArray(),
            snapshot.ActiveOrderCount,
            snapshot.Sequence);
    }

    /// <summary>
    /// 将撮合引擎监控指标投影为协议 DTO。
    /// </summary>
    public static DaoxuanMarketMetricsDto ToDto(DaoxuanMarketEngineMetrics metrics)
    {
        return new DaoxuanMarketMetricsDto(
            metrics.ShardCount,
            metrics.ActiveOrderCount,
            metrics.PlacementCount,
            metrics.MatchCount,
            metrics.CancelCount,
            metrics.RejectedCount,
            metrics.TradedQuantity,
            metrics.TradedGrossAmount);
    }

    /// <summary>
    /// 将市场订单视图投影为订单 DTO。
    /// </summary>
    public static DaoxuanMarketOrderDto ToDto(DaoxuanMarketOrderView order)
    {
        return new DaoxuanMarketOrderDto(
            order.OrderId,
            order.Instrument.ItemId,
            order.Instrument.EnhancementLevel,
            order.Side == DaoxuanMarketSide.Buy ? "buy" : "sell",
            order.Price,
            order.OriginalQuantity,
            order.RemainingQuantity,
            order.FilledQuantity,
            order.Status switch
            {
                DaoxuanMarketOrderStatus.Active => "active",
                DaoxuanMarketOrderStatus.PartiallyFilled => "partially-filled",
                DaoxuanMarketOrderStatus.Filled => "filled",
                DaoxuanMarketOrderStatus.Cancelled => "cancelled",
                DaoxuanMarketOrderStatus.Expired => "expired",
                _ => "unknown"
            },
            order.Sequence,
            order.CreatedAt,
            order.ExpiresAt);
    }

    /// <summary>
    /// 将市场挂单/撤单/提取产出结果投影为统一命令响应 DTO。
    /// </summary>
    public static DaoxuanMarketCommandResponseDto ToDto(DaoxuanMarketCommandOutcome outcome)
    {
        return new DaoxuanMarketCommandResponseDto(
            outcome.Success,
            outcome.Code,
            outcome.Message,
            outcome.Order is null ? null : ToDto(outcome.Order),
            outcome.Fills.Select(fill => new DaoxuanMarketFillDto(
                fill.TakerOrderId,
                fill.MakerOrderId,
                fill.BuyerId.ToString("D"),
                fill.SellerId.ToString("D"),
                fill.Instrument.ItemId,
                fill.Instrument.EnhancementLevel,
                fill.Price,
                fill.Quantity,
                fill.GrossAmount,
                fill.SellerProceeds,
                fill.SellerTax)).ToArray(),
            outcome.Snapshot);
    }

    private static DaoxuanMarketLevelDto ToLevel(DaoxuanMarketLevel level) =>
        new(level.Price, level.Quantity, level.OrderCount);
}
