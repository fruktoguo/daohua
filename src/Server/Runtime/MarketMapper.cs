// ==============================================================================
// 文件名称: MarketMapper.cs
// 主要职责: 市场领域模型与网络传输协议 DTO 之间的投影转换映射
// 核心逻辑 / 定义内容:
//   1. 将撮合引擎订单簿深度快照投影为 RESTful API 使用的 MarketBookDto。
//   2. 映射市场运行监控指标（MarketEngineMetrics -> MarketMetricsDto）。
//   3. 映射玩家市场订单视图与挂单、撤单、领奖等操作的响应结果 DTO。
// ==============================================================================

using Domain.Market;
using Domain.Models;
using Protocol;

namespace Server.Runtime;

/// <summary>
/// 市场领域对象与协议 DTO 转换映射器 (MarketMapper)。
/// <para>
/// 职责：
/// 1. 将撮合引擎的订单簿深度快照 (<see cref="MarketBookSnapshot"/>) 投影为 RESTful API 使用的 <see cref="MarketBookDto"/>。
/// 2. 映射市场运行监控指标 (<see cref="MarketEngineMetrics"/>)。
/// 3. 映射玩家订单详情 (<see cref="MarketOrderView"/>) 与挂单/撤单操作响应结果 (<see cref="MarketCommandOutcome"/>)。
/// </para>
/// </summary>
public static class MarketMapper
{
    /// <summary>
    /// 将订单簿深度快照投影为协议 DTO。
    /// </summary>
    public static MarketBookDto ToDto(MarketBookSnapshot snapshot)
    {
        return new MarketBookDto(
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
    public static MarketMetricsDto ToDto(MarketEngineMetrics metrics)
    {
        return new MarketMetricsDto(
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
    public static MarketOrderDto ToDto(MarketOrderView order)
    {
        return new MarketOrderDto(
            order.OrderId,
            order.Instrument.ItemId,
            order.Instrument.EnhancementLevel,
            order.Side == MarketSide.Buy ? "buy" : "sell",
            order.Price,
            order.OriginalQuantity,
            order.RemainingQuantity,
            order.FilledQuantity,
            order.Status switch
            {
                MarketOrderStatus.Active => "active",
                MarketOrderStatus.PartiallyFilled => "partially-filled",
                MarketOrderStatus.Filled => "filled",
                MarketOrderStatus.Cancelled => "cancelled",
                MarketOrderStatus.Expired => "expired",
                _ => "unknown"
            },
            order.Sequence,
            order.CreatedAt,
            order.ExpiresAt);
    }

    /// <summary>
    /// 将市场挂单/撤单/提取产出结果投影为统一命令响应 DTO。
    /// </summary>
    public static MarketCommandResponseDto ToDto(MarketCommandOutcome outcome)
    {
        return new MarketCommandResponseDto(
            outcome.Success,
            outcome.Code,
            outcome.Message,
            outcome.Order is null ? null : ToDto(outcome.Order),
            outcome.Fills.Select(fill => new MarketFillDto(
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

    private static MarketLevelDto ToLevel(MarketLevel level) =>
        new(level.Price, level.Quantity, level.OrderCount);
}
