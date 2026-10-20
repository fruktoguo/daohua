using System.Collections.Concurrent;
using System.Globalization;
using Domain.GameData;
using Domain.Market;
using Domain.Persistence;
using Domain.Player;

namespace Server.Clean.Social;

/// <summary>
/// 身家（valuation）估值服务：招募匹配用的量化参照，不是战斗属性（docs/design/总纲/21 §21.3）。
/// 口径：
///   身家 = Σ 已穿戴装备单价（强化 +N 按 "itemId:+N" 变体独立计价）
///        + Σ 每门已参悟功法（参悟等级 × 对应秘籍单价）；
///   单价 = 坊市市场参考价（最近 10 次成交单价的单值滑动平均，单笔多次购买算做一次） ?? 配表回收价 ItemDefinition.SellPrice ?? 0；
///   功法↔物品映射：ability.X → item.tome.X，配表无该秘籍时回落 item.fragment.X。
/// 数据源：已装载玩家读运行时快照，离线玩家读 loadout 分段（含刷盘队列叠加）；
/// 入队裁决与匹配自动并入使用实时值，列表展示允许 ≤30s 的估值缓存。
/// </summary>
public sealed class PartyValuationService
{
    /// <summary>展示路径估值缓存的寿命。</summary>
    private static readonly TimeSpan DisplayCacheTtl = TimeSpan.FromSeconds(30);
    /// <summary>功法 ID 前缀；去掉它即得到秘籍/残卷物品 ID 后缀。</summary>
    private const string AbilityIdPrefix = "ability.";
    /// <summary>秘籍物品 ID 前缀。</summary>
    private const string TomePrefix = "item.tome.";
    /// <summary>残卷物品 ID 前缀（秘籍缺配表时的回单价目）。</summary>
    private const string FragmentPrefix = "item.fragment.";

    private readonly IPlayerRuntime _runtime;
    private readonly PostgresSectionRepository _sections;
    private readonly MarketRuntime _market;
    private readonly GameDataCatalog _catalog;
    /// <summary>展示估值缓存：playerId → (身家, TickCount64 过期点)。</summary>
    private readonly ConcurrentDictionary<Guid, (double Value, long ExpiresAt)> _displayCache = new();

    /// <summary>构造身家估值服务。</summary>
    /// <param name="runtime">玩家运行时（已装载快照只读）。</param>
    /// <param name="sections">分段仓储（离线玩家 loadout 读取）。</param>
    /// <param name="market">市场运行时（卖一档价只读出口）。</param>
    /// <param name="catalog">游戏配表（物品定义与回收价）。</param>
    public PartyValuationService(IPlayerRuntime runtime, PostgresSectionRepository sections, MarketRuntime market, GameDataCatalog catalog)
    {
        _runtime = runtime;
        _sections = sections;
        _market = market;
        _catalog = catalog;
    }

