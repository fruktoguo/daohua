using System.Collections.Concurrent;
using Protocol;
using Server.Clean.Leaderboards;

namespace Server.Clean.Social;

/// <summary>传音发言结局。</summary>
public enum ChatSendStatus : byte
{
    /// <summary>已受理并落库。</summary>
    Ok = 0,
    /// <summary>正文为空或超过长度上限。</summary>
    InvalidBody,
    /// <summary>频道不可用（宗门未开放、附近无节点、非好友、未组队）。</summary>
    ChannelUnavailable,
    /// <summary>触发频道级发送节流。</summary>
    RateLimited,
    /// <summary>原消息不存在或不在当前有权访问的会话。</summary>
    QuoteUnavailable
}

/// <summary>传音发言处理结局。</summary>
/// <param name="Status">结局状态。</param>
/// <param name="Message">已落库的消息（成功时非空）。</param>
/// <param name="Recipients">需要接收该消息的玩家 ID 集合（成功时非空）。</param>
public sealed record ChatSendOutcome(ChatSendStatus Status, ChatMessageDto? Message = null, IReadOnlyList<Guid>? Recipients = null);

/// <summary>点赞设置结果：不可用、限频或携带权威状态成功。</summary>
public enum ChatLikeStatus : byte { Ok, Unavailable, RateLimited }

/// <summary>点赞广播及其收件人；幂等重放只校正发起者状态。</summary>
public sealed record ChatLikeOutcome(ChatLikeStatus Status, ChatLikeChangedDto? Update = null, IReadOnlyList<Guid>? Recipients = null);

/// <summary>
/// 传音频道运行时（单例）。
/// 职责：
/// 1. 把「频道标识 + 发送者」解析为唯一作用域键（世界空串 / 附近节点 / 好友双方 ID 升序 / 队伍 ID）；
/// 2. 频道级节流、正文长度校验与落库（每作用域只保留最近 100 条）；
/// 3. 解析广播收件人（世界=全部在线、附近=同节点在线、好友=双方、队伍=队伍成员）；
/// 4. 提供频道历史回放（最近 100 条，时间正序）。
/// </summary>
public sealed class ChatRuntime
{
    /// <summary>每个频道作用域保留的消息条数。</summary>
    public const int RetentionPerScope = 100;
    /// <summary>单条消息正文长度上限（字符）。</summary>
    public const int MaxBodyLength = 200;

    private const int MaxActionsPerWindow = 3;
    private const long WindowMilliseconds = 1000;
    private const int RateWindowPruneThreshold = 2048;

    private readonly IChatRepository _repository;
    private readonly ISocialRepository _social;
    private readonly OnlinePresenceRegistry _presence;
    private readonly PostgresZongmenRepository? _zongmen;
    private readonly LeaderboardRuntime? _leaderboards;
    private readonly ConcurrentDictionary<(Guid PlayerId, string Channel, bool ForLike), (long WindowStart, int Count)> _rateWindows = new();

    /// <summary>构造传音运行时。</summary>
    /// <param name="repository">传音消息持久化边界。</param>
    /// <param name="social">好友/队伍持久化边界（好友私聊与队伍频道作用域解析）。</param>
    /// <param name="presence">在线存在感注册表（广播收件人解析）。</param>
    /// <param name="zongmen">宗门持久化仓储（用于冠名与宗门频道校验）。</param>
    /// <param name="leaderboards">全服排行榜运行时（用于实时派生发言者最高个人榜 Top 50 徽标，不落盘）。</param>
    public ChatRuntime(
        IChatRepository repository,
        ISocialRepository social,
        OnlinePresenceRegistry presence,
        PostgresZongmenRepository? zongmen = null,
        LeaderboardRuntime? leaderboards = null)
    {
        _repository = repository;
        _social = social;
        _presence = presence;
        _zongmen = zongmen;
        _leaderboards = leaderboards;
    }

