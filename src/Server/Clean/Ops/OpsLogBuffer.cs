namespace Server.Clean.Ops;

/// <summary>运维面板日志条目（已渲染为纯文本，异常展开为字符串）。</summary>
/// <param name="Timestamp">写入时刻（UTC）。</param>
/// <param name="Level">日志级别名。</param>
/// <param name="Category">日志类别（通常为类型全名）。</param>
/// <param name="Message">已格式化的日志正文。</param>
/// <param name="Exception">异常全文；无异常为 null。</param>
/// <param name="Sequence">进程内单调序号，用于稳定排序。</param>
public sealed record OpsLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    string? Exception,
    long Sequence);

/// <summary>
/// 进程内日志环形缓冲，供本地运维面板回看最近日志。
/// 仅面板启用时注册；有界容量、线程安全，写入路径不阻塞业务线程，也不影响既有控制台输出。
/// </summary>
public sealed class OpsLogBuffer(int capacity) : ILoggerProvider
{
    private readonly int _capacity = Math.Max(1, capacity);
    private readonly object _gate = new();
    private readonly Queue<OpsLogEntry> _entries = new();
    private long _sequence;
    private volatile bool _disposed;

    /// <summary>创建按类别归属的缓冲记录器。</summary>
    /// <param name="categoryName">日志类别名。</param>
    public ILogger CreateLogger(string categoryName) => new BufferLogger(this, categoryName);

    /// <summary>释放缓冲：释放后丢弃所有后续写入。</summary>
    public void Dispose() => _disposed = true;

    /// <summary>
    /// 按序号倒序返回最近日志，可按最低级别过滤。
    /// </summary>
    /// <param name="minLevel">最低级别（含）；默认 Information。</param>
    /// <param name="limit">返回条数上限。</param>
    public IReadOnlyList<OpsLogEntry> Snapshot(LogLevel minLevel = LogLevel.Information, int limit = 200)
    {
        var bounded = Math.Clamp(limit, 1, 2_000);
        lock (_gate)
        {
            return _entries
                .Where(entry => ParseLevel(entry.Level) >= minLevel)
                .OrderByDescending(entry => entry.Sequence)
                .Take(bounded)
                .ToArray();
        }
    }

    private void Append(LogLevel level, string category, string message, Exception? exception)
    {
        if (_disposed) return;
        var entry = new OpsLogEntry(
            DateTimeOffset.UtcNow,
            level.ToString(),
            category,
            message,
            exception?.ToString(),
            Interlocked.Increment(ref _sequence));
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity) _entries.Dequeue();
        }
    }

    private static LogLevel ParseLevel(string level) =>
        Enum.TryParse<LogLevel>(level, out var parsed) ? parsed : LogLevel.Information;

    /// <summary>缓冲记录器：日志级别过滤已由日志框架按配置完成，这里只负责写入。</summary>
    private sealed class BufferLogger(OpsLogBuffer owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.Append(logLevel, category, formatter(state, exception), exception);
        }
    }
}
