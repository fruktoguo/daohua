namespace Server.Clean;

/// <summary>有硬上限的短期重试缓存。淘汰只允许令调用方重新验证，不可存放权威状态或提交围栏。</summary>
internal sealed class ExpiringRetryCache<TKey, TValue>(int capacity, TimeSpan retention, IEqualityComparer<TKey>? comparer = null)
    where TKey : notnull
{
    private readonly object _sync = new();
    private readonly Dictionary<TKey, Entry> _entries = new(comparer);
    private readonly SortedSet<Entry> _byExpiry = new(Comparer<Entry>.Create((a, b) =>
        a.ExpiresAt == b.ExpiresAt ? a.Ordinal.CompareTo(b.Ordinal) : a.ExpiresAt.CompareTo(b.ExpiresAt)));
    private long _ordinal;

    private sealed record Entry(TKey Key, TValue Value, DateTimeOffset ExpiresAt, long Ordinal);

    public bool TryGet(TKey key, DateTimeOffset now, out TValue value)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > now)
                {
                    value = entry.Value;
                    return true;
                }
                RemoveCore(entry);
            }
            value = default!;
            return false;
        }
    }

    public void Set(TKey key, TValue value, DateTimeOffset now) => AddOrUpdate(key, value, static (_, next) => next, now);

    public void AddOrUpdate(TKey key, TValue value, Func<TValue, TValue, TValue> update, DateTimeOffset now)
    {
        lock (_sync)
        {
            PruneCore(now);
            if (_entries.TryGetValue(key, out var previous))
            {
                value = update(previous.Value, value);
                RemoveCore(previous);
            }
            else if (_entries.Count >= capacity)
                RemoveCore(_byExpiry.Min!);

            var entry = new Entry(key, value, now + retention, ++_ordinal);
            _entries.Add(key, entry);
            _byExpiry.Add(entry);
        }
    }

    public void Remove(TKey key)
    {
        lock (_sync)
            if (_entries.TryGetValue(key, out var entry)) RemoveCore(entry);
    }

    public void PruneExpired(DateTimeOffset now)
    {
        lock (_sync) PruneCore(now);
    }

    private void PruneCore(DateTimeOffset now)
    {
        while (_byExpiry.Min is { } entry && entry.ExpiresAt <= now) RemoveCore(entry);
    }

    private void RemoveCore(Entry entry)
    {
        _byExpiry.Remove(entry);
        _entries.Remove(entry.Key);
    }
}
