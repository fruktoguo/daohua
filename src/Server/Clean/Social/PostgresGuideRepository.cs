using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Domain.Economy;
using Domain.Persistence;
using Npgsql;
using NpgsqlTypes;
using Protocol;

namespace Server.Clean.Social;

/// <summary>道友攻略与配装付费查阅业务异常分类。</summary>
public enum GuideOperationFailure
{
    InvalidArgument = 0,
    NotFound = 1,
    Forbidden = 2,
    BotRestricted = 3,
    NotRanked = 4,
    DailyQuotaExceeded = 5,
    InsufficientSpiritStones = 6,
    PriceChanged = 7,
    Conflict = 8
}

/// <summary>道友攻略与配装付费查阅业务异常（由端点层映射为明确 HTTP 状态码与错误码）。</summary>
public sealed class GuideOperationException(GuideOperationFailure failure, string message) : Exception(message)
{
    public GuideOperationFailure Failure { get; } = failure;
}

/// <summary>数据库查询出的单篇攻略原始持久化记录。</summary>
public sealed record GuidePostRow(
    Guid PostId,
    Guid AuthorId,
    string AuthorName,
    string AuthorAvatar,
    int AuthorRealmLevel,
    string? AuthorZongmenPrefix,
    string Title,
    string Summary,
    string Body,
    double UnlockPrice,
    bool IsUnlockedByViewer,
    bool LikedByViewer,
    double ViewerTippedStones,
    int ViewCount,
    int LikeCount,
    int TipperCount,
    double TotalRewardStones,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 道友攻略板块与角色名片配装付费查阅 PostgreSQL 持久化仓储：
/// - 承载攻略 CRUD、独立阅读计数、点赞、付费永久解锁、自由打赏及角色配装当日查阅授权；
/// - 所有跨玩家灵石结算严格遵循 <see cref="IParticipationAssetBoundary"/> → <see cref="PlayerCommitGates"/> 升序加锁与 Serializable 事务；
/// - 统一征收 10% 系统手续费（向下取整），经济流水写入第⑤类持久化白名单（guide.reward / privacy.inspect，保留 30 天）。
/// </summary>
public sealed partial class PostgresGuideRepository(
    NpgsqlDataSource dataSource,
    IParticipationAssetBoundary? participationAssets = null)
{
    public const string SpiritStoneAssetId = "currency.spirit-stone";
    public const double MaxPriceOrTipStones = 100_000d;
    public const double SystemTaxRate = 0.10d;
    public const int DailyPublishLimit = 1;

    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);
    private const int SerializableRetryAttempts = 6;
    private static readonly TimeSpan[] SerializableRetryBackoff =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(75),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(400)
    ];

    [GeneratedRegex(@"\{\{(?:item|order|party|location|map|img|sticker):[^}]+\}\}|!\[[^\]]*\]\([^)]+\)|[#>*`\-]", RegexOptions.Compiled)]
    private static partial Regex MarkupStripRegex();

    /// <summary>计算北京时间（UTC+8）当前自然日与当日结束零点（即次日 00:00:00+08:00，返回 UTC 时间戳）。</summary>
    public static (DateOnly AccessDate, DateTimeOffset NextMidnightUtc) ResolveCstDayWindow(DateTimeOffset nowUtc)
    {
        var cst = nowUtc.ToOffset(ChinaOffset);
        var accessDate = new DateOnly(cst.Year, cst.Month, cst.Day);
        var nextMidnightCst = new DateTimeOffset(cst.Year, cst.Month, cst.Day, 0, 0, 0, ChinaOffset).AddDays(1);
        return (accessDate, nextMidnightCst.ToUniversalTime());
    }

    /// <summary>按总纲 §31.4.1 计算攻略综合热度分。</summary>
    public static double ComputeHotScore(int viewCount, int likeCount, int tipperCount, double totalRewardStones)
        => viewCount + 5d * likeCount + 10d * tipperCount + 8d * Math.Log10(1d + Math.Max(0d, totalRewardStones));

    /// <summary>计算扣除 10% 系统手续费（向下取整）后的收款方实得灵石。</summary>
    public static (double Tax, double NetAmount) ComputeTaxAndNet(double grossAmount)
    {
        var tax = Math.Floor(grossAmount * SystemTaxRate);
        var net = Math.Max(0d, grossAmount - tax);
        return (tax, net);
    }

    /// <summary>生成列表卡片摘要文本：付费帖严格只返回作者显式填写的导语，免费帖未填导语时截取纯文本前 120 字。</summary>
    public static string BuildSafeExcerpt(string summary, string body, double unlockPrice)
    {
        if (!string.IsNullOrWhiteSpace(summary))
            return summary.Trim();
        if (unlockPrice > 0d)
            return string.Empty;

        var stripped = MarkupStripRegex().Replace(body ?? string.Empty, " ");
        var collapsed = string.Join(' ', stripped.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return TruncateByRunes(collapsed, 120);
    }

    private static string TruncateByRunes(string input, int maxRunes)
    {
        if (string.IsNullOrEmpty(input) || maxRunes <= 0) return string.Empty;
        var count = 0;
        var byteOrCharIndex = 0;
        foreach (var rune in input.EnumerateRunes())
        {
            if (count >= maxRunes)
                return string.Concat(input.AsSpan(0, byteOrCharIndex), "…");
            byteOrCharIndex += rune.Utf16SequenceLength;
            count++;
        }
        return input;
    }

    /// <summary>读取玩家有效身份与机器人标记；角色不存在或封禁时返回 null。</summary>
    public async ValueTask<(Guid PlayerId, string DisplayName, bool IsBot)?> GetPlayerFlagsAsync(
        Guid playerId,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT p.player_id, p.display_name, a.is_bot
            FROM identity.players p
            JOIN identity.accounts a ON a.account_id = p.account_id
            WHERE p.player_id = $1 AND p.status = 1 AND a.status = 1
            LIMIT 1
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return (reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2));
    }

    /// <summary>读取玩家在指定北京时间自然日已发布的攻略次数。</summary>
    public async ValueTask<int> GetDailyPublishedCountAsync(
        Guid playerId,
        DateOnly cstDate,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT post_count
            FROM social.guide_daily_quotas
            WHERE player_id = $1 AND post_date = $2
            LIMIT 1
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = playerId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = cstDate });
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is int count ? count : 0;
    }

    /// <summary>分页查询攻略列表（支持 hot 热度排序与 reward 打赏排序，以及 mineOnly 本人帖子筛选）。</summary>
    public async ValueTask<(int TotalCount, IReadOnlyList<GuidePostRow> Rows)> ListPostsAsync(
        Guid viewerId,
        string sort,
        bool mineOnly,
        int limit,
        int offset,
        CancellationToken ct = default)
    {
        var clampedLimit = Math.Clamp(limit, 1, 100);
        var clampedOffset = Math.Max(0, offset);
        var orderClause = string.Equals(sort, "reward", StringComparison.OrdinalIgnoreCase)
            ? "gp.total_reward_stones DESC, gp.tipper_count DESC, gp.created_at DESC, gp.post_id DESC"
            : "(gp.view_count + 5.0 * gp.like_count + 10.0 * gp.tipper_count + 8.0 * log(10.0, (1.0 + GREATEST(0.0, gp.total_reward_stones))::numeric)::double precision) DESC, gp.created_at DESC, gp.post_id DESC";

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        int totalCount;
        await using (var countCmd = new NpgsqlCommand($"""
            SELECT count(*)::integer
            FROM social.guide_posts gp
            JOIN identity.players p ON p.player_id = gp.author_id
            JOIN identity.accounts a ON a.account_id = p.account_id
            WHERE gp.deleted_at IS NULL
              AND p.status = 1 AND a.status = 1 AND NOT a.is_bot
              {(mineOnly ? "AND gp.author_id = $1" : "")}
            """, conn))
        {
            if (mineOnly)
                countCmd.Parameters.Add(new NpgsqlParameter { Value = viewerId });
            totalCount = (int)(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
        }

        if (totalCount == 0 || clampedOffset >= totalCount)
            return (totalCount, []);

        var sql = $"""
            SELECT gp.post_id,
                   gp.author_id,
                   p.display_name,
                   COALESCE(NULLIF(proj.avatar, ''), 'player') AS avatar,
                   GREATEST(1, FLOOR(COALESCE(proj.realm_rank, 1))::integer) AS realm_level,
                   z.prefix,
                   gp.title,
                   gp.summary,
                   CASE WHEN gp.unlock_price <= 0 THEN left(gp.body, 400) ELSE '' END AS preview_body,
                   gp.unlock_price,
                   (gp.unlock_price <= 0 OR gp.author_id = $1 OR u.viewer_id IS NOT NULL) AS is_unlocked,
                   (l.player_id IS NOT NULL) AS liked_by_me,
                   COALESCE(t.total_tipped_stones, 0) AS my_tipped,
                   gp.view_count,
                   gp.like_count,
                   gp.tipper_count,
                   gp.total_reward_stones,
                   gp.created_at,
                   gp.updated_at
            FROM social.guide_posts gp
            JOIN identity.players p ON p.player_id = gp.author_id
            JOIN identity.accounts a ON a.account_id = p.account_id
            LEFT JOIN game.player_projection proj ON proj.player_id = gp.author_id
            LEFT JOIN social.zongmen_members zm ON zm.player_id = gp.author_id
            LEFT JOIN social.zongmen z ON z.zongmen_id = zm.zongmen_id AND z.disbanded_at IS NULL
            LEFT JOIN social.guide_post_unlocks u ON u.post_id = gp.post_id AND u.viewer_id = $1
            LEFT JOIN social.guide_post_likes l ON l.post_id = gp.post_id AND l.player_id = $1
            LEFT JOIN social.guide_post_tips t ON t.post_id = gp.post_id AND t.tipper_id = $1
            WHERE gp.deleted_at IS NULL
              AND p.status = 1 AND a.status = 1 AND NOT a.is_bot
              {(mineOnly ? "AND gp.author_id = $1" : "")}
            ORDER BY {orderClause}
            LIMIT $2 OFFSET $3
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = viewerId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = clampedLimit });
        cmd.Parameters.Add(new NpgsqlParameter { Value = clampedOffset });

        var rows = new List<GuidePostRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadPostRow(reader));
        }
        return (totalCount, rows);
    }

    /// <summary>
    /// 读取单篇攻略详情：若查看者已具备可见权限（免费、本人或已付费解锁），自动记录去重阅读次数并返回完整正文；
    /// 若尚未解锁付费帖，则不计阅读量且不在返回行中暴露正文。
    /// </summary>
    public async ValueTask<GuidePostRow?> GetPostDetailAsync(
        Guid postId,
        Guid viewerId,
        bool recordView,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var row = await LoadSinglePostCoreAsync(conn, tx, postId, viewerId, ct).ConfigureAwait(false);
        if (row is null)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }

        if (recordView && row.IsUnlockedByViewer)
        {
            await using var viewCmd = new NpgsqlCommand("""
                INSERT INTO social.guide_post_views (post_id, player_id, viewed_at)
                VALUES ($1, $2, $3)
                ON CONFLICT (post_id, player_id) DO NOTHING
                RETURNING 1
                """, conn, tx);
            viewCmd.Parameters.Add(new NpgsqlParameter { Value = postId });
            viewCmd.Parameters.Add(new NpgsqlParameter { Value = viewerId });
            viewCmd.Parameters.Add(new NpgsqlParameter { Value = nowUtc });
            if (await viewCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            {
                await using var incCmd = new NpgsqlCommand("""
                    UPDATE social.guide_posts
                    SET view_count = view_count + 1
                    WHERE post_id = $1
                    RETURNING view_count
                    """, conn, tx);
                incCmd.Parameters.Add(new NpgsqlParameter { Value = postId });
                var updatedViews = (int)(await incCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? (row.ViewCount + 1));
                row = row with { ViewCount = updatedViews };
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>原子占用当日（UTC+8）1 次发帖配额并插入新攻略。</summary>
    public async ValueTask<GuidePostRow> CreatePostAsync(
        Guid authorId,
        string title,
        string summary,
        string body,
        double unlockPrice,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        var (cstDate, _) = ResolveCstDayWindow(nowUtc);
        var postId = Guid.NewGuid();

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var quotaCmd = new NpgsqlCommand("""
            INSERT INTO social.guide_daily_quotas (player_id, post_date, post_count, updated_at)
            VALUES ($1, $2, 1, $3)
            ON CONFLICT (player_id, post_date) DO UPDATE
                SET post_count = social.guide_daily_quotas.post_count + 1,
                    updated_at = EXCLUDED.updated_at
                WHERE social.guide_daily_quotas.post_count < $4
            RETURNING post_count
            """, conn, tx))
        {
            quotaCmd.Parameters.Add(new NpgsqlParameter { Value = authorId });
            quotaCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = cstDate });
            quotaCmd.Parameters.Add(new NpgsqlParameter { Value = nowUtc });
            quotaCmd.Parameters.Add(new NpgsqlParameter { Value = DailyPublishLimit });
            var claimed = await quotaCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (claimed is null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw new GuideOperationException(GuideOperationFailure.DailyQuotaExceeded, "今日已发布过攻略，每日最多发布 1 篇。");
            }
        }

        await using (var insertCmd = new NpgsqlCommand("""
            INSERT INTO social.guide_posts
                (post_id, author_id, title, summary, body, unlock_price, view_count, like_count, tipper_count, total_reward_stones, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, 1, 0, 0, 0, $7, $7)
            """, conn, tx))
        {
            insertCmd.Parameters.Add(new NpgsqlParameter { Value = postId });
            insertCmd.Parameters.Add(new NpgsqlParameter { Value = authorId });
            insertCmd.Parameters.Add(new NpgsqlParameter { Value = title });
            insertCmd.Parameters.Add(new NpgsqlParameter { Value = summary });
            insertCmd.Parameters.Add(new NpgsqlParameter { Value = body });
            insertCmd.Parameters.Add(new NpgsqlParameter { Value = unlockPrice });
            insertCmd.Parameters.Add(new NpgsqlParameter { Value = nowUtc });
            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var selfViewCmd = new NpgsqlCommand("""
            INSERT INTO social.guide_post_views (post_id, player_id, viewed_at)
            VALUES ($1, $2, $3)
            ON CONFLICT (post_id, player_id) DO NOTHING
            """, conn, tx))
        {
            selfViewCmd.Parameters.Add(new NpgsqlParameter { Value = postId });
            selfViewCmd.Parameters.Add(new NpgsqlParameter { Value = authorId });
            selfViewCmd.Parameters.Add(new NpgsqlParameter { Value = nowUtc });
            await selfViewCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var row = await LoadSinglePostCoreAsync(conn, tx, postId, authorId, ct).ConfigureAwait(false)
            ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "攻略创建后读取失败。");
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>更新本人已发布的攻略（不消耗每日发帖额度）。</summary>
    public async ValueTask<GuidePostRow> UpdatePostAsync(
        Guid postId,
        Guid authorId,
        string title,
        string summary,
        string body,
        double unlockPrice,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var checkCmd = new NpgsqlCommand("""
            SELECT author_id
            FROM social.guide_posts
            WHERE post_id = $1 AND deleted_at IS NULL
            FOR UPDATE
            """, conn, tx))
        {
            checkCmd.Parameters.Add(new NpgsqlParameter { Value = postId });
            var existingAuthor = await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as Guid?;
            if (existingAuthor is null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw new GuideOperationException(GuideOperationFailure.NotFound, "该攻略不存在或已删除。");
            }
            if (existingAuthor.Value != authorId)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw new GuideOperationException(GuideOperationFailure.Forbidden, "只能编辑自己发布的攻略。");
            }
        }

        await using (var updateCmd = new NpgsqlCommand("""
            UPDATE social.guide_posts
            SET title = $2, summary = $3, body = $4, unlock_price = $5, updated_at = $6
            WHERE post_id = $1 AND deleted_at IS NULL
            """, conn, tx))
        {
            updateCmd.Parameters.Add(new NpgsqlParameter { Value = postId });
            updateCmd.Parameters.Add(new NpgsqlParameter { Value = title });
            updateCmd.Parameters.Add(new NpgsqlParameter { Value = summary });
            updateCmd.Parameters.Add(new NpgsqlParameter { Value = body });
            updateCmd.Parameters.Add(new NpgsqlParameter { Value = unlockPrice });
            updateCmd.Parameters.Add(new NpgsqlParameter { Value = nowUtc });
            await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var row = await LoadSinglePostCoreAsync(conn, tx, postId, authorId, ct).ConfigureAwait(false)
            ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "攻略不存在。");
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>软删除本人已发布的攻略（当日已用额度保留不退还）。</summary>
    public async ValueTask DeletePostAsync(
        Guid postId,
        Guid authorId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            UPDATE social.guide_posts
            SET deleted_at = $3, updated_at = $3
            WHERE post_id = $1 AND author_id = $2 AND deleted_at IS NULL
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = postId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = authorId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = nowUtc });
        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (affected == 0)
        {
            await using var probe = new NpgsqlCommand("""
                SELECT author_id FROM social.guide_posts WHERE post_id = $1 AND deleted_at IS NULL
                """, conn);
            probe.Parameters.Add(new NpgsqlParameter { Value = postId });
            var owner = await probe.ExecuteScalarAsync(ct).ConfigureAwait(false) as Guid?;
            if (owner is not null && owner.Value != authorId)
                throw new GuideOperationException(GuideOperationFailure.Forbidden, "只能删除自己发布的攻略。");
            throw new GuideOperationException(GuideOperationFailure.NotFound, "该攻略不存在或已删除。");
        }
    }

    /// <summary>幂等切换玩家对攻略的点赞状态，并返回最新点赞数与热度分。</summary>
    public async ValueTask<SetGuideLikeResponse> SetLikeAsync(
        Guid postId,
        Guid playerId,
        bool liked,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var lockCmd = new NpgsqlCommand("""
            SELECT 1 FROM social.guide_posts WHERE post_id = $1 AND deleted_at IS NULL FOR UPDATE
            """, conn, tx))
        {
            lockCmd.Parameters.Add(new NpgsqlParameter { Value = postId });
            if (await lockCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw new GuideOperationException(GuideOperationFailure.NotFound, "该攻略不存在或已删除。");
            }
        }

        if (liked)
        {
            await using var ins = new NpgsqlCommand("""
                INSERT INTO social.guide_post_likes (post_id, player_id, created_at)
                VALUES ($1, $2, $3)
                ON CONFLICT (post_id, player_id) DO NOTHING
                RETURNING 1
                """, conn, tx);
            ins.Parameters.Add(new NpgsqlParameter { Value = postId });
            ins.Parameters.Add(new NpgsqlParameter { Value = playerId });
            ins.Parameters.Add(new NpgsqlParameter { Value = nowUtc });
            if (await ins.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            {
                await using var upd = new NpgsqlCommand("""
                    UPDATE social.guide_posts SET like_count = like_count + 1 WHERE post_id = $1
                    """, conn, tx);
                upd.Parameters.Add(new NpgsqlParameter { Value = postId });
                await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        else
        {
            await using var del = new NpgsqlCommand("""
                DELETE FROM social.guide_post_likes WHERE post_id = $1 AND player_id = $2 RETURNING 1
                """, conn, tx);
            del.Parameters.Add(new NpgsqlParameter { Value = postId });
            del.Parameters.Add(new NpgsqlParameter { Value = playerId });
            if (await del.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            {
                await using var upd = new NpgsqlCommand("""
                    UPDATE social.guide_posts SET like_count = GREATEST(0, like_count - 1) WHERE post_id = $1
                    """, conn, tx);
                upd.Parameters.Add(new NpgsqlParameter { Value = postId });
                await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        int views = 0, likes = 0, tippers = 0;
        double rewards = 0d;
        await using (var statCmd = new NpgsqlCommand("""
            SELECT view_count, like_count, tipper_count, total_reward_stones
            FROM social.guide_posts WHERE post_id = $1
            """, conn, tx))
        {
            statCmd.Parameters.Add(new NpgsqlParameter { Value = postId });
            await using var reader = await statCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                views = reader.GetInt32(0);
                likes = reader.GetInt32(1);
                tippers = reader.GetInt32(2);
                rewards = reader.GetDouble(3);
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new SetGuideLikeResponse(postId.ToString("D"), liked, likes, ComputeHotScore(views, likes, tippers, rewards));
    }

    /// <summary>付费永久解锁指定攻略（含 10% 系统手续费、防涨价滑点校验、幂等重放保护与阅读计数）。</summary>
    public async ValueTask<(GuidePostRow Row, Guid AuthorId, bool NewlyPaid)> UnlockPostAsync(
        Guid postId,
        Guid viewerId,
        double expectedPrice,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        Guid authorId;
        double currentPrice;
        await using (var preConn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var preCmd = new NpgsqlCommand("""
                SELECT gp.author_id, gp.unlock_price, (u.viewer_id IS NOT NULL) AS already_unlocked
                FROM social.guide_posts gp
                JOIN identity.players p ON p.player_id = gp.author_id
                JOIN identity.accounts a ON a.account_id = p.account_id
                LEFT JOIN social.guide_post_unlocks u ON u.post_id = gp.post_id AND u.viewer_id = $2
                WHERE gp.post_id = $1 AND gp.deleted_at IS NULL AND p.status = 1 AND a.status = 1 AND NOT a.is_bot
                LIMIT 1
                """, preConn);
            preCmd.Parameters.Add(new NpgsqlParameter { Value = postId });
            preCmd.Parameters.Add(new NpgsqlParameter { Value = viewerId });
            await using var reader = await preCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                throw new GuideOperationException(GuideOperationFailure.NotFound, "该攻略不存在或已删除。");
            authorId = reader.GetGuid(0);
            currentPrice = reader.GetDouble(1);
            var alreadyUnlocked = reader.GetBoolean(2);
            if (authorId == viewerId || currentPrice <= 0d || alreadyUnlocked)
            {
                await reader.CloseAsync().ConfigureAwait(false);
                var freeOrUnlocked = await GetPostDetailAsync(postId, viewerId, recordView: true, nowUtc, ct).ConfigureAwait(false)
                    ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "该攻略不存在或已删除。");
                return (freeOrUnlocked, authorId, false);
            }
        }

        if (currentPrice > expectedPrice)
            throw new GuideOperationException(GuideOperationFailure.PriceChanged, $"攻略解锁价格已变更为 {currentPrice:0} 灵石，请刷新后确认。");

        var lockPlayers = new[] { viewerId, authorId }.Order().ToArray();
        var assetKeys = lockPlayers.Select(pid => new ParticipationAssetKey(pid, SpiritStoneAssetId, string.Empty)).ToArray();
        using var assetScope = participationAssets is not null
            ? await participationAssets.AcquireAsync(assetKeys, ct).ConfigureAwait(false)
            : null;
        var stripes = PlayerCommitGates.StripesFor(lockPlayers);
        await PlayerCommitGates.AcquireAllAsync(stripes, ct).ConfigureAwait(false);
        try
        {
            return await ExecuteSerializableAsync(async (conn, tx, token) =>
            {
                double lockedPrice;
                await using (var lockPost = new NpgsqlCommand("""
                    SELECT unlock_price
                    FROM social.guide_posts
                    WHERE post_id = $1 AND author_id = $2 AND deleted_at IS NULL
                    FOR UPDATE
                    """, conn, tx))
                {
                    lockPost.Parameters.Add(new NpgsqlParameter { Value = postId });
                    lockPost.Parameters.Add(new NpgsqlParameter { Value = authorId });
                    var val = await lockPost.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (val is not double p)
                        throw new GuideOperationException(GuideOperationFailure.NotFound, "该攻略不存在或已删除。");
                    lockedPrice = p;
                }

                if (lockedPrice <= 0d)
                {
                    var freeRow = await LoadSinglePostCoreAsync(conn, tx, postId, viewerId, token).ConfigureAwait(false)
                        ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "该攻略不存在。");
                    await CommitTransactionAsync(tx, postId, token).ConfigureAwait(false);
                    return (freeRow, authorId, false);
                }

                if (lockedPrice > expectedPrice)
                    throw new GuideOperationException(GuideOperationFailure.PriceChanged, $"攻略解锁价格已变更为 {lockedPrice:0} 灵石，请刷新后确认。");

                var (_, netAmount) = ComputeTaxAndNet(lockedPrice);
                await using var insUnlock = new NpgsqlCommand("""
                    INSERT INTO social.guide_post_unlocks (post_id, viewer_id, paid_stones, author_received_stones, unlocked_at)
                    VALUES ($1, $2, $3, $4, $5)
                    ON CONFLICT (post_id, viewer_id) DO NOTHING
                    RETURNING 1
                    """, conn, tx);
                insUnlock.Parameters.Add(new NpgsqlParameter { Value = postId });
                insUnlock.Parameters.Add(new NpgsqlParameter { Value = viewerId });
                insUnlock.Parameters.Add(new NpgsqlParameter { Value = lockedPrice });
                insUnlock.Parameters.Add(new NpgsqlParameter { Value = netAmount });
                insUnlock.Parameters.Add(new NpgsqlParameter { Value = nowUtc });
                var newlyUnlocked = await insUnlock.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;

                if (newlyUnlocked)
                {
                    var idempotencyKey = $"guide.unlock:{viewerId:N}:{postId:N}";
                    var txId = DeterministicGuid(idempotencyKey);
                    var deltas = BuildTransferDeltas(viewerId, authorId, lockedPrice, netAmount);
                    await PostgresEconomyRepository.CommitCoreAsync(
                        conn,
                        tx,
                        new EconomyTransactionRequest(
                            txId,
                            idempotencyKey,
                            "guide.reward",
                            viewerId,
                            deltas,
                            nowUtc,
                            Encoding.UTF8.GetBytes($"{{\"kind\":\"unlock\",\"postId\":\"{postId:D}\",\"paid\":{lockedPrice},\"net\":{netAmount}}}"),
                            PersistLedger: true),
                        token).ConfigureAwait(false);

                    // 若该读者此前尚未打赏过本帖，解锁付费亦计入独立赞赏人数。
                    await using var hasTippedCmd = new NpgsqlCommand("""
                        SELECT EXISTS(SELECT 1 FROM social.guide_post_tips WHERE post_id = $1 AND tipper_id = $2)
                        """, conn, tx);
                    hasTippedCmd.Parameters.Add(new NpgsqlParameter { Value = postId });
                    hasTippedCmd.Parameters.Add(new NpgsqlParameter { Value = viewerId });
                    var alreadyTipper = (bool)(await hasTippedCmd.ExecuteScalarAsync(token).ConfigureAwait(false) ?? false);

                    await using var updPost = new NpgsqlCommand("""
                        UPDATE social.guide_posts
                        SET total_reward_stones = total_reward_stones + $2,
                            tipper_count = tipper_count + $3
                        WHERE post_id = $1
                        """, conn, tx);
                    updPost.Parameters.Add(new NpgsqlParameter { Value = postId });
                    updPost.Parameters.Add(new NpgsqlParameter { Value = lockedPrice });
                    updPost.Parameters.Add(new NpgsqlParameter { Value = alreadyTipper ? 0 : 1 });
                    await updPost.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                // 解锁同时计入有效阅读去重。
                await using (var viewCmd = new NpgsqlCommand("""
                    INSERT INTO social.guide_post_views (post_id, player_id, viewed_at)
                    VALUES ($1, $2, $3)
                    ON CONFLICT (post_id, player_id) DO NOTHING
                    RETURNING 1
                    """, conn, tx))
                {
                    viewCmd.Parameters.Add(new NpgsqlParameter { Value = postId });
                    viewCmd.Parameters.Add(new NpgsqlParameter { Value = viewerId });
                    viewCmd.Parameters.Add(new NpgsqlParameter { Value = nowUtc });
                    if (await viewCmd.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                    {
                        await using var incView = new NpgsqlCommand("""
                            UPDATE social.guide_posts SET view_count = view_count + 1 WHERE post_id = $1
                            """, conn, tx);
                        incView.Parameters.Add(new NpgsqlParameter { Value = postId });
                        await incView.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }

                var finalRow = await LoadSinglePostCoreAsync(conn, tx, postId, viewerId, token).ConfigureAwait(false)
                    ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "该攻略不存在。");
                await CommitTransactionAsync(tx, postId, token).ConfigureAwait(false);
                return (finalRow, authorId, newlyUnlocked);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            PlayerCommitGates.ReleaseAll(stripes);
        }
    }

    /// <summary>向指定攻略自由打赏灵石（含 10% 系统手续费、客户端 requestId 幂等防重与独立赞赏人数更新）。</summary>
    public async ValueTask<(TipGuidePostResponse Response, Guid AuthorId, bool NewlyPaid)> TipPostAsync(
        Guid postId,
        Guid tipperId,
        string clientRequestId,
        double amount,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        Guid authorId;
        await using (var preConn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var preCmd = new NpgsqlCommand("""
                SELECT gp.author_id,
                       (gp.unlock_price <= 0 OR gp.author_id = $2 OR u.viewer_id IS NOT NULL) AS is_unlocked
                FROM social.guide_posts gp
                JOIN identity.players p ON p.player_id = gp.author_id
                JOIN identity.accounts a ON a.account_id = p.account_id
                LEFT JOIN social.guide_post_unlocks u ON u.post_id = gp.post_id AND u.viewer_id = $2
                WHERE gp.post_id = $1 AND gp.deleted_at IS NULL AND p.status = 1 AND a.status = 1 AND NOT a.is_bot
                LIMIT 1
                """, preConn);
            preCmd.Parameters.Add(new NpgsqlParameter { Value = postId });
            preCmd.Parameters.Add(new NpgsqlParameter { Value = tipperId });
            await using var reader = await preCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                throw new GuideOperationException(GuideOperationFailure.NotFound, "该攻略不存在或已删除。");
            authorId = reader.GetGuid(0);
            var isUnlocked = reader.GetBoolean(1);
            if (authorId == tipperId)
                throw new GuideOperationException(GuideOperationFailure.Forbidden, "不能打赏自己发布的攻略。");
            if (!isUnlocked)
                throw new GuideOperationException(GuideOperationFailure.Forbidden, "请先解锁该攻略后再追加打赏。");
        }

        var idempotencyKey = $"guide.tip:{tipperId:N}:{SanitizeRequestId(clientRequestId)}";
        var txId = DeterministicGuid(idempotencyKey);
        var (_, netAmount) = ComputeTaxAndNet(amount);

        var lockPlayers = new[] { tipperId, authorId }.Order().ToArray();
        var assetKeys = lockPlayers.Select(pid => new ParticipationAssetKey(pid, SpiritStoneAssetId, string.Empty)).ToArray();
        using var assetScope = participationAssets is not null
            ? await participationAssets.AcquireAsync(assetKeys, ct).ConfigureAwait(false)
            : null;
        var stripes = PlayerCommitGates.StripesFor(lockPlayers);
        await PlayerCommitGates.AcquireAllAsync(stripes, ct).ConfigureAwait(false);
        try
        {
            return await ExecuteSerializableAsync(async (conn, tx, token) =>
            {
                await using (var lockPost = new NpgsqlCommand("""
                    SELECT 1 FROM social.guide_posts WHERE post_id = $1 AND author_id = $2 AND deleted_at IS NULL FOR UPDATE
                    """, conn, tx))
                {
                    lockPost.Parameters.Add(new NpgsqlParameter { Value = postId });
                    lockPost.Parameters.Add(new NpgsqlParameter { Value = authorId });
                    if (await lockPost.ExecuteScalarAsync(token).ConfigureAwait(false) is null)
                        throw new GuideOperationException(GuideOperationFailure.NotFound, "该攻略不存在或已删除。");
                }

                var deltas = BuildTransferDeltas(tipperId, authorId, amount, netAmount);
                var commitResult = await PostgresEconomyRepository.CommitCoreAsync(
                    conn,
                    tx,
                    new EconomyTransactionRequest(
                        txId,
                        idempotencyKey,
                        "guide.reward",
                        tipperId,
                        deltas,
                        nowUtc,
                        Encoding.UTF8.GetBytes($"{{\"kind\":\"tip\",\"postId\":\"{postId:D}\",\"paid\":{amount},\"net\":{netAmount}}}"),
                        PersistLedger: true),
                    token).ConfigureAwait(false);

                double myTippedTotal = 0d;
                if (!commitResult.AlreadyCommitted)
                {
                    await using var existedBeforeCmd = new NpgsqlCommand("""
                        SELECT EXISTS(SELECT 1 FROM social.guide_post_unlocks WHERE post_id = $1 AND viewer_id = $2)
                            OR EXISTS(SELECT 1 FROM social.guide_post_tips WHERE post_id = $1 AND tipper_id = $2)
                        """, conn, tx);
                    existedBeforeCmd.Parameters.Add(new NpgsqlParameter { Value = postId });
                    existedBeforeCmd.Parameters.Add(new NpgsqlParameter { Value = tipperId });
                    var alreadyCountedTipper = (bool)(await existedBeforeCmd.ExecuteScalarAsync(token).ConfigureAwait(false) ?? false);

                    await using var upsertTip = new NpgsqlCommand("""
                        INSERT INTO social.guide_post_tips (post_id, tipper_id, total_tipped_stones, tip_count, updated_at)
                        VALUES ($1, $2, $3, 1, $4)
                        ON CONFLICT (post_id, tipper_id) DO UPDATE
                            SET total_tipped_stones = social.guide_post_tips.total_tipped_stones + EXCLUDED.total_tipped_stones,
                                tip_count = social.guide_post_tips.tip_count + 1,
                                updated_at = EXCLUDED.updated_at
                        RETURNING total_tipped_stones
                        """, conn, tx);
                    upsertTip.Parameters.Add(new NpgsqlParameter { Value = postId });
                    upsertTip.Parameters.Add(new NpgsqlParameter { Value = tipperId });
                    upsertTip.Parameters.Add(new NpgsqlParameter { Value = amount });
                    upsertTip.Parameters.Add(new NpgsqlParameter { Value = nowUtc });
                    myTippedTotal = (double)(await upsertTip.ExecuteScalarAsync(token).ConfigureAwait(false) ?? amount);

                    await using var updPost = new NpgsqlCommand("""
                        UPDATE social.guide_posts
                        SET total_reward_stones = total_reward_stones + $2,
                            tipper_count = tipper_count + $3
                        WHERE post_id = $1
                        """, conn, tx);
                    updPost.Parameters.Add(new NpgsqlParameter { Value = postId });
                    updPost.Parameters.Add(new NpgsqlParameter { Value = amount });
                    updPost.Parameters.Add(new NpgsqlParameter { Value = alreadyCountedTipper ? 0 : 1 });
                    await updPost.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                else
                {
                    await using var readTip = new NpgsqlCommand("""
                        SELECT COALESCE(total_tipped_stones, 0) FROM social.guide_post_tips WHERE post_id = $1 AND tipper_id = $2
                        """, conn, tx);
                    readTip.Parameters.Add(new NpgsqlParameter { Value = postId });
                    readTip.Parameters.Add(new NpgsqlParameter { Value = tipperId });
                    myTippedTotal = Convert.ToDouble(await readTip.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0d);
                }

                int views = 0, likes = 0, tippers = 0;
                double totalRewards = 0d;
                await using (var statCmd = new NpgsqlCommand("""
                    SELECT view_count, like_count, tipper_count, total_reward_stones
                    FROM social.guide_posts WHERE post_id = $1
                    """, conn, tx))
                {
                    statCmd.Parameters.Add(new NpgsqlParameter { Value = postId });
                    await using var reader = await statCmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        views = reader.GetInt32(0);
                        likes = reader.GetInt32(1);
                        tippers = reader.GetInt32(2);
                        totalRewards = reader.GetDouble(3);
                    }
                }

                await CommitTransactionAsync(tx, txId, token).ConfigureAwait(false);
                var resp = new TipGuidePostResponse(
                    postId.ToString("D"),
                    amount,
                    netAmount,
                    myTippedTotal,
                    tippers,
                    totalRewards,
                    ComputeHotScore(views, likes, tippers, totalRewards));
                return (resp, authorId, !commitResult.AlreadyCommitted);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            PlayerCommitGates.ReleaseAll(stripes);
        }
    }

    /// <summary>查询访客对目标玩家在当前北京时间自然日内是否持有尚未过期的配装查阅授权（返回到期时刻）。</summary>
    public async ValueTask<DateTimeOffset?> GetActiveLoadoutInspectGrantAsync(
        Guid viewerId,
        Guid targetId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        var (accessDate, _) = ResolveCstDayWindow(nowUtc);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("""
            SELECT expires_at
            FROM social.loadout_inspect_grants
            WHERE viewer_id = $1 AND target_id = $2 AND access_date = $3 AND expires_at > $4
            LIMIT 1
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = viewerId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = targetId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = accessDate });
        cmd.Parameters.Add(new NpgsqlParameter { Value = nowUtc });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? reader.GetFieldValue<DateTimeOffset>(0).ToUniversalTime()
            : null;
    }

    /// <summary>支付灵石购买目标玩家当日角色名片配装查阅权（维持至北京时间当日结束零点，幂等重放安全）。</summary>
    public async ValueTask<(UnlockPlayerLoadoutResponse Response, bool NewlyPaid)> UnlockPlayerLoadoutAsync(
        Guid viewerId,
        Guid targetId,
        double authoritativePrice,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        if (viewerId == targetId)
            throw new GuideOperationException(GuideOperationFailure.InvalidArgument, "无需付费查阅自己的配装。");
        if (authoritativePrice <= 0d || authoritativePrice > MaxPriceOrTipStones)
            throw new GuideOperationException(GuideOperationFailure.InvalidArgument, "目标修士当前未开放付费查阅配装。");

        var (accessDate, nextMidnightUtc) = ResolveCstDayWindow(nowUtc);
        var (_, netAmount) = ComputeTaxAndNet(authoritativePrice);
        var dateTag = $"{accessDate.Year:D4}{accessDate.Month:D2}{accessDate.Day:D2}";
        var idempotencyKey = $"privacy.inspect:{viewerId:N}:{targetId:N}:{dateTag}";
        var txId = DeterministicGuid(idempotencyKey);

        var lockPlayers = new[] { viewerId, targetId }.Order().ToArray();
        var assetKeys = lockPlayers.Select(pid => new ParticipationAssetKey(pid, SpiritStoneAssetId, string.Empty)).ToArray();
        using var assetScope = participationAssets is not null
            ? await participationAssets.AcquireAsync(assetKeys, ct).ConfigureAwait(false)
            : null;
        var stripes = PlayerCommitGates.StripesFor(lockPlayers);
        await PlayerCommitGates.AcquireAllAsync(stripes, ct).ConfigureAwait(false);
        try
        {
            return await ExecuteSerializableAsync(async (conn, tx, token) =>
            {
                await using var checkCmd = new NpgsqlCommand("""
                    SELECT paid_stones, target_received_stones, expires_at
                    FROM social.loadout_inspect_grants
                    WHERE viewer_id = $1 AND target_id = $2 AND access_date = $3
                    FOR UPDATE
                    """, conn, tx);
                checkCmd.Parameters.Add(new NpgsqlParameter { Value = viewerId });
                checkCmd.Parameters.Add(new NpgsqlParameter { Value = targetId });
                checkCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = accessDate });
                await using (var reader = await checkCmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        var existingPaid = reader.GetDouble(0);
                        var existingNet = reader.GetDouble(1);
                        var existingExpires = reader.GetFieldValue<DateTimeOffset>(2).ToUniversalTime();
                        await reader.CloseAsync().ConfigureAwait(false);
                        await CommitTransactionAsync(tx, txId, token).ConfigureAwait(false);
                        return (new UnlockPlayerLoadoutResponse(
                            targetId.ToString("D"),
                            existingPaid,
                            existingNet,
                            existingExpires.ToUnixTimeMilliseconds() / 1000d), false);
                    }
                }

                await using (var insertCmd = new NpgsqlCommand("""
                    INSERT INTO social.loadout_inspect_grants
                        (viewer_id, target_id, access_date, paid_stones, target_received_stones, expires_at, created_at)
                    VALUES ($1, $2, $3, $4, $5, $6, $7)
                    """, conn, tx))
                {
                    insertCmd.Parameters.Add(new NpgsqlParameter { Value = viewerId });
                    insertCmd.Parameters.Add(new NpgsqlParameter { Value = targetId });
                    insertCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = accessDate });
                    insertCmd.Parameters.Add(new NpgsqlParameter { Value = authoritativePrice });
                    insertCmd.Parameters.Add(new NpgsqlParameter { Value = netAmount });
                    insertCmd.Parameters.Add(new NpgsqlParameter { Value = nextMidnightUtc });
                    insertCmd.Parameters.Add(new NpgsqlParameter { Value = nowUtc });
                    await insertCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                var deltas = BuildTransferDeltas(viewerId, targetId, authoritativePrice, netAmount);
                await PostgresEconomyRepository.CommitCoreAsync(
                    conn,
                    tx,
                    new EconomyTransactionRequest(
                        txId,
                        idempotencyKey,
                        "privacy.inspect",
                        viewerId,
                        deltas,
                        nowUtc,
                        Encoding.UTF8.GetBytes($"{{\"targetId\":\"{targetId:D}\",\"date\":\"{dateTag}\",\"paid\":{authoritativePrice},\"net\":{netAmount}}}"),
                        PersistLedger: true),
                    token).ConfigureAwait(false);

                await CommitTransactionAsync(tx, txId, token).ConfigureAwait(false);
                return (new UnlockPlayerLoadoutResponse(
                    targetId.ToString("D"),
                    authoritativePrice,
                    netAmount,
                    nextMidnightUtc.ToUnixTimeMilliseconds() / 1000d), true);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            PlayerCommitGates.ReleaseAll(stripes);
        }
    }

    private static AssetDelta[] BuildTransferDeltas(Guid payerId, Guid recipientId, double paidAmount, double recipientNetAmount)
    {
        if (recipientNetAmount > 0d)
        {
            return
            [
                new AssetDelta(payerId, SpiritStoneAssetId, string.Empty, -paidAmount),
                new AssetDelta(recipientId, SpiritStoneAssetId, string.Empty, recipientNetAmount)
            ];
        }
        return [new AssetDelta(payerId, SpiritStoneAssetId, string.Empty, -paidAmount)];
    }

    private static async ValueTask<GuidePostRow?> LoadSinglePostCoreAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid postId,
        Guid viewerId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT gp.post_id,
                   gp.author_id,
                   p.display_name,
                   COALESCE(NULLIF(proj.avatar, ''), 'player') AS avatar,
                   GREATEST(1, FLOOR(COALESCE(proj.realm_rank, 1))::integer) AS realm_level,
                   z.prefix,
                   gp.title,
                   gp.summary,
                   CASE WHEN (gp.unlock_price <= 0 OR gp.author_id = $2 OR u.viewer_id IS NOT NULL) THEN gp.body ELSE '' END AS visible_body,
                   gp.unlock_price,
                   (gp.unlock_price <= 0 OR gp.author_id = $2 OR u.viewer_id IS NOT NULL) AS is_unlocked,
                   (l.player_id IS NOT NULL) AS liked_by_me,
                   COALESCE(t.total_tipped_stones, 0) AS my_tipped,
                   gp.view_count,
                   gp.like_count,
                   gp.tipper_count,
                   gp.total_reward_stones,
                   gp.created_at,
                   gp.updated_at
            FROM social.guide_posts gp
            JOIN identity.players p ON p.player_id = gp.author_id
            JOIN identity.accounts a ON a.account_id = p.account_id
            LEFT JOIN game.player_projection proj ON proj.player_id = gp.author_id
            LEFT JOIN social.zongmen_members zm ON zm.player_id = gp.author_id
            LEFT JOIN social.zongmen z ON z.zongmen_id = zm.zongmen_id AND z.disbanded_at IS NULL
            LEFT JOIN social.guide_post_unlocks u ON u.post_id = gp.post_id AND u.viewer_id = $2
            LEFT JOIN social.guide_post_likes l ON l.post_id = gp.post_id AND l.player_id = $2
            LEFT JOIN social.guide_post_tips t ON t.post_id = gp.post_id AND t.tipper_id = $2
            WHERE gp.post_id = $1
              AND gp.deleted_at IS NULL
              AND p.status = 1 AND a.status = 1 AND NOT a.is_bot
            LIMIT 1
            """, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { Value = postId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = viewerId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return ReadPostRow(reader);
    }

    private static GuidePostRow ReadPostRow(NpgsqlDataReader reader) => new(
        PostId: reader.GetGuid(0),
        AuthorId: reader.GetGuid(1),
        AuthorName: reader.GetString(2),
        AuthorAvatar: reader.GetString(3),
        AuthorRealmLevel: reader.GetInt32(4),
        AuthorZongmenPrefix: reader.IsDBNull(5) ? null : reader.GetString(5),
        Title: reader.GetString(6),
        Summary: reader.GetString(7),
        Body: reader.GetString(8),
        UnlockPrice: reader.GetDouble(9),
        IsUnlockedByViewer: reader.GetBoolean(10),
        LikedByViewer: reader.GetBoolean(11),
        ViewerTippedStones: reader.GetDouble(12),
        ViewCount: reader.GetInt32(13),
        LikeCount: reader.GetInt32(14),
        TipperCount: reader.GetInt32(15),
        TotalRewardStones: reader.GetDouble(16),
        CreatedAt: reader.GetFieldValue<DateTimeOffset>(17).ToUniversalTime(),
        UpdatedAt: reader.GetFieldValue<DateTimeOffset>(18).ToUniversalTime());

    private static async ValueTask CommitTransactionAsync(NpgsqlTransaction tx, Guid txId, CancellationToken ct)
        => await PostgresEconomyRepository.CommitTransactionAsync(tx, txId, ct).ConfigureAwait(false);

    private async ValueTask<T> ExecuteSerializableAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, ValueTask<T>> work,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
            try
            {
                return await work(conn, tx, ct).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState is "40001" or "40P01" && attempt < SerializableRetryAttempts && !ct.IsCancellationRequested)
            {
                try { await tx.RollbackAsync(ct).ConfigureAwait(false); } catch { /* 忽略已中止事务回滚异常 */ }
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 25));
                await Task.Delay(SerializableRetryBackoff[attempt - 1] + jitter, ct).ConfigureAwait(false);
            }
            catch (EconomyInsufficientBalanceException)
            {
                try { await tx.RollbackAsync(ct).ConfigureAwait(false); } catch { }
                throw new GuideOperationException(GuideOperationFailure.InsufficientSpiritStones, "灵石余额不足。");
            }
            catch (EconomyIdempotencyConflictException)
            {
                try { await tx.RollbackAsync(ct).ConfigureAwait(false); } catch { }
                throw new GuideOperationException(GuideOperationFailure.Conflict, "请求参数与历史幂等记录冲突。");
            }
        }
    }

    private static string SanitizeRequestId(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > 64)
            throw new GuideOperationException(GuideOperationFailure.InvalidArgument, "请求标识 requestId 无效。");
        foreach (var ch in trimmed)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not ('-' or '_' or '.' or ':'))
                throw new GuideOperationException(GuideOperationFailure.InvalidArgument, "请求标识包含非法字符。");
        }
        return trimmed;
    }

    private static Guid DeterministicGuid(string key)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        Span<byte> guidBytes = stackalloc byte[16];
        hash[..16].CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
