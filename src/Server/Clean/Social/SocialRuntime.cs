using Protocol;

namespace Server.Clean.Social;

/// <summary>
/// 好友与队伍业务运行时（单例）。
/// 设计要点：
/// 1. 好友与队伍不属于玩家聚合状态，因此不进入 PlayerActor 单写者锁，避免跨玩家互相阻塞；
/// 2. 所有状态变更落 PostgreSQL 后，向受影响玩家推送完整 <see cref="SocialStateDto"/>；
/// 3. 在线存在感变化（上线/下线/换节点）会触发相关玩家的社交状态刷新（好友在线状态与附近修士列表）。
/// </summary>
public sealed class SocialRuntime
{
    /// <summary>队伍人数上限（含队长）。</summary>
    public const int MaxPartyMembers = 5;

    private readonly ISocialRepository _repository;
    private readonly OnlinePresenceRegistry _presence;
    private readonly ILogger<SocialRuntime> _logger;

    /// <summary>构造并接管在线存在感变化订阅。</summary>
    /// <param name="repository">好友/队伍持久化边界。</param>
    /// <param name="presence">在线存在感注册表。</param>
    /// <param name="logger">日志记录器。</param>
    public SocialRuntime(ISocialRepository repository, OnlinePresenceRegistry presence, ILogger<SocialRuntime> logger)
    {
        _repository = repository;
        _presence = presence;
        _logger = logger;
        _presence.PresenceChanged += OnPresenceChangedAsync;
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
        var friends = await _repository.ListFriendsAsync(playerId, cancellationToken);
        var incoming = await _repository.ListPendingRequestsAsync(playerId, true, cancellationToken);
        var outgoing = await _repository.ListPendingRequestsAsync(playerId, false, cancellationToken);
        var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken);
        var party = partyId is null ? null : await _repository.GetPartyAsync(partyId.Value, cancellationToken);
        var invitation = await _repository.GetPartyInvitationAsync(playerId, cancellationToken);

        return new SocialStateDto(
            requestId,
            [.. friends.Select(friend => new FriendDto(friend.FriendId.ToString("D"), friend.DisplayName, _presence.IsOnline(friend.FriendId)))],
            [.. incoming.Select(ToRequestDto)],
            [.. outgoing.Select(ToRequestDto)],
            party is null
                ? null
                : new PartyDto(
                    party.PartyId.ToString("D"),
                    party.LeaderId.ToString("D"),
                    [.. party.Members.Select(member => new PartyMemberDto(member.PlayerId.ToString("D"), member.DisplayName, _presence.IsOnline(member.PlayerId)))]),
            invitation is null
                ? null
                : new PartyInvitationDto(invitation.PartyId.ToString("D"), invitation.InviterId.ToString("D"), invitation.InviterName),
            _presence.SnapshotAtNode(_presence.GetNodeId(playerId)));
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
            case SocialActions.PartyInvite:
            {
                var targetId = ResolveTargetId(action);
                if (targetId is null) return new SocialOutcome(SocialFailure.TargetNotFound);
                if (targetId == playerId) return new SocialOutcome(SocialFailure.SelfTarget);
                // 未组队时以邀请人自建队伍并任队长：邀请是组队的唯一入口，不再单独暴露「创建队伍」指令。
                var partyId = await _repository.GetPartyIdAsync(playerId, cancellationToken)
                    ?? await _repository.CreatePartyAsync(playerId, now, cancellationToken);
                var party = await _repository.GetPartyAsync(partyId, cancellationToken);
                if (party is null) return new SocialOutcome(SocialFailure.NotInParty);
                if (party.LeaderId != playerId) return new SocialOutcome(SocialFailure.NotLeader);
                if (party.Members.Count >= MaxPartyMembers) return new SocialOutcome(SocialFailure.PartyFull);
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
                if (party is null || party.Members.Count >= MaxPartyMembers)
                {
                    await _repository.TryClearPartyInvitationAsync(playerId, cancellationToken);
                    return new SocialOutcome(party is null ? SocialFailure.NotInParty : SocialFailure.PartyFull);
                }
                if (!await _repository.TryAddPartyMemberAsync(invitation.PartyId, playerId, now, cancellationToken))
                    return new SocialOutcome(SocialFailure.AlreadyInParty);
                await _repository.TryClearPartyInvitationAsync(playerId, cancellationToken);
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
                await NotifyAsync(affected, playerId);
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
    /// 向受影响玩家推送最新社交状态。发起指令的玩家由 WebSocket 端点用带 requestId 的回执帧单独回复，
    /// 因此这里排除该玩家，避免同一状态被推送两次。
    /// </summary>
    private async ValueTask NotifyAsync(IReadOnlyCollection<Guid> playerIds, Guid? exclude = null)
    {
        var handler = StateChanged;
        if (handler is null || playerIds.Count == 0) return;
        foreach (var playerId in playerIds.Distinct())
        {
            if (playerId == exclude) continue;
            // 离线玩家没有会话，构建状态只会白跑数据库查询。
            if (!_presence.IsOnline(playerId)) continue;
            await handler(playerId);
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

    private static FriendRequestDto ToRequestDto(FriendRequestView view) =>
        new(view.RequestId.ToString("D"), view.PlayerId.ToString("D"), view.DisplayName, view.CreatedAt.ToUnixTimeMilliseconds());
}
