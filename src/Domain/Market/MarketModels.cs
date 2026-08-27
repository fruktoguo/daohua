namespace Domain.Market;

public readonly record struct MarketInstrument(ItemId ItemId, string Variant = "")
{
    public override string ToString() => string.IsNullOrEmpty(Variant) ? ItemId.ToString() : $"{ItemId}:{Variant}";
}

public enum MarketSide : byte
{
    Buy = 0,
    Sell = 1
}

public enum MarketOrderStatus : byte
{
    Active = 0,
    PartiallyFilled = 1,
    Filled = 2,
    Cancelled = 3,
    Expired = 4
}

public sealed record PlaceOrderRequest(
    OrderId OrderId,
    PlayerId OwnerId,
    MarketInstrument Instrument,
    MarketSide Side,
    double Price,
    double Quantity,
    DateTimeOffset CreatedAt,
    IdempotencyKey IdempotencyKey)
{
}

public sealed record MarketOrder(
    OrderId OrderId,
    PlayerId OwnerId,
    MarketInstrument Instrument,
    MarketSide Side,
    double Price,
    double OriginalQuantity,
    double RemainingQuantity,
    long Sequence,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    MarketOrderStatus Status)
{
    public double FilledQuantity => OriginalQuantity - RemainingQuantity;
}

public sealed record MarketFill(
    TransactionId TradeId,
    OrderId BuyOrderId,
    OrderId SellOrderId,
    PlayerId BuyerId,
    PlayerId SellerId,
    MarketInstrument Instrument,
    double Price,
    double Quantity,
    DateTimeOffset TradedAt);

public sealed record PlacementResult(
    MarketOrder IncomingOrder,
    IReadOnlyList<MarketFill> Fills,
    long BookVersion);

public sealed record CancellationResult(MarketOrder Order, long BookVersion);

public sealed record MarketLevel(double Price, double Quantity, int OrderCount);

public sealed record OrderBookSnapshot(
    MarketInstrument Instrument,
    IReadOnlyList<MarketLevel> Asks,
    IReadOnlyList<MarketLevel> Bids,
    long Version,
    DateTimeOffset CapturedAt);

public sealed record MarketPartition(int PartitionId, int PartitionCount)
{
}
