using Xiuxian.Server.Infrastructure;

namespace Xiuxian.Server.Runtime;

public sealed class XiuxianSimulationHostedService : BackgroundService
{
    private readonly XiuxianGameHost _gameHost;
    private readonly IXiuxianPersistenceQueue _persistenceQueue;
    private readonly XiuxianSimulationScheduleSignal _scheduleSignal;
    private readonly XiuxianSimulationClock _clock;
    private readonly ILogger<XiuxianSimulationHostedService> _logger;

    public XiuxianSimulationHostedService(
        XiuxianGameHost gameHost,
        IXiuxianPersistenceQueue persistenceQueue,
        XiuxianSimulationScheduleSignal scheduleSignal,
        XiuxianSimulationClock clock,
        ILogger<XiuxianSimulationHostedService> logger)
    {
        _gameHost = gameHost;
        _persistenceQueue = persistenceQueue;
        _scheduleSignal = scheduleSignal;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("游戏模拟已启用事件截止时间调度，不使用固定频率推进");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var gameplayDeadline = await _gameHost.GetNextOnlineTransitionAtAsync(stoppingToken);
                    if (gameplayDeadline is null)
                    {
                        await _scheduleSignal.WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                        continue;
                    }

                    var wait = gameplayDeadline.Value - _clock.UtcNow;
                    if (wait > TimeSpan.Zero && await _scheduleSignal.WaitAsync(wait, stoppingToken))
                    {
                        continue;
                    }

                    var now = _clock.UtcNow;
                    if (gameplayDeadline.Value <= now)
                    {
                        var report = await _gameHost.AdvanceDueOnlinePlayersAsync(now, stoppingToken);
                        if (report.ElapsedMilliseconds > 100)
                        {
                            _logger.LogWarning(
                                "事件结算耗时偏高：{Elapsed:F2}ms，更新玩家 {Players}",
                                report.ElapsedMilliseconds,
                                report.UpdatedPlayers);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "事件截止时间调度执行失败");
                    await _clock.DelayAsync(TimeSpan.FromMilliseconds(100), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _logger.LogInformation("开始停止事件调度并刷新玩家快照");
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await base.StopAsync(shutdownCts.Token);
        try
        {
            await _gameHost.FlushAllAsync(shutdownCts.Token);
        }
        finally
        {
            _persistenceQueue.Complete();
            _logger.LogInformation(
                "事件调度已停止，耗时 {ElapsedMilliseconds:F1}ms",
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }
}

public sealed class XiuxianMaintenanceHostedService : BackgroundService
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ColdEvictionInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MarketExpirationSweepInterval = TimeSpan.FromMinutes(1);
    private readonly XiuxianGameHost _gameHost;
    private readonly XiuxianMarketRuntime _marketRuntime;
    private readonly XiuxianSimulationClock _clock;
    private readonly ILogger<XiuxianMaintenanceHostedService> _logger;

    public XiuxianMaintenanceHostedService(
        XiuxianGameHost gameHost,
        XiuxianMarketRuntime marketRuntime,
        XiuxianSimulationClock clock,
        ILogger<XiuxianMaintenanceHostedService> logger)
    {
        _gameHost = gameHost;
        _marketRuntime = marketRuntime;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var now = _clock.UtcNow;
        var nextCheckpoint = now + CheckpointInterval;
        var nextColdEviction = now + ColdEvictionInterval;
        var nextMarketExpirationSweep = now;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nextDeadline = MinDeadline(nextCheckpoint, nextColdEviction, nextMarketExpirationSweep);
                var wait = nextDeadline - _clock.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await _clock.DelayAsync(wait, stoppingToken);
                }

                now = _clock.UtcNow;
                if (now >= nextCheckpoint)
                {
                    await _gameHost.CheckpointOnlinePlayersAsync(stoppingToken);
                    nextCheckpoint = now + CheckpointInterval;
                }

                if (now >= nextColdEviction)
                {
                    await _gameHost.EvictIdlePlayersAsync(now, stoppingToken);
                    nextColdEviction = now + ColdEvictionInterval;
                }

                if (now >= nextMarketExpirationSweep)
                {
                    var expiredCount = await _marketRuntime.ExpireOrdersAsync(now, stoppingToken);
                    nextMarketExpirationSweep = now + MarketExpirationSweepInterval;
                    if (expiredCount > 0)
                    {
                        _logger.LogInformation("市场已回收 {ExpiredCount} 个过期挂牌", expiredCount);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "后台维护调度执行失败");
                await _clock.DelayAsync(TimeSpan.FromMilliseconds(100), stoppingToken);
            }
        }
    }

    private static DateTimeOffset MinDeadline(
        DateTimeOffset checkpoint,
        DateTimeOffset coldEviction,
        DateTimeOffset marketExpirationSweep)
    {
        var next = checkpoint;
        if (coldEviction < next) next = coldEviction;
        if (marketExpirationSweep < next) next = marketExpirationSweep;
        return next;
    }
}
