using Domain.GameData;
using Protocol;

namespace Server.Clean.Social;

/// <summary>双轨邮件系统的全局约束、键值编解码与运营请求模型。</summary>
public static class MailRules
{
    /// <summary>单个角色个人定向邮件保留上限；插入超限时优先回收最旧已读无未领附件行。</summary>
    public const int MaxPlayerMails = 100;
    /// <summary>默认有效天数。</summary>
    public const int DefaultExpireDays = 30;
    /// <summary>允许配置的最大有效天数。</summary>
    public const int MaxExpireDays = 365;
    /// <summary>单封邮件最多附带的附件槽位数。</summary>
    public const int MaxAttachmentsPerMail = 12;
    /// <summary>标题最大长度。</summary>
    public const int MaxTitleLength = 120;
    /// <summary>正文最大长度。</summary>
    public const int MaxBodyLength = 4000;
    /// <summary>发件人名称最大长度。</summary>
    public const int MaxSenderLength = 64;
    /// <summary>默认系统发件署名。</summary>
    public const string DefaultSenderName = "道化司";
    /// <summary>个人与全服群发列表展示上限。</summary>
    public const int OpsBroadcastListLimit = 50;
}

/// <summary>邮件键所属物理轨道。</summary>
public enum MailTrackKind
{
    Broadcast = 1,
    Personal = 2
}

/// <summary>统一邮件键编解码：`b:{broadcastId}`（全服群发）与 `p:{mailId}`（个人定向）。</summary>
public static class MailKeyCodec
{
    public static string FormatBroadcast(long broadcastId) => $"b:{broadcastId}";

    public static string FormatPersonal(Guid mailId) => $"p:{mailId:D}";

    public static bool TryParse(string? mailKey, out MailTrackKind kind, out long broadcastId, out Guid personalMailId)
    {
        kind = default;
        broadcastId = 0;
        personalMailId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(mailKey)) return false;

        var trimmed = mailKey.Trim();
        if (trimmed.StartsWith("b:", StringComparison.Ordinal))
        {
            if (long.TryParse(trimmed.AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out broadcastId)
                && broadcastId > 0)
            {
                kind = MailTrackKind.Broadcast;
                return true;
            }
            return false;
        }

        if (trimmed.StartsWith("p:", StringComparison.Ordinal))
        {
            if (Guid.TryParse(trimmed.AsSpan(2), out personalMailId) && personalMailId != Guid.Empty)
            {
                kind = MailTrackKind.Personal;
                return true;
            }
            return false;
        }

        return false;
    }
}

/// <summary>全局只读不可变全服群发公告缓存记录（全服所有玩家共享同一引用）。</summary>
public sealed record BroadcastMailRecord(
    long BroadcastId,
    string Title,
    string Body,
    string SenderName,
    IReadOnlyList<MailAttachmentDto> Attachments,
    bool OnlyExistingPlayers,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt)
{
    public string MailKey => MailKeyCodec.FormatBroadcast(BroadcastId);
    public bool HasAttachments => Attachments.Count > 0;
    public bool IsExpired(DateTimeOffset now) => (ExpiresAt is { } exp && exp <= now) || RevokedAt is not null;
    public bool IsEligibleFor(DateTimeOffset accountCreatedAt, DateTimeOffset now)
        => !IsExpired(now) && (!OnlyExistingPlayers || accountCreatedAt <= CreatedAt);
}

/// <summary>运营后台查看的群发公告记录（含失效与已撤回条目）。</summary>
public sealed record OpsBroadcastMailDto(
    long BroadcastId,
    string Title,
    string SenderName,
    int AttachmentCount,
    bool OnlyExistingPlayers,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    bool IsActive);

/// <summary>运营后台发送个人定向邮件请求。</summary>
public sealed record OpsSendPlayerMailRequest(
    string? TargetPlayerId,
    string? TargetDisplayName,
    string Title,
    string Body,
    string? SenderName,
    IReadOnlyList<MailAttachmentDto>? Attachments,
    int? ExpireDays,
    bool NeverExpire = false);

/// <summary>运营后台发送全服群发邮件请求。</summary>
public sealed record OpsSendBroadcastMailRequest(
    string Title,
    string Body,
    string? SenderName,
    IReadOnlyList<MailAttachmentDto>? Attachments,
    bool OnlyExistingPlayers = true,
    int? ExpireDays = null,
    bool NeverExpire = false);

/// <summary>运营后台撤销全服群发公告请求。</summary>
public sealed record OpsRevokeBroadcastMailRequest(long BroadcastId);

/// <summary>邮件领域错误原因码。</summary>
public enum MailOperationFailure
{
    InvalidArgument = 1,
    NotFound = 2,
    HasUnclaimedAttachments = 3,
    BotRestricted = 4
}

/// <summary>邮件业务违例异常（映射到明确 HTTP 状态码，禁止静默吞掉）。</summary>
public sealed class MailOperationException(MailOperationFailure failure, string message) : Exception(message)
{
    public MailOperationFailure Failure { get; } = failure;
}
