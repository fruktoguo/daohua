using Domain.Market;
using Protocol;

namespace Server.Clean;

public static class MarketDtoMapper
{
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

    public static MarketOrderDto ToPlacement(PlacementResult result) => ToOrder(result.IncomingOrder);
    public static MarketOrderDto ToCancellation(CancellationResult result) => ToOrder(result.Order);
}
