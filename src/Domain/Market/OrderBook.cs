namespace Domain.Market;

/// <summary>
/// 单交易标的物（Instrument）内存订单簿。
/// 核心原则：
/// 1. 采用“价格优先、时间优先”（Price-Time Priority）严格确定性撮合；
/// 2. 数据库 PostgreSQL 作为权威账本落盘，本类作为单 Fiber Actor 内部的快速撮合与盘口投影；
/// 3. 禁止在领域层持有连接或直接扣减外部数据库。
/// 结构说明：除 _orders 主字典外维护四个派生索引——
/// 卖档/买档（价格, Sequence）有序索引用于撮合候选只取交叉队首，
/// 到期（ExpiresAt, Sequence）有序索引用于过期结算只处理到期队首，
/// 属主（PlayerId → 活动订单集合）索引用于「同一玩家同一标的至多一笔活动挂单」校验；
/// 索引由 WriteOrder 统一维护，Restore/Undo 后整体重建，纯内存结构不引入外部依赖。
/// </summary>
public sealed class OrderBook
{
    private readonly Dictionary<OrderId, MarketOrder> _orders = [];
    /// <summary>卖档索引：价格升序、同价 Sequence 升序，买单从队首起吃。</summary>
    private readonly SortedDictionary<(double Price, long Sequence), OrderId> _askIndex = new();
    /// <summary>买档索引：价格降序、同价 Sequence 升序，卖单从队首起吃。</summary>
    private readonly SortedDictionary<(double Price, long Sequence), OrderId> _bidIndex = new(BidPriceComparer.Instance);
    /// <summary>到期索引：ExpiresAt 升序、同刻 Sequence 升序，仅登记设定了到期时刻的活动订单。</summary>
    private readonly SortedDictionary<(DateTimeOffset ExpiresAt, long Sequence), OrderId> _expiryIndex = new();
    /// <summary>属主索引：玩家 → 该玩家在本簿的活动订单，用于「每标的至多一笔活动挂单」校验。</summary>
    private readonly Dictionary<PlayerId, HashSet<OrderId>> _ownerIndex = [];
    private long _sequence;
    private long _version;

    /// <summary>为指定交易标的创建空订单簿。</summary>
    /// <param name="instrument">挂钩的交易标的。</param>
    public OrderBook(MarketInstrument instrument)
    {
        if (!instrument.ItemId.IsValid || instrument.Variant is null || instrument.Variant.Length > 128)
            throw new DomainException(DomainErrorCode.InvalidArgument, "市场品种无效。");
        Instrument = instrument;
    }

    /// <summary>当前订单簿所挂钩的交易标的</summary>
    public MarketInstrument Instrument { get; }
    /// <summary>订单簿单调演进版本号</summary>
    public long Version => _version;
    /// <summary>当前最低活动卖价（卖一档价）；索引只登记活动挂牌单，无卖单返回 null。</summary>
    public double? BestAskPrice
    {
        get
        {
            foreach (var entry in _askIndex) return entry.Key.Price;
            return null;
        }
    }
    /// <summary>当前全部处于活跃挂牌排队状态的订单列表</summary>
    public IReadOnlyCollection<MarketOrder> ActiveOrders
    {
        get
        {
            var active = new List<MarketOrder>(_orders.Count);
            foreach (var order in _orders.Values)
            {
                if (IsActive(order)) active.Add(order);
            }
            return active;
        }
    }
    /// <summary>按订单 ID 尝试查找订单实体</summary>
    public bool TryGet(OrderId orderId, out MarketOrder order) => _orders.TryGetValue(orderId, out order!);

