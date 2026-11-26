namespace Protocol;

/// <summary>修士在个人排行榜（除宗门榜外）的上榜荣誉头衔投影。</summary>
/// <param name="BoardId">榜单唯一标识（如 realm、technique.alchemy、valuation、spirit-stone、duel）。</param>
/// <param name="Kind">榜别大类（realm / technique / valuation / spirit-stone / duel）。</param>
/// <param name="TechniqueId">技艺榜对应的生活技艺 ID；非技艺榜为 null。</param>
/// <param name="Rank">当前整点快照名次（1..50）。</param>
public sealed record GuideRankBadgeDto(
    string BoardId,
    string Kind,
    string? TechniqueId,
    int Rank);

/// <summary>当前认证修士在道友攻略板块的发帖资格与每日额度摘要。</summary>
/// <param name="CanCreate">当前是否允许立即发布新攻略（上榜且非机器人且今日额度未满）。</param>
/// <param name="IsRanked">当前是否在至少一张个人排行榜位列 Top 50。</param>
/// <param name="IsBot">当前账号是否为机器人账号。</param>
/// <param name="DailyLimit">自然日新发帖上限（固定 1）。</param>
/// <param name="TodayPublishedCount">今日（UTC+8）已发布新帖次数（删帖不退额度）。</param>
/// <param name="RankedBoards">当前位列 Top 50 的全部个人榜名次徽标（按名次升序）。</param>
public sealed record GuidePermissionsDto(
    bool CanCreate,
    bool IsRanked,
    bool IsBot,
    int DailyLimit,
    int TodayPublishedCount,
    IReadOnlyList<GuideRankBadgeDto> RankedBoards);

/// <summary>道友攻略列表条目摘要 DTO（不含完整正文，保护付费门槛内容）。</summary>
public sealed record GuidePostSummaryDto(
    string PostId,
    string AuthorId,
    string AuthorName,
    string AuthorAvatar,
    int AuthorRealmLevel,
    string? AuthorZongmenPrefix,
    GuideRankBadgeDto? AuthorTopRank,
    string Title,
    string Excerpt,
    double UnlockPrice,
    bool IsUnlocked,
    bool LikedByMe,
    int ViewCount,
    int LikeCount,
    int TipperCount,
    double TotalRewardStones,
    double HotScore,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>道友攻略列表查询响应 DTO。</summary>
public sealed record GuideListResponseDto(
    string Sort,
    bool MineOnly,
    int TotalCount,
    GuidePermissionsDto Permissions,
    IReadOnlyList<GuidePostSummaryDto> Items);

/// <summary>道友攻略完整详情 DTO；当 Locked 为 true 时 Body 严格为 null。</summary>
public sealed record GuidePostDetailDto(
    string PostId,
    string AuthorId,
    string AuthorName,
    string AuthorAvatar,
    int AuthorRealmLevel,
    string? AuthorZongmenPrefix,
    GuideRankBadgeDto? AuthorTopRank,
    string Title,
    string Summary,
    string? Body,
    double UnlockPrice,
    bool Locked,
    bool IsUnlocked,
    bool LikedByMe,
    double MyTippedStones,
    int ViewCount,
    int LikeCount,
    int TipperCount,
    double TotalRewardStones,
    double HotScore,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>发布新攻略请求载荷。</summary>
public sealed record CreateGuidePostRequest(
    string Title,
    string? Summary,
    string Body,
    double UnlockPrice);

/// <summary>编辑已有攻略请求载荷。</summary>
public sealed record UpdateGuidePostRequest(
    string Title,
    string? Summary,
    string Body,
    double UnlockPrice);

/// <summary>攻略点赞/取消赞请求载荷。</summary>
public sealed record SetGuideLikeRequest(bool Liked);

/// <summary>攻略点赞响应载荷。</summary>
public sealed record SetGuideLikeResponse(
    string PostId,
    bool Liked,
    int LikeCount,
    double HotScore);

/// <summary>付费解锁攻略请求载荷。</summary>
/// <param name="RequestId">客户端请求标识。</param>
/// <param name="ExpectedPrice">客户端展示的预期解锁价格（防作者刚涨价滑点）。</param>
public sealed record UnlockGuidePostRequest(
    string RequestId,
    double ExpectedPrice);

/// <summary>自由打赏攻略请求载荷。</summary>
/// <param name="RequestId">客户端幂等请求标识。</param>
/// <param name="Amount">打赏整数灵石数额（1..100,000）。</param>
public sealed record TipGuidePostRequest(
    string RequestId,
    double Amount);

/// <summary>打赏攻略成功响应载荷。</summary>
public sealed record TipGuidePostResponse(
    string PostId,
    double PaidStones,
    double AuthorReceivedStones,
    double MyTippedStones,
    int TipperCount,
    double TotalRewardStones,
    double HotScore);

/// <summary>付费查阅他人角色名片配装请求载荷。</summary>
/// <param name="RequestId">客户端请求标识。</param>
/// <param name="ExpectedPrice">客户端名片上展示的查阅价格（防目标刚涨价滑点）。</param>
public sealed record UnlockPlayerLoadoutRequest(
    string RequestId,
    double ExpectedPrice);

/// <summary>付费查阅他人角色名片配装响应载荷。</summary>
public sealed record UnlockPlayerLoadoutResponse(
    string TargetPlayerId,
    double PaidStones,
    double TargetReceivedStones,
    double UnlockedUntilUnixSeconds);
