namespace Server.Clean.Social;

/// <summary>
/// 周期执行离线超时逐出与成员绑定对账。活跃会话由独立权威检查点恢复，
/// 不因服务重启散场，也不在启动阶段强制装载离线成员。
/// </summary>
public sealed class PartySessionWatchdog(
    SocialRuntime social,
    ServerOptions options,
    ILogger<PartySessionWatchdog> logger) : BackgroundService
{
    /// <summary>周期下限：进程内最小 1 分钟，防止误配更小间隔空转打连接池。</summary>
    private static readonly TimeSpan MinTickInterval = TimeSpan.FromMinutes(1);

    private readonly LogThrottle _loopErrorLog = new(MinTickInterval);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 活跃会话从持久检查点按需恢复；进程重启不改变游戏会话终态。
        var interval = options.ColdCatchUpInterval < MinTickInterval ? MinTickInterval : options.ColdCatchUpInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            try
            {
                // 三态化周期顺序（总纲18 §14.5/§14.6）：先逐出越过 48h 边界的成员，
                // 再批算推进全员离线会话并强拉消费，随后修复已装载成员绑定，最后对账常驻集合。
                await social.EvictOfflinePartyMembersAsync(Domain.Player.PlayerLifecycleRules.OfflineIdleLimit, stoppingToken);
                await social.AdvanceOfflineSessionsAsync(options.OfflineBatchInterval, stoppingToken);
                await social.ReconcileLoadedSessionBindingsAsync(stoppingToken);
                await social.ReconcileResidentMembersAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (_loopErrorLog.TryAcquire())
                    logger.LogError(exception, "组队讨伐看门狗周期失败，下个周期重试。");
            }
        }
    }
}
