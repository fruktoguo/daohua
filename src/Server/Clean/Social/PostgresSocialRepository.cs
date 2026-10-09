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
            SELECT leader_id, name, activity_kind, node_id, min_valuation, is_public, created_at, open_slots
            FROM social.parties
            WHERE party_id = (SELECT party_id FROM social.party_members WHERE player_id = $1)
            """, playerId));
        batch.BatchCommands.Add(Command("""
            SELECT m.player_id, p.display_name, m.ready
            FROM social.party_members m
            JOIN identity.players p ON p.player_id = m.player_id
            WHERE m.party_id = (SELECT party_id FROM social.party_members WHERE player_id = $1)
            ORDER BY m.joined_at, m.player_id
            """, playerId));
        batch.BatchCommands.Add(Command("""
            SELECT i.party_id, i.inviter_id, p.display_name, COALESCE(pt.name, '')
            FROM social.party_invitations i
            JOIN identity.players p ON p.player_id = i.inviter_id
            LEFT JOIN social.parties pt ON pt.party_id = i.party_id
            WHERE i.invitee_id = $1
            """, playerId));
        batch.BatchCommands.Add(Command("""
            SELECT s.activity_kind, s.node_id, s.min_valuation, s.created_at
            FROM social.party_seekers s
            WHERE s.player_id = $1
            """, playerId));
        batch.BatchCommands.Add(Command("""
            SELECT s.session_id, s.party_id, s.spec::text, s.started_at, s.ended_at, s.end_reason
            FROM social.party_sessions s
            WHERE s.party_id = (SELECT party_id FROM social.party_members WHERE player_id = $1)
              AND s.ended_at IS NULL
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

        PartyProfileRecord? profile = null;
        Guid? leaderId = null;
        DateTimeOffset partyCreatedAt = default;
        var openSlots = 0;
        await reader.NextResultAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            leaderId = reader.GetGuid(0);
            profile = new PartyProfileRecord(
                reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetDouble(4), reader.GetBoolean(5));
            partyCreatedAt = reader.GetFieldValue<DateTimeOffset>(6);
            openSlots = reader.GetInt16(7);
        }

        var members = new List<PartyMemberRecord>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            members.Add(new PartyMemberRecord(reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2)));

        await reader.NextResultAsync(cancellationToken);
        var invitation = await reader.ReadAsync(cancellationToken)
            ? new PartyInvitationRecord(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3))
            : null;

        PartySeekerRecord? seeker = null;
        await reader.NextResultAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            // 本人登记无需道号：快照已含本人身份，DisplayName 占位空串。
            seeker = new PartySeekerRecord(
                playerId, string.Empty, reader.GetString(0), reader.GetString(1),
                reader.GetDouble(2), reader.GetFieldValue<DateTimeOffset>(3));
        }

        PartySessionRecord? partySession = null;
        await reader.NextResultAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            partySession = ReadPartySession(reader);

        // 成员行存在但 parties 行缺失时视为未组队，与 GetPartyAsync 的判定一致。
        var party = partyId is { } id && leaderId is { } leader && profile is { } found
            ? new PartyRecord(id, leader, found, partyCreatedAt, members, openSlots)
            : null;
        return new SocialStateSnapshot(friends, incoming, outgoing, party, invitation, seeker, partySession);
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

    /// <summary>读取队伍档案与成员（含道号，按加入时间排序）。</summary>
    public async ValueTask<PartyRecord?> GetPartyAsync(Guid partyId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        Guid leaderId;
        PartyProfileRecord? profile = null;
        DateTimeOffset createdAt = default;
        var openSlots = 0;
        await using (var partyCommand = new NpgsqlCommand("""
            SELECT leader_id, name, activity_kind, node_id, min_valuation, is_public, created_at, open_slots
            FROM social.parties WHERE party_id = $1
            """, connection))
        {
            partyCommand.Parameters.Add(new NpgsqlParameter { Value = partyId });
            await using var partyReader = await partyCommand.ExecuteReaderAsync(cancellationToken);
            if (!await partyReader.ReadAsync(cancellationToken)) return null;
            leaderId = partyReader.GetGuid(0);
            profile = new PartyProfileRecord(
                partyReader.GetString(1), partyReader.GetString(2), partyReader.GetString(3),
                partyReader.GetDouble(4), partyReader.GetBoolean(5));
            createdAt = partyReader.GetFieldValue<DateTimeOffset>(6);
            openSlots = partyReader.GetInt16(7);
        }
        await using var membersCommand = new NpgsqlCommand("""
            SELECT m.player_id, p.display_name, m.ready
            FROM social.party_members m
            JOIN identity.players p ON p.player_id = m.player_id
            WHERE m.party_id = $1
            ORDER BY m.joined_at, m.player_id
            """, connection);
        membersCommand.Parameters.Add(new NpgsqlParameter { Value = partyId });
        var members = new List<PartyMemberRecord>();
        await using var reader = await membersCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            members.Add(new PartyMemberRecord(reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2)));
        return new PartyRecord(partyId, leaderId, profile, createdAt, members, openSlots);
    }

    /// <summary>创建队伍并写入队长成员行（同一事务）；档案为招募标签。</summary>
    public async ValueTask<Guid> CreatePartyAsync(Guid leaderId, PartyProfileRecord profile, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var partyId = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var party = new NpgsqlCommand("""
            INSERT INTO social.parties(party_id, leader_id, name, activity_kind, node_id, min_valuation, is_public, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $8)
            """, connection, transaction))
        {
            party.Parameters.Add(new NpgsqlParameter { Value = partyId });
            party.Parameters.Add(new NpgsqlParameter { Value = leaderId });
            party.Parameters.Add(new NpgsqlParameter { Value = profile.Name });
            party.Parameters.Add(new NpgsqlParameter { Value = profile.ActivityKind });
            party.Parameters.Add(new NpgsqlParameter { Value = profile.NodeId });
            party.Parameters.Add(new NpgsqlParameter { Value = profile.MinValuation });
            party.Parameters.Add(new NpgsqlParameter { Value = profile.IsPublic });
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

    /// <summary>整体覆盖队伍档案；队伍不存在返回 false。</summary>
    public async ValueTask<bool> TryUpdatePartyProfileAsync(Guid partyId, PartyProfileRecord profile, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE social.parties
            SET name = $2, activity_kind = $3, node_id = $4, min_valuation = $5, is_public = $6, updated_at = $7
            WHERE party_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = partyId });
        command.Parameters.Add(new NpgsqlParameter { Value = profile.Name });
        command.Parameters.Add(new NpgsqlParameter { Value = profile.ActivityKind });
        command.Parameters.Add(new NpgsqlParameter { Value = profile.NodeId });
        command.Parameters.Add(new NpgsqlParameter { Value = profile.MinValuation });
        command.Parameters.Add(new NpgsqlParameter { Value = profile.IsPublic });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// 原子加队：parties 行 FOR UPDATE 串行化并发加入，事务内以 open_slots 复核满员与互斥；
    /// 成功时同事务清除该玩家的匹配登记与待处理邀请。
    /// </summary>
    public async ValueTask<PartyJoinResult> TryJoinPartyAsync(Guid partyId, Guid playerId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        int openSlots;
        await using (var gate = new NpgsqlCommand(
            "SELECT open_slots FROM social.parties WHERE party_id = $1 FOR UPDATE", connection, transaction))
        {
            gate.Parameters.Add(new NpgsqlParameter { Value = partyId });
            if (await gate.ExecuteScalarAsync(cancellationToken) is not short slots)
            {
                await transaction.RollbackAsync(cancellationToken);
                return PartyJoinResult.NotFound;
            }
            openSlots = slots;
        }
        int memberCount;
        await using (var count = new NpgsqlCommand(
            "SELECT count(*) FROM social.party_members WHERE party_id = $1", connection, transaction))
        {
            count.Parameters.Add(new NpgsqlParameter { Value = partyId });
            memberCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        }
        if (memberCount >= openSlots)
        {
            await transaction.RollbackAsync(cancellationToken);
            return PartyJoinResult.Full;
        }
        int inserted;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO social.party_members(player_id, party_id, joined_at)
            VALUES ($1, $2, $3)
            ON CONFLICT (player_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = playerId });
            insert.Parameters.Add(new NpgsqlParameter { Value = partyId });
            insert.Parameters.Add(new NpgsqlParameter { Value = now });
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        if (inserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return PartyJoinResult.AlreadyInParty;
        }
        // 入队副作用清理拆成两条独立命令：Npgsql 自动 prepare 不接受多语句命令。
        await using (var clearSeeker = new NpgsqlCommand(
            "DELETE FROM social.party_seekers WHERE player_id = $1", connection, transaction))
        {
            clearSeeker.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await clearSeeker.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var clearInvitation = new NpgsqlCommand(
            "DELETE FROM social.party_invitations WHERE invitee_id = $1", connection, transaction))
        {
            clearInvitation.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await clearInvitation.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return PartyJoinResult.Joined;
    }

    /// <summary>解散队伍：删除 parties 行（成员/邀请级联、聊天链接置空），返回解散前成员列表。</summary>
    public async ValueTask<IReadOnlyList<PartyMemberRecord>?> DisbandPartyAsync(Guid partyId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var members = new List<PartyMemberRecord>();
        await using (var collect = new NpgsqlCommand("""
            SELECT m.player_id, p.display_name, m.ready
            FROM social.party_members m JOIN identity.players p ON p.player_id = m.player_id
            WHERE m.party_id = $1
            """, connection, transaction))
        {
            collect.Parameters.Add(new NpgsqlParameter { Value = partyId });
            await using var reader = await collect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                members.Add(new PartyMemberRecord(reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2)));
        }
        int deleted;
        await using (var remove = new NpgsqlCommand(
            "DELETE FROM social.parties WHERE party_id = $1", connection, transaction))
        {
            remove.Parameters.Add(new NpgsqlParameter { Value = partyId });
            deleted = await remove.ExecuteNonQueryAsync(cancellationToken);
        }
        if (deleted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        await transaction.CommitAsync(cancellationToken);
        return members;
    }

    /// <summary>移除指定队伍中的成员行；该成员不在此队伍返回 false。</summary>
    public async ValueTask<bool> TryKickPartyMemberAsync(Guid partyId, Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM social.party_members
            WHERE player_id = $1 AND party_id = $2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = partyId });
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

    /// <summary>列出公开队伍（含成员与道号）；目标类型/地图为空串表示不限；按创建时间倒序。</summary>
    public async ValueTask<IReadOnlyList<PartyRecord>> ListPublicPartiesAsync(string activityKind, string nodeId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var parties = new List<(PartyRecord Seed, List<PartyMemberRecord> Members)>();
        await using (var command = new NpgsqlCommand("""
            SELECT party_id, leader_id, name, activity_kind, node_id, min_valuation, is_public, created_at, open_slots
            FROM social.parties
            WHERE is_public
              AND ($1 = '' OR $1 = 'any' OR activity_kind = 'any' OR activity_kind = $1)
              AND ($2 = '' OR node_id = $2)
            ORDER BY created_at DESC
            LIMIT $3
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = activityKind });
            command.Parameters.Add(new NpgsqlParameter { Value = nodeId });
            command.Parameters.Add(new NpgsqlParameter { Value = limit });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var profile = new PartyProfileRecord(
                    reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetDouble(5), reader.GetBoolean(6));
                parties.Add((new PartyRecord(
                    reader.GetGuid(0), reader.GetGuid(1), profile,
                    reader.GetFieldValue<DateTimeOffset>(7), [], reader.GetInt16(8)), []));
            }
        }
        if (parties.Count == 0) return [];
        var ids = parties.Select(p => p.Seed.PartyId).ToArray();
        var membersByParty = parties.ToDictionary(p => p.Seed.PartyId, p => p.Members);
        await using (var command = new NpgsqlCommand("""
            SELECT m.party_id, m.player_id, p.display_name, m.ready
            FROM social.party_members m
            JOIN identity.players p ON p.player_id = m.player_id
            WHERE m.party_id = ANY($1)
            ORDER BY m.party_id, m.joined_at, m.player_id
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = ids });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                membersByParty[reader.GetGuid(0)].Add(new PartyMemberRecord(reader.GetGuid(1), reader.GetString(2), reader.GetBoolean(3)));
        }
        return parties
            .Select(p => p.Seed with { Members = p.Members })
            .ToArray();
    }

    /// <summary>写入或覆盖玩家的匹配登记。</summary>
    public async ValueTask UpsertPartySeekerAsync(Guid playerId, string activityKind, string nodeId, double minValuation, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO social.party_seekers(player_id, activity_kind, node_id, min_valuation, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $5)
            ON CONFLICT (player_id) DO UPDATE
            SET activity_kind = EXCLUDED.activity_kind,
                node_id = EXCLUDED.node_id,
                min_valuation = EXCLUDED.min_valuation,
                updated_at = EXCLUDED.updated_at
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = activityKind });
        command.Parameters.Add(new NpgsqlParameter { Value = nodeId });
        command.Parameters.Add(new NpgsqlParameter { Value = minValuation });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>撤下玩家的匹配登记。</summary>
    public async ValueTask<bool> TryClearPartySeekerAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM social.party_seekers WHERE player_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>读取玩家的匹配登记；未登记返回 null。</summary>
    public async ValueTask<PartySeekerRecord?> GetPartySeekerAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT s.activity_kind, s.node_id, s.min_valuation, s.created_at
            FROM social.party_seekers s WHERE s.player_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new PartySeekerRecord(
            playerId, string.Empty, reader.GetString(0), reader.GetString(1),
            reader.GetDouble(2), reader.GetFieldValue<DateTimeOffset>(3));
    }

    /// <summary>列出匹配中散人（排除已在队伍中的滞留登记）；目标类型/地图为空串表示不限。</summary>
    public async ValueTask<IReadOnlyList<PartySeekerRecord>> ListPartySeekersAsync(string activityKind, string nodeId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var seekers = new List<PartySeekerRecord>();
        await using var command = new NpgsqlCommand("""
            SELECT s.player_id, p.display_name, s.activity_kind, s.node_id, s.min_valuation, s.created_at
            FROM social.party_seekers s
            JOIN identity.players p ON p.player_id = s.player_id
            WHERE ($1 = '' OR $1 = 'any' OR s.activity_kind = 'any' OR s.activity_kind = $1)
              AND ($2 = '' OR s.node_id = $2)
              AND NOT EXISTS (SELECT 1 FROM social.party_members m WHERE m.player_id = s.player_id)
            ORDER BY s.created_at DESC
            LIMIT $3
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = activityKind });
        command.Parameters.Add(new NpgsqlParameter { Value = nodeId });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            seekers.Add(new PartySeekerRecord(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetDouble(4), reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return seekers;
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

    /// <summary>读取玩家待处理队伍邀请（含邀请人道号与队伍名）。</summary>
    public async ValueTask<PartyInvitationRecord?> GetPartyInvitationAsync(Guid inviteeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT i.party_id, i.inviter_id, p.display_name, COALESCE(pt.name, '')
            FROM social.party_invitations i
            JOIN identity.players p ON p.player_id = i.inviter_id
            LEFT JOIN social.parties pt ON pt.party_id = i.party_id
            WHERE i.invitee_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = inviteeId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PartyInvitationRecord(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3))
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

    /// <summary>切换本人队伍就绪标记（party.ready/unready）。</summary>
    public async ValueTask<bool> TrySetPartyMemberReadyAsync(Guid playerId, bool ready, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE social.party_members SET ready = $2 WHERE player_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = ready });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// 调整开启槽位：parties 行 FOR UPDATE 串行化与加入/退出并发，
    /// 锁内复核 2~5 区间与「不得低于当前队员数」不变量。
    /// </summary>
    public async ValueTask<bool> TryUpdatePartyOpenSlotsAsync(Guid partyId, int openSlots, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (openSlots < 2 || openSlots > 5) return false;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var gate = new NpgsqlCommand(
            "SELECT 1 FROM social.parties WHERE party_id = $1 FOR UPDATE", connection, transaction))
        {
            gate.Parameters.Add(new NpgsqlParameter { Value = partyId });
            if (await gate.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }
        int memberCount;
        await using (var count = new NpgsqlCommand(
            "SELECT count(*) FROM social.party_members WHERE party_id = $1", connection, transaction))
        {
            count.Parameters.Add(new NpgsqlParameter { Value = partyId });
            memberCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        }
        // 收窄不得低于当前人数：这是领域不变量而非裁剪——拒绝而非逐出。
        if (openSlots < memberCount)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await using (var update = new NpgsqlCommand(
            "UPDATE social.parties SET open_slots = $2, updated_at = $3 WHERE party_id = $1", connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = partyId });
            update.Parameters.Add(new NpgsqlParameter { Value = (short)openSlots });
            update.Parameters.Add(new NpgsqlParameter { Value = now });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>按会话 ID 读取讨伐会话行（含已散场行）。</summary>
    public async ValueTask<PartySessionRecord?> GetPartySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT session_id, party_id, spec::text, started_at, ended_at, end_reason
            FROM social.party_sessions WHERE session_id = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = sessionId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPartySession(reader) : null;
    }

    /// <summary>读取队伍当前活跃讨伐会话。</summary>
    public async ValueTask<PartySessionRecord?> GetActivePartySessionAsync(Guid partyId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT session_id, party_id, spec::text, started_at, ended_at, end_reason
            FROM social.party_sessions WHERE party_id = $1 AND ended_at IS NULL
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = partyId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPartySession(reader) : null;
    }

    /// <summary>批量读取玩家投影节点；无投影行或正在迁地（current_node_id 为 NULL）的玩家缺席。</summary>
    public async ValueTask<IReadOnlyDictionary<Guid, string>> GetPlayerNodeMapAsync(IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<Guid, string>();
        if (playerIds.Count == 0) return map;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT player_id, current_node_id
            FROM game.player_projection
            WHERE player_id = ANY($1) AND current_node_id IS NOT NULL
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = playerIds.ToArray() });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            map[reader.GetGuid(0)] = reader.GetString(1);
        return map;
    }

    /// <summary>
    /// 48h 离线逐出：last_active_at 口径=「最后一次真实活跃提交」（冷挂机补算不推进该列），
    /// presence='online' 兜底防删——投影在线标记与实际连接脱节的窗口期不误逐。
    /// </summary>
    public async ValueTask<IReadOnlyList<PartyMemberEviction>> DeleteInactivePartyMembersAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            DELETE FROM social.party_members m
            USING game.player_projection p
            WHERE m.player_id = p.player_id
              AND p.last_active_at < $1
              AND p.presence <> 'online'
            RETURNING m.player_id, m.party_id
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = cutoff });
        var evicted = new List<PartyMemberEviction>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            evicted.Add(new PartyMemberEviction(reader.GetGuid(0), reader.GetGuid(1)));
        return evicted;
    }

    /// <summary>
    /// 会话行 → 领域可读记录：spec jsonb 只解析浅层字段（encounterId/nodeId/members[].playerId），
    /// 成员冻结面板整体不还原——规格真源在行动队列绑定内，本表是裁决/对账镜像。
    /// 载荷缺字段属于数据损坏，抛 InvalidDataException 快速失败。
    /// </summary>
    private static PartySessionRecord ReadPartySession(NpgsqlDataReader reader)
    {
        var spec = reader.GetString(2);
        using var document = JsonDocument.Parse(spec);
        var root = document.RootElement;
        var members = new List<Guid>();
        foreach (var member in root.GetProperty("members").EnumerateArray())
        {
            if (!member.TryGetProperty("playerId", out var id) || !Guid.TryParse(id.GetString(), out var memberId))
                throw new InvalidDataException("组队会话规格 members[].playerId 缺失或非法。");
            members.Add(memberId);
        }
        var encounterId = root.GetProperty("encounterId").GetString();
        var nodeId = root.GetProperty("nodeId").GetString();
        if (string.IsNullOrEmpty(encounterId) || string.IsNullOrEmpty(nodeId))
            throw new InvalidDataException("组队会话规格缺 encounterId/nodeId。");
        return new PartySessionRecord(
            reader.GetString(0),
            reader.GetGuid(1),
            encounterId,
            nodeId,
            reader.GetFieldValue<DateTimeOffset>(3),
            members,
            spec,
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
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
