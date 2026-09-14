namespace Server.Clean;

/// <summary>玩家状态 section 的稳定命名空间。新增 section 只需增加新键，不修改既有载荷。</summary>
public static class PlayerSectionKinds
{
    public const string Progression = "progression";
    public const string World = "world";
    public const string Combat = "combat";
    public const string Buffs = "buffs";
    public const string Loadout = "loadout";
    public const string Settings = "settings";
    public const string ActionQueue = "action_queue";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Progression, World, Combat, Buffs, Loadout, Settings, ActionQueue
    };

    public static bool IsValid(string value) => All.Contains(value);
}

/// <summary>单个 section 的可持久化载荷。Payload 已完成编码和压缩。</summary>
public sealed record SectionWrite(
    Guid PlayerId,
    string SectionKind,
    long StateVersion,
    int SchemaVersion,
    string Codec,
    ReadOnlyMemory<byte> Payload,
    int RawBytes,
    string Checksum,
    DateTimeOffset UpdatedAt,
    string GameDataFingerprint = "",
    DateTimeOffset? LastSimulatedAt = null)
{
    public int StoredBytes => Payload.Length;

    public void Validate()
    {
        if (PlayerId == Guid.Empty) throw new ArgumentException("PlayerId 不能为空。", nameof(PlayerId));
        if (!PlayerSectionKinds.IsValid(SectionKind)) throw new ArgumentException($"未知 section: {SectionKind}。", nameof(SectionKind));
        if (StateVersion <= 0) throw new ArgumentOutOfRangeException(nameof(StateVersion));
        if (SchemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(SchemaVersion));
        if (Codec is not (SectionCodec.RawJson or SectionCodec.BrotliJson))
            throw new ArgumentException("Codec 不受支持。", nameof(Codec));
        if (Payload.IsEmpty || Payload.Length > 4 * 1024 * 1024 || RawBytes <= 0 || RawBytes > 4 * 1024 * 1024)
            throw new ArgumentException("section 载荷大小无效。", nameof(Payload));
        if (string.IsNullOrWhiteSpace(Checksum) || Checksum.Length != 64 || Checksum.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Checksum 必须是 64 位十六进制字符串。", nameof(Checksum));
        if (GameDataFingerprint is null || GameDataFingerprint.Length > 128)
            throw new ArgumentException("游戏数据指纹超过长度上限。", nameof(GameDataFingerprint));
    }
}

/// <summary>持久化头元数据，用于跨实例与版本 CAS</summary>
public sealed record PlayerStateHead(
    Guid PlayerId,
    long StateVersion,
    int SchemaVersion,
    DateTimeOffset LastSimulatedAt,
    string GameDataFingerprint,
    string Checksum,
    DateTimeOffset UpdatedAt);

/// <summary>玩家不可变基础身份信息</summary>
public sealed record PlayerIdentity(
    Guid PlayerId,
    Guid AccountId,
    string DisplayName,
    DateTimeOffset CreatedAt);

/// <summary>数据库中存储的单个分段载荷行映射</summary>
public sealed record StoredSection(
    Guid PlayerId,
    string SectionKind,
    long StateVersion,
    int SchemaVersion,
    string Codec,
    byte[] Payload,
    int RawBytes,
    string Checksum,
    DateTimeOffset UpdatedAt);

/// <summary>聚合读取时从数据库加载出的完整数据绑定包</summary>
public sealed record PlayerStateBundle(
    PlayerIdentity Identity,
    PlayerStateHead Head,
    IReadOnlyDictionary<string, StoredSection> Sections,
    IReadOnlyList<StoredAction> Actions);

/// <summary>数据库持久化的行动队列项</summary>
public sealed record StoredAction(
    Guid PlayerId,
    int Slot,
    string ActionId,
    byte[] Payload,
    long ActionVersion,
    string Status,
    DateTimeOffset? NextDueAt,
    string IdempotencyKey);

/// <summary>分段异步刷盘工作队列中的待处理项</summary>
public sealed record FlushQueueItem(
    Guid PlayerId,
    string SectionKind,
    long StateVersion,
    int SchemaVersion,
    string Codec,
    byte[] Payload,
    int RawBytes,
    string Checksum,
    DateTimeOffset UpdatedAt,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    Guid LeaseToken,
    string GameDataFingerprint = "",
    DateTimeOffset? LastSimulatedAt = null)
{
    public void Validate(bool requireLease = false)
    {
        new SectionWrite(PlayerId, SectionKind, StateVersion, SchemaVersion, Codec, Payload, RawBytes, Checksum, UpdatedAt, GameDataFingerprint, LastSimulatedAt).Validate();
        if (Attempts < 0 || NextAttemptAt == default)
            throw new ArgumentException("section 队列重试元数据无效。", nameof(FlushQueueItem));
        if (requireLease && LeaseToken == Guid.Empty)
            throw new ArgumentException("section 队列租约标识无效。", nameof(FlushQueueItem));
    }
}

/// <summary>异步刷盘批处理统计结果</summary>
public sealed record FlushBatchResult(int Claimed, int Applied, int Failed, TimeSpan Duration);

/// <summary>指令幂等执行回执，记录特定 RequestId 的执行指纹与版本</summary>
public sealed record CommandReceipt(
    Guid PlayerId,
    Guid RequestId,
    string Command,
    string PayloadHash,
    long StateVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>事务发件箱（Transactional Outbox）待投递消息模型</summary>
public sealed record OutboxMessage(
    Guid OutboxId,
    string AggregateType,
    Guid AggregateId,
    string EventType,
    byte[] Payload,
    DateTimeOffset OccurredAt,
    int Attempts,
    DateTimeOffset VisibleAt,
    Guid LeaseToken);

/// <summary>玩家状态仓储接口：负责分段入队与异步流水线批量刷盘</summary>
public interface IPlayerStateRepository
{
    ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default);
    ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default);
    async ValueTask EnqueueSectionsAsync(
        IReadOnlyCollection<SectionWrite> sections,
        IReadOnlyCollection<CommandReceipt> receipts,
        CancellationToken cancellationToken = default)
    {
        await EnqueueSectionsAsync(sections, cancellationToken);
    }
    ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default);
}