    /// <summary>处理一次传音发言：校验 → 节流 → 落库 → 解析收件人。</summary>
    /// <param name="senderId">发送者玩家 ID。</param>
    /// <param name="senderName">发送者道号。</param>
    /// <param name="request">发言请求载荷。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>处理结局。</returns>
    public async ValueTask<ChatSendOutcome> SendAsync(Guid senderId, string senderName, ChatSendDto request, int senderRealmLevel = 1, CancellationToken cancellationToken = default)
    {
        var body = request.Body?.Trim() ?? string.Empty;
        if (body.Length == 0 || body.Length > MaxBodyLength) return new ChatSendOutcome(ChatSendStatus.InvalidBody);
        var quoteId = ParseId(request.QuoteMessageId);
        if (request.QuoteMessageId is not null && quoteId is null) return new ChatSendOutcome(ChatSendStatus.QuoteUnavailable);
        var unavailable = quoteId.HasValue ? ChatSendStatus.QuoteUnavailable : ChatSendStatus.ChannelUnavailable;
        if (!ChatChannels.IsSupported(request.Channel)) return new ChatSendOutcome(unavailable);
        PruneRateWindowsIfNeeded();
        if (!TryConsumeRateLimit(senderId, request.Channel)) return new ChatSendOutcome(ChatSendStatus.RateLimited);

        var targetId = ParseId(request.TargetPlayerId);
        var scopeKey = await ResolveScopeAsync(request.Channel, senderId, targetId, cancellationToken);
        if (scopeKey is null) return new ChatSendOutcome(unavailable);

        senderName = ZongmenRules.DisplayName(senderName, _zongmen is null ? null : await _zongmen.PrefixAsync(senderId, cancellationToken));
        var record = new ChatMessageRecord(Guid.NewGuid(), request.Channel, scopeKey, senderId, senderName, body, DateTimeOffset.UtcNow, null, senderRealmLevel);
        var stored = await _repository.AppendAsync(record, RetentionPerScope, quoteId, cancellationToken);
        if (stored is null) return new ChatSendOutcome(ChatSendStatus.QuoteUnavailable);
        var recipients = await ResolveRecipientsAsync(request.Channel, scopeKey, senderId, targetId, cancellationToken);
        var topRank = await ResolveTopRankAsync(senderId, cancellationToken);
        return new ChatSendOutcome(ChatSendStatus.Ok, ToDto(stored, topRank), recipients);
    }

    /// <summary>
    /// 发布队伍招募卡片：party.share 社交动作借道传音通道，
    /// 正文为服务端规范文本（不消耗玩家 200 字输入），party_ref 外键随消息落库。
    /// 作用域解析与限频口径与 <see cref="SendAsync"/> 一致；好友频道要求目标为双向好友。
    /// </summary>
    /// <param name="senderId">分享者玩家 ID（须为队伍成员）。</param>
    /// <param name="senderName">分享者道号。</param>
    /// <param name="channel">目标频道（world/nearby/friend；party 频道分享本队无意义，不支持）。</param>
    /// <param name="targetId">好友频道的私聊对象；其余频道忽略。</param>
    /// <param name="party">当前队伍记录。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>处理结局（Status 为 Ok 时 Message/Recipients 非空，由 WebSocket 端广播）。</returns>
    public async ValueTask<ChatSendOutcome> SendPartyShareAsync(
        Guid senderId, string senderName, string? channel, Guid? targetId, PartyRecord party, int senderRealmLevel = 1, CancellationToken cancellationToken = default)
    {
        if (channel is not (ChatChannels.World or ChatChannels.Nearby or ChatChannels.Friend))
            return new ChatSendOutcome(ChatSendStatus.ChannelUnavailable);
        PruneRateWindowsIfNeeded();
        if (!TryConsumeRateLimit(senderId, channel)) return new ChatSendOutcome(ChatSendStatus.RateLimited);

        var scopeKey = await ResolveScopeAsync(channel, senderId, targetId, cancellationToken);
        if (scopeKey is null) return new ChatSendOutcome(ChatSendStatus.ChannelUnavailable);

        var link = new PartyLinkRecord(
            party.PartyId, party.Profile.Name, party.Profile.ActivityKind,
            party.Profile.NodeId, party.Profile.MinValuation, party.Members.Count);
        var body = $"「{party.Profile.Name}」正在招募同道，点击卡片加入。";
        var record = new ChatMessageRecord(
            Guid.NewGuid(), channel, scopeKey, senderId, senderName, body, DateTimeOffset.UtcNow, link, senderRealmLevel);
        await _repository.AppendAsync(record, RetentionPerScope, cancellationToken: cancellationToken);
        var recipients = await ResolveRecipientsAsync(channel, scopeKey, senderId, targetId, cancellationToken);
        var topRank = await ResolveTopRankAsync(senderId, cancellationToken);
        return new ChatSendOutcome(ChatSendStatus.Ok, ToDto(record, topRank), recipients);
    }

