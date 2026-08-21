using Daoxuan.Domain.Market;
using Daoxuan.Protocol;

namespace Daoxuan.Server.Runtime;

public static class DaoxuanMarketMapper
{
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
