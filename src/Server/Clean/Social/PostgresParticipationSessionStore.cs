namespace Server.Clean.Social;

/// <summary>保存唯一会话检查点，并与各成员实际结算批次原子交付。</summary>
public sealed class PostgresParticipationSessionStore(NpgsqlDataSource dataSource) : IParticipationSessionStore
{
    private const string SelectColumns = "session_id, party_id, spec::text, runtime_state::text, state_version, last_simulated_at, ended_at, end_reason";

    /// <inheritdoc />
    public async ValueTask<ActionParticipationSessionRecord?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand($"SELECT {SelectColumns} FROM social.party_sessions WHERE session_id = $1 AND runtime_state IS NOT NULL");
        command.Parameters.AddWithValue(sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSession(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ActionParticipationSessionRecord>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand($"SELECT {SelectColumns} FROM social.party_sessions WHERE ended_at IS NULL AND runtime_state IS NOT NULL ORDER BY session_id");
        return await ReadSessionsAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ActionParticipationSessionRecord>> ListActiveForMemberAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand($"""
            SELECT {SelectColumns} FROM social.party_sessions
            WHERE ended_at IS NULL AND runtime_state IS NOT NULL
              AND spec @> $1::jsonb
            ORDER BY session_id
            """);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new { members = new[] { new { playerId = playerId.ToString("D") } } }));
        return await ReadSessionsAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetInvalidationReasonAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        // 读模型可滞后；位置必须叠加 durable 队列的较新 world 分段，与组织从同一快照读取。
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        string targetNode;
        string? configuredNode;
        Guid leaderId;
        await using (var command = new NpgsqlCommand("""
            SELECT CASE
                WHEN p.party_id IS NULL THEN 'party-disbanded'
                WHEN (SELECT count(*) FROM social.party_members WHERE party_id = s.party_id)
                        <> jsonb_array_length(s.spec -> 'members')
                    OR EXISTS (
                        SELECT 1 FROM jsonb_array_elements(s.spec -> 'members') member
                        WHERE NOT EXISTS (
                            SELECT 1 FROM social.party_members m
                            WHERE m.party_id = s.party_id AND m.player_id = (member ->> 'playerId')::uuid
                        )
                    ) THEN 'member-roster-changed'
                WHEN p.open_slots <> jsonb_array_length(s.spec -> 'members') THEN 'party-slots-changed'
                WHEN EXISTS (
                    SELECT 1 FROM social.party_members m WHERE m.party_id = s.party_id AND NOT m.ready
                ) THEN 'member-unready'
                ELSE NULL
            END, s.spec ->> 'nodeId', NULLIF(p.node_id, ''), p.leader_id
            FROM social.party_sessions s
            LEFT JOIN social.parties p ON p.party_id = s.party_id
            WHERE s.session_id = $1
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(sessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException($"会话成立条件缺少持久化规格 session={sessionId}。");
            if (!reader.IsDBNull(0)) return reader.GetString(0);
            targetNode = reader.GetString(1);
            configuredNode = reader.IsDBNull(2) ? null : reader.GetString(2);
            leaderId = reader.GetGuid(3);
        }
        if (configuredNode is not null && configuredNode != targetNode) return "target-node-changed";
        await using var worlds = new NpgsqlCommand("""
            SELECT m.player_id, world.state_version, world.schema_version, world.codec,
                   world.payload, world.raw_bytes, world.checksum, world.updated_at
            FROM social.party_members m
            JOIN social.party_sessions s ON s.party_id=m.party_id AND s.session_id=$1
            LEFT JOIN LATERAL (
                SELECT state_version, schema_version, codec, payload, raw_bytes, checksum, updated_at
                FROM (
                    SELECT section_version AS state_version, schema_version, codec, payload, raw_bytes, checksum, updated_at
                    FROM game.player_state_sections WHERE player_id=m.player_id AND section_kind='world'
                    UNION ALL
                    SELECT state_version, schema_version, codec, payload, raw_bytes, checksum, updated_at
                    FROM platform.section_flush_queue WHERE player_id=m.player_id AND section_kind='world'
                ) candidates ORDER BY state_version DESC LIMIT 1
            ) world ON true
            """, connection, transaction);
        worlds.Parameters.AddWithValue(sessionId);
        await using var worldReader = await worlds.ExecuteReaderAsync(cancellationToken);
        while (await worldReader.ReadAsync(cancellationToken))
        {
            if (worldReader.IsDBNull(1)) return "member-node-changed";
            var playerId = worldReader.GetGuid(0);
            var world = SectionCodec.Decode<PlayerWorldStateDocument>(new StoredSection(playerId, PlayerSectionKinds.World,
                worldReader.GetInt64(1), worldReader.GetInt32(2), worldReader.GetString(3), worldReader.GetFieldValue<byte[]>(4),
                worldReader.GetInt32(5), worldReader.GetString(6), worldReader.GetFieldValue<DateTimeOffset>(7)));
            var node = world.EdgeId is null ? world.NodeId : null;
            if (configuredNode is null && playerId == leaderId && node != targetNode) return "target-node-changed";
            if (node != targetNode) return "member-node-changed";
        }
        return null;
    }

    /// <inheritdoc />
    public async ValueTask<DateTimeOffset?> GetOfflineExpiryAsync(string sessionId, TimeSpan offlineLimit,
        IReadOnlyCollection<Guid> onlinePlayerIds, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT min(p.offline_since + $2)
            FROM social.party_sessions s
            CROSS JOIN LATERAL jsonb_array_elements(s.spec -> 'members') member
            JOIN game.player_projection p ON p.player_id = (member ->> 'playerId')::uuid
            WHERE s.session_id = $1 AND NOT (p.player_id = ANY($3))
              AND p.offline_since IS NOT NULL
            """);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(NpgsqlDbType.Interval, offlineLimit);
        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Uuid, onlinePlayerIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
    }

