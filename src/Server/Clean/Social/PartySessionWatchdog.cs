namespace Server.Clean.Social;

/// <summary>
/// 组队讨伐看门狗（总纲 §21.13），仅 Api 角色装配：
/// 1. 启动扫尾：进程重启=全部活跃会话终局（end_reason=server.restart）——成员队首绑定
///    由各自装载对账按权威散场时刻收尾，不在启动阶段强制装载任何玩家；
/// 2. 周期逐出：连续「彻底离线」（game.player_projection.last_active_at，冷挂机补算不推进该列）
///    超过 <see cref="ServerOptions.ColdCatchUpWindow"/> 的成员从队伍移除并触发散场仲裁；
/// 3. 周期兜底：已装载玩家的队首死会话绑定（散场投递与行更新之间崩溃的漏网）按权威时刻收尾。
/// 逐出与散场均为幂等行操作（DELETE … RETURNING / ended_at IS NULL 条件更新），多实例并发天然去重。
/// </summary>
public sealed class PartySessionWatchdog(
    ISocialRepository repository,
    SocialRuntime social,
    ServerOptions options,
    ILogger<PartySessionWatchdog> logger) : BackgroundService
{
    /// <summary>周期下限：进程内最小 1 分钟，防止误配更小间隔空转打连接池。</summary>
    private static readonly TimeSpan MinTickInterval = TimeSpan.FromMinutes(1);

    private readonly LogThrottle _loopErrorLog = new(MinTickInterval);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动扫尾：上一进程生命周期内遗留的活跃会话全部写终态。
        // 成员行动队列中的 ∞ 绑定不在这里处理——它们随各自下次装载对账收尾（行已散场→按 ended_at 终局）。
        try
        {
            var swept = await repository.EndAllActivePartySessionsAsync(DateTimeOffset.UtcNow, "server.restart", stoppingToken);
            if (swept.Count > 0)
                logger.LogWarning("组队讨伐重启扫尾：{Count} 个残留活跃会话已写终态（成员绑定随装载对账收尾）。", swept.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "组队讨伐重启扫尾失败：死会话由装载对账与周期兜底继续收尾。");
        }

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
                await social.EvictOfflinePartyMembersAsync(options.ColdCatchUpWindow, stoppingToken);
                await social.ReconcileLoadedSessionBindingsAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (_loopErrorLog.TryAcquire())
                    logger.LogError(exception, "组队讨伐看门狗周期失败，下个周期重试。");
            }
        }
    }
}
