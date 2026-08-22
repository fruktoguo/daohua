namespace DaoHua.Domain.Models;

/// <summary>
/// 交易委托买卖方向。
/// </summary>
public enum DaoHuaMarketSide
{
    /// <summary>买入订单（冻结灵石）。</summary>
    Buy = 0,
    /// <summary>卖出订单（冻结物品）。</summary>
    Sell = 1
}

/// <summary>
/// 市场订单生命周期状态。
/// </summary>
public enum DaoHuaMarketOrderStatus
{
    /// <summary>已挂单未成交。</summary>
    Active = 0,
    /// <summary>部分已成交，剩余部分仍在盘口。</summary>
    PartiallyFilled = 1,
    /// <summary>已全部成交闭环。</summary>
    Filled = 2,
    /// <summary>已主动撤单。</summary>
    Cancelled = 3,
    /// <summary>已超时过期。</summary>
    Expired = 4
}

/// <summary>
/// 市场交易品种键（由物品 ID 与装备强化等级唯一确定）。
/// </summary>
/// <param name="ItemId">物品 ID。</param>
/// <param name="EnhancementLevel">装备强化等级（默认 0）。</param>
public readonly record struct DaoHuaMarketInstrument(string ItemId, int EnhancementLevel = 0)
{
    /// <summary>校验品种有效性（物品 ID 非空且强化等级非负）。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(ItemId) && EnhancementLevel >= 0;
}

/// <summary>
/// 提交至市场撮合引擎的新订单委托请求。
/// </summary>
public sealed record DaoHuaMarketOrderRequest(
    string OrderId,
    Guid OwnerId,
    DaoHuaMarketInstrument Instrument,
    DaoHuaMarketSide Side,
    long Price,
    long Quantity,
    DateTimeOffset? CreatedAt = null);

/// <summary>
/// 市场订单对外只读明细视图。
/// </summary>
public sealed record DaoHuaMarketOrderView(
    string OrderId,
    Guid OwnerId,
    DaoHuaMarketInstrument Instrument,
    DaoHuaMarketSide Side,
    long Price,
    long OriginalQuantity,
    long RemainingQuantity,
    long FilledQuantity,
    DaoHuaMarketOrderStatus Status,
    long Sequence,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset ExpiresAt = default);

/// <summary>
/// 撮合成功的单笔成交记录明细。
/// </summary>
public sealed record DaoHuaMarketFill(
    string TakerOrderId,
    string MakerOrderId,
    Guid BuyerId,
    Guid SellerId,
    DaoHuaMarketInstrument Instrument,
    long Price,
    long Quantity,
    long GrossAmount,
    long SellerProceeds,
    long SellerTax);

/// <summary>
/// 挂单撮合执行结果记录。
/// </summary>
public sealed record DaoHuaMarketPlacementResult(
    bool Success,
    string Code,
    string Message,
    DaoHuaMarketOrderView? Order,
    IReadOnlyList<DaoHuaMarketFill> Fills)
{
    /// <summary>构建失败挂单结果。</summary>
    public static DaoHuaMarketPlacementResult Fail(string code, string message) =>
        new(false, code, message, null, []);
}

/// <summary>
/// 撤单执行结果记录。
/// </summary>
public sealed record DaoHuaMarketCancellationResult(
    bool Success,
    string Code,
    string Message,
    DaoHuaMarketOrderView? Order)
{
    /// <summary>构建失败撤单结果。</summary>
    public static DaoHuaMarketCancellationResult Fail(string code, string message) =>
        new(false, code, message, null);
}

/// <summary>
/// 盘口单价档位聚合项（包含该价位下的总委托量与订单总笔数）。
/// </summary>
public sealed record DaoHuaMarketLevel(long Price, long Quantity, int OrderCount);

/// <summary>
/// 某交易品种当前的买卖盘口深度快照。
/// </summary>
public sealed record DaoHuaMarketBookSnapshot(
    DaoHuaMarketInstrument Instrument,
    IReadOnlyList<DaoHuaMarketLevel> Asks,
    IReadOnlyList<DaoHuaMarketLevel> Bids,
    long ActiveOrderCount,
    long Sequence);

