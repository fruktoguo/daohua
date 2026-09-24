namespace Server.Clean.Social;

/// <summary>好友申请持久化行。</summary>
/// <param name="RequestId">申请唯一标识。</param>
/// <param name="RequesterId">申请人玩家 ID。</param>
/// <param name="TargetId">被申请人玩家 ID。</param>
/// <param name="Status">申请状态（pending/accepted/rejected/cancelled）。</param>
/// <param name="CreatedAt">申请发起时刻。</param>
public sealed record FriendRequestRecord(Guid RequestId, Guid RequesterId, Guid TargetId, string Status, DateTimeOffset CreatedAt);

/// <summary>好友申请的对端视图：把申请人/被申请人折叠为「对端玩家」。</summary>
/// <param name="RequestId">申请唯一标识。</param>
/// <param name="PlayerId">对端玩家 ID（收到时为申请人，发出时为目标）。</param>
/// <param name="DisplayName">对端道号。</param>
/// <param name="CreatedAt">申请发起时刻。</param>
public sealed record FriendRequestView(Guid RequestId, Guid PlayerId, string DisplayName, DateTimeOffset CreatedAt);

/// <summary>好友关系与道号的组合视图。</summary>
/// <param name="FriendId">好友玩家 ID。</param>
/// <param name="DisplayName">好友道号。</param>
public sealed record FriendRecord(Guid FriendId, string DisplayName);

/// <summary>队伍成员持久化行。</summary>
/// <param name="PlayerId">成员玩家 ID。</param>
/// <param name="DisplayName">成员道号。</param>
public sealed record PartyMemberRecord(Guid PlayerId, string DisplayName);

/// <summary>队伍与其成员。</summary>
/// <param name="PartyId">队伍唯一标识。</param>
/// <param name="LeaderId">队长玩家 ID。</param>
/// <param name="Members">成员列表（含队长）。</param>
public sealed record PartyRecord(Guid PartyId, Guid LeaderId, IReadOnlyList<PartyMemberRecord> Members);

/// <summary>待处理的队伍邀请。</summary>
/// <param name="PartyId">邀请方队伍。</param>
/// <param name="InviterId">邀请人玩家 ID。</param>
/// <param name="InviterName">邀请人道号。</param>
public sealed record PartyInvitationRecord(Guid PartyId, Guid InviterId, string InviterName);

/// <summary>
/// 一次往返装配好的社交状态持久化视图（<see cref="SocialRuntime.BuildStateAsync"/> 的输入）。
/// </summary>
/// <param name="Friends">好友列表（按道号排序）。</param>
/// <param name="IncomingRequests">收到的待处理好友申请。</param>
/// <param name="OutgoingRequests">发出的待处理好友申请。</param>
/// <param name="Party">所在队伍与成员；未组队或队伍记录缺失时为 null。</param>
/// <param name="Invitation">待处理的队伍邀请；无邀请时为 null。</param>
public sealed record SocialStateSnapshot(
    IReadOnlyList<FriendRecord> Friends,
    IReadOnlyList<FriendRequestView> IncomingRequests,
    IReadOnlyList<FriendRequestView> OutgoingRequests,
    PartyRecord? Party,
    PartyInvitationRecord? Invitation);

/// <summary>一条已持久化的传音消息。</summary>
/// <param name="MessageId">消息唯一标识。</param>
/// <param name="Channel">频道标识。</param>
/// <param name="ScopeKey">频道作用域键。</param>
/// <param name="SenderId">发送者玩家 ID。</param>
/// <param name="SenderName">发送者道号。</param>
/// <param name="Body">消息正文。</param>
/// <param name="SentAt">发送时刻。</param>
public sealed record ChatMessageRecord(Guid MessageId, string Channel, string ScopeKey, Guid SenderId, string SenderName, string Body, DateTimeOffset SentAt);

/// <summary>社交指令失败原因码；成功时为 <see cref="None"/>。</summary>
public enum SocialFailure : byte
{
    /// <summary>无失败。</summary>
    None = 0,
    /// <summary>动作标识不受支持。</summary>
    InvalidAction,
    /// <summary>目标玩家不存在。</summary>
    TargetNotFound,
    /// <summary>不能对自己执行该动作。</summary>
    SelfTarget,
    /// <summary>双方已是好友。</summary>
    AlreadyFriends,
    /// <summary>双方不是好友。</summary>
    NotFriends,
    /// <summary>已存在待处理的同向申请。</summary>
    RequestExists,
    /// <summary>申请不存在或不属于当前玩家。</summary>
    RequestNotFound,
    /// <summary>目标已在其他队伍中。</summary>
    TargetAlreadyInParty,
    /// <summary>自己已在队伍中。</summary>
    AlreadyInParty,
    /// <summary>自己不在队伍中。</summary>
    NotInParty,
    /// <summary>队伍人数已达上限。</summary>
    PartyFull,
    /// <summary>只有队长可以执行该动作。</summary>
    NotLeader,
    /// <summary>没有待处理的队伍邀请。</summary>
    InvitationNotFound
}

