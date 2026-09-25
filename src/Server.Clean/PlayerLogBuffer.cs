using Protocol;

namespace Server.Clean;

/// <summary>每玩家战报日志环形缓冲与单调递增 sequence；仅进程内存，不持久化。</summary>
public sealed class PlayerLogBuffer(int capacity)
{
    private readonly object _lock = new();
    private readonly List<LogEventDto> _events = [];
    private long _sequence;

    public long NextSequence() => Interlocked.Increment(ref _sequence);

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

    public IReadOnlyList<LogEventDto> SnapshotRecent()
    {
        lock (_lock) return _events.ToArray();
    }
}
