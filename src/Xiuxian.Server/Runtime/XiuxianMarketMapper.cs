using Xiuxian.Domain.Market;
using Xiuxian.Protocol;

namespace Xiuxian.Server.Runtime;

public static class XiuxianMarketMapper
{
    public static XiuxianMarketBookDto ToDto(XiuxianMarketBookSnapshot snapshot)
    {
        return new XiuxianMarketBookDto(
            snapshot.Instrument.ItemId,
            snapshot.Instrument.EnhancementLevel,
            snapshot.Asks.Select(ToLevel).ToArray(),
            snapshot.Bids.Select(ToLevel).ToArray(),
            snapshot.ActiveOrderCount,
            snapshot.Sequence);
    }

    public static XiuxianMarketMetricsDto ToDto(XiuxianMarketEngineMetrics metrics)
    {
        return new XiuxianMarketMetricsDto(
            metrics.ShardCount,
            metrics.ActiveOrderCount,
            metrics.PlacementCount,
            metrics.MatchCount,
            metrics.CancelCount,
            metrics.RejectedCount,
            metrics.TradedQuantity,
            metrics.TradedGrossAmount);
    }

    public static XiuxianMarketOrderDto ToDto(XiuxianMarketOrderView order)
    {
        return new XiuxianMarketOrderDto(
            order.OrderId,
            order.Instrument.ItemId,
            order.Instrument.EnhancementLevel,
            order.Side == XiuxianMarketSide.Buy ? "buy" : "sell",
            order.Price,
            order.OriginalQuantity,
            order.RemainingQuantity,
            order.FilledQuantity,
            order.Status switch
            {
                XiuxianMarketOrderStatus.Active => "active",
                XiuxianMarketOrderStatus.PartiallyFilled => "partially-filled",
                XiuxianMarketOrderStatus.Filled => "filled",
                XiuxianMarketOrderStatus.Cancelled => "cancelled",
                XiuxianMarketOrderStatus.Expired => "expired",
                _ => "unknown"
            },
            order.Sequence,
            order.CreatedAt,
            order.ExpiresAt);
    }

    public static XiuxianMarketCommandResponseDto ToDto(XiuxianMarketCommandOutcome outcome)
    {
        return new XiuxianMarketCommandResponseDto(
            outcome.Success,
            outcome.Code,
            outcome.Message,
            outcome.Order is null ? null : ToDto(outcome.Order),
            outcome.Fills.Select(fill => new XiuxianMarketFillDto(
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

    private static XiuxianMarketLevelDto ToLevel(XiuxianMarketLevel level) =>
        new(level.Price, level.Quantity, level.OrderCount);
}
