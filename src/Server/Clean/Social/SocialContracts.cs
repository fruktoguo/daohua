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
/// <param name="Ready">组队讨伐就绪标记（总纲 §21.13）。</param>
public sealed record PartyMemberRecord(Guid PlayerId, string DisplayName, bool Ready = false);

/// <summary>队伍档案（招募标签口径，不参与行动/战斗结算）。</summary>
/// <param name="Name">队伍名称；空串表示尚未命名（展示层回落「{队长道号}的小队」）。</param>
/// <param name="ActivityKind">目标类型：any/combat/exploration/cultivation。</param>
/// <param name="NodeId">目标节点 ID；空串表示不限。</param>
/// <param name="MinValuation">身家门槛；0 表示不设门槛。</param>
/// <param name="IsPublic">是否进入组队大厅。</param>
/// <param name="PvpEnabled">队伍斗法开关（总纲 §7.10.4）：整队作为一个匹配单位参与 PVP 撮合；
/// 仅 activityKind='combat' 可为 true、仅队长可改、会话创建时冻结进参与规格。</param>
public sealed record PartyProfileRecord(string Name, string ActivityKind, string NodeId, double MinValuation, bool IsPublic, bool PvpEnabled)
{
    /// <summary>默认档案：不限目标、无门槛、不公开、不斗法（邀请制自建队伍沿用）。</summary>
    public static readonly PartyProfileRecord Default = new("", "any", "", 0, false, false);
}

/// <summary>队伍与其成员。</summary>
/// <param name="PartyId">队伍唯一标识。</param>
/// <param name="LeaderId">队长玩家 ID。</param>
/// <param name="Profile">队伍档案。</param>
/// <param name="CreatedAt">创建时刻。</param>
/// <param name="Members">成员列表（含队长，按加入时间排序）。</param>
/// <param name="OpenSlots">开启槽位（2~5，总纲 §21.13）；满员判定与讨伐开战条件均以此为上限。</param>
public sealed record PartyRecord(Guid PartyId, Guid LeaderId, PartyProfileRecord Profile, DateTimeOffset CreatedAt,
    IReadOnlyList<PartyMemberRecord> Members, int OpenSlots = 5);

/// <summary>组队讨伐会话持久化行（总纲 §21.13）。</summary>
/// <param name="SessionId">会话唯一标识（party:{partyId}:{unixSec}:{rand} 文本）。</param>
/// <param name="PartyId">所属队伍。</param>
/// <param name="EncounterId">钉死的遭遇 ID。</param>
/// <param name="NodeId">讨伐节点 ID。</param>
/// <param name="StartedAt">开战时刻（权威墙钟）。</param>
/// <param name="MemberIds">冻结成员名册（spec.members 顺序）。</param>
/// <param name="EndedAt">散场时刻；null 表示会话活跃。</param>
/// <param name="EndReason">散场原因标签（member-left/party-disbanded/…/server.restart）。</param>
/// <param name="SpecJson">冻结的共享剧本规格原始 JSON（装载对账补发行动时反序列化为 <c>ActionParticipationSpec</c>）。</param>
/// <param name="PvpEnabled">冻结的队伍斗法开关（spec.pvpEnabled 浅层解析）：档案值漂移即触发散场重建。</param>
public sealed record PartySessionRecord(
    string SessionId,
    Guid PartyId,
    string EncounterId,
    string NodeId,
    DateTimeOffset StartedAt,
    IReadOnlyList<Guid> MemberIds,
    string SpecJson,
    DateTimeOffset? EndedAt = null,
    string? EndReason = null,
    bool PvpEnabled = false);

/// <summary>48h 离线逐出结果：一条被删除的成员行。</summary>
/// <param name="PlayerId">被逐出玩家 ID。</param>
/// <param name="PartyId">原队伍 ID。</param>
public sealed record PartyMemberEviction(Guid PlayerId, Guid PartyId);

