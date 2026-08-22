// ==============================================================================
// 文件名称: MarketCoordinator.cs
// 主要职责: 玩家资产与分片市场订单簿之间的结算边界与资金/物品冻结协调器。
// 核心逻辑 / 定义内容:
//   1. 挂单/撤单前的玩家灵石与物品资产冻结（Reservation）生命周期管理。
//   2. 订单成交、部分成交或撤单后的资产结算与待领取账本（PendingCredits）累积与领取。
//   3. 市场协调器状态的全量快照导出（ExportState）与崩溃恢复（RestoreState）。
// ==============================================================================

using Domain.Models;
using Domain.Simulation;


namespace Domain.Market;

/// <summary>
/// 玩家资产与订单簿之间的结算边界。
/// 调用方应在所属玩家 Fiber 内串行调用；订单簿本身仍按品种分片，不共享玩家锁。
/// </summary>
public sealed class MarketCoordinator
{
    private readonly MarketEngine _engine;
    private readonly Dictionary<string, Reservation> _reservations = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, PendingCredits> _pendingCredits = [];

    public MarketCoordinator(MarketEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>获取当前活动冻结记录数。</summary>
    public int ActiveReservationCount => _reservations.Count;

    /// <summary>
    /// 导出当前市场协调器的全量状态快照（包括所有活动冻结与待领取账本）。
    /// </summary>
    /// <returns>市场协调器快照对象。</returns>
    public MarketCoordinatorState ExportState()
    {
        var reservations = _reservations.Values
            .OrderBy(reservation => reservation.OrderId, StringComparer.Ordinal)
            .Select(reservation => new MarketReservationState(
                reservation.OrderId,
                reservation.OwnerId,
                reservation.Instrument,
                reservation.Side,
                reservation.RemainingQuantity,
                reservation.ReservedCoins))
            .ToArray();
        var pending = _pendingCredits
            .OrderBy(pair => pair.Key)
            .Select(pair => new MarketPendingCreditsState(
                pair.Key,
                pair.Value.Spirit,
                pair.Value.Items
                    .OrderBy(item => item.Key.Instrument.ItemId, StringComparer.Ordinal)
                    .ThenBy(item => item.Key.Instrument.EnhancementLevel)
                    .Select(item => new MarketPendingItemState(item.Key.Instrument, item.Value))
                    .ToArray()))
            .ToArray();
        return new MarketCoordinatorState(reservations, pending);
    }

    /// <summary>
    /// 恢复玩家资产冻结和待领取结算。恢复前必须先恢复订单簿，以便校验每个冻结对应活动订单。
    /// </summary>
    /// <param name="state">待恢复的市场协调器快照。</param>
    public void RestoreState(MarketCoordinatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _reservations.Clear();
        _pendingCredits.Clear();

        var reservations = state.Reservations ?? throw new InvalidDataException("市场冻结账本不能为空。");
        foreach (var snapshot in reservations)
        {
            if (snapshot is null
                || string.IsNullOrWhiteSpace(snapshot.OrderId)
                || snapshot.OwnerId == Guid.Empty
                || !snapshot.Instrument.IsValid
                || !Enum.IsDefined(snapshot.Side)
                || snapshot.RemainingQuantity < 0
                || snapshot.ReservedCoins < 0
                || !_engine.TryGetOrder(snapshot.OrderId, out var order)
                || order is null
                || order.OwnerId != snapshot.OwnerId
                || order.Instrument != snapshot.Instrument
                || order.Side != snapshot.Side
                || !IsReservationAmountConsistent(snapshot, order)
                || !_reservations.TryAdd(
                    snapshot.OrderId,
                    new Reservation(
                        snapshot.OrderId,
                        snapshot.OwnerId,
                        snapshot.Instrument,
                        snapshot.Side,
                        snapshot.RemainingQuantity,
                        snapshot.ReservedCoins)))
            {
                throw new InvalidDataException("市场冻结账本与活动订单不一致。");
            }
        }

        var pendingCredits = state.PendingCredits
            ?? throw new InvalidDataException("市场待领取账本不能为空。");
        foreach (var pending in pendingCredits)
        {
            if (pending is null
                || pending.OwnerId == Guid.Empty
                || pending.Spirit < 0
                || !_pendingCredits.TryAdd(pending.OwnerId, new PendingCredits()))
            {
                throw new InvalidDataException("市场待领取账本无效或重复。");
            }

            var target = _pendingCredits[pending.OwnerId];
            target.Spirit = pending.Spirit;
            foreach (var item in pending.Items ?? throw new InvalidDataException("待领取物品列表不能为空。"))
            {
                if (item is null
                    || !item.Instrument.IsValid
                    || item.Quantity <= 0
                    || !ItemCatalog.TryGet(item.Instrument.ItemId, out var definition)
                    || !definition.IsTradable)
                {
                    throw new InvalidDataException("市场待领取物品无效。");
                }

                var key = new PendingItemKey(item.Instrument);
                if (!target.Items.TryAdd(key, item.Quantity))
                {
                    throw new InvalidDataException("市场待领取物品重复。");
                }
            }
        }
    }

    /// <summary>
    /// 校验玩家资金/物品充足性、冻结对应资产并向撮合引擎提交订单，最后处理即时成交与待领取入账。
    /// </summary>
    /// <param name="owner">挂单发起玩家。</param>
    /// <param name="request">订单请求数据。</param>
    /// <returns>挂单撮合执行结果。</returns>
    public MarketPlacementResult Place(
        PlayerState owner,
        MarketOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (request is null)
        {
            return MarketPlacementResult.Fail(MarketResultCode.InvalidRequest, "订单参数不能为空。");
        }

        if (request.OwnerId != owner.PlayerId)
        {
            return MarketPlacementResult.Fail(MarketResultCode.OrderForbidden, "订单所有者必须是当前玩家。");
        }

        Claim(owner);
        if (!TryReserve(owner, request, out var reservation, out var errorCode, out var errorMessage))
        {
            return MarketPlacementResult.Fail(errorCode, errorMessage);
        }

        if (!_reservations.TryAdd(request.OrderId, reservation))
        {
            ReleaseReservation(owner, reservation);
            return MarketPlacementResult.Fail(MarketResultCode.DuplicateOrderId, "订单 ID 已存在。");
        }
        MarketPlacementResult result;
        try
        {
            result = _engine.Place(request);
        }
        catch
        {
            _reservations.Remove(request.OrderId);
            ReleaseReservation(owner, reservation);
            throw;
        }
        if (!result.Success)
        {
            ReleaseReservation(owner, reservation);
            _reservations.Remove(request.OrderId);
            return result;
        }

        foreach (var fill in result.Fills)
        {
            ApplyFill(fill);
        }

        CloseCompletedReservations(owner, result);
        Claim(owner);
        TouchInventory(owner);
        return result;
    }

    /// <summary>
    /// 取消指定订单并将未成交的冻结资产返还给玩家。
    /// </summary>
    /// <param name="owner">撤单玩家。</param>
    /// <param name="orderId">订单 ID。</param>
    /// <returns>撤单结果。</returns>
    public MarketCancellationResult Cancel(
        PlayerState owner,
        string orderId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Claim(owner);
        if (!_reservations.TryGetValue(orderId, out var reservation)
            || reservation.OwnerId != owner.PlayerId)
        {
            return MarketCancellationResult.Fail(MarketResultCode.OrderNotFound, "没有找到当前玩家的活动订单。");
        }

        var result = _engine.Cancel(orderId, owner.PlayerId);
        if (!result.Success) return result;
        ReleaseReservation(owner, reservation);
        _reservations.Remove(orderId);
        TouchInventory(owner);
        return result;
    }

    /// <summary>
    /// 提取属于该玩家的全部待领取灵石与物品至玩家背包。
    /// </summary>
    /// <param name="state">玩家聚合状态。</param>
    /// <returns>如果有任何待领取资产入账则返回 true，否则返回 false。</returns>
    public bool Claim(PlayerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!_pendingCredits.Remove(state.PlayerId, out var credits)) return false;

        if (credits.Spirit != 0)
        {
            state.Inventory.Add(ItemIds.Spirit, credits.Spirit);
        }

        foreach (var item in credits.Items)
        {
            state.Inventory.Add(item.Key.Instrument.ItemId, item.Value);
        }

        state.Version++;
        state.DirtySections |= DirtySections.Inventory;
        return true;
    }