/// <summary>社交指令执行结局。</summary>
/// <param name="Failure">失败原因；成功时为 <see cref="SocialFailure.None"/>。</param>
public sealed record SocialOutcome(SocialFailure Failure)
{
    /// <summary>成功结局单例。</summary>
    public static readonly SocialOutcome Ok = new(SocialFailure.None);
    /// <summary>是否执行成功。</summary>
    public bool Success => Failure == SocialFailure.None;
}

/// <summary>好友、队伍与队伍邀请的持久化边界。</summary>
public interface ISocialRepository
{
    /// <summary>按道号归一化精确查找玩家；不存在返回 null。</summary>
    ValueTask<Guid?> FindPlayerByNameAsync(string displayName, CancellationToken cancellationToken = default);

    /// <summary>读取玩家道号；玩家不存在返回 null。</summary>
    ValueTask<string?> GetDisplayNameAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>列出好友关系与对端道号。</summary>
    ValueTask<IReadOnlyList<FriendRecord>> ListFriendsAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>列出待处理好友申请的对端视图；incoming 为 true 时返回收到的，否则返回发出的。</summary>
    ValueTask<IReadOnlyList<FriendRequestView>> ListPendingRequestsAsync(Guid playerId, bool incoming, CancellationToken cancellationToken = default);

    /// <summary>
    /// 一次往返读取构建社交状态所需的全部视图（好友/双向申请/队伍含成员/队伍邀请），
    /// 供扇出与握手场景批量构建，替代逐条串行查询。
    /// </summary>
    ValueTask<SocialStateSnapshot> LoadStateSnapshotAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>判断双方是否已是好友。</summary>
    ValueTask<bool> AreFriendsAsync(Guid left, Guid right, CancellationToken cancellationToken = default);

    /// <summary>创建一条待处理好友申请；同向 pending 已存在时返回 false。</summary>
    ValueTask<bool> TryCreateFriendRequestAsync(Guid requestId, Guid requesterId, Guid targetId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>接受申请：置 accepted 并同事务写入双向好友关系；无待处理申请时返回 false。</summary>
    ValueTask<bool> TryAcceptFriendRequestAsync(Guid requesterId, Guid targetId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>拒绝申请：置 rejected；无待处理申请时返回 false。</summary>
    ValueTask<bool> TryRejectFriendRequestAsync(Guid requesterId, Guid targetId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>解除双向好友关系；实际删除任一行时返回 true。</summary>
    ValueTask<bool> TryRemoveFriendAsync(Guid playerId, Guid friendId, CancellationToken cancellationToken = default);

    /// <summary>读取玩家当前队伍 ID；未组队返回 null。</summary>
    ValueTask<Guid?> GetPartyIdAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>读取队伍与其成员（含道号）；队伍不存在返回 null。</summary>
    ValueTask<PartyRecord?> GetPartyAsync(Guid partyId, CancellationToken cancellationToken = default);

    /// <summary>创建队伍并写入队长成员行。</summary>
    ValueTask<Guid> CreatePartyAsync(Guid leaderId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>把玩家加入队伍；玩家已在其他队伍时返回 false。</summary>
    ValueTask<bool> TryAddPartyMemberAsync(Guid partyId, Guid playerId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>移除玩家队伍成员行；玩家不在队伍返回 false。</summary>
    ValueTask<bool> TryRemovePartyMemberAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>成员全部离开后删除空队伍记录。</summary>
    ValueTask DeletePartyIfEmptyAsync(Guid partyId, CancellationToken cancellationToken = default);

    /// <summary>写入或覆盖玩家的待处理队伍邀请（每名玩家最多一条）。</summary>
    ValueTask UpsertPartyInvitationAsync(Guid partyId, Guid inviteeId, Guid inviterId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>读取玩家待处理队伍邀请；无邀请返回 null。</summary>
    ValueTask<PartyInvitationRecord?> GetPartyInvitationAsync(Guid inviteeId, CancellationToken cancellationToken = default);

    /// <summary>清除玩家的待处理队伍邀请；实际删除行时返回 true。</summary>
    ValueTask<bool> TryClearPartyInvitationAsync(Guid inviteeId, CancellationToken cancellationToken = default);

    /// <summary>移交队长（队长离开队伍时把队长位交给剩余成员）。</summary>
    ValueTask TryTransferPartyLeaderAsync(Guid partyId, Guid leaderId, CancellationToken cancellationToken = default);
}

/// <summary>传音消息持久化边界。</summary>
public interface IChatRepository
{
    /// <summary>写入一条消息，并在同一事务内裁剪该作用域超出保留条数的旧行。</summary>
    /// <param name="message">待写入消息。</param>
    /// <param name="retention">该作用域保留的最新消息条数。</param>
    /// <param name="cancellationToken">取消标记。</param>
    ValueTask AppendAsync(ChatMessageRecord message, int retention, CancellationToken cancellationToken = default);

    /// <summary>读取某作用域最近 <paramref name="limit"/> 条消息，按时间正序。</summary>
    ValueTask<IReadOnlyList<ChatMessageRecord>> LoadRecentAsync(string channel, string scopeKey, int limit, CancellationToken cancellationToken = default);
}
