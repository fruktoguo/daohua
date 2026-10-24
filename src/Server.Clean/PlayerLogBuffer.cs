using Protocol;

namespace Server.Clean;

/// <summary>每玩家战报日志环形缓冲与单调递增 sequence；仅进程内存，不持久化。</summary>
public sealed class PlayerLogBuffer(int capacity)
{
    private readonly object _lock = new();
    private readonly List<LogEventDto> _events = [];
    private long _sequence;

    /// <summary>原子领取下一个日志序号：同玩家日志的去重/排序键，跨缓冲实例单调递增。</summary>
    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>
    /// 追加一条日志：与末条同模板同频道时合并为一条（计数累加、时间戳取较大、序号取新值），
    /// 否则入队；超出容量时淘汰最旧条目。
    /// </summary>
    public void Append(LogEventDto logEvent)
    {
        lock (_lock)
        {
            if (_events.Count > 0 && _events[^1].Template == logEvent.Template && _events[^1].Channel == logEvent.Channel)
            {
                var last = _events[^1];
                _events[^1] = last with
                {
                    Sequence = logEvent.Sequence,
                    TimestampUnixMilliseconds = Math.Max(last.TimestampUnixMilliseconds, logEvent.TimestampUnixMilliseconds),
                    Count = last.Count + logEvent.Count
                };
                return;
            }
            _events.Add(logEvent);
            while (_events.Count > capacity) _events.RemoveAt(0);
        }
    }

    /// <summary>当前缓冲内容的副本快照；锁内拷贝，返回后不受后续写入影响，供重连回放。</summary>
    public IReadOnlyList<LogEventDto> SnapshotRecent()
    {
        lock (_lock) return _events.ToArray();
    }
}
