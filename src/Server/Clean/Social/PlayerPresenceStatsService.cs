using Domain.Player;
using Protocol;

namespace Server.Clean.Social;

/// <summary>
/// 修士存在感与在线/挂机统计服务契约。
/// </summary>
public interface IPlayerPresenceStatsService
{
    /// <summary>获取当前在线修士与48小时离线挂机修士统计（离线挂机带短缓存保护）。</summary>
    ValueTask<PresenceStatsDto> GetStatsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 修士存在感与在线/挂机统计服务实现：
/// 1. 在线人数（OnlineCount）：由 OnlinePresenceRegistry 并发内存维护，实时精准；
/// 2. 离线挂机人数（OfflineIdleCount）：断线且 offline_since 在 48h 收益上限内（offline_since >= now - 48h），
///    排除彻底离线（>48h）及已被封禁（suspended）的修士，并剔除当前已在线玩家；
/// 3. 短缓存防击穿：离线挂机人数每 10 秒异步刷新一次，高频心跳 Pong 零数据库压力。
/// </summary>
public sealed class PlayerPresenceStatsService(
    OnlinePresenceRegistry presence,
    ISocialRepository repository,
    IPlayerSimulationClock clock) : IPlayerPresenceStatsService
{
    private static readonly TimeSpan CacheInterval = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastRefreshTicks = long.MinValue;
    private int _cachedOfflineIdleCount;
    private int _cachedOfflineTotalCount;

    /// <inheritdoc />
    public async ValueTask<PresenceStatsDto> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var onlineCount = presence.OnlineCount;
        var nowTicks = clock.UtcNow.UtcTicks;
        var lastTicks = Volatile.Read(ref _lastRefreshTicks);

        if (nowTicks - lastTicks < CacheInterval.Ticks && lastTicks != long.MinValue)
        {
            return new PresenceStatsDto(onlineCount, Volatile.Read(ref _cachedOfflineIdleCount), 0, Volatile.Read(ref _cachedOfflineTotalCount));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            nowTicks = clock.UtcNow.UtcTicks;
            if (nowTicks - _lastRefreshTicks < CacheInterval.Ticks && _lastRefreshTicks != long.MinValue)
            {
                return new PresenceStatsDto(onlineCount, _cachedOfflineIdleCount, 0, _cachedOfflineTotalCount);
            }

            var cutoff = clock.UtcNow - PlayerLifecycleRules.OfflineIdleLimit;
            var onlineIds = presence.SnapshotOnlinePlayerIds();
            var counts = await repository.CountOfflinePlayersAsync(cutoff, onlineIds, cancellationToken);

            _cachedOfflineIdleCount = counts.IdleOffline;
            _cachedOfflineTotalCount = counts.TotalOffline;
            Volatile.Write(ref _lastRefreshTicks, nowTicks);
            return new PresenceStatsDto(onlineCount, counts.IdleOffline, 0, counts.TotalOffline);
        }
        catch when (_lastRefreshTicks != long.MinValue)
        {
            // 数据库偶发抖动时优雅回退最后一次缓存结果
            return new PresenceStatsDto(onlineCount, _cachedOfflineIdleCount, 0, _cachedOfflineTotalCount);
        }
        finally
        {
            _gate.Release();
        }
    }
}
