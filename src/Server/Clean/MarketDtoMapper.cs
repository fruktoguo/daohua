using Domain.Market;
using Protocol;

namespace Server.Clean;

/// <summary>
/// 市场领域对象到协议 DTO 的映射器：标识转字符串、Side/Status 枚举转协议 snake_case 口径。
/// </summary>
public static class MarketDtoMapper
{
    /// <summary>
    /// 把一笔领域订单投影为协议订单 DTO。
    /// </summary>
    /// <param name="order">市场订单。</param>
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
        order.CreatedAt,
        order.ExpiresAt);

    /// <summary>
    /// 把挂单结果中的新订单投影为协议 DTO。
    /// </summary>
    /// <param name="result">挂单结算结果。</param>
    public static MarketOrderDto ToPlacement(PlacementResult result) => ToOrder(result.IncomingOrder);
    /// <summary>
    /// 把撤单结果中的订单投影为协议 DTO。
    /// </summary>
    /// <param name="result">撤单结算结果。</param>
    public static MarketOrderDto ToCancellation(CancellationResult result) => ToOrder(result.Order);
}
