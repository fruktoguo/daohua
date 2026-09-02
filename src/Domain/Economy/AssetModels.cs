namespace Domain.Economy;

/// <summary>
/// 资产唯一键，由基础道具 ID 与变体后缀（如品质、锻造词缀）复合而成。
/// </summary>
public readonly record struct AssetKey(ItemId ItemId, string Variant = "")
{
    public override string ToString() => string.IsNullOrEmpty(Variant) ? ItemId.ToString() : $"{ItemId}:{Variant}";
}

/// <summary>
/// 角色拥有的特定资产余额记录。
/// </summary>
/// <param name="PlayerId">所属角色 ID</param>
/// <param name="Asset">资产复合键</param>
/// <param name="Quantity">当前可用数量（double 精度）</param>
/// <param name="Version">单调递增版本号，用于 CAS 乐观锁核对</param>
public sealed record AssetBalance(PlayerId PlayerId, AssetKey Asset, double Quantity, long Version)
{
}

/// <summary>独立装备/法宝实例在经济系统中的流转状态。</summary>
public enum AssetInstanceState : byte
{
    /// <summary>玩家背包或穿戴中，自由持有</summary>
    Owned = 0,
    /// <summary>被交易、任务或炼制锁定预扣</summary>
    Reserved = 1,
    /// <summary>挂牌在坊市货架中等待撮合</summary>
    Listed = 2,
    /// <summary>已分解、熔炼或被系统销毁</summary>
    Destroyed = 3
}

/// <summary>
/// 具备独立属性的资产实例（如特定品阶词缀法宝、装备）。
/// </summary>
public sealed record AssetInstance(
    InstanceId InstanceId,
    PlayerId OwnerId,
    ItemId DefinitionId,
    AssetInstanceState State,
    long Version,
    IReadOnlyDictionary<string, double>? Attributes = null)
{
}

/// <summary>
/// 单笔资产变动请求或事件（增量数值与变更缘由）。
/// </summary>
public sealed record AssetDelta(
    PlayerId PlayerId,
    AssetKey Asset,
    double QuantityDelta,
    string Reason,
    InstanceId? InstanceId = null)
{
}

/// <summary>
/// 领域层角色内存钱包聚合。
/// 仅负责纯内存中的余额累加、扣减校验与防负数处理，不直接持有数据库连接或事务句柄。
/// </summary>
public sealed class AssetWallet
{
    private readonly Dictionary<AssetKey, AssetBalance> _balances = [];

    public AssetWallet(PlayerId playerId)
    {
        if (playerId.IsEmpty) throw new DomainException(DomainErrorCode.InvalidArgument, "钱包玩家不能为空。");
        PlayerId = playerId;
    }

    /// <summary>所属角色 ID</summary>
    public PlayerId PlayerId { get; }

    /// <summary>全部资产余额集合只读视图</summary>
    public IReadOnlyCollection<AssetBalance> Balances => _balances.Values;

    /// <summary>获取指定资产的当前可用余额，未持有则返回 0</summary>
    public double Get(AssetKey asset) => _balances.TryGetValue(asset, out var balance) ? balance.Quantity : 0;

    /// <summary>入账/增加指定资产数量，返回更新后的余额快照</summary>
    public AssetBalance Credit(AssetKey asset, double quantity, string reason)
    {
        ValidateQuantity(quantity);
        return Apply(asset, quantity, reason);
    }

    /// <summary>扣减指定资产数量；余额不足时抛出 InsufficientBalance 领域异常</summary>
    public AssetBalance Debit(AssetKey asset, double quantity, string reason)
    {
        ValidateQuantity(quantity);
        if (Get(asset) + 1e-12 < quantity)
            throw new DomainException(DomainErrorCode.InsufficientBalance, $"资产 {asset} 余额不足。");
        return Apply(asset, -quantity, reason);
    }

    /// <summary>应用单条资产变动差额</summary>
    public void Apply(AssetDelta delta)
    {
        if (delta.PlayerId != PlayerId) throw new DomainException(DomainErrorCode.InvalidArgument, "资产玩家与钱包不一致。");
        // 零增量是与 1e-12 清零逻辑配套的合法语义，不产生任何账目变更。
        if (delta.QuantityDelta == 0) return;
        if (delta.QuantityDelta > 0) Credit(delta.Asset, delta.QuantityDelta, delta.Reason);
        else Debit(delta.Asset, -delta.QuantityDelta, delta.Reason);
    }

    /// <summary>从持久化存储全量恢复钱包余额集合</summary>
    public void Restore(IEnumerable<AssetBalance> balances)
    {
        ArgumentNullException.ThrowIfNull(balances);
        _balances.Clear();
        foreach (var balance in balances)
        {
            if (balance.PlayerId != PlayerId || balance.PlayerId.IsEmpty || !balance.Asset.ItemId.IsValid || balance.Quantity < 0 || !double.IsFinite(balance.Quantity) || balance.Version < 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "资产余额无效。");
            if (!_balances.TryAdd(balance.Asset, balance))
                throw new DomainException(DomainErrorCode.Duplicate, $"重复资产余额：{balance.Asset}。");
        }
    }

