using System.Collections.Concurrent;
using Domain.GameData;
using Domain.Persistence;
using Domain.Player;
using Protocol;

namespace Server.Clean.Social;

/// <summary>
/// 道行/身家（valuation）计算服务：招募匹配与名片展示用的量化参照，不是战斗属性（docs/design/总纲/21 §21.3）。
/// 口径：委托 <see cref="ValuationMath.ComputeDaohang"/> 单一计算真源（原身家 Compute 保留不删除但暂不启用）；
/// 数据源：已装载玩家读运行时快照，离线玩家读 progression + loadout 分段（含刷盘队列叠加）；
/// 入队裁决与匹配自动并入使用实时值，列表展示允许 ≤30s 的缓存。
/// </summary>
public sealed class PartyValuationService
{
    /// <summary>展示路径缓存的寿命。</summary>
    private static readonly TimeSpan DisplayCacheTtl = TimeSpan.FromSeconds(30);

    private readonly IPlayerRuntime _runtime;
    private readonly PostgresSectionRepository _sections;
    private readonly MarketRuntime? _market;
    private readonly GameDataCatalog _catalog;
    /// <summary>装备回收定价参数簿预热缓存（保留供旧身家 Compute 兼容）。</summary>
    private readonly ValuationMath.EquipmentSellPriceBook _sellPriceBook;
    /// <summary>展示缓存：playerId → (构成细分结果, TickCount64 过期点)。</summary>
    private readonly ConcurrentDictionary<Guid, (ValuationResult Value, long ExpiresAt)> _displayCache = new();

    /// <summary>构造道行估值服务。</summary>
    /// <param name="runtime">玩家运行时（已装载快照只读）。</param>
    /// <param name="sections">分段仓储（离线玩家 progression/loadout 读取）。</param>
    /// <param name="market">市场运行时（保留供旧身家逻辑兼容）。</param>
    /// <param name="catalog">游戏配表（装备等级、功法与特质消耗）。</param>
    public PartyValuationService(IPlayerRuntime runtime, PostgresSectionRepository sections, MarketRuntime? market, GameDataCatalog catalog)
    {
        _runtime = runtime;
        _sections = sections;
        _market = market;
        _catalog = catalog;
        _sellPriceBook = ValuationMath.BuildSellPriceBook(catalog);
    }

    /// <summary>实时道行细分：入队裁决、名片构建与匹配自动并入使用，不走缓存。</summary>
    /// <param name="playerId">玩家 ID。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>道行细分结果；玩家无任何分段时各项为 0。</returns>
    public async ValueTask<ValuationResult> ComputeBreakdownAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var (progression, loadout) = await LoadPlayerDataAsync(playerId, cancellationToken);
        return progression is null && loadout is null ? default : ComputeDaohang(progression, loadout);
    }

    /// <summary>实时总道行便捷方法：仅需总值比较门槛处使用。</summary>
    public async ValueTask<double> ComputeAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var result = await ComputeBreakdownAsync(playerId, cancellationToken);
        return result.Total;
    }

    /// <summary>批量展示道行细分：≤30s 缓存摊薄大厅/队伍面板的重复计算，缺席玩家计 0。</summary>
    /// <param name="playerIds">玩家 ID 集合。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>playerId → 道行细分结果的完整映射。</returns>
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

        var playerData = await LoadPlayersDataAsync(misses, cancellationToken);
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
            var value = playerData.TryGetValue(playerId, out var pair)
                && (pair.Progression is not null || pair.Loadout is not null)
                ? ComputeDaohang(pair.Progression, pair.Loadout)
                : default;
            _displayCache[playerId] = (value, now + (long)DisplayCacheTtl.TotalMilliseconds);
            result[playerId] = value;
        }
        return result;
    }

    /// <summary>个人展示道行（缓存版单点入口）。</summary>
    public async ValueTask<ValuationResult> ComputeDisplayAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var values = await ComputeDisplayAsync([playerId], cancellationToken);
        return values.TryGetValue(playerId, out var value) ? value : default;
    }

    /// <summary>读取单个玩家的境界与装配文档：已装载走运行时快照，否则读持久化分段。</summary>
    private async ValueTask<(PlayerProgressionState? Progression, PlayerLoadoutDocument? Loadout)> LoadPlayerDataAsync(
        Guid playerId, CancellationToken cancellationToken)
    {
        if (_runtime.TryGetLoaded(playerId) is { } snapshot)
            return (snapshot.State.Progression, snapshot.State.Loadout);
        var loadoutSection = await _sections.LoadSectionAsync(playerId, PlayerSectionKinds.Loadout, cancellationToken);
        var progSection = await _sections.LoadSectionAsync(playerId, PlayerSectionKinds.Progression, cancellationToken);
        return (
            progSection is null ? null : SectionCodec.DecodeProgression(progSection),
            loadoutSection is null ? null : SectionCodec.DecodeLoadout(loadoutSection));
    }

    /// <summary>批量读取境界与装配文档：在线玩家用运行时快照，其余一次往返分别批量读 loadout 与 progression 分段。</summary>
    private async ValueTask<IReadOnlyDictionary<Guid, (PlayerProgressionState? Progression, PlayerLoadoutDocument? Loadout)>> LoadPlayersDataAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, (PlayerProgressionState? Progression, PlayerLoadoutDocument? Loadout)>(playerIds.Count);
        var offline = new List<Guid>();
        foreach (var playerId in playerIds)
        {
            if (_runtime.TryGetLoaded(playerId) is { } snapshot)
                result[playerId] = (snapshot.State.Progression, snapshot.State.Loadout);
            else
                offline.Add(playerId);
        }
        if (offline.Count == 0) return result;
        var loadoutSections = await _sections.LoadSectionsAsync(offline, PlayerSectionKinds.Loadout, cancellationToken);
        var progSections = await _sections.LoadSectionsAsync(offline, PlayerSectionKinds.Progression, cancellationToken);
        foreach (var playerId in offline)
        {
            var prog = progSections.TryGetValue(playerId, out var pSec) ? SectionCodec.DecodeProgression(pSec) : null;
            var loadout = loadoutSections.TryGetValue(playerId, out var lSec) ? SectionCodec.DecodeLoadout(lSec) : null;
            result[playerId] = (prog, loadout);
        }
        return result;
    }

    /// <summary>按当前道行口径计算境界根基、战斗装备、携带功法与携带特质。</summary>
    private ValuationResult ComputeDaohang(PlayerProgressionState? progression, PlayerLoadoutDocument? loadout)
        => ValuationMath.ComputeDaohang(progression?.RealmLevel ?? 1, progression, loadout, _catalog);

    /// <summary>【保留未用】按原身家口径合计战斗装备、功法与技艺工具价值。</summary>
    private ValuationResult Compute(PlayerLoadoutDocument loadout)
        => ValuationMath.Compute(loadout, _catalog, _sellPriceBook, _market);
}

/// <summary>道行/身家细分计算结果（Tools 保留供旧身家兼容）。</summary>
public readonly record struct ValuationResult(
    double Equipment,
    double Abilities,
    double Tools,
    double RealmAndGenji = 0,
    double Traits = 0)
{
    public double Total => ValuationMath.RoundDaohang(RealmAndGenji + Equipment + Abilities + Tools + Traits);
    public ValuationBreakdownDto ToDto() => new(Equipment, Abilities, Tools, RealmAndGenji, Traits);
}
