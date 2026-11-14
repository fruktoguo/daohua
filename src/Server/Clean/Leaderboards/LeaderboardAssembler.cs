using System.Collections.Frozen;
using Domain.GameData;
using Domain.Player;
using Protocol;

namespace Server.Clean.Leaderboards;

/// <summary>单个全服榜单的规范元数据定义。</summary>
/// <param name="BoardId">榜单唯一标识。</param>
/// <param name="Kind">榜别大类。</param>
/// <param name="TechniqueId">当 Kind 为 technique 时对应的生活技艺 ID，否则为 null。</param>
public sealed record LeaderboardBoardDescriptor(string BoardId, string Kind, string? TechniqueId);

/// <summary>全服排行榜汇编阶段消费的单个角色只读投影。</summary>
public sealed record LeaderboardPlayerInput(
    Guid PlayerId,
    long PlayerSeq,
    string DisplayName,
    string Avatar,
    int RealmLevel,
    double Experience,
    double? ExperienceToNext = null,
    double EquipmentValuation = 0,
    double AbilitiesValuation = 0,
    double ToolsValuation = 0,
    double SpiritStones = 0,
    double DuelWins = 0,
    double DuelLosses = 0,
    IReadOnlyDictionary<string, double>? TechniqueExperiences = null);

/// <summary>全服排行榜汇编阶段消费的单个宗门只读投影。</summary>
public sealed record LeaderboardZongmenInput(
    Guid ZongmenId,
    string Name,
    string Prefix,
    int MemberCount,
    int HallLevel,
    DateTimeOffset CreatedAt,
    IReadOnlyList<Guid> MemberPlayerIds);

/// <summary>单榜汇编完成产物：对外公共快照（MyRank=null）与全体上榜玩家的名次查表。</summary>
public sealed record AssembledLeaderboardBoard(
    string BoardId,
    string Kind,
    string? TechniqueId,
    LeaderboardSnapshotDto Snapshot,
    FrozenDictionary<Guid, LeaderboardMyRankDto> MyRanks);

/// <summary>全服排行榜单轮整点快照完整汇编结果（12 个固定榜单）。</summary>
public sealed record LeaderboardAssemblyResult(
    DateTimeOffset ComputedAt,
    DateTimeOffset NextComputeAt,
    LeaderboardIndexDto Index,
    FrozenDictionary<string, AssembledLeaderboardBoard> Boards);

/// <summary>
/// 全服排行榜确定性纯汇编器：
/// 把玩家与宗门只读输入转换为固定 12 榜（境界、7 项技艺、身家、灵石、宗门、斗法）的 Top 50 快照与全体名次索引。
/// 本类不访问数据库、不依赖系统时钟，供运行时物化与单元测试共用。
/// </summary>
public static class LeaderboardAssembler
{
    /// <summary>每个榜单公开展示的 Top N 条目上限。</summary>
    public const int TopN = 50;

    /// <summary>12 个全服固定榜单的规范展示顺序与元数据。</summary>
    public static readonly IReadOnlyList<LeaderboardBoardDescriptor> CanonicalBoards = BuildCanonicalBoards();

    private static readonly FrozenSet<string> s_knownBoardIds = CanonicalBoards
        .Select(b => b.BoardId)
        .ToFrozenSet(StringComparer.Ordinal);

    private static LeaderboardBoardDescriptor[] BuildCanonicalBoards()
    {
        var list = new List<LeaderboardBoardDescriptor>(5 + TechniqueCatalog.All.Length)
        {
            new(LeaderboardBoardIds.Realm, "realm", null),
        };
        foreach (var techId in TechniqueCatalog.All)
        {
            list.Add(new(LeaderboardBoardIds.Technique(techId), "technique", techId));
        }
        list.Add(new(LeaderboardBoardIds.Valuation, "valuation", null));
        list.Add(new(LeaderboardBoardIds.SpiritStone, "spirit-stone", null));
        list.Add(new(LeaderboardBoardIds.Zongmen, "zongmen", null));
        list.Add(new(LeaderboardBoardIds.Duel, "duel", null));
        return [.. list];
    }

