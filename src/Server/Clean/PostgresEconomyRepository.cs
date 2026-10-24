namespace Server.Clean;

/// <summary>资产余额和不可变流水的单提交点。所有改变资产的调用必须经过此服务。</summary>
public sealed class PostgresEconomyRepository(
    NpgsqlDataSource dataSource,
    ILogger<PostgresEconomyRepository> logger,
    Social.IParticipationAssetBoundary? participationAssets = null) : IEconomyTransactionStore
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

    /// <summary>
    /// 收集事务实际触碰的玩家（发起者、资产行、写回分段、回执与结算收件箱），
    /// 交由共享条带门串行化同玩家提交。
    /// </summary>
    private static int[] CollectGateStripes(EconomyTransactionRequest request)
    {
        var players = new List<Guid>(1 + request.Deltas.Count
            + (request.Sections?.Count ?? 0) + (request.Receipts?.Count ?? 0) + 1)
        {
            request.ActorId
        };
        foreach (var delta in request.Deltas) players.Add(delta.PlayerId);
        if (request.Sections is not null)
            foreach (var section in request.Sections) players.Add(section.PlayerId);
        if (request.Receipts is not null)
            foreach (var receipt in request.Receipts) players.Add(receipt.PlayerId);
        if (request.ParticipationReceipt is { } participation) players.Add(participation.PlayerId);
        return PlayerCommitGates.StripesFor(players);
    }

    /// <summary>
    /// 在单个 Serializable 事务内提交一笔资产变更：幂等键去重、按固定顺序对余额行加锁更新、写入不可变流水，
    /// 并将分段快照写回与命令回执同事务入队。幂等键已存在时校验请求指纹一致后直接返回首次提交结果。
    /// </summary>
    /// <param name="request">资产事务请求：事务 ID、幂等键、类型、发起者、变更行集合及随事务落盘的分段/回执。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>提交结果；<c>IdempotentReplay</c> 为 true 表示命中已有幂等记录、本次未重复生效。</returns>
    public async ValueTask<EconomyTransactionResult> CommitAsync(
        EconomyTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        // 缺省发生时间在重试前固定，保证同一事务重放时的审计时间不变。
        var stableRequest = request with { OccurredAt = request.OccurredAt ?? DateTimeOffset.UtcNow };
        // 会话门必须先于玩家提交门；持有至事务结局已确认，避免在线实耗与外部扣款双花。
        using var participationScope = participationAssets is not null && stableRequest.Deltas.Count > 0
            ? await participationAssets.AcquireAsync(stableRequest.Deltas.Select(delta =>
                new Social.ParticipationAssetKey(delta.PlayerId, delta.AssetId, delta.Variant)).ToArray(), cancellationToken)
            : null;
        var gateStripes = CollectGateStripes(stableRequest);
        await PlayerCommitGates.AcquireAllAsync(gateStripes, cancellationToken);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var result = await CommitOnceAsync(stableRequest, cancellationToken);
                    logger.LogDebug("资产事务提交 transactionId={TransactionId} lines={Lines} attempt={Attempt}",
                        stableRequest.TransactionId, stableRequest.Deltas.Count, attempt);
                    return result;
                }
                catch (PostgresException exception) when (IsSerializableConflict(exception)
                                                           && !cancellationToken.IsCancellationRequested)
                {
                    if (attempt >= SerializableRetryAttempts)
                        throw;

                    logger.LogWarning(exception,
                        "资产事务遇到 Serializable 瞬态冲突 transactionId={TransactionId} sqlState={SqlState} attempt={Attempt}/{MaxAttempts}，退避后重试",
                        stableRequest.TransactionId, exception.SqlState, attempt, SerializableRetryAttempts);
                    // 加少量线程安全抖动，避免同一批并发请求在固定退避点再次形成冲突波峰。
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 25));
                    await Task.Delay(SerializableRetryBackoff[attempt - 1] + jitter, cancellationToken);
                }
                catch (EconomyCommitOutcomeUnknownException unknown)
                {
                    // 不释放会话门或玩家门就精确重放确认，不能让后续实耗跨入未知资产结局。
                    try
                    {
                        using var confirmation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        return await CommitOnceAsync(stableRequest, confirmation.Token);
                    }
                    catch (Exception confirmationError)
                    {
                        unknown.Data["ConfirmationError"] = confirmationError;
                        throw unknown;
                    }
                }
            }
        }
        finally
        {
            PlayerCommitGates.ReleaseAll(gateStripes);
        }
    }

    private async ValueTask<EconomyTransactionResult> CommitOnceAsync(
        EconomyTransactionRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var result = await CommitCoreAsync(connection, transaction, request, cancellationToken);
        await CommitTransactionAsync(transaction, request.TransactionId, cancellationToken);
        return result;
    }

    internal sealed record CommittedEconomyRecord(
        Guid TransactionId,
        string IdempotencyKey,
        string RequestHash,
        string TransactionType,
        Guid ActorId,
        ReadOnlyMemory<byte> ResultPayload,
        DateTimeOffset CommittedAt);

    internal static async ValueTask<CommittedEconomyRecord?> TryLoadCommittedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT transaction_id, idempotency_key, request_hash, transaction_type, actor_id, result_payload, created_at
            FROM economy.transactions
            WHERE idempotency_key = $1
            FOR SHARE
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = idempotencyKey });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new CommittedEconomyRecord(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetGuid(4),
            reader.GetFieldValue<byte[]>(5), reader.GetFieldValue<DateTimeOffset>(6));
    }

    internal static async ValueTask CommitTransactionAsync(
        NpgsqlTransaction transaction, Guid transactionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { await transaction.CommitAsync(cancellationToken); }
        catch (PostgresException) { throw; }
        catch (Exception exception)
        {
            // COMMIT 已发送后异常不能证明服务端回滚；保留幂等键供请求重放查明事实。
            throw new EconomyCommitOutcomeUnknownException(transactionId, exception);
        }
    }

    internal static EconomyTransactionResult ValidateReplay(
        EconomyTransactionRequest request, CommittedEconomyRecord existing)
    {
        var deltas = request.Deltas.GroupBy(delta => (delta.PlayerId, delta.AssetId, delta.Variant))
            .Select(group => new AssetDelta(group.Key.PlayerId, group.Key.AssetId, group.Key.Variant, group.Sum(delta => delta.Delta))).ToArray();
        if (existing.ActorId != request.ActorId || existing.TransactionType != request.TransactionType
            || existing.RequestHash != ComputeRequestHash(request, deltas))
            throw new EconomyIdempotencyConflictException(request.IdempotencyKey);
        return new(existing.TransactionId, existing.IdempotencyKey, true, true, existing.CommittedAt, existing.ResultPayload);
    }

    private static bool IsSerializableConflict(PostgresException exception) =>
        exception.SqlState is "40001" or "40P01";

    /// <summary>
    /// 在调用方事务内执行经济事务落库：请求校验、幂等键去重、余额行按序 upsert、流水写入与随附写回。
    /// 不触碰事务边界（提交/回滚由调用方负责），供 <see cref="ComputeDispatcherWorker"/> 把
    /// 「节点资产增量 + section CAS + 任务完结」并入同一事务，保证三者同生同灭。
    /// 幂等命中时只读取既有事务结果并返回 <c>AlreadyCommitted</c>，调用方事务内其余写入仍正常提交。
    /// </summary>
    internal static async ValueTask<EconomyTransactionResult> CommitCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EconomyTransactionRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var occurredAt = request.OccurredAt ?? DateTimeOffset.UtcNow;
        var deltas = request.Deltas
            .GroupBy(x => (x.PlayerId, x.AssetId, x.Variant), x => x.Delta)
            .Select(g => new AssetDelta(g.Key.PlayerId, g.Key.AssetId, g.Key.Variant, g.Sum()))
            .ToArray();
        if (deltas.Any(delta => !double.IsFinite(delta.Delta) || delta.Delta == 0))
            throw new ArgumentException("合并后的资产变更量必须为有限非零 double。", nameof(request));
        var requestHash = ComputeRequestHash(request, deltas);

        if (request.PersistLedger)
        {
            var inserted = false;
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO economy.transactions
                    (transaction_id, idempotency_key, request_hash, transaction_type, actor_id, result_payload, created_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                ON CONFLICT (idempotency_key) DO NOTHING
                RETURNING transaction_id
                """, connection, transaction))
            {
                insert.Parameters.Add(new NpgsqlParameter { Value = request.TransactionId });
                insert.Parameters.Add(new NpgsqlParameter { Value = request.IdempotencyKey });
                insert.Parameters.Add(new NpgsqlParameter { Value = requestHash });
                insert.Parameters.Add(new NpgsqlParameter { Value = request.TransactionType });
                insert.Parameters.Add(new NpgsqlParameter { Value = request.ActorId });
                insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = request.ResultPayload.IsEmpty ? [] : request.ResultPayload.ToArray() });
                insert.Parameters.Add(new NpgsqlParameter { Value = occurredAt });
                inserted = await insert.ExecuteScalarAsync(cancellationToken) is Guid;
            }

            if (!inserted)
            {
                await using var existing = new NpgsqlCommand("""
                    SELECT transaction_id, request_hash, transaction_type, actor_id, result_payload, created_at
                    FROM economy.transactions
                    WHERE idempotency_key = $1
                    FOR SHARE
                    """, connection, transaction);
                existing.Parameters.Add(new NpgsqlParameter { Value = request.IdempotencyKey });
                Guid transactionId;
                byte[] payload;
                DateTimeOffset committedAt;
                await using (var reader = await existing.ExecuteReaderAsync(cancellationToken))
                {
                    if (!await reader.ReadAsync(cancellationToken))
                        throw new InvalidOperationException("幂等事务已存在但无法读取结果。");
                    transactionId = reader.GetGuid(0);
                    if (!string.Equals(reader.GetString(1), requestHash, StringComparison.Ordinal)
                        || !string.Equals(reader.GetString(2), request.TransactionType, StringComparison.Ordinal)
                        || reader.GetGuid(3) != request.ActorId)
                        throw new EconomyIdempotencyConflictException(request.IdempotencyKey);
                    payload = reader.GetFieldValue<byte[]>(4);
                    committedAt = reader.GetFieldValue<DateTimeOffset>(5);
                }
                // 幂等命中：不写任何行，结果随调用方事务边界一并提交。
                return new EconomyTransactionResult(transactionId, request.IdempotencyKey, true, true, committedAt, payload);
            }
        }

        if (request.ParticipationReceipt is { } participationReceipt)
            await LockParticipationInboxAsync(connection, transaction, participationReceipt, cancellationToken);

        // 商店配额先于余额行认领：超限直接抛错回滚，不会出现「扣了灵石才知限购已满」。
        if (request.ShopPurchase is { } shopClaim)
            await ClaimShopQuotaAsync(connection, transaction, shopClaim, occurredAt, cancellationToken);

        // 固定排序后逐行单语句 upsert：行锁按序获取，避免两个跨玩家交易以不同顺序锁余额而死锁。
        // 每行一次往返完成「存在则累加、不存在则插入」，RETURNING 顺路带回提交后余额与版本，
        // 供调用方直接同步内存钱包，替代旧的 FOR UPDATE 锁读 + upsert + 提交后整包重读。
        var ordered = deltas
            .OrderBy(x => x.PlayerId)
            .ThenBy(x => x.AssetId, StringComparer.Ordinal)
            .ThenBy(x => x.Variant, StringComparer.Ordinal)
            .ToArray();
        var committedBalances = new AssetBalanceRecord[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
            committedBalances[i] = await ApplyBalanceDeltaAsync(connection, transaction, ordered[i], occurredAt, cancellationToken);

        // 流水行仅在沉淀金融账本且存在增量时写入：unnest+ordinality 保证 line_no 与请求行序一致。
        if (request.PersistLedger && request.Deltas.Count > 0)
        {
            var linePlayers = new Guid[request.Deltas.Count];
            var lineAssets = new string[request.Deltas.Count];
            var lineVariants = new string[request.Deltas.Count];
            var lineAmounts = new double[request.Deltas.Count];
            for (var i = 0; i < request.Deltas.Count; i++)
            {
                var delta = request.Deltas[i];
                linePlayers[i] = delta.PlayerId;
                lineAssets[i] = delta.AssetId;
                lineVariants[i] = delta.Variant;
                lineAmounts[i] = delta.Delta;
            }
            await using (var lines = new NpgsqlCommand("""
                INSERT INTO economy.transaction_lines
                    (transaction_id, line_no, player_id, asset_id, variant, delta)
                SELECT $1, (line.ord - 1)::smallint, line.player_id, line.asset_id, line.variant, line.delta
                FROM unnest($2, $3, $4, $5) WITH ORDINALITY AS line(player_id, asset_id, variant, delta, ord)
                """, connection, transaction))
            {
                lines.Parameters.Add(new NpgsqlParameter { Value = request.TransactionId });
                lines.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, Value = linePlayers });
                lines.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Varchar, Value = lineAssets });
                lines.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Varchar, Value = lineVariants });
                lines.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Double, Value = lineAmounts });
                await lines.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await PostgresSectionRepository.EnqueueInTransactionAsync(connection, transaction,
            request.Sections ?? [], request.Receipts ?? [], cancellationToken);
        if (request.ParticipationReceipt is { } consumedReceipt)
        {
            await using var consume = new NpgsqlCommand("""
                UPDATE social.party_session_inbox SET consumed_at = $4, consumed_transaction_id = $5
                WHERE session_id = $1 AND player_id = $2 AND sequence = $3 AND consumed_at IS NULL
                """, connection, transaction);
            consume.Parameters.AddWithValue(consumedReceipt.SessionId);
            consume.Parameters.AddWithValue(consumedReceipt.PlayerId);
            consume.Parameters.AddWithValue(consumedReceipt.Sequence);
            consume.Parameters.AddWithValue(occurredAt);
            consume.Parameters.Add(new NpgsqlParameter { Value = request.PersistLedger ? request.TransactionId : DBNull.Value });
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("组队会话结算收件箱消费确认失败，拒绝提交成员状态与资产。");
        }
        return new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, occurredAt, request.ResultPayload, committedBalances);
    }

    /// <summary>
    /// 单语句认领系统商店当日配额：存在则累加、不存在则插入；累加后超过上限时
    /// ON CONFLICT 的 WHERE 守卫令语句静默跳过（不返回行），随后抛限购异常回滚整个事务。
    /// </summary>
    private static async ValueTask ClaimShopQuotaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ShopPurchaseClaim claim, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO economy.shop_daily_purchases
                (player_id, offer_id, day_index, quantity, updated_at)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (player_id, offer_id, day_index) DO UPDATE SET
                quantity = economy.shop_daily_purchases.quantity + EXCLUDED.quantity,
                updated_at = EXCLUDED.updated_at
            WHERE economy.shop_daily_purchases.quantity + EXCLUDED.quantity <= $6
            RETURNING quantity
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = claim.PlayerId });
        command.Parameters.Add(new NpgsqlParameter { Value = claim.OfferId });
        command.Parameters.Add(new NpgsqlParameter { Value = claim.DayIndex });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer, Value = claim.Quantity });
        command.Parameters.Add(new NpgsqlParameter { Value = occurredAt });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer, Value = claim.DailyLimit });
        if (await command.ExecuteScalarAsync(cancellationToken) is int) return;
        throw new ShopDailyLimitExceededException(claim.OfferId, claim.DayIndex, claim.DailyLimit);
    }

    /// <summary>锁定待消费结算，阻止不同经济幂等键重复确认同一成员份额。</summary>
    private static async ValueTask LockParticipationInboxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Social.ParticipationInboxReceipt receipt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT sequence FROM social.party_session_inbox
            WHERE session_id = $1 AND player_id = $2 AND sequence = $3 AND consumed_at IS NULL
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue(receipt.SessionId);
        command.Parameters.AddWithValue(receipt.PlayerId);
        command.Parameters.AddWithValue(receipt.Sequence);
        if (await command.ExecuteScalarAsync(cancellationToken) is not long)
            throw new InvalidOperationException("组队会话结算收件箱不存在或已消费。");
    }

    /// <summary>
    /// 单语句余额增减：存在则累加、不存在则按增量插入。结算结果越界（负余额/非有限）时
    /// 两侧 WHERE 令语句静默跳过——随后按当前余额抛出与旧实现一致的余额不足错误。
    /// RETURNING 带回落库后的 quantity/version；锁序与逐行处理顺序由调用方固定排序保证。
    /// </summary>
    private static async Task<AssetBalanceRecord> ApplyBalanceDeltaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AssetDelta delta,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        // CHECK 约束作用于「候选插入行」本身（先于冲突裁决），因此候选 quantity 不能直接放增量：
        // 行已存在时（EXISTS 放行）候选值必须为合法余额，此处取 0 占位——冲突后由 DO UPDATE
        // 计算真实结果，其 WHERE 守卫在被拒元组上不触发 CHECK（已验证行为）。
        await using var command = new NpgsqlCommand("""
            INSERT INTO economy.player_asset_balances
                (player_id, asset_id, variant, quantity, version, updated_at)
            SELECT $1, $2, $3,
                   CASE WHEN $4 >= 0 AND $4 < 'Infinity'::double precision THEN $4 ELSE 0::double precision END,
                   1, $5
            WHERE ($4 >= 0 AND $4 < 'Infinity'::double precision)
               OR EXISTS (
                   SELECT 1 FROM economy.player_asset_balances b
                   WHERE b.player_id = $1 AND b.asset_id = $2 AND b.variant = $3)
            ON CONFLICT (player_id, asset_id, variant) DO UPDATE SET
                quantity = economy.player_asset_balances.quantity + $4,
                version = economy.player_asset_balances.version + 1,
                updated_at = EXCLUDED.updated_at
            WHERE economy.player_asset_balances.quantity + $4 >= 0
              AND economy.player_asset_balances.quantity + $4 < 'Infinity'::double precision
            RETURNING quantity, version
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = delta.PlayerId });
        command.Parameters.Add(new NpgsqlParameter { Value = delta.AssetId });
        command.Parameters.Add(new NpgsqlParameter { Value = delta.Variant });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = delta.Delta });
        command.Parameters.Add(new NpgsqlParameter { Value = updatedAt });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            return new AssetBalanceRecord(delta.PlayerId, delta.AssetId, delta.Variant, reader.GetDouble(0), reader.GetInt64(1));
        await reader.DisposeAsync();
        // 新行值域守卫或既有行余额不足拒绝了写入：读取当前余额构造与旧实现一致的错误证据（事务随即回滚）。
        var available = await ReadBalanceAsync(connection, transaction, delta, cancellationToken);
        throw new EconomyInsufficientBalanceException(delta, available);
    }

    /// <summary>读取当前余额供余额不足错误取证；行不存在时返回 0（事务随后整体回滚，无需加锁）。</summary>
    private static async Task<double> ReadBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AssetDelta delta,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT quantity FROM economy.player_asset_balances
            WHERE player_id = $1 AND asset_id = $2 AND variant = $3
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = delta.PlayerId });
        command.Parameters.Add(new NpgsqlParameter { Value = delta.AssetId });
        command.Parameters.Add(new NpgsqlParameter { Value = delta.Variant });
        return await command.ExecuteScalarAsync(cancellationToken) is double quantity ? quantity : 0;
    }

    private static void Validate(EconomyTransactionRequest request)
    {
        if (request.TransactionId == Guid.Empty) throw new ArgumentException("TransactionId 不能为空。", nameof(request));
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("幂等键无效。", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TransactionType) || request.TransactionType.Length > 96)
            throw new ArgumentException("事务类型无效。", nameof(request));
        if (request.ActorId == Guid.Empty) throw new ArgumentException("ActorId 不能为空。", nameof(request));
        if (request.Deltas is null || request.Deltas.Count > 512
            || (request.Deltas.Count == 0 && (request.Sections is null || request.Sections.Count == 0)
                && (request.Receipts is null || request.Receipts.Count == 0) && request.ParticipationReceipt is null))
            throw new ArgumentException("资产事务必须至少包含一笔资产变更、分段写回或结算回执。", nameof(request));
        if (request.ParticipationReceipt is { } receipt
            && (string.IsNullOrWhiteSpace(receipt.SessionId) || receipt.SessionId.Length > 64
                || receipt.PlayerId != request.ActorId || receipt.Sequence < 1
                || request.Deltas.Any(delta => delta.PlayerId != receipt.PlayerId)
                || (request.Sections?.Any(section => section.PlayerId != receipt.PlayerId) ?? false)))
            throw new ArgumentException("组队会话结算只能确认本人收件箱并写入本人状态与资产。", nameof(request));
        if (request.ShopPurchase is { } shopPurchase
            && (shopPurchase.PlayerId != request.ActorId
                || string.IsNullOrWhiteSpace(shopPurchase.OfferId) || shopPurchase.OfferId.Length > 64
                || shopPurchase.DayIndex < 0 || shopPurchase.Quantity < 1
                || shopPurchase.DailyLimit < shopPurchase.Quantity
                || request.Deltas.Any(delta => delta.PlayerId != shopPurchase.PlayerId)))
            throw new ArgumentException("商店限购认领只能由本人发起，且必须与本人资产变更同事务。", nameof(request));
        if (request.ResultPayload.Length > 64 * 1024)
            throw new ArgumentException("资产事务结果载荷超过大小上限。", nameof(request));
        foreach (var delta in request.Deltas)
        {
            if (delta.PlayerId == Guid.Empty || string.IsNullOrWhiteSpace(delta.AssetId) || delta.AssetId.Length > 128 || delta.Variant is null || delta.Variant.Length > 128)
                throw new ArgumentException("资产变更标识无效。", nameof(request));
            if (double.IsNaN(delta.Delta) || double.IsInfinity(delta.Delta) || delta.Delta == 0)
                throw new ArgumentException("资产变更量必须为有限非零 double。", nameof(request));
        }
    }

    private static string ComputeRequestHash(EconomyTransactionRequest request, IReadOnlyCollection<AssetDelta> deltas)
    {
        var canonical = new StringBuilder()
            .Append(request.TransactionType).Append('\n')
            .Append(request.ActorId.ToString("D")).Append('\n')
            .Append(Convert.ToHexString(SHA256.HashData(request.ResultPayload.Span)).ToLowerInvariant()).Append('\n');
        foreach (var section in (request.Sections ?? []).OrderBy(section => section.SectionKind, StringComparer.Ordinal))
            canonical.Append(section.PlayerId).Append('|').Append(section.SectionKind).Append('|')
                .Append(section.StateVersion).Append('|').Append(section.Checksum).Append('\n');
        foreach (var receipt in (request.Receipts ?? []).OrderBy(receipt => receipt.PlayerId).ThenBy(receipt => receipt.RequestId))
            canonical.Append(receipt.PlayerId).Append('|').Append(receipt.RequestId).Append('|')
                .Append(receipt.Command).Append('|').Append(receipt.PayloadHash).Append('\n');
        if (request.ParticipationReceipt is { } participationReceipt)
            canonical.Append("participation|").Append(participationReceipt.SessionId).Append('|')
                .Append(participationReceipt.PlayerId).Append('|').Append(participationReceipt.Sequence).Append('\n');
        if (request.ShopPurchase is { } shopPurchase)
            canonical.Append("shop|").Append(shopPurchase.PlayerId.ToString("D")).Append('|')
                .Append(shopPurchase.OfferId).Append('|').Append(shopPurchase.DayIndex).Append('|')
                .Append(shopPurchase.Quantity).Append('|').Append(shopPurchase.DailyLimit).Append('\n');
        foreach (var delta in deltas
                     .OrderBy(value => value.PlayerId)
                     .ThenBy(value => value.AssetId, StringComparer.Ordinal)
                     .ThenBy(value => value.Variant, StringComparer.Ordinal))
        {
            canonical.Append(delta.PlayerId.ToString("D")).Append('|')
                .Append(delta.AssetId).Append('|')
                .Append(delta.Variant).Append('|')
                .Append(delta.Delta.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }
}

/// <summary>
/// 余额行锁内结算发现变更后数量为负或非有限值时抛出，携带引发失败的变更与当前可用余额供诊断。
/// </summary>
/// <param name="delta">触发余额不足的资产变更行。</param>
/// <param name="available">结算前该行余额数量。</param>
public sealed class EconomyInsufficientBalanceException(AssetDelta delta, double available)
    : InvalidOperationException($"资产余额不足 player={delta.PlayerId} asset={delta.AssetId} variant={delta.Variant} available={available} requestedDelta={delta.Delta}")
{
    /// <summary>触发失败的资产变更行。</summary>
    public AssetDelta Delta { get; } = delta;
    /// <summary>结算前该资产行的可用余额。</summary>
    public double Available { get; } = available;
}

/// <summary>
/// 幂等键已被请求指纹不同的历史事务占用时抛出，用于拦截同键异参的重放或并发冲突。
/// </summary>
/// <param name="idempotencyKey">发生冲突的幂等键。</param>
public sealed class EconomyIdempotencyConflictException(string idempotencyKey)
    : InvalidOperationException($"资产事务幂等键已绑定其他请求 idempotencyKey={idempotencyKey}");

/// <summary>
/// 系统商店每日限购配额不足时抛出；经济事务随即回滚，由命令层映射为限购提示。
/// </summary>
/// <param name="offerId">商品标识。</param>
/// <param name="dayIndex">UTC+8 自然日序号。</param>
/// <param name="dailyLimit">当日累计上限。</param>
public sealed class ShopDailyLimitExceededException(string offerId, long dayIndex, int dailyLimit)
    : InvalidOperationException($"商店商品 {offerId} 于自然日 {dayIndex} 已达每日限购上限 {dailyLimit} 件")
{
    /// <summary>触发限购的商品标识。</summary>
    public string OfferId { get; } = offerId;
    /// <summary>发生限购的自然日序号。</summary>
    public long DayIndex { get; } = dayIndex;
    /// <summary>该商品的当日累计上限。</summary>
    public int DailyLimit { get; } = dailyLimit;
}

/// <summary>
/// 跨仓储共享的按玩家提交闸：Serializable 写事务在进程内按玩家条带串行化，
/// 同玩家并发提交从「SSI 回退 + 整事务重放」变为排队等待，开销低一个量级。
/// 市场仓储写路径复用同一闸门，保证跨仓储的同玩家写入同样排队。
/// 桶数固定为 2 的幂，误共享只造成轻微过度串行、不影响正确性。
/// </summary>
internal static class PlayerCommitGates
{
    private const int Stripes = 128;
    private static readonly SemaphoreSlim[] Gates = CreateGates();

    private static SemaphoreSlim[] CreateGates()
    {
        var gates = new SemaphoreSlim[Stripes];
        for (var i = 0; i < gates.Length; i++) gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    /// <summary>把一组玩家标识映射为去重且升序的条带序号；序号排序保证多方加锁无环。</summary>
    public static int[] StripesFor(IEnumerable<Guid> playerIds)
    {
        var set = new HashSet<int>();
        foreach (var playerId in playerIds) set.Add(playerId.GetHashCode() & (Stripes - 1));
        var ordered = set.ToArray();
        Array.Sort(ordered);
        return ordered;
    }

    public static async ValueTask AcquireAllAsync(int[] stripes, CancellationToken cancellationToken)
    {
        foreach (var stripe in stripes)
            await Gates[stripe].WaitAsync(cancellationToken);
    }

    public static void ReleaseAll(int[] stripes)
    {
        foreach (var stripe in stripes)
            Gates[stripe].Release();
    }
}
