namespace Server.Clean;

// 玩家 section 落盘契约（PlayerSectionKinds/SectionWrite/PlayerStateHead/PlayerIdentity/
// StoredSection/PlayerStateBundle/StoredAction）与 SectionCodec 已统一收拢到
// Domain.Persistence：主服与 ComputeNode 共用同一份编解码实现，本文件只保留宿主侧契约。

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
    /// <summary>复用 <see cref="SectionWrite"/> 的边界校验，并额外检查重试元数据。</summary>
    /// <param name="requireLease">为 true 时要求队列行已被 worker 租约认领（LeaseToken 非空）。</param>
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
    /// <summary>加载玩家完整持久化状态；玩家存在但从未落盘时返回带空头的 bundle，不存在时返回 null。</summary>
    ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default);
    /// <summary>把脏 section 写入 latest-only 刷盘队列，由 worker 异步落库。</summary>
    ValueTask EnqueueSectionsAsync(IReadOnlyCollection<SectionWrite> sections, CancellationToken cancellationToken = default);
    /// <summary>同事务入队 section 与命令回执，保证「状态写回 + 幂等回执」原子生效。</summary>
    async ValueTask EnqueueSectionsAsync(
        IReadOnlyCollection<SectionWrite> sections,
        IReadOnlyCollection<CommandReceipt> receipts,
        CancellationToken cancellationToken = default)
    {
        await EnqueueSectionsAsync(sections, cancellationToken);
    }
    /// <summary>租约认领一批到期队列行并落库；行级冲突与坏载荷不回滚整批。</summary>
    ValueTask<FlushBatchResult> FlushPendingAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default);
}

/// <summary>客户端指令幂等回执存储边界</summary>
public interface ICommandReceiptStore
{
    /// <summary>按玩家与请求 ID 查未过期回执；命中即代表该命令已被执行过。</summary>
    ValueTask<CommandReceipt?> FindAsync(Guid playerId, Guid requestId, CancellationToken cancellationToken = default);
    /// <summary>物理删除过期回执，返回删除行数；由清理 worker 周期调用。</summary>
    ValueTask<int> PruneExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>
/// 玩家权威运行时租约。它不是路由器，而是跨实例写入的安全闸门：未持有租约的实例必须拒绝执行。
/// </summary>
public interface IPlayerOwnershipStore
{
    /// <summary>抢占玩家租约；仅当租约不存在、已过期或本实例已是持有者时返回 true。</summary>
    ValueTask<bool> AcquireAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default);
    /// <summary>持有者续租；ownerInstanceId 不匹配（已被他实例接管）时返回 false。</summary>
    ValueTask<bool> RenewAsync(Guid playerId, Guid ownerInstanceId, TimeSpan lease, CancellationToken cancellationToken = default);
    /// <summary>持有者主动释放租约；ownerInstanceId 不匹配时不误删他人租约。</summary>
    ValueTask ReleaseAsync(Guid playerId, Guid ownerInstanceId, CancellationToken cancellationToken = default);
}

/// <summary>玩家运行时租约被抢占或过期时抛出；调用方必须中止本次写路径。</summary>
/// <param name="playerId">丢失租约的玩家。</param>
public sealed class PlayerOwnershipLostException(Guid playerId)
    : InvalidOperationException($"玩家运行时租约已失效 player={playerId:D}");

/// <summary>
/// 事务发件箱存储边界：事件与业务写入同事务追加，由后台 worker 租约认领后投递。
/// </summary>
public interface IOutboxRepository
{
    /// <summary>在调用方提供的事务内追加一条待投递事件，随业务提交一起生效。</summary>
    /// <param name="connection">调用方已打开的连接。</param>
    /// <param name="transaction">业务写入所在的同一事务。</param>
    /// <param name="aggregateId">产生事件的聚合标识。</param>
    /// <param name="aggregateType">聚合类型，用于下游路由。</param>
    /// <param name="eventType">事件类型名。</param>
    /// <param name="payload">已序列化的事件载荷。</param>
    /// <param name="occurredAt">业务发生时刻，同时作为首次可见时间。</param>
    ValueTask AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid aggregateId,
        string aggregateType,
        string eventType,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    /// <summary>原子认领一批到期未投递事件并加租约；租约令牌随消息返回用于回执。</summary>
    ValueTask<IReadOnlyList<OutboxMessage>> ClaimAsync(int maxRows, TimeSpan lease, CancellationToken cancellationToken = default);
    /// <summary>投递成功后按租约令牌标记 delivered_at；令牌不匹配视为租约已转移，静默忽略。</summary>
    ValueTask MarkDeliveredAsync(Guid outboxId, Guid leaseToken, CancellationToken cancellationToken = default);
    /// <summary>投递失败时释放租约并按 retryAfter 推迟下次可见时间，error 留作诊断。</summary>
    ValueTask ReleaseAsync(Guid outboxId, Guid leaseToken, string error, TimeSpan retryAfter, CancellationToken cancellationToken = default);
}

