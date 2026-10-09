using System.Text.Json;
using Domain.GameData;
using Protocol;

namespace Server.Clean.Social;

/// <summary>
/// 好友与队伍业务运行时（单例）。
/// 设计要点：
/// 1. 好友与队伍不属于玩家聚合状态，因此不进入玩家级单写者门禁，避免跨玩家互相阻塞；
/// 2. 所有状态变更落 PostgreSQL 后，向受影响玩家推送完整 <see cref="SocialStateDto"/>；
/// 3. 在线存在感变化（上线/下线/换节点）会触发相关玩家的社交状态刷新（好友在线状态与附近修士列表）；
/// 4. 队伍档案/匹配/大厅是招募标签层（docs/design/总纲/21）：身价裁决与并发加队由服务端权威完成，
///    加队满员判定在 <see cref="ISocialRepository.TryJoinPartyAsync"/> 的行锁事务内收敛（open_slots 口径）；
/// 5. 组队讨伐（总纲 §21.13）：每次队伍状态变化后经 <see cref="ArbitratePartySessionAsync"/> 仲裁——
///    满槽+全员就绪+全员位于目标节点自动开打；任一条件失效或解散即写终态并逐成员收尾队首绑定。
///    会话规格（冻结面板/遭遇/种子）随 spec jsonb 持久化；成员行动入队走玩家运行时单写者边界，
///    离线成员强制装载变更后即卸落盘，装载对账与重启扫尾兜底崩溃漏网。
/// </summary>
public sealed class SocialRuntime
{
    /// <summary>队伍人数上限（含队长）。</summary>
    public const int MaxPartyMembers = 5;
    /// <summary>组队讨伐开启槽位下限（总纲 §21.13，与领域规格 2~5 一致）。</summary>
    public const int MinOpenSlots = 2;
    /// <summary>组队大厅各类列表的最大返回条数。</summary>
    public const int LobbyListLimit = 50;

    /// <summary>实例内推送的最大并行构建数：限制并发占用的数据库连接数。</summary>
    private const int MaxNotifyParallelism = 4;
    /// <summary>队伍名称长度上限（字符）。</summary>
    private const int MaxPartyNameLength = 32;

    private readonly ISocialRepository _repository;
    private readonly OnlinePresenceRegistry _presence;
    private readonly PartyValuationService _valuation;
    private readonly ChatRuntime _chat;
    private readonly ILogger<SocialRuntime> _logger;
    /// <summary>配表已知世界节点 ID 集合：party profile 的 nodeId 合法性判定。</summary>
    private readonly HashSet<string> _nodeIds;
    /// <summary>游戏配表：会话开战时的遭遇池钉死与成员面板冻结采集。</summary>
    private readonly GameDataCatalog _gameData;
    /// <summary>玩家运行时（组队讨伐成员变更通道）；单测装配可为 null 时仲裁退化跳过。</summary>
    private readonly IPlayerRuntime? _players;
    /// <summary>模拟时钟：会话开打/散场时刻与玩家推进同时间轴（确定性基准模式对齐）。</summary>
    private readonly IPlayerSimulationClock _clock;
    /// <summary>组队讨伐的每队伍裁决门：开打/散场仲裁串行化，跨实例竞态由 DB 唯一索引收敛。</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _partyGates = new();
    /// <summary>实例级通知并发门：跨并发存在感触发共享 4 个实际通知槽位。</summary>
    private readonly SemaphoreSlim _notifyGate = new(MaxNotifyParallelism, MaxNotifyParallelism);

    /// <summary>构造并接管在线存在感变化订阅。</summary>
    /// <param name="repository">好友/队伍持久化边界。</param>
    /// <param name="presence">在线存在感注册表。</param>
    /// <param name="valuation">身价估值服务。</param>
    /// <param name="chat">传音运行时（party.share 复用频道作用域与限频）。</param>
    /// <param name="catalog">游戏配表（节点合法性校验、世界遭遇池与成员面板采集）。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="players">玩家运行时适配器（组队讨伐开打/散场经单写者边界改成员行动）；为 null 时仲裁退化不动作。</param>
    /// <param name="simulationClock">模拟时钟；缺省按环境装配（与玩家运行时同口径）。</param>
    public SocialRuntime(
        ISocialRepository repository,
        OnlinePresenceRegistry presence,
        PartyValuationService valuation,
        ChatRuntime chat,
        GameDataCatalog catalog,
        ILogger<SocialRuntime> logger,
        IPlayerRuntime? players = null,
        IPlayerSimulationClock? simulationClock = null)
    {
        _repository = repository;
        _presence = presence;
        _valuation = valuation;
        _chat = chat;
        _logger = logger;
        _gameData = catalog;
        _players = players;
        _clock = simulationClock ?? PlayerSimulationClock.FromEnvironment();
        _nodeIds = catalog.WorldGraph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        _presence.PresenceChanged += OnPresenceChangedAsync;
        _presence.NodeChanged += OnNodeChangedAsync;
    }

    /// <summary>某玩家社交状态发生变化；订阅者按玩家过滤后下发。</summary>
    public event Func<Guid, ValueTask>? StateChanged;

    /// <summary>读取玩家当前队伍 ID（供聊天频道作用域解析）。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>队伍 ID 或 null。</returns>
    public ValueTask<Guid?> GetPartyIdAsync(Guid playerId, CancellationToken cancellationToken = default) =>
        _repository.GetPartyIdAsync(playerId, cancellationToken);

    /// <summary>读取队伍与成员（供队伍频道广播收件人解析）。</summary>
    /// <param name="partyId">队伍 ID。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>队伍记录或 null。</returns>
    public ValueTask<PartyRecord?> GetPartyAsync(Guid partyId, CancellationToken cancellationToken = default) =>
        _repository.GetPartyAsync(partyId, cancellationToken);

