namespace Domain.Economy;

public readonly record struct AssetKey(ItemId ItemId, string Variant = "")
{
    public override string ToString() => string.IsNullOrEmpty(Variant) ? ItemId.ToString() : $"{ItemId}:{Variant}";
}

public sealed record AssetBalance(PlayerId PlayerId, AssetKey Asset, double Quantity, long Version)
{
}

public enum AssetInstanceState : byte
{
    Owned = 0,
    Reserved = 1,
    Listed = 2,
    Destroyed = 3
}

public sealed record AssetInstance(
    InstanceId InstanceId,
    PlayerId OwnerId,
    ItemId DefinitionId,
    AssetInstanceState State,
    long Version,
    IReadOnlyDictionary<string, double>? Attributes = null)
{
}

public sealed record AssetDelta(
    PlayerId PlayerId,
    AssetKey Asset,
    double QuantityDelta,
    string Reason,
    InstanceId? InstanceId = null)
{
}

/// <summary>领域内存钱包；持久化层只需把变更映射到余额表和账本事务。</summary>
public sealed class AssetWallet
{
    private readonly Dictionary<AssetKey, AssetBalance> _balances = [];

    public AssetWallet(PlayerId playerId)
    {
        if (playerId.IsEmpty) throw new DomainException(DomainErrorCode.InvalidArgument, "钱包玩家不能为空。");
        PlayerId = playerId;
    }

    public PlayerId PlayerId { get; }

    public IReadOnlyCollection<AssetBalance> Balances => _balances.Values;

    public double Get(AssetKey asset) => _balances.TryGetValue(asset, out var balance) ? balance.Quantity : 0;

    public AssetBalance Credit(AssetKey asset, double quantity, string reason)
    {
        ValidateQuantity(quantity);
        return Apply(asset, quantity, reason);
    }

    public AssetBalance Debit(AssetKey asset, double quantity, string reason)
    {
        ValidateQuantity(quantity);
        if (Get(asset) + 1e-12 < quantity)
            throw new DomainException(DomainErrorCode.InsufficientBalance, $"资产 {asset} 余额不足。");
        return Apply(asset, -quantity, reason);
    }

    public void Apply(AssetDelta delta)
    {
        if (delta.PlayerId != PlayerId) throw new DomainException(DomainErrorCode.InvalidArgument, "资产玩家与钱包不一致。");
        // 零增量是与 1e-12 清零逻辑配套的合法语义，不产生任何账目变更。
        if (delta.QuantityDelta == 0) return;
        if (delta.QuantityDelta > 0) Credit(delta.Asset, delta.QuantityDelta, delta.Reason);
        else Debit(delta.Asset, -delta.QuantityDelta, delta.Reason);
    }

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

public enum EconomyTransactionType : byte
{
    SimulationReward = 0,
    MarketPlace = 1,
    MarketTrade = 2,
    MarketCancel = 3,
    AdminAdjustment = 4
}

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

    public TransactionId TransactionId { get; }
    public IdempotencyKey IdempotencyKey { get; }
    public EconomyTransactionType Type { get; }
    public PlayerId ActorId { get; }
    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyList<EconomyTransactionLine> Lines { get; }
    public string? ExternalEventType { get; }
}

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

    public int LineNumber { get; }
    public PlayerId PlayerId { get; }
    public AssetKey Asset { get; }
    public double Delta { get; }
    public InstanceId? InstanceId { get; }
}
