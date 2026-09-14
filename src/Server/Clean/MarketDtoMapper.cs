using Domain.Market;
using Protocol;

namespace Server.Clean;

/// <summary>
/// 提供 MarketDtoMapper 相关功能的静态容器。
/// </summary>
public static class MarketDtoMapper
{
    /// <summary>
    /// 转换为Order。
    /// </summary>
    /// <param name="order">市场订单。</param>
    /// <returns>返回 MarketOrderDto 类型的结果。</returns>
    public static MarketOrderDto ToOrder(MarketOrder order) => new(
        order.OrderId.Value.ToString("D"),
        order.OwnerId.Value.ToString("D"),
        order.Instrument.ItemId.Value,
        string.IsNullOrEmpty(order.Instrument.Variant) ? null : order.Instrument.Variant,
        order.Side == MarketSide.Buy ? "buy" : "sell",
        order.Price,
        order.OriginalQuantity,
        order.RemainingQuantity,
        order.Status switch
        {
            MarketOrderStatus.Active => "active",
            MarketOrderStatus.PartiallyFilled => "partially_filled",
            MarketOrderStatus.Filled => "filled",
            MarketOrderStatus.Cancelled => "cancelled",
            MarketOrderStatus.Expired => "expired",
            _ => "unknown"
        },
        order.Sequence,
        order.CreatedAt);

    /// <summary>
    /// 转换为Placement。
    /// </summary>
    /// <param name="result">操作结果。</param>
    /// <returns>返回 MarketOrderDto 类型的结果。</returns>
    public static MarketOrderDto ToPlacement(PlacementResult result) => ToOrder(result.IncomingOrder);
    /// <summary>
    /// 转换为Cancellation。
    /// </summary>
    /// <param name="result">操作结果。</param>
    /// <returns>返回 MarketOrderDto 类型的结果。</returns>
    public static MarketOrderDto ToCancellation(CancellationResult result) => ToOrder(result.Order);
}
