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

    /// <summary>
    /// 首次交付开场运行态；已初始化时返回存储权威检查点，不覆盖其推进进度。
    /// <paramref name="escrow"/> 非空时把开打消耗品托管实扣并入同一事务：会话行、检查点与
    /// 资产扣件同生同灭，不存在「托管已扣但会话未建」的孤儿窗口。
    /// </summary>
    ValueTask<ActionParticipationSessionRecord> CreateOrInitializeAsync(ActionParticipationSessionRecord session,
        EconomyTransactionRequest? escrow = null, CancellationToken cancellationToken = default);

    /// <summary>按检查点版本原子保存推进与全部成员结算；版本不匹配或已散场时返回 false。</summary>
    ValueTask<bool> CommitAdvanceAsync(string sessionId, long expectedVersion, string stateJson,
        DateTimeOffset lastSimulatedAt, IReadOnlyList<ParticipationInboxItem> inbox,
        DateTimeOffset? endedAt = null, string? endReason = null, CancellationToken cancellationToken = default);

    /// <summary>按会话与结算序号读取成员未消费结算。</summary>
    ValueTask<IReadOnlyList<ParticipationInboxItem>> LoadPendingAsync(Guid playerId, CancellationToken cancellationToken = default);
}

/// <summary>会话检查点 COMMIT 已发出但结局未知，宿主必须读取持久化检查点后决定恢复。</summary>
public sealed class ParticipationSessionCommitOutcomeUnknownException(string sessionId, long version, Exception innerException)
    : InvalidOperationException($"会话检查点提交结局未知 session={sessionId} version={version}", innerException)
{
    /// <summary>待确认的会话。</summary>
    public string SessionId { get; } = sessionId;

    /// <summary>本次尝试提交的检查点版本。</summary>
    public long Version { get; } = version;
}
