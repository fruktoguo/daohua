using System.Data;
using System.Text.Json;
using Domain.Economy;
using Domain.Player;
using Npgsql;
using NpgsqlTypes;
using Protocol;
using Server.Clean.Social;

namespace Server.Clean.Redeem;

/// <summary>
/// 兑换码 PostgreSQL 持久化仓储（总纲 §29）：
/// - 支持通用口令码（universal）与一码一用批次码（single_use）统一主键检索；
/// - 核销沿用 <see cref="IParticipationAssetBoundary"/> → <see cref="PlayerCommitGates"/> 外层门与 Serializable 事务；
/// - 资产入账走 <see cref="PostgresEconomyRepository.CommitCoreAsync"/>（<c>ops.redeem</c> 白名单流水保留 30 天），
///   玩家已兑防重记录 <c>economy.player_redeem_claims</c> 永久留存。
/// </summary>
public sealed class PostgresRedeemRepository(
    NpgsqlDataSource dataSource,
    IParticipationAssetBoundary? participationAssets = null)
{
    private const int SerializableRetryAttempts = 10;
    private const int OpsBatchListLimit = 100;
    private const int OpsCodeListMaxLimit = 5000;

    private static readonly TimeSpan[] SerializableRetryBackoff =
    [
        TimeSpan.FromMilliseconds(15),
        TimeSpan.FromMilliseconds(35),
        TimeSpan.FromMilliseconds(75),
        TimeSpan.FromMilliseconds(140),
        TimeSpan.FromMilliseconds(220),
        TimeSpan.FromMilliseconds(320),
        TimeSpan.FromMilliseconds(450),
        TimeSpan.FromMilliseconds(600),
        TimeSpan.FromMilliseconds(800)
    ];

    /// <summary>玩家核销兑换码并同事务直接入账背包。</summary>
    public async ValueTask<RedeemCodeResponseDto> RedeemAsync(
        Guid playerId,
        string normalizedCode,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        now = now.ToUniversalTime();

        IReadOnlyList<RedeemRewardDto> previewRewards;
        await using (var preConn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            // 预检合并为单命令双结果集一次往返：先验机器人，再取批次元数据与同批已兑判定。
            // 预检在 Serializable 事务外执行，不产生谓词锁；事务内仍会完整复核全部条件。
            string mode;
            string rewardsJson;
            int? maxClaims;
            int claimedCount;
            DateTimeOffset? expiresAt;
            DateTimeOffset? disabledAt;
            Guid? redeemedBy;
            bool alreadyClaimed;

            await using (var preBatch = new NpgsqlBatch(preConn))
            {
                var botCmd = new NpgsqlBatchCommand("""
                    SELECT a.is_bot
                    FROM identity.players p
                    JOIN identity.accounts a ON a.account_id = p.account_id
                    WHERE p.player_id = $1 AND a.status = 1
                    LIMIT 1
                    """);
                botCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
                preBatch.BatchCommands.Add(botCmd);

                var codeCmd = new NpgsqlBatchCommand("""
                    SELECT b.batch_id, b.mode, b.rewards::text, b.max_claims, b.claimed_count, b.expires_at, b.disabled_at, c.redeemed_by,
                           EXISTS(
                               SELECT 1 FROM economy.player_redeem_claims prc
                               WHERE prc.player_id = $1 AND prc.batch_id = c.batch_id
                           ) AS already_claimed
                    FROM economy.redeem_codes c
                    JOIN economy.redeem_batches b ON b.batch_id = c.batch_id
                    WHERE c.code_normalized = $2
                    LIMIT 1
                    """);
                codeCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
                codeCmd.Parameters.Add(new NpgsqlParameter { Value = normalizedCode });
                preBatch.BatchCommands.Add(codeCmd);

                await using var reader = await preBatch.ExecuteReaderAsync(ct).ConfigureAwait(false);

                var isBot = await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(0)
                    ? reader.GetBoolean(0)
                    : (bool?)null;
                if (isBot is null)
                    throw new RedeemOperationException(RedeemOperationFailure.PlayerNotFound, "角色不存在或已停用。");
                if (isBot.Value)
                    throw new RedeemOperationException(RedeemOperationFailure.BotRestricted, "系统或机器人账号不支持使用兑换码。");

                if (!await reader.NextResultAsync(ct).ConfigureAwait(false)
                    || !await reader.ReadAsync(ct).ConfigureAwait(false))
                    throw new RedeemOperationException(RedeemOperationFailure.NotFound, "兑换码不存在，请核对后重试。");

                mode = reader.GetString(1);
                rewardsJson = reader.GetString(2);
                maxClaims = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                claimedCount = reader.GetInt32(4);
                expiresAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5);
                disabledAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6);
                redeemedBy = reader.IsDBNull(7) ? null : reader.GetGuid(7);
                alreadyClaimed = reader.GetBoolean(8);
            }

            if (disabledAt is not null)
                throw new RedeemOperationException(RedeemOperationFailure.Disabled, "该兑换码已停用。");
            if (expiresAt is { } exp && exp <= now)
                throw new RedeemOperationException(RedeemOperationFailure.Expired, "该兑换码已过期。");

            if (alreadyClaimed)
                throw new RedeemOperationException(RedeemOperationFailure.AlreadyClaimedBatch, "你已领取过该礼包，不可重复兑换。");

            if (mode == RedeemCodeRules.ModeSingleUse && redeemedBy is not null)
                throw new RedeemOperationException(RedeemOperationFailure.CodeAlreadyUsed, "该兑换码已被使用。");

            if (maxClaims is { } cap && claimedCount >= cap)
                throw new RedeemOperationException(RedeemOperationFailure.Exhausted, "该兑换码名额已领完。");

            previewRewards = DeserializeRewards(rewardsJson);
            if (previewRewards.Count == 0)
                throw new RedeemOperationException(RedeemOperationFailure.Disabled, "该兑换码未配置有效奖励。");
        }

        var assetKeys = previewRewards
            .Select(r => new ParticipationAssetKey(
                playerId,
                r.ItemId,
                EnhancementRules.VariantForLevel(r.EnhancementLevel)))
            .Distinct()
            .ToArray();

        using var participationScope = participationAssets is not null && assetKeys.Length > 0
            ? await participationAssets.AcquireAsync(assetKeys, ct).ConfigureAwait(false)
            : null;
        var stripes = PlayerCommitGates.StripesFor([playerId]);
        await PlayerCommitGates.AcquireAllAsync(stripes, ct).ConfigureAwait(false);
        try
        {
            return await ExecuteRedeemTransactionWithRetryAsync(playerId, normalizedCode, now, ct).ConfigureAwait(false);
        }
        finally
        {
            PlayerCommitGates.ReleaseAll(stripes);
        }
    }

    private async ValueTask<RedeemCodeResponseDto> ExecuteRedeemTransactionWithRetryAsync(
        Guid playerId,
        string normalizedCode,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var txId = Guid.NewGuid();
        EconomyCommitOutcomeUnknownException? unknown = null;

        for (var attempt = 1; ; attempt++)
        {
            using var confirmation = unknown is null ? null : new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var token = confirmation?.Token ?? ct;
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
                await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);

                // 1. 先按码定位 batch_id，再在加锁前确认是否 UNKNOWN 重放或个人已领取，最后按固定锁序加锁，消除热点排队。
                long batchId;
                await using (var findBatchCmd = new NpgsqlCommand("""
                    SELECT batch_id
                    FROM economy.redeem_codes
                    WHERE code_normalized = $1
                    """, connection, tx))
                {
                    findBatchCmd.Parameters.Add(new NpgsqlParameter { Value = normalizedCode });
                    var scalar = await findBatchCmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (scalar is not long foundId)
                        throw new RedeemOperationException(RedeemOperationFailure.NotFound, "兑换码不存在，请核对后重试。");
                    batchId = foundId;
                }

                // 若前一次 COMMIT 网络结果未知，核对当前防重记录以安全重放确认。
                if (unknown is not null)
                {
                    var alreadyCommittedForCode = await IsCodeClaimedByPlayerAsync(connection, tx, playerId, batchId, normalizedCode, token).ConfigureAwait(false);
                    if (alreadyCommittedForCode)
                    {
                        await using var replayBatchCmd = new NpgsqlCommand("""
                            SELECT title, rewards::text
                            FROM economy.redeem_batches
                            WHERE batch_id = $1
                            """, connection, tx);
                        replayBatchCmd.Parameters.Add(new NpgsqlParameter { Value = batchId });
                        await using var replayReader = await replayBatchCmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                        if (await replayReader.ReadAsync(token).ConfigureAwait(false))
                        {
                            var replayTitle = replayReader.GetString(0);
                            var replayRewards = DeserializeRewards(replayReader.GetString(1));
                            await replayReader.CloseAsync().ConfigureAwait(false);
                            await tx.RollbackAsync(token).ConfigureAwait(false);
                            return new RedeemCodeResponseDto(batchId, replayTitle, normalizedCode, replayRewards);
                        }
                    }
                }

                if (await HasPlayerClaimedBatchAsync(connection, tx, playerId, batchId, token).ConfigureAwait(false))
                    throw new RedeemOperationException(RedeemOperationFailure.AlreadyClaimedBatch, "你已领取过该礼包，不可重复兑换。");

                string title;
                string mode;
                string rewardsJson;
                int? maxClaims;
                int claimedCount;
                DateTimeOffset? expiresAt;
                DateTimeOffset? disabledAt;

                // 批次行改 FOR KEY SHARE：claimed_count 上限由下方守卫 UPDATE 权威裁决，
                // 读路径不再互斥排队，削减同码并发核销的行锁占用时长与 40001 冲突放大。
                await using (var lockBatchCmd = new NpgsqlCommand("""
                    SELECT title, mode, rewards::text, max_claims, claimed_count, expires_at, disabled_at
                    FROM economy.redeem_batches
                    WHERE batch_id = $1
                    FOR KEY SHARE
                    """, connection, tx))
                {
                    lockBatchCmd.Parameters.Add(new NpgsqlParameter { Value = batchId });
                    await using var reader = await lockBatchCmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                        throw new RedeemOperationException(RedeemOperationFailure.NotFound, "兑换码不存在，请核对后重试。");

                    title = reader.GetString(0);
                    mode = reader.GetString(1);
                    rewardsJson = reader.GetString(2);
                    maxClaims = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                    claimedCount = reader.GetInt32(4);
                    expiresAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5);
                    disabledAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6);
                }

                // 通用口令码全服共享同一码行且 redeemed_by 恒 NULL 永不回写，跳过码行排他锁消除热点；
                // 单次码仍锁码行读取 redeemed_by，保证同码并发核销先到先得。
                Guid? redeemedBy = null;
                if (mode == RedeemCodeRules.ModeSingleUse)
                {
                    await using var lockCodeCmd = new NpgsqlCommand("""
                        SELECT redeemed_by
                        FROM economy.redeem_codes
                        WHERE code_normalized = $1
                        FOR UPDATE
                        """, connection, tx);
                    lockCodeCmd.Parameters.Add(new NpgsqlParameter { Value = normalizedCode });
                    await using var reader = await lockCodeCmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                        throw new RedeemOperationException(RedeemOperationFailure.NotFound, "兑换码不存在，请核对后重试。");
                    redeemedBy = reader.IsDBNull(0) ? null : reader.GetGuid(0);
                }

                var rewards = DeserializeRewards(rewardsJson);
                var idempotencyKey = $"ops.redeem:{playerId:D}:{batchId}";

                if (disabledAt is not null)
                    throw new RedeemOperationException(RedeemOperationFailure.Disabled, "该兑换码已停用。");
                if (expiresAt is { } exp && exp <= now)
                    throw new RedeemOperationException(RedeemOperationFailure.Expired, "该兑换码已过期。");

                if (mode == RedeemCodeRules.ModeSingleUse && redeemedBy is not null)
                    throw new RedeemOperationException(RedeemOperationFailure.CodeAlreadyUsed, "该兑换码已被使用。");

                if (maxClaims is { } cap && claimedCount >= cap)
                    throw new RedeemOperationException(RedeemOperationFailure.Exhausted, "该兑换码名额已领完。");

                if (mode == RedeemCodeRules.ModeSingleUse)
                {
                    await using var markCodeCmd = new NpgsqlCommand("""
                        UPDATE economy.redeem_codes
                           SET redeemed_by = $1, redeemed_at = $2
                         WHERE code_normalized = $3 AND redeemed_by IS NULL
                        """, connection, tx);
                    markCodeCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
                    markCodeCmd.Parameters.Add(new NpgsqlParameter { Value = now });
                    markCodeCmd.Parameters.Add(new NpgsqlParameter { Value = normalizedCode });
                    var updatedCodeRows = await markCodeCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    if (updatedCodeRows == 0)
                        throw new RedeemOperationException(RedeemOperationFailure.CodeAlreadyUsed, "该兑换码已被使用。");
                }

                await using (var updateBatchCmd = new NpgsqlCommand("""
                    UPDATE economy.redeem_batches
                       SET claimed_count = claimed_count + 1
                     WHERE batch_id = $1
                       AND disabled_at IS NULL
                       AND (expires_at IS NULL OR expires_at > $2)
                       AND (max_claims IS NULL OR claimed_count < max_claims)
                    """, connection, tx))
                {
                    updateBatchCmd.Parameters.Add(new NpgsqlParameter { Value = batchId });
                    updateBatchCmd.Parameters.Add(new NpgsqlParameter { Value = now });
                    var updatedBatchRows = await updateBatchCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    if (updatedBatchRows == 0)
                        throw new RedeemOperationException(RedeemOperationFailure.Exhausted, "该兑换码名额已领完或已失效。");
                }

                await using (var insertClaimCmd = new NpgsqlCommand("""
                    INSERT INTO economy.player_redeem_claims (player_id, batch_id, code_normalized, claimed_at)
                    VALUES ($1, $2, $3, $4)
                    """, connection, tx))
                {
                    insertClaimCmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
                    insertClaimCmd.Parameters.Add(new NpgsqlParameter { Value = batchId });
                    insertClaimCmd.Parameters.Add(new NpgsqlParameter { Value = normalizedCode });
                    insertClaimCmd.Parameters.Add(new NpgsqlParameter { Value = now });
                    await insertClaimCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                var deltas = rewards
                    .Select(r => new AssetDelta(
                        playerId,
                        r.ItemId,
                        EnhancementRules.VariantForLevel(r.EnhancementLevel),
                        r.Quantity))
                    .ToArray();
                var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
                    new { batchId, code = normalizedCode },
                    ProtocolJson.Options);

                var ecoRequest = new EconomyTransactionRequest(
                    txId,
                    idempotencyKey,
                    RedeemCodeRules.LedgerTransactionType,
                    playerId,
                    deltas,
                    now,
                    payloadBytes,
                    PersistLedger: true);
                await PostgresEconomyRepository.CommitCoreAsync(connection, tx, ecoRequest, token).ConfigureAwait(false);
                await PostgresEconomyRepository.CommitTransactionAsync(tx, txId, token).ConfigureAwait(false);

                return new RedeemCodeResponseDto(batchId, title, normalizedCode, rewards);
            }
            catch (PostgresException ex) when (ex.SqlState is "40001" or "40P01" && attempt < SerializableRetryAttempts && unknown is null && !ct.IsCancellationRequested)
            {
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 25));
                await Task.Delay(SerializableRetryBackoff[attempt - 1] + jitter, ct).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505" && unknown is null)
            {
                throw new RedeemOperationException(RedeemOperationFailure.AlreadyClaimedBatch, "你已领取过该礼包，不可重复兑换。");
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

    /// <summary>运营端创建通用口令码批次（唯一口令已存在时抛 CodeConflict）。</summary>
    public async ValueTask<OpsCreateRedeemBatchResponse> CreateUniversalBatchAsync(
        string normalizedCode,
        string title,
        IReadOnlyList<RedeemRewardDto> rewards,
        int? maxClaims,
        DateTimeOffset now,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        now = now.ToUniversalTime();
        var rewardsJson = SerializeRewards(rewards);

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var existsCmd = new NpgsqlCommand("SELECT 1 FROM economy.redeem_codes WHERE code_normalized = $1", connection, tx))
        {
            existsCmd.Parameters.Add(new NpgsqlParameter { Value = normalizedCode });
            if (await existsCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
                throw new RedeemOperationException(RedeemOperationFailure.CodeConflict, $"口令「{normalizedCode}」已被占用，请更换口令。");
        }

        long batchId;
        await using (var batchCmd = new NpgsqlCommand("""
            INSERT INTO economy.redeem_batches (title, mode, rewards, max_claims, claimed_count, created_at, expires_at, disabled_at)
            VALUES ($1, $2, $3::jsonb, $4, 0, $5, $6, NULL)
            RETURNING batch_id
            """, connection, tx))
        {
            batchCmd.Parameters.Add(new NpgsqlParameter { Value = title });
            batchCmd.Parameters.Add(new NpgsqlParameter { Value = RedeemCodeRules.ModeUniversal });
            batchCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = rewardsJson });
            batchCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)maxClaims ?? DBNull.Value });
            batchCmd.Parameters.Add(new NpgsqlParameter { Value = now });
            batchCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)expiresAt ?? DBNull.Value });
            batchId = (long)(await batchCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        try
        {
            await using var codeCmd = new NpgsqlCommand("""
                INSERT INTO economy.redeem_codes (code_normalized, batch_id, redeemed_by, redeemed_at)
                VALUES ($1, $2, NULL, NULL)
                """, connection, tx);
            codeCmd.Parameters.Add(new NpgsqlParameter { Value = normalizedCode });
            codeCmd.Parameters.Add(new NpgsqlParameter { Value = batchId });
            await codeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            throw new RedeemOperationException(RedeemOperationFailure.CodeConflict, $"口令「{normalizedCode}」已被占用，请更换口令。");
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        var dto = new OpsRedeemBatchDto(
            batchId,
            title,
            RedeemCodeRules.ModeUniversal,
            normalizedCode,
            rewards,
            maxClaims,
            ClaimedCount: 0,
            CreatedAt: now,
            ExpiresAt: expiresAt,
            DisabledAt: null,
            IsActive: expiresAt is null || expiresAt > now);
        return new OpsCreateRedeemBatchResponse(dto, [normalizedCode]);
    }

    /// <summary>运营端创建单次码批次并批量写入密码学随机唯一码。</summary>
    public async ValueTask<OpsCreateRedeemBatchResponse> CreateSingleUseBatchAsync(
        int count,
        string normalizedPrefix,
        string title,
        IReadOnlyList<RedeemRewardDto> rewards,
        DateTimeOffset now,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        now = now.ToUniversalTime();
        var rewardsJson = SerializeRewards(rewards);

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        long batchId;
        await using (var batchCmd = new NpgsqlCommand("""
            INSERT INTO economy.redeem_batches (title, mode, rewards, max_claims, claimed_count, created_at, expires_at, disabled_at)
            VALUES ($1, $2, $3::jsonb, $4, 0, $5, $6, NULL)
            RETURNING batch_id
            """, connection, tx))
        {
            batchCmd.Parameters.Add(new NpgsqlParameter { Value = title });
            batchCmd.Parameters.Add(new NpgsqlParameter { Value = RedeemCodeRules.ModeSingleUse });
            batchCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = rewardsJson });
            batchCmd.Parameters.Add(new NpgsqlParameter { Value = count });
            batchCmd.Parameters.Add(new NpgsqlParameter { Value = now });
            batchCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)expiresAt ?? DBNull.Value });
            batchId = (long)(await batchCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        // 批量生成候选唯一码并利用 ON CONFLICT DO NOTHING 去重补足，避免逐条往返。
        var insertedCodes = new List<string>(count);
        var localSeen = new HashSet<string>(count * 2, StringComparer.Ordinal);
        var safetyRounds = 0;

        while (insertedCodes.Count < count)
        {
            if (++safetyRounds > 12)
                throw new RedeemOperationException(RedeemOperationFailure.CodeConflict, "随机兑换码空间碰撞过多，请更换前缀后重试。");

            var needed = count - insertedCodes.Count;
            var candidateBatch = new List<string>(needed);
            while (candidateBatch.Count < needed)
            {
                var candidate = RedeemCodeRules.GenerateRandomCode(normalizedPrefix);
                if (localSeen.Add(candidate))
                    candidateBatch.Add(candidate);
            }

            await using var insertCmd = new NpgsqlCommand("""
                INSERT INTO economy.redeem_codes (code_normalized, batch_id, redeemed_by, redeemed_at)
                SELECT code_val, $1, NULL, NULL
                FROM unnest($2::varchar[]) AS code_val
                ON CONFLICT (code_normalized) DO NOTHING
                RETURNING code_normalized
                """, connection, tx);
            insertCmd.Parameters.Add(new NpgsqlParameter { Value = batchId });
            insertCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Varchar, Value = candidateBatch.ToArray() });
            await using var reader = await insertCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                insertedCodes.Add(reader.GetString(0));
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        var dto = new OpsRedeemBatchDto(
            batchId,
            title,
            RedeemCodeRules.ModeSingleUse,
            UniversalCode: null,
            rewards,
            MaxClaims: count,
            ClaimedCount: 0,
            CreatedAt: now,
            ExpiresAt: expiresAt,
            DisabledAt: null,
            IsActive: expiresAt is null || expiresAt > now);
        return new OpsCreateRedeemBatchResponse(dto, insertedCodes);
    }

    /// <summary>运营端查询最近的兑换码批次列表。</summary>
    public async ValueTask<IReadOnlyList<OpsRedeemBatchDto>> ListBatchesAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        now = now.ToUniversalTime();
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT b.batch_id,
                   b.title,
                   b.mode,
                   CASE WHEN b.mode = 'universal' THEN (
                       SELECT c.code_normalized FROM economy.redeem_codes c WHERE c.batch_id = b.batch_id LIMIT 1
                   ) ELSE NULL END AS universal_code,
                   b.rewards::text,
                   b.max_claims,
                   b.claimed_count,
                   b.created_at,
                   b.expires_at,
                   b.disabled_at
            FROM economy.redeem_batches b
            ORDER BY b.created_at DESC, b.batch_id DESC
            LIMIT $1
            """, connection);
        cmd.Parameters.Add(new NpgsqlParameter { Value = OpsBatchListLimit });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var items = new List<OpsRedeemBatchDto>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var batchId = reader.GetInt64(0);
            var title = reader.GetString(1);
            var mode = reader.GetString(2);
            var universalCode = reader.IsDBNull(3) ? null : reader.GetString(3);
            var rewards = DeserializeRewards(reader.GetString(4));
            var maxClaims = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
            var claimedCount = reader.GetInt32(6);
            var createdAt = reader.GetFieldValue<DateTimeOffset>(7);
            var expiresAt = reader.IsDBNull(8) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(8);
            var disabledAt = reader.IsDBNull(9) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(9);
            var isActive = disabledAt is null
                && (expiresAt is null || expiresAt > now)
                && (maxClaims is null || claimedCount < maxClaims.Value);

            items.Add(new OpsRedeemBatchDto(
                batchId,
                title,
                mode,
                universalCode,
                rewards,
                maxClaims,
                claimedCount,
                createdAt,
                expiresAt,
                disabledAt,
                isActive));
        }

        return items;
    }

    /// <summary>运营端查询指定批次下的兑换码列表（支持 all / unused / used 过滤）。</summary>
    public async ValueTask<IReadOnlyList<OpsRedeemCodeItemDto>> ListBatchCodesAsync(
        long batchId,
        string? statusFilter,
        int limit,
        CancellationToken ct = default)
    {
        var clampedLimit = Math.Clamp(limit <= 0 ? 500 : limit, 1, OpsCodeListMaxLimit);
        var normalizedStatus = (statusFilter ?? "all").Trim().ToLowerInvariant();

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using (var existsCmd = new NpgsqlCommand("SELECT mode FROM economy.redeem_batches WHERE batch_id = $1", connection))
        {
            existsCmd.Parameters.Add(new NpgsqlParameter { Value = batchId });
            var modeObj = await existsCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (modeObj is not string batchMode)
                throw new RedeemOperationException(RedeemOperationFailure.NotFound, "兑换码批次不存在。");

            // 通用口令码的领取人明细从 player_redeem_claims 展开，单次码从 redeem_codes 展开。
            if (batchMode == RedeemCodeRules.ModeUniversal && normalizedStatus == "used")
            {
                await using var claimsCmd = new NpgsqlCommand("""
                    SELECT prc.code_normalized, prc.player_id, p.display_name, prc.claimed_at
                    FROM economy.player_redeem_claims prc
                    LEFT JOIN identity.players p ON p.player_id = prc.player_id
                    WHERE prc.batch_id = $1
                    ORDER BY prc.claimed_at DESC
                    LIMIT $2
                    """, connection);
                claimsCmd.Parameters.Add(new NpgsqlParameter { Value = batchId });
                claimsCmd.Parameters.Add(new NpgsqlParameter { Value = clampedLimit });
                await using var claimReader = await claimsCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var claimItems = new List<OpsRedeemCodeItemDto>();
                while (await claimReader.ReadAsync(ct).ConfigureAwait(false))
                {
                    claimItems.Add(new OpsRedeemCodeItemDto(
                        claimReader.GetString(0),
                        claimReader.GetGuid(1),
                        claimReader.IsDBNull(2) ? null : claimReader.GetString(2),
                        claimReader.GetFieldValue<DateTimeOffset>(3)));
                }
                return claimItems;
            }
        }

        var whereClause = normalizedStatus switch
        {
            "unused" => "AND c.redeemed_by IS NULL",
            "used" => "AND c.redeemed_by IS NOT NULL",
            _ => string.Empty
        };

        await using var cmd = new NpgsqlCommand($"""
            SELECT c.code_normalized, c.redeemed_by, p.display_name, c.redeemed_at
            FROM economy.redeem_codes c
            LEFT JOIN identity.players p ON p.player_id = c.redeemed_by
            WHERE c.batch_id = $1 {whereClause}
            ORDER BY c.redeemed_at NULLS FIRST, c.code_normalized
            LIMIT $2
            """, connection);
        cmd.Parameters.Add(new NpgsqlParameter { Value = batchId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = clampedLimit });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var items = new List<OpsRedeemCodeItemDto>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new OpsRedeemCodeItemDto(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return items;
    }

    /// <summary>运营端停用或恢复指定兑换码批次。</summary>
    public async ValueTask<bool> SetBatchDisabledAsync(long batchId, bool disabled, DateTimeOffset now, CancellationToken ct = default)
    {
        now = now.ToUniversalTime();
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            disabled
                ? "UPDATE economy.redeem_batches SET disabled_at = COALESCE(disabled_at, $2) WHERE batch_id = $1"
                : "UPDATE economy.redeem_batches SET disabled_at = NULL WHERE batch_id = $1",
            connection);
        cmd.Parameters.Add(new NpgsqlParameter { Value = batchId });
        if (disabled)
            cmd.Parameters.Add(new NpgsqlParameter { Value = now });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private static async ValueTask<bool> HasPlayerClaimedBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? tx,
        Guid playerId,
        long batchId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT 1
            FROM economy.player_redeem_claims
            WHERE player_id = $1 AND batch_id = $2
            LIMIT 1
            """, connection, tx);
        cmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = batchId });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static async ValueTask<bool> IsCodeClaimedByPlayerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid playerId,
        long batchId,
        string normalizedCode,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT 1
            FROM economy.player_redeem_claims
            WHERE player_id = $1 AND batch_id = $2 AND code_normalized = $3
            LIMIT 1
            """, connection, tx);
        cmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = batchId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = normalizedCode });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static string SerializeRewards(IReadOnlyList<RedeemRewardDto> rewards)
        => JsonSerializer.Serialize(rewards, ProtocolJson.Options);

    private static IReadOnlyList<RedeemRewardDto> DeserializeRewards(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<RedeemRewardDto[]>(json, ProtocolJson.Options) ?? [];
    }
}
