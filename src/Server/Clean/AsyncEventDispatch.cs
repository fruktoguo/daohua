using System.Collections.Concurrent;

namespace Server.Clean;

/// <summary>
/// 异步多播事件隔离派发工具：逐个调用订阅者回调并隔离单个订阅者的异常（含客户端断连触发的取消异常），
/// 避免前一个订阅者失败阻断同一批后续在线会话的增量推送或事件响应。
/// </summary>
public static class AsyncEventDispatch
{
    /// <summary>逐一同步触发单参数多播订阅者，并对单订阅者异常做隔离记录。</summary>
    public static void InvokeEach<T1>(
        Action<T1>? handlers,
        T1 arg1,
        ILogger? logger,
        string errorMessage)
    {
        if (handlers is null) return;
        foreach (var candidate in handlers.GetInvocationList())
        {
            if (candidate is not Action<T1> handler) continue;
            try
            {
                handler(arg1);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, errorMessage);
            }
        }
    }

    /// <summary>逐一异步触发单参数多播订阅者，并对单订阅者异常做隔离记录。</summary>
    public static async ValueTask InvokeEachAsync<T1>(
        Func<T1, ValueTask>? handlers,
        T1 arg1,
        ILogger? logger,
        string errorMessage)
    {
        if (handlers is null) return;
        foreach (var candidate in handlers.GetInvocationList())
        {
            if (candidate is not Func<T1, ValueTask> handler) continue;
            try
            {
                await handler(arg1).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                logger?.LogDebug(ex, errorMessage);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, errorMessage);
            }
        }
    }

    /// <summary>逐一异步触发双参数多播订阅者，并对单订阅者异常做隔离记录。</summary>
    public static async ValueTask InvokeEachAsync<T1, T2>(
        Func<T1, T2, ValueTask>? handlers,
        T1 arg1,
        T2 arg2,
        ILogger? logger,
        string errorMessage)
    {
        if (handlers is null) return;
        foreach (var candidate in handlers.GetInvocationList())
        {
            if (candidate is not Func<T1, T2, ValueTask> handler) continue;
            try
            {
                await handler(arg1, arg2).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                logger?.LogDebug(ex, errorMessage);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, errorMessage);
            }
        }
    }
}

/// <summary>
/// 按键引用计数门闩表：按需分配 <see cref="SemaphoreSlim"/> 并在无等待者与持有者时安全释放移除，
/// 防止长时间运行中每玩家/每会话门闩字典无限膨胀。
/// </summary>
public sealed class RefCountedGateMap<TKey> where TKey : notnull
{
    internal sealed class GateEntry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    private readonly Lock _sync = new();
    private readonly Dictionary<TKey, GateEntry> _entries;

    /// <summary>初始化按键引用计数门闩表。</summary>
    public RefCountedGateMap(IEqualityComparer<TKey>? comparer = null)
    {
        _entries = new Dictionary<TKey, GateEntry>(comparer);
    }

    /// <summary>当前仍存活的门闩键数量（供测试与诊断观察内存回收）。</summary>
    public int ActiveCount
    {
        get
        {
            lock (_sync)
                return _entries.Count;
        }
    }

    /// <summary>等待并获取指定键的独占租约；取消或异常时自动回退引用计数并在归零时释放。</summary>
    public async ValueTask<Releaser> AcquireAsync(TKey key, CancellationToken cancellationToken = default)
    {
        GateEntry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new GateEntry();
                _entries[key] = entry;
            }
            entry.RefCount++;
        }

        var entered = false;
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return new Releaser(this, key, entry);
        }
        catch
        {
            ReleaseEntry(key, entry, entered);
            throw;
        }
    }

    /// <summary>按传入键排序去重后顺序获取多把门闩，任一步失败即逆序释放已获取租约，防止 AB-BA 死锁与半程泄漏。</summary>
    public async ValueTask<MultiReleaser> AcquireOrderedAsync(
        IEnumerable<TKey> keys,
        IComparer<TKey>? comparer = null,
        CancellationToken cancellationToken = default)
    {
        var ordered = keys.Distinct(_entries.Comparer).OrderBy(static k => k, comparer ?? Comparer<TKey>.Default).ToArray();
        var acquired = new List<Releaser>(ordered.Length);
        try
        {
            foreach (var key in ordered)
            {
                acquired.Add(await AcquireAsync(key, cancellationToken).ConfigureAwait(false));
            }
            return new MultiReleaser(acquired);
        }
        catch
        {
            for (var i = acquired.Count - 1; i >= 0; i--)
                acquired[i].Dispose();
            throw;
        }
    }

    private void ReleaseEntry(TKey key, GateEntry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore)
            entry.Semaphore.Release();

        lock (_sync)
        {
            entry.RefCount--;
            if (entry.RefCount == 0 && _entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    /// <summary>单个键的独占门闩释放器。</summary>
    public struct Releaser : IDisposable
    {
        private RefCountedGateMap<TKey>? _owner;
        private TKey _key;
        private GateEntry? _entry;

        internal Releaser(RefCountedGateMap<TKey> owner, TKey key, GateEntry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        /// <summary>释放底层信号量并在无人引用时从表内回收。</summary>
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            var entry = _entry;
            if (owner is null || entry is null) return;
            _entry = null;
            owner.ReleaseEntry(_key, entry, releaseSemaphore: true);
        }
    }

    /// <summary>多键有序独占门闩释放器（释放时按逆序归还）。</summary>
    public sealed class MultiReleaser : IDisposable
    {
        private List<Releaser>? _releasers;

        internal MultiReleaser(List<Releaser> releasers)
        {
            _releasers = releasers;
        }

        /// <summary>按获取逆序释放全部已持有门闩。</summary>
        public void Dispose()
        {
            var releasers = Interlocked.Exchange(ref _releasers, null);
            if (releasers is null) return;
            for (var i = releasers.Count - 1; i >= 0; i--)
                releasers[i].Dispose();
        }
    }
}

/// <summary>后台托管服务循环安全辅助：安全等待延时或周期定时器，只在宿主停机取消时返回 false，避免停机崩溃堆栈或未捕获取消异常。</summary>
public static class HostedLoopSupport
{
    /// <summary>按给定时间间隔退避等待；若因停机令牌触发取消则返回 false，否则返回 true。</summary>
    public static async Task<bool> DelayQuietlyAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested) return false;
        try
        {
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            return !stoppingToken.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>安全等待 PeriodicTimer 下一次触发；若因停机令牌取消或定时器已销毁则返回 false。</summary>
    public static async Task<bool> WaitNextTickQuietlyAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested) return false;
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
