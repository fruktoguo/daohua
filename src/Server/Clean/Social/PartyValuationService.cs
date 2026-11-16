using System.Collections.Concurrent;
using System.Collections.Frozen;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Protocol;

namespace Server.Clean.Social;

/// <summary>
/// 身家（valuation）估值服务：招募匹配用的量化参照，不是战斗属性（docs/design/总纲/21 §21.3）。
/// 口径：委托 <see cref="ValuationMath"/> 单一计算真源；
/// 数据源：已装载玩家读运行时快照，离线玩家读 loadout 分段（含刷盘队列叠加）；
/// 入队裁决与匹配自动并入使用实时值，列表展示允许 ≤30s 的估值缓存。
/// </summary>
public sealed class PartyValuationService
{
    /// <summary>展示路径估值缓存的寿命。</summary>
    private static readonly TimeSpan DisplayCacheTtl = TimeSpan.FromSeconds(30);

    private readonly IPlayerRuntime _runtime;
    private readonly PostgresSectionRepository _sections;
    private readonly MarketRuntime? _market;
    private readonly GameDataCatalog _catalog;
    /// <summary>装备白板商店基础回收售价预热缓存（原料成本×2.0工匠增值），构造期一次性构建不可变映射，彻底杜绝并发写竞争。</summary>
    private readonly FrozenDictionary<string, double> _baseSellPrices;
    /// <summary>展示估值缓存：playerId → (估值细分结果, TickCount64 过期点)。</summary>
    private readonly ConcurrentDictionary<Guid, (ValuationResult Value, long ExpiresAt)> _displayCache = new();

    /// <summary>构造身家估值服务。</summary>
    /// <param name="runtime">玩家运行时（已装载快照只读）。</param>
    /// <param name="sections">分段仓储（离线玩家 loadout 读取）。</param>
    /// <param name="market">市场运行时（卖一档价只读出口）。</param>
    /// <param name="catalog">游戏配表（物品定义与回收价）。</param>
    public PartyValuationService(IPlayerRuntime runtime, PostgresSectionRepository sections, MarketRuntime? market, GameDataCatalog catalog)
    {
        _runtime = runtime;
        _sections = sections;
        _market = market;
        _catalog = catalog;
        _baseSellPrices = ValuationMath.BuildBaseSellPrices(catalog);
    }

    /// <summary>实时身家细分：入队裁决、名片构建与匹配自动并入使用，不走缓存。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>身家细分结果；玩家无装配分段时各项为 0。</returns>
    public async ValueTask<ValuationResult> ComputeBreakdownAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var loadout = await LoadLoadoutAsync(playerId, cancellationToken);
        return loadout is null ? default : Compute(loadout);
    }

    /// <summary>实时总身家便捷方法：仅需总值比较门槛处使用。</summary>
    public async ValueTask<double> ComputeAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var result = await ComputeBreakdownAsync(playerId, cancellationToken);
        return result.Total;
    }

    /// <summary>批量展示身家细分：≤30s 缓存摊薄大厅/队伍面板的重复估值，缺席玩家计 0。</summary>
    /// <param name="playerIds">玩家 ID 集合。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>playerId → 身家细分结果的完整映射。</returns>
    public async ValueTask<IReadOnlyDictionary<Guid, ValuationResult>> ComputeDisplayAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, ValuationResult>(playerIds.Count);
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
        if (_displayCache.Count > 2048)
        {
            foreach (var pair in _displayCache)
            {
                if (pair.Value.ExpiresAt <= now)
                    _displayCache.TryRemove(new KeyValuePair<Guid, (ValuationResult Value, long ExpiresAt)>(pair.Key, pair.Value));
            }
            if (_displayCache.Count > 4096)
                _displayCache.Clear();
        }
        foreach (var playerId in misses)
        {
            var value = loadouts.TryGetValue(playerId, out var document) && document is not null
                ? Compute(document)
                : default;
            _displayCache[playerId] = (value, now + (long)DisplayCacheTtl.TotalMilliseconds);
            result[playerId] = value;
        }
        return result;
    }

    /// <summary>个人展示身家（缓存版单点入口）。</summary>
    public async ValueTask<ValuationResult> ComputeDisplayAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var values = await ComputeDisplayAsync([playerId], cancellationToken);
        return values.TryGetValue(playerId, out var value) ? value : default;
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

    /// <summary>按口径合计战斗装备、功法与技艺工具价值；非有限结果归零。</summary>
    private ValuationResult Compute(PlayerLoadoutDocument loadout)
        => ValuationMath.Compute(loadout, _catalog, _baseSellPrices, _market);
}

/// <summary>身家估值细分计算结果。</summary>
public readonly record struct ValuationResult(double Equipment, double Abilities, double Tools)
{
    public double Total => Equipment + Abilities + Tools;
    public ValuationBreakdownDto ToDto() => new(Equipment, Abilities, Tools);
}