/// <summary>一笔资产余额增减。</summary>
/// <param name="PlayerId">余额所属玩家。</param>
/// <param name="AssetId">资产标识（物品/货币定义 ID）。</param>
/// <param name="Variant">资产变体；普通资产为空串。</param>
/// <param name="Delta">增减量（double，负数为扣减）。</param>
public sealed record AssetDelta(Guid PlayerId, string AssetId, string Variant, double Delta);

/// <summary>只读资产余额查询边界。余额写入仍然只能经过 IEconomyTransactionStore。</summary>
public interface IAssetBalanceRepository
{
    /// <summary>读取玩家当前正余额列表（quantity > 0），用于快照与展示。</summary>
    ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadBalancesAsync(
        Guid playerId,
        CancellationToken cancellationToken = default);
}

/// <summary>一行资产余额的持久化视图。</summary>
/// <param name="PlayerId">余额所属玩家。</param>
/// <param name="AssetId">资产标识。</param>
/// <param name="Variant">资产变体。</param>
/// <param name="Quantity">当前余额。</param>
/// <param name="Version">乐观并发版本号，随每次写入递增。</param>
public sealed record AssetBalanceRecord(
    Guid PlayerId,
    string AssetId,
    string Variant,
    double Quantity,
    long Version);

/// <summary>一次资产事务的完整提交请求。</summary>
/// <param name="TransactionId">事务唯一标识，同时作为流水行主键。</param>
/// <param name="IdempotencyKey">幂等键；同键重复提交返回已存结果而不重复入账。</param>
/// <param name="TransactionType">事务类型标签（如 market），用于审计与幂等键归属校验。</param>
/// <param name="ActorId">发起事务的玩家标识。</param>
/// <param name="Deltas">本事务的全部余额增减行。</param>
/// <param name="OccurredAt">业务发生时刻；缺省由存储层取当前时间。</param>
/// <param name="ResultPayload">随事务持久化的结果载荷，重放时原样返回。</param>
/// <param name="Sections">随事务一并入队的玩家 section 写回。</param>
/// <param name="Receipts">随事务一并写入的命令幂等回执。</param>
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

/// <summary>资产事务提交结局。</summary>
/// <param name="TransactionId">实际落库的事务标识（幂等命中时为已存事务 ID）。</param>
/// <param name="IdempotencyKey">本次使用的幂等键。</param>
/// <param name="Applied">事务是否已被接受（含幂等命中）。</param>
/// <param name="AlreadyCommitted">为 true 表示命中幂等键直接返回历史结果，未重复入账。</param>
/// <param name="CommittedAt">事务实际提交时刻。</param>
/// <param name="ResultPayload">随事务保存的结果载荷。</param>
/// <param name="Balances">本次实际落库后的最新余额行（按合并后增量逐行 RETURNING 带回）；
/// 幂等重放（AlreadyCommitted）时为 null——本次未重复入账，调用方需要余额时自行重读。</param>
public sealed record EconomyTransactionResult(
    Guid TransactionId,
    string IdempotencyKey,
    bool Applied,
    bool AlreadyCommitted,
    DateTimeOffset CommittedAt,
    ReadOnlyMemory<byte> ResultPayload,
    IReadOnlyList<AssetBalanceRecord>? Balances = null);

