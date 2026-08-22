// ==============================================================================
// 文件名称: TickHostedService.cs
// 主要职责: 在线玩家事件截止时间驱动后台服务（SimulationHostedService）
// 核心逻辑 / 定义内容:
//   1. 采用离散事件截止时间驱动（Event Deadline Driven）模型，替代固定频率轮询。
//   2. 计算并等待最近事件截止时间，结合调度信号量支持动态提前唤醒。
//   3. 到期批量推进在线玩家演进、广播增量数据包并提交落盘入队。
//   4. 停机时安全排空，强制刷入全部内存玩家快照并关闭持久化队列。
// ==============================================================================

using Server.Infrastructure;

namespace Server.Runtime;

/// <summary>
/// 在线玩家事件截止时间驱动后台服务 (SimulationHostedService)。
/// <para>
/// 职责与调度模型：
/// 1. 采用离散事件截止时间驱动（Event Deadline Driven）而非固定 Tick 轮询。
/// 2. 获取所有在线玩家中最近的事件截止时间点进行精确异步等待；当有新玩家上线或指令修改队列时，由 <see cref="SimulationScheduleSignal"/> 提前打断并重新评估。
/// 3. 到达截止时间时批量推进演进 (<see cref="GameHost.AdvanceDueOnlinePlayersAsync"/>)，广播增量并入队落盘。
/// 4. 停机时执行安全排空：强制将全部内存玩家快照刷入持久化队列并关闭队列输入端。
/// </para>
/// </summary>
public sealed class SimulationHostedService : BackgroundService
{
    private readonly GameHost _gameHost;
    private readonly IPersistenceQueue _persistenceQueue;
    private readonly SimulationScheduleSignal _scheduleSignal;
    private readonly SimulationClock _clock;
    private readonly ILogger<SimulationHostedService> _logger;

    public SimulationHostedService(
        GameHost gameHost,
        IPersistenceQueue persistenceQueue,
        SimulationScheduleSignal scheduleSignal,
        SimulationClock clock,
        ILogger<SimulationHostedService> logger)
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

/// <summary>
/// 系统后台定期维护托管服务 (MaintenanceHostedService)。
/// <para>
/// 职责：
/// 1. 周期性检查点 (Checkpoint)：每 30 秒将当前所有在线玩家最新状态入队落盘。
/// 2. 冷玩家淘汰 (Cold Eviction)：每 15 秒扫描超时未活动的离线玩家，释放 ECS 实体并转入冷快照 LRU。
/// 3. 市场过期单清理 (Market Expiration Sweep)：每 60 秒扫描并回收已过有效期的市场挂牌。
/// 4. 采用单定时器动态对齐 (<see cref="MinDeadline"/>)，避免并发多定时器线程开销。
/// </para>
/// </summary>
public sealed class MaintenanceHostedService : BackgroundService
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ColdEvictionInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MarketExpirationSweepInterval = TimeSpan.FromMinutes(1);
    private readonly GameHost _gameHost;
    private readonly MarketRuntime _marketRuntime;
    private readonly SimulationClock _clock;
    private readonly ILogger<MaintenanceHostedService> _logger;

    public MaintenanceHostedService(
        GameHost gameHost,
        MarketRuntime marketRuntime,
        SimulationClock clock,
        ILogger<MaintenanceHostedService> logger)
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
