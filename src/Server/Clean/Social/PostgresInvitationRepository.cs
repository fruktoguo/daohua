namespace Server.Clean.Social;

/// <summary>
/// 邀请关系与里程碑奖励的 PostgreSQL 持久化实现。
/// 关键约束：
/// 1. 一个被邀请者终身至多一条关系（social.player_invitations 以 invitee_id 为主键）；
/// 2. 里程碑奖励的钱与领取标记在同一 Serializable 事务内提交，经济事务幂等键兜底重放；
/// 3. 邀请者可能离线：所有入账直接写余额表，不依赖对方运行时在线。
/// </summary>
public sealed class PostgresInvitationRepository(NpgsqlDataSource dataSource) : IInvitationRepository
{
    /// <inheritdoc />
    public async ValueTask<InvitationOverview?> GetOverviewAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        string? inviteCode = null;
        string? invitedByName = null;
        await using (var command = new NpgsqlCommand("""
            SELECT p.invite_code, inv.display_name
            FROM identity.players p
            LEFT JOIN social.player_invitations i ON i.invitee_id = p.player_id
            LEFT JOIN identity.players inv ON inv.player_id = i.inviter_id
            WHERE p.player_id = $1
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            inviteCode = reader.GetString(0);
            invitedByName = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var invitees = new List<InvitationInviteeRow>();
        await using (var command = new NpgsqlCommand("""
            SELECT p.display_name, i.highest_realm_level, i.qi_rewarded, i.foundation_rewarded, i.created_at
            FROM social.player_invitations i
            JOIN identity.players p ON p.player_id = i.invitee_id
            WHERE i.inviter_id = $1
            ORDER BY i.created_at DESC
            LIMIT 100
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                invitees.Add(new InvitationInviteeRow(
                    reader.GetString(0), reader.GetInt32(1), reader.GetBoolean(2), reader.GetBoolean(3),
                    reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return new InvitationOverview(inviteCode, invitees, invitedByName);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<InvitationMilestoneGrant>> GrantMilestonesAsync(
        Guid inviteeId, int realmLevel, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);

        Guid inviterId;
        int highestRealmLevel;
        bool qiRewarded;
        bool foundationRewarded;
        bool found;
        await using (var command = new NpgsqlCommand("""
            SELECT inviter_id, highest_realm_level, qi_rewarded, foundation_rewarded
            FROM social.player_invitations
            WHERE invitee_id = $1
            FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = inviteeId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            found = await reader.ReadAsync(cancellationToken);
            inviterId = found ? reader.GetGuid(0) : Guid.Empty;
            highestRealmLevel = found ? reader.GetInt32(1) : 0;
            qiRewarded = found && reader.GetBoolean(2);
            foundationRewarded = found && reader.GetBoolean(3);
        }
        // 无邀请关系：只读事务直接回滚返回（须在 reader 释放后再动事务）。
        if (!found)
        {
            await transaction.RollbackAsync(cancellationToken);
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var granted = new List<InvitationMilestoneGrant>(2);
        if (realmLevel >= InvitationRewards.QiRealmLevel && !qiRewarded)
        {
            await CommitMilestoneAsync(connection, transaction, inviteeId, inviterId,
                InvitationRewards.MilestoneQi, InvitationRewards.QiMilestoneMerit, now, cancellationToken);
            qiRewarded = true;
            granted.Add(new InvitationMilestoneGrant(InvitationRewards.MilestoneQi, inviterId, InvitationRewards.QiMilestoneMerit));
        }
        if (realmLevel >= InvitationRewards.FoundationRealmLevel && !foundationRewarded)
        {
            await CommitMilestoneAsync(connection, transaction, inviteeId, inviterId,
                InvitationRewards.MilestoneFoundation, InvitationRewards.FoundationMilestoneMerit, now, cancellationToken);
            foundationRewarded = true;
            granted.Add(new InvitationMilestoneGrant(InvitationRewards.MilestoneFoundation, inviterId, InvitationRewards.FoundationMilestoneMerit));
        }

        if (granted.Count > 0 || realmLevel > highestRealmLevel)
        {
            await using var update = new NpgsqlCommand("""
                UPDATE social.player_invitations
                SET highest_realm_level = GREATEST(highest_realm_level, $2),
                    qi_rewarded = $3,
                    foundation_rewarded = $4,
                    updated_at = $5
                WHERE invitee_id = $1
                """, connection, transaction);
            update.Parameters.Add(new NpgsqlParameter { Value = inviteeId });
            update.Parameters.Add(new NpgsqlParameter { Value = realmLevel });
            update.Parameters.Add(new NpgsqlParameter { Value = qiRewarded });
            update.Parameters.Add(new NpgsqlParameter { Value = foundationRewarded });
            update.Parameters.Add(new NpgsqlParameter { Value = now });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return granted;
    }

    /// <summary>
    /// 单里程碑双方入账：被邀请者与邀请者各得等量绑定功德；
    /// 幂等键 `invitation.milestone.{key}:{inviteeId}` 令重复执行安全返回不重复入账。
    /// </summary>
    private static async ValueTask CommitMilestoneAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid inviteeId, Guid inviterId, string milestone, double merit,
        DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var result = await PostgresEconomyRepository.CommitCoreAsync(connection, transaction,
            new EconomyTransactionRequest(
                Guid.NewGuid(),
                $"invitation.milestone.{milestone}:{inviteeId:D}",
                "invitation.milestone",
                inviteeId,
                [
                    new AssetDelta(inviteeId, InvitationRewards.BoundMeritItemId, string.Empty, merit),
                    new AssetDelta(inviterId, InvitationRewards.BoundMeritItemId, string.Empty, merit),
                ],
                occurredAt),
            cancellationToken);
        if (!result.Applied) throw new InvalidOperationException($"邀请里程碑事务未提交：{milestone} invitee={inviteeId:D}");
    }
}