/// <summary>资产事务唯一提交点：余额变更、流水与随附写回在同一 Serializable 事务内完成。</summary>
public interface IEconomyTransactionStore
{
    /// <summary>提交一次资产事务；幂等键冲突且载荷不一致时抛 EconomyIdempotencyConflictException。</summary>
    ValueTask<EconomyTransactionResult> CommitAsync(EconomyTransactionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>一笔待落库的市场挂单输入。</summary>
/// <param name="OrderId">订单唯一标识，由调用方生成以支持幂等重试。</param>
/// <param name="PartitionId">订单归属的市场分片。</param>
/// <param name="OwnerPlayerId">挂单玩家。</param>
/// <param name="Side">订单方向：buy 或 sell。</param>
/// <param name="AssetId">标的资产标识。</param>
/// <param name="Variant">标的变体。</param>
/// <param name="Price">限价。</param>
/// <param name="Quantity">委托数量。</param>
/// <param name="IdempotencyKey">挂单幂等键，绑定订单参数防重放改单。</param>
/// <param name="CreatedAt">委托时刻；缺省由存储层取当前时间。</param>
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

/// <summary>一笔撮合成交的落库事实。</summary>
/// <param name="TradeId">成交唯一标识。</param>
/// <param name="BuyOrderId">买单订单标识。</param>
/// <param name="SellOrderId">卖单订单标识。</param>
/// <param name="BuyerId">买方玩家。</param>
/// <param name="SellerId">卖方玩家。</param>
/// <param name="AssetId">标的资产标识。</param>
/// <param name="Variant">标的变体。</param>
/// <param name="Price">实际成交价（maker 价）。</param>
/// <param name="Quantity">成交数量。</param>
/// <param name="SellerTax">向卖方收取的手续费，从成交额中扣除。</param>
/// <param name="TradedAt">成交时刻；缺省由存储层取当前时间。</param>
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

/// <summary>挂单事务提交结局。</summary>
/// <param name="OrderId">提交的订单标识。</param>
/// <param name="Applied">订单是否已被账本接受（含幂等命中）。</param>
/// <param name="AlreadyCommitted">为 true 表示命中幂等，订单与成交未重复写入。</param>
/// <param name="TradeIds">本次新落库的成交标识列表。</param>
public sealed record MarketCommitResult(Guid OrderId, bool Applied, bool AlreadyCommitted, IReadOnlyList<Guid> TradeIds);
/// <summary>结算确认游标的写入结局。</summary>
/// <param name="PlayerId">领取结算的玩家。</param>
/// <param name="RequestId">客户端请求标识，用于幂等去重。</param>
/// <param name="SettledTradeCount">本次确认覆盖的已入账成交笔数。</param>
/// <param name="ClaimedAt">确认游标写入时刻。</param>
/// <param name="AlreadyClaimed">为 true 表示同 RequestId 已确认过，本次为幂等重放。</param>
public sealed record SettlementClaimResult(Guid PlayerId, Guid RequestId, int SettledTradeCount, DateTimeOffset ClaimedAt, bool AlreadyClaimed);

/// <summary>市场账本事务边界：挂单、撤单在 Serializable 事务 + 分片租约 fence 校验下提交。</summary>
public interface IMarketTransactionStore
{
    /// <summary>提交挂单及其即时撮合成交；fenceToken 失效抛 MarketLeaseLostException。</summary>
    /// <param name="order">挂单输入。</param>
    /// <param name="fills">该挂单撮合产生的成交列表。</param>
    /// <param name="fenceToken">当前写者持有的分片租约 fence。</param>
    ValueTask<MarketCommitResult> CommitPlacementAsync(
        MarketOrderInput order,
        IReadOnlyList<MarketFillInput> fills,
        long fenceToken,
        CancellationToken cancellationToken = default);

    /// <summary>撤单并释放冻结资产；幂等键已提交过则直接返回 true，订单不可撤返回 false。</summary>
    ValueTask<bool> CancelAsync(Guid orderId, Guid ownerPlayerId, int partitionId, long fenceToken, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>冷挂机计算结果落库结局。</summary>
public enum ComputeApplyOutcome : byte
{
    /// <summary>结果已按目标版本写入 section 与提交头。</summary>
    Applied = 0,
    /// <summary>提交头已不在任务发起版本：结果被丢弃，不覆盖较新状态。</summary>
    StaleVersion = 1,
    /// <summary>玩家提交头不存在：结果无处安放。</summary>
    MissingPlayer = 2
}

/// <summary>市场订单表的行映射，用于启动时重建订单簿投影与玩家订单查询。</summary>
/// <param name="OrderId">订单唯一标识。</param>
/// <param name="OwnerPlayerId">挂单玩家。</param>
/// <param name="AssetId">标的资产标识。</param>
/// <param name="Variant">标的变体。</param>
/// <param name="Side">订单方向：buy 或 sell。</param>
/// <param name="Price">限价。</param>
/// <param name="Quantity">委托总量。</param>
/// <param name="FilledQuantity">已成交数量。</param>
/// <param name="Status">订单状态（active/partially_filled/filled/cancelled/expired）。</param>
/// <param name="Sequence">分片内单调递增的撮合优先级序号。</param>
/// <param name="CreatedAt">委托时刻。</param>
/// <param name="ExpiresAt">订单过期时刻。</param>
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