/// <summary>
/// 盘口只读版本。它只包含判断条件响应所需的计数，不复制盘口档位，适合高频轮询。
/// </summary>
public readonly record struct DaoHuaMarketBookVersion(
    long Sequence,
    long ActiveOrderCount,
    long PlacementCount,
    long MatchCount,
    long CancelCount,
    long RejectedCount,
    long TradedQuantity,
    long TradedGrossAmount);

/// <summary>
/// 撮合引擎全局实时运行统计指标。
/// </summary>
public sealed record DaoHuaMarketEngineMetrics(
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
public sealed record DaoHuaMarketOrderSnapshot(
    string OrderId,
    Guid OwnerId,
    DaoHuaMarketInstrument Instrument,
    DaoHuaMarketSide Side,
    long Price,
    long OriginalQuantity,
    long RemainingQuantity,
    DaoHuaMarketOrderStatus Status,
    long Sequence,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset ExpiresAt = default);

/// <summary>
/// 单品种订单簿快照，包含恢复 FIFO 所需的序列和累计指标。
/// </summary>
public sealed record DaoHuaMarketBookState(
    DaoHuaMarketInstrument Instrument,
    IReadOnlyList<DaoHuaMarketOrderSnapshot> ActiveOrders,
    long Sequence,
    long PlacementCount,
    long MatchCount,
    long CancelCount,
    long RejectedCount,
    long TradedQuantity,
    long TradedGrossAmount);

/// <summary>
/// 撮合引擎全量持久化快照。
/// </summary>
public sealed record DaoHuaMarketEngineState(IReadOnlyList<DaoHuaMarketBookState> Books);

/// <summary>
/// 市场冻结资产记录快照。
/// </summary>
public sealed record DaoHuaMarketReservationState(
    string OrderId,
    Guid OwnerId,
    DaoHuaMarketInstrument Instrument,
    DaoHuaMarketSide Side,
    long RemainingQuantity,
    long ReservedCoins);

/// <summary>
/// 待领取物品记录快照。
/// </summary>
public sealed record DaoHuaMarketPendingItemState(
    DaoHuaMarketInstrument Instrument,
    long Quantity);

/// <summary>
/// 玩家待领取资产汇总快照（待领灵石 + 待领物品列表）。
/// </summary>
public sealed record DaoHuaMarketPendingCreditsState(
    Guid OwnerId,
    long Spirit,
    IReadOnlyList<DaoHuaMarketPendingItemState> Items);

/// <summary>
/// 市场协调器账本快照。
/// </summary>
public sealed record DaoHuaMarketCoordinatorState(
    IReadOnlyList<DaoHuaMarketReservationState> Reservations,
    IReadOnlyList<DaoHuaMarketPendingCreditsState> PendingCredits);

/// <summary>
/// 市场账本快照。Revision 用于并发写回时丢弃旧快照，SchemaVersion 便于未来迁移。
/// </summary>
public sealed record DaoHuaMarketLedgerState(
    int SchemaVersion,
    long Revision,
    DaoHuaMarketEngineState Engine,
    DaoHuaMarketCoordinatorState Coordinator);

/// <summary>
/// 坊市交易税率策略类。
/// </summary>
public static class DaoHuaMarketTaxPolicy
{
    /// <summary>标准卖家交易税率（2%）。</summary>
    public const double StandardSellerTaxRate = 0.02;
    /// <summary>特殊储物袋类卖家税率（18%）。</summary>
    public const double SpecialBagSellerTaxRate = 0.18;

    /// <summary>
    /// 根据物品 ID 获取卖家需承担的交易税率。
    /// </summary>
    /// <param name="itemId">物品 ID。</param>
    /// <returns>税率比例。</returns>
    public static double GetSellerTaxRate(string itemId)
    {
        // 预留特殊袋类物品的税率；当前修仙目录只开放普通物品。
        return itemId.Contains("bag", StringComparison.OrdinalIgnoreCase)
            ? SpecialBagSellerTaxRate
            : StandardSellerTaxRate;
    }

    /// <summary>
    /// 根据总成交金额和税率计算扣除交易税后卖家实际到手灵石收益。
    /// </summary>
    /// <param name="grossAmount">成交总金额。</param>
    /// <param name="taxRate">税率比例。</param>
    /// <returns>卖家实得收益（向下取整）。</returns>
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