    /// <summary>执行具体数值加减，包含浮点有限性校验与极微小残余误差清零</summary>
    private AssetBalance Apply(AssetKey asset, double delta, string reason)
    {
        if (!asset.ItemId.IsValid || asset.Variant is null || asset.Variant.Length > 128 || !double.IsFinite(delta) || delta == 0 || string.IsNullOrWhiteSpace(reason))
            throw new DomainException(DomainErrorCode.InvalidArgument, "资产变化无效。");
        var current = _balances.TryGetValue(asset, out var existing)
            ? existing
            : new AssetBalance(PlayerId, asset, 0, 0);
        var quantity = current.Quantity + delta;
        if (!double.IsFinite(quantity)) throw new DomainException(DomainErrorCode.InvariantViolation, $"资产 {asset} 数量超出数值范围。");
        if (quantity < -1e-12) throw new DomainException(DomainErrorCode.InsufficientBalance, $"资产 {asset} 余额不足。");
        quantity = Math.Abs(quantity) < 1e-12 ? 0 : quantity;
        var next = current with { Quantity = quantity, Version = checked(current.Version + 1) };
        _balances[asset] = next;
        return next;
    }

    private static void ValidateQuantity(double quantity)
    {
        if (!double.IsFinite(quantity) || quantity <= 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "资产数量必须为正的有限数。");
    }
}

/// <summary>经济账本事务类型分类</summary>
public enum EconomyTransactionType : byte
{
    /// <summary>挂机行动或突破产出奖励</summary>
    SimulationReward = 0,
    /// <summary>坊市挂单买入/卖出锁定</summary>
    MarketPlace = 1,
    /// <summary>坊市订单撮合成交结算</summary>
    MarketTrade = 2,
    /// <summary>坊市撤单解冻退款</summary>
    MarketCancel = 3,
    /// <summary>系统补偿或管理员调账</summary>
    AdminAdjustment = 4
}

/// <summary>
/// 经济账本不可变事务快照，记录一次完整原子性资产流转及其分录明细。
/// </summary>
public sealed record EconomyTransaction
{
    public EconomyTransaction(
        TransactionId transactionId,
        IdempotencyKey idempotencyKey,
        EconomyTransactionType type,
        PlayerId actorId,
        DateTimeOffset createdAt,
        IReadOnlyList<EconomyTransactionLine> lines,
        string? externalEventType = null)
    {
        if (transactionId.IsEmpty || !idempotencyKey.IsValid || actorId.IsEmpty || !Enum.IsDefined(type))
            throw new DomainException(DomainErrorCode.InvalidArgument, "经济事务头无效。");
        if (lines.Count == 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "经济事务至少需要一条明细。");
        var lineNumbers = new HashSet<int>();
        var copied = new EconomyTransactionLine[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line is null || line.LineNumber <= 0 || !lineNumbers.Add(line.LineNumber) || line.PlayerId.IsEmpty ||
                !line.Asset.ItemId.IsValid || line.Asset.Variant is null || line.Asset.Variant.Length > 128 ||
                !double.IsFinite(line.Delta) || Math.Abs(line.Delta) < 1e-12)
                throw new DomainException(DomainErrorCode.InvalidArgument, "经济事务明细无效。");
            copied[i] = line;
        }
        TransactionId = transactionId;
        IdempotencyKey = idempotencyKey;
        Type = type;
        ActorId = actorId;
        CreatedAt = createdAt.ToUniversalTime();
        Lines = copied;
        ExternalEventType = externalEventType;
    }

    /// <summary>事务全局唯一 ID</summary>
    public TransactionId TransactionId { get; }
    /// <summary>客户端或发起方幂等键，防止重复执行</summary>
    public IdempotencyKey IdempotencyKey { get; }
    /// <summary>业务事务类型</summary>
    public EconomyTransactionType Type { get; }
    /// <summary>发起操作的角色 ID</summary>
    public PlayerId ActorId { get; }
    /// <summary>事务创建时间（UTC）</summary>
    public DateTimeOffset CreatedAt { get; }
    /// <summary>各分录变动行列表（双分录记账）</summary>
    public IReadOnlyList<EconomyTransactionLine> Lines { get; }
    /// <summary>关联的外部事件代码</summary>
    public string? ExternalEventType { get; }
}

/// <summary>
/// 经济事务内的单行记账分录。
/// </summary>
public sealed record EconomyTransactionLine
{
    public EconomyTransactionLine(int lineNumber, PlayerId playerId, AssetKey asset, double delta, InstanceId? instanceId = null)
    {
        LineNumber = lineNumber;
        PlayerId = playerId;
        Asset = asset;
        Delta = delta;
        InstanceId = instanceId;
    }

    /// <summary>分录行号（1 起步递增）</summary>
    public int LineNumber { get; }
    /// <summary>变动涉及的角色 ID</summary>
    public PlayerId PlayerId { get; }
    /// <summary>变动的资产类型</summary>
    public AssetKey Asset { get; }
    /// <summary>变动差值（正数为增加，负数为扣减）</summary>
    public double Delta { get; }
    /// <summary>若涉及非同质化物品，则记录具体实例 ID</summary>
    public InstanceId? InstanceId { get; }
}

