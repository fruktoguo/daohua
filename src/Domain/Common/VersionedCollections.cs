using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Domain.Common;

/// <summary>
/// 带单调版本号的可变列表：任何结构性写入（增/删/改/清空/批量填充）递增 <see cref="Version"/>。
/// 聚合缓存失效检测依赖该版本号判定「自上次导出以来是否有写入」，免去逐项内容比对；
/// 只读操作（索引读取/枚举/查找）不影响版本号。枚举器沿用 List 的结构体枚举，高频遍历零分配。
/// </summary>
public sealed class VersionedList<T> : IList<T>, IReadOnlyList<T>
{
    private readonly List<T> _inner = [];

    /// <summary>内容修订号：写入严格单调递增，只读不变。</summary>
    internal long Version { get; private set; }

    private void NoteMutation() => Version = checked(Version + 1);

    /// <inheritdoc />
    public int Count => _inner.Count;
    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public T this[int index]
    {
        get => _inner[index];
        set
        {
            _inner[index] = value;
            NoteMutation();
        }
    }

    /// <summary>结构体枚举器：foreach 直接命中本重载，不产生接口装箱分配。</summary>
    public List<T>.Enumerator GetEnumerator() => _inner.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public int IndexOf(T item) => _inner.IndexOf(item);
    /// <inheritdoc />
    public bool Contains(T item) => _inner.Contains(item);
    /// <inheritdoc />
    public void CopyTo(T[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);
    /// <summary>复制为独立数组（与 List.ToArray 同语义）。</summary>
    public T[] ToArray() => _inner.ToArray();

    /// <inheritdoc />
    public void Add(T item)
    {
        _inner.Add(item);
        NoteMutation();
    }

    /// <summary>批量追加（与 List.AddRange 同语义；空序列追加也计一次写入，保守失效）。</summary>
    public void AddRange(IEnumerable<T> items)
    {
        _inner.AddRange(items);
        NoteMutation();
    }

    /// <inheritdoc />
    public void Insert(int index, T item)
    {
        _inner.Insert(index, item);
        NoteMutation();
    }

    /// <inheritdoc />
    public bool Remove(T item)
    {
        if (!_inner.Remove(item)) return false;
        NoteMutation();
        return true;
    }

    /// <summary>按条件批量移除（与 List.RemoveAll 同语义）；实际移除数为 0 时不递增版本号。</summary>
    public int RemoveAll(Predicate<T> match)
    {
        var removed = _inner.RemoveAll(match);
        if (removed > 0) NoteMutation();
        return removed;
    }

    /// <inheritdoc />
    public void RemoveAt(int index)
    {
        _inner.RemoveAt(index);
        NoteMutation();
    }

    /// <summary>清空；空表调用视为无写入（不产生失效）。</summary>
    public void Clear()
    {
        if (_inner.Count == 0) return;
        _inner.Clear();
        NoteMutation();
    }

    /// <summary>回滚检查点专用：把内容修订号写回备份值；调用方须先把内容恢复到与版本一致的状态。</summary>
    internal void RestoreVersion(long version) => Version = version;
}

/// <summary>
/// 带单调版本号的可变字典：语义同 <see cref="VersionedList{T}"/>。
/// 枚举顺序与底层 Dictionary 完全一致（插入序），导出字节序不因跟踪而改变。
/// </summary>
public sealed class VersionedDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _inner;

    public VersionedDictionary() : this(null) { }
    public VersionedDictionary(IEqualityComparer<TKey>? comparer)
    {
        _inner = comparer is null ? new() : new(comparer);
    }

    /// <summary>内容修订号：写入严格单调递增，只读不变。</summary>
    internal long Version { get; private set; }

    private void NoteMutation() => Version = checked(Version + 1);

    /// <inheritdoc />
    public int Count => _inner.Count;
    /// <inheritdoc />
    public bool IsReadOnly => false;
    /// <inheritdoc />
    public ICollection<TKey> Keys => _inner.Keys;
    /// <inheritdoc />
    public ICollection<TValue> Values => _inner.Values;
    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => _inner.Keys;
    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => _inner.Values;

    /// <inheritdoc />
    public TValue this[TKey key]
    {
        get => _inner[key];
        set
        {
            _inner[key] = value;
            NoteMutation();
        }
    }

    /// <summary>结构体枚举器：foreach 直接命中本重载，不产生接口装箱分配。</summary>
    public Dictionary<TKey, TValue>.Enumerator GetEnumerator() => _inner.GetEnumerator();
    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public bool ContainsKey(TKey key) => _inner.ContainsKey(key);
    /// <inheritdoc />
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => _inner.TryGetValue(key, out value);

    /// <inheritdoc />
    public void Add(TKey key, TValue value)
    {
        _inner.Add(key, value);
        NoteMutation();
    }

    /// <inheritdoc />
    public bool Remove(TKey key)
    {
        if (!_inner.Remove(key)) return false;
        NoteMutation();
        return true;
    }

    /// <summary>按键移除并取出值（与 Dictionary.Remove(key, out value) 同语义）；未命中不递增版本号。</summary>
    public bool Remove(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (!_inner.Remove(key, out value)) return false;
        NoteMutation();
        return true;
    }

    /// <summary>清空；空表调用视为无写入（不产生失效）。</summary>
    public void Clear()
    {
        if (_inner.Count == 0) return;
        _inner.Clear();
        NoteMutation();
    }

    void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item)
    {
        ((ICollection<KeyValuePair<TKey, TValue>>)_inner).Add(item);
        NoteMutation();
    }

    bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item)
        => ((ICollection<KeyValuePair<TKey, TValue>>)_inner).Contains(item);

    void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        => ((ICollection<KeyValuePair<TKey, TValue>>)_inner).CopyTo(array, arrayIndex);

    bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item)
    {
        if (!((ICollection<KeyValuePair<TKey, TValue>>)_inner).Remove(item)) return false;
        NoteMutation();
        return true;
    }

    /// <summary>回滚检查点专用：把内容修订号写回备份值；调用方须先把内容恢复到与版本一致的状态。</summary>
    internal void RestoreVersion(long version) => Version = version;
}
