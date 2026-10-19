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
    public async ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadConsumableBalancesAsync(
        IReadOnlyList<ParticipationAssetKey> keys, CancellationToken cancellationToken = default)
    {
        var ordered = keys.Distinct().OrderBy(key => key.PlayerId)
            .ThenBy(key => key.AssetId, StringComparer.Ordinal).ThenBy(key => key.Variant, StringComparer.Ordinal).ToArray();
        foreach (var key in ordered) ValidateAssetKey(key.PlayerId, key.AssetId, key.Variant);
        if (ordered.Length == 0) return [];
        await using var command = dataSource.CreateCommand("""
            SELECT keys.player_id, keys.asset_id, keys.variant, COALESCE(b.quantity, 0), COALESCE(b.version, 0)
            FROM unnest($1::uuid[], $2::text[], $3::text[]) AS keys(player_id, asset_id, variant)
            LEFT JOIN economy.player_asset_balances b USING (player_id, asset_id, variant)
            ORDER BY keys.player_id, keys.asset_id COLLATE "C", keys.variant COLLATE "C"
            """);
        command.Parameters.AddWithValue(ordered.Select(key => key.PlayerId).ToArray());
        command.Parameters.AddWithValue(ordered.Select(key => key.AssetId).ToArray());
        command.Parameters.AddWithValue(ordered.Select(key => key.Variant).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var balances = new List<AssetBalanceRecord>(ordered.Length);
        while (await reader.ReadAsync(cancellationToken))
            balances.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetInt64(4)));
        return balances;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AssetBalanceRecord>> LoadConsumableBalancesForItemsAsync(
        IReadOnlyList<ParticipationAssetKey> keys, CancellationToken cancellationToken = default)
    {
        var ordered = keys.Select(key => (key.PlayerId, key.AssetId)).Distinct().OrderBy(key => key.PlayerId)
            .ThenBy(key => key.AssetId, StringComparer.Ordinal).ToArray();
        foreach (var key in ordered) ValidateAssetKey(key.PlayerId, key.AssetId, "");
        if (ordered.Length == 0) return [];
        // 同一语句快照读取所有相关变体，并为无默认行的物品补足缺失证明；不扫描玩家其余钱包。
        await using var command = dataSource.CreateCommand("""
            WITH keys AS (
                SELECT * FROM unnest($1::uuid[], $2::text[]) AS input(player_id, asset_id)
            ), balances AS (
                SELECT b.player_id, b.asset_id, b.variant, b.quantity, b.version
                FROM keys JOIN economy.player_asset_balances b USING (player_id, asset_id)
            ), complete AS (
                SELECT player_id, asset_id, variant, quantity, version FROM balances
                UNION ALL
                SELECT keys.player_id, keys.asset_id, '', 0, 0 FROM keys
                WHERE NOT EXISTS (
                    SELECT 1 FROM balances b
                    WHERE b.player_id = keys.player_id AND b.asset_id = keys.asset_id AND b.variant = ''
                )
            )
            SELECT player_id, asset_id, variant, quantity, version FROM complete
            ORDER BY player_id, asset_id COLLATE "C", variant COLLATE "C"
            """);
        command.Parameters.AddWithValue(ordered.Select(key => key.PlayerId).ToArray());
        command.Parameters.AddWithValue(ordered.Select(key => key.AssetId).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var balances = new List<AssetBalanceRecord>(ordered.Length);
        while (await reader.ReadAsync(cancellationToken))
            balances.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetInt64(4)));
        return balances;
    }

    /// <inheritdoc />
    public async ValueTask<ActionParticipationSessionRecord> CreateOrInitializeAsync(ActionParticipationSessionRecord session,
        ParticipationSessionEconomyCommit? economy = null, IReadOnlyList<ParticipationInboxItem>? inbox = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        inbox ??= [];
        ValidateInbox(session.SessionId, inbox);
        ValidateReadSet(economy);
        using var spec = JsonDocument.Parse(session.SpecJson);
        var members = spec.RootElement.GetProperty("members").EnumerateArray()
            .Select(member => member.GetProperty("playerId").GetGuid());
        var stripes = CollectGateStripes(members, economy, inbox);
        // 所有资产写入口都先取有序条带，再取身份行锁；倒序会与成员结算、市场写入形成死锁。
        await PlayerCommitGates.AcquireAllAsync(stripes, cancellationToken);
        try
        {
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
                if (stored is null)
                    throw new InvalidOperationException($"会话开场初始化被拒绝 session={session.SessionId}。");
                // 首次 COMMIT 结局未知后重放只恢复权威检查点，不重验旧钱包或重复扣款、投递。
            }
            else
            {
                if (!await CommitEconomyAsync(connection, transaction, economy, cancellationToken))
                    throw new ParticipationSessionEconomyConflictException(session.SessionId);
                await DeliverInboxAsync(connection, transaction, inbox, cancellationToken);
            }
            await CommitAsync(transaction, session.SessionId, stored.Version, cancellationToken);
            return stored;
        }
        finally { PlayerCommitGates.ReleaseAll(stripes); }
    }

    /// <inheritdoc />
    public async ValueTask<bool> CommitAdvanceAsync(string sessionId, long expectedVersion, string stateJson,
        DateTimeOffset lastSimulatedAt, IReadOnlyList<ParticipationInboxItem> inbox,
        DateTimeOffset? endedAt = null, string? endReason = null,
        ParticipationSessionEconomyCommit? economy = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 64 || expectedVersion < 0 || string.IsNullOrWhiteSpace(stateJson))
            throw new ArgumentException("会话检查点标识、版本或载荷无效。");
        if ((endedAt is null) != (endReason is null) || endReason?.Length > 64)
            throw new ArgumentException("会话散场时刻与原因必须同时给出。");
        ValidateInbox(sessionId, inbox);
        ValidateReadSet(economy);
        var nextVersion = checked(expectedVersion + 1);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var members = await LoadMemberIdsAsync(connection, sessionId, cancellationToken);
        var stripes = CollectGateStripes(members, economy, inbox);
        await PlayerCommitGates.AcquireAllAsync(stripes, cancellationToken);
        try
        {
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
            if (!await CommitEconomyAsync(connection, transaction, economy, cancellationToken))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }
            // 就绪位跨会话常驻（总纲 §21.13）；未消费份额仍是离线成员唯一收益交付证据，不能修剪。
            await using (var prune = new NpgsqlCommand("DELETE FROM social.party_session_inbox WHERE session_id = $1 AND consumed_at IS NOT NULL", connection, transaction))
            {
                prune.Parameters.AddWithValue(sessionId);
                await prune.ExecuteNonQueryAsync(cancellationToken);
            }
            await DeliverInboxAsync(connection, transaction, inbox, cancellationToken);
            await CommitAsync(transaction, sessionId, nextVersion, cancellationToken);
            return true;
        }
        finally { PlayerCommitGates.ReleaseAll(stripes); }
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

    private static int[] CollectGateStripes(IEnumerable<Guid> members, ParticipationSessionEconomyCommit? economy,
        IReadOnlyList<ParticipationInboxItem> inbox)
    {
        var players = new List<Guid>(members);
        foreach (var item in inbox) players.Add(item.PlayerId);
        if (economy is not null)
        {
            foreach (var balance in economy.ReadSet) players.Add(balance.PlayerId);
            if (economy.Transaction is { } request)
            {
                players.Add(request.ActorId);
                foreach (var delta in request.Deltas) players.Add(delta.PlayerId);
                if (request.Sections is not null)
                    foreach (var section in request.Sections) players.Add(section.PlayerId);
                if (request.Receipts is not null)
                    foreach (var receipt in request.Receipts) players.Add(receipt.PlayerId);
                if (request.ParticipationReceipt is { } participationReceipt) players.Add(participationReceipt.PlayerId);
            }
        }
        return PlayerCommitGates.StripesFor(players);
    }

    private static async ValueTask<IReadOnlyList<Guid>> LoadMemberIdsAsync(NpgsqlConnection connection,
        string sessionId, CancellationToken cancellationToken)
    {
        // 冻结名册不可变，事务前只用于确定条带集合；身份 fence 仍在后续事务内重新读取。
        await using var command = new NpgsqlCommand("""
            SELECT (member ->> 'playerId')::uuid
            FROM social.party_sessions s
            CROSS JOIN LATERAL jsonb_array_elements(s.spec -> 'members') member
            WHERE s.session_id = $1
            """, connection);
        command.Parameters.AddWithValue(sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var members = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken)) members.Add(reader.GetGuid(0));
        return members;
    }

    private static async ValueTask<bool> CommitEconomyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ParticipationSessionEconomyCommit? economy, CancellationToken cancellationToken)
    {
        if (economy is null) return true;
        foreach (var expected in economy.ReadSet.OrderBy(balance => balance.PlayerId)
                     .ThenBy(balance => balance.AssetId, StringComparer.Ordinal)
                     .ThenBy(balance => balance.Variant, StringComparer.Ordinal))
        {
            // 不存在的键也必须有行锁证明：零行仅承载锁与版本，不产生经济流水。
            // 表约束要求持久化版本从 1 起；只有本事务首次插入的行才能核对为原始缺失版本 0。
            var inserted = false;
            if (expected.Version == 0)
            {
                await using var ensure = new NpgsqlCommand("""
                    INSERT INTO economy.player_asset_balances(player_id, asset_id, variant, quantity, version, updated_at)
                    VALUES ($1, $2, $3, 0, 1, now())
                    ON CONFLICT (player_id, asset_id, variant) DO NOTHING
                    RETURNING version
                    """, connection, transaction);
                ensure.Parameters.AddWithValue(expected.PlayerId);
                ensure.Parameters.AddWithValue(expected.AssetId);
                ensure.Parameters.AddWithValue(expected.Variant);
                inserted = await ensure.ExecuteScalarAsync(cancellationToken) is not null;
            }
            await using var locked = new NpgsqlCommand("""
                SELECT quantity, version FROM economy.player_asset_balances
                WHERE player_id = $1 AND asset_id = $2 AND variant = $3
                FOR UPDATE
                """, connection, transaction);
            locked.Parameters.AddWithValue(expected.PlayerId);
            locked.Parameters.AddWithValue(expected.AssetId);
            locked.Parameters.AddWithValue(expected.Variant);
            await using var reader = await locked.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || reader.GetDouble(0) != expected.Quantity
                || (inserted ? 0 : reader.GetInt64(1)) != expected.Version)
                return false;
        }
        if (economy.Transaction is not { } request || !request.Deltas.Any(delta => delta.Delta != 0))
            return true;
        var committed = await PostgresEconomyRepository.CommitCoreAsync(connection, transaction, request, cancellationToken);
        // 检查点 CAS 已胜出却命中历史经济流水，说明本批误用了旧键；不能只推进而漏掉本批实耗。
        return !committed.AlreadyCommitted;
    }

    private static async ValueTask DeliverInboxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<ParticipationInboxItem> inbox, CancellationToken cancellationToken)
    {
        foreach (var item in inbox)
        {
            await using var delivery = new NpgsqlCommand("""
                INSERT INTO social.party_session_inbox(session_id, player_id, sequence, payload, created_at)
                VALUES ($1, $2, $3, $4::jsonb, $5)
                """, connection, transaction);
            delivery.Parameters.AddWithValue(item.SessionId);
            delivery.Parameters.AddWithValue(item.PlayerId);
            delivery.Parameters.AddWithValue(item.Sequence);
            delivery.Parameters.AddWithValue(NpgsqlDbType.Jsonb, item.PayloadJson);
            delivery.Parameters.AddWithValue(item.CreatedAt);
            await delivery.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void ValidateAssetKey(Guid playerId, string assetId, string variant)
    {
        if (playerId == Guid.Empty || string.IsNullOrWhiteSpace(assetId) || assetId.Length > 128
            || variant is null || variant.Length > 128)
            throw new ArgumentException("会话工作钱包资产键无效。");
    }

    private static void ValidateReadSet(ParticipationSessionEconomyCommit? economy)
    {
        if (economy is null) return;
        var keys = new HashSet<(Guid, string, string)>();
        foreach (var balance in economy.ReadSet)
        {
            ValidateAssetKey(balance.PlayerId, balance.AssetId, balance.Variant);
            if (!double.IsFinite(balance.Quantity) || balance.Quantity < 0 || balance.Version < 0
                || (balance.Version == 0 && balance.Quantity != 0)
                || !keys.Add((balance.PlayerId, balance.AssetId, balance.Variant)))
                throw new ArgumentException("会话工作钱包读集余额、版本或唯一键无效。", nameof(economy));
        }
    }

    private static void ValidateInbox(string sessionId, IReadOnlyList<ParticipationInboxItem> inbox)
    {
        var keys = new HashSet<(Guid PlayerId, long Sequence)>();
        foreach (var item in inbox)
            if (item.SessionId != sessionId || item.PlayerId == Guid.Empty || item.Sequence < 1
                || string.IsNullOrWhiteSpace(item.PayloadJson) || !keys.Add((item.PlayerId, item.Sequence)))
                throw new ArgumentException("会话结算收件箱标识、序号或载荷无效。", nameof(inbox));
    }

    // 与 Compute 结果的身份行锁共用 MVCC fence：仅检查会话不存在不足以隔离旧快照。
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
