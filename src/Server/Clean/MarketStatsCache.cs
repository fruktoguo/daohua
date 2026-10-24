namespace Server.Clean;

/// <summary>
/// market.item_stats 全表短 TTL 共享缓存：行情悬浮卡按 15 秒轮询、行情页拉全表，
/// 每个在线客户端独立直读会把同一份分钟级聚合的只读小表放大成重复查询；
/// 这里以 10 秒 TTL 的全表快照共享给所有请求，单品种查询在内存中过滤。
/// </summary>
public sealed class MarketStatsCache
{
    private readonly object _sync = new();
    private IReadOnlyList<MarketItemStatsSnapshot>? _snapshot;
    private DateTimeOffset _loadedAt;

    /// <summary>缓存有效期：行情统计本身按分钟级聚合刷新，10 秒共享窗口与前端 15 秒轮询节奏匹配。</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);

    /// <summary>读取全表快照；过期时回源重建。回源竞态下可能少量重复查询，属可接受的只读放大。</summary>
    public async ValueTask<IReadOnlyList<MarketItemStatsSnapshot>> GetAsync(PostgresMarketRepository store, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_snapshot is not null && DateTimeOffset.UtcNow - _loadedAt < Ttl) return _snapshot;
        }
        var rows = await store.LoadItemStatsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _snapshot = rows;
            _loadedAt = DateTimeOffset.UtcNow;
        }
        return rows;
    }
}