    /// <summary>读取某频道作用域的最近消息（时间正序）。</summary>
    /// <param name="playerId">请求玩家 ID（好友/队伍作用域按请求者解析）。</param>
    /// <param name="request">历史回放请求载荷。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>历史载荷；频道不可用时返回 null。</returns>
    public async ValueTask<ChatHistoryDto?> LoadHistoryAsync(Guid playerId, ChatHistoryRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!ChatChannels.IsSupported(request.Channel)) return null;
        var targetId = request.Channel == ChatChannels.Friend ? ParseId(request.TargetPlayerId) : null;
        var scopeKey = await ResolveScopeAsync(request.Channel, playerId, targetId, cancellationToken);
        if (scopeKey is null) return null;
        var messages = await _repository.LoadRecentAsync(request.Channel, scopeKey, playerId, RetentionPerScope, cancellationToken);
        var topRanks = await ResolveTopRanksAsync(messages, cancellationToken);
        return new ChatHistoryDto(
            request.Channel,
            scopeKey,
            targetId?.ToString("D"),
            [.. messages.Select(message => ToDto(message, topRanks.GetValueOrDefault(message.SenderId)))]);
    }

    /// <summary>按历史回放相同的权限解析会话，再设置本人对该消息的点赞状态。</summary>
    public async ValueTask<ChatLikeOutcome> SetLikeAsync(Guid playerId, ChatLikeSetDto request, CancellationToken cancellationToken = default)
    {
        if (!ChatChannels.IsSupported(request.Channel) || ParseId(request.MessageId) is not { } messageId)
            return new(ChatLikeStatus.Unavailable);
        PruneRateWindowsIfNeeded();
        if (!TryConsumeRateLimit(playerId, request.Channel, forLike: true)) return new(ChatLikeStatus.RateLimited);

        var targetId = request.Channel == ChatChannels.Friend ? ParseId(request.TargetPlayerId) : null;
        var scopeKey = await ResolveScopeAsync(request.Channel, playerId, targetId, cancellationToken);
        if (scopeKey is null) return new(ChatLikeStatus.Unavailable);

        var state = await _repository.SetLikeAsync(request.Channel, scopeKey, messageId, playerId, request.Liked, cancellationToken);
        if (state is null) return new(ChatLikeStatus.Unavailable);

        var update = new ChatLikeChangedDto(request.Channel, scopeKey, messageId.ToString("D"), playerId.ToString("D"),
            request.Liked, state.Count, state.Version);
        var recipients = state.Changed
            ? await ResolveRecipientsAsync(request.Channel, scopeKey, playerId, targetId, cancellationToken)
            : [playerId];
        return new(ChatLikeStatus.Ok, update, recipients);
    }

    /// <summary>解析频道作用域键；频道当前不可用时返回 null。</summary>
    private async ValueTask<string?> ResolveScopeAsync(string channel, Guid playerId, Guid? targetId, CancellationToken cancellationToken)
    {
        switch (channel)
        {
            case ChatChannels.World:
                return string.Empty;
            case ChatChannels.Nearby:
            {
                var nodeId = _presence.GetNodeId(playerId);
                return string.IsNullOrEmpty(nodeId) ? null : nodeId;
            }
            case ChatChannels.Friend:
            {
                if (targetId is null || targetId == playerId) return null;
                return await _social.AreFriendsAsync(playerId, targetId.Value, cancellationToken)
                    ? BuildFriendScopeKey(playerId, targetId.Value)
                    : null;
            }
            case ChatChannels.Sect:
                return _zongmen is null ? null : (await _zongmen.FindForPlayerAsync(playerId, cancellationToken))?.Id.ToString("D");
            case ChatChannels.Party:
            {
                var partyId = await _social.GetPartyIdAsync(playerId, cancellationToken);
                return partyId?.ToString("D");
            }
            default:
                return null;
        }
    }

    /// <summary>解析广播收件人；离线玩家不参与推送，但消息本身已落库供历史回放。</summary>
    private async ValueTask<IReadOnlyList<Guid>> ResolveRecipientsAsync(string channel, string scopeKey, Guid senderId, Guid? targetId, CancellationToken cancellationToken)
    {
        switch (channel)
        {
            case ChatChannels.World:
                return _presence.SnapshotOnlinePlayerIds();
            case ChatChannels.Nearby:
                return [.. _presence.SnapshotAtNode(scopeKey).Select(player => Guid.Parse(player.PlayerId))];
            case ChatChannels.Friend:
                return targetId is null ? [senderId] : [senderId, targetId.Value];
            case ChatChannels.Sect:
                return _zongmen is null ? [] : await _zongmen.MemberIdsAsync(Guid.Parse(scopeKey), cancellationToken);
            case ChatChannels.Party:
            {
                var party = await _social.GetPartyAsync(Guid.Parse(scopeKey), cancellationToken);
                return party is null ? [senderId] : [.. party.Members.Select(member => member.PlayerId)];
            }
            default:
                return [senderId];
        }
    }

    /// <summary>好友私聊作用域键：双方 ID 升序拼接，保证同一对好友共享同一作用域。</summary>
    /// <param name="left">一方玩家 ID。</param>
    /// <param name="right">另一方玩家 ID。</param>
    /// <returns>作用域键。</returns>
    public static string BuildFriendScopeKey(Guid left, Guid right) =>
        left.CompareTo(right) <= 0 ? $"{left:D}:{right:D}" : $"{right:D}:{left:D}";

    /// <summary>频道级固定窗口节流：同一玩家同一频道每秒最多 3 次，发言与点赞各自计数。</summary>
    private bool TryConsumeRateLimit(Guid playerId, string channel, bool forLike = false)
    {
        var now = Environment.TickCount64;
        var key = (playerId, channel, forLike);
        while (true)
        {
            var current = _rateWindows.GetOrAdd(key, _ => (now, 0));
            if (now - current.WindowStart >= WindowMilliseconds)
            {
                if (_rateWindows.TryUpdate(key, (now, 1), current)) return true;
                continue;
            }
            if (current.Count >= MaxActionsPerWindow) return false;
            if (_rateWindows.TryUpdate(key, (current.WindowStart, current.Count + 1), current)) return true;
        }
    }

    /// <summary>节流窗口表只随在线玩家规模增长，超阈值时清掉已过期的窗口避免长跑会话内存膨胀。</summary>
    private void PruneRateWindowsIfNeeded()
    {
        if (_rateWindows.Count < RateWindowPruneThreshold) return;
        var now = Environment.TickCount64;
        foreach (var pair in _rateWindows)
        {
            if (now - pair.Value.WindowStart >= WindowMilliseconds) _rateWindows.TryRemove(pair.Key, out _);
        }
    }

    private ValueTask<GuideRankBadgeDto?> ResolveTopRankAsync(Guid senderId, CancellationToken cancellationToken)
        => _leaderboards is null
            ? ValueTask.FromResult<GuideRankBadgeDto?>(null)
            : _leaderboards.GetBestPersonalRankAsync(senderId, LeaderboardAssembler.TopN, cancellationToken);

    private ValueTask<IReadOnlyDictionary<Guid, GuideRankBadgeDto>> ResolveTopRanksAsync(
        IReadOnlyList<ChatMessageRecord> messages,
        CancellationToken cancellationToken)
    {
        if (_leaderboards is null || messages.Count == 0)
            return ValueTask.FromResult<IReadOnlyDictionary<Guid, GuideRankBadgeDto>>(EmptyTopRanks);

        var senderIds = new HashSet<Guid>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
            senderIds.Add(messages[i].SenderId);
        return _leaderboards.GetBestPersonalRanksAsync(senderIds, LeaderboardAssembler.TopN, cancellationToken);
    }

    private static readonly IReadOnlyDictionary<Guid, GuideRankBadgeDto> EmptyTopRanks =
        new Dictionary<Guid, GuideRankBadgeDto>(0);

    private static Guid? ParseId(string? value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : null;

    private static ChatMessageDto ToDto(ChatMessageRecord record, GuideRankBadgeDto? senderTopRank = null) => new(
        record.MessageId.ToString("D"),
        record.Channel,
        record.ScopeKey,
        record.SenderId.ToString("D"),
        record.SenderName,
        record.Body,
        record.SentAt.ToUnixTimeMilliseconds(),
        record.PartyLink is null
            ? null
            : new PartyLinkDto(
                record.PartyLink.PartyId.ToString("D"),
                record.PartyLink.Name,
                record.PartyLink.ActivityKind,
                record.PartyLink.NodeId,
                record.PartyLink.MinValuation,
                record.PartyLink.MemberCount),
        record.SenderRealmLevel,
        record.Quote is { } quote ? new ChatQuoteDto(quote.MessageId.ToString("D"), quote.SenderName, quote.Body) : null,
        record.LikeCount,
        record.LikedByMe,
        record.LikeVersion,
        senderTopRank);
}
