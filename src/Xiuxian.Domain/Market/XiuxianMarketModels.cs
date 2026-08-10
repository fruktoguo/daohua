namespace Xiuxian.Domain.Market;

public enum XiuxianMarketSide
{
    Buy = 0,
    Sell = 1
}

public enum XiuxianMarketOrderStatus
{
    Active = 0,
    PartiallyFilled = 1,
    Filled = 2,
    Cancelled = 3,
    Expired = 4
}

public readonly record struct XiuxianMarketInstrument(string ItemId, int EnhancementLevel = 0)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(ItemId) && EnhancementLevel >= 0;
}

public sealed record XiuxianMarketOrderRequest(
    string OrderId,
    Guid OwnerId,
    XiuxianMarketInstrument Instrument,
    XiuxianMarketSide Side,
    long Price,
    long Quantity,
    DateTimeOffset? CreatedAt = null);

public sealed record XiuxianMarketOrderView(
    string OrderId,
    Guid OwnerId,
    XiuxianMarketInstrument Instrument,
    XiuxianMarketSide Side,
    long Price,
    long OriginalQuantity,
    long RemainingQuantity,
    long FilledQuantity,
    XiuxianMarketOrderStatus Status,
    long Sequence,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset ExpiresAt = default);

public sealed record XiuxianMarketFill(
    string TakerOrderId,
    string MakerOrderId,
    Guid BuyerId,
    Guid SellerId,
    XiuxianMarketInstrument Instrument,
    long Price,
    long Quantity,
    long GrossAmount,
    long SellerProceeds,
    long SellerTax);

public sealed record XiuxianMarketPlacementResult(
    bool Success,
    string Code,
    string Message,
    XiuxianMarketOrderView? Order,
    IReadOnlyList<XiuxianMarketFill> Fills)
{
    public static XiuxianMarketPlacementResult Fail(string code, string message) =>
        new(false, code, message, null, []);
}

public sealed record XiuxianMarketCancellationResult(
    bool Success,
    string Code,
    string Message,
    XiuxianMarketOrderView? Order)
{
    public static XiuxianMarketCancellationResult Fail(string code, string message) =>
        new(false, code, message, null);
}

public sealed record XiuxianMarketLevel(long Price, long Quantity, int OrderCount);

public sealed record XiuxianMarketBookSnapshot(
    XiuxianMarketInstrument Instrument,
    IReadOnlyList<XiuxianMarketLevel> Asks,
    IReadOnlyList<XiuxianMarketLevel> Bids,
    long ActiveOrderCount,
    long Sequence);

/// <summary>
/// 盘口只读版本。它只包含判断条件响应所需的计数，不复制盘口档位，适合高频轮询。
/// </summary>
public readonly record struct XiuxianMarketBookVersion(
    long Sequence,
    long ActiveOrderCount,
    long PlacementCount,
    long MatchCount,
    long CancelCount,
    long RejectedCount,
    long TradedQuantity,
    long TradedGrossAmount);

public sealed record XiuxianMarketEngineMetrics(
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
public sealed record XiuxianMarketOrderSnapshot(
    string OrderId,
    Guid OwnerId,
    XiuxianMarketInstrument Instrument,
    XiuxianMarketSide Side,
    long Price,
    long OriginalQuantity,
    long RemainingQuantity,
    XiuxianMarketOrderStatus Status,
    long Sequence,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset ExpiresAt = default);

/// <summary>
/// 单品种订单簿快照，包含恢复 FIFO 所需的序列和累计指标。
/// </summary>
public sealed record XiuxianMarketBookState(
    XiuxianMarketInstrument Instrument,
    IReadOnlyList<XiuxianMarketOrderSnapshot> ActiveOrders,
    long Sequence,
    long PlacementCount,
    long MatchCount,
    long CancelCount,
    long RejectedCount,
    long TradedQuantity,
    long TradedGrossAmount);

public sealed record XiuxianMarketEngineState(IReadOnlyList<XiuxianMarketBookState> Books);

public sealed record XiuxianMarketReservationState(
    string OrderId,
    Guid OwnerId,
    XiuxianMarketInstrument Instrument,
    XiuxianMarketSide Side,
    long RemainingQuantity,
    long ReservedCoins);

public sealed record XiuxianMarketPendingItemState(
    XiuxianMarketInstrument Instrument,
    long Quantity);

public sealed record XiuxianMarketPendingCreditsState(
    Guid OwnerId,
    long Spirit,
    IReadOnlyList<XiuxianMarketPendingItemState> Items);

public sealed record XiuxianMarketCoordinatorState(
    IReadOnlyList<XiuxianMarketReservationState> Reservations,
    IReadOnlyList<XiuxianMarketPendingCreditsState> PendingCredits);

/// <summary>
/// 市场账本快照。Revision 用于并发写回时丢弃旧快照，SchemaVersion 便于未来迁移。
/// </summary>
public sealed record XiuxianMarketLedgerState(
    int SchemaVersion,
    long Revision,
    XiuxianMarketEngineState Engine,
    XiuxianMarketCoordinatorState Coordinator);

public static class XiuxianMarketTaxPolicy
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