    /// <summary>从数据库当前活动订单重建内存投影；只导入，不触发撮合或资产变更。</summary>
    public void Restore(IEnumerable<MarketOrder> orders, long version = 0)
    {
        ArgumentNullException.ThrowIfNull(orders);
        if (version < 0) throw new DomainException(DomainErrorCode.InvalidArgument, "盘口版本不能为负数。");
        var imported = orders as IList<MarketOrder> ?? orders.ToList();
        var sorted = new MarketOrder[imported.Count];
        for (var i = 0; i < imported.Count; i++) sorted[i] = imported[i];
        Array.Sort(sorted, static (a, b) => a.Sequence.CompareTo(b.Sequence));
        var ids = new HashSet<OrderId>();
        var sequences = new HashSet<long>();
        // 两阶段提交：先全量校验再整体替换，校验失败时原盘口不被破坏。
        var staged = new List<MarketOrder>(sorted.Length);
        var maxSequence = 0L;
        foreach (var order in sorted)
        {
            if (order.Instrument != Instrument || order.OrderId.IsEmpty || order.OwnerId.IsEmpty ||
                !Enum.IsDefined(order.Side) || !Enum.IsDefined(order.Status) ||
                !double.IsFinite(order.Price) || order.Price <= 0 || !double.IsFinite(order.OriginalQuantity) || order.OriginalQuantity <= 0 ||
                !double.IsFinite(order.RemainingQuantity) || order.RemainingQuantity <= 0 || order.RemainingQuantity > order.OriginalQuantity ||
                order.Sequence <= 0 || !ids.Add(order.OrderId) || !sequences.Add(order.Sequence) || !IsActive(order))
                throw new DomainException(DomainErrorCode.InvalidArgument, "活动订单快照无效。");
            staged.Add(order);
            maxSequence = Math.Max(maxSequence, order.Sequence);
        }
        if (version < maxSequence) throw new DomainException(DomainErrorCode.InvalidArgument, "盘口版本不能小于订单序列。");
        _orders.Clear();
        foreach (var order in staged) _orders.Add(order.OrderId, order);
        _sequence = maxSequence;
        _version = version;
        RebuildIndexes();
    }

    /// <summary>
    /// 提交挂单并立即执行撮合（Taker 吃单）。
    /// 若有交叉价格对手单，按价格优先、时间优先依次成交；未完全成交部分作为 Maker 保留在货架上。
    /// </summary>
    public PlacementResult Place(PlaceOrderRequest request) => Place(request, null);