/// <summary>客户端指令幂等回执存储边界</summary>
public interface ICommandReceiptStore
{
    ValueTask<CommandReceipt?> FindAsync(Guid playerId, Guid requestId, CancellationToken cancellationToken = default);
    ValueTask<int> PruneExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>
/// 玩家权威运行时租约。它不是路由器，而是跨实例写入的安全闸门：未持有租约的实例必须拒绝执行。
/// </summary>
public interface IPlayerOwnershipStore
{
    ValueTask<bool> AcquireAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default);
    ValueTask<bool> RenewAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default);
    ValueTask ReleaseAsync(Guid playerId, Guid ownerInstanceId, CancellationToken cancellationToken = default);
}

public sealed class PlayerOwnershipLostException(Guid playerId)
    : InvalidOperationException($"玩家运行时租约已失效 player={playerId:D}");

public interface IOutboxRepository
{
    ValueTask AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid aggregateId,
        string aggregateType,
        string eventType,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<OutboxMessage>> ClaimAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default);
    ValueTask MarkDeliveredAsync(Guid outboxId, Guid leaseToken, CancellationToken cancellationToken = default);
    ValueTask ReleaseAsync(Guid outboxId, Guid leaseToken, string error, TimeSpan retryAfter, CancellationToken cancellationToken = default);
}

public sealed record AssetDelta(Guid PlayerId, string AssetId, string Variant, double Delta);

/// <summary>只读资产余额查询边界。余额写入仍然只能经过 IEconomyTransactionStore。</summary>
public interface IAssetBalanceRepository
{
    ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(
        Guid playerId,
        CancellationToken cancellationToken = default);
}

public sealed record AssetBalanceRecord(
    Guid PlayerId,
    string AssetId,
    string Variant,
    double Quantity,
    long Version);

public sealed record EconomyTransactionRequest(
    Guid TransactionId,
    string IdempotencyKey,
    string TransactionType,
    Guid ActorId,
    IReadOnlyList<AssetDelta> Deltas,
    DateTimeOffset? OccurredAt = null,
    ReadOnlyMemory<byte> ResultPayload = default,
    IReadOnlyList<SectionWrite>? Sections = null,
    IReadOnlyList<CommandReceipt>? Receipts = null);

public sealed record EconomyTransactionResult(
    Guid TransactionId,
    string IdempotencyKey,
    bool Applied,
    bool AlreadyCommitted,
    DateTimeOffset CommittedAt,
    ReadOnlyMemory<byte> ResultPayload);

public interface IEconomyTransactionStore
{
    ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default);
}

public sealed record MarketOrderInput(
    Guid OrderId,
    int PartitionId,
    Guid OwnerPlayerId,
    string Side,
    string AssetId,
    string Variant,
    double Price,
    double Quantity,
    string IdempotencyKey,
    DateTimeOffset? CreatedAt = null);

public sealed record MarketFillInput(
    Guid TradeId,
    Guid BuyOrderId,
    Guid SellOrderId,
    Guid BuyerId,
    Guid SellerId,
    string AssetId,
    string Variant,
    double Price,
    double Quantity,
    double SellerTax,
    DateTimeOffset? TradedAt = null);

public sealed record MarketCommitResult(Guid OrderId, bool Applied, bool AlreadyCommitted, IReadOnlyList<Guid> TradeIds);
public sealed record SettlementClaimResult(Guid PlayerId, Guid RequestId, int SettledTradeCount, DateTimeOffset ClaimedAt, bool AlreadyClaimed);

public interface IMarketTransactionStore
{
    ValueTask<MarketCommitResult> CommitPlacementAsync(
        MarketOrderInput order,
        IReadOnlyList<MarketFillInput> fills,
        long fenceToken,
        CancellationToken cancellationToken = default);

    ValueTask<bool> CancelAsync(Guid orderId, Guid ownerPlayerId, int partitionId, long fenceToken, string idempotencyKey, CancellationToken cancellationToken = default);
}

public sealed record StoredMarketOrder(
    Guid OrderId,
    Guid OwnerPlayerId,
    string AssetId,
    string Variant,
    string Side,
    double Price,
    double Quantity,
    double FilledQuantity,
    string Status,
    long Sequence,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
