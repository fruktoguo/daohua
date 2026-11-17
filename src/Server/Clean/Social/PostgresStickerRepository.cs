using System.Data;
using System.Text.RegularExpressions;
using Npgsql;

namespace Server.Clean.Social;

/// <summary>表情包业务异常类型。</summary>
public enum StickerOperationFailure
{
    InvalidArgument = 0,
    LimitExceeded = 1
}

/// <summary>表情包操作异常，供端点映射为明确 HTTP 状态与结构化载荷。</summary>
public sealed class StickerOperationException(
    StickerOperationFailure failure,
    string message,
    StickerSyncStateResponse? currentState = null) : Exception(message)
{
    public StickerOperationFailure Failure { get; } = failure;
    public StickerSyncStateResponse? CurrentState { get; } = currentState;
}

/// <summary>单条表情包同步 DTO。</summary>
public sealed record StickerDto(
    string StickerId,
    string Url,
    string? Sha256,
    string Mime,
    int UseCount,
    long LastUsedAt,
    long AddedAt,
    long UpdatedAt,
    bool NeedsReupload);

/// <summary>表情包软删除墓碑 DTO（用于跨端离线删改 LWW 合并）。</summary>
public sealed record StickerTombstoneDto(
    string StickerId,
    long DeletedAt);

/// <summary>客户端表情包全量同步响应。</summary>
public sealed record StickerSyncStateResponse(
    int MaxStickers,
    long ServerTimeMs,
    StickerDto[] Stickers,
    StickerTombstoneDto[] Tombstones);

/// <summary>客户端提交的表情包变更条目。</summary>
public sealed record StickerSyncUpsertItem(
    string StickerId,
    string Url,
    string? Sha256,
    string? Mime,
    int UseCount,
    long LastUsedAt,
    long AddedAt,
    long UpdatedAt,
    bool? NeedsReupload = null,
    string? BrokenUrl = null,
    string? ReplacedStickerId = null);

/// <summary>客户端表情包增量合并请求。</summary>
public sealed record StickerSyncRequest(
    StickerSyncUpsertItem[]? Items,
    StickerTombstoneDto[]? Deleted);

/// <summary>客户端表情包批量删除请求。</summary>
public sealed record StickerDeleteRequest(
    string[] StickerIds,
    long? DeletedAt = null);

/// <summary>
/// 传音自定义表情包 PostgreSQL 仓储：
/// - 独立旁路表 <c>social.player_stickers</c> 存储单角色最多 100 个收藏表情包元数据，零 ECS 与八分段侵入；
/// - 写路径采用按 <c>player_id</c> 的事务建议锁（域 29）串行化，保证跨设备并发写入严格不超 100 上限；
/// - 基于 <c>deleted_at</c> 墓碑与时间戳 LWW 实现多端离线增删合并与失效重传协同。
/// </summary>
public sealed partial class PostgresStickerRepository(NpgsqlDataSource dataSource)
{
    public const int MaxStickersPerPlayer = 100;
    /// <summary>单角色软删除墓碑总量上限：超出按删除时刻升序物理淘汰最旧墓碑（响应侧本就只回最近 200 条）。</summary>
    public const int MaxTombstonesPerPlayer = 200;
    public const int MaxUrlLength = 200;
    public const int MaxStickerIdLength = 64;

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex StickerIdRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    private sealed class MutableRow
    {
        public required string StickerId { get; init; }
        public string Url { get; set; } = string.Empty;
        public string? Sha256 { get; set; }
        public string Mime { get; set; } = string.Empty;
        public int UseCount { get; set; }
        public long LastUsedAt { get; set; }
        public long AddedAt { get; set; }
        public long UpdatedAt { get; set; }
        public bool NeedsReupload { get; set; }
        public long? DeletedAt { get; set; }
        public bool Dirty { get; set; }
    }