    /// <inheritdoc />
    public async ValueTask<ActionParticipationSessionRecord> CreateOrInitializeAsync(ActionParticipationSessionRecord session, CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await FenceSpecMembersAsync(connection, transaction, session.SpecJson, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO social.party_sessions
                (session_id, party_id, spec, started_at, runtime_state, state_version, last_simulated_at, ended_at, end_reason)
            VALUES ($1, $2, $3::jsonb, $4, $5::jsonb, $6, $4, $7, $8)
            ON CONFLICT (session_id) DO UPDATE SET
                runtime_state = EXCLUDED.runtime_state,
                state_version = EXCLUDED.state_version,
                last_simulated_at = EXCLUDED.last_simulated_at
            WHERE social.party_sessions.runtime_state IS NULL
              AND social.party_sessions.ended_at IS NULL
              AND social.party_sessions.party_id = EXCLUDED.party_id
              AND social.party_sessions.spec = EXCLUDED.spec
            RETURNING {SelectColumns}
            """, connection, transaction);
        command.Parameters.AddWithValue(session.SessionId);
        command.Parameters.AddWithValue(session.PartyId);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, session.SpecJson);
        command.Parameters.AddWithValue(session.LastSimulatedAt);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, session.StateJson);
        command.Parameters.AddWithValue(session.Version);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)session.EndedAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = (object?)session.EndReason ?? DBNull.Value });
        ActionParticipationSessionRecord? stored;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            stored = await reader.ReadAsync(cancellationToken) ? ReadSession(reader) : null;
        if (stored is null)
        {
            await using var existing = new NpgsqlCommand($"SELECT {SelectColumns} FROM social.party_sessions WHERE session_id = $1 AND runtime_state IS NOT NULL", connection, transaction);
            existing.Parameters.AddWithValue(session.SessionId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            stored = await reader.ReadAsync(cancellationToken) ? ReadSession(reader) : null;
        }
        if (stored is null)
            throw new InvalidOperationException($"会话开场初始化被拒绝 session={session.SessionId}。");
        await CommitAsync(transaction, session.SessionId, stored.Version, cancellationToken);
        return stored;
    }

    /// <inheritdoc />
    public async ValueTask<bool> CommitAdvanceAsync(string sessionId, long expectedVersion, string stateJson,
        DateTimeOffset lastSimulatedAt, IReadOnlyList<ParticipationInboxItem> inbox,
        DateTimeOffset? endedAt = null, string? endReason = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 64 || expectedVersion < 0 || string.IsNullOrWhiteSpace(stateJson))
            throw new ArgumentException("会话检查点标识、版本或载荷无效。");
        if ((endedAt is null) != (endReason is null) || endReason?.Length > 64)
            throw new ArgumentException("会话散场时刻与原因必须同时给出。");
        var keys = new HashSet<(Guid PlayerId, long Sequence)>();
        foreach (var item in inbox)
            if (item.SessionId != sessionId || item.PlayerId == Guid.Empty || item.Sequence < 1
                || string.IsNullOrWhiteSpace(item.PayloadJson) || !keys.Add((item.PlayerId, item.Sequence)))
                throw new ArgumentException("会话结算收件箱标识、序号或载荷无效。", nameof(inbox));

        var nextVersion = checked(expectedVersion + 1);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await FenceMembersAsync(connection, transaction, sessionId, cancellationToken);
        await using (var checkpoint = new NpgsqlCommand("""
            UPDATE social.party_sessions SET runtime_state = $3::jsonb,
                state_version = $4, last_simulated_at = $5, ended_at = $6, end_reason = $7
            WHERE session_id = $1 AND state_version = $2 AND ended_at IS NULL
              AND runtime_state IS NOT NULL AND last_simulated_at <= $5
            """, connection, transaction))
        {
            checkpoint.Parameters.AddWithValue(sessionId);
            checkpoint.Parameters.AddWithValue(expectedVersion);
            checkpoint.Parameters.AddWithValue(NpgsqlDbType.Jsonb, stateJson);
            checkpoint.Parameters.AddWithValue(nextVersion);
            checkpoint.Parameters.AddWithValue(lastSimulatedAt);
            checkpoint.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)endedAt ?? DBNull.Value });
            checkpoint.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = (object?)endReason ?? DBNull.Value });
            if (await checkpoint.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }
        }
        // 就绪位跨会话常驻（总纲 §21.13）：散场不改写名册 ready，仅成员本人
        // party.unready 清除；条件再次齐备时由下一次仲裁直接重开新会话。
        // 已确认份额的审计事实保留在经济流水；下一检查点移除其载荷，避免在线帧结算无界占盘。
        // 未消费份额仍是离线成员唯一收益交付证据，任何推进都不得删除。
        await using (var prune = new NpgsqlCommand("DELETE FROM social.party_session_inbox WHERE session_id = $1 AND consumed_at IS NOT NULL", connection, transaction))
        {
            prune.Parameters.AddWithValue(sessionId);
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var item in inbox)
        {
            await using var delivery = new NpgsqlCommand("""
                INSERT INTO social.party_session_inbox(session_id, player_id, sequence, payload, created_at)
                VALUES ($1, $2, $3, $4::jsonb, $5)
                """, connection, transaction);
            delivery.Parameters.AddWithValue(sessionId);
            delivery.Parameters.AddWithValue(item.PlayerId);
            delivery.Parameters.AddWithValue(item.Sequence);
            delivery.Parameters.AddWithValue(NpgsqlDbType.Jsonb, item.PayloadJson);
            delivery.Parameters.AddWithValue(item.CreatedAt);
            await delivery.ExecuteNonQueryAsync(cancellationToken);
        }
        await CommitAsync(transaction, sessionId, nextVersion, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ParticipationInboxItem>> LoadPendingAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT session_id, player_id, sequence, payload::text, created_at
            FROM social.party_session_inbox WHERE player_id = $1 AND consumed_at IS NULL
            ORDER BY created_at, session_id, sequence
            """);
        command.Parameters.AddWithValue(playerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ParticipationInboxItem>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new ParticipationInboxItem(reader.GetString(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        return result;
    }

    // 与 Compute 结果的身份行锁共用 MVCC fence：仅检查会话不存在不足以隔离旧 Serializable 快照。
    // 按稳定成员序锁行，避免不同会话名册交叠时反向获取玩家锁。
    private static async ValueTask FenceSpecMembersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string specJson, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT p.player_id
            FROM jsonb_array_elements($1::jsonb -> 'members') member
            JOIN identity.players p ON p.player_id = (member ->> 'playerId')::uuid
            ORDER BY p.player_id
            """, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, specJson);
        var members = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) members.Add(reader.GetGuid(0));
        foreach (var playerId in members)
            await PostgresPlayerOwnershipRepository.LockPlayerIdentityAsync(connection, transaction, playerId, cancellationToken);
    }

    private static async ValueTask FenceMembersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sessionId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT p.player_id
            FROM social.party_sessions s
            CROSS JOIN LATERAL jsonb_array_elements(s.spec -> 'members') member
            JOIN identity.players p ON p.player_id = (member ->> 'playerId')::uuid
            WHERE s.session_id = $1
            ORDER BY p.player_id
            """, connection, transaction);
        command.Parameters.AddWithValue(sessionId);
        var members = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) members.Add(reader.GetGuid(0));
        foreach (var playerId in members)
            await PostgresPlayerOwnershipRepository.LockPlayerIdentityAsync(connection, transaction, playerId, cancellationToken);
    }
    private static async ValueTask CommitAsync(NpgsqlTransaction transaction, string sessionId, long version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { await transaction.CommitAsync(cancellationToken); }
        catch (PostgresException) { throw; }
        catch (Exception exception) { throw new ParticipationSessionCommitOutcomeUnknownException(sessionId, version, exception); }
    }

    private static ActionParticipationSessionRecord ReadSession(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
        reader.GetFieldValue<DateTimeOffset>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    private static async ValueTask<IReadOnlyList<ActionParticipationSessionRecord>> ReadSessionsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sessions = new List<ActionParticipationSessionRecord>();
        while (await reader.ReadAsync(cancellationToken)) sessions.Add(ReadSession(reader));
        return sessions;
    }

    private static void ValidateSession(ActionParticipationSessionRecord session)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId) || session.SessionId.Length > 64 || session.PartyId == Guid.Empty
            || session.Version < 0 || string.IsNullOrWhiteSpace(session.SpecJson) || string.IsNullOrWhiteSpace(session.StateJson))
            throw new ArgumentException("会话开场标识、版本或载荷无效。", nameof(session));
        if ((session.EndedAt is null) != (session.EndReason is null) || session.EndReason?.Length > 64)
            throw new ArgumentException("会话散场时刻与原因必须同时给出。", nameof(session));
    }
}