/// <summary>离线修士统计结果（总离线、48小时内挂机收益中修士与非挂机纯离线修士）。</summary>
/// <param name="TotalOffline">总离线修士数（有效注册且非在线、非封禁）。</param>
/// <param name="IdleOffline">48小时收益窗口内离线挂机修士数。</param>
public readonly record struct OfflinePlayerCounts(int TotalOffline, int IdleOffline)
{
    /// <summary>非挂机离线人数（超出48小时收益期或完全离线）。</summary>
    public int PureOffline => Math.Max(0, TotalOffline - IdleOffline);
}

/// <summary>待处理的队伍邀请。</summary>
/// <param name="PartyId">邀请方队伍。</param>
/// <param name="InviterId">邀请人玩家 ID。</param>
/// <param name="InviterName">邀请人道号。</param>
/// <param name="PartyName">邀请方队伍名称。</param>
public sealed record PartyInvitationRecord(Guid PartyId, Guid InviterId, string InviterName, string PartyName);

/// <summary>「匹配中」散人登记持久化行。</summary>
/// <param name="PlayerId">散人玩家 ID。</param>
/// <param name="DisplayName">散人道号。</param>
/// <param name="ActivityKind">目标类型。</param>
/// <param name="NodeId">目标节点 ID；空串表示不限。</param>
/// <param name="MinValuation">期望队友的最低身家。</param>
/// <param name="CreatedAt">登记时刻。</param>
public sealed record PartySeekerRecord(Guid PlayerId, string DisplayName, string ActivityKind, string NodeId, double MinValuation, DateTimeOffset CreatedAt);

/// <summary>
/// 一次往返装配好的社交状态持久化视图（<see cref="SocialRuntime.BuildStateAsync"/> 的输入）。
/// </summary>
/// <param name="Friends">好友列表（按道号排序）。</param>
/// <param name="IncomingRequests">收到的待处理好友申请。</param>
/// <param name="OutgoingRequests">发出的待处理好友申请。</param>
/// <param name="Party">所在队伍与成员；未组队或队伍记录缺失时为 null。</param>
/// <param name="Invitation">待处理的队伍邀请；无邀请时为 null。</param>
/// <param name="Seeker">本人匹配中登记；未登记为 null。</param>
/// <param name="PartySession">所在队伍的活跃组队讨伐会话；无会话或已散场为 null。</param>
public sealed record SocialStateSnapshot(
    IReadOnlyList<FriendRecord> Friends,
    IReadOnlyList<FriendRequestView> IncomingRequests,
    IReadOnlyList<FriendRequestView> OutgoingRequests,
    PartyRecord? Party,
    PartyInvitationRecord? Invitation,
    PartySeekerRecord? Seeker,
    PartySessionRecord? PartySession = null);

/// <summary>传音消息内嵌的队伍卡片持久化视图。</summary>
/// <param name="PartyId">队伍唯一标识。</param>
/// <param name="Name">队伍名称。</param>
/// <param name="ActivityKind">目标类型。</param>
/// <param name="NodeId">目标节点 ID。</param>
/// <param name="MinValuation">身家门槛。</param>
/// <param name="MemberCount">当前队伍人数。</param>
public sealed record PartyLinkRecord(Guid PartyId, string Name, string ActivityKind, string NodeId, double MinValuation, int MemberCount);

/// <summary>一条已持久化的传音消息。</summary>
/// <param name="MessageId">消息唯一标识。</param>
/// <param name="Channel">频道标识。</param>
/// <param name="ScopeKey">频道作用域键。</param>
/// <param name="SenderId">发送者玩家 ID。</param>
/// <param name="SenderName">发送者道号。</param>
/// <param name="Body">消息正文。</param>
/// <param name="SentAt">发送时刻。</param>
/// <param name="PartyLink">内嵌队伍卡片；无链接或队伍已解散时为 null。</param>
public sealed record ChatMessageRecord(Guid MessageId, string Channel, string ScopeKey, Guid SenderId, string SenderName, string Body, DateTimeOffset SentAt, PartyLinkRecord? PartyLink = null);

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
    InvitationNotFound,
    /// <summary>目标队伍不存在或已解散。</summary>
    PartyNotFound,
    /// <summary>加入者身家未达队伍门槛。</summary>
    ValuationTooLow,
    /// <summary>队伍档案字段非法（目标类型/节点/门槛/名称）。</summary>
    InvalidPartyProfile,
    /// <summary>本人没有匹配中登记。</summary>
    SeekingNotFound,
    /// <summary>发言触发频道节流。</summary>
    RateLimited,
    /// <summary>频道当前不可用（好友/附近作用域解析失败等）。</summary>
    ChannelUnavailable
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

