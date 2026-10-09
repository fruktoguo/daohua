using System.Diagnostics;

namespace Server.Clean;

/// <summary>玩家 section 与 latest-only 刷盘队列的 PostgreSQL 实现。</summary>
public sealed class PostgresSectionRepository(
    NpgsqlDataSource dataSource,
    ILogger<PostgresSectionRepository> logger) : IPlayerStateRepository
{
    private readonly LogThrottle _flushErrorLog = new(TimeSpan.FromMinutes(1));
    /// <summary>
    /// 加载玩家完整持久化状态：identity + 提交头 + 已落盘 section + 尚未落盘的 flush 队列行
    /// 在 RepeatableRead 快照内合并，队列中的较新版本覆盖旧快照；玩家不存在返回 null，
    /// 完整性/校验和不过抛 InvalidDataException。
    /// </summary>
    public async ValueTask<PlayerStateBundle?> LoadAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // 头、section、action queue 和 latest-only 队列必须来自同一快照；
        // 否则 worker 恰好在读取中间提交会把旧 section 与新 head 拼成假状态。
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var bundle = await LoadInTransactionAsync(connection, transaction, playerId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return bundle;
    }

    /// <summary>在调用方的一致事务快照中加载有效头、全集分段与耐久队列；不提交事务。</summary>
    internal static async ValueTask<PlayerStateBundle?> LoadInTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid playerId, CancellationToken cancellationToken)
    {
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

        return new PlayerStateBundle(identity, head, sections, actions);
    }

    /// <summary>
    /// 读取 Compute 任务所需的完整玩家状态与余额版本快照；两者在同一 RepeatableRead 事务内冻结。
    /// </summary>
    public async ValueTask<ComputeFrozenInput?> LoadComputeInputAsync(
        Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var bundle = await LoadInTransactionAsync(connection, transaction, playerId, cancellationToken);
        if (bundle is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var balances = new List<AssetBalanceRecord>();
        await using (var command = new NpgsqlCommand("""
            SELECT player_id, asset_id, variant, quantity, version
            FROM economy.player_asset_balances
            WHERE player_id = $1 AND quantity > 0
            ORDER BY asset_id, variant
            """, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                balances.Add(new AssetBalanceRecord(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetInt64(4)));
        }
        await transaction.CommitAsync(cancellationToken);
        return new ComputeFrozenInput(bundle, balances);
    }

    /// <summary>
    /// 读取玩家单个分段的生效载荷：已落盘行 + 刷盘队列中较新版本叠加（与 <see cref="LoadAsync"/> 同一口径）。
    /// 供身价估值等轻量场景只取目标分段，不必加载全部八段与整包校验。
    /// </summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="sectionKind">分段键（见 <see cref="PlayerSectionKinds"/>）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>生效分段；未落盘且队列无挂起时为 null。</returns>
    public async ValueTask<StoredSection?> LoadSectionAsync(Guid playerId, string sectionKind, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        StoredSection? persisted = null;
        await using (var command = new NpgsqlCommand("""
            SELECT player_id, section_kind, section_version, schema_version, codec,
                   payload, raw_bytes, checksum, updated_at
            FROM game.player_state_sections
            WHERE player_id = $1 AND section_kind = $2
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            command.Parameters.Add(new NpgsqlParameter { Value = sectionKind });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                persisted = new StoredSection(
                    reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetString(4),
                    reader.GetFieldValue<byte[]>(5), reader.GetInt32(6), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8));
            }
        }
        await using (var command = new NpgsqlCommand("""
            SELECT player_id, section_kind, state_version, schema_version, codec,
                   payload, raw_bytes, checksum, updated_at
            FROM platform.section_flush_queue
            WHERE player_id = $1 AND section_kind = $2
            ORDER BY state_version DESC
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            command.Parameters.Add(new NpgsqlParameter { Value = sectionKind });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            // 队列为 latest-only：首个（最新）行即候选；比已落盘版本新则采用，同版本校验不一致载荷。
            if (await reader.ReadAsync(cancellationToken))
            {
                var pending = new StoredSection(
                    reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetString(4),
                    reader.GetFieldValue<byte[]>(5), reader.GetInt32(6), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8));
                if (persisted is null || pending.StateVersion > persisted.StateVersion) persisted = pending;
                else if (pending.StateVersion == persisted.StateVersion
                         && !string.Equals(pending.Checksum, persisted.Checksum, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("玩家 section 同一版本载荷冲突。");
                }
            }
        }
        return persisted;
    }

    /// <summary>
    /// 批量读取多名玩家同一分段的生效载荷：已落盘行 + 刷盘队列中较新版本叠加（与 <see cref="LoadAsync"/> 同一口径）。
    /// 供组队大厅等批量估值场景一次往返取回全体成员的 loadout 分段。
    /// </summary>
    /// <param name="playerIds">玩家 ID 集合。</param>
    /// <param name="sectionKind">分段键（见 <see cref="PlayerSectionKinds"/>）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>playerId → 生效分段；未落盘且队列无挂起的玩家缺席。</returns>
    public async ValueTask<IReadOnlyDictionary<Guid, StoredSection>> LoadSectionsAsync(
        IReadOnlyCollection<Guid> playerIds, string sectionKind, CancellationToken cancellationToken = default)
    {
        if (playerIds.Count == 0) return new Dictionary<Guid, StoredSection>();
        var ids = playerIds.ToArray();
        var result = new Dictionary<Guid, StoredSection>(ids.Length);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("""
            SELECT player_id, section_kind, section_version, schema_version, codec,
                   payload, raw_bytes, checksum, updated_at
            FROM game.player_state_sections
            WHERE player_id = ANY($1) AND section_kind = $2
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = ids });
            command.Parameters.Add(new NpgsqlParameter { Value = sectionKind });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result[reader.GetGuid(0)] = new StoredSection(
                    reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetString(4),
                    reader.GetFieldValue<byte[]>(5), reader.GetInt32(6), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8));
            }
        }
        await using (var command = new NpgsqlCommand("""
            SELECT player_id, section_kind, state_version, schema_version, codec,
                   payload, raw_bytes, checksum, updated_at
            FROM platform.section_flush_queue
            WHERE player_id = ANY($1) AND section_kind = $2
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = ids });
            command.Parameters.Add(new NpgsqlParameter { Value = sectionKind });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var pending = new StoredSection(
                    reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetString(4),
                    reader.GetFieldValue<byte[]>(5), reader.GetInt32(6), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8));
                if (!result.TryGetValue(pending.PlayerId, out var persisted)
                    || pending.StateVersion > persisted.StateVersion) result[pending.PlayerId] = pending;
                else if (pending.StateVersion == persisted.StateVersion
                         && !string.Equals(pending.Checksum, persisted.Checksum, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("玩家 section 同一版本载荷冲突。");
                }
            }
        }
        return result;
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

    /// <summary>把脏 section 写入 latest-only 刷盘队列（无回执场景）。</summary>
    public async ValueTask EnqueueSectionsAsync(
        IReadOnlyCollection<SectionWrite> sections,
        CancellationToken cancellationToken = default)
        => await EnqueueSectionsAsync(sections, [], cancellationToken);

    /// <summary>同事务入队 section 与命令回执；写回失败整体回滚，保证回执不会先于状态生效。</summary>
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
        await EnqueueInTransactionAsync(connection, transaction, sections, receipts, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 在调用方事务内写入 flush 队列与命令回执；供经济事务等需要「结算 + 状态写回」
    /// 原子提交的路径复用。队列 UPSERT 只接受更高 state_version，旧版本写回被静默丢弃。
    /// </summary>
    internal static async ValueTask EnqueueInTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyCollection<SectionWrite> sections, IReadOnlyCollection<CommandReceipt> receipts, CancellationToken cancellationToken)
    {
        foreach (var section in sections) section.Validate();
        foreach (var receipt in receipts) ValidateReceipt(receipt);
        if (sections.Count > 0)
        {
            var uniqueKinds = new HashSet<(Guid PlayerId, string SectionKind)>();
            foreach (var section in sections)
                if (!uniqueKinds.Add((section.PlayerId, section.SectionKind)))
                    throw new InvalidDataException("section 队列包含重复分段。");
            await using var batch = connection.CreateBatch();
            batch.Transaction = transaction;
            foreach (var section in sections)
            {
                var batchCommand = new NpgsqlBatchCommand("""
                    INSERT INTO platform.section_flush_queue
                        (player_id, section_kind, state_version, schema_version, codec, payload,
                         raw_bytes, checksum, game_data_fingerprint, last_simulated_at,
                         attempts, next_attempt_at, lease_until, lease_token, last_error, updated_at)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 0, $11, NULL, NULL, NULL, $11)
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
                    """);
                batchCommand.Parameters.Add(new NpgsqlParameter { Value = section.PlayerId });
                batchCommand.Parameters.Add(new NpgsqlParameter { Value = section.SectionKind });
                batchCommand.Parameters.Add(new NpgsqlParameter { Value = section.StateVersion });
                batchCommand.Parameters.Add(new NpgsqlParameter { Value = section.SchemaVersion });
                batchCommand.Parameters.Add(new NpgsqlParameter { Value = section.Codec });
                batchCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = section.Payload });
                batchCommand.Parameters.Add(new NpgsqlParameter { Value = section.RawBytes });
                batchCommand.Parameters.Add(new NpgsqlParameter { Value = section.Checksum });
                batchCommand.Parameters.Add(new NpgsqlParameter { Value = section.GameDataFingerprint ?? string.Empty });
                batchCommand.Parameters.Add(new NpgsqlParameter { Value = section.LastSimulatedAt ?? section.UpdatedAt });
                batchCommand.Parameters.Add(new NpgsqlParameter { Value = section.UpdatedAt });
                batch.BatchCommands.Add(batchCommand);
            }
            await batch.ExecuteNonQueryAsync(cancellationToken);
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

    }

    private static void ValidateReceipt(CommandReceipt receipt)
    {
        if (receipt.PlayerId == Guid.Empty || receipt.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(receipt.Command)
            || receipt.Command.Length > 96 || receipt.PayloadHash.Length != 64
            || receipt.PayloadHash.Any(character => !Uri.IsHexDigit(character)) || receipt.StateVersion < 0
            || receipt.ExpiresAt <= receipt.CreatedAt || receipt.ExpiresAt - receipt.CreatedAt > TimeSpan.FromDays(2))
            throw new ArgumentException("命令回执参数无效。", nameof(receipt));
    }

    /// <summary>
    /// 租约认领一批到期队列行并落盘：先 UPDATE...FROM CTE 原子打租约，再按（玩家, 版本）分组
    /// 逐组开事务应用；单组失败回滚该组并退避重试，不阻塞其他玩家。认领行中的较旧版本
    /// 靠 section_version 谓词自然丢弃，不会让旧快照覆盖新状态。
    /// </summary>
    public ValueTask<FlushBatchResult> FlushPendingAsync(
        int maxRows,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
        => FlushPendingCoreAsync(maxRows, lease, ignoreSchedule: false, cancellationToken);

    /// <summary>
    /// 基准收尾专用刷盘：立即领取队列中的行，忽略模拟时钟产生的 next_attempt_at。
    /// 正常后台 worker 仍使用 FlushPendingAsync 的退避调度，避免改变生产重试语义。
    /// </summary>
    public ValueTask<FlushBatchResult> FlushPendingNowAsync(
        int maxRows,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
        => FlushPendingCoreAsync(maxRows, lease, ignoreSchedule: true, cancellationToken);

    private async ValueTask<FlushBatchResult> FlushPendingCoreAsync(
        int maxRows,
        TimeSpan lease,
        bool ignoreSchedule,
        CancellationToken cancellationToken)
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
                    WHERE ($5 OR next_attempt_at <= $1)
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
            command.Parameters.Add(new NpgsqlParameter { Value = ignoreSchedule });
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
                await ReleaseClaimsAsync(connection, releaseTransaction, items, ex.Message, cancellationToken);
                await releaseTransaction.CommitAsync(cancellationToken);
            }
        }

        stopwatch.Stop();
        return new FlushBatchResult(claimed.Count, applied, failed, stopwatch.Elapsed);
    }

    /// <summary>
    /// 冷挂机计算结果落库：仅当提交头仍是任务发起时的 sourceStateVersion 才写入，
    /// 在线写者或其他补算已推进提交头时整体丢弃，保证远端结果绝不覆盖较新状态。
    /// 与 ApplyBatchAsync 遵循同一加锁顺序（先提交头后 section 行）。
    /// </summary>
    /// <param name="playerId">玩家唯一标识。</param>
    /// <param name="sourceStateVersion">任务发起时的提交头版本。</param>
    /// <param name="targetStateVersion">推进后的目标版本。</param>
    /// <param name="writes">节点回传并已完成线侧校验的 section 写入集。</param>
    /// <param name="lastSimulatedAt">推进后的模拟时刻。</param>
    /// <param name="gameDataFingerprint">本批使用的游戏数据指纹。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async ValueTask<ComputeApplyOutcome> ApplyComputeResultAsync(
        Guid playerId,
        long sourceStateVersion,
        long targetStateVersion,
        IReadOnlyList<SectionWrite> writes,
        DateTimeOffset lastSimulatedAt,
        string gameDataFingerprint,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var outcome = await ApplyComputeResultInTransactionAsync(
            connection, transaction, playerId, sourceStateVersion, targetStateVersion,
            writes, lastSimulatedAt, gameDataFingerprint, cancellationToken);
        if (outcome == ComputeApplyOutcome.Applied)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            // 未应用时事务内只有提交头行锁读，回滚释放即可；合并事务路径由调用方自行提交。
            await transaction.RollbackAsync(cancellationToken);
        }
        return outcome;
    }

    /// <summary>
    /// 在调用方事务内执行冷挂机结果落库：提交头行锁 + 版本 CAS + section 合批 upsert + 提交头更新。
    /// 供 <see cref="ComputeDispatcherWorker"/> 把「section 写回 + compute_jobs 完结」并入同一事务；
    /// 调用方负责提交或回滚，本方法只返回结局不触碰事务边界。
    /// </summary>
    /// <summary>
    /// 在调用方事务内执行冷挂机结果落库：提交头行锁 + 版本 CAS + section 合批 upsert + 提交头更新。
    /// 供 <see cref="ComputeDispatcherWorker"/> 把「section 写回 + compute_jobs 完结」并入同一事务；
    /// 调用方负责提交或回滚，本方法只返回结局不触碰事务边界。
    /// compute.v2 口径允许两类空写：
    /// 1. <paramref name="targetStateVersion"/> 等于源版本（空推进，无 section 变更）——只前移提交头
    ///    的 last_simulated_at/指纹，防止无变化玩家在扫描窗口内被无限重派且每次重算整个欠债窗口；
    /// 2. 目标版本前进但 <paramref name="writes"/> 为空（48 小时封顶尾段只推时钟）——提交头照常升级。
    /// 同版本携带 section 写回属于契约违反（脏段必然伴随版本递增），调用方先行拒绝。
    /// </summary>
    internal async ValueTask<ComputeApplyOutcome> ApplyComputeResultInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playerId,
        long sourceStateVersion,
        long targetStateVersion,
        IReadOnlyList<SectionWrite> writes,
        DateTimeOffset lastSimulatedAt,
        string gameDataFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (targetStateVersion < sourceStateVersion
            || (targetStateVersion == sourceStateVersion && writes.Count > 0))
            throw new ArgumentException("计算结果版本与载荷无效。");
        // 重复 (player, kind) 是跨边界载荷歧义，必须 fail-closed，不能用 DistinctBy 静默吞掉。
        var uniqueKinds = new HashSet<(Guid PlayerId, string SectionKind)>();
        foreach (var write in writes)
        {
            write.Validate();
            if (!uniqueKinds.Add((write.PlayerId, write.SectionKind)))
                throw new InvalidDataException("计算结果包含重复 section。");
        }
        var deduped = writes.ToArray();

        var now = DateTimeOffset.UtcNow;
        long headVersion;
        await using (var headCommand = new NpgsqlCommand("""
            SELECT state_version FROM game.player_state_heads WHERE player_id = $1 FOR UPDATE
            """, connection, transaction))
        {
            headCommand.Parameters.Add(new NpgsqlParameter { Value = playerId });
            var current = await headCommand.ExecuteScalarAsync(cancellationToken);
            if (current is not long version)
                return ComputeApplyOutcome.MissingPlayer;
            headVersion = version;
        }
        if (headVersion != sourceStateVersion)
            return ComputeApplyOutcome.StaleVersion;

        if (targetStateVersion == sourceStateVersion)
        {
            // 空推进：版本与 section 集不变，仅前移模拟时钟与配表指纹（checksum 覆盖 section，无需重算）。
            // 行锁已保证串行，版本谓词仍是最后防线；漂移时与下方全量更新一致抛错回滚。
            await using (var headUpdate = new NpgsqlCommand("""
                UPDATE game.player_state_heads
                SET last_simulated_at = $2, game_data_fingerprint = $3, updated_at = $4
                WHERE player_id = $1 AND state_version = $5
                """, connection, transaction))
            {
                headUpdate.Parameters.Add(new NpgsqlParameter { Value = playerId });
                headUpdate.Parameters.Add(new NpgsqlParameter { Value = lastSimulatedAt });
                headUpdate.Parameters.Add(new NpgsqlParameter { Value = gameDataFingerprint });
                headUpdate.Parameters.Add(new NpgsqlParameter { Value = now });
                headUpdate.Parameters.Add(new NpgsqlParameter { Value = sourceStateVersion });
                if (await headUpdate.ExecuteNonQueryAsync(cancellationToken) == 0)
                    throw new InvalidOperationException("计算结果落库时提交头发生并发漂移。");
            }
            return ComputeApplyOutcome.Applied;
        }

        // 封顶尾段「只推时钟」会让目标版本前进而本节集合为空：跳过 upsert 直接更新提交头。
        var applied = deduped.Length == 0
            ? []
            : await UpsertSectionsAsync(connection, transaction, deduped, cancellationToken);
        // 仅当这次 UPSERT 真正写入了更新版本时才重建关系型行动队列（RETURNING 集合判定）。
        foreach (var write in deduped)
        {
            if (write.SectionKind == PlayerSectionKinds.ActionQueue
                && applied.Contains((write.PlayerId, write.SectionKind)))
            {
                await ApplyActionQueueAsync(connection, transaction,
                    new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion, write.SchemaVersion,
                        write.Codec, write.Payload.ToArray(), write.RawBytes, write.Checksum, write.UpdatedAt),
                    cancellationToken);
            }
        }

        // 冷挂机回写同样维护读模型投影（建筑拥挤计数依赖 current_node_id）：补算迁地/上下线
        // 状态变化经本路径落库时必须同步投影，否则节点使用人数会按滞留旧节点聚合。
        // 不算玩家活跃——last_active_at 不推进，避免扭曲冷扫描窗口口径。
        var projectable = deduped
            .Where(write => applied.Contains((write.PlayerId, write.SectionKind))
                && write.SectionKind is PlayerSectionKinds.Progression
                    or PlayerSectionKinds.World
                    or PlayerSectionKinds.Settings)
            .Select(write => new StoredSection(write.PlayerId, write.SectionKind, write.StateVersion, write.SchemaVersion,
                write.Codec, write.Payload.ToArray(), write.RawBytes, write.Checksum, write.UpdatedAt))
            .ToArray();
        if (projectable.Length > 0)
            await UpsertProjectionAsync(connection, transaction, playerId, projectable, now, markActive: false, cancellationToken);

        var checkpoints = await ReadSectionChecksumsAsync(connection, transaction, playerId, cancellationToken);
        var headChecksum = ComputeHeadChecksum(checkpoints, out _);
        // 提交头条件更新兜底：行锁已保证串行，此处仍带版本谓词作为最后防线。
        await using (var headUpdate = new NpgsqlCommand("""
            UPDATE game.player_state_heads
            SET state_version = $2, last_simulated_at = $3, game_data_fingerprint = $4,
                checksum = $5, updated_at = $6
            WHERE player_id = $1 AND state_version = $7
            """, connection, transaction))
        {
            headUpdate.Parameters.Add(new NpgsqlParameter { Value = playerId });
            headUpdate.Parameters.Add(new NpgsqlParameter { Value = targetStateVersion });
            headUpdate.Parameters.Add(new NpgsqlParameter { Value = lastSimulatedAt });
            headUpdate.Parameters.Add(new NpgsqlParameter { Value = gameDataFingerprint });
            headUpdate.Parameters.Add(new NpgsqlParameter { Value = headChecksum });
            headUpdate.Parameters.Add(new NpgsqlParameter { Value = now });
            headUpdate.Parameters.Add(new NpgsqlParameter { Value = sourceStateVersion });
            if (await headUpdate.ExecuteNonQueryAsync(cancellationToken) == 0)
                throw new InvalidOperationException("计算结果落库时提交头发生并发漂移。");
        }
        return ComputeApplyOutcome.Applied;
    }

    /// <summary>
    /// section 合批 upsert：unnest 数组单语句写入，ON CONFLICT 只接受更高版本；
    /// RETURNING 精确回传真实写入的 (player_id, section_kind) 集合，保持逐 section applied 判定语义。
    /// SELECT 内按 section_kind 固定行序，调用方必须先持提交头行锁（死锁约定）。
    /// </summary>
    private static async Task<HashSet<(Guid PlayerId, string SectionKind)>> UpsertSectionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<SectionWrite> writes,
        CancellationToken cancellationToken)
    {
        var count = writes.Count;
        var playerIds = new Guid[count];
        var sectionKinds = new string[count];
        var stateVersions = new long[count];
        var schemaVersions = new int[count];
        var codecs = new string[count];
        var payloads = new byte[count][];
        var rawBytes = new int[count];
        var storedBytes = new int[count];
        var checksums = new string[count];
        var updatedAts = new DateTimeOffset[count];
        for (var i = 0; i < count; i++)
        {
            var write = writes[i];
            playerIds[i] = write.PlayerId;
            sectionKinds[i] = write.SectionKind;
            stateVersions[i] = write.StateVersion;
            schemaVersions[i] = write.SchemaVersion;
            codecs[i] = write.Codec;
            payloads[i] = write.Payload.ToArray();
            rawBytes[i] = write.RawBytes;
            storedBytes[i] = write.Payload.Length;
            checksums[i] = write.Checksum;
            updatedAts[i] = write.UpdatedAt;
        }
        return await UpsertSectionsAsync(connection, transaction,
            playerIds, sectionKinds, stateVersions, schemaVersions, codecs, payloads,
            rawBytes, storedBytes, checksums, updatedAts, cancellationToken);
    }

    /// <inheritdoc cref="UpsertSectionsAsync(NpgsqlConnection, NpgsqlTransaction, IReadOnlyList{SectionWrite}, CancellationToken)"/>
    private static async Task<HashSet<(Guid PlayerId, string SectionKind)>> UpsertSectionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<FlushQueueItem> items,
        CancellationToken cancellationToken)
    {
        var count = items.Count;
        var playerIds = new Guid[count];
        var sectionKinds = new string[count];
        var stateVersions = new long[count];
        var schemaVersions = new int[count];
        var codecs = new string[count];
        var payloads = new byte[count][];
        var rawBytes = new int[count];
        var storedBytes = new int[count];
        var checksums = new string[count];
        var updatedAts = new DateTimeOffset[count];
        for (var i = 0; i < count; i++)
        {
            var item = items[i];
            playerIds[i] = item.PlayerId;
            sectionKinds[i] = item.SectionKind;
            stateVersions[i] = item.StateVersion;
            schemaVersions[i] = item.SchemaVersion;
            codecs[i] = item.Codec;
            payloads[i] = item.Payload;
            rawBytes[i] = item.RawBytes;
            storedBytes[i] = item.Payload.Length;
            checksums[i] = item.Checksum;
            updatedAts[i] = item.UpdatedAt;
        }
        return await UpsertSectionsAsync(connection, transaction,
            playerIds, sectionKinds, stateVersions, schemaVersions, codecs, payloads,
            rawBytes, storedBytes, checksums, updatedAts, cancellationToken);
    }

    private static async Task<HashSet<(Guid PlayerId, string SectionKind)>> UpsertSectionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] playerIds,
        string[] sectionKinds,
        long[] stateVersions,
        int[] schemaVersions,
        string[] codecs,
        byte[][] payloads,
        int[] rawBytes,
        int[] storedBytes,
        string[] checksums,
        DateTimeOffset[] updatedAts,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO game.player_state_sections
                (player_id, section_kind, section_version, schema_version, codec, payload,
                 raw_bytes, stored_bytes, checksum, updated_at)
            SELECT t.player_id, t.section_kind, t.section_version, t.schema_version, t.codec, t.payload,
                   t.raw_bytes, t.stored_bytes, t.checksum, t.updated_at
            FROM unnest($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                AS t(player_id, section_kind, section_version, schema_version, codec, payload,
                     raw_bytes, stored_bytes, checksum, updated_at)
            ORDER BY t.section_kind
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
            RETURNING player_id, section_kind
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = playerIds });
        command.Parameters.Add(new NpgsqlParameter { Value = sectionKinds });
        command.Parameters.Add(new NpgsqlParameter { Value = stateVersions });
        command.Parameters.Add(new NpgsqlParameter { Value = schemaVersions });
        command.Parameters.Add(new NpgsqlParameter { Value = codecs });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = payloads });
        command.Parameters.Add(new NpgsqlParameter { Value = rawBytes });
        command.Parameters.Add(new NpgsqlParameter { Value = storedBytes });
        command.Parameters.Add(new NpgsqlParameter { Value = checksums });
        command.Parameters.Add(new NpgsqlParameter { Value = updatedAts });
        var applied = new HashSet<(Guid, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied.Add((reader.GetGuid(0), reader.GetString(1)));
        }
        return applied;
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
        // 加锁顺序约定：任何玩家状态写者都必须先锁提交头再动 section 行。
        // ApplyComputeResultAsync 同样遵循该顺序，两条写路径不会互相死锁。
        await using (var headLock = new NpgsqlCommand("""
            SELECT 1 FROM game.player_state_heads WHERE player_id = $1 FOR UPDATE
            """, connection, transaction))
        {
            headLock.Parameters.Add(new NpgsqlParameter { Value = first.PlayerId });
            await headLock.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in items)
        {
            if (item.PlayerId != first.PlayerId || item.StateVersion != first.StateVersion)
                throw new InvalidDataException("section 批次必须属于同一玩家和状态版本。");
            ValidateQueueItem(item);
        }

        // 合批 upsert 单语句落库；RETURNING 精确给出真正写入的 (player, kind) 集合，
        // 保持「逐 section affected-rows → applied」判定语义不变。
        var appliedKeys = await UpsertSectionsAsync(connection, transaction, items, cancellationToken);
        var applied = new List<FlushQueueItem>(appliedKeys.Count);
        foreach (var item in items)
        {
            if (!appliedKeys.Contains((item.PlayerId, item.SectionKind))) continue;
            applied.Add(item);
            // 仅当这次 UPSERT 真正写入了更新版本时才重建关系型行动队列。
            // PostgreSQL 的 ON CONFLICT ... WHERE 对旧版本不回行，不能让旧载荷删除新队列。
            if (item.SectionKind == PlayerSectionKinds.ActionQueue)
            {
                await ApplyActionQueueAsync(connection, transaction, ToStoredSection(item), cancellationToken);
            }
        }

        var checkpoints = await ReadSectionChecksumsAsync(connection, transaction, first.PlayerId, cancellationToken);
        var hasCompleteState = true;
        foreach (var kind in PlayerSectionKinds.All)
        {
            var found = false;
            for (var j = 0; j < checkpoints.Count; j++)
            {
                if (checkpoints[j].SectionKind == kind)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                hasCompleteState = false;
                break;
            }
        }

        if (hasCompleteState)
        {
            var headChecksum = ComputeHeadChecksum(checkpoints, out var maxSectionVersion);

            var metadata = new List<FlushQueueItem>();
            string? firstFingerprint = null;
            DateTimeOffset? maxSimulatedAt = null;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.StateVersion == maxSectionVersion)
                {
                    metadata.Add(item);
                    if (!string.IsNullOrWhiteSpace(item.GameDataFingerprint))
                    {
                        if (firstFingerprint is null)
                        {
                            firstFingerprint = item.GameDataFingerprint;
                        }
                        else if (!string.Equals(firstFingerprint, item.GameDataFingerprint, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("同一玩家状态版本携带了冲突的游戏数据指纹。");
                        }
                    }
                    if (item.LastSimulatedAt is not null)
                    {
                        if (maxSimulatedAt is null || item.LastSimulatedAt.Value > maxSimulatedAt.Value)
                            maxSimulatedAt = item.LastSimulatedAt.Value;
                    }
                }
            }

            // 旧 worker 可能只提交了较低版本 section；没有当前最高版本的元数据时，
            // 保留数据库已有 head，避免把 last_simulated_at/指纹回拨。
            var existingHead = await ReadHeadMetadataAsync(connection, transaction, first.PlayerId, cancellationToken);
            var fingerprint = firstFingerprint ?? existingHead?.GameDataFingerprint;
            var lastSimulatedAt = maxSimulatedAt ?? existingHead?.LastSimulatedAt ?? first.UpdatedAt;
            if (string.IsNullOrWhiteSpace(fingerprint) && existingHead is null)
                throw new InvalidDataException("完整 section 缺少游戏数据指纹，无法建立提交头。");
            if (metadata.Count == 0 && existingHead is null)
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

        // 读模型投影与真源提交同事务顺带维护；不参与一致性判定，可随时从 section 重建。
        await UpsertProjectionAsync(connection, transaction, first.PlayerId,
            applied.Select(ToStoredSection).ToArray(), now, markActive: true, cancellationToken);

        // 已落盘队列行单语句删除；逐行 (kind, state_version, lease_token) 谓词语义不变。
        var deleteKinds = new string[items.Count];
        var deleteVersions = new long[items.Count];
        var deleteTokens = new Guid[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            deleteKinds[i] = items[i].SectionKind;
            deleteVersions[i] = items[i].StateVersion;
            deleteTokens[i] = items[i].LeaseToken;
        }
        await using (var delete = new NpgsqlCommand("""
            DELETE FROM platform.section_flush_queue AS q
            USING (SELECT * FROM unnest($1, $2, $3)) AS t(section_kind, state_version, lease_token)
            WHERE q.player_id = $4
              AND q.section_kind = t.section_kind
              AND q.state_version <= t.state_version
              AND q.lease_token = t.lease_token
            """, connection, transaction))
        {
            delete.Parameters.Add(new NpgsqlParameter { Value = deleteKinds });
            delete.Parameters.Add(new NpgsqlParameter { Value = deleteVersions });
            delete.Parameters.Add(new NpgsqlParameter { Value = deleteTokens });
            delete.Parameters.Add(new NpgsqlParameter { Value = first.PlayerId });
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void ValidateQueueItem(FlushQueueItem item)
    {
        item.Validate(requireLease: true);
        if (item.SchemaVersion != SectionCodec.CurrentSchemaVersion)
            throw new InvalidDataException("section 队列 schema 版本不受支持。");
    }

    /// <summary>section 行的轻量检查点，用于重算提交头校验和而不必取回完整载荷。</summary>
    private sealed record SectionCheckpoint(string SectionKind, long SectionVersion, string Checksum);

    /// <summary>既有提交头的元数据子集，用于保留 last_simulated_at/指纹不回拨。</summary>
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
        StoredSection stored,
        CancellationToken cancellationToken)
    {
        var actions = SectionCodec.Decode<List<QueuedActionState>>(stored);
        await using (var delete = new NpgsqlCommand("DELETE FROM game.player_action_queue WHERE player_id = $1", connection, transaction))
        {
            delete.Parameters.Add(new NpgsqlParameter { Value = stored.PlayerId });
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        if (actions.Count == 0) return;

        // 关系型行动队列整批重建：DELETE 之后一条 unnest INSERT 落齐全部槽位。
        var count = actions.Count;
        var slots = new short[count];
        var actionIds = new string[count];
        var actionPayloads = new byte[count][];
        var statuses = new string[count];
        var idempotencyKeys = new string[count];
        for (var slot = 0; slot < count; slot++)
        {
            var action = actions[slot];
            slots[slot] = (short)slot;
            actionIds[slot] = action.Intent.ActionId.Value;
            actionPayloads[slot] = JsonSerializer.SerializeToUtf8Bytes(action.Intent, ServerJson.Options);
            statuses[slot] = action.Status.ToString().ToLowerInvariant();
            idempotencyKeys[slot] = $"action:{action.Id:D}";
        }
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO game.player_action_queue
                (player_id, slot, action_id, action_payload, action_version, status, next_due_at, idempotency_key, updated_at)
            SELECT $1, t.slot, t.action_id, t.action_payload, $2, t.status, NULL, t.idempotency_key, $3
            FROM unnest($4, $5, $6, $7, $8)
                AS t(slot, action_id, action_payload, status, idempotency_key)
            """, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = stored.PlayerId });
            insert.Parameters.Add(new NpgsqlParameter { Value = stored.StateVersion });
            insert.Parameters.Add(new NpgsqlParameter { Value = stored.UpdatedAt });
            insert.Parameters.Add(new NpgsqlParameter { Value = slots });
            insert.Parameters.Add(new NpgsqlParameter { Value = actionIds });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bytea, Value = actionPayloads });
            insert.Parameters.Add(new NpgsqlParameter { Value = statuses });
            insert.Parameters.Add(new NpgsqlParameter { Value = idempotencyKeys });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static StoredSection ToStoredSection(FlushQueueItem item) =>
        new(item.PlayerId, item.SectionKind, item.StateVersion, item.SchemaVersion, item.Codec,
            item.Payload, item.RawBytes, item.Checksum, item.UpdatedAt);

    /// <summary>
    /// 由 section 检查点重建提交头校验和；格式必须与 SectionCodec.ComputeHeadChecksum 完全一致。
    /// </summary>
    private static string ComputeHeadChecksum(IReadOnlyList<SectionCheckpoint> checkpoints, out long maxSectionVersion)
    {
        var builder = new StringBuilder(checkpoints.Count * 96);
        maxSectionVersion = 0;
        for (var i = 0; i < checkpoints.Count; i++)
        {
            var cp = checkpoints[i];
            if (cp.SectionVersion > maxSectionVersion) maxSectionVersion = cp.SectionVersion;
            if (i > 0) builder.Append('|');
            builder.Append(cp.SectionKind).Append(':').Append(cp.SectionVersion).Append(':').Append(cp.Checksum);
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// 顺带维护 game.player_projection 读模型：只取本批真实写入的 section 中可投影的字段，
    /// 未涉及的字段保留既有值；markActive 时 last_active_at 随真源提交刷新
    /// （ComputeNode 冷挂机回写不算玩家活跃，不推进该字段，避免扭曲冷扫描窗口口径）。
    /// </summary>
    private static async Task UpsertProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playerId,
        IReadOnlyList<StoredSection> applied,
        DateTimeOffset now,
        bool markActive,
        CancellationToken cancellationToken)
    {
        double? realmRank = null;
        string? nodeId = null;
        string? presence = null;
        foreach (var item in applied)
        {
            switch (item.SectionKind)
            {
                case PlayerSectionKinds.Progression:
                    realmRank = ReadProjectionDouble(item, "realmLevel");
                    break;
                case PlayerSectionKinds.World:
                    nodeId = ReadProjectionString(item, "nodeId");
                    break;
                case PlayerSectionKinds.Settings:
                    presence = ReadProjectionPresence(item);
                    break;
            }
        }

        await using var command = new NpgsqlCommand("""
            INSERT INTO game.player_projection
                (player_id, realm_rank, current_node_id, presence, last_active_at, projection_version, updated_at)
            VALUES ($1, COALESCE($2, 0), $3, COALESCE($4, 'offline'), $5, 1, $5)
            ON CONFLICT (player_id) DO UPDATE SET
                realm_rank = COALESCE($2, game.player_projection.realm_rank),
                current_node_id = COALESCE($3, game.player_projection.current_node_id),
                presence = COALESCE($4, game.player_projection.presence),
                last_active_at = CASE WHEN $6 THEN $5 ELSE game.player_projection.last_active_at END,
                projection_version = game.player_projection.projection_version + 1,
                updated_at = $5
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = playerId });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)realmRank ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)nodeId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)presence ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        command.Parameters.Add(new NpgsqlParameter { Value = markActive });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static double? ReadProjectionDouble(StoredSection item, string property)
    {
        using var document = JsonDocument.Parse(SectionCodec.DecodeRaw(item.Codec, item.Payload));
        return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }

    private static string? ReadProjectionString(StoredSection item, string property)
    {
        using var document = JsonDocument.Parse(SectionCodec.DecodeRaw(item.Codec, item.Payload));
        return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ReadProjectionPresence(StoredSection item)
    {
        using var document = JsonDocument.Parse(SectionCodec.DecodeRaw(item.Codec, item.Payload));
        if (!document.RootElement.TryGetProperty("presence", out var value)) return null;
        // PresenceState 以数值枚举持久化：0=offline 1=online 2=suspended。
        var code = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : -1;
        return code switch
        {
            (int)PresenceState.Offline => "offline",
            (int)PresenceState.Online => "online",
            (int)PresenceState.Suspended => "suspended",
            _ => null
        };
    }

    /// <summary>整组释放 flush 队列租约：逐行 attempts+1 并按其旧值算指数退避，unnest 单语句合批。</summary>
    private static async Task ReleaseClaimsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<FlushQueueItem> items,
        string error,
        CancellationToken cancellationToken)
    {
        var playerIds = new Guid[items.Count];
        var sectionKinds = new string[items.Count];
        var leaseTokens = new Guid[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            playerIds[i] = items[i].PlayerId;
            sectionKinds[i] = items[i].SectionKind;
            leaseTokens[i] = items[i].LeaseToken;
        }
        await using var command = new NpgsqlCommand("""
            UPDATE platform.section_flush_queue AS q
            SET attempts = q.attempts + 1,
                next_attempt_at = now() + LEAST((2 ^ LEAST(q.attempts + 1, 8)) * interval '1 second', interval '5 minutes'),
                lease_until = NULL,
                lease_token = NULL,
                last_error = left($1, 512),
                updated_at = now()
            FROM (SELECT * FROM unnest($2, $3, $4)) AS t(player_id, section_kind, lease_token)
            WHERE q.player_id = t.player_id AND q.section_kind = t.section_kind AND q.lease_token = t.lease_token
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = error });
        command.Parameters.Add(new NpgsqlParameter { Value = playerIds });
        command.Parameters.Add(new NpgsqlParameter { Value = sectionKinds });
        command.Parameters.Add(new NpgsqlParameter { Value = leaseTokens });
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

    /// <summary>
    /// 刷盘主循环：按 SectionFlushInterval 轮询 durable 刷盘队列，把到期分段按批落库
    /// （认领/应用/失败计数见 Debug 日志）；循环异常经节流记录后 2 秒退避重试，停机令牌取消即退出。
    /// </summary>
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