    /// <summary>
    /// 处理已过期订单并把未成交的冻结资产转为待领取结算。
    /// 该操作不要求玩家在线，也不触碰玩家 Fiber；玩家下次领取时再写入库存。
    /// </summary>
    /// <param name="now">当前时间戳。</param>
    /// <returns>本次处理的过期订单数量。</returns>
    public int Expire(DateTimeOffset now)
    {
        var expired = _engine.Expire(now);
        foreach (var order in expired)
        {
            if (!_reservations.Remove(order.OrderId, out var reservation)) continue;

            if (reservation.Side == MarketSide.Sell)
            {
                AddPendingItem(reservation.OwnerId, reservation.Instrument, reservation.RemainingQuantity);
            }
            else
            {
                AddPendingSpirit(reservation.OwnerId, reservation.ReservedCoins);
            }
        }

        return expired.Count;
    }


    private bool TryReserve(
        PlayerState owner,
        MarketOrderRequest request,
        out Reservation reservation,
        out MarketResultCode errorCode,
        out string errorMessage)
    {
        reservation = default!;
        if (!Enum.IsDefined(request.Side)
            || request.Price <= 0
            || request.Quantity <= 0
            || !request.Instrument.IsValid)
        {
            errorCode = MarketResultCode.InvalidRequest;
            errorMessage = "订单价格、数量或品种无效。";
            return false;
        }

        if (request.Side == MarketSide.Sell)
        {
            if (owner.Inventory.Get(request.Instrument.ItemId) < request.Quantity)
            {
                errorCode = MarketResultCode.InsufficientItem;
                errorMessage = "挂牌物品数量不足。";
                return false;
            }

            owner.Inventory.Add(request.Instrument.ItemId, -request.Quantity);
            reservation = new Reservation(
                request.OrderId,
                owner.PlayerId,
                request.Instrument,
                request.Side,
                request.Quantity,
                0);
        }
        else
        {
            long maximumAmount;
            try
            {
                maximumAmount = checked(request.Price * request.Quantity);
            }
            catch (OverflowException)
            {
                errorCode = MarketResultCode.OrderTooLarge;
                errorMessage = "订单金额超出支持范围。";
                return false;
            }

            if (owner.Inventory.Get(ItemIds.Spirit) < maximumAmount)
            {
                errorCode = MarketResultCode.InsufficientSpirit;
                errorMessage = "灵石余额不足以覆盖买单上限。";
                return false;
            }

            owner.Inventory.Add(ItemIds.Spirit, -maximumAmount);
            reservation = new Reservation(
                request.OrderId,
                owner.PlayerId,
                request.Instrument,
                request.Side,
                0,
                maximumAmount);
        }

        errorCode = MarketResultCode.Ok;
        errorMessage = string.Empty;
        return true;
    }

