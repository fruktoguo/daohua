namespace Domain.Market;

/// <summary>
/// 坊市交易标的物（标的道具 ID 与变体标识）。
/// </summary>
public readonly record struct MarketInstrument(ItemId ItemId, string Variant = "")
{
    public override string ToString() => string.IsNullOrEmpty(Variant) ? ItemId.ToString() : $"{ItemId}:{Variant}";
}

/// <summary>交易买卖方向</summary>
public enum MarketSide : byte
{
    /// <summary>买单（出资求购）</summary>
    Buy = 0,
    /// <summary>卖单（出货变现）</summary>
    Sell = 1
}

/// <summary>
/// 订单生命周期状态。
/// </summary>
public enum MarketOrderStatus : byte
{
    /// <summary>活跃挂牌中，尚未发生撮合成交</summary>
    Active = 0,
    /// <summary>部分成交，剩余数量继续在货架中排队</summary>
    PartiallyFilled = 1,
    /// <summary>已全部成交闭环</summary>
    Filled = 2,
    /// <summary>挂单已被持有者主动撤回并解冻资金/货物</summary>
    Cancelled = 3,
    /// <summary>挂单已过有效期限，系统自动下架</summary>
    Expired = 4
}

/// <summary>
/// 玩家挂单请求入参。
/// </summary>
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

/// <summary>
/// 订单簿中维护的订单实体记录。
/// </summary>
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
    /// <summary>已成功撮合成交的数量</summary>
    public double FilledQuantity => OriginalQuantity - RemainingQuantity;
}

/// <summary>
/// 撮合成交明细（对买卖双方资金与货物的交换凭据）。
/// </summary>
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

/// <summary>挂单下单处理结果（包含吃单产生的即时成交记录与盘口版本）</summary>
public sealed record PlacementResult(
    MarketOrder IncomingOrder,
    IReadOnlyList<MarketFill> Fills,
    long BookVersion);

/// <summary>撤销订单处理结果</summary>
public sealed record CancellationResult(MarketOrder Order, long BookVersion);

/// <summary>盘口单一价格档位聚合（深度信息）</summary>
public sealed record MarketLevel(double Price, double Quantity, int OrderCount);

/// <summary>
/// 订单簿盘口深度快照（包含当前卖档 Asks 与买档 Bids）。
/// </summary>
public sealed record OrderBookSnapshot(
    MarketInstrument Instrument,
    IReadOnlyList<MarketLevel> Asks,
    IReadOnlyList<MarketLevel> Bids,
    long Version,
    DateTimeOffset CapturedAt);

/// <summary>坊市分布式分片元数据</summary>
public sealed record MarketPartition(int PartitionId, int PartitionCount)
{
}

