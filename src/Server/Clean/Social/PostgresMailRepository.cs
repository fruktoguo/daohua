using System.Collections.Immutable;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.GameData;
using Npgsql;
using NpgsqlTypes;
using Protocol;

namespace Server.Clean.Social;

/// <summary>
/// 双轨邮件持久化仓储（PostgreSQL）：
/// 1) `social.broadcast_mails`：全服公告只落 1 行，服务端全局共享；
/// 2) `social.player_mail_states`：玩家交互位图行按需惰性创建，无交互 0 行，写入时按活跃 ID 交集压实；
/// 3) `social.player_mails`：个人定向邮件（上限 100 封），列表查询省略正文列，删前强检未领附件；
/// 4) 附件入账沿用 <see cref="IParticipationAssetBoundary"/> → <see cref="PlayerCommitGates"/> 锁序与 Serializable 事务。
/// </summary>
public sealed class PostgresMailRepository(
    NpgsqlDataSource dataSource,
    IParticipationAssetBoundary? participationAssets = null)
{
    private const int SerializableRetryAttempts = 6;
    private static readonly TimeSpan[] SerializableRetryBackoff =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(75),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(400)
    ];

    /// <summary>加载全部未过期且未撤销的群发公告，返回共享不可变数组。</summary>
    public async ValueTask<ImmutableArray<BroadcastMailRecord>> LoadActiveBroadcastsAsync(
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT broadcast_id, title, body, sender_name, attachments::text, only_existing_players, created_at, expires_at, revoked_at
            FROM social.broadcast_mails
            WHERE revoked_at IS NULL AND (expires_at IS NULL OR expires_at > $1)
            ORDER BY created_at DESC, broadcast_id DESC
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var builder = ImmutableArray.CreateBuilder<BroadcastMailRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            builder.Add(ReadBroadcastRecord(reader));
        }
        return builder.ToImmutable();
    }

    /// <summary>读取玩家归属账号的机器人标记与创建时间（不存在返回 null）。</summary>
    public async ValueTask<(bool IsBot, DateTimeOffset AccountCreatedAt)?> GetPlayerAccountFlagsAsync(
        Guid playerId,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT a.is_bot, a.created_at
            FROM identity.players p
            JOIN identity.accounts a ON a.account_id = p.account_id
            WHERE p.player_id = $1 AND a.status = 1 AND p.status = 1
            LIMIT 1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return (reader.GetBoolean(0), reader.GetFieldValue<DateTimeOffset>(1));
    }

    /// <summary>计算轻量红点摘要（供 WS Hello 与接口回执使用）；机器人账号恒返空摘要。</summary>
    public async ValueTask<MailSummaryDto> GetSummaryAsync(
        Guid playerId,
        ImmutableArray<BroadcastMailRecord> activeBroadcasts,
        DateTimeOffset now,
        bool? knownIsBot = null,
        DateTimeOffset? knownAccountCreatedAt = null,
        CancellationToken ct = default)
    {
        var flags = await ResolveFlagsAsync(playerId, knownIsBot, knownAccountCreatedAt, ct).ConfigureAwait(false);
        if (flags is null || flags.Value.IsBot) return new MailSummaryDto(0, 0, false);

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var (readIds, claimedIds, deletedIds) = await ReadStateSetsAsync(connection, null, playerId, ct).ConfigureAwait(false);

        int unreadCount = 0;
        int claimableCount = 0;

        foreach (var broadcast in activeBroadcasts)
        {
            if (!broadcast.IsEligibleFor(flags.Value.AccountCreatedAt, now)) continue;
            if (deletedIds.Contains(broadcast.BroadcastId)) continue;
            if (!readIds.Contains(broadcast.BroadcastId)) unreadCount++;
            if (broadcast.HasAttachments && !claimedIds.Contains(broadcast.BroadcastId)) claimableCount++;
        }

        await using var personalCommand = new NpgsqlCommand("""
            SELECT
                count(*) FILTER (WHERE NOT is_read)::integer,
                count(*) FILTER (WHERE has_attachments AND NOT is_claimed)::integer
            FROM social.player_mails
            WHERE player_id = $1 AND (expires_at IS NULL OR expires_at > $2)
            """, connection);
        personalCommand.Parameters.Add(new NpgsqlParameter { Value = playerId });
        personalCommand.Parameters.Add(new NpgsqlParameter { Value = now });
        await using var reader = await personalCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            unreadCount += reader.GetInt32(0);
            claimableCount += reader.GetInt32(1);
        }

        return new MailSummaryDto(unreadCount, claimableCount, false);
    }

    /// <summary>读取邮件列表（不查/不下发个人正文列 body，节省带宽与分配）。</summary>
    public async ValueTask<MailListResponseDto> ListMailsAsync(
        Guid playerId,
        ImmutableArray<BroadcastMailRecord> activeBroadcasts,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var flags = await GetPlayerAccountFlagsAsync(playerId, ct).ConfigureAwait(false);
        if (flags is null || flags.Value.IsBot)
            return new MailListResponseDto([], new MailSummaryDto(0, 0, false));

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var (readIds, claimedIds, deletedIds) = await ReadStateSetsAsync(connection, null, playerId, ct).ConfigureAwait(false);

        var items = new List<MailListItemDto>(activeBroadcasts.Length + 16);
        var unreadCount = 0;
        var claimableCount = 0;

        foreach (var broadcast in activeBroadcasts)
        {
            if (!broadcast.IsEligibleFor(flags.Value.AccountCreatedAt, now)) continue;
            if (deletedIds.Contains(broadcast.BroadcastId)) continue;
            var isRead = readIds.Contains(broadcast.BroadcastId);
            var isClaimed = !broadcast.HasAttachments || claimedIds.Contains(broadcast.BroadcastId);
            if (!isRead) unreadCount++;
            if (broadcast.HasAttachments && !isClaimed) claimableCount++;
            items.Add(new MailListItemDto(
                broadcast.MailKey,
                "broadcast",
                broadcast.Title,
                broadcast.SenderName,
                broadcast.CreatedAt,
                broadcast.ExpiresAt,
                broadcast.HasAttachments,
                isRead,
                isClaimed,
                broadcast.OnlyExistingPlayers,
                broadcast.Attachments));
        }

        // 列表查询显式省略 body 列
        await using (var command = new NpgsqlCommand("""
            SELECT mail_id, title, sender_name, attachments::text, has_attachments, is_read, is_claimed, created_at, expires_at
            FROM social.player_mails
            WHERE player_id = $1 AND (expires_at IS NULL OR expires_at > $2)
            ORDER BY created_at DESC, mail_id DESC
            LIMIT $3
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            command.Parameters.Add(new NpgsqlParameter { Value = now });
            command.Parameters.Add(new NpgsqlParameter { Value = MailRules.MaxPlayerMails });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var mailId = reader.GetGuid(0);
                var title = reader.GetString(1);
                var senderName = reader.GetString(2);
                var attachments = DeserializeAttachments(reader.GetString(3));
                var hasAttachments = reader.GetBoolean(4);
                var isRead = reader.GetBoolean(5);
                var isClaimed = reader.GetBoolean(6);
                var createdAt = reader.GetFieldValue<DateTimeOffset>(7);
                var expiresAt = reader.IsDBNull(8) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(8);
                if (!isRead) unreadCount++;
                if (hasAttachments && !isClaimed) claimableCount++;
                items.Add(new MailListItemDto(
                    MailKeyCodec.FormatPersonal(mailId),
                    "personal",
                    title,
                    senderName,
                    createdAt,
                    expiresAt,
                    hasAttachments,
                    isRead,
                    isClaimed,
                    false,
                    attachments));
            }
        }

        items.Sort(static (a, b) =>
        {
            var timeCmp = b.CreatedAt.CompareTo(a.CreatedAt);
            return timeCmp != 0 ? timeCmp : string.CompareOrdinal(a.MailKey, b.MailKey);
        });
        return new MailListResponseDto(items, new MailSummaryDto(unreadCount, claimableCount, false));
    }

    /// <summary>按需读取单封邮件正文并在未读时自动标为已读。</summary>
    public async ValueTask<MailDetailResponseDto> GetDetailAndMarkReadAsync(
        Guid playerId,
        string mailKey,
        ImmutableArray<BroadcastMailRecord> activeBroadcasts,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (!MailKeyCodec.TryParse(mailKey, out var kind, out var broadcastId, out var personalMailId))
            throw new MailOperationException(MailOperationFailure.InvalidArgument, "邮件标识格式无效。");

        var flags = await GetPlayerAccountFlagsAsync(playerId, ct).ConfigureAwait(false)
            ?? throw new MailOperationException(MailOperationFailure.NotFound, "角色不存在。");
        if (flags.IsBot)
            throw new MailOperationException(MailOperationFailure.BotRestricted, "系统或机器人账号不支持读取邮件。");

        if (kind == MailTrackKind.Broadcast)
        {
            var broadcast = activeBroadcasts.FirstOrDefault(item => item.BroadcastId == broadcastId);
            if (broadcast is null || !broadcast.IsEligibleFor(flags.AccountCreatedAt, now))
                throw new MailOperationException(MailOperationFailure.NotFound, "该传书不存在或已过期。");

            await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await LockPlayerStateAsync(connection, tx, playerId, ct).ConfigureAwait(false);
            var (readIds, claimedIds, deletedIds) = await ReadStateSetsAsync(connection, tx, playerId, ct).ConfigureAwait(false);
            if (deletedIds.Contains(broadcastId))
                throw new MailOperationException(MailOperationFailure.NotFound, "该传书已被删除。");

            var changed = readIds.Add(broadcastId);
            if (changed)
            {
                var activeIds = await LoadActiveBroadcastIdSetAsync(connection, tx, now, ct).ConfigureAwait(false);
                await SaveCompactedStateAsync(connection, tx, playerId, readIds, claimedIds, deletedIds, activeIds, now, ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);

            var detail = new MailDetailDto(
                broadcast.MailKey,
                "broadcast",
                broadcast.Title,
                broadcast.Body,
                broadcast.SenderName,
                broadcast.CreatedAt,
                broadcast.ExpiresAt,
                broadcast.HasAttachments,
                true,
                !broadcast.HasAttachments || claimedIds.Contains(broadcast.BroadcastId),
                broadcast.OnlyExistingPlayers,
                broadcast.Attachments);
            var summary = await GetSummaryAsync(playerId, activeBroadcasts, now, flags.IsBot, flags.AccountCreatedAt, ct).ConfigureAwait(false);
            return new MailDetailResponseDto(detail, summary);
        }

        await using (var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("""
                UPDATE social.player_mails
                   SET is_read = true
                 WHERE mail_id = $1 AND player_id = $2 AND (expires_at IS NULL OR expires_at > $3)
                RETURNING title, body, sender_name, attachments::text, has_attachments, is_claimed, created_at, expires_at
                """, connection, tx);
            command.Parameters.Add(new NpgsqlParameter { Value = personalMailId });
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            command.Parameters.Add(new NpgsqlParameter { Value = now });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                throw new MailOperationException(MailOperationFailure.NotFound, "该传书不存在或已过期。");

            var attachments = DeserializeAttachments(reader.GetString(3));
            var hasAttachments = reader.GetBoolean(4);
            var detail = new MailDetailDto(
                MailKeyCodec.FormatPersonal(personalMailId),
                "personal",
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                hasAttachments,
                true,
                !hasAttachments || reader.GetBoolean(5),
                false,
                attachments);
            await reader.DisposeAsync().ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            var summary = await GetSummaryAsync(playerId, activeBroadcasts, now, flags.IsBot, flags.AccountCreatedAt, ct).ConfigureAwait(false);
            return new MailDetailResponseDto(detail, summary);
        }
    }

    /// <summary>显式标记单封邮件为已读并压实群发状态。</summary>
    public async ValueTask<MailReadResponseDto> MarkReadAsync(
        Guid playerId,
        string mailKey,
        ImmutableArray<BroadcastMailRecord> activeBroadcasts,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var detail = await GetDetailAndMarkReadAsync(playerId, mailKey, activeBroadcasts, now, ct).ConfigureAwait(false);
        return new MailReadResponseDto(detail.Mail.MailKey, detail.Summary);
    }

    /// <summary>领取单封邮件附件（Strict Serializable + ParticipationAssetBoundary + PlayerCommitGates 锁序）。</summary>
    public async ValueTask<MailClaimResponseDto> ClaimSingleAsync(
        Guid playerId,
        string mailKey,
        ImmutableArray<BroadcastMailRecord> activeBroadcasts,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (!MailKeyCodec.TryParse(mailKey, out var kind, out var broadcastId, out var personalMailId))
            throw new MailOperationException(MailOperationFailure.InvalidArgument, "邮件标识格式无效。");

        var flags = await GetPlayerAccountFlagsAsync(playerId, ct).ConfigureAwait(false)
            ?? throw new MailOperationException(MailOperationFailure.NotFound, "角色不存在。");
        if (flags.IsBot)
            throw new MailOperationException(MailOperationFailure.BotRestricted, "系统或机器人账号禁止领取邮件附件。");

        IReadOnlyList<MailAttachmentDto> previewAttachments;
        if (kind == MailTrackKind.Broadcast)
        {
            var broadcast = activeBroadcasts.FirstOrDefault(item => item.BroadcastId == broadcastId);
            if (broadcast is null || !broadcast.IsEligibleFor(flags.AccountCreatedAt, now))
                throw new MailOperationException(MailOperationFailure.NotFound, "该传书不存在或已过期。");
            if (!broadcast.HasAttachments)
            {
                var emptySummary = await GetSummaryAsync(playerId, activeBroadcasts, now, flags.IsBot, flags.AccountCreatedAt, ct).ConfigureAwait(false);
                return new MailClaimResponseDto([], [], emptySummary);
            }
            previewAttachments = broadcast.Attachments;
        }
        else
        {
            await using var preConn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var preCmd = new NpgsqlCommand("""
                SELECT attachments::text, has_attachments, is_claimed
                FROM social.player_mails
                WHERE mail_id = $1 AND player_id = $2 AND (expires_at IS NULL OR expires_at > $3)
                LIMIT 1
                """, preConn);
            preCmd.Parameters.Add(new NpgsqlParameter { Value = personalMailId });
            preCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
            preCmd.Parameters.Add(new NpgsqlParameter { Value = now });
            await using var reader = await preCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                throw new MailOperationException(MailOperationFailure.NotFound, "该传书不存在或已过期。");
            var hasAttachments = reader.GetBoolean(1);
            var isClaimed = reader.GetBoolean(2);
            previewAttachments = DeserializeAttachments(reader.GetString(0));
            if (!hasAttachments || isClaimed || previewAttachments.Count == 0)
            {
                var emptySummary = await GetSummaryAsync(playerId, activeBroadcasts, now, flags.IsBot, flags.AccountCreatedAt, ct).ConfigureAwait(false);
                return new MailClaimResponseDto([], [], emptySummary);
            }
        }

        var assetKeys = previewAttachments
            .Select(att => new ParticipationAssetKey(playerId, att.ItemId, string.Empty))
            .Distinct()
            .ToArray();
        using var participationScope = participationAssets is not null && assetKeys.Length > 0
            ? await participationAssets.AcquireAsync(assetKeys, ct).ConfigureAwait(false)
            : null;
        var stripes = PlayerCommitGates.StripesFor([playerId]);
        await PlayerCommitGates.AcquireAllAsync(stripes, ct).ConfigureAwait(false);
        try
        {
            var claimed = await ExecuteClaimTransactionWithRetryAsync(
                playerId,
                onlyTargetKey: mailKey,
                expectedKind: kind,
                targetBroadcastId: broadcastId,
                targetPersonalId: personalMailId,
                candidateAttachments: previewAttachments,
                accountCreatedAt: flags.AccountCreatedAt,
                now: now,
                ct: ct).ConfigureAwait(false);
            var summary = await GetSummaryAsync(playerId, activeBroadcasts, now, flags.IsBot, flags.AccountCreatedAt, ct).ConfigureAwait(false);
            return new MailClaimResponseDto(claimed.ClaimedKeys, claimed.GrantedAttachments, summary);
        }
        finally
        {
            PlayerCommitGates.ReleaseAll(stripes);
        }
    }

    /// <summary>一键领取全部当前可领取附件（单事务原子入账 + 幂等防重）。</summary>
    public async ValueTask<MailClaimResponseDto> ClaimAllAsync(
        Guid playerId,
        ImmutableArray<BroadcastMailRecord> activeBroadcasts,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var flags = await GetPlayerAccountFlagsAsync(playerId, ct).ConfigureAwait(false)
            ?? throw new MailOperationException(MailOperationFailure.NotFound, "角色不存在。");
        if (flags.IsBot)
            throw new MailOperationException(MailOperationFailure.BotRestricted, "系统或机器人账号禁止领取邮件附件。");

        // 先无锁采样候选键集合，仅在确实存在待领附件时获取跨组件资产界线与玩家提交门。
        var previewAttachments = new List<MailAttachmentDto>();
        await using (var preConn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            var (_, claimedIds, deletedIds) = await ReadStateSetsAsync(preConn, null, playerId, ct).ConfigureAwait(false);
            foreach (var broadcast in activeBroadcasts)
            {
                if (!broadcast.HasAttachments || !broadcast.IsEligibleFor(flags.AccountCreatedAt, now)) continue;
                if (deletedIds.Contains(broadcast.BroadcastId) || claimedIds.Contains(broadcast.BroadcastId)) continue;
                previewAttachments.AddRange(broadcast.Attachments);
            }

            await using var cmd = new NpgsqlCommand("""
                SELECT attachments::text
                FROM social.player_mails
                WHERE player_id = $1 AND (expires_at IS NULL OR expires_at > $2) AND has_attachments AND NOT is_claimed
                """, preConn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = now });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                previewAttachments.AddRange(DeserializeAttachments(reader.GetString(0)));
            }
        }

        if (previewAttachments.Count == 0)
        {
            var emptySummary = await GetSummaryAsync(playerId, activeBroadcasts, now, flags.IsBot, flags.AccountCreatedAt, ct).ConfigureAwait(false);
            return new MailClaimResponseDto([], [], emptySummary);
        }

        var assetKeys = previewAttachments
            .Select(att => new ParticipationAssetKey(playerId, att.ItemId, string.Empty))
            .Distinct()
            .ToArray();
        using var participationScope = participationAssets is not null && assetKeys.Length > 0
            ? await participationAssets.AcquireAsync(assetKeys, ct).ConfigureAwait(false)
            : null;
        var stripes = PlayerCommitGates.StripesFor([playerId]);
        await PlayerCommitGates.AcquireAllAsync(stripes, ct).ConfigureAwait(false);
        try
        {
            var coveredAssets = assetKeys.Select(k => k.AssetId).ToHashSet(StringComparer.Ordinal);
            var claimed = await ExecuteClaimAllTransactionWithRetryAsync(
                playerId,
                flags.AccountCreatedAt,
                coveredAssets,
                now,
                ct).ConfigureAwait(false);
            var summary = await GetSummaryAsync(playerId, activeBroadcasts, now, flags.IsBot, flags.AccountCreatedAt, ct).ConfigureAwait(false);
            return new MailClaimResponseDto(claimed.ClaimedKeys, claimed.GrantedAttachments, summary);
        }
        finally
        {
            PlayerCommitGates.ReleaseAll(stripes);
        }
    }

    /// <summary>删除单封邮件；存在未领取附件时拒绝删除。</summary>
    public async ValueTask<MailDeleteResponseDto> DeleteSingleAsync(
        Guid playerId,
        string mailKey,
        ImmutableArray<BroadcastMailRecord> activeBroadcasts,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (!MailKeyCodec.TryParse(mailKey, out var kind, out var broadcastId, out var personalMailId))
            throw new MailOperationException(MailOperationFailure.InvalidArgument, "邮件标识格式无效。");

        var flags = await GetPlayerAccountFlagsAsync(playerId, ct).ConfigureAwait(false)
            ?? throw new MailOperationException(MailOperationFailure.NotFound, "角色不存在。");

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        if (kind == MailTrackKind.Personal)
        {
            bool hasAttachments;
            bool isClaimed;
            await using (var checkCmd = new NpgsqlCommand("""
                SELECT has_attachments, is_claimed
                FROM social.player_mails
                WHERE mail_id = $1 AND player_id = $2
                FOR UPDATE
                """, connection, tx))
            {
                checkCmd.Parameters.Add(new NpgsqlParameter { Value = personalMailId });
                checkCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
                await using var reader = await checkCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    throw new MailOperationException(MailOperationFailure.NotFound, "该传书不存在或已被删除。");
                hasAttachments = reader.GetBoolean(0);
                isClaimed = reader.GetBoolean(1);
            }

            if (hasAttachments && !isClaimed)
                throw new MailOperationException(MailOperationFailure.HasUnclaimedAttachments, "邮件尚有未领取的附件，请先领取后再删除。");

            await using (var deleteCmd = new NpgsqlCommand("""
                DELETE FROM social.player_mails
                WHERE mail_id = $1 AND player_id = $2
                """, connection, tx))
            {
                deleteCmd.Parameters.Add(new NpgsqlParameter { Value = personalMailId });
                deleteCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
                await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            var summary = await GetSummaryAsync(playerId, activeBroadcasts, now, flags.IsBot, flags.AccountCreatedAt, ct).ConfigureAwait(false);
            return new MailDeleteResponseDto([MailKeyCodec.FormatPersonal(personalMailId)], 0, summary);
        }

        var broadcast = activeBroadcasts.FirstOrDefault(item => item.BroadcastId == broadcastId);
        if (broadcast is null || !broadcast.IsEligibleFor(flags.AccountCreatedAt, now))
            throw new MailOperationException(MailOperationFailure.NotFound, "该传书不存在或已过期。");

        await LockPlayerStateAsync(connection, tx, playerId, ct).ConfigureAwait(false);
        var (readIds, claimedIds, deletedIds) = await ReadStateSetsAsync(connection, tx, playerId, ct).ConfigureAwait(false);
        if (deletedIds.Contains(broadcastId))
            throw new MailOperationException(MailOperationFailure.NotFound, "该传书已被删除。");
        if (broadcast.HasAttachments && !claimedIds.Contains(broadcastId))
            throw new MailOperationException(MailOperationFailure.HasUnclaimedAttachments, "邮件尚有未领取的附件，请先领取后再删除。");

        deletedIds.Add(broadcastId);
        readIds.Add(broadcastId);
        var activeIds = await LoadActiveBroadcastIdSetAsync(connection, tx, now, ct).ConfigureAwait(false);
        await SaveCompactedStateAsync(connection, tx, playerId, readIds, claimedIds, deletedIds, activeIds, now, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        var summaryAfter = await GetSummaryAsync(playerId, activeBroadcasts, now, flags.IsBot, flags.AccountCreatedAt, ct).ConfigureAwait(false);
        return new MailDeleteResponseDto([broadcast.MailKey], 0, summaryAfter);
    }

    /// <summary>批量删除全部「已读且无未领附件」邮件，跳过已读但仍带未领附件的邮件并统计跳过数。</summary>
    public async ValueTask<MailDeleteResponseDto> DeleteAllReadAsync(
        Guid playerId,
        ImmutableArray<BroadcastMailRecord> activeBroadcasts,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var flags = await GetPlayerAccountFlagsAsync(playerId, ct).ConfigureAwait(false)
            ?? throw new MailOperationException(MailOperationFailure.NotFound, "角色不存在。");

        var deletedKeys = new List<string>();
        var skippedUnclaimed = 0;

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockPlayerStateAsync(connection, tx, playerId, ct).ConfigureAwait(false);

        await using (var deletePersonalCmd = new NpgsqlCommand("""
            DELETE FROM social.player_mails
            WHERE player_id = $1 AND is_read AND (NOT has_attachments OR is_claimed)
            RETURNING mail_id
            """, connection, tx))
        {
            deletePersonalCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await using var reader = await deletePersonalCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                deletedKeys.Add(MailKeyCodec.FormatPersonal(reader.GetGuid(0)));
            }
        }

        await using (var skipCmd = new NpgsqlCommand("""
            SELECT count(*)::integer
            FROM social.player_mails
            WHERE player_id = $1 AND (expires_at IS NULL OR expires_at > $2) AND is_read AND has_attachments AND NOT is_claimed
            """, connection, tx))
        {
            skipCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
            skipCmd.Parameters.Add(new NpgsqlParameter { Value = now });
            var skippedObj = await skipCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            skippedUnclaimed += skippedObj is int count ? count : Convert.ToInt32(skippedObj ?? 0);
        }

        var (readIds, claimedIds, deletedIds) = await ReadStateSetsAsync(connection, tx, playerId, ct).ConfigureAwait(false);
        var activeInMemoryIds = activeBroadcasts.Select(b => b.BroadcastId).ToHashSet();
        var hasStaleState = readIds.Any(id => !activeInMemoryIds.Contains(id))
            || claimedIds.Any(id => !activeInMemoryIds.Contains(id))
            || deletedIds.Any(id => !activeInMemoryIds.Contains(id));
        var stateChanged = false;
        if (!flags.IsBot)
        {
            foreach (var broadcast in activeBroadcasts)
            {
                if (!broadcast.IsEligibleFor(flags.AccountCreatedAt, now)) continue;
                if (deletedIds.Contains(broadcast.BroadcastId) || !readIds.Contains(broadcast.BroadcastId)) continue;
                if (broadcast.HasAttachments && !claimedIds.Contains(broadcast.BroadcastId))
                {
                    skippedUnclaimed++;
                    continue;
                }
                if (deletedIds.Add(broadcast.BroadcastId))
                {
                    deletedKeys.Add(broadcast.MailKey);
                    stateChanged = true;
                }
            }
        }

        if (stateChanged || hasStaleState)
        {
            var activeIds = await LoadActiveBroadcastIdSetAsync(connection, tx, now, ct).ConfigureAwait(false);
            await SaveCompactedStateAsync(connection, tx, playerId, readIds, claimedIds, deletedIds, activeIds, now, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        var summary = await GetSummaryAsync(playerId, activeBroadcasts, now, flags.IsBot, flags.AccountCreatedAt, ct).ConfigureAwait(false);
        return new MailDeleteResponseDto(deletedKeys, skippedUnclaimed, summary);
    }

    /// <summary>向指定个人角色发送邮件；若超过个人保留上限 100 封，优先淘汰最旧的已读无未领附件行。</summary>
    public async ValueTask<(Guid PlayerId, Guid MailId, MailSummaryDto Summary)> SendPersonalMailAsync(
        Guid? targetPlayerId,
        string? targetDisplayName,
        string title,
        string body,
        string senderName,
        IReadOnlyList<MailAttachmentDto> attachments,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt,
        ImmutableArray<BroadcastMailRecord> activeBroadcasts,
        CancellationToken ct = default)
    {
        Guid resolvedPlayerId;
        bool isBot;
        DateTimeOffset accountCreatedAt;

        await using (var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var lookupCmd = targetPlayerId is { } explicitId && explicitId != Guid.Empty
                ? new NpgsqlCommand("""
                    SELECT p.player_id, a.is_bot, a.created_at
                    FROM identity.players p
                    JOIN identity.accounts a ON a.account_id = p.account_id
                    WHERE p.player_id = $1 AND p.status = 1 AND a.status = 1
                    LIMIT 1
                    """, connection)
                {
                    Parameters = { new NpgsqlParameter { Value = explicitId } }
                }
                : new NpgsqlCommand("""
                    SELECT p.player_id, a.is_bot, a.created_at
                    FROM identity.players p
                    JOIN identity.accounts a ON a.account_id = p.account_id
                    WHERE p.display_name_normalized = $1 AND p.status = 1 AND a.status = 1
                    LIMIT 1
                    """, connection)
                {
                    Parameters = { new NpgsqlParameter { Value = PostgresAccountRepository.NormalizeNameForLookup(targetDisplayName ?? string.Empty) } }
                };

            await using var reader = await lookupCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                throw new MailOperationException(MailOperationFailure.NotFound, "目标角色不存在或状态不可用。");
            resolvedPlayerId = reader.GetGuid(0);
            isBot = reader.GetBoolean(1);
            accountCreatedAt = reader.GetFieldValue<DateTimeOffset>(2);
        }

        if (isBot)
            throw new MailOperationException(MailOperationFailure.BotRestricted, "目标账号为机器人账号，禁止发送个人邮件。");

        var mailId = Guid.NewGuid();
        var hasAttachments = attachments.Count > 0;
        var attachmentsJson = SerializeAttachments(attachments);

        await using (var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await LockPlayerStateAsync(connection, tx, resolvedPlayerId, ct).ConfigureAwait(false);

            await using (var insertCmd = new NpgsqlCommand("""
                INSERT INTO social.player_mails
                    (mail_id, player_id, title, body, sender_name, attachments, has_attachments, is_read, is_claimed, created_at, expires_at)
                VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7, false, $8, $9, $10)
                """, connection, tx))
            {
                insertCmd.Parameters.Add(new NpgsqlParameter { Value = mailId });
                insertCmd.Parameters.Add(new NpgsqlParameter { Value = resolvedPlayerId });
                insertCmd.Parameters.Add(new NpgsqlParameter { Value = title });
                insertCmd.Parameters.Add(new NpgsqlParameter { Value = body });
                insertCmd.Parameters.Add(new NpgsqlParameter { Value = senderName });
                insertCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = attachmentsJson });
                insertCmd.Parameters.Add(new NpgsqlParameter { Value = hasAttachments });
                insertCmd.Parameters.Add(new NpgsqlParameter { Value = !hasAttachments });
                insertCmd.Parameters.Add(new NpgsqlParameter { Value = createdAt });
                insertCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)expiresAt ?? DBNull.Value });
                await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // 单人 100 封上限防溢出：优先删最旧「已读且无未领附件」，其次已读带附件，最后未读
            await using (var trimCmd = new NpgsqlCommand("""
                WITH overflow AS (
                    SELECT count(*)::integer - $2 AS excess
                    FROM social.player_mails
                    WHERE player_id = $1
                ),
                doomed AS (
                    SELECT m.mail_id
                    FROM social.player_mails m, overflow
                    WHERE m.player_id = $1 AND overflow.excess > 0
                    ORDER BY
                        CASE
                            WHEN m.is_read AND (NOT m.has_attachments OR m.is_claimed) THEN 0
                            WHEN m.is_read THEN 1
                            ELSE 2
                        END ASC,
                        m.created_at ASC,
                        m.mail_id ASC
                    LIMIT GREATEST(0, (SELECT excess FROM overflow))
                )
                DELETE FROM social.player_mails m
                USING doomed
                WHERE m.mail_id = doomed.mail_id
                """, connection, tx))
            {
                trimCmd.Parameters.Add(new NpgsqlParameter { Value = resolvedPlayerId });
                trimCmd.Parameters.Add(new NpgsqlParameter { Value = MailRules.MaxPlayerMails });
                await trimCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        var summary = await GetSummaryAsync(resolvedPlayerId, activeBroadcasts, createdAt, false, accountCreatedAt, ct).ConfigureAwait(false);
        return (resolvedPlayerId, mailId, summary);
    }

    /// <summary>写入一条全服群发公告（只落 1 行）。</summary>
    public async ValueTask<BroadcastMailRecord> InsertBroadcastMailAsync(
        string title,
        string body,
        string senderName,
        IReadOnlyList<MailAttachmentDto> attachments,
        bool onlyExistingPlayers,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        var attachmentsJson = SerializeAttachments(attachments);
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO social.broadcast_mails
                (title, body, sender_name, attachments, only_existing_players, created_at, expires_at, revoked_at)
            VALUES ($1, $2, $3, $4::jsonb, $5, $6, $7, NULL)
            RETURNING broadcast_id, title, body, sender_name, attachments::text, only_existing_players, created_at, expires_at, revoked_at
            """, connection);
        cmd.Parameters.Add(new NpgsqlParameter { Value = title });
        cmd.Parameters.Add(new NpgsqlParameter { Value = body });
        cmd.Parameters.Add(new NpgsqlParameter { Value = senderName });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = attachmentsJson });
        cmd.Parameters.Add(new NpgsqlParameter { Value = onlyExistingPlayers });
        cmd.Parameters.Add(new NpgsqlParameter { Value = createdAt });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)expiresAt ?? DBNull.Value });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return ReadBroadcastRecord(reader);
    }

    /// <summary>撤销一条尚未撤销的全服群发公告。</summary>
    public async ValueTask<bool> RevokeBroadcastMailAsync(long broadcastId, DateTimeOffset revokedAt, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            UPDATE social.broadcast_mails
               SET revoked_at = $2
             WHERE broadcast_id = $1 AND revoked_at IS NULL
            """, connection);
        cmd.Parameters.Add(new NpgsqlParameter { Value = broadcastId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = revokedAt });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <summary>运营后台查看最近全服群发公告列表（含生效与已撤销/过期）。</summary>
    public async ValueTask<IReadOnlyList<OpsBroadcastMailDto>> ListOpsBroadcastsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT broadcast_id, title, sender_name, jsonb_array_length(attachments)::integer, only_existing_players, created_at, expires_at, revoked_at
            FROM social.broadcast_mails
            ORDER BY created_at DESC, broadcast_id DESC
            LIMIT $1
            """, connection);
        cmd.Parameters.Add(new NpgsqlParameter { Value = MailRules.OpsBroadcastListLimit });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<OpsBroadcastMailDto>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var createdAt = reader.GetFieldValue<DateTimeOffset>(5);
            var expiresAt = reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6);
            var revokedAt = reader.IsDBNull(7) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(7);
            list.Add(new OpsBroadcastMailDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetBoolean(4),
                createdAt,
                expiresAt,
                revokedAt,
                revokedAt is null && (expiresAt is null || expiresAt > now)));
        }
        return list;
    }

    private async ValueTask<(IReadOnlyList<string> ClaimedKeys, IReadOnlyList<MailAttachmentDto> GrantedAttachments)> ExecuteClaimTransactionWithRetryAsync(
        Guid playerId,
        string onlyTargetKey,
        MailTrackKind expectedKind,
        long targetBroadcastId,
        Guid targetPersonalId,
        IReadOnlyList<MailAttachmentDto> candidateAttachments,
        DateTimeOffset accountCreatedAt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var txId = Guid.NewGuid();
        var idempotencyKey = $"mail.claim:{playerId:D}:{onlyTargetKey}";
        EconomyCommitOutcomeUnknownException? unknown = null;

        for (var attempt = 1; ; attempt++)
        {
            using var confirmation = unknown is null ? null : new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var token = confirmation?.Token ?? ct;
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
                await LockPlayerStateAsync(connection, transaction, playerId, token).ConfigureAwait(false);

                // 仅在提交结果未知重放确认时检查幂等记录；正常重复领取请求直接走下方状态校验返回空集合。
                if (unknown is not null)
                {
                    var existing = await PostgresEconomyRepository.TryLoadCommittedAsync(connection, transaction, idempotencyKey, token).ConfigureAwait(false);
                    if (existing is not null)
                    {
                        await transaction.RollbackAsync(token).ConfigureAwait(false);
                        var replayAttachments = MergeAttachments(candidateAttachments);
                        return ([onlyTargetKey], replayAttachments);
                    }
                }

                IReadOnlyList<MailAttachmentDto> actualAttachments;
                if (expectedKind == MailTrackKind.Personal)
                {
                    await using var lockCmd = new NpgsqlCommand("""
                        SELECT attachments::text, has_attachments, is_claimed
                        FROM social.player_mails
                        WHERE mail_id = $1 AND player_id = $2 AND (expires_at IS NULL OR expires_at > $3)
                        FOR UPDATE
                        """, connection, transaction);
                    lockCmd.Parameters.Add(new NpgsqlParameter { Value = targetPersonalId });
                    lockCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
                    lockCmd.Parameters.Add(new NpgsqlParameter { Value = now });
                    await using var reader = await lockCmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                        throw new MailOperationException(MailOperationFailure.NotFound, "该传书不存在或已过期。");
                    var hasAtt = reader.GetBoolean(1);
                    var isClaimed = reader.GetBoolean(2);
                    actualAttachments = DeserializeAttachments(reader.GetString(0));
                    if (!hasAtt || isClaimed || actualAttachments.Count == 0)
                    {
                        await reader.DisposeAsync().ConfigureAwait(false);
                        await transaction.RollbackAsync(token).ConfigureAwait(false);
                        return ([], []);
                    }
                    await reader.DisposeAsync().ConfigureAwait(false);

                    await using var updateCmd = new NpgsqlCommand("""
                        UPDATE social.player_mails
                           SET is_read = true, is_claimed = true
                         WHERE mail_id = $1 AND player_id = $2
                        """, connection, transaction);
                    updateCmd.Parameters.Add(new NpgsqlParameter { Value = targetPersonalId });
                    updateCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
                    await updateCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                else
                {
                    BroadcastMailRecord? freshBroadcast = null;
                    await using (var bCmd = new NpgsqlCommand("""
                        SELECT broadcast_id, title, body, sender_name, attachments::text, only_existing_players, created_at, expires_at, revoked_at
                        FROM social.broadcast_mails
                        WHERE broadcast_id = $1 AND revoked_at IS NULL AND (expires_at IS NULL OR expires_at > $2)
                        LIMIT 1
                        """, connection, transaction))
                    {
                        bCmd.Parameters.Add(new NpgsqlParameter { Value = targetBroadcastId });
                        bCmd.Parameters.Add(new NpgsqlParameter { Value = now });
                        await using var reader = await bCmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            freshBroadcast = ReadBroadcastRecord(reader);
                    }
                    if (freshBroadcast is null || !freshBroadcast.IsEligibleFor(accountCreatedAt, now))
                        throw new MailOperationException(MailOperationFailure.NotFound, "该传书不存在或已过期。");

                    var (readIds, claimedIds, deletedIds) = await ReadStateSetsAsync(connection, transaction, playerId, token, forUpdate: true).ConfigureAwait(false);
                    if (deletedIds.Contains(targetBroadcastId))
                        throw new MailOperationException(MailOperationFailure.NotFound, "该传书已被删除。");
                    if (!freshBroadcast.HasAttachments || claimedIds.Contains(targetBroadcastId))
                    {
                        await transaction.RollbackAsync(token).ConfigureAwait(false);
                        return ([], []);
                    }

                    actualAttachments = freshBroadcast.Attachments;
                    readIds.Add(targetBroadcastId);
                    claimedIds.Add(targetBroadcastId);
                    var activeIds = await LoadActiveBroadcastIdSetAsync(connection, transaction, now, token).ConfigureAwait(false);
                    await SaveCompactedStateAsync(connection, transaction, playerId, readIds, claimedIds, deletedIds, activeIds, now, token).ConfigureAwait(false);
                }

                var merged = MergeAttachments(actualAttachments);
                var deltas = merged.Select(att => new AssetDelta(playerId, att.ItemId, string.Empty, att.Quantity)).ToArray();
                var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(new { mailKeys = new[] { onlyTargetKey } }, ProtocolJson.Options);
                var ecoRequest = new EconomyTransactionRequest(
                    txId,
                    idempotencyKey,
                    "mail.claim",
                    playerId,
                    deltas,
                    now,
                    payloadBytes,
                    PersistLedger: true);
                await PostgresEconomyRepository.CommitCoreAsync(connection, transaction, ecoRequest, token).ConfigureAwait(false);
                await PostgresEconomyRepository.CommitTransactionAsync(transaction, txId, token).ConfigureAwait(false);
                return ([onlyTargetKey], merged);
            }
            catch (PostgresException ex) when (ex.SqlState is "40001" or "40P01" && attempt < SerializableRetryAttempts && unknown is null && !ct.IsCancellationRequested)
            {
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 25));
                await Task.Delay(SerializableRetryBackoff[attempt - 1] + jitter, ct).ConfigureAwait(false);
            }
            catch (EconomyCommitOutcomeUnknownException ex) when (unknown is null)
            {
                unknown = ex;
            }
            catch (Exception ex) when (unknown is not null)
            {
                unknown.Data["ConfirmationError"] = ex;
                throw unknown;
            }
        }
    }

    private async ValueTask<(IReadOnlyList<string> ClaimedKeys, IReadOnlyList<MailAttachmentDto> GrantedAttachments)> ExecuteClaimAllTransactionWithRetryAsync(
        Guid playerId,
        DateTimeOffset accountCreatedAt,
        HashSet<string> coveredAssets,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var txId = Guid.NewGuid();
        EconomyCommitOutcomeUnknownException? unknown = null;
        string? fixedIdempotencyKey = null;
        IReadOnlyList<string>? lastAttemptKeys = null;
        IReadOnlyList<MailAttachmentDto>? lastAttemptAttachments = null;

        for (var attempt = 1; ; attempt++)
        {
            using var confirmation = unknown is null ? null : new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var token = confirmation?.Token ?? ct;
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
                await LockPlayerStateAsync(connection, transaction, playerId, token).ConfigureAwait(false);

                if (unknown is not null && fixedIdempotencyKey is not null)
                {
                    var existing = await PostgresEconomyRepository.TryLoadCommittedAsync(connection, transaction, fixedIdempotencyKey, token).ConfigureAwait(false);
                    if (existing is not null)
                    {
                        await transaction.RollbackAsync(token).ConfigureAwait(false);
                        return (lastAttemptKeys ?? [], lastAttemptAttachments ?? []);
                    }
                }

                var (readIds, claimedIds, deletedIds) = await ReadStateSetsAsync(connection, transaction, playerId, token, forUpdate: true).ConfigureAwait(false);
                var activeIds = new HashSet<long>();
                var candidateBroadcasts = new List<BroadcastMailRecord>();

                await using (var bCmd = new NpgsqlCommand("""
                    SELECT broadcast_id, title, body, sender_name, attachments::text, only_existing_players, created_at, expires_at, revoked_at
                    FROM social.broadcast_mails
                    WHERE revoked_at IS NULL AND (expires_at IS NULL OR expires_at > $1)
                    ORDER BY broadcast_id ASC
                    """, connection, transaction))
                {
                    bCmd.Parameters.Add(new NpgsqlParameter { Value = now });
                    await using var reader = await bCmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        var rec = ReadBroadcastRecord(reader);
                        activeIds.Add(rec.BroadcastId);
                        if (rec.HasAttachments
                            && rec.IsEligibleFor(accountCreatedAt, now)
                            && !deletedIds.Contains(rec.BroadcastId)
                            && !claimedIds.Contains(rec.BroadcastId)
                            && rec.Attachments.All(att => coveredAssets.Contains(att.ItemId)))
                        {
                            candidateBroadcasts.Add(rec);
                        }
                    }
                }

                var candidatePersonals = new List<(Guid MailId, IReadOnlyList<MailAttachmentDto> Attachments)>();
                await using (var pCmd = new NpgsqlCommand("""
                    SELECT mail_id, attachments::text
                    FROM social.player_mails
                    WHERE player_id = $1 AND (expires_at IS NULL OR expires_at > $2) AND has_attachments AND NOT is_claimed
                    ORDER BY mail_id ASC
                    FOR UPDATE
                    """, connection, transaction))
                {
                    pCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
                    pCmd.Parameters.Add(new NpgsqlParameter { Value = now });
                    await using var reader = await pCmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        var mailId = reader.GetGuid(0);
                        var atts = DeserializeAttachments(reader.GetString(1));
                        if (atts.Count > 0 && atts.All(att => coveredAssets.Contains(att.ItemId)))
                            candidatePersonals.Add((mailId, atts));
                    }
                }

                if (candidateBroadcasts.Count == 0 && candidatePersonals.Count == 0)
                {
                    await transaction.RollbackAsync(token).ConfigureAwait(false);
                    return ([], []);
                }

                var claimedKeys = new List<string>(candidateBroadcasts.Count + candidatePersonals.Count);
                var rawAttachments = new List<MailAttachmentDto>();

                if (candidateBroadcasts.Count > 0)
                {
                    foreach (var rec in candidateBroadcasts)
                    {
                        readIds.Add(rec.BroadcastId);
                        claimedIds.Add(rec.BroadcastId);
                        claimedKeys.Add(rec.MailKey);
                        rawAttachments.AddRange(rec.Attachments);
                    }
                    await SaveCompactedStateAsync(connection, transaction, playerId, readIds, claimedIds, deletedIds, activeIds, now, token).ConfigureAwait(false);
                }

                if (candidatePersonals.Count > 0)
                {
                    var mailIds = candidatePersonals.Select(x => x.MailId).ToArray();
                    foreach (var item in candidatePersonals)
                    {
                        claimedKeys.Add(MailKeyCodec.FormatPersonal(item.MailId));
                        rawAttachments.AddRange(item.Attachments);
                    }
                    await using var updCmd = new NpgsqlCommand("""
                        UPDATE social.player_mails
                           SET is_read = true, is_claimed = true
                         WHERE player_id = $1 AND mail_id = ANY($2)
                        """, connection, transaction);
                    updCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
                    updCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = mailIds });
                    await updCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                claimedKeys.Sort(StringComparer.Ordinal);
                fixedIdempotencyKey ??= ComputeBatchClaimIdempotencyKey(playerId, claimedKeys);

                var merged = MergeAttachments(rawAttachments);
                lastAttemptKeys = claimedKeys;
                lastAttemptAttachments = merged;
                var deltas = merged.Select(att => new AssetDelta(playerId, att.ItemId, string.Empty, att.Quantity)).ToArray();
                var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(new { mailKeys = claimedKeys }, ProtocolJson.Options);
                var ecoRequest = new EconomyTransactionRequest(
                    txId,
                    fixedIdempotencyKey,
                    "mail.claim",
                    playerId,
                    deltas,
                    now,
                    payloadBytes,
                    PersistLedger: true);
                await PostgresEconomyRepository.CommitCoreAsync(connection, transaction, ecoRequest, token).ConfigureAwait(false);
                await PostgresEconomyRepository.CommitTransactionAsync(transaction, txId, token).ConfigureAwait(false);
                return (claimedKeys, merged);
            }
            catch (PostgresException ex) when (ex.SqlState is "40001" or "40P01" && attempt < SerializableRetryAttempts && unknown is null && !ct.IsCancellationRequested)
            {
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 25));
                await Task.Delay(SerializableRetryBackoff[attempt - 1] + jitter, ct).ConfigureAwait(false);
            }
            catch (EconomyCommitOutcomeUnknownException ex) when (unknown is null)
            {
                unknown = ex;
            }
            catch (Exception ex) when (unknown is not null)
            {
                unknown.Data["ConfirmationError"] = ex;
                throw unknown;
            }
        }
    }

    private async ValueTask<(bool IsBot, DateTimeOffset AccountCreatedAt)?> ResolveFlagsAsync(
        Guid playerId,
        bool? knownIsBot,
        DateTimeOffset? knownAccountCreatedAt,
        CancellationToken ct)
    {
        if (knownIsBot is { } isBot && knownAccountCreatedAt is { } createdAt && createdAt != DateTimeOffset.MinValue && createdAt != default)
            return (isBot, createdAt);
        return await GetPlayerAccountFlagsAsync(playerId, ct).ConfigureAwait(false);
    }

    private static async ValueTask LockPlayerStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playerId,
        CancellationToken ct)
    {
        // 用独立命名域 27 对 playerId 加事务级建议锁，保护稀疏 player_mail_states 首次建行与 100 封淘汰串行。
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 27))", connection, transaction);
        cmd.Parameters.Add(new NpgsqlParameter { Value = playerId.ToString("D") });
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static async ValueTask<HashSet<long>> LoadActiveBroadcastIdSetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT broadcast_id
            FROM social.broadcast_mails
            WHERE revoked_at IS NULL AND (expires_at IS NULL OR expires_at > $1)
            """, connection, transaction);
        cmd.Parameters.Add(new NpgsqlParameter { Value = now });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var active = new HashSet<long>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            active.Add(reader.GetInt64(0));
        return active;
    }

    private static async ValueTask<(HashSet<long> ReadIds, HashSet<long> ClaimedIds, HashSet<long> DeletedIds)> ReadStateSetsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid playerId,
        CancellationToken ct,
        bool forUpdate = false)
    {
        var sql = forUpdate
            ? "SELECT read_ids, claimed_ids, deleted_ids FROM social.player_mail_states WHERE player_id = $1 FOR UPDATE"
            : "SELECT read_ids, claimed_ids, deleted_ids FROM social.player_mail_states WHERE player_id = $1";
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        cmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return ([], [], []);
        return (
            reader.GetFieldValue<long[]>(0).ToHashSet(),
            reader.GetFieldValue<long[]>(1).ToHashSet(),
            reader.GetFieldValue<long[]>(2).ToHashSet());
    }

    private static async ValueTask SaveCompactedStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playerId,
        HashSet<long> readIds,
        HashSet<long> claimedIds,
        HashSet<long> deletedIds,
        HashSet<long> activeBroadcastIds,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var compactRead = readIds.Where(activeBroadcastIds.Contains).Order().ToArray();
        var compactClaimed = claimedIds.Where(activeBroadcastIds.Contains).Order().ToArray();
        var compactDeleted = deletedIds.Where(activeBroadcastIds.Contains).Order().ToArray();

        // 若当前全部公告均已过期/撤销且三列交集皆空，直接回收稀疏行做到真 0 行占用。
        if (compactRead.Length == 0 && compactClaimed.Length == 0 && compactDeleted.Length == 0)
        {
            await using var delCmd = new NpgsqlCommand("DELETE FROM social.player_mail_states WHERE player_id = $1", connection, transaction);
            delCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return;
        }

        await using var upsertCmd = new NpgsqlCommand("""
            INSERT INTO social.player_mail_states(player_id, read_ids, claimed_ids, deleted_ids, updated_at)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (player_id) DO UPDATE SET
                read_ids = EXCLUDED.read_ids,
                claimed_ids = EXCLUDED.claimed_ids,
                deleted_ids = EXCLUDED.deleted_ids,
                updated_at = EXCLUDED.updated_at
            """, connection, transaction);
        upsertCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
        upsertCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = compactRead });
        upsertCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = compactClaimed });
        upsertCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = compactDeleted });
        upsertCmd.Parameters.Add(new NpgsqlParameter { Value = now });
        await upsertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static BroadcastMailRecord ReadBroadcastRecord(NpgsqlDataReader reader)
    {
        return new BroadcastMailRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            DeserializeAttachments(reader.GetString(4)),
            reader.GetBoolean(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));
    }

    private static string SerializeAttachments(IReadOnlyList<MailAttachmentDto> attachments)
        => attachments.Count == 0 ? "[]" : JsonSerializer.Serialize(attachments, ProtocolJson.Options);

    private static IReadOnlyList<MailAttachmentDto> DeserializeAttachments(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return [];
        return JsonSerializer.Deserialize<MailAttachmentDto[]>(json, ProtocolJson.Options) ?? [];
    }

    private static IReadOnlyList<MailAttachmentDto> MergeAttachments(IEnumerable<MailAttachmentDto> attachments)
    {
        return attachments
            .GroupBy(att => att.ItemId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new MailAttachmentDto(group.Key, group.Sum(item => item.Quantity)))
            .ToArray();
    }

    private static string ComputeBatchClaimIdempotencyKey(Guid playerId, IReadOnlyList<string> sortedMailKeys)
    {
        var joined = $"{playerId:D}:{string.Join(',', sortedMailKeys)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
        return $"mail.claim-batch:{hash}";
    }
}