    /// <summary>
    /// 就地提交挂单并撮合；传入 journal 时登记撤销日志，供持久化失败时 <see cref="Undo"/> 回滚。
    /// 撮合次序、成交价量与不带日志版本逐位一致，日志本身不改变结算结果。
    /// </summary>
    /// <param name="request">挂单请求。</param>
    /// <param name="journal">可选撤销日志；为 null 时与无日志挂单完全等价。</param>
    public PlacementResult Place(PlaceOrderRequest request, OrderBookJournal? journal)
    {
        // 起始水位必须在任何校验/写入前捕获：调用方收到异常后按日志回滚时恒为安全无操作。
        journal?.Capture(_version, _sequence);
        if (request.OrderId.IsEmpty || request.OwnerId.IsEmpty || !request.IdempotencyKey.IsValid ||
            !double.IsFinite(request.Price) || request.Price <= 0 ||
            !double.IsFinite(request.Quantity) || request.Quantity <= 0 || request.Quantity != Math.Floor(request.Quantity))
            throw new DomainException(DomainErrorCode.InvalidArgument, "订单参数无效。");
        if (!Enum.IsDefined(request.Side))
            throw new DomainException(DomainErrorCode.InvalidArgument, "订单方向无效。");
        if (request.Instrument != Instrument)
            throw new DomainException(DomainErrorCode.InvalidArgument, "订单品种与订单簿不一致。");
        // 每标的单笔约束：同一玩家在本簿只允许一笔活动挂单，双向共存与同向多单都被拒绝。
        // 撤单/成交/到期会把订单移出属主索引，因此撤销后可立即换向重挂。
        if (ActiveOrderOf(request.OwnerId) is { } existing)
            throw new DomainException(DomainErrorCode.OwnerOrderExists,
                existing.Side == MarketSide.Sell ? "该标的已有在售挂单，请先撤销后再操作。" : "该标的已有求购挂单，请先撤销后再操作。");

        var incoming = new MarketOrder(
            request.OrderId,
            request.OwnerId,
            request.Instrument,
            request.Side,
            request.Price,
            request.Quantity,
            request.Quantity,
            checked(++_sequence),
            request.CreatedAt.ToUniversalTime(),
            request.CreatedAt.ToUniversalTime().AddDays(30),
            MarketOrderStatus.Active);
        // 订单标识重复时拒绝；与原 TryAdd 行为一致——序号在冲突判定前已消耗，属于既定语义。
        if (_orders.ContainsKey(request.OrderId))
            throw new DomainException(DomainErrorCode.Duplicate, "订单标识已存在。");
        WriteOrder(request.OrderId, incoming, journal);

        var fills = new List<MarketFill>();
        var candidates = MatchingCandidates(incoming);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (incoming.RemainingQuantity <= 0) break;
            var maker = candidates[i];
            // 自成交防护：同一玩家的订单禁止撮合互刷
            if (maker.OwnerId == incoming.OwnerId) continue;
            var quantity = Math.Min(incoming.RemainingQuantity, maker.RemainingQuantity);
            var price = maker.Price;
            var buyer = incoming.Side == MarketSide.Buy ? incoming : maker;
            var seller = incoming.Side == MarketSide.Sell ? incoming : maker;
            fills.Add(new MarketFill(
                TransactionId.New(),
                buyer.OrderId,
                seller.OrderId,
                buyer.OwnerId,
                seller.OwnerId,
                Instrument,
                price,
                quantity,
                request.CreatedAt.ToUniversalTime()));
            incoming = incoming with
            {
                RemainingQuantity = Math.Max(0, incoming.RemainingQuantity - quantity),
                Status = incoming.RemainingQuantity - quantity <= 1e-12 ? MarketOrderStatus.Filled : MarketOrderStatus.PartiallyFilled
            };
            maker = maker with
            {
                RemainingQuantity = Math.Max(0, maker.RemainingQuantity - quantity),
                Status = maker.RemainingQuantity - quantity <= 1e-12 ? MarketOrderStatus.Filled : MarketOrderStatus.PartiallyFilled
            };
            WriteOrder(incoming.OrderId, incoming, journal);
            WriteOrder(maker.OrderId, maker, journal);
        }

