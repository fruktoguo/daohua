namespace Server.Clean.Social;

/// <summary>
/// 好友、队伍与队伍邀请的 PostgreSQL 持久化实现。
/// 关键约束：
/// 1. 好友为双向关系：接受申请时在同一事务内写入两行 friendships，解除时删除两行；
/// 2. 一名玩家最多属于一个队伍：social.party_members 以 player_id 为主键天然互斥；
/// 3. 每名玩家最多一条待处理队伍邀请：social.party_invitations 以 invitee_id 为主键；
/// 4. 所有写入都在显式事务内完成，避免出现「申请已接受但只有单向好友」的中间态。
/// </summary>
public sealed class PostgresSocialRepository(NpgsqlDataSource dataSource) : ISocialRepository
{
    /// <summary>按道号归一化精确查找玩家。</summary>
    public async ValueTask<Guid?> FindPlayerByNameAsync(string displayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT player_id
            FROM identity.players
            WHERE display_name_normalized = $1 AND status = 1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = PostgresAccountRepository.NormalizeNameForLookup(displayName) });
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid playerId ? playerId : null;
    }

    /// <summary>读取玩家道号。</summary>
    public async ValueTask<string?> GetDisplayNameAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        if (playerId == Guid.Empty) return null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT display_name FROM identity.players WHERE player_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>列出好友关系与对端道号（按道号排序）。</summary>
    public async ValueTask<IReadOnlyList<FriendRecord>> ListFriendsAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT f.friend_id, p.display_name
            FROM social.friendships f
            JOIN identity.players p ON p.player_id = f.friend_id
            WHERE f.player_id = $1
            ORDER BY p.display_name
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        var result = new List<FriendRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new FriendRecord(reader.GetGuid(0), reader.GetString(1)));
        return result;
    }

    /// <summary>列出待处理好友申请的对端视图；incoming 为 true 时返回收到的，否则返回发出的。</summary>
    public async ValueTask<IReadOnlyList<FriendRequestView>> ListPendingRequestsAsync(Guid playerId, bool incoming, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT r.request_id,
                   CASE WHEN $2 THEN r.requester_id ELSE r.target_id END,
                   p.display_name,
                   r.created_at
            FROM social.friend_requests r
            JOIN identity.players p
              ON p.player_id = CASE WHEN $2 THEN r.requester_id ELSE r.target_id END
            WHERE r.status = 'pending'
              AND (CASE WHEN $2 THEN r.target_id ELSE r.requester_id END) = $1
            ORDER BY r.created_at
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = incoming });
        var result = new List<FriendRequestView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new FriendRequestView(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        return result;
    }

    /// <summary>
    /// 一次往返读取社交状态快照：把好友、收/发申请、队伍 ID、队长、成员与邀请共 7 条查询
    /// 打包进同一连接上的 NpgsqlBatch，替代原先 6 次串行往返。
    /// 队伍头与成员通过「玩家当前队伍」子查询定位，免去先取 party_id 再查第二轮的依赖往返；
    /// 玩家未组队或 parties 行缺失时对应结果集为空，拼装语义与逐条查询完全一致。
    /// </summary>
    public async ValueTask<SocialStateSnapshot> LoadStateSnapshotAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection);

        static NpgsqlBatchCommand Command(string sql, Guid playerId)
        {
            var command = new NpgsqlBatchCommand(sql);
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            return command;
        }

        batch.BatchCommands.Add(Command("""
            SELECT f.friend_id, p.display_name
            FROM social.friendships f
            JOIN identity.players p ON p.player_id = f.friend_id
            WHERE f.player_id = $1
            ORDER BY p.display_name
            """, playerId));
        batch.BatchCommands.Add(Command("""
            SELECT r.request_id, r.requester_id, p.display_name, r.created_at
            FROM social.friend_requests r
            JOIN identity.players p ON p.player_id = r.requester_id
            WHERE r.status = 'pending' AND r.target_id = $1
            ORDER BY r.created_at
            """, playerId));
        batch.BatchCommands.Add(Command("""
            SELECT r.request_id, r.target_id, p.display_name, r.created_at
            FROM social.friend_requests r
            JOIN identity.players p ON p.player_id = r.target_id
            WHERE r.status = 'pending' AND r.requester_id = $1
            ORDER BY r.created_at
            """, playerId));
        batch.BatchCommands.Add(Command(
            "SELECT party_id FROM social.party_members WHERE player_id = $1", playerId));
        batch.BatchCommands.Add(Command("""
            SELECT leader_id FROM social.parties
            WHERE party_id = (SELECT party_id FROM social.party_members WHERE player_id = $1)
            """, playerId));
        batch.BatchCommands.Add(Command("""
            SELECT m.player_id, p.display_name
            FROM social.party_members m
            JOIN identity.players p ON p.player_id = m.player_id
            WHERE m.party_id = (SELECT party_id FROM social.party_members WHERE player_id = $1)
            ORDER BY m.joined_at, m.player_id
            """, playerId));
        batch.BatchCommands.Add(Command("""
            SELECT i.party_id, i.inviter_id, p.display_name
            FROM social.party_invitations i
            JOIN identity.players p ON p.player_id = i.inviter_id
            WHERE i.invitee_id = $1
            """, playerId));

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);

        var friends = new List<FriendRecord>();
        while (await reader.ReadAsync(cancellationToken))
            friends.Add(new FriendRecord(reader.GetGuid(0), reader.GetString(1)));

        var incoming = new List<FriendRequestView>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            incoming.Add(new FriendRequestView(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3)));

        var outgoing = new List<FriendRequestView>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            outgoing.Add(new FriendRequestView(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3)));

        await reader.NextResultAsync(cancellationToken);
        Guid? partyId = await reader.ReadAsync(cancellationToken) ? reader.GetGuid(0) : null;

        await reader.NextResultAsync(cancellationToken);
        Guid? leaderId = await reader.ReadAsync(cancellationToken) ? reader.GetGuid(0) : null;

        var members = new List<PartyMemberRecord>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            members.Add(new PartyMemberRecord(reader.GetGuid(0), reader.GetString(1)));

        await reader.NextResultAsync(cancellationToken);
        var invitation = await reader.ReadAsync(cancellationToken)
            ? new PartyInvitationRecord(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2))
            : null;

        // 成员行存在但 parties 行缺失时视为未组队，与 GetPartyAsync 的判定一致。
        var party = partyId is { } id && leaderId is { } leader
            ? new PartyRecord(id, leader, members)
            : null;
        return new SocialStateSnapshot(friends, incoming, outgoing, party, invitation);
    }

    /// <summary>判断双方是否已是好友。</summary>
    public async ValueTask<bool> AreFriendsAsync(Guid left, Guid right, CancellationToken cancellationToken = default)
    {
        if (left == Guid.Empty || right == Guid.Empty || left == right) return false;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM social.friendships WHERE player_id = $1 AND friend_id = $2)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = left });
        command.Parameters.Add(new NpgsqlParameter { Value = right });
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>创建待处理好友申请；同向 pending 已存在时由部分唯一索引拒绝并返回 false。</summary>
    public async ValueTask<bool> TryCreateFriendRequestAsync(Guid requestId, Guid requesterId, Guid targetId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO social.friend_requests(request_id, requester_id, target_id, status, created_at, updated_at)
            VALUES ($1, $2, $3, 'pending', $4, $4)
            ON CONFLICT DO NOTHING
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = requestId });
        command.Parameters.Add(new NpgsqlParameter { Value = requesterId });
        command.Parameters.Add(new NpgsqlParameter { Value = targetId });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // 并发下同一对玩家的重复申请由部分唯一索引兜底。
            return false;
        }
    }

    /// <summary>接受申请：置 accepted 并同事务写入双向好友关系。</summary>
    public async ValueTask<bool> TryAcceptFriendRequestAsync(Guid requesterId, Guid targetId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await TryClosePendingRequestAsync(connection, transaction, requesterId, targetId, "accepted", now, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await using (var friendship = new NpgsqlCommand("""
            INSERT INTO social.friendships(player_id, friend_id, created_at)
            VALUES ($1, $2, $3), ($2, $1, $3)
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            friendship.Parameters.Add(new NpgsqlParameter { Value = requesterId });
            friendship.Parameters.Add(new NpgsqlParameter { Value = targetId });
            friendship.Parameters.Add(new NpgsqlParameter { Value = now });
            await friendship.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>拒绝申请：置 rejected。</summary>
    public async ValueTask<bool> TryRejectFriendRequestAsync(Guid requesterId, Guid targetId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var closed = await TryClosePendingRequestAsync(connection, transaction, requesterId, targetId, "rejected", now, cancellationToken);
        if (!closed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>解除双向好友关系。</summary>
    public async ValueTask<bool> TryRemoveFriendAsync(Guid playerId, Guid friendId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM social.friendships
            WHERE (player_id = $1 AND friend_id = $2) OR (player_id = $2 AND friend_id = $1)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = friendId });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>读取玩家当前队伍 ID。</summary>
    public async ValueTask<Guid?> GetPartyIdAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT party_id FROM social.party_members WHERE player_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid partyId ? partyId : null;
    }

    /// <summary>读取队伍与成员（含道号，按加入时间排序）。</summary>
    public async ValueTask<PartyRecord?> GetPartyAsync(Guid partyId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        Guid leaderId;
        await using (var partyCommand = new NpgsqlCommand(
            "SELECT leader_id FROM social.parties WHERE party_id = $1", connection))
        {
            partyCommand.Parameters.Add(new NpgsqlParameter { Value = partyId });
            if (await partyCommand.ExecuteScalarAsync(cancellationToken) is not Guid found) return null;
            leaderId = found;
        }
        await using var membersCommand = new NpgsqlCommand("""
            SELECT m.player_id, p.display_name
            FROM social.party_members m
            JOIN identity.players p ON p.player_id = m.player_id
            WHERE m.party_id = $1
            ORDER BY m.joined_at, m.player_id
            """, connection);
        membersCommand.Parameters.Add(new NpgsqlParameter { Value = partyId });
        var members = new List<PartyMemberRecord>();
        await using var reader = await membersCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            members.Add(new PartyMemberRecord(reader.GetGuid(0), reader.GetString(1)));
        return new PartyRecord(partyId, leaderId, members);
    }

    /// <summary>创建队伍并写入队长成员行（同一事务）。</summary>
    public async ValueTask<Guid> CreatePartyAsync(Guid leaderId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var partyId = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var party = new NpgsqlCommand(
            "INSERT INTO social.parties(party_id, leader_id, created_at) VALUES ($1, $2, $3)", connection, transaction))
        {
            party.Parameters.Add(new NpgsqlParameter { Value = partyId });
            party.Parameters.Add(new NpgsqlParameter { Value = leaderId });
            party.Parameters.Add(new NpgsqlParameter { Value = now });
            await party.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var member = new NpgsqlCommand(
            "INSERT INTO social.party_members(player_id, party_id, joined_at) VALUES ($1, $2, $3)", connection, transaction))
        {
            member.Parameters.Add(new NpgsqlParameter { Value = leaderId });
            member.Parameters.Add(new NpgsqlParameter { Value = partyId });
            member.Parameters.Add(new NpgsqlParameter { Value = now });
            await member.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return partyId;
    }

    /// <summary>把玩家加入队伍；玩家已在其他队伍时由主键冲突返回 false。</summary>
    public async ValueTask<bool> TryAddPartyMemberAsync(Guid partyId, Guid playerId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO social.party_members(player_id, party_id, joined_at)
            VALUES ($1, $2, $3)
            ON CONFLICT (player_id) DO NOTHING
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = partyId });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>移除玩家队伍成员行。</summary>
    public async ValueTask<bool> TryRemovePartyMemberAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM social.party_members WHERE player_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>成员全部离开后删除空队伍记录。</summary>
    public async ValueTask DeletePartyIfEmptyAsync(Guid partyId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM social.parties
            WHERE party_id = $1 AND NOT EXISTS (SELECT 1 FROM social.party_members WHERE party_id = $1)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = partyId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>写入或覆盖玩家的待处理队伍邀请（每名玩家最多一条）。</summary>
    public async ValueTask UpsertPartyInvitationAsync(Guid partyId, Guid inviteeId, Guid inviterId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO social.party_invitations(invitee_id, party_id, inviter_id, created_at)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (invitee_id) DO UPDATE
                SET party_id = EXCLUDED.party_id, inviter_id = EXCLUDED.inviter_id, created_at = EXCLUDED.created_at
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = inviteeId });
        command.Parameters.Add(new NpgsqlParameter { Value = partyId });
        command.Parameters.Add(new NpgsqlParameter { Value = inviterId });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>读取玩家待处理队伍邀请（含邀请人道号）。</summary>
    public async ValueTask<PartyInvitationRecord?> GetPartyInvitationAsync(Guid inviteeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT i.party_id, i.inviter_id, p.display_name
            FROM social.party_invitations i
            JOIN identity.players p ON p.player_id = i.inviter_id
            WHERE i.invitee_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = inviteeId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PartyInvitationRecord(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2))
            : null;
    }

    /// <summary>清除玩家的待处理队伍邀请。</summary>
    public async ValueTask<bool> TryClearPartyInvitationAsync(Guid inviteeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM social.party_invitations WHERE invitee_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = inviteeId });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>把队伍队长位移交给指定成员（队长离开时调用）。</summary>
    public async ValueTask TryTransferPartyLeaderAsync(Guid partyId, Guid leaderId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE social.parties SET leader_id = $2 WHERE party_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = partyId });
        command.Parameters.Add(new NpgsqlParameter { Value = leaderId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 在事务内把一条待处理申请流转到目标状态：必须命中 (requester, target, pending)，
    /// 否则返回 false 由调用方回滚，杜绝重复接受/越权处理。
    /// </summary>
    private static async ValueTask<bool> TryClosePendingRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid requesterId,
        Guid targetId,
        string status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE social.friend_requests
            SET status = $3, updated_at = $4
            WHERE requester_id = $1 AND target_id = $2 AND status = 'pending'
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = requesterId });
        command.Parameters.Add(new NpgsqlParameter { Value = targetId });
        command.Parameters.Add(new NpgsqlParameter { Value = status });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
