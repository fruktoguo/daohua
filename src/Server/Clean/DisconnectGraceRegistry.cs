using Server.Clean.Social;

namespace Server.Clean;

/// <summary>
/// 在线断线宽限注册表（总纲18 §14.3）：拥有会话的连接断开后登记到期收尾，
/// 宽限内在线位与实体常驻、按在线实时推进；到期执行「注销在线位 → 落盘卸载 → 触发 Expired」。
/// 宽限时长为 0 时退化为断开即卸载的同步收尾；优雅停机立即收尾全部在途条目。
/// </summary>
public sealed class DisconnectGraceRegistry : IHostedService
{
    private readonly OnlinePresenceRegistry _presence;
    private readonly IPlayerRuntime _runtime;
    private readonly IPlayerSimulationClock _clock;
    private readonly ServerOptions _options;
    private readonly ILogger<DisconnectGraceRegistry> _logger;
    /// <summary>在途宽限条目：同一玩家至多一条，最新登记以原子替换为准。</summary>
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public DisconnectGraceRegistry(OnlinePresenceRegistry presence, IPlayerRuntime runtime,
        IPlayerSimulationClock clock, ServerOptions options, ILogger<DisconnectGraceRegistry> logger)
    {
        _presence = presence;
        _runtime = runtime;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    /// <summary>到期收尾完成（含失败已记录并交由刷盘轨道重试）后触发；供组队常驻成员等组件重对账。</summary>
    public event Func<Guid, ValueTask>? Expired;

    /// <summary>
    /// 登记一条断线宽限：宽限大于 0 时登记定时到期并立即返回，等于 0 时内联执行到期收尾并等待完成。
    /// 同一玩家重复登记以最新为准：先取消旧条目，已在执行的旧收尾等待其完成后再登记。
    /// </summary>
    /// <param name="playerId">断线玩家 ID。</param>
    /// <param name="disconnectedAt">真实断线时刻；到期落盘时作为离线锚点。</param>
    public async ValueTask ScheduleAsync(Guid playerId, DateTimeOffset disconnectedAt)
    {
        if (_options.OfflineGrace <= TimeSpan.Zero)
        {
            await RunTeardownAsync(playerId, disconnectedAt).ConfigureAwait(false);
            return;
        }
        // 先取消旧条目：未触发的定时作废，已触发的收尾等它完成，保证新旧收尾串行。
        while (true)
        {
            await CancelAsync(playerId, CancellationToken.None).ConfigureAwait(false);
            var entry = new Entry(playerId, disconnectedAt);
            if (_entries.TryAdd(playerId, entry))
            {
                _ = RunTimerAsync(entry);
                return;
            }
            // 并发登记竞态：最新登记为准，取消对方条目后重试。
        }
    }

    /// <summary>
    /// 取消尚未触发的宽限定时；若到期收尾已在执行则等待其完成后返回（收尾不可中断）。
    /// 新连接握手在装载前调用，保证「复用实体」与「卸载收尾」不会并发。
    /// </summary>
    public async ValueTask CancelAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        while (_entries.TryGetValue(playerId, out var entry))
        {
            CancellationTokenSource? timer = null;
            Task? completion = null;
            lock (entry)
            {
                switch (entry.State)
                {
                    case EntryState.Pending:
                        entry.State = EntryState.Cancelled;
                        timer = entry.Timer;
                        _entries.TryRemove(new KeyValuePair<Guid, Entry>(playerId, entry));
                        break;
                    case EntryState.Tearing:
                        completion = entry.Completion;
                        break;
                    default:
                        return;
                }
            }
            timer?.Cancel();
            if (completion is not null)
            {
                // 调用方取消只解除等待，不中断收尾；条目由收尾自身移除。
                await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                _entries.TryRemove(new KeyValuePair<Guid, Entry>(playerId, entry));
            }
            if (completion is null) return;
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>优雅停机：立即执行全部尚未触发条目的到期收尾（锚点仍为各自断线时刻），并等待全部完成。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var completions = new List<Task>();
        var timers = new List<CancellationTokenSource>();
        foreach (var entry in _entries.Values)
        {
            Task? completion = null;
            lock (entry)
            {
                switch (entry.State)
                {
                    case EntryState.Pending:
                        entry.State = EntryState.Tearing;
                        timers.Add(entry.Timer);
                        entry.Completion = RunTeardownAndCleanupAsync(entry);
                        completion = entry.Completion;
                        break;
                    case EntryState.Tearing:
                        completion = entry.Completion;
                        break;
                }
            }
            if (completion is not null) completions.Add(completion);
        }
        foreach (var timer in timers) timer.Cancel();
        await Task.WhenAll(completions).ConfigureAwait(false);
    }

    private async Task RunTimerAsync(Entry entry)
    {
        try
        {
            // 到期时刻 = 断线时刻 + 宽限；登记时已过期（异常断线补登记）立即收尾。
            var remaining = entry.DisconnectedAt + _options.OfflineGrace - _clock.UtcNow;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, entry.Timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Task completion;
        lock (entry)
        {
            if (entry.State != EntryState.Pending) return;
            entry.State = EntryState.Tearing;
            entry.Completion = RunTeardownAndCleanupAsync(entry);
            completion = entry.Completion;
        }
        await completion.ConfigureAwait(false);
    }

    private async Task RunTeardownAndCleanupAsync(Entry entry)
    {
        try { await RunTeardownAsync(entry.PlayerId, entry.DisconnectedAt).ConfigureAwait(false); }
        finally { _entries.TryRemove(new KeyValuePair<Guid, Entry>(entry.PlayerId, entry)); }
    }

    /// <summary>到期收尾固定顺序：注销在线位 → 按断线时刻落盘卸载 → 触发订阅；逐段记录异常不中断后续步骤。</summary>
    private async Task RunTeardownAsync(Guid playerId, DateTimeOffset disconnectedAt)
    {
        try { await _presence.UnregisterAsync(playerId).ConfigureAwait(false); }
        catch (Exception exception)
        {
            _logger.LogError(exception, "断线宽限收尾：在线存在感注销失败 playerId={PlayerId}", playerId);
        }
        try
        {
            // 卸载失败时实体与脏标记保留，由刷盘轨道重试；异常只记录不吞没。
            await _runtime.RemoveAsync(playerId, flush: true, offlineSince: disconnectedAt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "断线宽限收尾：玩家卸载失败 playerId={PlayerId}", playerId);
        }
        try
        {
            if (Expired is { } expired) await expired(playerId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "断线宽限收尾：Expired 订阅者异常 playerId={PlayerId}", playerId);
        }
    }

    private enum EntryState
    {
        Pending,
        Tearing,
        Cancelled
    }

    private sealed class Entry(Guid playerId, DateTimeOffset disconnectedAt)
    {
        public readonly Guid PlayerId = playerId;
        public readonly DateTimeOffset DisconnectedAt = disconnectedAt;
        public readonly CancellationTokenSource Timer = new();
        public EntryState State = EntryState.Pending;
        public Task? Completion;
    }
}
