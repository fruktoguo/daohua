using Domain.Common;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Protocol;
using Server.Clean.Leaderboards;

namespace Server.Clean.Social;

/// <summary>
/// 道友攻略与角色名片配装付费查阅应用层运行时：
/// - 对齐全服排行榜整点快照（除宗门榜外 11 张个人榜 Top 50）裁决发帖资格并投影作者最优头衔；
/// - 统一执行 Unicode 标量值（Rune）字数校验（标题 ≤ 60、导语 ≤ 200、正文 ≤ 10,000）；
/// - 协调在线玩家钱包前置冲刷（FlushAsync）与提交后实时热刷新通知（ReloadWalletAndNotifyAsync）。
/// </summary>
public sealed class GuideRuntime(
    PostgresGuideRepository repository,
    LeaderboardRuntime leaderboards,
    GameDataCatalog gameData,
    IPlayerRuntime? playerRuntime = null,
    IPlayerStateRepository? sectionRepository = null,
    IPlayerSimulationClock? clock = null)
{
    public const int MaxTitleRunes = 60;
    public const int MaxSummaryRunes = 200;
    public const int MaxBodyRunes = 10_000;

    private readonly IPlayerSimulationClock _clock = clock ?? PlayerSimulationClock.FromEnvironment();

    /// <summary>获取玩家当前的攻略发帖资格与今日剩余额度。</summary>
    public async ValueTask<GuidePermissionsDto> GetPermissionsAsync(Guid playerId, CancellationToken ct = default)
    {
        var flags = await repository.GetPlayerFlagsAsync(playerId, ct).ConfigureAwait(false)
            ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "角色不存在或不可用。");

        var nowUtc = _clock.UtcNow;
        var (cstDate, _) = PostgresGuideRepository.ResolveCstDayWindow(nowUtc);
        var todayCount = await repository.GetDailyPublishedCountAsync(playerId, cstDate, ct).ConfigureAwait(false);
        var ranks = flags.IsBot
            ? []
            : await leaderboards.GetPersonalTopRanksAsync(playerId, LeaderboardAssembler.TopN, ct).ConfigureAwait(false);

        var isRanked = !flags.IsBot && ranks.Count > 0;
        var canCreate = isRanked && todayCount < PostgresGuideRepository.DailyPublishLimit;
        return new GuidePermissionsDto(
            CanCreate: canCreate,
            IsRanked: isRanked,
            IsBot: flags.IsBot,
            DailyLimit: PostgresGuideRepository.DailyPublishLimit,
            TodayPublishedCount: todayCount,
            RankedBoards: ranks);
    }

    /// <summary>分页查询攻略列表并附带当前玩家的发帖资格摘要与各作者当前最优上榜头衔。</summary>
    public async ValueTask<GuideListResponseDto> ListAsync(
        Guid viewerId,
        string? sort,
        bool mineOnly,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var normalizedSort = string.Equals(sort, "reward", StringComparison.OrdinalIgnoreCase) ? "reward" : "hot";
        var permissions = await GetPermissionsAsync(viewerId, ct).ConfigureAwait(false);
        var (totalCount, rows) = await repository.ListPostsAsync(viewerId, normalizedSort, mineOnly, limit, offset, ct).ConfigureAwait(false);

        var authorIds = rows.Select(r => r.AuthorId).Distinct().ToArray();
        var bestRanks = await leaderboards.GetBestPersonalRanksAsync(authorIds, LeaderboardAssembler.TopN, ct).ConfigureAwait(false);

        var items = new List<GuidePostSummaryDto>(rows.Count);
        foreach (var row in rows)
        {
            var (authorAvatar, authorRealm) = ResolveAuthorAppearance(row.AuthorId, row.AuthorAvatar, row.AuthorRealmLevel);
            bestRanks.TryGetValue(row.AuthorId, out var topRank);
            items.Add(new GuidePostSummaryDto(
                PostId: row.PostId.ToString("D"),
                AuthorId: row.AuthorId.ToString("D"),
                AuthorName: row.AuthorName,
                AuthorAvatar: authorAvatar,
                AuthorRealmLevel: authorRealm,
                AuthorZongmenPrefix: row.AuthorZongmenPrefix,
                AuthorTopRank: topRank,
                Title: row.Title,
                Excerpt: PostgresGuideRepository.BuildSafeExcerpt(row.Summary, row.Body, row.UnlockPrice),
                UnlockPrice: row.UnlockPrice,
                IsUnlocked: row.IsUnlockedByViewer,
                LikedByMe: row.LikedByViewer,
                ViewCount: row.ViewCount,
                LikeCount: row.LikeCount,
                TipperCount: row.TipperCount,
                TotalRewardStones: row.TotalRewardStones,
                HotScore: PostgresGuideRepository.ComputeHotScore(row.ViewCount, row.LikeCount, row.TipperCount, row.TotalRewardStones),
                CreatedAt: row.CreatedAt,
                UpdatedAt: row.UpdatedAt));
        }

        return new GuideListResponseDto(normalizedSort, mineOnly, totalCount, permissions, items);
    }

    /// <summary>获取单篇攻略详情（已解锁时自动记一次独立阅读）。</summary>
    public async ValueTask<GuidePostDetailDto> GetDetailAsync(
        Guid viewerId,
        Guid postId,
        CancellationToken ct = default)
    {
        var flags = await repository.GetPlayerFlagsAsync(viewerId, ct).ConfigureAwait(false)
            ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "角色不存在。");

        var row = await repository.GetPostDetailAsync(postId, viewerId, recordView: !flags.IsBot, _clock.UtcNow, ct).ConfigureAwait(false)
            ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "该攻略不存在或已删除。");

        return await MapDetailDtoAsync(row, ct).ConfigureAwait(false);
    }

    /// <summary>发布新攻略（要求当前在任意非宗门个人榜 Top 50 且今日未发过帖）。</summary>
    public async ValueTask<GuidePostDetailDto> CreateAsync(
        Guid authorId,
        CreateGuidePostRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (title, summary, body, unlockPrice) = ValidatePostContent(request.Title, request.Summary, request.Body, request.UnlockPrice);

        var permissions = await GetPermissionsAsync(authorId, ct).ConfigureAwait(false);
        if (permissions.IsBot)
            throw new GuideOperationException(GuideOperationFailure.BotRestricted, "系统或机器人账号禁止发布攻略。");
        if (!permissions.IsRanked)
            throw new GuideOperationException(GuideOperationFailure.NotRanked, "仅身处个人排行榜（前 50 名）的修士可发布新攻略。");
        if (permissions.TodayPublishedCount >= permissions.DailyLimit)
            throw new GuideOperationException(GuideOperationFailure.DailyQuotaExceeded, "今日已发布过攻略，每日最多发布 1 篇。");

        var row = await repository.CreatePostAsync(authorId, title, summary, body, unlockPrice, _clock.UtcNow, ct).ConfigureAwait(false);
        return await MapDetailDtoAsync(row, ct).ConfigureAwait(false);
    }

    /// <summary>编辑本人已发布的攻略（掉榜后仍允许维护旧帖，不消耗当日额度）。</summary>
    public async ValueTask<GuidePostDetailDto> UpdateAsync(
        Guid authorId,
        Guid postId,
        UpdateGuidePostRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var flags = await repository.GetPlayerFlagsAsync(authorId, ct).ConfigureAwait(false)
            ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "角色不存在。");
        if (flags.IsBot)
            throw new GuideOperationException(GuideOperationFailure.BotRestricted, "系统或机器人账号禁止编辑攻略。");

        var (title, summary, body, unlockPrice) = ValidatePostContent(request.Title, request.Summary, request.Body, request.UnlockPrice);
        var row = await repository.UpdatePostAsync(postId, authorId, title, summary, body, unlockPrice, _clock.UtcNow, ct).ConfigureAwait(false);
        return await MapDetailDtoAsync(row, ct).ConfigureAwait(false);
    }

    /// <summary>删除本人已发布的攻略（当日已用发帖额度不退还）。</summary>
    public async ValueTask DeleteAsync(Guid authorId, Guid postId, CancellationToken ct = default)
    {
        _ = await repository.GetPlayerFlagsAsync(authorId, ct).ConfigureAwait(false)
            ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "角色不存在。");
        await repository.DeletePostAsync(postId, authorId, _clock.UtcNow, ct).ConfigureAwait(false);
    }

    /// <summary>点赞或取消点赞攻略。</summary>
    public async ValueTask<SetGuideLikeResponse> SetLikeAsync(
        Guid playerId,
        Guid postId,
        bool liked,
        CancellationToken ct = default)
    {
        var flags = await repository.GetPlayerFlagsAsync(playerId, ct).ConfigureAwait(false)
            ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "角色不存在。");
        if (flags.IsBot)
            throw new GuideOperationException(GuideOperationFailure.BotRestricted, "系统或机器人账号禁止点赞。");

        return await repository.SetLikeAsync(postId, playerId, liked, _clock.UtcNow, ct).ConfigureAwait(false);
    }

    /// <summary>支付灵石永久解锁付费攻略正文。</summary>
    public async ValueTask<GuidePostDetailDto> UnlockAsync(
        Guid viewerId,
        Guid postId,
        UnlockGuidePostRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var flags = await repository.GetPlayerFlagsAsync(viewerId, ct).ConfigureAwait(false)
            ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "角色不存在。");
        if (flags.IsBot)
            throw new GuideOperationException(GuideOperationFailure.BotRestricted, "系统或机器人账号禁止付费解锁攻略。");
        if (!double.IsFinite(request.ExpectedPrice) || request.ExpectedPrice < 0d || request.ExpectedPrice > PostgresGuideRepository.MaxPriceOrTipStones)
            throw new GuideOperationException(GuideOperationFailure.InvalidArgument, "预期解锁价格参数无效。");

        await FlushOnlineWalletIfLoadedAsync(viewerId, ct).ConfigureAwait(false);

        var (row, authorId, newlyPaid) = await repository.UnlockPostAsync(postId, viewerId, request.ExpectedPrice, _clock.UtcNow, ct).ConfigureAwait(false);
        if (newlyPaid)
        {
            await ReloadOnlineWalletsAsync(viewerId, authorId).ConfigureAwait(false);
        }
        return await MapDetailDtoAsync(row, ct).ConfigureAwait(false);
    }

    /// <summary>向攻略自由打赏灵石。</summary>
    public async ValueTask<TipGuidePostResponse> TipAsync(
        Guid tipperId,
        Guid postId,
        TipGuidePostRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var flags = await repository.GetPlayerFlagsAsync(tipperId, ct).ConfigureAwait(false)
            ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "角色不存在。");
        if (flags.IsBot)
            throw new GuideOperationException(GuideOperationFailure.BotRestricted, "系统或机器人账号禁止打赏攻略。");

        var amount = ValidatePositiveIntegerStones(request.Amount, "打赏灵石数额");
        await FlushOnlineWalletIfLoadedAsync(tipperId, ct).ConfigureAwait(false);

        var (response, authorId, newlyPaid) = await repository.TipPostAsync(postId, tipperId, request.RequestId, amount, _clock.UtcNow, ct).ConfigureAwait(false);
        if (newlyPaid)
        {
            await ReloadOnlineWalletsAsync(tipperId, authorId).ConfigureAwait(false);
        }
        return response;
    }

    /// <summary>支付灵石解锁查阅目标玩家的角色名片配装（权限维持至北京时间当日结束零点）。</summary>
    public async ValueTask<UnlockPlayerLoadoutResponse> UnlockPlayerLoadoutAsync(
        Guid viewerId,
        Guid targetId,
        UnlockPlayerLoadoutRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (viewerId == targetId)
            throw new GuideOperationException(GuideOperationFailure.InvalidArgument, "本人配装始终可见，无需付费解锁。");
        if (!double.IsFinite(request.ExpectedPrice) || request.ExpectedPrice <= 0d || request.ExpectedPrice > PostgresGuideRepository.MaxPriceOrTipStones)
            throw new GuideOperationException(GuideOperationFailure.InvalidArgument, "预期查阅价格无效。");

        var viewerFlags = await repository.GetPlayerFlagsAsync(viewerId, ct).ConfigureAwait(false)
            ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "当前角色不存在。");
        if (viewerFlags.IsBot)
            throw new GuideOperationException(GuideOperationFailure.BotRestricted, "系统或机器人账号禁止付费查阅他人配装。");

        var targetFlags = await repository.GetPlayerFlagsAsync(targetId, ct).ConfigureAwait(false)
            ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "目标修士不存在。");
        if (targetFlags.IsBot)
            throw new GuideOperationException(GuideOperationFailure.BotRestricted, "无法对系统或机器人角色执行付费查阅。");

        var targetSettings = await ResolveTargetSettingsAsync(targetId, ct).ConfigureAwait(false)
            ?? throw new GuideOperationException(GuideOperationFailure.NotFound, "目标修士不存在。");

        if (targetSettings.PublicLoadoutVisible)
            throw new GuideOperationException(GuideOperationFailure.PriceChanged, "该修士已免费公开配装，请刷新名片直接查看。");

        var actualPrice = PlayerSettingsState.NormalizeLoadoutInspectPrice(targetSettings.LoadoutInspectPrice, targetSettings.PublicLoadoutVisible);
        if (actualPrice <= 0d)
            throw new GuideOperationException(GuideOperationFailure.Forbidden, "该修士已完全隐藏配装，暂未开放付费查阅。");

        if (actualPrice > request.ExpectedPrice)
            throw new GuideOperationException(GuideOperationFailure.PriceChanged, $"查阅价格已更新为 {actualPrice:0} 灵石，请刷新名片确认。");

        await FlushOnlineWalletIfLoadedAsync(viewerId, ct).ConfigureAwait(false);
        var (response, newlyPaid) = await repository.UnlockPlayerLoadoutAsync(viewerId, targetId, actualPrice, _clock.UtcNow, ct).ConfigureAwait(false);
        if (newlyPaid)
        {
            await ReloadOnlineWalletsAsync(viewerId, targetId).ConfigureAwait(false);
        }
        return response;
    }

    private async ValueTask<PlayerSettingsStateDocument?> ResolveTargetSettingsAsync(Guid targetId, CancellationToken ct)
    {
        if (playerRuntime?.TryGetLoaded(targetId) is { } loaded)
            return loaded.State.Settings;
        if (sectionRepository is null)
            return null;
        var bundle = await sectionRepository.LoadAsync(targetId, ct).ConfigureAwait(false);
        if (bundle is null)
            return null;
        var state = bundle.Head.StateVersion > 0 || bundle.Sections.Count > 0
            ? SectionCodec.DecodeAggregate(bundle)
            : PlayerAggregate.Create(new PlayerId(targetId), new AccountId(bundle.Identity.AccountId),
                bundle.Identity.DisplayName, bundle.Identity.CreatedAt, gameData.Fingerprint).ExportState();
        return state.Settings;
    }

    private async ValueTask FlushOnlineWalletIfLoadedAsync(Guid playerId, CancellationToken ct)
    {
        if (playerRuntime is not null && playerRuntime.TryGetLoaded(playerId) is not null)
        {
            await playerRuntime.FlushAsync(playerId, force: true, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask ReloadOnlineWalletsAsync(Guid payerId, Guid recipientId)
    {
        if (playerRuntime is null) return;
        await playerRuntime.ReloadWalletAndNotifyAsync(payerId, CancellationToken.None).ConfigureAwait(false);
        if (recipientId != payerId)
            await playerRuntime.ReloadWalletAndNotifyAsync(recipientId, CancellationToken.None).ConfigureAwait(false);
    }

    private (string Avatar, int RealmLevel) ResolveAuthorAppearance(Guid authorId, string dbAvatar, int dbRealmLevel)
    {
        if (playerRuntime?.TryGetLoaded(authorId) is { } online)
        {
            var av = string.IsNullOrWhiteSpace(online.State.Settings.Avatar) ? dbAvatar : online.State.Settings.Avatar;
            return (av, Math.Max(1, online.State.Progression.RealmLevel));
        }
        return (dbAvatar, Math.Max(1, dbRealmLevel));
    }

    private async ValueTask<GuidePostDetailDto> MapDetailDtoAsync(GuidePostRow row, CancellationToken ct)
    {
        var (authorAvatar, authorRealm) = ResolveAuthorAppearance(row.AuthorId, row.AuthorAvatar, row.AuthorRealmLevel);
        var bestRanks = await leaderboards.GetBestPersonalRanksAsync([row.AuthorId], LeaderboardAssembler.TopN, ct).ConfigureAwait(false);
        bestRanks.TryGetValue(row.AuthorId, out var topRank);
        var locked = !row.IsUnlockedByViewer;
        return new GuidePostDetailDto(
            PostId: row.PostId.ToString("D"),
            AuthorId: row.AuthorId.ToString("D"),
            AuthorName: row.AuthorName,
            AuthorAvatar: authorAvatar,
            AuthorRealmLevel: authorRealm,
            AuthorZongmenPrefix: row.AuthorZongmenPrefix,
            AuthorTopRank: topRank,
            Title: row.Title,
            Summary: row.Summary,
            Body: locked ? null : row.Body,
            UnlockPrice: row.UnlockPrice,
            Locked: locked,
            IsUnlocked: row.IsUnlockedByViewer,
            LikedByMe: row.LikedByViewer,
            MyTippedStones: row.ViewerTippedStones,
            ViewCount: row.ViewCount,
            LikeCount: row.LikeCount,
            TipperCount: row.TipperCount,
            TotalRewardStones: row.TotalRewardStones,
            HotScore: PostgresGuideRepository.ComputeHotScore(row.ViewCount, row.LikeCount, row.TipperCount, row.TotalRewardStones),
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt);
    }

    public static int CountUnicodeRunes(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
            count++;
        return count;
    }

    public static (string Title, string Summary, string Body, double UnlockPrice) ValidatePostContent(
        string? rawTitle,
        string? rawSummary,
        string? rawBody,
        double rawUnlockPrice)
    {
        var title = NormalizeLineEndings(rawTitle).Trim();
        var titleLen = CountUnicodeRunes(title);
        if (titleLen < 1 || titleLen > MaxTitleRunes)
            throw new GuideOperationException(GuideOperationFailure.InvalidArgument, $"攻略标题须为 1 至 {MaxTitleRunes} 字。");
        if (title.Contains('\n') || title.Contains('\r'))
            throw new GuideOperationException(GuideOperationFailure.InvalidArgument, "攻略标题不得包含换行符。");

        var summary = NormalizeLineEndings(rawSummary).Trim();
        var summaryLen = CountUnicodeRunes(summary);
        if (summaryLen > MaxSummaryRunes)
            throw new GuideOperationException(GuideOperationFailure.InvalidArgument, $"攻略导语最多 {MaxSummaryRunes} 字。");

        var body = NormalizeLineEndings(rawBody).Trim();
        var bodyLen = CountUnicodeRunes(body);
        if (bodyLen < 1 || bodyLen > MaxBodyRunes)
            throw new GuideOperationException(GuideOperationFailure.InvalidArgument, $"攻略正文须为 1 至 {MaxBodyRunes:N0} 字（当前 {bodyLen:N0} 字）。");

        if (!double.IsFinite(rawUnlockPrice) || rawUnlockPrice < 0d
            || rawUnlockPrice > PostgresGuideRepository.MaxPriceOrTipStones
            || Math.Floor(rawUnlockPrice) != rawUnlockPrice)
        {
            throw new GuideOperationException(GuideOperationFailure.InvalidArgument, $"解锁门槛须为 0 至 {PostgresGuideRepository.MaxPriceOrTipStones:N0} 的整数灵石。");
        }

        return (title, summary, body, rawUnlockPrice);
    }

    private static double ValidatePositiveIntegerStones(double amount, string fieldName)
    {
        if (!double.IsFinite(amount) || amount < 1d
            || amount > PostgresGuideRepository.MaxPriceOrTipStones
            || Math.Floor(amount) != amount)
        {
            throw new GuideOperationException(GuideOperationFailure.InvalidArgument, $"{fieldName}须为 1 至 {PostgresGuideRepository.MaxPriceOrTipStones:N0} 的整数灵石。");
        }
        return amount;
    }

    private static string NormalizeLineEndings(string? input)
        => string.IsNullOrEmpty(input) ? string.Empty : input.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