    /// <summary>获取角色当前云端表情包全量状态（活跃条目 + 近期墓碑）。</summary>
    public async ValueTask<StickerSyncStateResponse> GetStateAsync(
        Guid playerId,
        CancellationToken ct = default)
    {
        if (playerId == Guid.Empty)
            throw new StickerOperationException(StickerOperationFailure.InvalidArgument, "角色身份无效。");

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        var rows = await LoadRowsAsync(connection, null, playerId, forUpdate: false, ct).ConfigureAwait(false);
        return BuildResponse(rows.Values, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>按时间戳 LWW 与墓碑规则合并同步客户端表情包变更，并严格校验 100 个活跃上限。</summary>
    public async ValueTask<StickerSyncStateResponse> SyncAsync(
        Guid playerId,
        StickerSyncRequest request,
        CancellationToken ct = default)
    {
        if (playerId == Guid.Empty)
            throw new StickerOperationException(StickerOperationFailure.InvalidArgument, "角色身份无效。");
        ArgumentNullException.ThrowIfNull(request);

        var items = request.Items ?? [];
        var deleted = request.Deleted ?? [];
        if (items.Length > 200 || deleted.Length > 200)
            throw new StickerOperationException(StickerOperationFailure.InvalidArgument, "单次同步条目数超出限制。");

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await AcquirePlayerLockAsync(connection, transaction, playerId, ct).ConfigureAwait(false);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rows = await LoadRowsAsync(connection, transaction, playerId, forUpdate: true, ct).ConfigureAwait(false);
        var originalResponse = BuildResponse(rows.Values, nowMs);

        // 1. 先处理删除墓碑（防止先删后重加或乱序导致误删）
        foreach (var tomb in deleted)
        {
            var stickerId = NormalizeStickerId(tomb.StickerId);
            var deletedAt = ClampTimestamp(tomb.DeletedAt > 0 ? tomb.DeletedAt : nowMs, nowMs);
            if (rows.TryGetValue(stickerId, out var existing))
            {
                if (existing.DeletedAt is null ? deletedAt >= existing.UpdatedAt : deletedAt > existing.DeletedAt.Value)
                {
                    existing.DeletedAt = deletedAt;
                    existing.UpdatedAt = Math.Max(existing.UpdatedAt, deletedAt);
                    existing.NeedsReupload = false;
                    existing.Dirty = true;
                }
            }
            // 不为数据库中从未存在的标识创建墓碑：否则客户端反复提交随机 stickerId 即可无限膨胀墓碑行。
        }

        // 2. 处理新增与更新（含旧临时 ID -> 正式 SHA256 ID 升级合并）
        foreach (var raw in items)
        {
            var stickerId = NormalizeStickerId(raw.StickerId);
            var url = NormalizeStickerUrl(raw.Url);
            var sha256 = NormalizeOptionalSha256(raw.Sha256);
            var mime = NormalizeMime(raw.Mime);
            var useCount = Math.Max(0, raw.UseCount);
            var lastUsedAt = ClampTimestamp(raw.LastUsedAt, nowMs);
            var addedAt = ClampTimestamp(raw.AddedAt > 0 ? raw.AddedAt : nowMs, nowMs);
            var updatedAt = ClampTimestamp(raw.UpdatedAt > 0 ? raw.UpdatedAt : nowMs, nowMs);

            // 若由旧无哈希临时 ID 升级为规范 SHA-256 ID，先继承旧条目的统计并墓碑化旧 ID
            if (!string.IsNullOrWhiteSpace(raw.ReplacedStickerId))
            {
                var oldId = NormalizeStickerId(raw.ReplacedStickerId);
                if (!string.Equals(oldId, stickerId, StringComparison.Ordinal)
                    && rows.TryGetValue(oldId, out var oldRow)
                    && oldRow.DeletedAt is null)
                {
                    useCount = Math.Max(useCount, oldRow.UseCount);
                    lastUsedAt = Math.Max(lastUsedAt, oldRow.LastUsedAt);
                    addedAt = Math.Min(addedAt, oldRow.AddedAt);
                    oldRow.DeletedAt = Math.Max(updatedAt, nowMs);
                    oldRow.UpdatedAt = Math.Max(oldRow.UpdatedAt, oldRow.DeletedAt.Value);
                    oldRow.NeedsReupload = false;
                    oldRow.Dirty = true;
                }
            }

            if (rows.TryGetValue(stickerId, out var existing))
            {
                // 若另一端在该修改之后已删除，尊重墓碑不复活
                if (existing.DeletedAt is not null && updatedAt <= existing.DeletedAt.Value)
                    continue;

                if (existing.DeletedAt is not null && updatedAt > existing.DeletedAt.Value)
                {
                    existing.DeletedAt = null;
                    existing.Url = url;
                    existing.Sha256 = sha256 ?? existing.Sha256;
                    existing.Mime = string.IsNullOrEmpty(mime) ? existing.Mime : mime;
                    existing.NeedsReupload = raw.NeedsReupload ?? false;
                    existing.UpdatedAt = updatedAt;
                    existing.AddedAt = Math.Min(existing.AddedAt, addedAt);
                    existing.UseCount = Math.Max(existing.UseCount, useCount);
                    existing.LastUsedAt = Math.Max(existing.LastUsedAt, lastUsedAt);
                    existing.Dirty = true;
                    continue;
                }

                // 失效自愈协同：仅当上报损坏的 URL 仍等于当前云端 URL 时标记待补传；
                // 若当前云端 URL 已是其他端补传后的新地址，绝不能把旧损坏报告当成常规更新回滚 URL。
                if (raw.NeedsReupload == true && !string.IsNullOrWhiteSpace(raw.BrokenUrl))
                {
                    if (string.Equals(existing.Url, raw.BrokenUrl.Trim(), StringComparison.Ordinal) && !existing.NeedsReupload)
                    {
                        existing.NeedsReupload = true;
                        existing.UpdatedAt = Math.Max(existing.UpdatedAt, updatedAt);
                        existing.Dirty = true;
                    }
                }
                else if (updatedAt > existing.UpdatedAt)
                {
                    existing.Url = url;
                    if (sha256 is not null) existing.Sha256 = sha256;
                    if (!string.IsNullOrEmpty(mime)) existing.Mime = mime;
                    existing.NeedsReupload = raw.NeedsReupload ?? false;
                    existing.UpdatedAt = updatedAt;
                    existing.Dirty = true;
                }
                else
                {
                    if (existing.Sha256 is null && sha256 is not null)
                    {
                        existing.Sha256 = sha256;
                        existing.Dirty = true;
                    }
                    if (string.IsNullOrEmpty(existing.Mime) && !string.IsNullOrEmpty(mime))
                    {
                        existing.Mime = mime;
                        existing.Dirty = true;
                    }
                }

                if (useCount > existing.UseCount)
                {
                    existing.UseCount = useCount;
                    existing.Dirty = true;
                }
                if (lastUsedAt > existing.LastUsedAt)
                {
                    existing.LastUsedAt = lastUsedAt;
                    existing.Dirty = true;
                }
                if (addedAt < existing.AddedAt)
                {
                    existing.AddedAt = addedAt;
                    existing.Dirty = true;
                }
            }
            else
            {
                rows[stickerId] = new MutableRow
                {
                    StickerId = stickerId,
                    Url = url,
                    Sha256 = sha256,
                    Mime = mime,
                    UseCount = useCount,
                    LastUsedAt = lastUsedAt,
                    AddedAt = addedAt,
                    UpdatedAt = updatedAt,
                    NeedsReupload = raw.NeedsReupload ?? false,
                    DeletedAt = null,
                    Dirty = true
                };
            }
        }

        // 3. 严格检查单角色活跃数量上限（100 个，不静默删旧）
        var activeCount = rows.Values.Count(r => r.DeletedAt is null);
        if (activeCount > MaxStickersPerPlayer)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw new StickerOperationException(
                StickerOperationFailure.LimitExceeded,
                $"表情包最多支持保存 {MaxStickersPerPlayer} 个，请先删除部分后再试。",
                originalResponse);
        }

        await EnforceTombstoneCapAsync(connection, transaction, playerId, rows, ct).ConfigureAwait(false);
        await PersistDirtyRowsAsync(connection, transaction, playerId, rows.Values, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return BuildResponse(rows.Values, nowMs);
    }

    /// <summary>批量删除表情包（写入软删除墓碑，保障离线设备同步时正确删除）。</summary>
    public async ValueTask<StickerSyncStateResponse> DeleteAsync(
        Guid playerId,
        StickerDeleteRequest request,
        CancellationToken ct = default)
    {
        if (playerId == Guid.Empty)
            throw new StickerOperationException(StickerOperationFailure.InvalidArgument, "角色身份无效。");
        ArgumentNullException.ThrowIfNull(request);
        if (request.StickerIds is null || request.StickerIds.Length == 0 || request.StickerIds.Length > 200)
            throw new StickerOperationException(StickerOperationFailure.InvalidArgument, "待删除表情包列表无效。");

        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await AcquirePlayerLockAsync(connection, transaction, playerId, ct).ConfigureAwait(false);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var deletedAt = ClampTimestamp(request.DeletedAt is > 0 ? request.DeletedAt.Value : nowMs, nowMs);
        var rows = await LoadRowsAsync(connection, transaction, playerId, forUpdate: true, ct).ConfigureAwait(false);

        foreach (var rawId in request.StickerIds)
        {
            var stickerId = NormalizeStickerId(rawId);
            if (rows.TryGetValue(stickerId, out var existing))
            {
                existing.DeletedAt = Math.Max(existing.DeletedAt ?? 0, deletedAt);
                existing.UpdatedAt = Math.Max(existing.UpdatedAt, existing.DeletedAt.Value);
                existing.NeedsReupload = false;
                existing.Dirty = true;
            }
            // 不存在的服务端条目无需落墓碑：不存在的东西本来就没有可删状态。
        }

        await EnforceTombstoneCapAsync(connection, transaction, playerId, rows, ct).ConfigureAwait(false);
        await PersistDirtyRowsAsync(connection, transaction, playerId, rows.Values, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return BuildResponse(rows.Values, nowMs);
    }

    private static async Task AcquirePlayerLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playerId,
        CancellationToken ct)
    {
        // 使用独立建议锁域 29 串行化单角色表情包读写，避免多端并发写入突破 100 个上限。
        await using var cmd = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 29))",
            connection,
            transaction);
        cmd.Parameters.AddWithValue(playerId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, MutableRow>> LoadRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid playerId,
        bool forUpdate,
        CancellationToken ct)
    {
        var sql = forUpdate
            ? """
              SELECT sticker_id, url, sha256, mime, use_count, last_used_at, added_at, updated_at, needs_reupload, deleted_at
              FROM social.player_stickers
              WHERE player_id = $1
              FOR UPDATE
              """
            : """
              SELECT sticker_id, url, sha256, mime, use_count, last_used_at, added_at, updated_at, needs_reupload, deleted_at
              FROM social.player_stickers
              WHERE player_id = $1
              """;

        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        cmd.Parameters.AddWithValue(playerId);

        var result = new Dictionary<string, MutableRow>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var stickerId = reader.GetString(0);
            result[stickerId] = new MutableRow
            {
                StickerId = stickerId,
                Url = reader.GetString(1),
                Sha256 = reader.IsDBNull(2) ? null : reader.GetString(2),
                Mime = reader.GetString(3),
                UseCount = reader.GetInt32(4),
                LastUsedAt = reader.GetInt64(5),
                AddedAt = reader.GetInt64(6),
                UpdatedAt = reader.GetInt64(7),
                NeedsReupload = reader.GetBoolean(8),
                DeletedAt = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                Dirty = false
            };
        }
        return result;
    }

    /// <summary>单角色墓碑总量设限：超出 <see cref="MaxTombstonesPerPlayer"/> 时按删除时刻升序物理淘汰最旧墓碑。</summary>
    private static async Task EnforceTombstoneCapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playerId,
        Dictionary<string, MutableRow> rows,
        CancellationToken ct)
    {
        var tombstoneCount = rows.Values.Count(r => r.DeletedAt is not null);
        if (tombstoneCount <= MaxTombstonesPerPlayer) return;
        var evictIds = rows.Values
            .Where(r => r.DeletedAt is not null)
            .OrderBy(r => r.DeletedAt!.Value)
            .Take(tombstoneCount - MaxTombstonesPerPlayer)
            .Select(r => r.StickerId)
            .ToArray();
        foreach (var id in evictIds) rows.Remove(id);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM social.player_stickers WHERE player_id = $1 AND sticker_id = ANY($2)",
            connection,
            transaction);
        cmd.Parameters.AddWithValue(playerId);
        cmd.Parameters.AddWithValue(evictIds);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task PersistDirtyRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid playerId,
        IEnumerable<MutableRow> rows,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO social.player_stickers (
                player_id, sticker_id, url, sha256, mime,
                use_count, last_used_at, added_at, updated_at,
                needs_reupload, deleted_at
            )
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            ON CONFLICT (player_id, sticker_id) DO UPDATE SET
                url = EXCLUDED.url,
                sha256 = EXCLUDED.sha256,
                mime = EXCLUDED.mime,
                use_count = EXCLUDED.use_count,
                last_used_at = EXCLUDED.last_used_at,
                added_at = EXCLUDED.added_at,
                updated_at = EXCLUDED.updated_at,
                needs_reupload = EXCLUDED.needs_reupload,
                deleted_at = EXCLUDED.deleted_at
            """;

        foreach (var row in rows)
        {
            if (!row.Dirty) continue;
            await using var cmd = new NpgsqlCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue(playerId);
            cmd.Parameters.AddWithValue(row.StickerId);
            cmd.Parameters.AddWithValue(row.Url);
            cmd.Parameters.AddWithValue((object?)row.Sha256 ?? DBNull.Value);
            cmd.Parameters.AddWithValue(row.Mime);
            cmd.Parameters.AddWithValue(row.UseCount);
            cmd.Parameters.AddWithValue(row.LastUsedAt);
            cmd.Parameters.AddWithValue(row.AddedAt);
            cmd.Parameters.AddWithValue(row.UpdatedAt);
            cmd.Parameters.AddWithValue(row.NeedsReupload);
            cmd.Parameters.AddWithValue((object?)row.DeletedAt ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static StickerSyncStateResponse BuildResponse(IEnumerable<MutableRow> rows, long serverTimeMs)
    {
        var active = new List<StickerDto>();
        var tombstones = new List<StickerTombstoneDto>();

        foreach (var row in rows)
        {
            if (row.DeletedAt is not null)
            {
                tombstones.Add(new StickerTombstoneDto(row.StickerId, row.DeletedAt.Value));
            }
            else
            {
                active.Add(new StickerDto(
                    row.StickerId,
                    row.Url,
                    row.Sha256,
                    row.Mime,
                    row.UseCount,
                    row.LastUsedAt,
                    row.AddedAt,
                    row.UpdatedAt,
                    row.NeedsReupload));
            }
        }

        active.Sort(static (a, b) =>
        {
            var cmp = b.UseCount.CompareTo(a.UseCount);
            if (cmp != 0) return cmp;
            cmp = b.LastUsedAt.CompareTo(a.LastUsedAt);
            if (cmp != 0) return cmp;
            return b.AddedAt.CompareTo(a.AddedAt);
        });
        tombstones.Sort(static (a, b) => b.DeletedAt.CompareTo(a.DeletedAt));

        return new StickerSyncStateResponse(
            MaxStickersPerPlayer,
            serverTimeMs,
            active.ToArray(),
            tombstones.Take(200).ToArray());
    }

    private static string NormalizeStickerId(string? raw)
    {
        var trimmed = raw?.Trim() ?? string.Empty;
        if (!StickerIdRegex().IsMatch(trimmed))
            throw new StickerOperationException(StickerOperationFailure.InvalidArgument, "表情包标识格式无效。");
        return trimmed;
    }

    private static string NormalizeStickerUrl(string? raw)
    {
        var trimmed = raw?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxUrlLength)
            throw new StickerOperationException(StickerOperationFailure.InvalidArgument, "表情包链接长度无效（上限 200 字符）。");

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new StickerOperationException(StickerOperationFailure.InvalidArgument, "表情包链接必须为有效 HTTP/HTTPS 地址。");
        }

        return trimmed;
    }

    private static string? NormalizeOptionalSha256(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = raw.Trim().ToLowerInvariant();
        if (!Sha256Regex().IsMatch(normalized))
            throw new StickerOperationException(StickerOperationFailure.InvalidArgument, "表情包内容摘要格式无效。");
        return normalized;
    }

    private static string NormalizeMime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var clean = raw.Trim().ToLowerInvariant();
        return clean.Length <= 32 ? clean : clean[..32];
    }

    private static long ClampTimestamp(long candidate, long serverNowMs)
    {
        if (candidate <= 0) return 0;
        // 容忍设备 5 分钟轻微时钟漂移，防止未来伪造时间戳锁死合并
        var upperBound = serverNowMs + 300_000L;
        return Math.Min(candidate, upperBound);
    }
}
