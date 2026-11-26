using System.Text.Json;
using Microsoft.Net.Http.Headers;
using Protocol;

namespace Server.Clean;

/// <summary>全表行情统计缓存快照：包含原始行、预编码 UTF-8 JSON 正文与弱 ETag。</summary>
public sealed record MarketStatsCachedPayload(
    IReadOnlyList<MarketItemStatsSnapshot> Rows,
    byte[] AllStatsJsonUtf8,
    EntityTagHeaderValue ETag);

/// <summary>
/// market.item_stats 全表短 TTL 共享缓存：行情悬浮卡按 15 秒轮询、行情页拉全表，
/// 每个在线客户端独立直读会把同一份分钟级聚合的只读小表放大成重复查询与重复序列化；
/// 这里以 10 秒 TTL 共享全表快照、预编码 JSON 字节与弱 ETag，未变更轮询直接以 304 零正文返回。
/// </summary>
public sealed class MarketStatsCache
{
    private readonly object _sync = new();
    private MarketStatsCachedPayload? _payload;
    private DateTimeOffset _loadedAt;

    /// <summary>缓存有效期：行情统计本身按分钟级聚合刷新，10 秒共享窗口与前端 15 秒轮询节奏匹配。</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);

    /// <summary>读取全表原始行快照；兼容既有调用方。</summary>
    public async ValueTask<IReadOnlyList<MarketItemStatsSnapshot>> GetAsync(PostgresMarketRepository store, CancellationToken cancellationToken)
        => (await GetPayloadAsync(store, cancellationToken).ConfigureAwait(false)).Rows;

    /// <summary>读取含预编码 JSON 与弱 ETag 的全表缓存载荷；过期时回源重建一次。</summary>
    public async ValueTask<MarketStatsCachedPayload> GetPayloadAsync(PostgresMarketRepository store, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_payload is not null && DateTimeOffset.UtcNow - _loadedAt < Ttl) return _payload;
        }
        var rows = await store.LoadItemStatsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var dtos = rows.Select(MarketDtoMapper.ToStats).ToArray();
        var jsonUtf8 = JsonSerializer.SerializeToUtf8Bytes(dtos, ProtocolJson.Options);
        var payload = new MarketStatsCachedPayload(rows, jsonUtf8, CatalogHttpCache.ComputeWeakETag(jsonUtf8));
        lock (_sync)
        {
            _payload = payload;
            _loadedAt = DateTimeOffset.UtcNow;
        }
        return payload;
    }
}
