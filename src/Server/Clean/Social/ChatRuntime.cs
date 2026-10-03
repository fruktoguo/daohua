using System.Collections.Concurrent;
using Protocol;

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
    RateLimited
}

/// <summary>传音发言处理结局。</summary>
/// <param name="Status">结局状态。</param>
/// <param name="Message">已落库的消息（成功时非空）。</param>
/// <param name="Recipients">需要接收该消息的玩家 ID 集合（成功时非空）。</param>
public sealed record ChatSendOutcome(ChatSendStatus Status, ChatMessageDto? Message = null, IReadOnlyList<Guid>? Recipients = null);

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

    private const int MaxSendsPerWindow = 3;
    private const long WindowMilliseconds = 1000;
    private const int RateWindowPruneThreshold = 2048;

    private readonly IChatRepository _repository;
    private readonly ISocialRepository _social;
    private readonly OnlinePresenceRegistry _presence;
    private readonly ConcurrentDictionary<(Guid PlayerId, string Channel), (long WindowStart, int Count)> _rateWindows = new();

    /// <summary>构造传音运行时。</summary>
    /// <param name="repository">传音消息持久化边界。</param>
    /// <param name="social">好友/队伍持久化边界（好友私聊与队伍频道作用域解析）。</param>
    /// <param name="presence">在线存在感注册表（广播收件人解析）。</param>
    public ChatRuntime(IChatRepository repository, ISocialRepository social, OnlinePresenceRegistry presence)
    {
        _repository = repository;
        _social = social;
        _presence = presence;
    }

    /// <summary>处理一次传音发言：校验 → 节流 → 落库 → 解析收件人。</summary>
    /// <param name="senderId">发送者玩家 ID。</param>
    /// <param name="senderName">发送者道号。</param>
    /// <param name="request">发言请求载荷。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>处理结局。</returns>
    public async ValueTask<ChatSendOutcome> SendAsync(Guid senderId, string senderName, ChatSendDto request, CancellationToken cancellationToken = default)
    {
        var body = request.Body?.Trim() ?? string.Empty;
        if (body.Length == 0 || body.Length > MaxBodyLength) return new ChatSendOutcome(ChatSendStatus.InvalidBody);
        if (!ChatChannels.IsSupported(request.Channel)) return new ChatSendOutcome(ChatSendStatus.ChannelUnavailable);
        PruneRateWindowsIfNeeded();
        if (!TryConsumeRateLimit(senderId, request.Channel)) return new ChatSendOutcome(ChatSendStatus.RateLimited);

        var targetId = ParsePlayerId(request.TargetPlayerId);
        var scopeKey = await ResolveScopeAsync(request.Channel, senderId, targetId, cancellationToken);
        if (scopeKey is null) return new ChatSendOutcome(ChatSendStatus.ChannelUnavailable);

        var record = new ChatMessageRecord(Guid.NewGuid(), request.Channel, scopeKey, senderId, senderName, body, DateTimeOffset.UtcNow);
        await _repository.AppendAsync(record, RetentionPerScope, cancellationToken);
        var recipients = await ResolveRecipientsAsync(request.Channel, scopeKey, senderId, targetId, cancellationToken);
        return new ChatSendOutcome(ChatSendStatus.Ok, ToDto(record), recipients);
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
        Guid senderId, string senderName, string? channel, Guid? targetId, PartyRecord party, CancellationToken cancellationToken = default)
    {
        if (channel is not (ChatChannels.World or ChatChannels.Nearby or ChatChannels.Friend))
            return new ChatSendOutcome(ChatSendStatus.ChannelUnavailable);
        PruneRateWindowsIfNeeded();
        if (!TryConsumeRateLimit(senderId, channel)) return new ChatSendOutcome(ChatSendStatus.RateLimited);

        var scopeKey = await ResolveScopeAsync(channel, senderId, targetId, cancellationToken);
        if (scopeKey is null) return new ChatSendOutcome(ChatSendStatus.ChannelUnavailable);

        var link = new PartyLinkRecord(
            party.PartyId, party.Profile.Name, party.Profile.ActivityKind,
            party.Profile.MapId, party.Profile.MinValuation, party.Members.Count);
        var body = $"「{party.Profile.Name}」正在招募同道，点击卡片加入。";
        var record = new ChatMessageRecord(
            Guid.NewGuid(), channel, scopeKey, senderId, senderName, body, DateTimeOffset.UtcNow, link);
        await _repository.AppendAsync(record, RetentionPerScope, cancellationToken);
        var recipients = await ResolveRecipientsAsync(channel, scopeKey, senderId, targetId, cancellationToken);
        return new ChatSendOutcome(ChatSendStatus.Ok, ToDto(record), recipients);
    }

    /// <summary>读取某频道作用域的最近消息（时间正序）。</summary>
    /// <param name="playerId">请求玩家 ID（好友/队伍作用域按请求者解析）。</param>
    /// <param name="request">历史回放请求载荷。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>历史载荷；频道不可用时返回 null。</returns>
    public async ValueTask<ChatHistoryDto?> LoadHistoryAsync(Guid playerId, ChatHistoryRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!ChatChannels.IsSupported(request.Channel)) return null;
        var targetId = ParsePlayerId(request.TargetPlayerId);
        var scopeKey = await ResolveScopeAsync(request.Channel, playerId, targetId, cancellationToken);
        if (scopeKey is null) return null;
        var messages = await _repository.LoadRecentAsync(request.Channel, scopeKey, RetentionPerScope, cancellationToken);
        return new ChatHistoryDto(request.Channel, scopeKey, targetId?.ToString("D"), [.. messages.Select(ToDto)]);
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

    /// <summary>频道级固定窗口节流：同一玩家同一频道每秒最多 3 条。</summary>
    private bool TryConsumeRateLimit(Guid playerId, string channel)
    {
        var now = Environment.TickCount64;
        var key = (playerId, channel);
        while (true)
        {
            var current = _rateWindows.GetOrAdd(key, _ => (now, 0));
            if (now - current.WindowStart >= WindowMilliseconds)
            {
                if (_rateWindows.TryUpdate(key, (now, 1), current)) return true;
                continue;
            }
            if (current.Count >= MaxSendsPerWindow) return false;
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

    private static Guid? ParsePlayerId(string? value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : null;

    private static ChatMessageDto ToDto(ChatMessageRecord record) => new(
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
                record.PartyLink.MapId,
                record.PartyLink.MinValuation,
                record.PartyLink.MemberCount));
}
