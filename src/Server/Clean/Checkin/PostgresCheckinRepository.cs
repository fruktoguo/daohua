using System.Data;
using Domain.Economy;
using Npgsql;
using Protocol;
using Server.Clean.Social;

namespace Server.Clean.Checkin;

/// <summary>每日签到异常分类。</summary>
public enum CheckinOperationFailure
{
    InvalidPlayer = 0,
    BotRestricted = 1
}

/// <summary>每日签到业务异常（映射为明确的 HTTP 状态码）。</summary>
public sealed class CheckinOperationException(CheckinOperationFailure failure, string message) : Exception(message)
{
    public CheckinOperationFailure Failure { get; } = failure;
}

/// <summary>
/// 每日签到 PostgreSQL 持久化仓储：
/// - 独立旁路表 <c>game.player_daily_checkins</c> 每角色至多 1 行，零 ECS 与八分段侵入；
/// - 领奖沿用 <see cref="IParticipationAssetBoundary"/> → <see cref="PlayerCommitGates"/> 全局锁序与 Serializable 事务；
/// - 通过 <see cref="PostgresEconomyRepository.CommitCoreAsync"/>（<c>PersistLedger = false</c> + <c>transient_idempotency</c>）
///   与签到状态行同事务原子提交，遵守 5 类持久化流水白名单规范。
/// </summary>
public sealed class PostgresCheckinRepository(
    NpgsqlDataSource dataSource,
    IParticipationAssetBoundary? participationAssets = null)
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

    internal sealed record CheckinRecord(
        long LastClaimDayIndex,
        string LastClaimDate,
        int StreakDays,
        int TotalDays,
        int LastRewardMerit,
        long WeekStartDayIndex,
        int WeekDays,
        int WeekMerit,
        int WeekDaysMask,
        int MonthKey,
        int MonthDays,
        int MonthMerit,
        DateTimeOffset LastClaimedAt);

    /// <summary>获取 WS 握手用的轻量签到摘要；机器人或无效角色返回不可签空摘要。</summary>
    public async ValueTask<CheckinSummaryDto> GetSummaryAsync(
        Guid playerId,
        DateTimeOffset now,
        bool? knownIsBot = null,
        CancellationToken ct = default)
    {
        now = now.ToUniversalTime();
        var todayDayIndex = DailyCheckinRules.DayIndexOf(now);
        var todayDate = DailyCheckinRules.ChinaDateKeyOf(now);

        var isBot = knownIsBot ?? await ResolveIsBotAsync(playerId, ct).ConfigureAwait(false);
        if (isBot is null || isBot.Value)
            return new CheckinSummaryDto(false, todayDate, todayDayIndex, 0, 0, null, IsBot: true);

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var record = await LoadRecordAsync(connection, null, playerId, forUpdate: false, ct).ConfigureAwait(false);
        var status = ProjectStatus(record, now);
        return new CheckinSummaryDto(
            status.CanClaimToday,
            status.TodayDate,
            status.DayIndex,
            status.StreakDays,
            status.TotalDays,
            status.LastRewardMerit,
            IsBot: false);
    }

    /// <summary>读取完整每日签到状态（GET /api/v1/checkin）。</summary>
    public async ValueTask<CheckinStatusDto> GetStatusAsync(
        Guid playerId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        now = now.ToUniversalTime();
        var isBot = await ResolveIsBotAsync(playerId, ct).ConfigureAwait(false);
        if (isBot is null)
            throw new CheckinOperationException(CheckinOperationFailure.InvalidPlayer, "玩家角色不存在或已停用。");
        if (isBot.Value)
            throw new CheckinOperationException(CheckinOperationFailure.BotRestricted, "系统账号不支持每日签到。");

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var record = await LoadRecordAsync(connection, null, playerId, forUpdate: false, ct).ConfigureAwait(false);
        return ProjectStatus(record, now);
    }

    /// <summary>
    /// 执行每日签到并原子入账绑定功德：
    /// 若今日已签，安全返回 Claimed = false 与最新状态（幂等防重）。
    /// </summary>
    public async ValueTask<CheckinClaimResponseDto> ClaimAsync(
        Guid playerId,
        DateTimeOffset now,
        Func<double>? unitRandom = null,
        CancellationToken ct = default)
    {
        now = now.ToUniversalTime();
        var isBot = await ResolveIsBotAsync(playerId, ct).ConfigureAwait(false);
        if (isBot is null)
            throw new CheckinOperationException(CheckinOperationFailure.InvalidPlayer, "玩家角色不存在或已停用。");
        if (isBot.Value)
            throw new CheckinOperationException(CheckinOperationFailure.BotRestricted, "系统账号不支持每日签到。");

        var assetKeys = new[] { new ParticipationAssetKey(playerId, DailyCheckinRules.RewardItemId, string.Empty) };
        using var participationScope = participationAssets is not null
            ? await participationAssets.AcquireAsync(assetKeys, ct).ConfigureAwait(false)
            : null;
        var stripes = PlayerCommitGates.StripesFor([playerId]);
        await PlayerCommitGates.AcquireAllAsync(stripes, ct).ConfigureAwait(false);
        try
        {
            return await ExecuteClaimTransactionWithRetryAsync(playerId, now, unitRandom, ct).ConfigureAwait(false);
        }
        finally
        {
            PlayerCommitGates.ReleaseAll(stripes);
        }
    }

    private async ValueTask<CheckinClaimResponseDto> ExecuteClaimTransactionWithRetryAsync(
        Guid playerId,
        DateTimeOffset now,
        Func<double>? unitRandom,
        CancellationToken ct)
    {
        var txId = Guid.NewGuid();
        var todayDayIndex = DailyCheckinRules.DayIndexOf(now);
        var todayDate = DailyCheckinRules.ChinaDateKeyOf(now);
        var weekStartDayIndex = DailyCheckinRules.WeekStartDayIndexOf(now);
        var dayOfWeekBit = 1 << DailyCheckinRules.ChinaDayOfWeekIndexMonday(now);
        var monthKey = DailyCheckinRules.ChinaMonthKeyOf(now);
        var idempotencyKey = $"checkin.claim:{playerId:D}:{todayDayIndex}";
        EconomyCommitOutcomeUnknownException? unknown = null;

        for (var attempt = 1; ; attempt++)
        {
            using var confirmation = unknown is null ? null : new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var token = confirmation?.Token ?? ct;
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
                await LockPlayerCheckinAsync(connection, transaction, playerId, token).ConfigureAwait(false);

                var current = await LoadRecordAsync(connection, transaction, playerId, forUpdate: true, token).ConfigureAwait(false);
                if (current is not null && current.LastClaimDayIndex >= todayDayIndex)
                {
                    await transaction.RollbackAsync(token).ConfigureAwait(false);
                    var status = ProjectStatus(current, now);
                    // 若前一次 COMMIT 结果未知重放，且当日记录已推进，则向调用方确认该次已成功入账。
                    var confirmedReplay = unknown is not null && current.LastClaimDayIndex == todayDayIndex;
                    return new CheckinClaimResponseDto(
                        Claimed: confirmedReplay,
                        RewardMerit: confirmedReplay ? current.LastRewardMerit : 0,
                        RewardItemId: DailyCheckinRules.RewardItemId,
                        Status: status);
                }

                var effectiveStreakDays = DailyCheckinRules.ResolveEffectiveStreakDays(
                    current?.LastClaimDayIndex,
                    current?.StreakDays ?? 0,
                    todayDayIndex);
                var rewardMerit = DailyCheckinRules.RollReward(effectiveStreakDays, unitRandom);
                var nextTotalDays = (current?.TotalDays ?? 0) + 1;

                var sameWeek = current is not null && current.WeekStartDayIndex == weekStartDayIndex;
                var nextWeekDays = (sameWeek ? current!.WeekDays : 0) + 1;
                var nextWeekMerit = (sameWeek ? current!.WeekMerit : 0) + rewardMerit;
                var nextWeekMask = (sameWeek ? current!.WeekDaysMask : 0) | dayOfWeekBit;

                var sameMonth = current is not null && current.MonthKey == monthKey;
                var nextMonthDays = (sameMonth ? current!.MonthDays : 0) + 1;
                var nextMonthMerit = (sameMonth ? current!.MonthMerit : 0) + rewardMerit;

                await using (var upsert = new NpgsqlCommand("""
                    INSERT INTO game.player_daily_checkins
                        (player_id, last_claim_day_index, last_claim_date, streak_days, total_days, last_reward_merit,
                         week_start_day_index, week_days, week_merit, week_days_mask, month_key, month_days, month_merit,
                         last_claimed_at, updated_at)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $14)
                    ON CONFLICT (player_id) DO UPDATE SET
                        last_claim_day_index = EXCLUDED.last_claim_day_index,
                        last_claim_date = EXCLUDED.last_claim_date,
                        streak_days = EXCLUDED.streak_days,
                        total_days = EXCLUDED.total_days,
                        last_reward_merit = EXCLUDED.last_reward_merit,
                        week_start_day_index = EXCLUDED.week_start_day_index,
                        week_days = EXCLUDED.week_days,
                        week_merit = EXCLUDED.week_merit,
                        week_days_mask = EXCLUDED.week_days_mask,
                        month_key = EXCLUDED.month_key,
                        month_days = EXCLUDED.month_days,
                        month_merit = EXCLUDED.month_merit,
                        last_claimed_at = EXCLUDED.last_claimed_at,
                        updated_at = EXCLUDED.updated_at
                    """, connection, transaction))
                {
                    upsert.Parameters.Add(new NpgsqlParameter { Value = playerId });
                    upsert.Parameters.Add(new NpgsqlParameter { Value = todayDayIndex });
                    upsert.Parameters.Add(new NpgsqlParameter { Value = todayDate });
                    upsert.Parameters.Add(new NpgsqlParameter { Value = effectiveStreakDays });
                    upsert.Parameters.Add(new NpgsqlParameter { Value = nextTotalDays });
                    upsert.Parameters.Add(new NpgsqlParameter { Value = rewardMerit });
                    upsert.Parameters.Add(new NpgsqlParameter { Value = weekStartDayIndex });
                    upsert.Parameters.Add(new NpgsqlParameter { Value = nextWeekDays });
                    upsert.Parameters.Add(new NpgsqlParameter { Value = nextWeekMerit });
                    upsert.Parameters.Add(new NpgsqlParameter { Value = (short)nextWeekMask });
                    upsert.Parameters.Add(new NpgsqlParameter { Value = monthKey });
                    upsert.Parameters.Add(new NpgsqlParameter { Value = nextMonthDays });
                    upsert.Parameters.Add(new NpgsqlParameter { Value = nextMonthMerit });
                    upsert.Parameters.Add(new NpgsqlParameter { Value = now });
                    await upsert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                var ecoRequest = new EconomyTransactionRequest(
                    txId,
                    idempotencyKey,
                    "checkin.claim",
                    playerId,
                    [new AssetDelta(playerId, DailyCheckinRules.RewardItemId, string.Empty, rewardMerit)],
                    now,
                    PersistLedger: false);
                var ecoResult = await PostgresEconomyRepository.CommitCoreAsync(connection, transaction, ecoRequest, token).ConfigureAwait(false);
                if (ecoResult.AlreadyCommitted)
                {
                    await transaction.RollbackAsync(token).ConfigureAwait(false);
                    var fresh = await LoadRecordAsync(connection, null, playerId, forUpdate: false, token).ConfigureAwait(false);
                    var status = ProjectStatus(fresh, now);
                    return new CheckinClaimResponseDto(
                        Claimed: unknown is not null,
                        RewardMerit: unknown is not null ? (fresh?.LastRewardMerit ?? rewardMerit) : 0,
                        RewardItemId: DailyCheckinRules.RewardItemId,
                        Status: status);
                }

                await PostgresEconomyRepository.CommitTransactionAsync(transaction, txId, token).ConfigureAwait(false);

                var updated = new CheckinRecord(
                    todayDayIndex,
                    todayDate,
                    effectiveStreakDays,
                    nextTotalDays,
                    rewardMerit,
                    weekStartDayIndex,
                    nextWeekDays,
                    nextWeekMerit,
                    nextWeekMask,
                    monthKey,
                    nextMonthDays,
                    nextMonthMerit,
                    now);
                return new CheckinClaimResponseDto(
                    Claimed: true,
                    RewardMerit: rewardMerit,
                    RewardItemId: DailyCheckinRules.RewardItemId,
                    Status: ProjectStatus(updated, now));
            }
            catch (PostgresException ex) when (ex.SqlState is "40001" or "40P01" && attempt < SerializableRetryAttempts && unknown is null && !ct.IsCancellationRequested)
            {
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 25));
                await Task.Delay(SerializableRetryBackoff[attempt - 1] + jitter, ct).ConfigureAwait(false);
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

    internal static CheckinStatusDto ProjectStatus(CheckinRecord? record, DateTimeOffset now)
    {
        var todayDayIndex = DailyCheckinRules.DayIndexOf(now);
        var todayDate = DailyCheckinRules.ChinaDateKeyOf(now);
        var weekStartDayIndex = DailyCheckinRules.WeekStartDayIndexOf(now);
        var monthKey = DailyCheckinRules.ChinaMonthKeyOf(now);
        var nextResetUnix = DailyCheckinRules.NextResetUnixSeconds(now);

        var claimedToday = record is not null && record.LastClaimDayIndex >= todayDayIndex;
        var canClaimToday = !claimedToday;
        var displayStreakDays = record switch
        {
            null => 0,
            _ when record.LastClaimDayIndex >= todayDayIndex - 1 => Math.Max(1, record.StreakDays),
            _ => 0
        };

        int? todayRewardMerit = claimedToday ? record!.LastRewardMerit : null;
        string? todayFortuneTier = todayRewardMerit is { } merit
            ? DailyCheckinRules.ResolveFortuneTier(merit)
            : null;

        var sameWeek = record is not null && record.WeekStartDayIndex == weekStartDayIndex;
        var weekDays = sameWeek ? record!.WeekDays : 0;
        var weekMerit = sameWeek ? record!.WeekMerit : 0;
        var weekDaysMask = sameWeek ? record!.WeekDaysMask : 0;
        string? weekFortuneTier = weekDays > 0
            ? DailyCheckinRules.ResolveFortuneTier(weekMerit / (double)weekDays)
            : null;

        var sameMonth = record is not null && record.MonthKey == monthKey;
        var monthDays = sameMonth ? record!.MonthDays : 0;
        var monthMerit = sameMonth ? record!.MonthMerit : 0;

        return new CheckinStatusDto(
            CanClaimToday: canClaimToday,
            TodayDate: todayDate,
            DayIndex: todayDayIndex,
            LastClaimDate: record?.LastClaimDate.Trim(),
            StreakDays: displayStreakDays,
            TotalDays: record?.TotalDays ?? 0,
            TodayRewardMerit: todayRewardMerit,
            LastRewardMerit: record?.LastRewardMerit,
            TodayFortuneTier: todayFortuneTier,
            WeekDays: weekDays,
            WeekMerit: weekMerit,
            WeekDaysMask: weekDaysMask,
            WeekFortuneTier: weekFortuneTier,
            MonthDays: monthDays,
            MonthMerit: monthMerit,
            LastClaimedAt: record?.LastClaimedAt,
            RewardItemId: DailyCheckinRules.RewardItemId,
            NextResetUnixSeconds: nextResetUnix);
    }

    private async ValueTask<bool?> ResolveIsBotAsync(Guid playerId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT a.is_bot
            FROM identity.players p
            JOIN identity.accounts a ON a.account_id = p.account_id
            WHERE p.player_id = $1 AND a.status = 1 AND p.status = 1
            LIMIT 1
            """, connection);
        cmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is bool isBot ? isBot : null;
    }

    private static async ValueTask<CheckinRecord?> LoadRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid playerId,
        bool forUpdate,
        CancellationToken ct)
    {
        var sql = forUpdate
            ? """
              SELECT last_claim_day_index, last_claim_date, streak_days, total_days, last_reward_merit,
                     week_start_day_index, week_days, week_merit, week_days_mask,
                     month_key, month_days, month_merit, last_claimed_at
              FROM game.player_daily_checkins
              WHERE player_id = $1
              FOR UPDATE
              """
            : """
              SELECT last_claim_day_index, last_claim_date, streak_days, total_days, last_reward_merit,
                     week_start_day_index, week_days, week_merit, week_days_mask,
                     month_key, month_days, month_merit, last_claimed_at
              FROM game.player_daily_checkins
              WHERE player_id = $1
              """;
        await using var cmd = transaction is null
            ? new NpgsqlCommand(sql, connection)
            : new NpgsqlCommand(sql, connection, transaction);
        cmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        return new CheckinRecord(
            reader.GetInt64(0),
            reader.GetString(1).Trim(),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt64(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt16(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetFieldValue<DateTimeOffset>(12));
    }

    private static async ValueTask LockPlayerCheckinAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playerId,
        CancellationToken ct)
    {
        // 用独立命名域 28 对 playerId 加事务级建议锁，保护首次签到建行与并发领奖串行。
        await using var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 28))", connection, transaction);
        cmd.Parameters.Add(new NpgsqlParameter { Value = playerId.ToString("D") });
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}