    /// <summary>构建玩家当前完整社交状态视图。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="requestId">回执关联的社交指令请求 ID；主动推送传 null。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>社交状态载荷。</returns>
    public async ValueTask<SocialStateDto> BuildStateAsync(Guid playerId, string? requestId, CancellationToken cancellationToken = default)
    {
        // 持久化视图打包成单连接一次往返（NpgsqlBatch），替换原先 6 次串行查询；
        // 本方法无副作用且只依赖 playerId，可由握手/扇出等多调用方安全并行调用。
        var snapshot = await _repository.LoadStateSnapshotAsync(playerId, cancellationToken);
        var party = snapshot.Party;
        var invitation = snapshot.Invitation;

        // 队伍成员身价走展示缓存口径（≤30s），推送风暴下摊薄估值开销。
        IReadOnlyDictionary<Guid, double> valuations = new Dictionary<Guid, double>();
        if (party is { Members.Count: > 0 })
            valuations = await _valuation.ComputeDisplayAsync(
                [.. party.Members.Select(member => member.PlayerId)], cancellationToken);
        var leaderName = party?.Members.FirstOrDefault(member => member.PlayerId == party.LeaderId)?.DisplayName;

        return new SocialStateDto(
            requestId,
            [.. snapshot.Friends.Select(friend => new FriendDto(friend.FriendId.ToString("D"), friend.DisplayName, _presence.IsOnline(friend.FriendId)))],
            [.. snapshot.IncomingRequests.Select(ToRequestDto)],
            [.. snapshot.OutgoingRequests.Select(ToRequestDto)],
            party is null
                ? null
                : new PartyDto(
                    party.PartyId.ToString("D"),
                    party.LeaderId.ToString("D"),
                    DisplayName(party.Profile, leaderName),
                    party.Profile.ActivityKind,
                    party.Profile.NodeId,
                    party.Profile.MinValuation,
                    party.Profile.IsPublic,
                    [.. party.Members.Select(member => new PartyMemberDto(
                        member.PlayerId.ToString("D"), member.DisplayName,
                        _presence.IsOnline(member.PlayerId),
                        valuations.TryGetValue(member.PlayerId, out var value) ? value : 0,
                        member.Ready))],
                    party.OpenSlots,
                    snapshot.PartySession is { } session
                        ? new PartySessionDto(session.SessionId, session.EncounterId, session.NodeId,
                            [.. session.MemberIds.Select(id => id.ToString("D"))],
                            session.StartedAt.ToUnixTimeMilliseconds())
                        : null),
            invitation is null
                ? null
                : new PartyInvitationDto(invitation.PartyId.ToString("D"), invitation.InviterId.ToString("D"), invitation.InviterName, invitation.PartyName),
            _presence.SnapshotAtNode(_presence.GetNodeId(playerId)),
            snapshot.Seeker is null
                ? null
                : new PartySeekerDto(
                    snapshot.Seeker.ActivityKind, snapshot.Seeker.NodeId, snapshot.Seeker.MinValuation,
                    snapshot.Seeker.CreatedAt.ToUnixTimeMilliseconds()));
    }

    /// <summary>
    /// 构建组队大厅视图：公开队伍与匹配中散人，按筛选条件过滤。
    /// minValuation 语义：队伍按成员最低身价、散人按本人身价过滤。
    /// </summary>
    /// <param name="playerId">请求玩家 ID。</param>
    /// <param name="request">大厅筛选请求。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>大厅载荷（含请求者本人身价）。</returns>
    public async ValueTask<PartyLobbyDto> BuildLobbyAsync(Guid playerId, PartyLobbyRequestDto request, CancellationToken cancellationToken = default)
    {
        // 非法筛选收敛为「不限」，大厅是只读视图不做拒绝语义。
        var activity = PartyActivityKinds.IsSupported(request.ActivityKind) ? request.ActivityKind! : PartyActivityKinds.Any;
        var nodeId = string.IsNullOrWhiteSpace(request.NodeId) ? string.Empty : request.NodeId.Trim();
        var minValuation = request.MinValuation is { } floor && double.IsFinite(floor) ? Math.Max(0, floor) : 0;

        var parties = await _repository.ListPublicPartiesAsync(activity, nodeId, LobbyListLimit, cancellationToken);
        var seekers = await _repository.ListPartySeekersAsync(activity, nodeId, LobbyListLimit, cancellationToken);

        var subjectIds = new HashSet<Guid> { playerId };
        foreach (var party in parties)
            foreach (var member in party.Members)
                subjectIds.Add(member.PlayerId);
        foreach (var seeker in seekers)
            subjectIds.Add(seeker.PlayerId);
        var valuations = await _valuation.ComputeDisplayAsync(subjectIds, cancellationToken);

        var entries = new List<PartyLobbyEntryDto>(parties.Count);
        foreach (var party in parties)
        {
            if (party.Members.Count == 0) continue;
            var memberDtos = new List<PartyLobbyMemberDto>(party.Members.Count);
            var minMemberValuation = double.MaxValue;
            foreach (var member in party.Members)
            {
                var value = valuations.TryGetValue(member.PlayerId, out var found) ? found : 0;
                if (value < minMemberValuation) minMemberValuation = value;
                memberDtos.Add(new PartyLobbyMemberDto(
                    member.PlayerId.ToString("D"), member.DisplayName,
                    _presence.IsOnline(member.PlayerId), value));
            }
            if (minMemberValuation < minValuation) continue;
            entries.Add(new PartyLobbyEntryDto(
                party.PartyId.ToString("D"),
                DisplayName(party.Profile, party.Members.FirstOrDefault(member => member.PlayerId == party.LeaderId)?.DisplayName),
                party.Profile.ActivityKind, party.Profile.NodeId,
                party.LeaderId.ToString("D"),
                party.Members.FirstOrDefault(member => member.PlayerId == party.LeaderId)?.DisplayName ?? string.Empty,
                party.Profile.MinValuation,
                party.CreatedAt.ToUnixTimeMilliseconds(),
                memberDtos));
        }

        var seekerDtos = new List<PartyLobbySeekerDto>(seekers.Count);
        foreach (var seeker in seekers)
        {
            var value = valuations.TryGetValue(seeker.PlayerId, out var found) ? found : 0;
            if (value < minValuation) continue;
            seekerDtos.Add(new PartyLobbySeekerDto(
                seeker.PlayerId.ToString("D"), seeker.DisplayName,
                _presence.IsOnline(seeker.PlayerId), value,
                seeker.ActivityKind, seeker.NodeId, seeker.MinValuation,
                seeker.CreatedAt.ToUnixTimeMilliseconds()));
        }

        return new PartyLobbyDto(
            request.RequestId,
            valuations.TryGetValue(playerId, out var mine) ? mine : 0,
            entries,
            seekerDtos);
    }

