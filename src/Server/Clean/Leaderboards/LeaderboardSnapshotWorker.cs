namespace Server.Clean.Leaderboards;

/// <summary>
/// 全服排行榜整点物化后台托管服务：
/// 启动时先尝试从数据库恢复当前 UTC 小时内既有快照，失败或缺失时立即重建；
/// 之后每 15 秒检查一次 UTC 小时是否翻新，命中整点立即触发新一轮快照物化。
/// </summary>
public sealed class LeaderboardSnapshotWorker(
    LeaderboardRuntime runtime,
    ILogger<LeaderboardSnapshotWorker> logger) : BackgroundService
{
    private static readonly TimeSpan s_checkInterval = TimeSpan.FromSeconds(15);
    private readonly LogThrottle _errorLog = new(TimeSpan.FromMinutes(1));

    /// <summary>执行启动预热与整点轮询物化主循环。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var restored = await runtime.TryRestoreRecentFromDatabaseAsync(stoppingToken);
            if (!restored && !stoppingToken.IsCancellationRequested)
            {
                await runtime.RebuildAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LeaderboardSnapshotWorker 初始化失败，将在定时循环内重试。");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(s_checkInterval, stoppingToken);

                var currentHour = LeaderboardAssembler.FloorToHourUtc(DateTimeOffset.UtcNow);
                var lastComputedAt = runtime.ComputedAt;
                if (lastComputedAt is null || currentHour > LeaderboardAssembler.FloorToHourUtc(lastComputedAt.Value))
                {
                    await runtime.RebuildAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_errorLog.TryAcquire())
                    logger.LogError(ex, "LeaderboardSnapshotWorker 整点重建异常，将在下个检查周期重试。");
            }
        }
    }
}
