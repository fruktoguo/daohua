namespace Server.Clean.Social;

/// <summary>组队讨伐唯一权威检查点；冻结输入与战斗运行态均由会话持有。</summary>
/// <param name="SessionId">会话标识。</param>
/// <param name="PartyId">开场队伍标识；队伍解散后仍保留历史引用。</param>
/// <param name="SpecJson">开场冻结输入。</param>
/// <param name="StateJson">可直接恢复的会话运行态。</param>
/// <param name="Version">检查点版本，用于条件更新。</param>
/// <param name="LastSimulatedAt">已提交的模拟截止时刻。</param>
/// <param name="EndedAt">权威散场时刻。</param>
/// <param name="EndReason">散场原因。</param>
public sealed record ActionParticipationSessionRecord(
    string SessionId,
    Guid PartyId,
    string SpecJson,
    string StateJson,
    long Version,
    DateTimeOffset LastSimulatedAt,
    DateTimeOffset? EndedAt = null,
    string? EndReason = null);

/// <summary>一次实际结算批次投递给单名成员的待消费载荷，不保存逐帧展示副本。</summary>
/// <param name="SessionId">来源会话。</param>
/// <param name="PlayerId">结算归属成员。</param>
/// <param name="Sequence">会话内结算序号。</param>
/// <param name="PayloadJson">个人结算载荷。</param>
/// <param name="CreatedAt">结算发生时刻。</param>
public sealed record ParticipationInboxItem(
    string SessionId,
    Guid PlayerId,
    long Sequence,
    string PayloadJson,
    DateTimeOffset CreatedAt);

/// <summary>随成员资产与分段原子提交的会话收件箱消费标识。</summary>
/// <param name="SessionId">来源会话。</param>
/// <param name="PlayerId">消费成员。</param>
/// <param name="Sequence">本次消费的结算序号。</param>
public sealed record ParticipationInboxReceipt(string SessionId, Guid PlayerId, long Sequence);

/// <summary>会话工作钱包需要读取的精确资产键，不包含数量或整包钱包。</summary>
/// <param name="PlayerId">资产所属成员。</param>
/// <param name="AssetId">消耗品定义标识。</param>
/// <param name="Variant">资产变体；普通消耗品为空串。</param>
public sealed record ParticipationAssetKey(Guid PlayerId, string AssetId, string Variant);

/// <summary>与检查点同事务核对的工作钱包读集和实际资产变化；缺货时只保留读集，不制造空流水。</summary>
/// <param name="Transaction">本批实际资产事务；没有余额变化时为 null。</param>
/// <param name="ReadSet">计算本批结算所依赖的余额与版本，包含缺失键的零余额、零版本。</param>
public sealed record ParticipationSessionEconomyCommit(
    EconomyTransactionRequest? Transaction,
    IReadOnlyList<AssetBalanceRecord> ReadSet);

/// <summary>权威会话检查点与成员结算收件箱的持久化边界。</summary>
public interface IParticipationSessionStore
{
    /// <summary>读取已初始化的会话检查点；开场规格已落库但运行态尚未交付时返回 null。</summary>
    ValueTask<ActionParticipationSessionRecord?> LoadAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>读取可恢复的活跃会话，不装载任何成员玩家。</summary>
    ValueTask<IReadOnlyList<ActionParticipationSessionRecord>> ListActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>按开场冻结名册读取成员的活跃会话，不依赖当前队伍成员关系。</summary>
    ValueTask<IReadOnlyList<ActionParticipationSessionRecord>> ListActiveForMemberAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>从持久化队伍、冻结名册与最新世界分段复核会话成立条件；成立返回 null，否则返回散场原因。</summary>
    ValueTask<string?> GetInvalidationReasonAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>读取冻结名册中当前离线成员最早的逐出截止时刻（离线锚点 + 宽限），防止回线冷算越过组织失效边界。</summary>
    ValueTask<DateTimeOffset?> GetOfflineExpiryAsync(string sessionId, TimeSpan offlineLimit,
        IReadOnlyCollection<Guid> onlinePlayerIds, CancellationToken cancellationToken = default);

    /// <summary>仅读取给定消耗品键并去重；缺失键仍返回零余额、零版本，供整批乐观核对。</summary>
    ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadConsumableBalancesAsync(
        IReadOnlyList<ParticipationAssetKey> keys, CancellationToken cancellationToken = default);

    /// <summary>按成员与物品读取全部实际变体及版本，忽略输入 Variant；缺失的默认变体仍返回零余额、零版本。</summary>
    /// <remarks>调用方必须持有覆盖读取至提交的参与关系闸门，避免本批读集之外新增变体。</remarks>
    ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadConsumableBalancesForItemsAsync(
        IReadOnlyList<ParticipationAssetKey> keys, CancellationToken cancellationToken = default);

    /// <summary>
    /// 首次原子交付开场检查点、实际资产变化与初始成员收件箱；已初始化时只返回权威检查点。
    /// 工作钱包读集冲突时抛出 ParticipationSessionEconomyConflictException，宿主必须整批重算。
    /// </summary>
    ValueTask<ActionParticipationSessionRecord> CreateOrInitializeAsync(ActionParticipationSessionRecord session,
        ParticipationSessionEconomyCommit? economy = null, IReadOnlyList<ParticipationInboxItem>? inbox = null,
        CancellationToken cancellationToken = default);

    /// <summary>按版本原子保存检查点、工作钱包实耗与成员结算；版本、余额读集冲突或已散场时返回 false。</summary>
    ValueTask<bool> CommitAdvanceAsync(string sessionId, long expectedVersion, string stateJson,
        DateTimeOffset lastSimulatedAt, IReadOnlyList<ParticipationInboxItem> inbox,
        DateTimeOffset? endedAt = null, string? endReason = null,
        ParticipationSessionEconomyCommit? economy = null, CancellationToken cancellationToken = default);

    /// <summary>按会话与结算序号读取成员未消费结算。</summary>
    ValueTask<IReadOnlyList<ParticipationInboxItem>> LoadPendingAsync(Guid playerId, CancellationToken cancellationToken = default);
}

/// <summary>开场工作钱包读集或经济幂等事实已改变；本批检查点、资产与收件箱均未提交，必须重新计算。</summary>
public sealed class ParticipationSessionEconomyConflictException(string sessionId)
    : InvalidOperationException($"会话工作钱包读集冲突 session={sessionId}");

/// <summary>会话检查点 COMMIT 已发出但结局未知，宿主必须读取持久化检查点后决定恢复。</summary>
public sealed class ParticipationSessionCommitOutcomeUnknownException(string sessionId, long version, Exception innerException)
    : InvalidOperationException($"会话检查点提交结局未知 session={sessionId} version={version}", innerException)
{
    /// <summary>待确认的会话。</summary>
    public string SessionId { get; } = sessionId;

    /// <summary>本次尝试提交的检查点版本。</summary>
    public long Version { get; } = version;
}