        WriteOrder(request.OrderId, incoming, journal);
        if (incoming.RemainingQuantity <= 1e-12)
        {
            incoming = incoming with { RemainingQuantity = 0, Status = MarketOrderStatus.Filled };
            WriteOrder(request.OrderId, incoming, journal);
        }
        _version = checked(_version + 1);
        return new PlacementResult(_orders[request.OrderId], fills, _version);
    }

    /// <summary>
    /// 主动撤销未完全成交的挂单。
    /// </summary>
    public CancellationResult Cancel(OrderId orderId, PlayerId ownerId) => Cancel(orderId, ownerId, null);

    /// <summary>
    /// 就地撤销未完全成交的挂单；传入 journal 时登记撤销日志，供持久化失败时 <see cref="Undo"/> 回滚。
    /// </summary>
    /// <param name="orderId">待撤销订单标识。</param>
    /// <param name="ownerId">订单属主玩家。</param>
    /// <param name="journal">可选撤销日志；为 null 时与无日志撤单完全等价。</param>
    public CancellationResult Cancel(OrderId orderId, PlayerId ownerId, OrderBookJournal? journal)
    {
        journal?.Capture(_version, _sequence);
        if (!_orders.TryGetValue(orderId, out var order))
            throw new DomainException(DomainErrorCode.NotFound, "订单不存在。");
        if (order.OwnerId != ownerId)
            throw new DomainException(DomainErrorCode.Conflict, "只能撤销自己的订单。");
        if (!IsActive(order))
        {
            if (order.Status == MarketOrderStatus.Cancelled) return new CancellationResult(order, _version);
            throw new DomainException(DomainErrorCode.InvalidTransition, "订单已处于终态。");
        }
        order = order with { Status = MarketOrderStatus.Cancelled };
        WriteOrder(orderId, order, journal);
        _version = checked(_version + 1);
        return new CancellationResult(order, _version);
    }

    /// <summary>
    /// 回滚一次就地变更：按日志逆序回放订单旧值，并恢复版本号与撮合序号。
    /// 仅用于持久化边界失败的兜底撤销；索引随后按恢复结果整体重建。
    /// </summary>
    /// <param name="journal">Place/Cancel 期间登记的撤销日志。</param>
    public void Undo(OrderBookJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        for (var i = journal.Entries.Count - 1; i >= 0; i--)
        {
            var (orderId, previous) = journal.Entries[i];
            if (previous is null) _orders.Remove(orderId);
            else _orders[orderId] = previous;
        }
        _version = journal.StartVersion;
        _sequence = journal.StartSequence;
        RebuildIndexes();
    }

    /// <summary>
    /// 按到期索引返回当前已到期且仍挂牌的订单（Sequence 升序），不修改簿内状态。
    /// 未到期簿的首个索引项即提前终止枚举，不再全簿扫描。
    /// </summary>
    /// <param name="now">到期判定时刻。</param>
    public IReadOnlyList<MarketOrder> GetExpiredOrders(DateTimeOffset now) => CollectDueOrders(now.ToUniversalTime());

    /// <summary>
    /// 定期下架超过截止时间的过期挂单。
    /// </summary>
    public IReadOnlyList<MarketOrder> Expire(DateTimeOffset now)
    {
        var expired = CollectDueOrders(now.ToUniversalTime());
        if (expired.Count == 0) return [];
        for (var i = 0; i < expired.Count; i++)
        {
            var order = expired[i];
            WriteOrder(order.OrderId, order with { Status = MarketOrderStatus.Expired }, null);
        }
        _version = checked(_version + 1);
        return expired;
    }

    /// <summary>
    /// 生成买卖盘指定档位深度的聚合快照（Asks 升序、Bids 降序）。
    /// 采用单趟遍历字典聚合与数组排序，消除多重 LINQ GroupBy 产生的大量闭包分配。
    /// </summary>
    public OrderBookSnapshot Snapshot(int depth = 20, DateTimeOffset? now = null)
    {
        if (depth is < 1 or > 200) throw new DomainException(DomainErrorCode.InvalidArgument, "盘口深度必须在 1 到 200 之间。");
        var askMap = new Dictionary<double, (double Quantity, int Count)>();
        var bidMap = new Dictionary<double, (double Quantity, int Count)>();

        foreach (var order in _orders.Values)
        {
            if (!IsActive(order)) continue;
            var targetMap = order.Side == MarketSide.Sell ? askMap : bidMap;
            if (targetMap.TryGetValue(order.Price, out var current))
            {
                targetMap[order.Price] = (current.Quantity + order.RemainingQuantity, current.Count + 1);
            }
            else
            {
                targetMap[order.Price] = (order.RemainingQuantity, 1);
            }
        }

        var askPrices = new double[askMap.Count];
        askMap.Keys.CopyTo(askPrices, 0);
        Array.Sort(askPrices);
        var askCount = Math.Min(depth, askPrices.Length);
        var asks = new MarketLevel[askCount];
        for (var i = 0; i < askCount; i++)
        {
            var price = askPrices[i];
            var (qty, count) = askMap[price];
            asks[i] = new MarketLevel(price, qty, count);
        }

        var bidPrices = new double[bidMap.Count];
        bidMap.Keys.CopyTo(bidPrices, 0);
        Array.Sort(bidPrices);
        Array.Reverse(bidPrices);
        var bidCount = Math.Min(depth, bidPrices.Length);
        var bids = new MarketLevel[bidCount];
        for (var i = 0; i < bidCount; i++)
        {
            var price = bidPrices[i];
            var (qty, count) = bidMap[price];
            bids[i] = new MarketLevel(price, qty, count);
        }

        return new OrderBookSnapshot(Instrument, asks, bids, _version, (now ?? DateTimeOffset.UtcNow).ToUniversalTime());
    }

    /// <summary>
    /// 查询指定玩家名下的活动挂单列表（按创建时序逆序排列）。
    /// 走属主索引直取，不对全簿扫描；历史库残留的同标的多笔活动单也一并如实返回。
    /// </summary>
    public IReadOnlyList<MarketOrder> OrdersByOwner(PlayerId ownerId, int limit = 100)
    {
        if (ownerId.IsEmpty) throw new DomainException(DomainErrorCode.InvalidArgument, "玩家标识不能为空。");
        if (limit is < 1 or > 1000) throw new DomainException(DomainErrorCode.InvalidArgument, "订单查询上限无效。");
        if (!_ownerIndex.TryGetValue(ownerId, out var owned) || owned.Count == 0) return [];
        var list = new List<MarketOrder>(owned.Count);
        foreach (var orderId in owned)
        {
            if (_orders.TryGetValue(orderId, out var order) && IsActive(order))
            {
                list.Add(order);
            }
        }
        list.Sort(static (a, b) => b.Sequence.CompareTo(a.Sequence));
        if (list.Count > limit) list.RemoveRange(limit, list.Count - limit);
        return list;
    }

    /// <summary>
    /// 导出当前全部活动排队订单（供持久化刷盘或校验）。
    /// </summary>
    public IReadOnlyList<MarketOrder> ExportActiveOrders()
    {
        var active = new List<MarketOrder>(_orders.Count);
        foreach (var order in _orders.Values)
        {
            if (IsActive(order)) active.Add(order);
        }
        active.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
        return active;
    }

    /// <summary>
    /// 到期索引前缀枚举：只走到期队首，命中后按 Sequence 排序返回。
    /// 索引项与订单状态可能因撤销日志重建而滞后，命中前仍以 IsActive 复核。
    /// </summary>
    private List<MarketOrder> CollectDueOrders(DateTimeOffset timestampUtc)
    {
        var due = new List<MarketOrder>();
        foreach (var entry in _expiryIndex)
        {
            if (entry.Key.ExpiresAt > timestampUtc) break;
            if (_orders.TryGetValue(entry.Value, out var order) && IsActive(order))
                due.Add(order);
        }
        if (due.Count > 1) due.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
        return due;
    }

    /// <summary>
    /// 按方向价格索引取撮合候选：买单从最低卖价起、卖单从最高买价起按 (价格, Sequence) 枚举，
    /// 首个不交叉价位即终止；产出次序与原「价格优先、Sequence 优先」全扫+排序逐位一致。
    /// </summary>
    private List<MarketOrder> MatchingCandidates(MarketOrder incoming)
    {
        var index = incoming.Side == MarketSide.Buy ? _askIndex : _bidIndex;
        var candidates = new List<MarketOrder>();
        foreach (var entry in index)
        {
            // 买价低于当前卖档价 / 卖价高于当前买档价时，后续档位不可能再交叉。
            if (incoming.Side == MarketSide.Buy ? entry.Key.Price > incoming.Price : entry.Key.Price < incoming.Price) break;
            if (_orders.TryGetValue(entry.Value, out var order)
                && IsActive(order)
                && (order.ExpiresAt is null || order.ExpiresAt > incoming.CreatedAt))
                candidates.Add(order);
        }
        return candidates;
    }

    /// <summary>
    /// 订单写入唯一入口：登记撤销日志、摘除旧索引、写入字典、补登新索引。
    /// 终态订单由 AddIndexes 内部跳过，索引只保留活动挂牌单。
    /// </summary>
    private void WriteOrder(OrderId orderId, MarketOrder order, OrderBookJournal? journal)
    {
        _orders.TryGetValue(orderId, out var previous);
        journal?.Record(orderId, previous);
        if (previous is not null) RemoveIndexes(previous);
        _orders[orderId] = order;
        AddIndexes(order);
    }

    /// <summary>按订单当前状态补登各派生索引；非挂牌状态不登记。</summary>
    private void AddIndexes(MarketOrder order)
    {
        if (!IsActive(order)) return;
        if (order.ExpiresAt is { } expiresAt)
            _expiryIndex[(expiresAt, order.Sequence)] = order.OrderId;
        var book = order.Side == MarketSide.Sell ? _askIndex : _bidIndex;
        book[(order.Price, order.Sequence)] = order.OrderId;
        if (!_ownerIndex.TryGetValue(order.OwnerId, out var owned))
            _ownerIndex[order.OwnerId] = owned = [];
        owned.Add(order.OrderId);
    }

    /// <summary>摘除订单在派生索引中的登记项；未登记的项按缺失处理（幂等）。</summary>
    private void RemoveIndexes(MarketOrder order)
    {
        if (order.ExpiresAt is { } expiresAt)
            _expiryIndex.Remove((expiresAt, order.Sequence));
        var book = order.Side == MarketSide.Sell ? _askIndex : _bidIndex;
        book.Remove((order.Price, order.Sequence));
        if (_ownerIndex.TryGetValue(order.OwnerId, out var owned) && owned.Remove(order.OrderId) && owned.Count == 0)
            _ownerIndex.Remove(order.OwnerId);
    }

    /// <summary>按当前订单字典整体重建派生索引；用于 Restore/Undo 这类非增量路径。</summary>
    private void RebuildIndexes()
    {
        _askIndex.Clear();
        _bidIndex.Clear();
        _expiryIndex.Clear();
        _ownerIndex.Clear();
        foreach (var order in _orders.Values) AddIndexes(order);
    }

    /// <summary>
    /// 取该玩家在本簿的活动挂单（每标的至多一笔，命中即唯一）；无活动单返回 null。
    /// 与 <see cref="OrdersByOwner"/> 的区别：只查单簿、直接命中属主索引，不做跨簿聚合与排序。
    /// </summary>
    /// <param name="ownerId">订单属主玩家。</param>
    public MarketOrder? ActiveOrderOf(PlayerId ownerId)
    {
        if (!_ownerIndex.TryGetValue(ownerId, out var owned) || owned.Count == 0) return null;
        foreach (var orderId in owned)
            if (_orders.TryGetValue(orderId, out var order) && IsActive(order)) return order;
        return null;
    }

    /// <summary>判定订单是否属于有效挂牌中（未撤销未完成且剩余数量大于容差）</summary>
    private static bool IsActive(MarketOrder order) =>
        (order.Status is MarketOrderStatus.Active or MarketOrderStatus.PartiallyFilled)
        && order.RemainingQuantity > 1e-12;

    /// <summary>买档索引比较器：价格降序优先、同价 Sequence 升序，与卖单吃单枚举序一致。</summary>
    private sealed class BidPriceComparer : IComparer<(double Price, long Sequence)>
    {
        public static readonly BidPriceComparer Instance = new();
        public int Compare((double Price, long Sequence) x, (double Price, long Sequence) y)
        {
            var cmp = y.Price.CompareTo(x.Price);
            return cmp != 0 ? cmp : x.Sequence.CompareTo(y.Sequence);
        }
    }
}

/// <summary>
/// 订单簿就地变更的撤销日志：每次订单字典首次写入前登记旧值（原先不存在记 null），
/// 并捕获变更前的版本号与撮合序号；持久化失败时按逆序回放即可恢复变更前状态。
/// </summary>
public sealed class OrderBookJournal
{
    private readonly List<KeyValuePair<OrderId, MarketOrder?>> _entries = new(8);

    /// <summary>变更前的盘口版本号。</summary>
    internal long StartVersion { get; private set; }
    /// <summary>变更前的撮合序号。</summary>
    internal long StartSequence { get; private set; }
    /// <summary>按写入顺序登记的原始记录（键为订单标识，值为写入前订单或 null）。</summary>
    internal IReadOnlyList<KeyValuePair<OrderId, MarketOrder?>> Entries => _entries;

    /// <summary>由 OrderBook 在首次写入前捕获起始水位。</summary>
    internal void Capture(long version, long sequence)
    {
        StartVersion = version;
        StartSequence = sequence;
    }

    /// <summary>由 OrderBook.WriteOrder 在每次覆盖订单字典前登记旧值。</summary>
    internal void Record(OrderId orderId, MarketOrder? previous) => _entries.Add(new(orderId, previous));
}
