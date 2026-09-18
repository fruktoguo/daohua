namespace Server.Clean;

/// <summary>资产余额和不可变流水的单提交点。所有改变资产的调用必须经过此服务。</summary>
public sealed class PostgresEconomyRepository(
    NpgsqlDataSource dataSource,
    ILogger<PostgresEconomyRepository> logger) : IEconomyTransactionStore
{
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
        Validate(request);
        var occurredAt = request.OccurredAt ?? DateTimeOffset.UtcNow;
        var deltas = request.Deltas
            .GroupBy(x => (x.PlayerId, x.AssetId, x.Variant), x => x.Delta)
            .Select(g => new AssetDelta(g.Key.PlayerId, g.Key.AssetId, g.Key.Variant, g.Sum()))
            .ToArray();
        if (deltas.Any(delta => !double.IsFinite(delta.Delta) || delta.Delta == 0))
            throw new ArgumentException("合并后的资产变更量必须为有限非零 double。", nameof(request));
        var requestHash = ComputeRequestHash(request, deltas);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);

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
            await transaction.CommitAsync(cancellationToken);
            return new EconomyTransactionResult(transactionId, request.IdempotencyKey, true, true, committedAt, payload);
        }

        // 固定排序后再加行锁，避免两个跨玩家交易以不同顺序锁定余额而死锁。
        foreach (var delta in deltas.OrderBy(x => x.PlayerId).ThenBy(x => x.AssetId, StringComparer.Ordinal).ThenBy(x => x.Variant, StringComparer.Ordinal))
        {
            var current = await ReadBalanceForUpdateAsync(connection, transaction, delta, cancellationToken);
            var next = current.quantity + delta.Delta;
            if (double.IsNaN(next) || double.IsInfinity(next) || next < 0)
                throw new EconomyInsufficientBalanceException(delta, current.quantity);
            await UpsertBalanceAsync(connection, transaction, delta, next, current.version + 1, occurredAt, cancellationToken);
        }

        for (var lineNo = 0; lineNo < request.Deltas.Count; lineNo++)
        {
            var delta = request.Deltas[lineNo];
            await using var line = new NpgsqlCommand("""
                INSERT INTO economy.transaction_lines
                    (transaction_id, line_no, player_id, asset_id, variant, delta)
                VALUES ($1, $2, $3, $4, $5, $6)
                """, connection, transaction);
            line.Parameters.Add(new NpgsqlParameter { Value = request.TransactionId });
            line.Parameters.Add(new NpgsqlParameter { Value = lineNo });
            line.Parameters.Add(new NpgsqlParameter { Value = delta.PlayerId });
            line.Parameters.Add(new NpgsqlParameter { Value = delta.AssetId });
            line.Parameters.Add(new NpgsqlParameter { Value = delta.Variant });
            line.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = delta.Delta });
            await line.ExecuteNonQueryAsync(cancellationToken);
        }

        await PostgresSectionRepository.EnqueueInTransactionAsync(connection, transaction,
            request.Sections ?? [], request.Receipts ?? [], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogDebug("资产事务提交 transactionId={TransactionId} lines={Lines}", request.TransactionId, request.Deltas.Count);
        return new EconomyTransactionResult(request.TransactionId, request.IdempotencyKey, true, false, occurredAt, request.ResultPayload);
    }

    private static async Task<(double quantity, long version)> ReadBalanceForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AssetDelta delta,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT quantity, version
            FROM economy.player_asset_balances
            WHERE player_id = $1 AND asset_id = $2 AND variant = $3
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = delta.PlayerId });
        command.Parameters.Add(new NpgsqlParameter { Value = delta.AssetId });
        command.Parameters.Add(new NpgsqlParameter { Value = delta.Variant });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (0, 0);
        return (reader.GetDouble(0), reader.GetInt64(1));
    }

    private static async Task UpsertBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AssetDelta delta,
        double quantity,
        long version,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO economy.player_asset_balances
                (player_id, asset_id, variant, quantity, version, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (player_id, asset_id, variant) DO UPDATE SET
                quantity = EXCLUDED.quantity,
                version = EXCLUDED.version,
                updated_at = EXCLUDED.updated_at
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = delta.PlayerId });
        command.Parameters.Add(new NpgsqlParameter { Value = delta.AssetId });
        command.Parameters.Add(new NpgsqlParameter { Value = delta.Variant });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Double, Value = quantity });
        command.Parameters.Add(new NpgsqlParameter { Value = version });
        command.Parameters.Add(new NpgsqlParameter { Value = updatedAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Validate(EconomyTransactionRequest request)
    {
        if (request.TransactionId == Guid.Empty) throw new ArgumentException("TransactionId 不能为空。", nameof(request));
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("幂等键无效。", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TransactionType) || request.TransactionType.Length > 96)
            throw new ArgumentException("事务类型无效。", nameof(request));
        if (request.ActorId == Guid.Empty) throw new ArgumentException("ActorId 不能为空。", nameof(request));
        if (request.Deltas is null || request.Deltas.Count == 0 || request.Deltas.Count > 512)
            throw new ArgumentException("资产事务行数必须在 1..512。", nameof(request));
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