/// <summary>加队裁决结果（满员/在队互斥由数据库原子判定）。</summary>
public enum PartyJoinResult : byte
{
    /// <summary>已加入。</summary>
    Joined = 0,
    /// <summary>队伍不存在。</summary>
    NotFound,
    /// <summary>队伍已满员。</summary>
    Full,
    /// <summary>玩家已在其他队伍中。</summary>
    AlreadyInParty
}

/// <summary>玩家身份视图（角色信息卡身份区数据源）。</summary>
/// <param name="DisplayName">道号。</param>
/// <param name="PlayerSeq">角色序号（identity.players.player_seq 注册自增编号）。</param>
public sealed record PlayerIdentityView(string DisplayName, long PlayerSeq);

/// <summary>好友、队伍与队伍邀请的持久化边界。</summary>
public interface ISocialRepository
{
    /// <summary>按道号归一化精确查找玩家；不存在返回 null。</summary>
    ValueTask<Guid?> FindPlayerByNameAsync(string displayName, CancellationToken cancellationToken = default);

    /// <summary>读取玩家道号；玩家不存在返回 null。</summary>
    ValueTask<string?> GetDisplayNameAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>读取玩家身份视图（道号 + 角色序号）；玩家不存在返回 null。</summary>
    ValueTask<PlayerIdentityView?> GetPlayerIdentityAsync(Guid playerId, CancellationToken cancellationToken = default);

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

