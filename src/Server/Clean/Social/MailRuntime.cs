using System.Collections.Immutable;
using System.Text.Json;
using Domain.GameData;
using Protocol;

namespace Server.Clean.Social;

/// <summary>
/// 双轨邮件核心运行时（全服单体共享群发缓存 + 零群发 DB 风暴推送）：
/// - 活跃全服群发公告以 <see cref="ImmutableArray{T}"/> 全局共享，不为离线或未打开邮件的角色分配按人内存；
/// - 个人发件向在线目标单推精确红点摘要；全服群发只序列化 1 帧 ForceRedDot 信封向在线非机器人连接旁路放送；
/// - 附件入账提交后调用 <see cref="IPlayerRuntime.ReloadWalletAndNotifyAsync"/> 即时同步在线角色钱包。
/// </summary>
public sealed class MailRuntime(
    PostgresMailRepository repository,
    GameDataCatalog gameData,
    SessionRegistry? sessions = null,
    IPlayerRuntime? playerRuntime = null)
{
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private ImmutableArray<BroadcastMailRecord> _activeBroadcasts = [];
    private long _cacheExpiresAtUtcTicks = DateTimeOffset.MinValue.UtcTicks;
    private int _cacheInitialized;

    /// <summary>获取全局共享只读未过期全服群发列表（绝大多数请求零 DB 访问）。</summary>
    public async ValueTask<ImmutableArray<BroadcastMailRecord>> GetActiveBroadcastsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (Volatile.Read(ref _cacheInitialized) == 1 && now.UtcTicks < Interlocked.Read(ref _cacheExpiresAtUtcTicks))
            return _activeBroadcasts;

        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (Volatile.Read(ref _cacheInitialized) == 1 && now.UtcTicks < Interlocked.Read(ref _cacheExpiresAtUtcTicks))
                return _activeBroadcasts;
            return await ReloadCacheLockedAsync(now, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    /// <summary>获取轻量红点摘要（供 WS Hello 握手或轮询）。</summary>
    public async ValueTask<MailSummaryDto> GetSummaryAsync(
        Guid playerId,
        bool? knownIsBot = null,
        DateTimeOffset? knownAccountCreatedAt = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var broadcasts = await GetActiveBroadcastsAsync(ct).ConfigureAwait(false);
        return await repository.GetSummaryAsync(playerId, broadcasts, now, knownIsBot, knownAccountCreatedAt, ct).ConfigureAwait(false);
    }

    /// <summary>查询玩家邮件列表（不含个人正文 body）。</summary>
    public async ValueTask<MailListResponseDto> ListMailsAsync(Guid playerId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var broadcasts = await GetActiveBroadcastsAsync(ct).ConfigureAwait(false);
        return await repository.ListMailsAsync(playerId, broadcasts, now, ct).ConfigureAwait(false);
    }

    /// <summary>按需读取单封邮件正文并在未读时自动标为已读。</summary>
    public async ValueTask<MailDetailResponseDto> GetDetailAndMarkReadAsync(Guid playerId, string mailKey, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var broadcasts = await GetActiveBroadcastsAsync(ct).ConfigureAwait(false);
        return await repository.GetDetailAndMarkReadAsync(playerId, mailKey, broadcasts, now, ct).ConfigureAwait(false);
    }

    /// <summary>显式标记单封邮件为已读。</summary>
    public async ValueTask<MailReadResponseDto> MarkReadAsync(Guid playerId, string mailKey, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var broadcasts = await GetActiveBroadcastsAsync(ct).ConfigureAwait(false);
        return await repository.MarkReadAsync(playerId, mailKey, broadcasts, now, ct).ConfigureAwait(false);
    }

    /// <summary>领取单封邮件附件，成功后热刷新在线玩家 ECS 钱包并推送红点摘要。</summary>
    public async ValueTask<MailClaimResponseDto> ClaimSingleAsync(Guid playerId, string mailKey, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var broadcasts = await GetActiveBroadcastsAsync(ct).ConfigureAwait(false);
        var result = await repository.ClaimSingleAsync(playerId, mailKey, broadcasts, now, ct).ConfigureAwait(false);
        if (result.ClaimedMailKeys.Count > 0 && playerRuntime is not null)
            await playerRuntime.ReloadWalletAndNotifyAsync(playerId, CancellationToken.None).ConfigureAwait(false);
        PushSummaryToSession(playerId, result.Summary);
        return result;
    }

    /// <summary>一键领取全部当前可领取附件，成功后热刷新在线玩家 ECS 钱包并推送红点摘要。</summary>
    public async ValueTask<MailClaimResponseDto> ClaimAllAsync(Guid playerId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var broadcasts = await GetActiveBroadcastsAsync(ct).ConfigureAwait(false);
        var result = await repository.ClaimAllAsync(playerId, broadcasts, now, ct).ConfigureAwait(false);
        if (result.ClaimedMailKeys.Count > 0 && playerRuntime is not null)
            await playerRuntime.ReloadWalletAndNotifyAsync(playerId, CancellationToken.None).ConfigureAwait(false);
        PushSummaryToSession(playerId, result.Summary);
        return result;
    }

    /// <summary>删除单封邮件（含未领附件时抛 409）。</summary>
    public async ValueTask<MailDeleteResponseDto> DeleteSingleAsync(Guid playerId, string mailKey, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var broadcasts = await GetActiveBroadcastsAsync(ct).ConfigureAwait(false);
        var result = await repository.DeleteSingleAsync(playerId, mailKey, broadcasts, now, ct).ConfigureAwait(false);
        PushSummaryToSession(playerId, result.Summary);
        return result;
    }

    /// <summary>删除全部已读且无未领附件邮件（跳过未领附件并统计跳过数）。</summary>
    public async ValueTask<MailDeleteResponseDto> DeleteAllReadAsync(Guid playerId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var broadcasts = await GetActiveBroadcastsAsync(ct).ConfigureAwait(false);
        var result = await repository.DeleteAllReadAsync(playerId, broadcasts, now, ct).ConfigureAwait(false);
        PushSummaryToSession(playerId, result.Summary);
        return result;
    }

    /// <summary>发送个人定向邮件（Ops 或系统内部），并向在线非机器人接收方单推最新红点摘要。</summary>
    public async ValueTask<(Guid PlayerId, Guid MailId, MailSummaryDto Summary)> SendPlayerMailAsync(
        OpsSendPlayerMailRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.TargetPlayerId) && string.IsNullOrWhiteSpace(request.TargetDisplayName))
            throw new MailOperationException(MailOperationFailure.InvalidArgument, "请指定收件角色 UUID 或道号。");

        Guid? parsedTargetId = null;
        if (!string.IsNullOrWhiteSpace(request.TargetPlayerId))
        {
            if (!Guid.TryParse(request.TargetPlayerId.Trim(), out var targetGuid) || targetGuid == Guid.Empty)
                throw new MailOperationException(MailOperationFailure.InvalidArgument, "收件角色 UUID 格式无效。");
            parsedTargetId = targetGuid;
        }

        var (title, body, sender, attachments, expiresAt) = ValidateContent(
            request.Title, request.Body, request.SenderName, request.Attachments, request.ExpireDays, request.NeverExpire, now: out var now);
        var broadcasts = await GetActiveBroadcastsAsync(ct).ConfigureAwait(false);
        var outcome = await repository.SendPersonalMailAsync(
            parsedTargetId,
            request.TargetDisplayName?.Trim(),
            title,
            body,
            sender,
            attachments,
            now,
            expiresAt,
            broadcasts,
            ct).ConfigureAwait(false);

        PushSummaryToSession(outcome.PlayerId, outcome.Summary);
        return outcome;
    }

    /// <summary>发送全服群发公告（只落 1 行并刷共享缓存），单帧广播在线非机器人会话，零 DB 扇出风暴。</summary>
    public async ValueTask<BroadcastMailRecord> SendBroadcastMailAsync(
        OpsSendBroadcastMailRequest request,
        CancellationToken ct = default)
    {
        var (title, body, sender, attachments, expiresAt) = ValidateContent(
            request.Title, request.Body, request.SenderName, request.Attachments, request.ExpireDays, request.NeverExpire, now: out var now);

        var created = await repository.InsertBroadcastMailAsync(
            title,
            body,
            sender,
            attachments,
            request.OnlyExistingPlayers,
            now,
            expiresAt,
            ct).ConfigureAwait(false);

        await ForceRefreshBroadcastCacheAsync(CancellationToken.None).ConfigureAwait(false);

        if (sessions is not null)
        {
            // 零扇出优化：全服群发仅序列化 1 次轻帧载荷，逐在线连接投递点亮红点，绝不为几千连接回查数据库。
            var frameBytes = (ReadOnlyMemory<byte>)JsonSerializer.SerializeToUtf8Bytes(
                new MailSummaryDto(0, 0, ForceRedDot: true), ProtocolJson.Options);
            foreach (var session in sessions.SnapshotSessions())
            {
                if (session.IsBot) continue;
                if (created.OnlyExistingPlayers && session.AccountCreatedAt > created.CreatedAt) continue;
                try { session.TrySendDroppableEnvelope(WireMessageType.MailSummary, Guid.NewGuid(), frameBytes); }
                catch { /* 单会话通道异常或已关闭，忽略以防阻断其余在线连接 */ }
            }
        }

        return created;
    }

    /// <summary>撤销全服群发公告并立即刷新共享缓存。</summary>
    public async ValueTask<bool> RevokeBroadcastMailAsync(long broadcastId, CancellationToken ct = default)
    {
        if (broadcastId <= 0)
            throw new MailOperationException(MailOperationFailure.InvalidArgument, "群发公告 ID 无效。");
        var revoked = await repository.RevokeBroadcastMailAsync(broadcastId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        if (revoked)
            await ForceRefreshBroadcastCacheAsync(CancellationToken.None).ConfigureAwait(false);
        return revoked;
    }

    /// <summary>运营后台查询最近全服群发公告清单。</summary>
    public ValueTask<IReadOnlyList<OpsBroadcastMailDto>> ListOpsBroadcastsAsync(CancellationToken ct = default)
        => repository.ListOpsBroadcastsAsync(DateTimeOffset.UtcNow, ct);

    internal async ValueTask ForceRefreshBroadcastCacheAsync(CancellationToken ct = default)
    {
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ReloadCacheLockedAsync(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private async ValueTask<ImmutableArray<BroadcastMailRecord>> ReloadCacheLockedAsync(DateTimeOffset now, CancellationToken ct)
    {
        var loaded = await repository.LoadActiveBroadcastsAsync(now, ct).ConfigureAwait(false);
        _activeBroadcasts = loaded;
        // 缓存自动失效锚点取「最早到期时刻」与「5 分钟保底轮巡」的较早者。
        var nextExpiry = now.AddMinutes(5);
        foreach (var item in loaded)
        {
            if (item.ExpiresAt is { } exp && exp < nextExpiry) nextExpiry = exp;
        }
        Interlocked.Exchange(ref _cacheExpiresAtUtcTicks, nextExpiry.UtcTicks);
        Interlocked.Exchange(ref _cacheInitialized, 1);
        return loaded;
    }

    private void PushSummaryToSession(Guid playerId, MailSummaryDto summary)
    {
        if (sessions is null || !sessions.TryGet(playerId, out var session) || session.IsBot) return;
        var bytes = (ReadOnlyMemory<byte>)JsonSerializer.SerializeToUtf8Bytes(summary, ProtocolJson.Options);
        session.TrySendDroppableEnvelope(WireMessageType.MailSummary, Guid.NewGuid(), bytes);
    }

    private (string Title, string Body, string SenderName, IReadOnlyList<MailAttachmentDto> Attachments, DateTimeOffset? ExpiresAt) ValidateContent(
        string? rawTitle,
        string? rawBody,
        string? rawSender,
        IReadOnlyList<MailAttachmentDto>? rawAttachments,
        int? rawExpireDays,
        bool neverExpire,
        out DateTimeOffset now)
    {
        now = DateTimeOffset.UtcNow;
        var title = (rawTitle ?? string.Empty).Trim();
        if (title.Length == 0 || title.Length > MailRules.MaxTitleLength)
            throw new MailOperationException(MailOperationFailure.InvalidArgument, $"邮件标题长度必须在 1~{MailRules.MaxTitleLength} 字之间。");

        var body = (rawBody ?? string.Empty).Trim();
        if (body.Length == 0 || body.Length > MailRules.MaxBodyLength)
            throw new MailOperationException(MailOperationFailure.InvalidArgument, $"邮件正文长度必须在 1~{MailRules.MaxBodyLength} 字之间。");

        var sender = string.IsNullOrWhiteSpace(rawSender) ? MailRules.DefaultSenderName : rawSender.Trim();
        if (sender.Length > MailRules.MaxSenderLength)
            throw new MailOperationException(MailOperationFailure.InvalidArgument, $"发件署名长度不能超过 {MailRules.MaxSenderLength} 字。");

        DateTimeOffset? expiresAt = null;
        if (!neverExpire)
        {
            var expireDays = rawExpireDays ?? MailRules.DefaultExpireDays;
            if (expireDays < 1 || expireDays > MailRules.MaxExpireDays)
                throw new MailOperationException(MailOperationFailure.InvalidArgument, $"有效天数必须在 1~{MailRules.MaxExpireDays} 天之间。");
            expiresAt = now.AddDays(expireDays);
        }

        var merged = new Dictionary<string, double>(StringComparer.Ordinal);
        if (rawAttachments is { Count: > 0 })
        {
            if (rawAttachments.Count > MailRules.MaxAttachmentsPerMail)
                throw new MailOperationException(MailOperationFailure.InvalidArgument, $"单封邮件最多附带 {MailRules.MaxAttachmentsPerMail} 种附件。");

            foreach (var entry in rawAttachments)
            {
                var itemId = (entry.ItemId ?? string.Empty).Trim();
                if (itemId.Length == 0)
                    throw new MailOperationException(MailOperationFailure.InvalidArgument, "附件物品 ID 不能为空。");
                var knownAsset = gameData.Items.ContainsKey(new ItemId(itemId))
                    || gameData.RawItems.ContainsKey(itemId)
                    || gameData.RawEquipments.ContainsKey(itemId);
                if (!knownAsset)
                    throw new MailOperationException(MailOperationFailure.InvalidArgument, $"附件物品不存在于当前配表：{itemId}");
                if (!double.IsFinite(entry.Quantity) || entry.Quantity <= 0 || entry.Quantity > 1_000_000_000d)
                    throw new MailOperationException(MailOperationFailure.InvalidArgument, $"附件 {itemId} 数量必须是大于 0 的有限数值。");

                merged[itemId] = merged.GetValueOrDefault(itemId) + entry.Quantity;
                if (merged.Count > MailRules.MaxAttachmentsPerMail)
                    throw new MailOperationException(MailOperationFailure.InvalidArgument, $"单封邮件最多附带 {MailRules.MaxAttachmentsPerMail} 种附件。");
            }
        }

        var normalizedAttachments = merged
            .Select(pair => new MailAttachmentDto(pair.Key, pair.Value))
            .ToArray();
        return (title, body, sender, normalizedAttachments, expiresAt);
    }
}
