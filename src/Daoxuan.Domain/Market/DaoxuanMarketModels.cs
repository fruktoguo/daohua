namespace Daoxuan.Domain.Market;

public enum DaoxuanMarketSide
{
    Buy = 0,
    Sell = 1
}

public enum DaoxuanMarketOrderStatus
{
    Active = 0,
    PartiallyFilled = 1,
    Filled = 2,
    Cancelled = 3,
    Expired = 4
}

public readonly record struct DaoxuanMarketInstrument(string ItemId, int EnhancementLevel = 0)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(ItemId) && EnhancementLevel >= 0;
}

public sealed record DaoxuanMarketOrderRequest(
    string OrderId,
    Guid OwnerId,
    DaoxuanMarketInstrument Instrument,
    DaoxuanMarketSide Side,
    long Price,
    long Quantity,
    DateTimeOffset? CreatedAt = null);

public sealed record DaoxuanMarketOrderView(
    string OrderId,
    Guid OwnerId,
    DaoxuanMarketInstrument Instrument,
    DaoxuanMarketSide Side,
    long Price,
    long OriginalQuantity,
    long RemainingQuantity,
    long FilledQuantity,
    DaoxuanMarketOrderStatus Status,
    long Sequence,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset ExpiresAt = default);

public sealed record DaoxuanMarketFill(
    string TakerOrderId,
    string MakerOrderId,
    Guid BuyerId,
    Guid SellerId,
    DaoxuanMarketInstrument Instrument,
    long Price,
    long Quantity,
    long GrossAmount,
    long SellerProceeds,
    long SellerTax);

public sealed record DaoxuanMarketPlacementResult(
    bool Success,
    string Code,
    string Message,
    DaoxuanMarketOrderView? Order,
    IReadOnlyList<DaoxuanMarketFill> Fills)
{
    public static DaoxuanMarketPlacementResult Fail(string code, string message) =>
        new(false, code, message, null, []);
}

public sealed record DaoxuanMarketCancellationResult(
    bool Success,
    string Code,
    string Message,
    DaoxuanMarketOrderView? Order)
{
    public static DaoxuanMarketCancellationResult Fail(string code, string message) =>
        new(false, code, message, null);
}

public sealed record DaoxuanMarketLevel(long Price, long Quantity, int OrderCount);

public sealed record DaoxuanMarketBookSnapshot(
    DaoxuanMarketInstrument Instrument,
    IReadOnlyList<DaoxuanMarketLevel> Asks,
    IReadOnlyList<DaoxuanMarketLevel> Bids,
    long ActiveOrderCount,
    long Sequence);

/// <summary>
/// 盘口只读版本。它只包含判断条件响应所需的计数，不复制盘口档位，适合高频轮询。
/// </summary>
public readonly record struct DaoxuanMarketBookVersion(
    long Sequence,
    long ActiveOrderCount,
    long PlacementCount,
    long MatchCount,
    long CancelCount,
    long RejectedCount,
    long TradedQuantity,
    long TradedGrossAmount);

public sealed record DaoxuanMarketEngineMetrics(
    int ShardCount,
    long ActiveOrderCount,
    long PlacementCount,
    long MatchCount,
    long CancelCount,
    long RejectedCount,
    long TradedQuantity,
    long TradedGrossAmount);

/// <summary>
/// 订单簿中的活动订单快照。终态订单只保留在有限历史中，不参与恢复。
/// </summary>
public sealed record DaoxuanMarketOrderSnapshot(
    string OrderId,
    Guid OwnerId,
    DaoxuanMarketInstrument Instrument,
    DaoxuanMarketSide Side,
    long Price,
    long OriginalQuantity,
    long RemainingQuantity,
    DaoxuanMarketOrderStatus Status,
    long Sequence,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset ExpiresAt = default);

/// <summary>
/// 单品种订单簿快照，包含恢复 FIFO 所需的序列和累计指标。
/// </summary>
public sealed record DaoxuanMarketBookState(
    DaoxuanMarketInstrument Instrument,
    IReadOnlyList<DaoxuanMarketOrderSnapshot> ActiveOrders,
    long Sequence,
    long PlacementCount,
    long MatchCount,
    long CancelCount,
    long RejectedCount,
    long TradedQuantity,
    long TradedGrossAmount);

public sealed record DaoxuanMarketEngineState(IReadOnlyList<DaoxuanMarketBookState> Books);

public sealed record DaoxuanMarketReservationState(
    string OrderId,
    Guid OwnerId,
    DaoxuanMarketInstrument Instrument,
    DaoxuanMarketSide Side,
    long RemainingQuantity,
    long ReservedCoins);

public sealed record DaoxuanMarketPendingItemState(
    DaoxuanMarketInstrument Instrument,
    long Quantity);

public sealed record DaoxuanMarketPendingCreditsState(
    Guid OwnerId,
    long Spirit,
    IReadOnlyList<DaoxuanMarketPendingItemState> Items);

public sealed record DaoxuanMarketCoordinatorState(
    IReadOnlyList<DaoxuanMarketReservationState> Reservations,
    IReadOnlyList<DaoxuanMarketPendingCreditsState> PendingCredits);

/// <summary>
/// 市场账本快照。Revision 用于并发写回时丢弃旧快照，SchemaVersion 便于未来迁移。
/// </summary>
public sealed record DaoxuanMarketLedgerState(
    int SchemaVersion,
    long Revision,
    DaoxuanMarketEngineState Engine,
    DaoxuanMarketCoordinatorState Coordinator);

public static class DaoxuanMarketTaxPolicy
{
    public const double StandardSellerTaxRate = 0.02;
    public const double SpecialBagSellerTaxRate = 0.18;

    public static double GetSellerTaxRate(string itemId)
    {
        // 预留特殊袋类物品的税率；当前修仙目录只开放普通物品。
        return itemId.Contains("bag", StringComparison.OrdinalIgnoreCase)
            ? SpecialBagSellerTaxRate
            : StandardSellerTaxRate;
    }

    public static long CalculateSellerProceeds(long grossAmount, double taxRate)
    {
        if (grossAmount < 0) throw new ArgumentOutOfRangeException(nameof(grossAmount));
        if (double.IsNaN(taxRate) || double.IsInfinity(taxRate) || taxRate is < 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(taxRate));
        }
        // 使用 decimal 避免 long 金额在 double 乘法中的低位舍入误差。
        var retained = (decimal)grossAmount * (1m - (decimal)taxRate);
        return checked((long)decimal.Floor(retained));
    }
}