    private void ApplyFill(MarketFill fill)
    {
        if (_reservations.TryGetValue(fill.TakerOrderId, out var taker))
        {
            ConsumeReservation(taker, fill);
        }

        if (_reservations.TryGetValue(fill.MakerOrderId, out var maker))
        {
            ConsumeReservation(maker, fill);
        }

        AddPendingSpirit(fill.SellerId, fill.SellerProceeds);
        AddPendingItem(fill.BuyerId, fill.Instrument, fill.Quantity);
    }

    private void ConsumeReservation(Reservation reservation, MarketFill fill)
    {
        if (reservation.Side == MarketSide.Sell)
        {
            reservation.RemainingQuantity = checked(reservation.RemainingQuantity - fill.Quantity);
        }
        else
        {
            reservation.ReservedCoins = checked(reservation.ReservedCoins - fill.GrossAmount);
        }
    }

    private void CloseCompletedReservations(
        PlayerState currentOwner,
        MarketPlacementResult placement)
    {
        if (placement.Order is { Status: MarketOrderStatus.Filled } incoming
            && _reservations.Remove(incoming.OrderId, out var incomingReservation))
        {
            ReleaseReservation(currentOwner, incomingReservation);
        }

        // Maker 的终态由成交量推导，订单簿会在同一撮合边界移除其路由。
        foreach (var fill in placement.Fills)
        {
            if (_reservations.TryGetValue(fill.MakerOrderId, out var maker)
                && (maker.Side == MarketSide.Sell
                    ? maker.RemainingQuantity == 0
                    : maker.ReservedCoins == 0))
            {
                _reservations.Remove(fill.MakerOrderId);
                ReleaseReservation(currentOwner, maker);
            }
        }
    }

