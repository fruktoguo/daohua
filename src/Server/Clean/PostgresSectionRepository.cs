using System.Diagnostics;

namespace Server.Clean;

/// <summary>玩家 section 与 latest-only 刷盘队列的 PostgreSQL 实现。</summary>
public sealed class PostgresSectionRepository(
    NpgsqlDataSource dataSource,
    ILogger<PostgresSectionRepository> logger) : IPlayerStateRepository
{
    private readonly LogThrottle _flushErrorLog = new(TimeSpan.FromMinutes(1));
    public async ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // 头、section、action queue 和 latest-only 队列必须来自同一快照；
        // 否则 worker 恰好在读取中间提交会把旧 section 与新 head 拼成假状态。
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        PlayerIdentity? identity = null;
        await using (var command = new NpgsqlCommand("""
            SELECT player_id, account_id, display_name, created_at
            FROM identity.players WHERE player_id = $1 AND status = 1
            """, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                identity = new PlayerIdentity(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
            }
        }
        if (identity is null) return null;

        PlayerStateHead? head = null;
        await using (var command = new NpgsqlCommand("""
            SELECT player_id, state_version, schema_version, last_simulated_at,
                   game_data_fingerprint, checksum, updated_at
            FROM game.player_state_heads
            WHERE player_id = $1
            """, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                head = new PlayerStateHead(
                    reader.GetGuid(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetString(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6));
            }
        }

        if (head is null)
        {
            // 注册成功但尚未首次刷盘的玩家：用身份信息返回一个空 bundle，由运行时创建默认聚合。
            head = new PlayerStateHead(playerId, 0, SectionCodec.CurrentSchemaVersion, identity.CreatedAt, string.Empty, string.Empty, identity.CreatedAt);
        }

        var sections = new Dictionary<string, StoredSection>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("""
            SELECT player_id, section_kind, section_version, schema_version, codec,
                   payload, raw_bytes, checksum, updated_at
            FROM game.player_state_sections
            WHERE player_id = $1
            """, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var section = new StoredSection(
                    reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetString(4),
                    reader.GetFieldValue<byte[]>(5), reader.GetInt32(6), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8));
                sections.Add(section.SectionKind, section);
            }
        }

        // section 进入 durable latest-only 队列后，worker 可能尚未完成落库。
        // 加载必须把队列中的更新叠加到 game 表，否则进程在这段窗口崩溃会回退到旧快照。
        var pendingSections = await LoadPendingSectionsAsync(connection, transaction, playerId, cancellationToken);
        foreach (var pending in pendingSections)
        {
            if (!sections.TryGetValue(pending.SectionKind, out var persisted)
                || pending.StateVersion > persisted.StateVersion)
            {
                sections[pending.SectionKind] = new StoredSection(
                    pending.PlayerId,
                    pending.SectionKind,
                    pending.StateVersion,
                    pending.SchemaVersion,
                    pending.Codec,
                    pending.Payload,
                    pending.RawBytes,
                    pending.Checksum,
                    pending.UpdatedAt);
            }
            else if (pending.StateVersion == persisted.StateVersion
                     && !string.Equals(pending.Checksum, persisted.Checksum, StringComparison.Ordinal))
            {
                // 同一状态版本出现两份不同载荷意味着 durable 边界已损坏，不能静默选择其一。
                throw new InvalidDataException("玩家 section 同一版本载荷冲突。");
            }
        }

        head = BuildEffectiveHead(identity, head, sections, pendingSections);

        if (head.StateVersion > 0)
        {
            if (head.SchemaVersion != SectionCodec.CurrentSchemaVersion
                || sections.Count != PlayerSectionKinds.All.Count
                || PlayerSectionKinds.All.Any(kind => !sections.ContainsKey(kind))
                || sections.Values.Any(section => section.PlayerId != playerId || section.StateVersion > head.StateVersion))
                throw new InvalidDataException("玩家持久化 section 不完整或版本越界。");
            var checksum = SectionCodec.ComputeHeadChecksum(sections.Values);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(checksum), Encoding.ASCII.GetBytes(head.Checksum)))
                throw new InvalidDataException("玩家持久化头校验和不匹配。");
        }

        var pendingAction = pendingSections.FirstOrDefault(item => item.SectionKind == PlayerSectionKinds.ActionQueue);
        var actions = pendingAction is not null
            && sections.TryGetValue(PlayerSectionKinds.ActionQueue, out var effectiveAction)
            && effectiveAction.StateVersion == pendingAction.StateVersion
            ? DecodeQueuedActions(effectiveAction)
            : await LoadStoredActionsAsync(connection, transaction, playerId, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new PlayerStateBundle(identity, head, sections, actions);
    }

    private static async Task<List<StoredAction>> LoadStoredActionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        var actions = new List<StoredAction>();
        await using (var command = new NpgsqlCommand("""
                SELECT player_id, slot, action_id, action_payload, action_version, status,
                       next_due_at, idempotency_key
                FROM game.player_action_queue
                WHERE player_id = $1 ORDER BY slot
                """, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                actions.Add(new StoredAction(
                    reader.GetGuid(0), reader.GetInt16(1), reader.GetString(2), reader.GetFieldValue<byte[]>(3),
                    reader.GetInt64(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetString(7)));
            }
        }
        return actions;
    }

    private static async Task<List<FlushQueueItem>> LoadPendingSectionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        var result = new List<FlushQueueItem>();
        await using var command = new NpgsqlCommand("""
            SELECT player_id, section_kind, state_version, schema_version, codec,
                   payload, raw_bytes, checksum, attempts, next_attempt_at,
                   updated_at, game_data_fingerprint, last_simulated_at, lease_token
            FROM platform.section_flush_queue
            WHERE player_id = $1
            ORDER BY state_version, section_kind
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FlushQueueItem(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetFieldValue<byte[]>(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetInt32(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.IsDBNull(13) ? Guid.Empty : reader.GetGuid(13),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12)));
        }

        return result;
    }

    private static PlayerStateHead BuildEffectiveHead(
        PlayerIdentity identity,
        PlayerStateHead? persistedHead,
        IReadOnlyDictionary<string, StoredSection> sections,
        IReadOnlyList<FlushQueueItem> pending)
    {
        var fallback = persistedHead ?? new PlayerStateHead(
            identity.PlayerId,
            0,
            SectionCodec.CurrentSchemaVersion,
            identity.CreatedAt,
            string.Empty,
            string.Empty,
            identity.CreatedAt);
        if (sections.Count == 0 && pending.Count == 0) return fallback;

        var maxSectionVersion = sections.Count == 0 ? 0 : sections.Values.Max(section => section.StateVersion);
        var stateVersion = Math.Max(fallback.StateVersion, maxSectionVersion);
        if (stateVersion <= 0) return fallback;

        // 只有真正携带当前最高版本的 queue 行才有资格推进 head 的模拟时间/指纹；
        // 否则保留已提交 head，避免旧 worker 覆盖新元数据。
        var metadata = pending
            .Where(item => item.StateVersion == stateVersion)
            .OrderByDescending(item => item.UpdatedAt)
            .ToArray();
        var fingerprints = metadata
            .Select(item => item.GameDataFingerprint)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (fingerprints.Length > 1)
            throw new InvalidDataException("同一玩家状态版本携带了冲突的游戏数据指纹。");
        var fingerprint = metadata
            .Select(item => item.GameDataFingerprint)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? fallback.GameDataFingerprint;
        var lastSimulatedAt = metadata
            .Select(item => item.LastSimulatedAt)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .DefaultIfEmpty(fallback.LastSimulatedAt)
            .Max();
        var checksum = SectionCodec.ComputeHeadChecksum(sections.Values);
        return new PlayerStateHead(
            identity.PlayerId,
            stateVersion,
            SectionCodec.CurrentSchemaVersion,
            lastSimulatedAt < identity.CreatedAt ? identity.CreatedAt : lastSimulatedAt,
            fingerprint,
            checksum,
            metadata.Select(item => item.UpdatedAt).DefaultIfEmpty(fallback.UpdatedAt).Max());
    }

    private static IReadOnlyList<StoredAction> DecodeQueuedActions(StoredSection section)
    {
        var values = SectionCodec.Decode<List<QueuedActionState>>(section);
        return values.Select((action, slot) => new StoredAction(
            section.PlayerId,
            slot,
            action.Intent.ActionId.Value,
            JsonSerializer.SerializeToUtf8Bytes(action.Intent, ServerJson.Options),
            section.StateVersion,
            action.Status.ToString().ToLowerInvariant(),
            null,
            $"action:{action.Id:D}")).ToArray();
    }

    public async ValueTask EnqueueSectionsAsync(
        IReadOnlyCollection<SectionWrite> sections,
        CancellationToken cancellationToken = default)
        => await EnqueueSectionsAsync(sections, [], cancellationToken);

    public async ValueTask EnqueueSectionsAsync(
        IReadOnlyCollection<SectionWrite> sections,
        IReadOnlyCollection<CommandReceipt> receipts,
        CancellationToken cancellationToken = default)
    {
        if (sections.Count == 0 && receipts.Count == 0) return;
        foreach (var section in sections) section.Validate();
        foreach (var receipt in receipts) ValidateReceipt(receipt);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var section in sections)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO platform.section_flush_queue
                    (player_id, section_kind, state_version, schema_version, codec, payload,
                     raw_bytes, checksum, game_data_fingerprint, last_simulated_at,
                     attempts, next_attempt_at, lease_until, lease_token, last_error, updated_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 0, $10, NULL, NULL, NULL, $10)
                ON CONFLICT (player_id, section_kind) DO UPDATE SET
                    state_version = EXCLUDED.state_version,
                    schema_version = EXCLUDED.schema_version,
                    codec = EXCLUDED.codec,
                    payload = EXCLUDED.payload,
                    raw_bytes = EXCLUDED.raw_bytes,
                    checksum = EXCLUDED.checksum,
                    game_data_fingerprint = EXCLUDED.game_data_fingerprint,
                    last_simulated_at = EXCLUDED.last_simulated_at,
                    attempts = 0,
                    next_attempt_at = EXCLUDED.next_attempt_at,
                    lease_until = NULL,
                    lease_token = NULL,
                    last_error = NULL,
                    updated_at = EXCLUDED.updated_at
                WHERE EXCLUDED.state_version > platform.section_flush_queue.state_version
                """, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = section.PlayerId });
            command.Parameters.Add(new NpgsqlParameter { Value = section.SectionKind });
            command.Parameters.Add(new NpgsqlParameter { Value = section.StateVersion });
            command.Parameters.Add(new NpgsqlParameter { Value = section.SchemaVersion });
            command.Parameters.Add(new NpgsqlParameter { Value = section.Codec });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = section.Payload.ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = section.RawBytes });
            command.Parameters.Add(new NpgsqlParameter { Value = section.Checksum });
            command.Parameters.Add(new NpgsqlParameter { Value = section.GameDataFingerprint ?? string.Empty });
            command.Parameters.Add(new NpgsqlParameter { Value = section.LastSimulatedAt ?? section.UpdatedAt });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var receipt in receipts)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO platform.command_receipts
                    (player_id, request_id, command, payload_hash, result_payload, state_version, created_at, expires_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                ON CONFLICT (player_id, request_id) DO NOTHING
                """, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = receipt.PlayerId });
            command.Parameters.Add(new NpgsqlParameter { Value = receipt.RequestId });
            command.Parameters.Add(new NpgsqlParameter { Value = receipt.Command });
            command.Parameters.Add(new NpgsqlParameter { Value = receipt.PayloadHash });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = Array.Empty<byte>() });
            command.Parameters.Add(new NpgsqlParameter { Value = receipt.StateVersion });
            command.Parameters.Add(new NpgsqlParameter { Value = receipt.CreatedAt });
            command.Parameters.Add(new NpgsqlParameter { Value = receipt.ExpiresAt });
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await using var existing = new NpgsqlCommand("""
                    SELECT command, payload_hash
                    FROM platform.command_receipts
                    WHERE player_id = $1 AND request_id = $2
                    FOR SHARE
                    """, connection, transaction);
                existing.Parameters.Add(new NpgsqlParameter { Value = receipt.PlayerId });
                existing.Parameters.Add(new NpgsqlParameter { Value = receipt.RequestId });
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)
                    || !string.Equals(reader.GetString(0), receipt.Command, StringComparison.Ordinal)
                    || !string.Equals(reader.GetString(1), receipt.PayloadHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("命令回执幂等键已绑定其他请求。");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static void ValidateReceipt(CommandReceipt receipt)
    {
        if (receipt.PlayerId == Guid.Empty || receipt.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(receipt.Command)
            || receipt.Command.Length > 96 || receipt.PayloadHash.Length != 64
            || receipt.PayloadHash.Any(character => !Uri.IsHexDigit(character)) || receipt.StateVersion < 0
            || receipt.ExpiresAt <= receipt.CreatedAt || receipt.ExpiresAt - receipt.CreatedAt > TimeSpan.FromDays(2))
            throw new ArgumentException("命令回执参数无效。", nameof(receipt));
    }

    public async ValueTask<FlushBatchResult> FlushPendingAsync(
        int maxRows,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        if (maxRows <= 0) return new FlushBatchResult(0, 0, 0, TimeSpan.Zero);
        var stopwatch = Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(lease);
        var leaseToken = Guid.NewGuid();
        var claimed = new List<FlushQueueItem>(maxRows);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            // 以 UPDATE ... FROM CTE 原子领取，避免 SELECT 后事务回滚导致租约没有落盘。
            await using var command = new NpgsqlCommand("""
                WITH candidates AS (
                    SELECT player_id, section_kind
                    FROM platform.section_flush_queue
                    WHERE next_attempt_at <= $1
                      AND (lease_until IS NULL OR lease_until < $1)
                    ORDER BY updated_at
                    FOR UPDATE SKIP LOCKED
                    LIMIT $2
                )
                UPDATE platform.section_flush_queue AS q
                SET lease_until = $3, lease_token = $4
                FROM candidates AS c
                WHERE q.player_id = c.player_id AND q.section_kind = c.section_kind
                RETURNING q.player_id, q.section_kind, q.state_version, q.schema_version, q.codec,
                          q.payload, q.raw_bytes, q.checksum, q.attempts, q.next_attempt_at,
                          q.updated_at, q.game_data_fingerprint, q.last_simulated_at
                """, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = now });
            command.Parameters.Add(new NpgsqlParameter { Value = maxRows });
            command.Parameters.Add(new NpgsqlParameter { Value = leaseUntil });
            command.Parameters.Add(new NpgsqlParameter { Value = leaseToken });
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    claimed.Add(new FlushQueueItem(
                        reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetString(4),
                        reader.GetFieldValue<byte[]>(5), reader.GetInt32(6), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(10),
                        reader.GetInt32(8), reader.GetFieldValue<DateTimeOffset>(9), leaseToken,
                        reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12)));
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }

        if (claimed.Count == 0)
        {
            stopwatch.Stop();
            return new FlushBatchResult(0, 0, 0, stopwatch.Elapsed);
        }

        var applied = 0;
        var failed = 0;
        // 同一玩家、同一状态版本在一个事务内完成；不同玩家之间独立提交，避免一名玩家的坏载荷阻塞整批。
        // 同一玩家可能因不同 section 的刷盘时机出现多个版本；严格按版本升序应用，
        // 避免旧 action_queue 覆盖已经提交的新队列。
        foreach (var group in claimed
                     .GroupBy(item => (item.PlayerId, item.StateVersion))
                     .OrderBy(group => group.Key.PlayerId)
                     .ThenBy(group => group.Key.StateVersion))
        {
            var items = group.ToArray();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await ApplyBatchAsync(connection, transaction, items, now, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                applied += items.Length;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                failed += items.Length;
                if (_flushErrorLog.TryAcquire())
                    logger.LogError(ex, "写入玩家 section 批次失败 player={PlayerId} version={Version} sections={Sections}",
                        group.Key.PlayerId, group.Key.StateVersion, string.Join(',', items.Select(item => item.SectionKind)));
                await using var releaseTransaction = await connection.BeginTransactionAsync(cancellationToken);
                foreach (var item in items)
                {
                    await ReleaseClaimAsync(connection, releaseTransaction, item, ex.Message, cancellationToken);
                }

                await releaseTransaction.CommitAsync(cancellationToken);
            }
        }

        stopwatch.Stop();
        return new FlushBatchResult(claimed.Count, applied, failed, stopwatch.Elapsed);
    }

    private static async Task ApplyBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<FlushQueueItem> items,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;
        var first = items[0];
        foreach (var item in items)
        {
            if (item.PlayerId != first.PlayerId || item.StateVersion != first.StateVersion)
                throw new InvalidDataException("section 批次必须属于同一玩家和状态版本。");
            ValidateQueueItem(item);
            await using var command = new NpgsqlCommand("""
            INSERT INTO game.player_state_sections
                (player_id, section_kind, section_version, schema_version, codec, payload,
                 raw_bytes, stored_bytes, checksum, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            ON CONFLICT (player_id, section_kind) DO UPDATE SET
                section_version = EXCLUDED.section_version,
                schema_version = EXCLUDED.schema_version,
                codec = EXCLUDED.codec,
                payload = EXCLUDED.payload,
                raw_bytes = EXCLUDED.raw_bytes,
                stored_bytes = EXCLUDED.stored_bytes,
                checksum = EXCLUDED.checksum,
                updated_at = EXCLUDED.updated_at
            WHERE EXCLUDED.section_version > game.player_state_sections.section_version
            """, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter { Value = item.PlayerId });
            command.Parameters.Add(new NpgsqlParameter { Value = item.SectionKind });
            command.Parameters.Add(new NpgsqlParameter { Value = item.StateVersion });
            command.Parameters.Add(new NpgsqlParameter { Value = item.SchemaVersion });
            command.Parameters.Add(new NpgsqlParameter { Value = item.Codec });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = item.Payload });
            command.Parameters.Add(new NpgsqlParameter { Value = item.RawBytes });
            command.Parameters.Add(new NpgsqlParameter { Value = item.Payload.Length });
            command.Parameters.Add(new NpgsqlParameter { Value = item.Checksum });
            command.Parameters.Add(new NpgsqlParameter { Value = item.UpdatedAt });
            var sectionApplied = await command.ExecuteNonQueryAsync(cancellationToken) > 0;

            // 仅当这次 UPSERT 真正写入了更新版本时才重建关系型行动队列。
            // PostgreSQL 的 ON CONFLICT ... WHERE 对旧版本返回 0，不能让旧载荷删除新队列。
            if (sectionApplied && item.SectionKind == PlayerSectionKinds.ActionQueue)
            {
                await ApplyActionQueueAsync(connection, transaction, item, cancellationToken);
            }
        }

        var checkpoints = await ReadSectionChecksumsAsync(connection, transaction, first.PlayerId, cancellationToken);
        var hasCompleteState = PlayerSectionKinds.All.All(kind => checkpoints.Any(value => value.SectionKind == kind));
        if (hasCompleteState)
        {
            var headChecksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', checkpoints
                .OrderBy(value => value.SectionKind, StringComparer.Ordinal)
                .Select(value => $"{value.SectionKind}:{value.SectionVersion}:{value.Checksum}"))))).ToLowerInvariant();
            var maxSectionVersion = checkpoints.Max(value => value.SectionVersion);
            var metadata = items
                .Where(item => item.StateVersion == maxSectionVersion)
                .OrderByDescending(item => item.UpdatedAt)
                .ToArray();
            var fingerprints = metadata
                .Select(item => item.GameDataFingerprint)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (fingerprints.Length > 1)
                throw new InvalidDataException("同一玩家状态版本携带了冲突的游戏数据指纹。");
            // 旧 worker 可能只提交了较低版本 section；没有当前最高版本的元数据时，
            // 保留数据库已有 head，避免把 last_simulated_at/指纹回拨。
            var existingHead = await ReadHeadMetadataAsync(connection, transaction, first.PlayerId, cancellationToken);
            var fingerprint = metadata.Select(item => item.GameDataFingerprint).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? existingHead?.GameDataFingerprint;
            var lastSimulatedAt = metadata
                .Select(item => item.LastSimulatedAt)
                .Where(value => value is not null)
                .Select(value => value!.Value)
                .DefaultIfEmpty(existingHead?.LastSimulatedAt ?? first.UpdatedAt)
                .Max();
            if (string.IsNullOrWhiteSpace(fingerprint) && existingHead is null)
                throw new InvalidDataException("完整 section 缺少游戏数据指纹，无法建立提交头。");
            if (metadata.Length == 0 && existingHead is null)
                throw new InvalidDataException("完整 section 缺少当前版本元数据，无法建立提交头。");
            var headVersion = Math.Max(maxSectionVersion, existingHead?.StateVersion ?? 0);
            await using (var command = new NpgsqlCommand("""
                INSERT INTO game.player_state_heads
                    (player_id, state_version, schema_version, last_simulated_at,
                     game_data_fingerprint, checksum, updated_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                ON CONFLICT (player_id) DO UPDATE SET
                    state_version = EXCLUDED.state_version,
                    schema_version = EXCLUDED.schema_version,
                    last_simulated_at = EXCLUDED.last_simulated_at,
                    game_data_fingerprint = EXCLUDED.game_data_fingerprint,
                    checksum = EXCLUDED.checksum,
                    updated_at = EXCLUDED.updated_at
                WHERE EXCLUDED.state_version > game.player_state_heads.state_version
                   OR (EXCLUDED.state_version = game.player_state_heads.state_version
                       AND EXCLUDED.checksum <> game.player_state_heads.checksum)
                """, connection, transaction))
            {
                command.Parameters.Add(new NpgsqlParameter { Value = first.PlayerId });
                command.Parameters.Add(new NpgsqlParameter { Value = headVersion });
                command.Parameters.Add(new NpgsqlParameter { Value = first.SchemaVersion });
                command.Parameters.Add(new NpgsqlParameter { Value = lastSimulatedAt });
                command.Parameters.Add(new NpgsqlParameter { Value = fingerprint });
                command.Parameters.Add(new NpgsqlParameter { Value = headChecksum });
                command.Parameters.Add(new NpgsqlParameter { Value = now });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        foreach (var item in items)
        {
            await using var delete = new NpgsqlCommand("""
                DELETE FROM platform.section_flush_queue
                WHERE player_id = $1 AND section_kind = $2
                  AND state_version <= $3 AND lease_token = $4
                """, connection, transaction);
            delete.Parameters.Add(new NpgsqlParameter { Value = item.PlayerId });
            delete.Parameters.Add(new NpgsqlParameter { Value = item.SectionKind });
            delete.Parameters.Add(new NpgsqlParameter { Value = item.StateVersion });
            delete.Parameters.Add(new NpgsqlParameter { Value = item.LeaseToken });
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void ValidateQueueItem(FlushQueueItem item)
    {
        item.Validate(requireLease: true);
        if (item.SchemaVersion != SectionCodec.CurrentSchemaVersion)
            throw new InvalidDataException("section 队列 schema 版本不受支持。");
    }

    private sealed record SectionCheckpoint(string SectionKind, long SectionVersion, string Checksum);

    private sealed record HeadMetadata(long StateVersion, DateTimeOffset LastSimulatedAt, string GameDataFingerprint);

    private static async Task<IReadOnlyList<SectionCheckpoint>> ReadSectionChecksumsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT section_kind, section_version, checksum
            FROM game.player_state_sections
            WHERE player_id = $1
            ORDER BY section_kind
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var checksums = new List<SectionCheckpoint>();
        while (await reader.ReadAsync(cancellationToken))
        {
            checksums.Add(new SectionCheckpoint(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        }

        return checksums;
    }

    private static async Task<HeadMetadata?> ReadHeadMetadataAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT state_version, last_simulated_at, game_data_fingerprint
            FROM game.player_state_heads
            WHERE player_id = $1
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new HeadMetadata(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2));
    }

    private static async Task ApplyActionQueueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FlushQueueItem item,
        CancellationToken cancellationToken)
    {
        var stored = new StoredSection(item.PlayerId, item.SectionKind, item.StateVersion, item.SchemaVersion, item.Codec,
            item.Payload, item.RawBytes, item.Checksum, item.UpdatedAt);
        var actions = SectionCodec.Decode<List<QueuedActionState>>(stored);
        await using (var delete = new NpgsqlCommand("DELETE FROM game.player_action_queue WHERE player_id = $1", connection, transaction))
        {
            delete.Parameters.Add(new NpgsqlParameter { Value = item.PlayerId });
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var slot = 0; slot < actions.Count; slot++)
        {
            var action = actions[slot];
            await using var insert = new NpgsqlCommand("""
                INSERT INTO game.player_action_queue
                    (player_id, slot, action_id, action_payload, action_version, status, next_due_at, idempotency_key, updated_at)
                VALUES ($1, $2, $3, $4, $5, $6, NULL, $7, $8)
                """, connection, transaction);
            insert.Parameters.Add(new NpgsqlParameter { Value = item.PlayerId });
            insert.Parameters.Add(new NpgsqlParameter { Value = slot });
            insert.Parameters.Add(new NpgsqlParameter { Value = action.Intent.ActionId.Value });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = JsonSerializer.SerializeToUtf8Bytes(action.Intent, ServerJson.Options) });
            insert.Parameters.Add(new NpgsqlParameter { Value = item.StateVersion });
            insert.Parameters.Add(new NpgsqlParameter { Value = action.Status.ToString().ToLowerInvariant() });
            insert.Parameters.Add(new NpgsqlParameter { Value = $"action:{action.Id:D}" });
            insert.Parameters.Add(new NpgsqlParameter { Value = item.UpdatedAt });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ReleaseClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FlushQueueItem item,
        string error,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE platform.section_flush_queue
            SET attempts = attempts + 1,
                next_attempt_at = now() + LEAST((2 ^ LEAST(attempts + 1, 8)) * interval '1 second', interval '5 minutes'),
                lease_until = NULL,
                lease_token = NULL,
                last_error = left($1, 512),
                updated_at = now()
            WHERE player_id = $2 AND section_kind = $3 AND lease_token = $4
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = error });
        command.Parameters.Add(new NpgsqlParameter { Value = item.PlayerId });
        command.Parameters.Add(new NpgsqlParameter { Value = item.SectionKind });
        command.Parameters.Add(new NpgsqlParameter { Value = item.LeaseToken });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>latest-only section 刷盘后台任务；队列故障不被静默吞掉。</summary>
public sealed class SectionFlushWorker(
    IPlayerStateRepository repository,
    ServerOptions options,
    ILogger<SectionFlushWorker> logger) : BackgroundService
{
    private readonly LogThrottle _loopErrorLog = new(TimeSpan.FromMinutes(1));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SectionFlushWorker started batch={Batch} interval={Interval}.", options.SectionFlushBatchSize, options.SectionFlushInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await repository.FlushPendingAsync(options.SectionFlushBatchSize, options.SectionFlushLease, stoppingToken);
                if (result.Claimed > 0)
                {
                    logger.LogDebug("section flush claimed={Claimed} applied={Applied} failed={Failed} durationMs={DurationMs}",
                        result.Claimed, result.Applied, result.Failed, result.Duration.TotalMilliseconds);
                }
                await Task.Delay(options.SectionFlushInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_loopErrorLog.TryAcquire())
                    logger.LogError(ex, "SectionFlushWorker 循环失败，将在下个周期重试。");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
