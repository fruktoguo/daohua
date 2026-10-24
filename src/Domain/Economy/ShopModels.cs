namespace Domain.Economy;

/// <summary>
/// 系统商店商品定义（官价直售，不参与坊市撮合）。
/// </summary>
/// <param name="Id">商品标识，必须以 <c>shop.</c> 前缀开头。</param>
/// <param name="ItemId">发放的物品 ID。</param>
/// <param name="UnitPrice">单价（灵石），正整数。</param>
/// <param name="DailyLimit">每日限购件数；null 表示不限购。</param>
/// <param name="SortOrder">货架排序权重，升序。</param>
public sealed record ShopOfferDefinition(
    string Id,
    string ItemId,
    double UnitPrice,
    int? DailyLimit = null,
    int SortOrder = 0)
{
    /// <summary>商品标识统一前缀。</summary>
    public const string IdPrefix = "shop.";

    /// <summary>是否受每日限购约束。</summary>
    public bool IsDailyLimited => DailyLimit is > 0;
}

/// <summary>
/// 系统商店商品目录：只读、按排序权重稳定输出，加载期完成配表校验。
/// </summary>
public sealed class ShopCatalog
{
    /// <summary>空目录（未上架任何商品）。</summary>
    public static ShopCatalog Empty { get; } = new([]);

    private readonly ShopOfferDefinition[] _ordered;
    private readonly Dictionary<string, ShopOfferDefinition> _byId;

    /// <summary>用商品定义集合构建目录；任一条目不合法即抛出领域异常，拒绝带病上架。</summary>
    /// <param name="offers">商品定义集合。</param>
    public ShopCatalog(IEnumerable<ShopOfferDefinition> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);
        var ordered = offers
            .OrderBy(offer => offer.SortOrder)
            .ThenBy(offer => offer.Id, StringComparer.Ordinal)
            .ToArray();
        _byId = new Dictionary<string, ShopOfferDefinition>(ordered.Length, StringComparer.Ordinal);
        foreach (var offer in ordered)
        {
            if (offer is null)
                throw new DomainException(DomainErrorCode.InvalidArgument, "商店商品定义不能为空。");
            if (string.IsNullOrWhiteSpace(offer.Id) || !offer.Id.StartsWith(ShopOfferDefinition.IdPrefix, StringComparison.Ordinal)
                || offer.Id.Length > 64)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"商店商品 ID 无效：{offer.Id}。");
            if (string.IsNullOrWhiteSpace(offer.ItemId) || offer.ItemId.Length > 128)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"商店商品 {offer.Id} 的物品 ID 无效。");
            if (!double.IsFinite(offer.UnitPrice) || offer.UnitPrice <= 0 || offer.UnitPrice != Math.Floor(offer.UnitPrice))
                throw new DomainException(DomainErrorCode.InvalidArgument, $"商店商品 {offer.Id} 的单价必须为正整数。");
            if (offer.DailyLimit is <= 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, $"商店商品 {offer.Id} 的每日限购必须为正整数。");
            if (!_byId.TryAdd(offer.Id, offer))
                throw new DomainException(DomainErrorCode.Duplicate, $"商店商品 ID 重复：{offer.Id}。");
        }
        _ordered = ordered;
    }

    /// <summary>全部商品（按排序权重升序）。</summary>
    public IReadOnlyList<ShopOfferDefinition> All => _ordered;

    /// <summary>商品数量。</summary>
    public int Count => _ordered.Length;

    /// <summary>按商品标识查表；不存在返回 false。</summary>
    /// <param name="offerId">商品标识。</param>
    /// <param name="offer">命中的商品定义。</param>
    public bool TryGet(string? offerId, out ShopOfferDefinition offer)
    {
        if (!string.IsNullOrWhiteSpace(offerId) && _byId.TryGetValue(offerId, out var found))
        {
            offer = found;
            return true;
        }
        offer = null!;
        return false;
    }
}

/// <summary>
/// 系统商店每日限购口径：与行动统计每日账本一致，按 UTC+8 自然日切分。
/// </summary>
public static class ShopDailyWindow
{
    /// <summary>UTC+8 相对 UTC 的秒偏移。</summary>
    public const double UtcOffsetSeconds = 28_800d;

    /// <summary>一秒的秒数（自然日长度）。</summary>
    public const double SecondsPerDay = 86_400d;

    /// <summary>由时间点换算 UTC+8 自然日序号（1970-01-01 08:00 UTC+8 为第 0 天）。</summary>
    /// <param name="at">任意时刻。</param>
    /// <returns>自然日序号。</returns>
    public static long DayIndexOf(DateTimeOffset at)
        => (long)Math.Floor((at.ToUnixTimeSeconds() + UtcOffsetSeconds) / SecondsPerDay);

    /// <summary>当前时刻所在的自然日序号。</summary>
    public static long CurrentDayIndex() => DayIndexOf(DateTimeOffset.UtcNow);
}

/// <summary>
/// 系统商店购买规则：数量上限与限购余量换算。
/// </summary>
public static class ShopPurchaseRules
{
    /// <summary>单次购买件数硬上限（防止一次请求放大事务与响应体）。</summary>
    public const int MaxQuantityPerPurchase = 999;

    /// <summary>购买件数是否合法：正整数且不超过单次上限。</summary>
    /// <param name="quantity">请求件数。</param>
    public static bool IsQuantityValid(int quantity)
        => quantity >= 1 && quantity <= MaxQuantityPerPurchase;

    /// <summary>限购商品当日剩余可购件数；不限购商品返回 <see cref="int.MaxValue"/>。</summary>
    /// <param name="offer">商品定义。</param>
    /// <param name="purchasedToday">当日已购件数。</param>
    public static int RemainingToday(ShopOfferDefinition offer, int purchasedToday)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (offer.DailyLimit is not { } limit) return int.MaxValue;
        return Math.Max(0, limit - Math.Max(0, purchasedToday));
    }
}