    /// <summary>执行一条社交指令。</summary>
    /// <param name="playerId">发起玩家 ID。</param>
    /// <param name="action">社交指令载荷。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>执行结局；失败时携带原因码。</returns>
    public async ValueTask<SocialOutcome> ApplyAsync(Guid playerId, SocialActionDto action, CancellationToken cancellationToken = default)
    {
        if (!SocialActions.IsSupported(action.Action)) return new SocialOutcome(SocialFailure.InvalidAction);
        var now = DateTimeOffset.UtcNow;
        switch (action.Action)
        {
            case SocialActions.FriendRequest:
            {
                var targetId = await ResolveTargetAsync(action, cancellationToken);
                if (targetId is null) return new SocialOutcome(SocialFailure.TargetNotFound);
                if (targetId == playerId) return new SocialOutcome(SocialFailure.SelfTarget);
                if (await _repository.AreFriendsAsync(playerId, targetId.Value, cancellationToken))
                    return new SocialOutcome(SocialFailure.AlreadyFriends);
                if (!await _repository.TryCreateFriendRequestAsync(Guid.NewGuid(), playerId, targetId.Value, now, cancellationToken))
                    return new SocialOutcome(SocialFailure.RequestExists);
                await NotifyAsync([playerId, targetId.Value], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.FriendAccept:
            {
                var targetId = ResolveTargetId(action);
                if (targetId is null) return new SocialOutcome(SocialFailure.TargetNotFound);
                if (!await _repository.TryAcceptFriendRequestAsync(targetId.Value, playerId, now, cancellationToken))
                    return new SocialOutcome(SocialFailure.RequestNotFound);
                await NotifyAsync([playerId, targetId.Value], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.FriendReject:
            {
                var targetId = ResolveTargetId(action);
                if (targetId is null) return new SocialOutcome(SocialFailure.TargetNotFound);
                if (!await _repository.TryRejectFriendRequestAsync(targetId.Value, playerId, now, cancellationToken))
                    return new SocialOutcome(SocialFailure.RequestNotFound);
                await NotifyAsync([playerId, targetId.Value], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.FriendRemove:
            {
                var targetId = ResolveTargetId(action);
                if (targetId is null) return new SocialOutcome(SocialFailure.TargetNotFound);
                if (!await _repository.TryRemoveFriendAsync(playerId, targetId.Value, cancellationToken))
                    return new SocialOutcome(SocialFailure.NotFriends);
                await NotifyAsync([playerId, targetId.Value], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyCreate:
            {
                if (await _repository.GetPartyIdAsync(playerId, cancellationToken) is not null)
                    return new SocialOutcome(SocialFailure.AlreadyInParty);
                if (!TryBuildProfile(action.Party, PartyProfileRecord.Default, full: true, out var profile))
                    return new SocialOutcome(SocialFailure.InvalidPartyProfile);
                // 自建队伍任队长；匹配登记与旧邀请随入队一并清除（创建路径无成员冲突）。
                await _repository.TryClearPartySeekerAsync(playerId, cancellationToken);
                await _repository.TryClearPartyInvitationAsync(playerId, cancellationToken);
                var partyId = await _repository.CreatePartyAsync(playerId, profile, now, cancellationToken);
                await NotifyAsync([playerId], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyInvite:
            {
                var targetId = ResolveTargetId(action);
                if (targetId is null) return new SocialOutcome(SocialFailure.TargetNotFound);
                if (targetId == playerId) return new SocialOutcome(SocialFailure.SelfTarget);
                // 未组队时以默认档案自建队伍并任队长：好友列表的「邀请入队」不经大厅表单也可直达。
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken)
                    ?? await _repository.CreatePartyAsync(playerId, PartyProfileRecord.Default, now, cancellationToken);
                var party = await _repository.GetPartyAsync(partyId, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.NotInParty);
                if (party.LeaderId != playerId) return new SocialOutcome(SocialFailure.NotLeader);
                if (party.Members.Count >= party.OpenSlots) return new SocialOutcome(SocialFailure.PartyFull);
                if (await _repository.GetPartyIdAsync(targetId.Value, cancellationToken) is not null)
                    return new SocialOutcome(SocialFailure.TargetAlreadyInParty);
                await _repository.UpsertPartyInvitationAsync(partyId, targetId.Value, playerId, now, cancellationToken);
                await NotifyAsync([playerId, targetId.Value], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyAccept:
            {
                var invitation = await _repository.GetPartyInvitationAsync(playerId, cancellationToken);
                if (invitation is null) return new SocialOutcome(SocialFailure.InvitationNotFound);
                if (await _repository.GetPartyIdAsync(playerId, cancellationToken) is not null)
                {
                    await _repository.TryClearPartyInvitationAsync(playerId, cancellationToken);
                    return new SocialOutcome(SocialFailure.AlreadyInParty);
                }
                var party = await _repository.GetPartyAsync(invitation.PartyId, cancellationToken);
                if (party is null)
                {
                    await _repository.TryClearPartyInvitationAsync(playerId, cancellationToken);
                    return new SocialOutcome(SocialFailure.PartyNotFound);
                }
                if (party.Profile.MinValuation > 0
                    && await _valuation.ComputeAsync(playerId, cancellationToken) < party.Profile.MinValuation)
                    return new SocialOutcome(SocialFailure.ValuationTooLow);
                var join = await _repository.TryJoinPartyAsync(invitation.PartyId, playerId, now, cancellationToken);
                if (join != PartyJoinResult.Joined)
                {
                    if (join == PartyJoinResult.NotFound) await _repository.TryClearPartyInvitationAsync(playerId, cancellationToken);
                    return new SocialOutcome(join switch
                    {
                        PartyJoinResult.NotFound => SocialFailure.PartyNotFound,
                        PartyJoinResult.Full => SocialFailure.PartyFull,
                        _ => SocialFailure.AlreadyInParty
                    });
                }
                // 成员加入是讨伐仲裁触发点（总纲 §21.13）：阵容变化先裁决再广播。
                await ArbitratePartySessionAsync(invitation.PartyId, "member-joined", cancellationToken);
                await NotifyAsync([.. party.Members.Select(member => member.PlayerId), playerId, invitation.InviterId], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyDecline:
            {
                if (!await _repository.TryClearPartyInvitationAsync(playerId, cancellationToken))
                    return new SocialOutcome(SocialFailure.InvitationNotFound);
                await NotifyAsync([playerId], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyJoin:
            {
                if (!Guid.TryParse(action.PartyId, out var partyId) || partyId == Guid.Empty)
                    return new SocialOutcome(SocialFailure.PartyNotFound);
                if (await _repository.GetPartyIdAsync(playerId, cancellationToken) is not null)
                    return new SocialOutcome(SocialFailure.AlreadyInParty);
                var party = await _repository.GetPartyAsync(partyId, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.PartyNotFound);
                // 公开队伍大厅加入与不公开队伍链接点击共用此校验：满员与身价门槛服务端说了算。
                if (party.Profile.MinValuation > 0
                    && await _valuation.ComputeAsync(playerId, cancellationToken) < party.Profile.MinValuation)
                    return new SocialOutcome(SocialFailure.ValuationTooLow);
                var join = await _repository.TryJoinPartyAsync(partyId, playerId, now, cancellationToken);
                if (join != PartyJoinResult.Joined)
                {
                    return new SocialOutcome(join switch
                    {
                        PartyJoinResult.NotFound => SocialFailure.PartyNotFound,
                        PartyJoinResult.Full => SocialFailure.PartyFull,
                        _ => SocialFailure.AlreadyInParty
                    });
                }
                // 成员加入是讨伐仲裁触发点（总纲 §21.13）：阵容变化先裁决再广播。
                await ArbitratePartySessionAsync(partyId, "member-joined", cancellationToken);
                await NotifyAsync([.. party.Members.Select(member => member.PlayerId), playerId], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyUpdate:
            {
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken);
                if (partyId is null) return new SocialOutcome(SocialFailure.NotInParty);
                var party = await _repository.GetPartyAsync(partyId.Value, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.NotInParty);
                if (party.LeaderId != playerId) return new SocialOutcome(SocialFailure.NotLeader);
                if (!TryBuildProfile(action.Party, party.Profile, full: false, out var profile))
                    return new SocialOutcome(SocialFailure.InvalidPartyProfile);
                if (!await _repository.TryUpdatePartyProfileAsync(partyId.Value, profile, now, cancellationToken))
                    return new SocialOutcome(SocialFailure.PartyNotFound);
                // 目标节点/目标类型变化是讨伐仲裁触发点：会话成立性随新档案重评。
                await ArbitratePartySessionAsync(partyId.Value, "profile-changed", cancellationToken);
                await NotifyAsync([.. party.Members.Select(member => member.PlayerId)], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyKick:
            {
                var targetId = ResolveTargetId(action);
                if (targetId is null) return new SocialOutcome(SocialFailure.TargetNotFound);
                if (targetId == playerId) return new SocialOutcome(SocialFailure.SelfTarget);
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken);
                if (partyId is null) return new SocialOutcome(SocialFailure.NotInParty);
                var party = await _repository.GetPartyAsync(partyId.Value, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.NotInParty);
                if (party.LeaderId != playerId) return new SocialOutcome(SocialFailure.NotLeader);
                if (!await _repository.TryKickPartyMemberAsync(partyId.Value, targetId.Value, cancellationToken))
                    return new SocialOutcome(SocialFailure.TargetNotFound);
                // 踢出使阵容收缩：活跃会话条件必然失效，先仲裁散场再广播。
                await ArbitratePartySessionAsync(partyId.Value, "member-kicked", cancellationToken);
                await NotifyAsync([.. party.Members.Select(member => member.PlayerId), targetId.Value], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyDisband:
            {
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken);
                if (partyId is null) return new SocialOutcome(SocialFailure.NotInParty);
                var party = await _repository.GetPartyAsync(partyId.Value, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.NotInParty);
                if (party.LeaderId != playerId) return new SocialOutcome(SocialFailure.NotLeader);
                // 先对活跃讨伐会话做权威散场（spec 名册含当前成员），再删 parties 行——
                // 顺序不可颠倒：行删除后 spec 名册不可考，成员队首绑定只能等装载对账兜底。
                await ArbitratePartySessionAsync(partyId.Value, "party-disbanded", cancellationToken, forceDissolve: true);
                // 成员/邀请随 ON DELETE CASCADE 清除，聊天卡片链接置空失效。
                var former = await _repository.DisbandPartyAsync(partyId.Value, cancellationToken);
                if (former is null) return new SocialOutcome(SocialFailure.PartyNotFound);
                await NotifyAsync([.. former.Select(member => member.PlayerId)], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyMatchSet:
            {
                if (await _repository.GetPartyIdAsync(playerId, cancellationToken) is not null)
                    return new SocialOutcome(SocialFailure.AlreadyInParty);
                if (!TryBuildSeekerCriteria(action.Party, out var activity, out var nodeId, out var minValuation))
                    return new SocialOutcome(SocialFailure.InvalidPartyProfile);
                await _repository.UpsertPartySeekerAsync(playerId, activity, nodeId, minValuation, now, cancellationToken);
                // 自动并入：公开且未满员、目标匹配、双向身价门槛满足的队伍；多命中取人多者、再取创建最早者。
                var myValuation = await _valuation.ComputeAsync(playerId, cancellationToken);
                var candidates = await _repository.ListPublicPartiesAsync(activity, nodeId, LobbyListLimit, cancellationToken);
                PartyRecord? best = null;
                foreach (var candidate in candidates)
                {
                    if (candidate.Members.Count >= candidate.OpenSlots) continue;
                    if (candidate.Profile.MinValuation > myValuation) continue;
                    if (minValuation > 0)
                    {
                        // 我方申报的队友门槛：每名在队成员身价都要满足（实时口径）。
                        var memberIds = candidate.Members.Select(member => member.PlayerId).ToArray();
                        var meetsAll = true;
                        foreach (var memberId in memberIds)
                        {
                            if (await _valuation.ComputeAsync(memberId, cancellationToken) < minValuation)
                            {
                                meetsAll = false;
                                break;
                            }
                        }
                        if (!meetsAll) continue;
                    }
                    if (best is null
                        || candidate.Members.Count > best.Members.Count
                        || (candidate.Members.Count == best.Members.Count && candidate.CreatedAt < best.CreatedAt))
                    {
                        best = candidate;
                    }
                }
                if (best is not null)
                {
                    var join = await _repository.TryJoinPartyAsync(best.PartyId, playerId, now, cancellationToken);
                    if (join == PartyJoinResult.Joined)
                    {
                        // 匹配并入改变阵容：并入后仲裁一次（新成员未就绪时只会散场评估）。
                        await ArbitratePartySessionAsync(best.PartyId, "member-joined", cancellationToken);
                        await NotifyAsync([.. best.Members.Select(member => member.PlayerId), playerId], playerId);
                        return SocialOutcome.Ok;
                    }
                    // 并入竞争失败（满员/他队截胡）：登记已写入，保持匹配中。
                    if (join == PartyJoinResult.AlreadyInParty) return new SocialOutcome(SocialFailure.AlreadyInParty);
                }
                await NotifyAsync([playerId], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyMatchClear:
            {
                if (!await _repository.TryClearPartySeekerAsync(playerId, cancellationToken))
                    return new SocialOutcome(SocialFailure.SeekingNotFound);
                await NotifyAsync([playerId], playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyShare:
            {
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken);
                if (partyId is null) return new SocialOutcome(SocialFailure.NotInParty);
                var party = await _repository.GetPartyAsync(partyId.Value, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.NotInParty);
                var senderName = party.Members.FirstOrDefault(member => member.PlayerId == playerId)?.DisplayName ?? string.Empty;
                var share = await _chat.SendPartyShareAsync(
                    playerId, senderName, action.Channel, ResolveTargetId(action), party, cancellationToken);
                if (share.Status != ChatSendStatus.Ok)
                {
                    return new SocialOutcome(share.Status == ChatSendStatus.RateLimited
                        ? SocialFailure.RateLimited
                        : SocialFailure.ChannelUnavailable);
                }
                // 队伍卡片发言不改变社交状态，但仍回执一帧供前端确认。
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyLeave:
            {
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken);
                if (partyId is null) return new SocialOutcome(SocialFailure.NotInParty);
                var party = await _repository.GetPartyAsync(partyId.Value, cancellationToken);
                if (!await _repository.TryRemovePartyMemberAsync(playerId, cancellationToken))
                    return new SocialOutcome(SocialFailure.NotInParty);
                var affected = new List<Guid> { playerId };
                if (party is not null)
                {
                    affected.AddRange(party.Members.Select(member => member.PlayerId));
                    // 队长离开时把队长位交给剩余成员中最早加入者（members 已按加入时间排序）。
                    if (party.LeaderId == playerId)
                    {
                        var next = party.Members.FirstOrDefault(member => member.PlayerId != playerId);
                        if (next is not null)
                            await _repository.TryTransferPartyLeaderAsync(partyId.Value, next.PlayerId, cancellationToken);
                    }
                }
                await _repository.DeletePartyIfEmptyAsync(partyId.Value, cancellationToken);
                // 离队使活跃会话的冻结名册失效：先仲裁散场，再广播新旧成员。
                await ArbitratePartySessionAsync(partyId.Value, "member-left", cancellationToken);
                await NotifyAsync(affected, playerId);
                return SocialOutcome.Ok;
            }
            case SocialActions.PartyReady or SocialActions.PartyUnready:
            {
                // 就绪标记是成员本人的会话意愿开关（总纲 §21.13）；重复置位幂等。
                var ready = action.Action == SocialActions.PartyReady;
                if (!await _repository.TrySetPartyMemberReadyAsync(playerId, ready, cancellationToken))
                    return new SocialOutcome(SocialFailure.NotInParty);
                if (await _repository.GetPartyIdAsync(playerId, cancellationToken) is { } ownedPartyId)
                {
                    await ArbitratePartySessionAsync(ownedPartyId, ready ? "member-ready" : "member-unready", cancellationToken);
                    if (await _repository.GetPartyAsync(ownedPartyId, cancellationToken) is { } refreshed)
                        await NotifyAsync([.. refreshed.Members.Select(member => member.PlayerId)], playerId);
                }
                return SocialOutcome.Ok;
            }
            case SocialActions.PartySlots:
            {
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken);
                if (partyId is null) return new SocialOutcome(SocialFailure.NotInParty);
                var party = await _repository.GetPartyAsync(partyId.Value, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.NotInParty);
                if (party.LeaderId != playerId) return new SocialOutcome(SocialFailure.NotLeader);
                // 开启槽位 2~5 且不得低于当前队员数（收窄不逐人，只收紧后续加入与开战判定）。
                if (action.Party?.OpenSlots is not { } openSlots
                    || openSlots < MinOpenSlots || openSlots > MaxPartyMembers
                    || openSlots < party.Members.Count)
                    return new SocialOutcome(SocialFailure.InvalidPartyProfile);
                if (!await _repository.TryUpdatePartyOpenSlotsAsync(partyId.Value, openSlots, now, cancellationToken))
                    return new SocialOutcome(SocialFailure.PartyNotFound);
                // 槽位收窄使「满槽」失效直接散场；放开后若仍满槽也可能满足开战——统一重裁。
                await ArbitratePartySessionAsync(partyId.Value, "open-slots-changed", cancellationToken);
                await NotifyAsync([.. party.Members.Select(member => member.PlayerId)], playerId);
                return SocialOutcome.Ok;
            }
            default:
                return new SocialOutcome(SocialFailure.InvalidAction);
        }
    }

    /// <summary>主动向单个玩家推送最新社交状态（握手完成、节点变化等场景）。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public ValueTask PushAsync(Guid playerId, CancellationToken cancellationToken = default) =>
        NotifyAsync([playerId]);

    private async ValueTask OnPresenceChangedAsync(IReadOnlyList<Guid> affected)
    {
        try
        {
            await NotifyAsync(affected);
        }
        catch (Exception exception)
        {
            // 存在感推送失败不能影响上线/下线主流程：仅记录，由下一次状态变更或重连补偿。
            _logger.LogError(exception, "在线存在感变化推送社交状态失败。");
        }
    }

    /// <summary>
    /// 档案补丁归一化：full=true（创建）时未提供字段回落默认值，full=false（更新）时保留基线字段。
    /// 队伍名允许空串（展示层回落「{队长道号}的小队」），非空名称去首尾空白后限长 32。
    /// </summary>
    private bool TryBuildProfile(PartyProfileDto? patch, PartyProfileRecord baseline, bool full, out PartyProfileRecord result)
    {
        result = baseline;
        var name = baseline.Name;
        if (patch?.Name is { } rawName)
        {
            name = rawName.Trim();
            if (name.Length > MaxPartyNameLength) return false;
        }
        else if (full) name = string.Empty;

        var activity = baseline.ActivityKind;
        if (patch?.ActivityKind is { } rawActivity)
        {
            activity = rawActivity.Trim();
            if (!PartyActivityKinds.IsSupported(activity)) return false;
        }
        else if (full) activity = PartyActivityKinds.Any;

        var nodeId = baseline.NodeId;
        if (patch?.NodeId is { } rawNode)
        {
            nodeId = rawNode.Trim();
            if (nodeId.Length > 0 && !_nodeIds.Contains(nodeId)) return false;
        }
        else if (full) nodeId = string.Empty;

        var minValuation = baseline.MinValuation;
        if (patch?.MinValuation is { } rawMin)
        {
            if (!double.IsFinite(rawMin) || rawMin < 0) return false;
            minValuation = rawMin;
        }
        else if (full) minValuation = 0;

        var isPublic = baseline.IsPublic;
        if (patch?.IsPublic is { } rawPublic) isPublic = rawPublic;
        else if (full) isPublic = false;

        result = new PartyProfileRecord(name, activity, nodeId, minValuation, isPublic);
        return true;
    }

    /// <summary>匹配登记条件解析：只取 activityKind/nodeId/minValuation 三个招募条件字段。</summary>
    private bool TryBuildSeekerCriteria(PartyProfileDto? patch, out string activity, out string nodeId, out double minValuation)
    {
        activity = PartyActivityKinds.Any;
        nodeId = string.Empty;
        minValuation = 0;
        if (patch?.ActivityKind is { } rawActivity)
        {
            activity = rawActivity.Trim();
            if (!PartyActivityKinds.IsSupported(activity)) return false;
        }
        if (patch?.NodeId is { } rawNode)
        {
            nodeId = rawNode.Trim();
            if (nodeId.Length > 0 && !_nodeIds.Contains(nodeId)) return false;
        }
        if (patch?.MinValuation is { } rawMin)
        {
            if (!double.IsFinite(rawMin) || rawMin < 0) return false;
            minValuation = rawMin;
        }
        return true;
    }

    /// <summary>队伍显示名：档案名称为空时回落「{队长道号}的小队」。</summary>
    private static string DisplayName(PartyProfileRecord profile, string? leaderName) =>
        profile.Name.Length > 0 ? profile.Name : $"{leaderName ?? string.Empty}的小队";

    /// <summary>
    /// 向受影响玩家推送最新社交状态。发起指令的玩家由 WebSocket 端点用带 requestId 的回执帧单独回复，
    /// 因此这里排除该玩家，避免同一状态被推送两次。
    /// </summary>
    private async ValueTask NotifyAsync(IReadOnlyCollection<Guid> playerIds, Guid? exclude = null)
    {
        var handler = StateChanged;
        if (handler is null || playerIds.Count == 0) return;
        // 固定本次订阅快照；直接调用多播异步委托只能得到最后一个订阅的 ValueTask。
        var subscribers = handler.GetInvocationList();
        // 收件人去重；离线玩家没有会话，构建状态只会白跑数据库查询。
        var recipients = new List<Guid>(playerIds.Count);
        var seen = new HashSet<Guid>();
        foreach (var playerId in playerIds)
        {
            if (playerId == exclude || !seen.Add(playerId)) continue;
            if (!_presence.IsOnline(playerId)) continue;
            recipients.Add(playerId);
        }
        if (recipients.Count == 0) return;
        if (recipients.Count == 1)
        {
            await NotifyOneAsync(recipients[0]);
            return;
        }
        // 不同收件人并行；完整状态与存在感投影、单人推送与多次扇出都共享实例上限 4。
        var pending = new List<Task>(recipients.Count);
        foreach (var playerId in recipients)
            pending.Add(NotifyOneAsync(playerId));
        await Task.WhenAll(pending);

        async Task NotifyOneAsync(Guid playerId)
        {
            // 仅持有实际通知槽位，不包存在感/节点仲裁入口，避免仲裁的嵌套通知再次获取同一槽位。
            await _notifyGate.WaitAsync();
            try
            {
                foreach (Func<Guid, ValueTask> subscriber in subscribers)
                    await subscriber(playerId);
            }
            finally
            {
                _notifyGate.Release();
            }
        }
    }

    private async ValueTask<Guid?> ResolveTargetAsync(SocialActionDto action, CancellationToken cancellationToken)
    {
        var byId = ResolveTargetId(action);
        if (byId is not null) return byId;
        if (string.IsNullOrWhiteSpace(action.TargetName)) return null;
        return await _repository.FindPlayerByNameAsync(action.TargetName, cancellationToken);
    }

    private static Guid? ResolveTargetId(SocialActionDto action) =>
        Guid.TryParse(action.TargetPlayerId, out var targetId) && targetId != Guid.Empty ? targetId : null;

    // ============================================================================
    // 组队讨伐仲裁（总纲 §21.13）：共享剧本各自推演——开打冻结全员面板+钉死遭遇，
    // 任一成立条件失效（离队/踢出/取消就绪/槽位变化/节点变化/解散/离线逐出）即散场重建。
    // ============================================================================

    /// <summary>成员节点变化触发讨伐仲裁（Unregister 不触发：离线保留席位，逐出走 48h 扫描）。</summary>
    private async ValueTask OnNodeChangedAsync(Guid playerId)
    {
        try
        {
            if (await _repository.GetPartyIdAsync(playerId, CancellationToken.None) is { } partyId)
                await ArbitratePartySessionAsync(partyId, "member-node-changed", CancellationToken.None);
        }
        catch (Exception exception)
        {
            // 仲裁失败只影响本次触发：就绪/节点等后续变更会再次进入仲裁，会话行是唯一收敛点。
            _logger.LogError(exception, "节点变化触发组队讨伐仲裁失败（玩家 {PlayerId}）。", playerId);
        }
    }

    /// <summary>
    /// 队伍讨伐会话仲裁入口：每次队伍状态变化后裁决一次。进程内经每队伍门串行；
    /// 跨实例并发由 ux_party_sessions_active 部分唯一索引与 ended_at IS NULL 条件更新收敛。
    /// </summary>
    /// <param name="partyId">待裁决队伍。</param>
    /// <param name="reason">仲裁原因标签（散场时落入 end_reason）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <param name="forceDissolve">true 时忽略成立条件强制散场（解散队伍等终局路径）。</param>
    private async ValueTask ArbitratePartySessionAsync(Guid partyId, string reason, CancellationToken cancellationToken, bool forceDissolve = false)
    {
        if (_players is null) return; // 无玩家运行时的装配（社交单测）：仲裁退化，社交状态照常落库。
        var gate = _partyGates.GetOrAdd(partyId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await ArbitratePartySessionOwnedAsync(partyId, reason, forceDissolve, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>裁决体（必须持有队伍门后调用）：条件齐备开打，条件失效散场，二者互斥且幂等。</summary>
    private async ValueTask ArbitratePartySessionOwnedAsync(Guid partyId, string reason, bool forceDissolve, CancellationToken cancellationToken)
    {
        var party = await _repository.GetPartyAsync(partyId, cancellationToken);
        var session = await _repository.GetActivePartySessionAsync(partyId, cancellationToken);
        if (party is null)
        {
            // 队伍已删但残留活跃会话（解散竞态漏网）：按名册散场收尾。
            if (session is not null)
                await DissolveSessionAsync(session, reason, cancellationToken);
            return;
        }
        if (session is not null)
        {
            // 会话存续要求名册冻结：成员变动/目标节点漂移/满槽就绪失任一条件即散场。
            var stillTarget = await ResolveReadyTargetNodeAsync(party, cancellationToken);
            var rosterIntact = session.MemberIds.Count == party.Members.Count
                && party.Members.All(member => session.MemberIds.Contains(member.PlayerId));
            if (forceDissolve
                || stillTarget is null
                || !rosterIntact
                || !string.Equals(stillTarget, session.NodeId, StringComparison.Ordinal))
                await DissolveSessionAsync(session, reason, cancellationToken);
            return;
        }
        if (!forceDissolve
            && await ResolveReadyTargetNodeAsync(party, cancellationToken) is { } targetNode)
            await StartSessionAsync(party, targetNode, cancellationToken);
    }

    /// <summary>
    /// 开战条件齐备判定：成员数=开启槽位（2~5）、全员就绪、全员位于目标节点。
    /// 目标节点：档案指定优先，空串回落队长所在节点；任一成员不在目标节点即不成立。
    /// 返回成立时的目标节点 ID，不成立返回 null。
    /// </summary>
    private async ValueTask<string?> ResolveReadyTargetNodeAsync(PartyRecord party, CancellationToken cancellationToken)
    {
        if (party.Members.Count < MinOpenSlots || party.Members.Count != party.OpenSlots) return null;
        if (party.Members.Any(member => !member.Ready)) return null;
        var nodeMap = await _repository.GetPlayerNodeMapAsync(
            [.. party.Members.Select(member => member.PlayerId)], cancellationToken);
        var target = party.Profile.NodeId.Length > 0
            ? party.Profile.NodeId
            : ResolveMemberNode(party.LeaderId, nodeMap);
        if (string.IsNullOrEmpty(target)) return null;
        foreach (var member in party.Members)
            if (!string.Equals(ResolveMemberNode(member.PlayerId, nodeMap), target, StringComparison.Ordinal))
                return null;
        return target;
    }

    /// <summary>成员节点口径：已装载实体以内存态为准（快照比投影新）；未装载成员走持久化投影（迁地途中为缺席）。</summary>
    private string? ResolveMemberNode(Guid memberId, IReadOnlyDictionary<Guid, string> projections)
    {
        if (_players?.TryGetLoaded(memberId) is { } live)
            return live.State.World.EdgeId is null ? live.State.World.NodeId : null;
        return projections.TryGetValue(memberId, out var nodeId) ? nodeId : null;
    }

    /// <summary>
    /// 开打：冻结全员面板 → 钉死遭遇 → 写会话行占活跃位（并发仲裁收敛点）→ 逐成员入队 session 行动。
    /// 行先行、行动后投递：投递失败的成员由各自装载对账与看门狗兜底补发。
    /// </summary>
    private async ValueTask StartSessionAsync(PartyRecord party, string targetNode, CancellationToken cancellationToken)
    {
        var startedAt = _clock.UtcNow;
        if (!_gameData.WorldGraph.TryGetNode(targetNode, out var node) || node.EncounterIds.Count == 0)
        {
            _logger.LogWarning("组队讨伐开战放弃：目标节点 {NodeId} 不在世界图或无遭遇池（队伍 {PartyId}）。", targetNode, party.PartyId);
            return;
        }
        if (party.Profile.ActivityKind is not ("any" or "combat"))
        {
            _logger.LogInformation("行动参与开战跳过：队伍目标类型 {ActivityKind} 不是讨伐（队伍 {PartyId}）。", party.Profile.ActivityKind, party.PartyId);
            return;
        }
        var action = _gameData.Actions.All
            .Where(definition => definition.Kind == ActionKind.Combat
                && definition.ExecutionKind == ActionExecutionKind.CombatEncounter
                && (definition.AllowedNodeIds is null || definition.AllowedNodeIds.Contains(targetNode))
                && (definition.ExcludedNodeIds is null || !definition.ExcludedNodeIds.Contains(targetNode)))
            .OrderBy(definition => definition.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        if (action is null)
        {
            _logger.LogWarning("行动参与开战放弃：目标节点 {NodeId} 没有可用讨伐行动（队伍 {PartyId}）。", targetNode, party.PartyId);
            return;
        }
        var seed = Random.Shared.NextInt64(long.MaxValue);
        var encounterId = node.EncounterIds[(int)(seed % node.EncounterIds.Count)];
        var sessionId = $"action:{Guid.NewGuid():N}";

        // 逐成员装载并冻结面板（离线成员强制装载后即卸）；任一失败整局放弃，不开半个会话。
        var snapshots = new ActionParticipantSnapshot?[party.Members.Count];
        for (var i = 0; i < party.Members.Count; i++)
        {
            var member = party.Members[i];
            try
            {
                await ApplySessionMutationAsync(member.PlayerId,
                    aggregate => snapshots[i] = aggregate.CaptureActionParticipantSnapshot(_gameData),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "组队讨伐开战放弃：成员 {PlayerId} 冻结面板采集异常（队伍 {PartyId}）。", member.PlayerId, party.PartyId);
                return;
            }
            if (snapshots[i] is null)
            {
                _logger.LogError("组队讨伐开战放弃：成员 {PlayerId} 冻结面板为空（队伍 {PartyId}）。", member.PlayerId, party.PartyId);
                return;
            }
        }

        var spec = new ActionParticipationSpec(
            sessionId, encounterId, targetNode, seed,
            snapshots.Max(member => member!.SearchSpeedPoints),
            startedAt.ToUnixTimeMilliseconds() / 1000d,
            [.. snapshots.Select(member => member!)])
        {
            ActionId = action.Id
        };
        if (!spec.IsValid)
        {
            _logger.LogError("组队讨伐开战放弃：会话规格不合法（队伍 {PartyId}，会话 {SessionId}）。", party.PartyId, sessionId);
            return;
        }
        var specJson = JsonSerializer.Serialize(spec, PersistenceJson.Options);
        // 占活跃位：并发仲裁由部分唯一索引收敛，败方直接放弃（成员变更未投递，无副作用残留）。
        if (!await _repository.TryInsertPartySessionAsync(sessionId, party.PartyId, specJson, startedAt, cancellationToken))
            return;

        var undelivered = 0;
        foreach (var member in party.Members)
        {
            try
            {
                await ApplySessionMutationAsync(member.PlayerId, aggregate =>
                {
                    // 先扫净残留会话绑定（崩溃/重启遗留的异会话或非队首同名绑定），再幂等入队本会话。
                    for (var i = aggregate.Actions.Count - 1; i >= 0; i--)
                    {
                        var binding = aggregate.Actions[i].Participation;
                        if (binding is null) continue;
                        if (i == 0 && string.Equals(binding.Spec.SessionId, spec.SessionId, StringComparison.Ordinal)) continue;
                        aggregate.RemoveAction(aggregate.Actions[i].Id);
                    }
                    aggregate.StartActionParticipation(spec, startedAt, _gameData.Actions);
                }, cancellationToken);
            }
            catch (Exception exception)
            {
                undelivered++;
                _logger.LogError(exception, "组队讨伐行动投递失败：成员 {PlayerId}（会话 {SessionId}）。", member.PlayerId, sessionId);
            }
        }
        if (undelivered > 0)
            _logger.LogWarning("组队讨伐会话 {SessionId} 有 {Count} 名成员行动未投递；其装载对账将按活跃会话行补发。", sessionId, undelivered);
        await NotifyAsync([.. party.Members.Select(member => member.PlayerId)]);
        _logger.LogInformation("组队讨伐会话 {SessionId} 开打：队伍 {PartyId}，遭遇 {EncounterId}，成员 {Count} 人。",
            sessionId, party.PartyId, encounterId, party.Members.Count);
    }

    /// <summary>
    /// 散场：先写会话行终态（幂等收敛并发散场），再逐成员收尾——
    /// 队首本会话绑定写权威散场时刻（引擎推进到该时刻自然收尾），其余残留绑定一律移除。
    /// </summary>
    private async ValueTask DissolveSessionAsync(PartySessionRecord session, string reason, CancellationToken cancellationToken)
    {
        var endedAt = _clock.UtcNow;
        var endsAtUnixSeconds = endedAt.ToUnixTimeMilliseconds() / 1000d;
        await _repository.TryEndPartySessionAsync(session.SessionId, endedAt, reason, cancellationToken);
        foreach (var memberId in session.MemberIds)
        {
            try
            {
                await ApplySessionMutationAsync(memberId, aggregate =>
                {
                    for (var i = aggregate.Actions.Count - 1; i >= 0; i--)
                    {
                        var binding = aggregate.Actions[i].Participation;
                        if (binding is null) continue;
                        // 队首本会话绑定写权威散场时刻；异会话遗留与非队首残留直接移除（不在队首无推进语义）。
                        if (i == 0 && string.Equals(binding.Spec.SessionId, session.SessionId, StringComparison.Ordinal))
                            aggregate.EndActionParticipation(session.SessionId, endsAtUnixSeconds);
                        else
                            aggregate.RemoveAction(aggregate.Actions[i].Id);
                    }
                }, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "组队讨伐散场收尾失败：成员 {PlayerId}（会话 {SessionId}）。", memberId, session.SessionId);
            }
        }
        await NotifyAsync([.. session.MemberIds]);
        _logger.LogInformation("组队讨伐会话 {SessionId} 散场：{Reason}。", session.SessionId, reason);
    }

    /// <summary>
    /// 会话成员变更通道：在线/离线成员统一经玩家运行时门内装载+变更，标脏进刷盘轨道；
    /// 本会话强拉的离线实体变更后即卸并强制落盘，不留驻内存、不落「在线」假状态。
    /// </summary>
    private async ValueTask ApplySessionMutationAsync(Guid memberId, Action<PlayerAggregate> mutate, CancellationToken cancellationToken)
    {
        if (_players is null) return;
        var wasLoaded = _players.TryGetLoaded(memberId) is not null;
        await _players.MutateOwnedAsync(memberId, aggregate =>
        {
            mutate(aggregate);
            // 强制装载走统一上线路径（LoadAsync 内置置 Online），门内按注册表复位离线。
            if (!_presence.IsOnline(memberId)) aggregate.SetPresence(PresenceState.Offline);
        }, cancellationToken);
        // 仅卸载本会话强拉的实体：真在线玩家与既有驻留实体不动（变更后复核注册表收窄竞态）。
        if (!wasLoaded && !_presence.IsOnline(memberId))
            await _players.RemoveAsync(memberId, flush: true, cancellationToken);
    }

    /// <summary>
    /// 48h 离线成员逐出（看门狗调用）：逐队在队伍门内完成队长移交/空队清理/讨伐仲裁，
    /// 再向剩余成员与被逐者广播（被逐者离线时被推送层过滤，其上线对账兜底）。
    /// </summary>
    public async ValueTask EvictOfflinePartyMembersAsync(TimeSpan offlineLimit, CancellationToken cancellationToken = default)
    {
        var evicted = await _repository.DeleteInactivePartyMembersAsync(_clock.UtcNow - offlineLimit, cancellationToken);
        if (evicted.Count == 0) return;
        foreach (var group in evicted.GroupBy(item => item.PartyId))
        {
            var partyId = group.Key;
            var gate = _partyGates.GetOrAdd(partyId, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                var party = await _repository.GetPartyAsync(partyId, cancellationToken);
                if (party is null) continue;
                // 被逐出者是队长时移交最早剩余成员（与离队路径同规则）。
                if (group.Any(item => item.PlayerId == party.LeaderId))
                {
                    var next = party.Members.FirstOrDefault(member => group.All(item => item.PlayerId != member.PlayerId));
                    if (next is not null)
                        await _repository.TryTransferPartyLeaderAsync(partyId, next.PlayerId, cancellationToken);
                }
                await _repository.DeletePartyIfEmptyAsync(partyId, cancellationToken);
                await ArbitratePartySessionOwnedAsync(partyId, "member-offline-timeout", forceDissolve: false, cancellationToken);
                await NotifyAsync([.. party.Members.Select(member => member.PlayerId), .. group.Select(item => item.PlayerId)]);
                _logger.LogInformation("48h 离线逐出完成：队伍 {PartyId} 移除 {Count} 人。", partyId, group.Count());
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// 已装载玩家的队首死会话兜底收尾（看门狗调用）：散场行已写而成员绑定未终局
    /// （散场投递与终止更新之间崩溃的漏网），或本人已离开会话所属队伍时，按权威时刻补写。
    /// </summary>
    public async ValueTask ReconcileLoadedSessionBindingsAsync(CancellationToken cancellationToken = default)
    {
        if (_players is null) return;
        var loaded = await _players.GetLoadedSnapshotsAsync(cancellationToken);
        var stale = new List<(Guid PlayerId, string SessionId)>();
        foreach (var snapshot in loaded)
        {
            if (snapshot.State.Actions.Count > 0 && snapshot.State.Actions[0].Participation is { } binding
                && double.IsPositiveInfinity(binding.EndsAtUnixSeconds))
                stale.Add((snapshot.PlayerId, binding.Spec.SessionId));
        }
        var endedCache = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var aliveCache = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (memberId, sessionId) in stale)
        {
            try
            {
                if (!aliveCache.Contains(sessionId) && !endedCache.ContainsKey(sessionId))
                {
                    // 死会话判定：行缺失/已散场/本人已不在会话所属队伍（离队与散场间崩溃的漏网）。
                    var row = await _repository.GetPartySessionAsync(sessionId, cancellationToken);
                    var memberParty = await _repository.GetPartyIdAsync(memberId, cancellationToken);
                    if (row is not null && row.EndedAt is null && memberParty == row.PartyId)
                        aliveCache.Add(sessionId);
                    else
                        endedCache[sessionId] = row?.EndedAt ?? _clock.UtcNow;
                }
                if (aliveCache.Contains(sessionId) || !endedCache.TryGetValue(sessionId, out var endedAt)) continue;
                await ApplySessionMutationAsync(memberId,
                    aggregate => aggregate.EndActionParticipation(sessionId, endedAt.ToUnixTimeMilliseconds() / 1000d),
                    cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "组队讨伐绑定兜底收尾失败：成员 {PlayerId}（会话 {SessionId}）。", memberId, sessionId);
            }
        }

        // 开打记录先落库、逐成员投递后可能因崩溃漏投；周期对账必须扫描“活跃会话但队首无绑定”的在线玩家，
        // 不能只扫描已经存在的死绑定。
        foreach (var snapshot in loaded)
        {
            try
            {
                var partyId = await _repository.GetPartyIdAsync(snapshot.PlayerId, cancellationToken);
                if (partyId is not { } ownedPartyId) continue;
                var active = await _repository.GetActivePartySessionAsync(ownedPartyId, cancellationToken);
                if (active is not { } live || !live.MemberIds.Contains(snapshot.PlayerId)) continue;
                var spec = JsonSerializer.Deserialize<ActionParticipationSpec>(live.SpecJson, PersistenceJson.Options);
                if (spec is not { IsValid: true } participationSpec
                    || participationSpec.SelfIndexOf(new PlayerId(snapshot.PlayerId)) < 0)
                    continue;
                var current = _players.TryGetLoaded(snapshot.PlayerId) ?? snapshot;
                var bound = current.State.Actions.Count > 0
                    && current.State.Actions[0].Participation is { } participation
                    && string.Equals(participation.Spec.SessionId, live.SessionId, StringComparison.Ordinal)
                    && double.IsPositiveInfinity(participation.EndsAtUnixSeconds)
                    && current.State.Actions.Skip(1).All(action => action.Participation is null);
                if (bound) continue;
                await ApplySessionMutationAsync(snapshot.PlayerId, aggregate =>
                {
                    for (var i = aggregate.Actions.Count - 1; i >= 0; i--)
                    {
                        if (aggregate.Actions[i].Participation is null) continue;
                        aggregate.RemoveAction(aggregate.Actions[i].Id);
                    }
                    aggregate.StartActionParticipation(participationSpec, live.StartedAt, _gameData.Actions);
                }, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "行动参与绑定补发失败：成员 {PlayerId}。", snapshot.PlayerId);
            }
        }
    }

    private static FriendRequestDto ToRequestDto(FriendRequestView view) =>
        new(view.RequestId.ToString("D"), view.PlayerId.ToString("D"), view.DisplayName, view.CreatedAt.ToUnixTimeMilliseconds());
}