    /// <summary>判断给定榜单 ID 是否属于 12 个受支持的固定榜别。</summary>
    public static bool IsKnownBoard(string? boardId)
        => !string.IsNullOrEmpty(boardId) && s_knownBoardIds.Contains(boardId);

    /// <summary>将 UTC 时间向下取整至整点时刻，用于计算快照窗口与下次刷新时间。</summary>
    public static DateTimeOffset FloorToHourUtc(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// 预选境界榜 Top N 玩家 ID，供仓储层在主汇编前仅对离线 Top N 玩家补充灵根破镜乘区。
    /// 排序口径必须与正式汇编完全一致，确保预选集合精确覆盖最终展示名单。
    /// </summary>
    public static IReadOnlyList<Guid> SelectRealmTopPlayerIds(IReadOnlyList<LeaderboardPlayerInput> players)
    {
        ArgumentNullException.ThrowIfNull(players);
        if (players.Count == 0) return [];

        var copy = players.ToArray();
        Array.Sort(copy, CompareRealm);
        var take = Math.Min(TopN, copy.Length);
        var ids = new Guid[take];
        for (var i = 0; i < take; i++)
            ids[i] = copy[i].PlayerId;
        return ids;
    }

    /// <summary>确定性汇编 12 张全服榜单快照与全体上榜名次。</summary>
    /// <param name="computedAt">本轮物化的权威 UTC 时刻。</param>
    /// <param name="players">全体有效玩家投影。</param>
    /// <param name="zongmens">全体未解散宗门投影。</param>
    /// <param name="realmCatalog">境界与技艺经验曲线配表。</param>
    public static LeaderboardAssemblyResult Assemble(
        DateTimeOffset computedAt,
        IReadOnlyList<LeaderboardPlayerInput> players,
        IReadOnlyList<LeaderboardZongmenInput> zongmens,
        RealmProgressionCatalog? realmCatalog)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(zongmens);

        var utcComputedAt = computedAt.ToUniversalTime();
        var nextComputeAt = FloorToHourUtc(utcComputedAt).AddHours(1);

        var boards = new Dictionary<string, AssembledLeaderboardBoard>(CanonicalBoards.Count, StringComparer.Ordinal);
        var summaries = new List<LeaderboardBoardSummaryDto>(CanonicalBoards.Count);

        void Register(LeaderboardBoardDescriptor desc, IReadOnlyList<LeaderboardEntryDto> entries, int participantCount, FrozenDictionary<Guid, LeaderboardMyRankDto> myRanks)
        {
            var topScore = entries.Count > 0 ? (double?)entries[0].Score : null;
            var snapshot = new LeaderboardSnapshotDto(
                desc.BoardId,
                utcComputedAt,
                nextComputeAt,
                participantCount,
                entries,
                MyRank: null);
            boards[desc.BoardId] = new AssembledLeaderboardBoard(desc.BoardId, desc.Kind, desc.TechniqueId, snapshot, myRanks);
            summaries.Add(new LeaderboardBoardSummaryDto(desc.BoardId, desc.Kind, desc.TechniqueId, participantCount, topScore));
        }

        // 1. 境界榜（全服活跃玩家均上榜；先按整数境界降序，再按当前层修为降序）
        {
            var sorted = players.ToArray();
            Array.Sort(sorted, CompareRealm);
            var myRanks = new Dictionary<Guid, LeaderboardMyRankDto>(sorted.Length);
            var topCount = Math.Min(TopN, sorted.Length);
            var entries = new LeaderboardEntryDto[topCount];
            for (var i = 0; i < sorted.Length; i++)
            {
                var p = sorted[i];
                var rank = i + 1;
                var realm = SafeRealmLevel(p.RealmLevel);
                var exp = NonNegFinite(p.Experience);
                myRanks[p.PlayerId] = new LeaderboardMyRankDto(rank, realm);

                if (i < topCount)
                {
                    var expToNext = p.ExperienceToNext is { } ov && double.IsFinite(ov) && ov >= 0
                        ? ov
                        : HeavenGateRules.ResolveExpCapWithLinggen(realm, LinggenValues.Empty, false, realmCatalog);
                    entries[i] = new LeaderboardEntryDto(
                        Rank: rank,
                        Score: realm,
                        DisplayName: p.DisplayName,
                        Avatar: NormalizeAvatar(p.Avatar),
                        PlayerId: p.PlayerId.ToString("D"),
                        RealmLevel: realm,
                        Experience: exp,
                        ExperienceToNext: expToNext,
                        PlayerSeq: p.PlayerSeq);
                }
            }
            Register(CanonicalBoards[0], entries, sorted.Length, myRanks.ToFrozenDictionary());
        }

        // 2. 七大生活技艺榜（排除 exp <= 0；先按技艺整数等级降序，再按累计技艺经验降序）
        for (var idx = 0; idx < TechniqueCatalog.All.Length; idx++)
        {
            var techId = TechniqueCatalog.All[idx];
            var desc = CanonicalBoards[1 + idx];
            var candidates = new List<(LeaderboardPlayerInput Player, int Level, double Exp)>(players.Count);
            foreach (var p in players)
            {
                if (p.TechniqueExperiences is null || !p.TechniqueExperiences.TryGetValue(techId, out var rawExp))
                    continue;
                var exp = NonNegFinite(rawExp);
                if (exp <= 0) continue;
                var level = (int)Math.Floor(TechniqueCatalog.LevelFor(exp, realmCatalog));
                candidates.Add((p, level, exp));
            }
            candidates.Sort(static (a, b) =>
            {
                var cmp = b.Level.CompareTo(a.Level);
                if (cmp != 0) return cmp;
                cmp = b.Exp.CompareTo(a.Exp);
                if (cmp != 0) return cmp;
                return CompareTie(a.Player, b.Player);
            });

            var myRanks = new Dictionary<Guid, LeaderboardMyRankDto>(candidates.Count);
            var topCount = Math.Min(TopN, candidates.Count);
            var entries = new LeaderboardEntryDto[topCount];
            for (var i = 0; i < candidates.Count; i++)
            {
                var item = candidates[i];
                var rank = i + 1;
                myRanks[item.Player.PlayerId] = new LeaderboardMyRankDto(rank, item.Exp, TechniqueLevel: item.Level);
                if (i < topCount)
                {
                    entries[i] = new LeaderboardEntryDto(
                        Rank: rank,
                        Score: item.Exp,
                        DisplayName: item.Player.DisplayName,
                        Avatar: NormalizeAvatar(item.Player.Avatar),
                        PlayerId: item.Player.PlayerId.ToString("D"),
                        RealmLevel: SafeRealmLevel(item.Player.RealmLevel),
                        TechniqueLevel: item.Level,
                        TechniqueExp: item.Exp,
                        PlayerSeq: item.Player.PlayerSeq);
                }
            }
            Register(desc, entries, candidates.Count, myRanks.ToFrozenDictionary());
        }

        // 3. 身家估值榜（排除总身家 <= 0；同总值按装备身家 -> 功法身家 -> 序号打破平手）
        {
            var desc = CanonicalBoards[1 + TechniqueCatalog.All.Length];
            var candidates = new List<(LeaderboardPlayerInput Player, double Total, double Eq, double Ab, double Tools)>(players.Count);
            foreach (var p in players)
            {
                var eq = NonNegFinite(p.EquipmentValuation);
                var ab = NonNegFinite(p.AbilitiesValuation);
                var tools = NonNegFinite(p.ToolsValuation);
                var total = eq + ab + tools;
                if (!double.IsFinite(total) || total <= 0) continue;
                candidates.Add((p, total, eq, ab, tools));
            }
            candidates.Sort(static (a, b) =>
            {
                var cmp = b.Total.CompareTo(a.Total);
                if (cmp != 0) return cmp;
                cmp = b.Eq.CompareTo(a.Eq);
                if (cmp != 0) return cmp;
                cmp = b.Ab.CompareTo(a.Ab);
                if (cmp != 0) return cmp;
                return CompareTie(a.Player, b.Player);
            });

            var myRanks = new Dictionary<Guid, LeaderboardMyRankDto>(candidates.Count);
            var topCount = Math.Min(TopN, candidates.Count);
            var entries = new LeaderboardEntryDto[topCount];
            for (var i = 0; i < candidates.Count; i++)
            {
                var item = candidates[i];
                var rank = i + 1;
                myRanks[item.Player.PlayerId] = new LeaderboardMyRankDto(rank, item.Total);
                if (i < topCount)
                {
                    entries[i] = new LeaderboardEntryDto(
                        Rank: rank,
                        Score: item.Total,
                        DisplayName: item.Player.DisplayName,
                        Avatar: NormalizeAvatar(item.Player.Avatar),
                        PlayerId: item.Player.PlayerId.ToString("D"),
                        RealmLevel: SafeRealmLevel(item.Player.RealmLevel),
                        Valuation: item.Total,
                        ValuationBreakdown: new ValuationBreakdownDto(item.Eq, item.Ab, item.Tools),
                        PlayerSeq: item.Player.PlayerSeq);
                }
            }
            Register(desc, entries, candidates.Count, myRanks.ToFrozenDictionary());
        }

        // 4. 灵石富豪榜（排除灵石 <= 0）
        {
            var desc = CanonicalBoards[2 + TechniqueCatalog.All.Length];
            var candidates = new List<(LeaderboardPlayerInput Player, double Stones)>(players.Count);
            foreach (var p in players)
            {
                var qty = NonNegFinite(p.SpiritStones);
                if (qty <= 0) continue;
                candidates.Add((p, qty));
            }
            candidates.Sort(static (a, b) =>
            {
                var cmp = b.Stones.CompareTo(a.Stones);
                if (cmp != 0) return cmp;
                return CompareTie(a.Player, b.Player);
            });

            var myRanks = new Dictionary<Guid, LeaderboardMyRankDto>(candidates.Count);
            var topCount = Math.Min(TopN, candidates.Count);
            var entries = new LeaderboardEntryDto[topCount];
            for (var i = 0; i < candidates.Count; i++)
            {
                var item = candidates[i];
                var rank = i + 1;
                myRanks[item.Player.PlayerId] = new LeaderboardMyRankDto(rank, item.Stones);
                if (i < topCount)
                {
                    entries[i] = new LeaderboardEntryDto(
                        Rank: rank,
                        Score: item.Stones,
                        DisplayName: item.Player.DisplayName,
                        Avatar: NormalizeAvatar(item.Player.Avatar),
                        PlayerId: item.Player.PlayerId.ToString("D"),
                        RealmLevel: SafeRealmLevel(item.Player.RealmLevel),
                        PlayerSeq: item.Player.PlayerSeq);
                }
            }
            Register(desc, entries, candidates.Count, myRanks.ToFrozenDictionary());
        }

        // 5. 宗门势力榜（成员数降序 -> 宗门大殿等级降序 -> 创宗时间升序；将宗门名次映射至每位成员以展示 myRank）
        {
            var desc = CanonicalBoards[3 + TechniqueCatalog.All.Length];
            var activeZongmens = new List<LeaderboardZongmenInput>(zongmens.Count);
            foreach (var z in zongmens)
            {
                if (z.MemberCount > 0)
                    activeZongmens.Add(z);
            }
            activeZongmens.Sort(static (a, b) =>
            {
                var cmp = b.MemberCount.CompareTo(a.MemberCount);
                if (cmp != 0) return cmp;
                cmp = b.HallLevel.CompareTo(a.HallLevel);
                if (cmp != 0) return cmp;
                cmp = a.CreatedAt.CompareTo(b.CreatedAt);
                if (cmp != 0) return cmp;
                return a.ZongmenId.CompareTo(b.ZongmenId);
            });

            var myRanks = new Dictionary<Guid, LeaderboardMyRankDto>();
            var topCount = Math.Min(TopN, activeZongmens.Count);
            var entries = new LeaderboardEntryDto[topCount];
            for (var i = 0; i < activeZongmens.Count; i++)
            {
                var z = activeZongmens[i];
                var rank = i + 1;
                var score = (double)z.MemberCount;
                var rankDto = new LeaderboardMyRankDto(rank, score);
                foreach (var memberId in z.MemberPlayerIds)
                {
                    myRanks.TryAdd(memberId, rankDto);
                }
                if (i < topCount)
                {
                    entries[i] = new LeaderboardEntryDto(
                        Rank: rank,
                        Score: score,
                        DisplayName: z.Name,
                        ZongmenId: z.ZongmenId.ToString("D"),
                        ZongmenName: z.Name,
                        ZongmenPrefix: z.Prefix,
                        MemberCount: z.MemberCount,
                        HallLevel: Math.Max(1, z.HallLevel));
                }
            }
            Register(desc, entries, activeZongmens.Count, myRanks.ToFrozenDictionary());
        }

        // 6. 斗法胜场榜（排除 0 胜；胜场降序 -> 负场升序 -> 序号升序）
        {
            var desc = CanonicalBoards[4 + TechniqueCatalog.All.Length];
            var candidates = new List<(LeaderboardPlayerInput Player, double Wins, double Losses)>(players.Count);
            foreach (var p in players)
            {
                var wins = NonNegFinite(p.DuelWins);
                if (wins <= 0) continue;
                var losses = NonNegFinite(p.DuelLosses);
                candidates.Add((p, wins, losses));
            }
            candidates.Sort(static (a, b) =>
            {
                var cmp = b.Wins.CompareTo(a.Wins);
                if (cmp != 0) return cmp;
                cmp = a.Losses.CompareTo(b.Losses);
                if (cmp != 0) return cmp;
                return CompareTie(a.Player, b.Player);
            });

            var myRanks = new Dictionary<Guid, LeaderboardMyRankDto>(candidates.Count);
            var topCount = Math.Min(TopN, candidates.Count);
            var entries = new LeaderboardEntryDto[topCount];
            for (var i = 0; i < candidates.Count; i++)
            {
                var item = candidates[i];
                var rank = i + 1;
                myRanks[item.Player.PlayerId] = new LeaderboardMyRankDto(rank, item.Wins, DuelLosses: item.Losses);
                if (i < topCount)
                {
                    entries[i] = new LeaderboardEntryDto(
                        Rank: rank,
                        Score: item.Wins,
                        DisplayName: item.Player.DisplayName,
                        Avatar: NormalizeAvatar(item.Player.Avatar),
                        PlayerId: item.Player.PlayerId.ToString("D"),
                        RealmLevel: SafeRealmLevel(item.Player.RealmLevel),
                        DuelWins: item.Wins,
                        DuelLosses: item.Losses,
                        PlayerSeq: item.Player.PlayerSeq);
                }
            }
            Register(desc, entries, candidates.Count, myRanks.ToFrozenDictionary());
        }

        var indexDto = new LeaderboardIndexDto(utcComputedAt, nextComputeAt, summaries);
        return new LeaderboardAssemblyResult(utcComputedAt, nextComputeAt, indexDto, boards.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static int CompareRealm(LeaderboardPlayerInput a, LeaderboardPlayerInput b)
    {
        var cmp = SafeRealmLevel(b.RealmLevel).CompareTo(SafeRealmLevel(a.RealmLevel));
        if (cmp != 0) return cmp;
        cmp = NonNegFinite(b.Experience).CompareTo(NonNegFinite(a.Experience));
        if (cmp != 0) return cmp;
        return CompareTie(a, b);
    }

    private static int CompareTie(LeaderboardPlayerInput a, LeaderboardPlayerInput b)
    {
        var cmp = a.PlayerSeq.CompareTo(b.PlayerSeq);
        return cmp != 0 ? cmp : a.PlayerId.CompareTo(b.PlayerId);
    }

    private static int SafeRealmLevel(int realmLevel) => Math.Max(1, realmLevel);

    private static double NonNegFinite(double value) => double.IsFinite(value) && value > 0 ? value : 0d;

    private static string NormalizeAvatar(string? avatar)
        => string.IsNullOrWhiteSpace(avatar) ? PlayerSettingsState.DefaultFemaleAvatar : avatar.Trim();
}