    /// <summary>创建队伍并写入队长成员行（同一事务）；档案为招募标签，名称允许空串由上层回落。</summary>
    ValueTask<Guid> CreatePartyAsync(Guid leaderId, PartyProfileRecord profile, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>整体覆盖队伍档案（调用方已合并补丁语义）；队伍不存在返回 false。</summary>
    ValueTask<bool> TryUpdatePartyProfileAsync(Guid partyId, PartyProfileRecord profile, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// 原子加队：party 行 FOR UPDATE 串行化并发加入，事务内以 open_slots 复核满员与互斥；
    /// 成功时同事务清除该玩家的匹配登记与待处理邀请。
    /// </summary>
    ValueTask<PartyJoinResult> TryJoinPartyAsync(Guid partyId, Guid playerId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>解散队伍：删除 parties 行（成员/邀请级联、聊天链接置空），返回解散前成员列表；队伍不存在返回 null。</summary>
    ValueTask<IReadOnlyList<PartyMemberRecord>?> DisbandPartyAsync(Guid partyId, CancellationToken cancellationToken = default);

    /// <summary>移除指定队伍中的成员行；该成员不在此队伍返回 false。</summary>
    ValueTask<bool> TryKickPartyMemberAsync(Guid partyId, Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>移除玩家队伍成员行；玩家不在队伍返回 false。</summary>
    ValueTask<bool> TryRemovePartyMemberAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>成员全部离开后删除空队伍记录。</summary>
    ValueTask DeletePartyIfEmptyAsync(Guid partyId, CancellationToken cancellationToken = default);

    /// <summary>列出公开队伍（含成员与道号）；目标类型/节点为空串表示不限；按创建时间倒序，至多 limit 条。</summary>
    ValueTask<IReadOnlyList<PartyRecord>> ListPublicPartiesAsync(string activityKind, string nodeId, int limit, CancellationToken cancellationToken = default);

    /// <summary>写入或覆盖玩家的匹配登记（每名玩家最多一条）。</summary>
    ValueTask UpsertPartySeekerAsync(Guid playerId, string activityKind, string nodeId, double minValuation, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>撤下玩家的匹配登记；实际删除行时返回 true。</summary>
    ValueTask<bool> TryClearPartySeekerAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>读取玩家的匹配登记；未登记返回 null。</summary>
    ValueTask<PartySeekerRecord?> GetPartySeekerAsync(Guid playerId, CancellationToken cancellationToken = default);

    /// <summary>列出匹配中散人（自动排除已在队伍中的滞留登记）；目标类型/节点为空串表示不限；按登记时间倒序，至多 limit 条。</summary>
    ValueTask<IReadOnlyList<PartySeekerRecord>> ListPartySeekersAsync(string activityKind, string nodeId, int limit, CancellationToken cancellationToken = default);

    /// <summary>写入或覆盖玩家的待处理队伍邀请（每名玩家最多一条）。</summary>
    ValueTask UpsertPartyInvitationAsync(Guid partyId, Guid inviteeId, Guid inviterId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>读取玩家待处理队伍邀请；无邀请返回 null。</summary>
    ValueTask<PartyInvitationRecord?> GetPartyInvitationAsync(Guid inviteeId, CancellationToken cancellationToken = default);

    /// <summary>清除玩家的待处理队伍邀请；实际删除行时返回 true。</summary>
    ValueTask<bool> TryClearPartyInvitationAsync(Guid inviteeId, CancellationToken cancellationToken = default);

    /// <summary>移交队长（队长离开队伍时把队长位交给剩余成员）。</summary>
    ValueTask TryTransferPartyLeaderAsync(Guid partyId, Guid leaderId, CancellationToken cancellationToken = default);

    /// <summary>切换本人队伍就绪标记（party.ready/unready）；玩家不在队伍中返回 false。</summary>
    ValueTask<bool> TrySetPartyMemberReadyAsync(Guid playerId, bool ready, CancellationToken cancellationToken = default);

    /// <summary>
    /// 调整开启槽位（party.slots，队长专用）：2~5 且不得低于当前队员数，
    /// 条件不满足返回 false；行锁内复核保证与并发加入/退出互斥。
    /// </summary>
    ValueTask<bool> TryUpdatePartyOpenSlotsAsync(Guid partyId, int openSlots, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>按会话 ID 读取组队讨伐会话；不存在返回 null（含已散场行）。</summary>
    ValueTask<PartySessionRecord?> GetPartySessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>读取队伍当前活跃讨伐会话（ended_at IS NULL）；无活跃会话返回 null。</summary>
    ValueTask<PartySessionRecord?> GetActivePartySessionAsync(Guid partyId, CancellationToken cancellationToken = default);

    /// <summary>列出全部未散场的讨伐会话；常驻成员对账与全员离线批算的遍历源。</summary>
    ValueTask<IReadOnlyList<PartySessionRecord>> ListActivePartySessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>批量读取玩家投影节点（game.player_projection.current_node_id，离线成员的位置口径）；无投影行的玩家缺席。</summary>
    ValueTask<IReadOnlyDictionary<Guid, string>> GetPlayerNodeMapAsync(IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// 48h 离线逐出（总纲 §21.13）：删除离线锚点 offline_since 早于 cutoff 且非在线标记的成员行，
    /// 返回被逐出的成员列表供逐队仲裁（移交队长/空队解散/散场）。
    /// </summary>
    ValueTask<IReadOnlyList<PartyMemberEviction>> DeleteInactivePartyMembersAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计当前离线挂机修士人数（offline_since >= cutoff 且 presence &lt;&gt; 'suspended'），并排除已在线的玩家集合。
    /// </summary>
    ValueTask<int> CountOfflineIdlePlayersAsync(DateTimeOffset cutoff, IReadOnlyCollection<Guid> onlinePlayerIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计当前离线修士总人数与48小时离线挂机人数（排除当前在线与被封禁修士）。
    /// </summary>
    ValueTask<OfflinePlayerCounts> CountOfflinePlayersAsync(DateTimeOffset cutoff, IReadOnlyCollection<Guid> onlinePlayerIds, CancellationToken cancellationToken = default);
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