    private void ReleaseReservation(PlayerState currentOwner, Reservation reservation)
    {
        if (reservation.Side == MarketSide.Sell)
        {
            AddPendingItem(reservation.OwnerId, reservation.Instrument, reservation.RemainingQuantity);
        }
        else
        {
            AddPendingSpirit(reservation.OwnerId, reservation.ReservedCoins);
        }

        if (reservation.OwnerId == currentOwner.PlayerId)
        {
            Claim(currentOwner);
        }
    }

    private void AddPendingSpirit(Guid ownerId, long amount)
    {
        if (amount == 0) return;
        var credits = GetPending(ownerId);
        credits.Spirit = checked(credits.Spirit + amount);
    }

    private void AddPendingItem(Guid ownerId, MarketInstrument instrument, long amount)
    {
        if (amount == 0) return;
        var credits = GetPending(ownerId);
        var key = new PendingItemKey(instrument);
        credits.Items[key] = checked(credits.Items.GetValueOrDefault(key) + amount);
    }

    private PendingCredits GetPending(Guid ownerId)
    {
        if (!_pendingCredits.TryGetValue(ownerId, out var credits))
        {
            credits = new PendingCredits();
            _pendingCredits.Add(ownerId, credits);
        }

        return credits;
    }

    private static bool IsReservationAmountConsistent(
        MarketReservationState reservation,
        MarketOrderView order)
    {
        return reservation.Side == MarketSide.Sell
            ? reservation.RemainingQuantity == order.RemainingQuantity && reservation.ReservedCoins == 0
            : reservation.RemainingQuantity == 0 && reservation.ReservedCoins >= 0;
    }

    private static void TouchInventory(PlayerState state)
    {
        state.Version++;
        state.DirtySections |= DirtySections.Inventory;
    }

    private sealed class Reservation
    {
        public Reservation(
            string orderId,
            Guid ownerId,
            MarketInstrument instrument,
            MarketSide side,
            long remainingQuantity,
            long reservedCoins)
        {
            OrderId = orderId;
            OwnerId = ownerId;
            Instrument = instrument;
            Side = side;
            RemainingQuantity = remainingQuantity;
            ReservedCoins = reservedCoins;
        }

        public string OrderId { get; }
        public Guid OwnerId { get; }
        public MarketInstrument Instrument { get; }
        public MarketSide Side { get; }
        public long RemainingQuantity { get; set; }
        public long ReservedCoins { get; set; }
    }

    private sealed class PendingCredits
    {
        public long Spirit { get; set; }
        public Dictionary<PendingItemKey, long> Items { get; } = [];
    }

    private readonly record struct PendingItemKey(MarketInstrument Instrument);
}