    /// <summary>实时身家：入队裁决与匹配自动并入使用，不走缓存。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>身家估值；玩家无装配分段时返回 0。</returns>
    public async ValueTask<double> ComputeAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var loadout = await LoadLoadoutAsync(playerId, cancellationToken);
        return loadout is null ? 0 : Compute(loadout);
    }

    /// <summary>批量展示身家：≤30s 缓存摊薄大厅/队伍面板的重复估值，缺席玩家计 0。</summary>
    /// <param name="playerIds">玩家 ID 集合。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>playerId → 身家的完整映射。</returns>
    public async ValueTask<IReadOnlyDictionary<Guid, double>> ComputeDisplayAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, double>(playerIds.Count);
        var misses = new List<Guid>();
        var now = Environment.TickCount64;
        foreach (var playerId in playerIds)
        {
            if (_displayCache.TryGetValue(playerId, out var cached) && cached.ExpiresAt > now)
                result[playerId] = cached.Value;
            else
                misses.Add(playerId);
        }
        if (misses.Count == 0) return result;

        var loadouts = await LoadLoadoutsAsync(misses, cancellationToken);
        foreach (var playerId in misses)
        {
            var value = loadouts.TryGetValue(playerId, out var document) && document is not null
                ? Compute(document)
                : 0;
            _displayCache[playerId] = (value, now + (long)DisplayCacheTtl.TotalMilliseconds);
            result[playerId] = value;
        }
        return result;
    }

    /// <summary>个人展示身家（缓存版单点入口）。</summary>
    public async ValueTask<double> ComputeDisplayAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var values = await ComputeDisplayAsync([playerId], cancellationToken);
        return values.TryGetValue(playerId, out var value) ? value : 0;
    }

    /// <summary>读取单个玩家的装配文档：已装载走运行时快照，否则读持久化分段。</summary>
    private async ValueTask<PlayerLoadoutDocument?> LoadLoadoutAsync(Guid playerId, CancellationToken cancellationToken)
    {
        if (_runtime.TryGetLoaded(playerId) is { } snapshot) return snapshot.State.Loadout;
        var section = await _sections.LoadSectionAsync(playerId, PlayerSectionKinds.Loadout, cancellationToken);
        return section is null ? null : SectionCodec.DecodeLoadout(section);
    }

    /// <summary>批量装配文档：在线玩家用运行时快照，其余一次往返批量读 loadout 分段。</summary>
    private async ValueTask<IReadOnlyDictionary<Guid, PlayerLoadoutDocument?>> LoadLoadoutsAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, PlayerLoadoutDocument?>(playerIds.Count);
        var offline = new List<Guid>();
        foreach (var playerId in playerIds)
        {
            if (_runtime.TryGetLoaded(playerId) is { } snapshot) result[playerId] = snapshot.State.Loadout;
            else offline.Add(playerId);
        }
        if (offline.Count == 0) return result;
        var sections = await _sections.LoadSectionsAsync(offline, PlayerSectionKinds.Loadout, cancellationToken);
        foreach (var playerId in offline)
        {
            result[playerId] = sections.TryGetValue(playerId, out var section)
                ? SectionCodec.DecodeLoadout(section)
                : null;
        }
        return result;
    }

    /// <summary>按口径合计装备与功法价值；非有限结果归零。</summary>
    private double Compute(PlayerLoadoutDocument loadout)
    {
        var total = 0d;
        foreach (var slot in loadout.Equipment)
        {
            var variant = slot.EnhancementLevel > 0
                ? string.Create(CultureInfo.InvariantCulture, $"+{slot.EnhancementLevel:0}")
                : string.Empty;
            total += EquipmentUnitPrice(slot.ItemId, variant);
        }
        if (loadout.AbilityExperiences is not null)
        {
            foreach (var (abilityId, exp) in loadout.AbilityExperiences)
            {
                var level = AbilityExpCurve.LevelFor(exp);
                if (level < 1) continue;
                var suffix = abilityId.StartsWith(AbilityIdPrefix, StringComparison.Ordinal)
                    ? abilityId[AbilityIdPrefix.Length..]
                    : abilityId;
                total += level * (ItemUnitPrice(TomePrefix + suffix) ?? ItemUnitPrice(FragmentPrefix + suffix) ?? 0);
            }
        }
        return double.IsFinite(total) ? total : 0;
    }

    /// <summary>装备单价：配表已知装备才算价，市场参考价优先；无参考价按配表回收价兜底，无回收价计 0。</summary>
    private double EquipmentUnitPrice(string itemIdText, string variant)
    {
        if (!_catalog.RawEquipments.ContainsKey(itemIdText)) return 0;
        var instrument = new MarketInstrument(new ItemId(itemIdText), variant);
        var refPrice = _market.TryGetReferencePrice(instrument);
        if (refPrice is > 0) return refPrice.Value;
        if (_catalog.Items.TryGetValue(new ItemId(itemIdText), out var itemDef))
            return Math.Max(0, itemDef.SellPrice);
        return 0;
    }

    /// <summary>普通物品单价：配表未知返回 null（供秘籍→残卷回退判断），否则参考价优先、回收价兜底。</summary>
    private double? ItemUnitPrice(string itemIdText)
    {
        var itemId = new ItemId(itemIdText);
        if (!_catalog.Items.TryGetValue(itemId, out var definition)) return null;
        return _market.TryGetReferencePrice(new MarketInstrument(itemId)) ?? Math.Max(0, definition.SellPrice);
    }
}
