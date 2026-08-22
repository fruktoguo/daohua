// ==============================================================================
// 文件名称: DaoHuaMarketMapper.cs
// 主要职责: 市场领域模型与网络传输协议 DTO 之间的投影转换映射
// 核心逻辑 / 定义内容:
//   1. 将撮合引擎订单簿深度快照投影为 RESTful API 使用的 DaoHuaMarketBookDto。
//   2. 映射市场运行监控指标（DaoHuaMarketEngineMetrics -> DaoHuaMarketMetricsDto）。
//   3. 映射玩家市场订单视图与挂单、撤单、领奖等操作的响应结果 DTO。
// ==============================================================================

using DaoHua.Domain.Market;
using DaoHua.Domain.Models;
using DaoHua.Protocol;

namespace DaoHua.Server.Runtime;

/// <summary>
/// 市场领域对象与协议 DTO 转换映射器 (DaoHuaMarketMapper)。
/// <para>
/// 职责：
/// 1. 将撮合引擎的订单簿深度快照 (<see cref="DaoHuaMarketBookSnapshot"/>) 投影为 RESTful API 使用的 <see cref="DaoHuaMarketBookDto"/>。
/// 2. 映射市场运行监控指标 (<see cref="DaoHuaMarketEngineMetrics"/>)。
/// 3. 映射玩家订单详情 (<see cref="DaoHuaMarketOrderView"/>) 与挂单/撤单操作响应结果 (<see cref="DaoHuaMarketCommandOutcome"/>)。
/// </para>
/// </summary>
public static class DaoHuaMarketMapper
{
    /// <summary>
    /// 将订单簿深度快照投影为协议 DTO。
    /// </summary>
    public static DaoHuaMarketBookDto ToDto(DaoHuaMarketBookSnapshot snapshot)
    {
        return new DaoHuaMarketBookDto(
            snapshot.Instrument.ItemId,
            snapshot.Instrument.EnhancementLevel,
            snapshot.Asks.Select(ToLevel).ToArray(),
            snapshot.Bids.Select(ToLevel).ToArray(),
            snapshot.ActiveOrderCount,
            snapshot.Sequence);
    }

    /// <summary>
    /// 将撮合引擎监控指标投影为协议 DTO。
    /// </summary>
    public static DaoHuaMarketMetricsDto ToDto(DaoHuaMarketEngineMetrics metrics)
    {
        return new DaoHuaMarketMetricsDto(
            metrics.ShardCount,
            metrics.ActiveOrderCount,
            metrics.PlacementCount,
            metrics.MatchCount,
            metrics.CancelCount,
            metrics.RejectedCount,
            metrics.TradedQuantity,
            metrics.TradedGrossAmount);
    }

    /// <summary>
    /// 将市场订单视图投影为订单 DTO。
    /// </summary>
    public static DaoHuaMarketOrderDto ToDto(DaoHuaMarketOrderView order)
    {
        return new DaoHuaMarketOrderDto(
            order.OrderId,
            order.Instrument.ItemId,
            order.Instrument.EnhancementLevel,
            order.Side == DaoHuaMarketSide.Buy ? "buy" : "sell",
            order.Price,
            order.OriginalQuantity,
            order.RemainingQuantity,
            order.FilledQuantity,
            order.Status switch
            {
                DaoHuaMarketOrderStatus.Active => "active",
                DaoHuaMarketOrderStatus.PartiallyFilled => "partially-filled",
                DaoHuaMarketOrderStatus.Filled => "filled",
                DaoHuaMarketOrderStatus.Cancelled => "cancelled",
                DaoHuaMarketOrderStatus.Expired => "expired",
                _ => "unknown"
            },
            order.Sequence,
            order.CreatedAt,
            order.ExpiresAt);
    }

    /// <summary>
    /// 将市场挂单/撤单/提取产出结果投影为统一命令响应 DTO。
    /// </summary>
    public static DaoHuaMarketCommandResponseDto ToDto(DaoHuaMarketCommandOutcome outcome)
    {
        return new DaoHuaMarketCommandResponseDto(
            outcome.Success,
            outcome.Code,
            outcome.Message,
            outcome.Order is null ? null : ToDto(outcome.Order),
            outcome.Fills.Select(fill => new DaoHuaMarketFillDto(
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

    private static DaoHuaMarketLevelDto ToLevel(DaoHuaMarketLevel level) =>
        new(level.Price, level.Quantity, level.OrderCount);
}
