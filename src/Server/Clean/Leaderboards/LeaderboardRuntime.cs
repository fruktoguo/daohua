using System.Collections.Frozen;
using System.Diagnostics;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Domain.World;
using Microsoft.Extensions.Primitives;
using Npgsql;
using NpgsqlTypes;
using Protocol;
using Server.Clean.Social;

namespace Server.Clean.Leaderboards;

/// <summary>
/// 全服排行榜内存缓存与 PostgreSQL 物化服务（派生读模型）：
/// 维护 12 张榜单的预序列化 UTF-8 基础 JSON 载荷与全服名次哈希表，实现 API 零分配/近零开销直读；
/// 整点重建在信号量保护下全库采集、补算离线身家与灵根上限、一次性原子落库后切换内存引用。
/// </summary>
public sealed class LeaderboardRuntime
{
    private static readonly byte[] s_myRankPropertyPrefix = ",\"myRank\":"u8.ToArray();

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresSectionRepository _sections;
    private readonly IPlayerRuntime _players;
    private readonly MarketRuntime? _market;
    private readonly GameDataCatalog _gameData;
    private readonly FrozenDictionary<string, double> _baseSellPrices;
    private readonly ILogger<LeaderboardRuntime> _logger;
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);

    private volatile CachedLeaderboardState? _state;
    private bool _brJsonRepairAttempted;
    private double _lastRebuildDurationMs;

    /// <summary>构造全服排行榜运行时。</summary>
    public LeaderboardRuntime(
        NpgsqlDataSource dataSource,
        PostgresSectionRepository sections,
        IPlayerRuntime players,
        MarketRuntime? market,
        GameDataCatalog gameData,
        ILogger<LeaderboardRuntime> logger)
    {
        _dataSource = dataSource;
        _sections = sections;
        _players = players;
        _market = market;
        _gameData = gameData;
        _baseSellPrices = ValuationMath.BuildBaseSellPrices(gameData);
        _logger = logger;
    }

    /// <summary>当前是否已完成至少一次整点或重启后恢复快照。</summary>
    public bool HasSnapshot => _state is not null;

    /// <summary>最近一次生效快照的权威生成时刻；未就绪为 null。</summary>
    public DateTimeOffset? ComputedAt => _state?.ComputedAt;

    /// <summary>直接判断总览元数据的 If-None-Match 命中与返回（未就绪返回 StatusCode=503）。</summary>
    public LeaderboardHttpPayload? TryGetIndex(StringValues ifNoneMatch)
    {
        var state = _state;
        if (state is null) return null;

        if (MatchesIfNoneMatch(ifNoneMatch, state.IndexETag))
            return new LeaderboardHttpPayload(NotModified: true, state.IndexETag, ReadOnlyMemory<byte>.Empty);

        return new LeaderboardHttpPayload(NotModified: false, state.IndexETag, state.IndexJsonUtf8);
    }

    /// <summary>按榜单 ID 与认证玩家 ID 获取预序列化 JSON（仅当该玩家上榜时在尾部零拷贝拼接 myRank）。</summary>
    public LeaderboardHttpPayload? TryGetBoard(string boardId, Guid? playerId, StringValues ifNoneMatch)
    {
        var state = _state;
        if (state is null || !state.Boards.TryGetValue(boardId, out var board))
            return null;

        // 将 playerId 纳入 ETag（配合 Vary: Cookie），保证同浏览器切号后绝不复用他人 myRank 的 304 缓存。
        var viewerTag = playerId is { } pid ? pid.ToString("N") : "anon";
        var etag = $"W/\"lb-{boardId}-{state.ComputedAtUnixMs}-{viewerTag}\"";

        if (MatchesIfNoneMatch(ifNoneMatch, etag))
            return new LeaderboardHttpPayload(NotModified: true, etag, ReadOnlyMemory<byte>.Empty);

        if (playerId is null || !board.MyRanks.TryGetValue(playerId.Value, out var myRank))
            return new LeaderboardHttpPayload(NotModified: false, etag, board.FullBaseJsonUtf8);

        var myRankBytes = JsonSerializer.SerializeToUtf8Bytes(myRank, ProtocolJson.Options);
        var prefix = board.BasePrefixUtf8;
        var combined = new byte[prefix.Length + s_myRankPropertyPrefix.Length + myRankBytes.Length + 1];
        prefix.Span.CopyTo(combined);
        s_myRankPropertyPrefix.CopyTo(combined.AsSpan(prefix.Length));
        myRankBytes.CopyTo(combined.AsSpan(prefix.Length + s_myRankPropertyPrefix.Length));
        combined[^1] = (byte)'}';
        return new LeaderboardHttpPayload(NotModified: false, etag, combined);
    }

    /// <summary>供运维面板展示当前排行榜物化状态。</summary>
    public object GetStatus()
    {
        var state = _state;
        var now = DateTimeOffset.UtcNow;
        return state is null
            ? new
            {
                ready = false,
                boardCount = LeaderboardAssembler.CanonicalBoards.Count,
                computedAt = (DateTimeOffset?)null,
                nextComputeAt = LeaderboardAssembler.FloorToHourUtc(now).AddHours(1),
                totalRankedEntries = 0,
                lastDurationMs = Math.Round(_lastRebuildDurationMs, 1),
            }
            : new
            {
                ready = true,
                boardCount = state.Boards.Count,
                computedAt = (DateTimeOffset?)state.ComputedAt,
                nextComputeAt = state.NextComputeAt,
                totalRankedEntries = state.TotalRankRows,
                lastDurationMs = Math.Round(_lastRebuildDurationMs, 1),
            };
    }

    /// <summary>
    /// 进程重启快速预热：若库中已有 12 张同当前整点小时窗口内的派生快照，则直接恢复内存缓存并跳过冷重建；
    /// 若缺失或已跨越整点，则返回 false 交由 Worker 立即重建。
    /// </summary>
    public async ValueTask<bool> TryRestoreRecentFromDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await _rebuildLock.WaitAsync(cancellationToken);
        try
        {
            if (_state is not null) return true;

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            var snapshots = new Dictionary<string, (DateTimeOffset ComputedAt, LeaderboardSnapshotDto Dto)>(
                LeaderboardAssembler.CanonicalBoards.Count, StringComparer.Ordinal);

            await using (var command = new NpgsqlCommand("""
                SELECT board_id, computed_at, payload::text
                FROM game.leaderboard_snapshots
                """, connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var boardId = reader.GetString(0);
                    if (!LeaderboardAssembler.IsKnownBoard(boardId)) continue;
                    var computedAt = reader.GetFieldValue<DateTimeOffset>(1).ToUniversalTime();
                    var json = reader.GetString(2);
                    var dto = JsonSerializer.Deserialize<LeaderboardSnapshotDto>(json, ProtocolJson.Options);
                    if (dto is null) return false;
                    snapshots[boardId] = (computedAt, dto with { MyRank = null });
                }
            }

            if (snapshots.Count != LeaderboardAssembler.CanonicalBoards.Count)
                return false;

            var currentHour = LeaderboardAssembler.FloorToHourUtc(DateTimeOffset.UtcNow);
            var oldestComputedAt = snapshots.Values.Min(item => item.ComputedAt);
            if (LeaderboardAssembler.FloorToHourUtc(oldestComputedAt) < currentHour)
                return false;

            var latestComputedAt = snapshots.Values.Max(item => item.ComputedAt);
            var nextComputeAt = LeaderboardAssembler.FloorToHourUtc(latestComputedAt).AddHours(1);

            var rankMaps = new Dictionary<string, Dictionary<Guid, LeaderboardMyRankDto>>(
                LeaderboardAssembler.CanonicalBoards.Count, StringComparer.Ordinal);
            foreach (var desc in LeaderboardAssembler.CanonicalBoards)
                rankMaps[desc.BoardId] = new Dictionary<Guid, LeaderboardMyRankDto>();

            var totalRankRows = 0;
            await using (var rankCmd = new NpgsqlCommand("""
                SELECT r.player_id, r.board_id, r.rank, r.score, COALESCE(p.duel_losses, 0)
                FROM game.leaderboard_ranks r
                LEFT JOIN game.player_projection p ON p.player_id = r.player_id
                """, connection))
            await using (var reader = await rankCmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var playerId = reader.GetGuid(0);
                    var boardId = reader.GetString(1);
                    if (!rankMaps.TryGetValue(boardId, out var map)) continue;

                    var rank = reader.GetInt32(2);
                    var score = reader.GetDouble(3);
                    var duelLosses = reader.GetDouble(4);

                    var myRank = boardId == LeaderboardBoardIds.Duel
                        ? new LeaderboardMyRankDto(rank, score, DuelLosses: duelLosses)
                        : boardId.StartsWith("technique.", StringComparison.Ordinal)
                            ? new LeaderboardMyRankDto(
                                rank,
                                score,
                                TechniqueLevel: (int)Math.Floor(TechniqueCatalog.LevelFor(score, _gameData.RealmProgression)))
                            : new LeaderboardMyRankDto(rank, score);

                    map[playerId] = myRank;
                    totalRankRows++;
                }
            }

            var summaries = new List<LeaderboardBoardSummaryDto>(LeaderboardAssembler.CanonicalBoards.Count);
            var cachedBoards = new Dictionary<string, CachedBoard>(LeaderboardAssembler.CanonicalBoards.Count, StringComparer.Ordinal);
            foreach (var desc in LeaderboardAssembler.CanonicalBoards)
            {
                var dto = snapshots[desc.BoardId].Dto;
                var normalizedDto = dto with { ComputedAt = latestComputedAt, NextComputeAt = nextComputeAt, MyRank = null };
                var topScore = normalizedDto.Entries.Count > 0 ? (double?)normalizedDto.Entries[0].Score : null;
                summaries.Add(new LeaderboardBoardSummaryDto(desc.BoardId, desc.Kind, desc.TechniqueId, normalizedDto.ParticipantCount, topScore));
                cachedBoards[desc.BoardId] = CreateCachedBoard(desc.BoardId, normalizedDto, rankMaps[desc.BoardId].ToFrozenDictionary());
            }

            var indexDto = new LeaderboardIndexDto(latestComputedAt, nextComputeAt, summaries);
            _state = CreateState(latestComputedAt, nextComputeAt, indexDto, cachedBoards.ToFrozenDictionary(StringComparer.Ordinal), totalRankRows);
            _logger.LogInformation(
                "排行榜已从数据库恢复当期整点快照：computedAt={ComputedAt} ranks={RankRows}",
                latestComputedAt, totalRankRows);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "排行榜启动恢复失败，将回退为立即全量重建。");
            return false;
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    /// <summary>执行一轮全服排行榜物化：采集权威状态、批量解码离线分段、汇编 12 榜并原子落库。</summary>
    public async ValueTask RebuildAsync(CancellationToken cancellationToken = default)
    {
        await _rebuildLock.WaitAsync(cancellationToken);
        var sw = Stopwatch.StartNew();
        try
        {
            var computedAt = DateTimeOffset.UtcNow;

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

            // 兼容从 v53 迁移上来且载荷为 Brotli 压缩（br-json）的老存档：首轮补齐投影新列，避免整点快照误读 0 值。
            if (!_brJsonRepairAttempted)
            {
                await RepairCompressedProjectionsAsync(connection, cancellationToken);
                _brJsonRepairAttempted = true;
            }

            // 1. 全量读取有效玩家与投影列
            var players = await LoadBasePlayersAsync(connection, cancellationToken);
            // 2. 全量读取正余额灵石
            var spiritStones = await LoadSpiritStonesAsync(connection, cancellationToken);
            // 3. 全量读取活跃宗门、成员映射与驻地大殿工程
            var zongmens = await LoadZongmensAsync(connection, computedAt, cancellationToken);

            // 4. 叠加当前已装载在线/挂机玩家的内存权威状态（确保比 DB 更鲜活）
            var loadedSnapshots = await _players.GetLoadedSnapshotsAsync(cancellationToken);
            var loadedById = new Dictionary<Guid, PlayerRuntimeSnapshot>(loadedSnapshots.Count);
            foreach (var snap in loadedSnapshots)
            {
                loadedById[snap.PlayerId] = snap;
            }

            // 先定位离线玩家 ID，分片（500 人一批）读取 loadout 分段算身家 + 7 项技艺，控制 2c4g 内存峰值
            var offlinePlayerIds = new List<Guid>(Math.Max(0, players.Count - loadedById.Count));
            foreach (var playerId in players.Keys)
            {
                if (!loadedById.ContainsKey(playerId))
                    offlinePlayerIds.Add(playerId);
            }

            var offlineValuations = new Dictionary<Guid, ValuationResult>(offlinePlayerIds.Count);
            var offlineTechniques = new Dictionary<Guid, IReadOnlyDictionary<string, double>>(offlinePlayerIds.Count);
            const int batchSize = 500;
            for (var offset = 0; offset < offlinePlayerIds.Count; offset += batchSize)
            {
                var sliceCount = Math.Min(batchSize, offlinePlayerIds.Count - offset);
                var batchIds = offlinePlayerIds.GetRange(offset, sliceCount);
                var sections = await _sections.LoadSectionsAsync(batchIds, PlayerSectionKinds.Loadout, cancellationToken);
                foreach (var (pid, stored) in sections)
                {
                    try
                    {
                        var loadout = SectionCodec.DecodeLoadout(stored);
                        offlineValuations[pid] = ValuationMath.Compute(loadout, _gameData, _baseSellPrices, _market);
                        if (loadout.TechniqueExperiences is { Count: > 0 })
                            offlineTechniques[pid] = loadout.TechniqueExperiences;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or JsonException)
                    {
                        _logger.LogWarning(ex, "排行榜跳过损坏的离线 loadout 分段：playerId={PlayerId}", pid);
                    }
                }
            }

            // 5. 构建基础 LeaderboardPlayerInput 列表，并叠加在线玩家权威快照
            var inputs = new List<LeaderboardPlayerInput>(players.Count);
            foreach (var (playerId, row) in players)
            {
                var displayName = row.DisplayName;
                var avatar = row.Avatar;
                var realmLevel = row.RealmLevel;
                var experience = row.Experience;
                var duelWins = row.DuelWins;
                var duelLosses = row.DuelLosses;
                var stones = spiritStones.GetValueOrDefault(playerId);
                double? expToNext = null;
                ValuationResult valuation;
                IReadOnlyDictionary<string, double>? techniqueExps;

                if (loadedById.TryGetValue(playerId, out var online))
                {
                    displayName = online.DisplayName;
                    avatar = string.IsNullOrWhiteSpace(online.State.Settings.Avatar) ? avatar : online.State.Settings.Avatar;
                    realmLevel = Math.Max(1, online.State.Progression.RealmLevel);
                    experience = Math.Max(0, online.State.Progression.Experience);
                    duelWins = Math.Max(0, online.State.Progression.DuelWins);
                    duelLosses = Math.Max(0, online.State.Progression.DuelLosses);
                    expToNext = HeavenGateRules.ResolveExpCapWithLinggen(
                        realmLevel,
                        online.State.Progression.HeavenGateConfirmed ?? LinggenValues.Empty,
                        online.State.Progression.HeavenGateEntered,
                        _gameData.RealmProgression);

                    var onlineStones = FindSpiritStones(online.Assets);
                    if (onlineStones is { } os) stones = os;

                    valuation = ValuationMath.Compute(online.State.Loadout, _gameData, _baseSellPrices, _market);
                    techniqueExps = online.State.Loadout.TechniqueExperiences;
                }
                else
                {
                    valuation = offlineValuations.GetValueOrDefault(playerId);
                    techniqueExps = offlineTechniques.GetValueOrDefault(playerId);
                }

                inputs.Add(new LeaderboardPlayerInput(
                    playerId,
                    row.PlayerSeq,
                    displayName,
                    avatar,
                    realmLevel,
                    experience,
                    expToNext,
                    valuation.Equipment,
                    valuation.Abilities,
                    valuation.Tools,
                    stones,
                    duelWins,
                    duelLosses,
                    techniqueExps));
            }

            // 6. 仅针对落入境界榜 Top 50 且处于离线的玩家，再补充精确灵根破镜乘区（最多 50 行，几乎零代价）
            var realmTopIds = LeaderboardAssembler.SelectRealmTopPlayerIds(inputs);
            var offlineRealmTopIds = new List<Guid>(realmTopIds.Count);
            foreach (var id in realmTopIds)
            {
                if (!loadedById.ContainsKey(id))
                    offlineRealmTopIds.Add(id);
            }
            if (offlineRealmTopIds.Count > 0)
            {
                var progSections = await _sections.LoadSectionsAsync(offlineRealmTopIds, PlayerSectionKinds.Progression, cancellationToken);
                var offlineExpToNext = new Dictionary<Guid, double>(progSections.Count);
                foreach (var (pid, stored) in progSections)
                {
                    try
                    {
                        var prog = SectionCodec.DecodeProgression(stored);
                        offlineExpToNext[pid] = HeavenGateRules.ResolveExpCapWithLinggen(
                            Math.Max(1, prog.RealmLevel),
                            prog.HeavenGateConfirmed ?? LinggenValues.Empty,
                            prog.HeavenGateEntered,
                            _gameData.RealmProgression);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or JsonException)
                    {
                        _logger.LogWarning(ex, "排行榜跳过损坏的离线 progression 分段：playerId={PlayerId}", pid);
                    }
                }
                if (offlineExpToNext.Count > 0)
                {
                    for (var i = 0; i < inputs.Count; i++)
                    {
                        if (offlineExpToNext.TryGetValue(inputs[i].PlayerId, out var cap))
                            inputs[i] = inputs[i] with { ExperienceToNext = cap };
                    }
                }
            }

            // 7. 纯确定性汇编 12 张榜单
            var assembly = LeaderboardAssembler.Assemble(computedAt, inputs, zongmens, _gameData.RealmProgression);

            var state = BuildCachedState(assembly, out var cachedBoards);

            // 8. 事务内原子写入快照表 + BINARY COPY 名次表
            await PersistAssemblyAsync(connection, assembly, cachedBoards, cancellationToken);

            // 9. 一切落库成功后再原子切换内存快照
            _state = state;

            sw.Stop();
            _lastRebuildDurationMs = sw.Elapsed.TotalMilliseconds;
            _logger.LogInformation(
                "全服排行榜整点物化完成：players={PlayerCount} zongmens={ZongmenCount} ranks={RankRows} elapsedMs={ElapsedMs:F1}",
                inputs.Count, zongmens.Count, state.TotalRankRows, _lastRebuildDurationMs);
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private async ValueTask RepairCompressedProjectionsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var repairProgIds = new List<Guid>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT s.player_id
            FROM game.player_state_sections s
            LEFT JOIN game.player_projection p ON p.player_id = s.player_id
            WHERE s.section_kind = 'progression'
              AND s.codec = 'br-json'
              AND COALESCE(p.lifetime_experience, 0) = 0
            """, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                repairProgIds.Add(reader.GetGuid(0));
        }

        if (repairProgIds.Count > 0)
        {
            var sections = await _sections.LoadSectionsAsync(repairProgIds, PlayerSectionKinds.Progression, ct);
            foreach (var (pid, stored) in sections)
            {
                var (realmRank, exp, lifetime, wins, losses) = PostgresSectionRepository.ReadProgressionProjection(stored);
                if (realmRank is null && exp is null && lifetime is null && wins is null && losses is null) continue;
                await using var up = new NpgsqlCommand("""
                    UPDATE game.player_projection
                    SET realm_rank = COALESCE($2, realm_rank),
                        experience = COALESCE($3, experience),
                        lifetime_experience = COALESCE($4, lifetime_experience),
                        duel_wins = COALESCE($5, duel_wins),
                        duel_losses = COALESCE($6, duel_losses)
                    WHERE player_id = $1
                    """, connection);
                up.Parameters.Add(new NpgsqlParameter { Value = pid });
                up.Parameters.Add(new NpgsqlParameter { Value = (object?)realmRank ?? DBNull.Value });
                up.Parameters.Add(new NpgsqlParameter { Value = (object?)exp ?? DBNull.Value });
                up.Parameters.Add(new NpgsqlParameter { Value = (object?)lifetime ?? DBNull.Value });
                up.Parameters.Add(new NpgsqlParameter { Value = (object?)wins ?? DBNull.Value });
                up.Parameters.Add(new NpgsqlParameter { Value = (object?)losses ?? DBNull.Value });
                await up.ExecuteNonQueryAsync(ct);
            }
        }

        var repairSettingsIds = new List<Guid>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT s.player_id
            FROM game.player_state_sections s
            LEFT JOIN game.player_projection p ON p.player_id = s.player_id
            WHERE s.section_kind = 'settings'
              AND s.codec = 'br-json'
              AND COALESCE(NULLIF(p.avatar, ''), 'player') = 'player'
            """, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                repairSettingsIds.Add(reader.GetGuid(0));
        }

        if (repairSettingsIds.Count > 0)
        {
            var sections = await _sections.LoadSectionsAsync(repairSettingsIds, PlayerSectionKinds.Settings, ct);
            foreach (var (pid, stored) in sections)
            {
                var (_, _, avatar) = PostgresSectionRepository.ReadSettingsProjection(stored);
                if (string.IsNullOrWhiteSpace(avatar) || avatar == PlayerSettingsState.DefaultFemaleAvatar) continue;
                await using var up = new NpgsqlCommand("""
                    UPDATE game.player_projection
                    SET avatar = $2
                    WHERE player_id = $1
                    """, connection);
                up.Parameters.Add(new NpgsqlParameter { Value = pid });
                up.Parameters.Add(new NpgsqlParameter { Value = avatar });
                await up.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static async ValueTask<Dictionary<Guid, PlayerDbRow>> LoadBasePlayersAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var result = new Dictionary<Guid, PlayerDbRow>();
        await using var command = new NpgsqlCommand("""
            SELECT i.player_id,
                   i.player_seq,
                   i.display_name,
                   COALESCE(p.realm_rank, 1),
                   COALESCE(p.experience, 0),
                   COALESCE(p.duel_wins, 0),
                   COALESCE(p.duel_losses, 0),
                   COALESCE(NULLIF(p.avatar, ''), 'player')
            FROM identity.players i
            JOIN identity.accounts a ON a.account_id = i.account_id
            LEFT JOIN game.player_projection p ON p.player_id = i.player_id
            WHERE i.status = 1 AND NOT a.is_bot
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            result[id] = new PlayerDbRow(
                PlayerSeq: reader.GetInt64(1),
                DisplayName: reader.GetString(2),
                RealmLevel: Math.Max(1, (int)Math.Floor(reader.GetDouble(3))),
                Experience: Math.Max(0, reader.GetDouble(4)),
                DuelWins: Math.Max(0, reader.GetDouble(5)),
                DuelLosses: Math.Max(0, reader.GetDouble(6)),
                Avatar: reader.GetString(7));
        }
        return result;
    }

    private static async ValueTask<Dictionary<Guid, double>> LoadSpiritStonesAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var result = new Dictionary<Guid, double>();
        await using var command = new NpgsqlCommand("""
            SELECT player_id, quantity
            FROM economy.player_asset_balances
            WHERE asset_id = $1 AND variant = '' AND quantity > 0
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = EnhancementRules.SpiritStoneItemId });
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetGuid(0)] = reader.GetDouble(1);
        }
        return result;
    }

    private async ValueTask<List<LeaderboardZongmenInput>> LoadZongmensAsync(
        NpgsqlConnection connection,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var headers = new Dictionary<Guid, (string Name, string Prefix, DateTimeOffset CreatedAt)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT zongmen_id, name, prefix, created_at
            FROM social.zongmen
            WHERE disbanded_at IS NULL
            """, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                headers[reader.GetGuid(0)] = (reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
            }
        }
        if (headers.Count == 0) return [];

        var membersByZongmen = new Dictionary<Guid, List<Guid>>(headers.Count);
        await using (var cmd = new NpgsqlCommand("""
            SELECT zm.zongmen_id, zm.player_id
            FROM social.zongmen_members zm
            JOIN identity.players i ON i.player_id = zm.player_id
            JOIN identity.accounts a ON a.account_id = i.account_id
            WHERE i.status = 1 AND NOT a.is_bot
            """, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var zid = reader.GetGuid(0);
                if (!headers.ContainsKey(zid)) continue;
                if (!membersByZongmen.TryGetValue(zid, out var list))
                    membersByZongmen[zid] = list = [];
                list.Add(reader.GetGuid(1));
            }
        }

        var hallLevels = new Dictionary<Guid, int>(headers.Count);
        var hallDef = _gameData.Buildings.GetValueOrDefault(ZongmenRules.HallBuildingId);
        var nowUnixSeconds = now.ToUnixTimeMilliseconds() / 1000d;
        await using (var cmd = new NpgsqlCommand("""
            SELECT node_id, level, target_level, started_at_unix_seconds, contributed_materials::text
            FROM game.node_buildings
            WHERE building_id = $1 AND node_id LIKE $2
            """, connection))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = ZongmenRules.HallBuildingId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = ZongmenRules.ResidencePrefix + "%" });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var nodeId = reader.GetString(0);
                if (!ZongmenRules.TryResidenceId(nodeId, out var zongmenId) || !headers.ContainsKey(zongmenId))
                    continue;

                var level = reader.GetInt32(1);
                if (hallDef is not null)
                {
                    var materialsRaw = reader.IsDBNull(4) ? "{}" : reader.GetString(4);
                    var materials = string.IsNullOrWhiteSpace(materialsRaw)
                        ? new Dictionary<string, double>()
                        : JsonSerializer.Deserialize<Dictionary<string, double>>(materialsRaw, PersistenceJson.Options)
                            ?? new Dictionary<string, double>();
                    var bState = new NodeBuildingState(
                        ZongmenRules.HallBuildingId,
                        level,
                        reader.GetInt32(2),
                        reader.GetDouble(3),
                        materials);
                    level = NodeBuildingRules.EffectiveLevel(bState, hallDef, nowUnixSeconds, nodeId);
                }
                hallLevels[zongmenId] = Math.Max(1, level);
            }
        }

        var result = new List<LeaderboardZongmenInput>(headers.Count);
        foreach (var (zid, meta) in headers)
        {
            var members = membersByZongmen.GetValueOrDefault(zid) ?? [];
            result.Add(new LeaderboardZongmenInput(
                zid,
                meta.Name,
                meta.Prefix,
                members.Count,
                hallLevels.GetValueOrDefault(zid, 1),
                meta.CreatedAt,
                members));
        }
        return result;
    }

    private static async ValueTask PersistAssemblyAsync(
        NpgsqlConnection connection,
        LeaderboardAssemblyResult assembly,
        IReadOnlyDictionary<string, CachedBoard> cachedBoards,
        CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var count = LeaderboardAssembler.CanonicalBoards.Count;
        var boardIds = new string[count];
        var computedAts = new DateTimeOffset[count];
        var counts = new int[count];
        var payloads = new string[count];
        for (var i = 0; i < count; i++)
        {
            var boardId = LeaderboardAssembler.CanonicalBoards[i].BoardId;
            var cached = cachedBoards[boardId];
            boardIds[i] = boardId;
            computedAts[i] = assembly.ComputedAt;
            counts[i] = cached.ParticipantCount;
            payloads[i] = Encoding.UTF8.GetString(cached.FullBaseJsonUtf8.Span);
        }

        await using (var snapCmd = new NpgsqlCommand("""
            INSERT INTO game.leaderboard_snapshots (board_id, computed_at, participant_count, payload)
            SELECT t.board_id, t.computed_at, t.participant_count, t.payload
            FROM unnest($1, $2, $3, $4) AS t(board_id, computed_at, participant_count, payload)
            ON CONFLICT (board_id) DO UPDATE SET
                computed_at = EXCLUDED.computed_at,
                participant_count = EXCLUDED.participant_count,
                payload = EXCLUDED.payload
            """, connection, transaction))
        {
            snapCmd.Parameters.Add(new NpgsqlParameter { Value = boardIds });
            snapCmd.Parameters.Add(new NpgsqlParameter { Value = computedAts });
            snapCmd.Parameters.Add(new NpgsqlParameter { Value = counts });
            snapCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb, Value = payloads });
            await snapCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var clearCmd = new NpgsqlCommand("DELETE FROM game.leaderboard_ranks", connection, transaction))
        {
            await clearCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var importer = await connection.BeginBinaryImportAsync(
            "COPY game.leaderboard_ranks (player_id, board_id, rank, score, updated_at) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var desc in LeaderboardAssembler.CanonicalBoards)
            {
                var myRanks = assembly.Boards[desc.BoardId].MyRanks;
                foreach (var (pid, rankDto) in myRanks)
                {
                    if (!double.IsFinite(rankDto.Score) || rankDto.Score <= 0) continue;
                    await importer.StartRowAsync(ct);
                    await importer.WriteAsync(pid, NpgsqlDbType.Uuid, ct);
                    await importer.WriteAsync(desc.BoardId, NpgsqlDbType.Varchar, ct);
                    await importer.WriteAsync(rankDto.Rank, NpgsqlDbType.Integer, ct);
                    await importer.WriteAsync(rankDto.Score, NpgsqlDbType.Double, ct);
                    await importer.WriteAsync(assembly.ComputedAt, NpgsqlDbType.TimestampTz, ct);
                }
            }
            await importer.CompleteAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static double? FindSpiritStones(IReadOnlyList<AssetBalanceRecord> assets)
    {
        for (var i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];
            if (asset.AssetId == EnhancementRules.SpiritStoneItemId && string.IsNullOrEmpty(asset.Variant))
                return Math.Max(0, asset.Quantity);
        }
        return null;
    }

    /// <summary>单元测试专用：直接注入预先汇编好的整点快照并构造不可变缓存状态。</summary>
    internal void SeedSnapshotForTesting(LeaderboardAssemblyResult assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _state = BuildCachedState(assembly, out _);
    }

    private static CachedLeaderboardState BuildCachedState(
        LeaderboardAssemblyResult assembly,
        out Dictionary<string, CachedBoard> cachedBoards)
    {
        cachedBoards = new Dictionary<string, CachedBoard>(assembly.Boards.Count, StringComparer.Ordinal);
        var totalRankRows = 0;
        foreach (var (boardId, assembled) in assembly.Boards)
        {
            cachedBoards[boardId] = CreateCachedBoard(boardId, assembled.Snapshot, assembled.MyRanks);
            totalRankRows += assembled.MyRanks.Count;
        }

        return CreateState(
            assembly.ComputedAt,
            assembly.NextComputeAt,
            assembly.Index,
            cachedBoards.ToFrozenDictionary(StringComparer.Ordinal),
            totalRankRows);
    }

    private static CachedBoard CreateCachedBoard(
        string boardId,
        LeaderboardSnapshotDto snapshot,
        FrozenDictionary<Guid, LeaderboardMyRankDto> myRanks)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot with { MyRank = null }, ProtocolJson.Options);
        if (bytes.Length < 2 || bytes[^1] != (byte)'}')
            throw new InvalidOperationException($"排行榜 {boardId} 基础 JSON 尾字符异常，无法构造拼接前缀。");

        var full = (ReadOnlyMemory<byte>)bytes;
        return new CachedBoard(boardId, snapshot.ParticipantCount, full, full[..^1], myRanks);
    }

    private static CachedLeaderboardState CreateState(
        DateTimeOffset computedAt,
        DateTimeOffset nextComputeAt,
        LeaderboardIndexDto indexDto,
        FrozenDictionary<string, CachedBoard> boards,
        int totalRankRows)
    {
        var ms = computedAt.ToUnixTimeMilliseconds();
        var indexBytes = (ReadOnlyMemory<byte>)JsonSerializer.SerializeToUtf8Bytes(indexDto, ProtocolJson.Options);
        var etag = $"W/\"lb-idx-{ms}\"";
        return new CachedLeaderboardState(computedAt, nextComputeAt, ms, indexBytes, etag, boards, totalRankRows);
    }

    private static bool MatchesIfNoneMatch(StringValues headerValues, string etag)
    {
        var targetTag = StripWeakPrefix(etag.AsSpan().Trim());
        foreach (var rawHeader in headerValues)
        {
            if (string.IsNullOrWhiteSpace(rawHeader)) continue;
            var span = rawHeader.AsSpan().Trim();
            if (span.Equals("*", StringComparison.Ordinal)) return true;
            foreach (var part in rawHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (StripWeakPrefix(part.AsSpan().Trim()).Equals(targetTag, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static ReadOnlySpan<char> StripWeakPrefix(ReadOnlySpan<char> token)
        => token.Length >= 2 && (token[0] == 'W' || token[0] == 'w') && token[1] == '/'
            ? token[2..]
            : token;

    private readonly record struct PlayerDbRow(
        long PlayerSeq,
        string DisplayName,
        int RealmLevel,
        double Experience,
        double DuelWins,
        double DuelLosses,
        string Avatar);

    private sealed record CachedBoard(
        string BoardId,
        int ParticipantCount,
        ReadOnlyMemory<byte> FullBaseJsonUtf8,
        ReadOnlyMemory<byte> BasePrefixUtf8,
        FrozenDictionary<Guid, LeaderboardMyRankDto> MyRanks);

    private sealed record CachedLeaderboardState(
        DateTimeOffset ComputedAt,
        DateTimeOffset NextComputeAt,
        long ComputedAtUnixMs,
        ReadOnlyMemory<byte> IndexJsonUtf8,
        string IndexETag,
        FrozenDictionary<string, CachedBoard> Boards,
        int TotalRankRows);
}

/// <summary>HTTP 排行榜预序列化响应载荷。</summary>
public readonly record struct LeaderboardHttpPayload(
    bool NotModified,
    string ETag,
    ReadOnlyMemory<byte> JsonUtf8);
